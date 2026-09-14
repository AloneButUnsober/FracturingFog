// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Text;

using FracturingFog.Imaging;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #784 — streaming animated-GIF writer (GifSequenceWriter). Round-trips
/// through SkiaSharp's animated-GIF decoder (SKCodec) to validate frame count,
/// per-frame colours, timestamp-derived delays, and the NETSCAPE2.0 loop block.
/// </summary>
public sealed class ImageExportAnimatedGifTests
{
    private const int W = 12, H = 10;
    private const long TicksPerCs = 100_000; // 1 centisecond = 100_000 ticks of 100 ns

    private static uint[] Solid(uint argb)
    {
        var px = new uint[W * H];
        for (int i = 0; i < px.Length; i++) px[i] = argb;
        return px;
    }

    private static string WriteThreeFrameGif()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-agif-{Guid.NewGuid():N}.gif");
        using (var gw = new GifSequenceWriter(path, W, H, defaultDelayCs: 5))
        {
            // 5 cs (50 ms) between frames via timestamps.
            gw.WriteFrame(Solid(0xFFFF0000u), 0);                 // red
            gw.WriteFrame(Solid(0xFF00FF00u), 5 * TicksPerCs);   // green
            gw.WriteFrame(Solid(0xFF0000FFu), 10 * TicksPerCs);  // blue
        } // Dispose drains the encoder + writes trailer
        return path;
    }

    private static SKBitmap DecodeFrame(SKCodec codec, int index)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bmp = new SKBitmap(info);
        var opts = new SKCodecOptions(index);
        codec.GetPixels(info, bmp.GetPixels(), opts);
        return bmp;
    }

    [Fact]
    public void ThreeFrames_DecodeToCorrectCountAndColours()
    {
        string path = WriteThreeFrameGif();
        try
        {
            using var codec = SKCodec.Create(path);
            Assert.NotNull(codec);
            Assert.Equal(W, codec!.Info.Width);
            Assert.Equal(H, codec.Info.Height);
            Assert.Equal(3, codec.FrameCount);

            (byte r, byte g, byte b)[] expected =
            {
                (255, 0, 0), (0, 255, 0), (0, 0, 255),
            };
            for (int i = 0; i < 3; i++)
            {
                using var frame = DecodeFrame(codec, i);
                var c = frame.GetPixel(W / 2, H / 2);
                Assert.Equal(expected[i].r, c.Red);
                Assert.Equal(expected[i].g, c.Green);
                Assert.Equal(expected[i].b, c.Blue);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Frames_CarryTimestampDerivedDelays()
    {
        string path = WriteThreeFrameGif();
        try
        {
            using var codec = SKCodec.Create(path);
            var infos = codec!.FrameInfo;
            Assert.Equal(3, infos.Length);
            // 5 cs → 50 ms for the first two (measured), default 5 cs for the last.
            foreach (var fi in infos)
                Assert.InRange(fi.Duration, 40, 60);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void File_ContainsNetscapeLoopExtension()
    {
        string path = WriteThreeFrameGif();
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            string ascii = Encoding.ASCII.GetString(bytes);
            Assert.StartsWith("GIF89a", ascii);
            Assert.Contains("NETSCAPE2.0", ascii);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ManyFrames_EncodeAndDecodeThroughBoundedQueue()
    {
        // More frames than the internal queue bound (8) to exercise the
        // producer-blocks path and ordered background flushing.
        string path = Path.Combine(Path.GetTempPath(), $"ff-agif-many-{Guid.NewGuid():N}.gif");
        const int frames = 24;
        try
        {
            using (var gw = new GifSequenceWriter(path, W, H, defaultDelayCs: 4, queueBound: 4))
            {
                for (int i = 0; i < frames; i++)
                {
                    byte shade = (byte)(i * 10);
                    uint c = 0xFF000000u | (uint)(shade << 16) | (uint)(shade << 8) | shade;
                    gw.WriteFrame(Solid(c), i * 4L * TicksPerCs);
                }
            }
            using var codec = SKCodec.Create(path);
            Assert.NotNull(codec);
            Assert.Equal(frames, codec!.FrameCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
