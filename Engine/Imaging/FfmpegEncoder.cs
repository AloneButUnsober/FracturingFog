// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog
{
    /// <summary>
    /// Encodes a PNG image sequence (frame_NNNNNN.png) into a video file using
    /// an external ffmpeg binary. The binary is located by, in order:
    ///   1. App base directory (`ffmpeg.exe` on Win, `ffmpeg` on Linux/macOS).
    ///   2. App base `Tools/&lt;rid&gt;/ffmpeg{.exe}` (per-RID bundle).
    ///   3. App base `Tools/ffmpeg{.exe}` (legacy single-binary bundle).
    ///   4. App base `Resources/ffmpeg{.exe}` (legacy resource bundle).
    ///   5. PATH.
    /// When ffmpeg is not present, callers should fall back to keeping the
    /// PNG sequence on disk.
    /// </summary>
    public static class FfmpegEncoder
    {
        public enum Preset
        {
            /// <summary>libx264 CRF 0 (-qp 0) — visually & mathematically lossless H.264.</summary>
            LosslessH264Mp4,
            /// <summary>FFV1 v3 in Matroska — true lossless intermediate, smaller than uncompressed.</summary>
            Ffv1Mkv,
            /// <summary>libx264 CRF 18 — visually lossless, much smaller files.</summary>
            HighQualityH264Mp4,
        }

        /// <summary>
        /// Phase X.2 / Slice 2.3 — cross-platform binary name. Windows hosts
        /// resolve `ffmpeg.exe`; every other OS resolves `ffmpeg` (Linux,
        /// macOS, BSD package managers all install without the .exe suffix).
        /// </summary>
        private static string FfmpegFileName =>
            OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        /// <summary>Resolves the ffmpeg binary path or returns null if not found.</summary>
        public static string? FindFfmpeg()
        {
            string baseDir = AppContext.BaseDirectory;
            string fileName = FfmpegFileName;
            string rid = RuntimeInformation.RuntimeIdentifier;

            string[] candidates =
            {
                Path.Combine(baseDir, fileName),
                // Per-RID bundle (Slice 2.4). Lets a published app ship a
                // matched ffmpeg next to the binary for each shipping RID.
                Path.Combine(baseDir, "Tools", rid, fileName),
                // Legacy single-binary bundle (pre-X.2 Windows builds dropped
                // ffmpeg.exe into Tools/ at the App base directly).
                Path.Combine(baseDir, "Tools", fileName),
                Path.Combine(baseDir, "Resources", fileName),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            // Look on PATH. Path.PathSeparator is ';' on Windows, ':' on
            // Linux/macOS — System.IO.Path handles both correctly.
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
                    catch { /* ignore malformed PATH entries */ }
                }
            }
            return null;
        }

        public static bool IsAvailable() => FindFfmpeg() != null;

        /// <summary>
        /// True when ffmpeg is on disk AND the user has not explicitly opted
        /// out via the FFmpeg setup dialog (FfmpegUserElection.Skip). Use
        /// this in the UI surface that decides whether to enable / show
        /// ffmpeg-dependent controls (Save Lossless presets, lossless video
        /// encode buttons). The bare <see cref="IsAvailable"/> still answers
        /// "does the file exist" — call that from headless paths
        /// (BatchRenderer, server) where the user election does not apply.
        /// </summary>
        public static bool IsEnabledForUser() =>
            IsAvailable() &&
            !FracturingFog.Models.FfmpegPreferences.Instance.IsVideoDisabledByUser();

        /// <summary>
        /// Runs ffmpeg to encode the PNG sequence in <paramref name="pngFolder"/>
        /// (frame_NNNNNN.png) at <paramref name="fps"/> into <paramref name="outputPath"/>.
        /// Returns (success, stderr-tail). Blocks until ffmpeg exits or
        /// <paramref name="ct"/> is cancelled (which kills the process).
        /// </summary>
        public static async Task<(bool ok, string log)> EncodeAsync(
            string pngFolder, string outputPath, Preset preset,
            int fps = 30, CancellationToken ct = default,
            Action<string>? onProgressLine = null)
        {
            string? exe = FindFfmpeg();
            if (exe == null)
                return (false, "ffmpeg.exe not found.");

            string args = BuildArgs(pngFolder, outputPath, preset, fps);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = pngFolder,
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var logBuf = new System.Text.StringBuilder();

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (logBuf)
                {
                    logBuf.AppendLine(e.Data);
                    // Keep memory bounded — trim from the head.
                    if (logBuf.Length > 32_000)
                        logBuf.Remove(0, logBuf.Length - 16_000);
                }
                onProgressLine?.Invoke(e.Data);
            };
            proc.OutputDataReceived += (_, _) => { };

            try { proc.Start(); }
            catch (Exception ex) { return (false, $"ffmpeg launch failed: {ex.Message}"); }

            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            using var reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* race with exit — ignore */ }
            });

            try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { return (false, $"ffmpeg wait failed: {ex.Message}\n{logBuf}"); }

            // Drain the async stderr/stdout pumps — WaitForExitAsync returns
            // when the process exits but does not always wait for the redirected
            // readers to flush their final buffers. The parameterless sync
            // WaitForExit() does, so call it once more on the already-exited
            // process to guarantee we have all of ffmpeg's output before we
            // read it.
            try { proc.WaitForExit(); } catch { }

            string log;
            lock (logBuf) log = logBuf.ToString();

            int exitCode;
            try { exitCode = proc.ExitCode; } catch { exitCode = -1; }

            if (ct.IsCancellationRequested) return (false, "Encoding cancelled.\n" + log);

            if (exitCode != 0)
            {
                // Always include the command line and exit code so the failure
                // dialog has something to show even when ffmpeg produced no
                // stderr output.
                string diag =
                    $"ffmpeg exit code: {exitCode}\n" +
                    $"Command: \"{exe}\" {args}\n" +
                    (string.IsNullOrWhiteSpace(log) ? "(ffmpeg produced no stderr output)" : log);
                return (false, diag);
            }
            return (true, log);
        }

        private static string BuildArgs(string pngFolder, string outputPath, Preset preset, int fps)
        {
            // -start_number 1 because PngSequenceWriter names frames frame_000001.png
            // upward; ffmpeg's image2 demuxer defaults to start_number 0 and
            // bails out when frame_000000.png isn't present, leaving an empty
            // stderr and a non-zero exit code.
            string input =
                $"-y -framerate {fps} -start_number 1 " +
                $"-i \"{Path.Combine(pngFolder, "frame_%06d.png")}\"";
            string codec = preset switch
            {
                Preset.LosslessH264Mp4 =>
                    "-c:v libx264 -preset veryslow -qp 0 -pix_fmt yuv444p -movflags +faststart",
                Preset.Ffv1Mkv =>
                    "-c:v ffv1 -level 3 -coder 1 -context 1 -g 1 -slices 24 -slicecrc 1",
                Preset.HighQualityH264Mp4 =>
                    "-c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart",
                _ => throw new ArgumentOutOfRangeException(nameof(preset)),
            };
            return $"{input} {codec} \"{outputPath}\"";
        }

        public static string DefaultExtensionFor(Preset preset) => preset switch
        {
            Preset.LosslessH264Mp4 => "mp4",
            Preset.HighQualityH264Mp4 => "mp4",
            Preset.Ffv1Mkv => "mkv",
            _ => "mp4",
        };
    }
}
