// ServerHost/SkiaClusterImageCodec.cs
// D-2b: concrete IClusterImageCodec backed by SkiaSharp. Lives in
// ServerHost/ so both shells (WinExe FracturingFogCLD, cross-plat
// FracturingFog.App) pick it up via the source-link in their csproj.
// SkiaSharp is available transitively through Rendering.Skia in both
// hosts (WinExe already references it; FracturingFog.App gains the ref
// in this slice so the cluster path resolves).
//
// Decode path: SKBitmap.Decode → SKBitmap with Bgra8888/Premul →
// CopyTo into the merger's expected byte[] layout. The merger then
// pastes that buffer at the tile's (offX, offY) rect.
//
// Encode path: wrap the supplied BGRA byte[] in a pinned SKBitmap
// (InstallPixels — same pattern as PngSequenceWriter.SavePng) and
// encode to disk. Quality 100 because the master writes once and
// callers expect lossless fidelity from a cluster render.

using System;
using System.IO;
using System.Runtime.InteropServices;

using FracturingFog.Server.Cluster;
using SkiaSharp;

namespace FracturingFog.ServerHost;

public sealed class SkiaClusterImageCodec : IClusterImageCodec
{
    public byte[] DecodePngToBgra(byte[] png, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0) throw new InvalidDataException("empty PNG payload");

        // SKBitmap.Decode normalises to the native colour type. Force a
        // re-decode into Bgra8888/Premul so every codepath downstream
        // sees the same byte order the merger pastes.
        var info = new SKImageInfo(0, 0, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var src = SKBitmap.Decode(png)
            ?? throw new InvalidDataException("SKBitmap.Decode failed");
        width  = src.Width;
        height = src.Height;
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"decoded dims invalid: {width}x{height}");

        SKBitmap bgra;
        bool ownsBgra = false;
        if (src.ColorType == SKColorType.Bgra8888 && src.AlphaType == SKAlphaType.Premul)
        {
            bgra = src;
        }
        else
        {
            bgra = new SKBitmap(new SKImageInfo(width, height,
                SKColorType.Bgra8888, SKAlphaType.Premul));
            ownsBgra = true;
            if (!src.CopyTo(bgra, SKColorType.Bgra8888))
            {
                bgra.Dispose();
                throw new InvalidDataException("SKBitmap.CopyTo Bgra8888 failed");
            }
        }

        try
        {
            int stride = width * 4;
            byte[] outBytes = new byte[(long)stride * height];
            IntPtr pixPtr = bgra.GetPixels(out IntPtr pixLen);
            if (pixPtr == IntPtr.Zero || (long)pixLen < (long)stride * height)
                throw new InvalidDataException("SKBitmap.GetPixels returned empty/short buffer");
            Marshal.Copy(pixPtr, outBytes, 0, outBytes.Length);
            return outBytes;
        }
        finally
        {
            if (ownsBgra) bgra.Dispose();
        }
    }

    public unsafe void EncodeBgraToPng(byte[] bgra, int width, int height, string outPath)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(outPath);
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"invalid dims {width}x{height}");
        long want = (long)width * height * 4;
        if (bgra.LongLength != want)
            throw new ArgumentException(
                $"bgra length {bgra.LongLength} != expected {want} for {width}x{height}");

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        fixed (byte* p = bgra)
        {
            using var bmp = new SKBitmap();
            if (!bmp.InstallPixels(info, (IntPtr)p, info.RowBytes))
                throw new InvalidOperationException("SKBitmap.InstallPixels failed");
            using var image = SKImage.FromBitmap(bmp);
            using var data  = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("SKImage.Encode returned null");
            using var fs = File.Create(outPath);
            data.SaveTo(fs);
        }
    }
}
