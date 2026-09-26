// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using FracturingFog.Imaging;
using FracturingFog.Render;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #944 — instant record: LiveFrameRecorder (change-detected sampling into a
/// unique-frame PNG intermediate + hold manifest) and LiveRecordingEncoder
/// (CFR scheduling, ffmpeg args, no-ffmpeg exports).
/// </summary>
public sealed class LiveRecordingTests
{
    private const long Sec = TimeSpan.TicksPerSecond;

    private sealed class FakeDisplay
    {
        public int W = 32, H = 20;
        public uint Color = 0x00FF0000u; // alpha 0 on purpose — recorder must force opaque
        public int Pulls;
        public uint[] Pull(out int w, out int h)
        {
            Pulls++;
            w = W; h = H;
            return Enumerable.Repeat(Color, W * H).ToArray();
        }
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"ff-live-{Guid.NewGuid():N}");

    [Fact]
    public void Recorder_captures_only_changed_frames_with_wall_clock_holds()
    {
        var disp = new FakeDisplay();
        var rec = new LiveFrameRecorder(TempDir(), 30, disp.Pull);
        try
        {
            rec.SampleAt(0);                 // initial frame (starts dirty)
            rec.SampleAt(Sec / 2);           // unchanged → no capture
            disp.Color = 0x0000FF00u; rec.NotifyFrameChanged();
            rec.SampleAt(1 * Sec);           // changed → capture
            var r = rec.StopAt(3 * Sec)!;

            Assert.Equal(2, disp.Pulls);
            Assert.Equal(2, r.Frames.Count);
            Assert.Equal(1.0, r.Frames[0].Seconds, 6);
            Assert.Equal(2.0, r.Frames[1].Seconds, 6);
            Assert.Equal(3.0, r.DurationSeconds, 6);
            Assert.Equal((32, 20), (r.Width, r.Height));

            using var bmp = SKBitmap.Decode(Path.Combine(r.Folder, r.Frames[1].FileName));
            var c = bmp.GetPixel(5, 5);
            Assert.Equal((byte)255, c.Alpha);
            Assert.Equal((byte)255, c.Green);
            Assert.Equal((byte)0, c.Red);

            string manifest = File.ReadAllText(r.ManifestPath);
            Assert.StartsWith("ffconcat version 1.0", manifest);
            Assert.Equal(3, manifest.Split('\n').Count(l => l.StartsWith("file ")));  // last repeated
            Assert.Contains("duration 2\n", manifest);
        }
        finally { rec.Dispose(); }
    }

