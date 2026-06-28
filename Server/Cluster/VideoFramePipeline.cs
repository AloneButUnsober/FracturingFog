// Server/Cluster/VideoFramePipeline.cs
// D-4b — streaming ffmpeg encoder for video cluster jobs.
//
// Why streaming? Workers deliver frame batches over the wire as tiles
// complete. Once frame_000001.png is on disk we can start feeding it to
// ffmpeg via stdin (image2pipe / vcodec=png) so the encode runs in
// parallel with the rest of the job. This collapses the wall-clock
// "render then encode" two-step into one overlapped phase and avoids
// the master having to hold the entire frame set in memory or wait for
// the slowest tile before any encode work begins.
//
// Backpressure: the coordinator gates tile.next when DeliveredFrames -
// EncodedFrames > MaxFrameQueueDepth so workers can't race ahead of the
// sequential image2pipe ingest (a fast worker could otherwise dump 600
// frames on disk for a 64-deep encoder queue and cost real memory in
// the OS file cache).
//
// Why the encoder lives in Server, not Engine: Server/ is independent
// of Engine/ by design (Engine pulls SkiaSharp, calculators, the full
// render stack). The cluster only needs the ffmpeg subprocess, not the
// render path, so the binary lookup is small and self-contained here.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.Server.Cluster;

public enum ClusterVideoPreset
{
    /// <summary>libx264 -qp 0 (mathematically lossless H.264 in MP4).</summary>
    LosslessH264Mp4,
    /// <summary>FFV1 v3 in Matroska — lossless intermediate.</summary>
    Ffv1Mkv,
    /// <summary>libx264 -crf 18 (visually lossless H.264 in MP4).</summary>
    HighQualityH264Mp4,
}

/// <summary>Per-video-job streaming encoder. The pipeline owns an
/// ffmpeg subprocess fed via stdin (image2pipe / vcodec=png) and a
/// reader task that watches <see cref="FramesDir"/> for the next
/// expected frame file. Construction starts both; <see cref="Completion"/>
/// resolves to (ok, log) once ffmpeg exits or the cancellation token
/// fires.</summary>
public sealed class VideoFramePipeline : IAsyncDisposable
{
    public string FramesDir { get; }
    public int TotalFrames { get; }
    public int Fps { get; }
    public ClusterVideoPreset Preset { get; }
    public string ArtifactPath { get; }
    public string ArtifactExt { get; }

    /// <summary>Per-frame poll interval while waiting for the next
    /// <c>frame_NNNNNN.png</c> to land on disk. Short enough that the
    /// encoder stays close to wire delivery; long enough not to burn a
    /// core on a missing-file loop.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    private int _delivered;
    private int _encoded;

    public int DeliveredFrames => Volatile.Read(ref _delivered);
    public int EncodedFrames   => Volatile.Read(ref _encoded);
    public int Backlog => Math.Max(0, DeliveredFrames - EncodedFrames);

    public bool IsBehind(int maxQueueDepth) => Backlog > maxQueueDepth;

    /// <summary>Called by the coordinator on every successful
    /// <c>tile.deliver PayloadKind="frames"</c> so the pipeline knows
    /// how far ahead of the encoder the frame queue has grown. Drives
    /// the backpressure gate in <see cref="IsBehind"/>.</summary>
    public void NotifyFramesDelivered(int n)
    {
        if (n > 0) Interlocked.Add(ref _delivered, n);
    }

    public Task<(bool ok, string log)> Completion { get; }

    private readonly Process _proc;
    private readonly CancellationTokenSource _internalCts;
    private readonly StringBuilder _stderr = new();
    private readonly Task _readerTask;

