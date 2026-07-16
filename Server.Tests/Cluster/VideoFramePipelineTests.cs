// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/Cluster/VideoFramePipelineTests.cs
// D-4b — exercise the streaming ffmpeg encoder pipeline and its
// preset / extension mapping helpers. The encode round-trip tests are
// skipped when ffmpeg is not on disk (CI without ffmpeg installed,
// dev box that opted out of the bundled binary); the static-mapping
// tests run everywhere.

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Cluster;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class VideoFramePipelineTests
{
    [Fact]
    public void PresetFromLossless_Maps_Known_Values()
    {
        Assert.Equal(ClusterVideoPreset.LosslessH264Mp4,    VideoFramePipeline.PresetFromLossless("h264"));
        Assert.Equal(ClusterVideoPreset.Ffv1Mkv,            VideoFramePipeline.PresetFromLossless("ffv1"));
        Assert.Equal(ClusterVideoPreset.HighQualityH264Mp4, VideoFramePipeline.PresetFromLossless("h264hq"));
    }

    [Fact]
    public void PresetFromLossless_Is_Case_Insensitive()
    {
        Assert.Equal(ClusterVideoPreset.Ffv1Mkv, VideoFramePipeline.PresetFromLossless("FFV1"));
        Assert.Equal(ClusterVideoPreset.Ffv1Mkv, VideoFramePipeline.PresetFromLossless("Ffv1"));
    }

    [Fact]
    public void PresetFromLossless_None_Returns_Null()
    {
        Assert.Null(VideoFramePipeline.PresetFromLossless("none"));
        Assert.Null(VideoFramePipeline.PresetFromLossless(""));
        Assert.Null(VideoFramePipeline.PresetFromLossless(null));
        Assert.Null(VideoFramePipeline.PresetFromLossless("not-a-codec"));
    }

    [Fact]
    public void DefaultExtensionFor_Matches_Container()
    {
        Assert.Equal("mp4", VideoFramePipeline.DefaultExtensionFor(ClusterVideoPreset.LosslessH264Mp4));
        Assert.Equal("mp4", VideoFramePipeline.DefaultExtensionFor(ClusterVideoPreset.HighQualityH264Mp4));
        Assert.Equal("mkv", VideoFramePipeline.DefaultExtensionFor(ClusterVideoPreset.Ffv1Mkv));
    }

    [Fact]
    public void TryStart_With_No_Ffmpeg_Available_Returns_Null()
    {
        if (VideoFramePipeline.IsAvailable())
            return; // The encode test below covers the available branch.

        using var td = new TempDir();
        var pipe = VideoFramePipeline.TryStart(
            framesDir: Path.Combine(td.Path, "frames"),
            totalFrames: 2,
            fps: 10,
            preset: ClusterVideoPreset.HighQualityH264Mp4,
            artifactBasePathNoExt: Path.Combine(td.Path, "artifact"),
            ct: CancellationToken.None);
        Assert.Null(pipe);
    }

    [Fact]
    public async Task Pipeline_Streams_Frames_From_Disk_And_Produces_Mp4()
    {
        if (!VideoFramePipeline.IsAvailable())
            return; // No ffmpeg → skip the encode round-trip.

        using var td = new TempDir();
        string framesDir = Path.Combine(td.Path, "frames");
        Directory.CreateDirectory(framesDir);

        const int total = 6;
        var pipe = VideoFramePipeline.TryStart(
            framesDir, total, fps: 10,
            preset: ClusterVideoPreset.HighQualityH264Mp4,
            artifactBasePathNoExt: Path.Combine(td.Path, "artifact"),
            ct: CancellationToken.None);
        Assert.NotNull(pipe);

        // Drip frames in over time so we exercise the "wait-for-next-png"
        // poll path inside the reader. Each frame is a fresh 8×8 RGB PNG.
        for (int i = 1; i <= total; i++)
        {
            byte[] png = TinyPng.Make(seed: i, width: 8, height: 8);
            string path = Path.Combine(framesDir, $"frame_{i:D6}.png");
            await File.WriteAllBytesAsync(path, png);
            pipe!.NotifyFramesDelivered(1);
            await Task.Delay(15);
        }

        var (ok, log) = await pipe!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(ok, $"ffmpeg failed: {log}");
        Assert.True(File.Exists(pipe.ArtifactPath), $"artifact missing: {pipe.ArtifactPath}");
        Assert.True(new FileInfo(pipe.ArtifactPath).Length > 0);
        Assert.Equal(total, pipe.EncodedFrames);

        await pipe.DisposeAsync();
    }

    [Fact]
    public async Task IsBehind_Tracks_Delivered_Minus_Encoded()
    {
        if (!VideoFramePipeline.IsAvailable())
            return;

        using var td = new TempDir();
        string framesDir = Path.Combine(td.Path, "frames");
        Directory.CreateDirectory(framesDir);

        const int total = 3;
        var pipe = VideoFramePipeline.TryStart(
            framesDir, total, fps: 10,
            preset: ClusterVideoPreset.HighQualityH264Mp4,
            artifactBasePathNoExt: Path.Combine(td.Path, "artifact"),
            ct: CancellationToken.None);
        Assert.NotNull(pipe);

        // Mark 5 delivered before any frames are on disk → encoded=0,
        // backlog=5, IsBehind(4)=true.
        pipe!.NotifyFramesDelivered(5);
        Assert.Equal(5, pipe.DeliveredFrames);
        Assert.True(pipe.IsBehind(4));
        Assert.False(pipe.IsBehind(5));
        Assert.False(pipe.IsBehind(100));

        // Tear down without finishing — verifies DisposeAsync kills the
        // ffmpeg child cleanly even mid-encode.
        await pipe.DisposeAsync();
    }

    // ── helpers ───────────────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ff-vfp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}

/// <summary>Hand-rolls a valid 8-bit RGB PNG so the test rig can feed
/// real frames into ffmpeg without taking a SkiaSharp dependency on the
/// test project (Server.Tests stays UI-/imaging-stack free by design).
/// PNG chunk layout: signature + IHDR + IDAT (zlib-compressed raw
/// scanlines) + IEND.</summary>
internal static class TinyPng
{
    public static byte[] Make(int seed, int width, int height)
    {
        var rng = new Random(seed);
        // Raw image: per scanline = 1 filter byte + (3 bytes/px * width).
        var raw = new byte[height * (1 + width * 3)];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0; // filter type "none"
            for (int x = 0; x < width; x++)
            {
                raw[o++] = (byte)rng.Next(256);
                raw[o++] = (byte)rng.Next(256);
                raw[o++] = (byte)rng.Next(256);
            }
        }

        byte[] idat = ZlibDeflate(raw);
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(ms, "IHDR", BuildIhdr(width, height));
        WriteChunk(ms, "IDAT", idat);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] BuildIhdr(int w, int h)
    {
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, w);
        BigEndian(ihdr, 4, h);
        ihdr[8]  = 8;   // bit depth
        ihdr[9]  = 2;   // RGB
        ihdr[10] = 0;   // compression
        ihdr[11] = 0;   // filter
        ihdr[12] = 0;   // interlace
        return ihdr;
    }

    private static byte[] ZlibDeflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            zs.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] payload)
    {
        Span<byte> lenBe = stackalloc byte[4];
        BigEndian(lenBe, 0, payload.Length);
        s.Write(lenBe);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(payload);

        uint crc = Crc32(Concat(typeBytes, payload));
        Span<byte> crcBe = stackalloc byte[4];
        BigEndian(crcBe, 0, (int)crc);
        s.Write(crcBe);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static void BigEndian(byte[] dst, int off, int v)
    {
        dst[off]   = (byte)((v >> 24) & 0xFF);
        dst[off+1] = (byte)((v >> 16) & 0xFF);
        dst[off+2] = (byte)((v >> 8)  & 0xFF);
        dst[off+3] = (byte)(v & 0xFF);
    }

    private static void BigEndian(Span<byte> dst, int off, int v)
    {
        dst[off]   = (byte)((v >> 24) & 0xFF);
        dst[off+1] = (byte)((v >> 16) & 0xFF);
        dst[off+2] = (byte)((v >> 8)  & 0xFF);
        dst[off+3] = (byte)(v & 0xFF);
    }

    // PNG CRC-32 with polynomial 0xEDB88320 — same table the reference
    // PNG spec ships. Pre-compute lazily.
    private static readonly uint[] _crcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
            c = _crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