    [Fact]
    public void Recorder_with_no_frames_returns_null_and_cleans_up()
    {
        string dir = TempDir();
        var rec = new LiveFrameRecorder(dir, 30, (out int w, out int h) => { w = h = 0; return null; });
        rec.SampleAt(0);
        Assert.Null(rec.StopAt(Sec));
        rec.Dispose();
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Recorder_resamples_midrecording_resize_to_first_size()
    {
        var disp = new FakeDisplay { W = 33, H = 21 }; // odd → even-cropped 32x20
        var rec = new LiveFrameRecorder(TempDir(), 30, disp.Pull);
        try
        {
            rec.SampleAt(0);
            disp.W = 64; disp.H = 40; rec.NotifyFrameChanged();
            rec.SampleAt(Sec);
            var r = rec.StopAt(2 * Sec)!;
            Assert.Equal((32, 20), (r.Width, r.Height));
            using var bmp = SKBitmap.Decode(Path.Combine(r.Folder, r.Frames[1].FileName));
            Assert.Equal((32, 20), (bmp.Width, bmp.Height));
        }
        finally { try { rec.Dispose(); } catch { } }
    }

    [Fact]
    public void FitOpaque_crops_odd_edge_and_forces_alpha()
    {
        var src = new uint[3 * 3];
        for (int i = 0; i < src.Length; i++) src[i] = (uint)i;
        var dst = LiveFrameRecorder.FitOpaque(src, 3, 3, 2, 2);
        Assert.Equal(new uint[] { 0xFF000000u, 0xFF000001u, 0xFF000003u, 0xFF000004u }, dst);
    }

    [Fact]
    public void CfrSchedule_is_drift_free()
    {
        var frames = Enumerable.Range(0, 100)
            .Select(_ => new LiveRecordingFrame { FileName = "x", Seconds = 0.0417 })
            .ToList();
        var counts = LiveRecordingEncoder.CfrSchedule(frames, 30);
        Assert.Equal((int)Math.Round(100 * 0.0417 * 30), counts.Sum());
        Assert.All(counts, c => Assert.InRange(c, 1, 2));
    }

    [Fact]
    public void CfrSchedule_never_empty()
    {
        var counts = LiveRecordingEncoder.CfrSchedule(
            new[] { new LiveRecordingFrame { FileName = "x", Seconds = 0.001 } }, 30);
        Assert.Equal(new[] { 1 }, counts);
    }

    [Theory]
    [InlineData(LiveRecordingFormat.Mp4H264, LiveRecordingQuality.High, "libx264", "-crf 18")]
    [InlineData(LiveRecordingFormat.Mp4H264, LiveRecordingQuality.Lossless, "libx264", "-qp 0")]
    [InlineData(LiveRecordingFormat.Mp4H265, LiveRecordingQuality.Medium, "libx265", "-crf 26")]
    [InlineData(LiveRecordingFormat.WebmVp9, LiveRecordingQuality.Low, "libvpx-vp9", "-crf 40")]
    [InlineData(LiveRecordingFormat.MkvFfv1, LiveRecordingQuality.Low, "ffv1", "-level 3")]
    [InlineData(LiveRecordingFormat.Gif, LiveRecordingQuality.High, "palettegen", "-loop 0")]
    public void FfmpegArgs_select_codec_and_quality(LiveRecordingFormat f, LiveRecordingQuality q, string codec, string knob)
    {
        var rec = new LiveRecordingResult { Folder = "x", ManifestPath = "x/frames.ffconcat", Width = 1920, Height = 1080 };
        string args = LiveRecordingEncoder.BuildFfmpegArgs(rec,
            new LiveRecordingExportOptions { Format = f, Quality = q, Fps = 24, ScalePercent = 50 }, "out");
        Assert.Contains("-f concat -safe 0 -i \"frames.ffconcat\"", args);
        Assert.Contains("fps=24,scale=960:540", args);
        Assert.Contains(codec, args);
        Assert.Contains(knob, args);
    }

    private static LiveRecordingResult RecordTwoFrames()
    {
        var disp = new FakeDisplay();
        var rec = new LiveFrameRecorder(TempDir(), 30, disp.Pull);
        rec.SampleAt(0);
        disp.Color = 0x000000FFu; rec.NotifyFrameChanged();
        rec.SampleAt(Sec / 2);
        var r = rec.StopAt(Sec)!;
        rec.Dispose();
        return r;
    }

    [Fact]
    public async Task PngSequence_export_expands_to_constant_rate()
    {
        var r = RecordTwoFrames();
        string outDir = TempDir();
        try
        {
            var (ok, log) = await LiveRecordingEncoder.EncodeAsync(r,
                new LiveRecordingExportOptions { Format = LiveRecordingFormat.PngSequence, Fps = 10, ScalePercent = 50 },
                outDir);
            Assert.True(ok, log);
            var files = Directory.GetFiles(outDir, "frame_*.png");
            Assert.Equal(10, files.Length); // 1 s at 10 fps
            using var bmp = SKBitmap.Decode(files.OrderBy(f => f).Last());
            Assert.Equal((16, 10), (bmp.Width, bmp.Height));
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { }
            try { Directory.Delete(r.Folder, true); } catch { }
        }
    }

    [Fact]
    public async Task Gif_export_without_ffmpeg_uses_builtin_encoder()
    {
        var r = RecordTwoFrames();
        string gif = Path.Combine(Path.GetTempPath(), $"ff-live-{Guid.NewGuid():N}.gif");
        try
        {
            var (ok, log) = await LiveRecordingEncoder.EncodeAsync(r,
                new LiveRecordingExportOptions { Format = LiveRecordingFormat.Gif, Fps = 30 },
                gif, ffmpegAllowed: false);
            Assert.True(ok, log);
            using var codec = SKCodec.Create(gif);
            Assert.Equal(2, codec.FrameCount);
        }
        finally
        {
            try { File.Delete(gif); } catch { }
            try { Directory.Delete(r.Folder, true); } catch { }
        }
    }

    [Fact]
    public async Task Ffmpeg_only_format_fails_cleanly_without_ffmpeg()
    {
        var r = RecordTwoFrames();
        try
        {
            var (ok, log) = await LiveRecordingEncoder.EncodeAsync(r,
                new LiveRecordingExportOptions { Format = LiveRecordingFormat.WebmVp9 },
                "unused.webm", ffmpegAllowed: false);
            Assert.False(ok);
            Assert.Contains("ffmpeg", log);
        }
        finally { try { Directory.Delete(r.Folder, true); } catch { } }
    }

    [Theory]
    [InlineData(LiveRecordingFormat.Mp4H264)]
    [InlineData(LiveRecordingFormat.Gif)]
    [InlineData(LiveRecordingFormat.MkvFfv1)]
    public async Task Ffmpeg_concat_export_runs_when_ffmpeg_installed(LiveRecordingFormat f)
    {
        if (!FfmpegEncoder.IsAvailable()) Assert.Skip("ffmpeg not installed");
        var r = RecordTwoFrames();
        string outPath = Path.Combine(Path.GetTempPath(), $"ff-live-{Guid.NewGuid():N}.{LiveRecordingEncoder.ExtensionFor(f)}");
        try
        {
            var (ok, log) = await LiveRecordingEncoder.EncodeAsync(r,
                new LiveRecordingExportOptions { Format = f, Fps = 30, ScalePercent = 50 }, outPath);
            Assert.True(ok, log);
            Assert.True(new FileInfo(outPath).Length > 0);
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
            try { Directory.Delete(r.Folder, true); } catch { }
        }
    }
}