    private VideoFramePipeline(
        string framesDir, int totalFrames, int fps,
        ClusterVideoPreset preset,
        string artifactPath, string artifactExt,
        Process proc, CancellationToken ct)
    {
        FramesDir    = framesDir;
        TotalFrames  = totalFrames;
        Fps          = fps;
        Preset       = preset;
        ArtifactPath = artifactPath;
        ArtifactExt  = artifactExt;
        _proc        = proc;
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 32_000) _stderr.Remove(0, _stderr.Length - 16_000);
            }
        };
        proc.BeginErrorReadLine();

        _readerTask = Task.Run(() => RunReaderAsync(_internalCts.Token));
        Completion  = WrapCompletionAsync(_readerTask);
    }

    /// <summary>Spawn ffmpeg in image2pipe mode and start the reader.
    /// Returns null when no ffmpeg binary is on disk — caller falls
    /// back to the frames-manifest stub.</summary>
    public static VideoFramePipeline? TryStart(
        string framesDir, int totalFrames, int fps,
        ClusterVideoPreset preset, string artifactBasePathNoExt,
        CancellationToken ct)
    {
        string? exe = FindFfmpeg();
        if (exe == null) return null;

        string ext = DefaultExtensionFor(preset);
        string outPath = artifactBasePathNoExt + "." + ext;
        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

        Directory.CreateDirectory(framesDir);

        string args = BuildArgs(fps, preset, outPath);
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardError  = true,
            RedirectStandardOutput = false,
            CreateNoWindow         = true,
            WorkingDirectory       = framesDir,
        };

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try { proc.Start(); }
        catch
        {
            proc.Dispose();
            return null;
        }

        return new VideoFramePipeline(
            framesDir, totalFrames, fps, preset, outPath, ext, proc, ct);
    }

    private static string BuildArgs(int fps, ClusterVideoPreset preset, string outPath)
    {
        // image2pipe + vcodec=png means ffmpeg reads concatenated PNG
        // frames off stdin and demuxes them as a sequence at the given
        // framerate. Output codec settings mirror the disk-based
        // FfmpegEncoder.EncodeAsync presets so cluster output matches
        // a single-server batch render byte-for-byte (modulo encoder
        // non-determinism, which ffv1 + h264-qp0 explicitly avoid).
        string input =
            $"-y -f image2pipe -framerate {fps} -vcodec png -i -";
        string codec = preset switch
        {
            ClusterVideoPreset.LosslessH264Mp4 =>
                "-c:v libx264 -preset veryslow -qp 0 -pix_fmt yuv444p -movflags +faststart",
            ClusterVideoPreset.Ffv1Mkv =>
                "-c:v ffv1 -level 3 -coder 1 -context 1 -g 1 -slices 24 -slicecrc 1",
            ClusterVideoPreset.HighQualityH264Mp4 =>
                "-c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart",
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
        return $"{input} {codec} \"{outPath}\"";
    }

    private async Task RunReaderAsync(CancellationToken ct)
    {
        var stdin = _proc.StandardInput.BaseStream;
        try
        {
            for (int n = 1; n <= TotalFrames; n++)
            {
                if (ct.IsCancellationRequested) break;
                string path = Path.Combine(FramesDir, $"frame_{n:D6}.png");

                while (!File.Exists(path))
                {
                    if (ct.IsCancellationRequested) return;
                    if (_proc.HasExited) return;   // ffmpeg died — abort reader
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                }

                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false); }
                catch (IOException)
                {
                    // Write-and-rename publish race: tmp→final move
                    // happens after File.Exists sees the final inode in
                    // most filesystems, but a hostile race could land
                    // an empty file between the two syscalls. Retry once.
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                    bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                }

                await stdin.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stdin.FlushAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _encoded);
            }
        }
        finally
        {
            try { stdin.Close(); } catch { /* ffmpeg may have already exited */ }
        }
    }

    private async Task<(bool ok, string log)> WrapCompletionAsync(Task readerTask)
    {
        try { await readerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* propagate via exit code */ }
        catch (Exception ex)
        {
            lock (_stderr) _stderr.AppendLine("reader-failed: " + ex.Message);
        }

        try { await _proc.WaitForExitAsync(_internalCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        try { _proc.WaitForExit(); } catch { } // drain stderr pump

        int exitCode;
        try { exitCode = _proc.ExitCode; } catch { exitCode = -1; }
        string log;
        lock (_stderr) log = _stderr.ToString();
        return (exitCode == 0, log);
    }

    public async ValueTask DisposeAsync()
    {
        try { _internalCts.Cancel(); } catch { }
        try
        {
            if (!_proc.HasExited)
            {
                try { _proc.StandardInput.Close(); } catch { }
                _proc.Kill(entireProcessTree: true);
            }
        }
        catch { }
        try { await _readerTask.ConfigureAwait(false); } catch { }
        _proc.Dispose();
        _internalCts.Dispose();
    }

    // ── ffmpeg binary discovery ────────────────────────────────────────
    // Self-contained on purpose: the Server assembly cannot reference
    // Engine/Imaging/FfmpegEncoder.cs without pulling SkiaSharp + the
    // render stack. Same lookup order as that helper.

    public static string FfmpegFileName =>
        OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>Resolves the ffmpeg binary path or returns null if not
    /// found. Mirrors Engine/Imaging/FfmpegEncoder.FindFfmpeg lookup
    /// order so a cluster master ships with the same ffmpeg discovery
    /// behaviour as the single-server batch path.</summary>
    public static string? FindFfmpeg()
    {
        string baseDir = AppContext.BaseDirectory;
        string fileName = FfmpegFileName;
        string rid = RuntimeInformation.RuntimeIdentifier;

        string[] candidates =
        {
            Path.Combine(baseDir, fileName),
            Path.Combine(baseDir, "Tools", rid, fileName),
            Path.Combine(baseDir, "Tools", fileName),
            Path.Combine(baseDir, "Resources", fileName),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string p = Path.Combine(dir.Trim(), fileName);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        return null;
    }

    public static bool IsAvailable() => FindFfmpeg() != null;

    public static string DefaultExtensionFor(ClusterVideoPreset preset) => preset switch
    {
        ClusterVideoPreset.LosslessH264Mp4    => "mp4",
        ClusterVideoPreset.HighQualityH264Mp4 => "mp4",
        ClusterVideoPreset.Ffv1Mkv            => "mkv",
        _ => "mp4",
    };

    /// <summary>Map <c>RenderRequestDto.Lossless</c> string to a cluster
    /// preset, or null when no encode should run ("none" → fall back to
    /// the frames-manifest stub).</summary>
    public static ClusterVideoPreset? PresetFromLossless(string? lossless)
    {
        return (lossless ?? "").ToLowerInvariant() switch
        {
            "h264"   => ClusterVideoPreset.LosslessH264Mp4,
            "ffv1"   => ClusterVideoPreset.Ffv1Mkv,
            "h264hq" => ClusterVideoPreset.HighQualityH264Mp4,
            _        => null,
        };
    }
}
