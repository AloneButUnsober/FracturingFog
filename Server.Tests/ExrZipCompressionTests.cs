// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap S7 (#394) — ZIP compression for the OpenEXR writer. ZIP is the exact
// inverse of OpenExrReader's decode (inflate -> un-delta -> de-interleave), so
// the correctness contract is round-trip fidelity through that reader plus the
// spec raw-fallback (a block that doesn't shrink is stored verbatim). These lock:
//   • FLOAT ZIP round-trips bit-exact across multiple 16-line chunks (incl. a
//     partial last chunk).
//   • HALF ZIP round-trips within binary16 precision.
//   • ZIP actually shrinks a compressible image below the NONE encoding.
//   • Incompressible (random) data still round-trips exactly — exercises the
//     encoder's raw-fallback and the reader's raw detection.
//   • The 8-bit WriteBgra8 bridge round-trips under ZIP.

using System;
using System.Collections.Generic;
using System.IO;
using FracturingFog.Imaging;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ExrZipCompressionTests
{
    private static string TempExr() =>
        Path.Combine(Path.GetTempPath(), $"ff-exrzip-{Guid.NewGuid():N}.exr");

    // FLOAT + ZIP over 40 rows → chunks of 16 + 16 + 8, so the predictor/
    // interleave/deflate path is exercised across full and partial blocks.
    [Fact]
    public void FloatRgb_Zip_RoundTrips_Exactly_MultiChunk()
    {
        int w = 7, h = 40;
        var r = new float[w * h];
        var g = new float[w * h];
        var b = new float[w * h];
        for (int i = 0; i < w * h; i++)
        {
            r[i] = i * 0.125f;              // exact in float
            g[i] = 1.0f - (i % 17) * 0.03125f;
            b[i] = (i % 3 == 0) ? 4.25f : 0.5f;   // HDR values > 1
        }

        string path = TempExr();
        try
        {
            OpenExrWriter.WriteFile(path, w, h, new List<ExrChannel>
            {
                new("R", r, half: false),
                new("G", g, half: false),
                new("B", b, half: false),
            }, ExrCompression.Zip);

            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            Assert.Equal(w, img!.Width);
            Assert.Equal(h, img.Height);
            for (int i = 0; i < w * h; i++)
            {
                Assert.Equal(r[i], img.Data[i * 3 + 0]);
                Assert.Equal(g[i], img.Data[i * 3 + 1]);
                Assert.Equal(b[i], img.Data[i * 3 + 2]);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HalfRgb_Zip_RoundTrips_Within_HalfPrecision()
    {
        int w = 5, h = 20;
        var r = new float[w * h];
        var g = new float[w * h];
        var b = new float[w * h];
        for (int i = 0; i < w * h; i++) { r[i] = i * 0.1f; g[i] = 0.5f; b[i] = 3.0f - (i % 11) * 0.05f; }

        string path = TempExr();
        try
        {
            OpenExrWriter.WriteFile(path, w, h, new List<ExrChannel>
            {
                new("R", r, half: true),
                new("G", g, half: true),
                new("B", b, half: true),
            }, ExrCompression.Zip);

            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            for (int i = 0; i < w * h; i++)
            {
                Assert.Equal((float)(Half)r[i], img!.Data[i * 3 + 0], 3);
                Assert.Equal((float)(Half)g[i], img.Data[i * 3 + 1], 3);
                Assert.Equal((float)(Half)b[i], img.Data[i * 3 + 2], 3);
            }
        }
        finally { File.Delete(path); }
    }

    // A smooth (highly compressible) gradient: ZIP must produce a strictly
    // smaller file than the uncompressed NONE encoding.
    [Fact]
    public void Zip_Is_Smaller_Than_None_For_Compressible_Image()
    {
        int w = 64, h = 64;
        var bgra = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bgra[y * w + x] = 0xFF000000u | ((uint)x << 16) | ((uint)x << 8) | (uint)x; // horizontal ramp

        string pn = TempExr(), pz = TempExr();
        try
        {
            OpenExrWriter.WriteBgra8(pn, bgra, w, h, compression: ExrCompression.None);
            OpenExrWriter.WriteBgra8(pz, bgra, w, h, compression: ExrCompression.Zip);

            long none = new FileInfo(pn).Length;
            long zip  = new FileInfo(pz).Length;
            Assert.True(zip < none, $"ZIP ({zip}) should be smaller than NONE ({none}).");

            // ...and still decode to the same dimensions.
            Assert.True(HdriRegistry.TryLoadFromFile(pz, out var img) && img != null);
            Assert.Equal(w, img!.Width);
            Assert.Equal(h, img.Height);
        }
        finally { File.Delete(pn); File.Delete(pz); }
    }

    // Random FLOAT data barely compresses, so the encoder stores blocks raw
    // (chunkSize == uncompressed). The reader must detect that and skip
    // inflate/predictor/interleave — the value must still round-trip exactly.
    [Fact]
    public void Incompressible_Zip_RawFallback_RoundTrips_Exactly()
    {
        int w = 8, h = 32;
        var rng = new Random(12345);
        var r = new float[w * h];
        var g = new float[w * h];
        var b = new float[w * h];
        for (int i = 0; i < w * h; i++)
        {
            r[i] = (float)rng.NextDouble() * 10f;
            g[i] = (float)rng.NextDouble() * 10f;
            b[i] = (float)rng.NextDouble() * 10f;
        }

        string path = TempExr();
        try
        {
            OpenExrWriter.WriteFile(path, w, h, new List<ExrChannel>
            {
                new("R", r, half: false),
                new("G", g, half: false),
                new("B", b, half: false),
            }, ExrCompression.Zip);

            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            for (int i = 0; i < w * h; i++)
            {
                Assert.Equal(r[i], img!.Data[i * 3 + 0]);
                Assert.Equal(g[i], img.Data[i * 3 + 1]);
                Assert.Equal(b[i], img.Data[i * 3 + 2]);
            }
        }
        finally { File.Delete(path); }
    }

    // The 8-bit bridge under ZIP round-trips (linearized RGB), matching the NONE
    // path's decoded values.
    [Fact]
    public void WriteBgra8_Zip_Matches_None_Decoded()
    {
        int w = 24, h = 24;
        var bgra = new uint[w * h];
        for (int i = 0; i < bgra.Length; i++)
            bgra[i] = 0xFF000000u | ((uint)(i * 5 % 256) << 16) | ((uint)(i * 9 % 256) << 8) | (uint)(i * 13 % 256);

        string pn = TempExr(), pz = TempExr();
        try
        {
            OpenExrWriter.WriteBgra8(pn, bgra, w, h, compression: ExrCompression.None);
            OpenExrWriter.WriteBgra8(pz, bgra, w, h, compression: ExrCompression.Zip);

            Assert.True(HdriRegistry.TryLoadFromFile(pn, out var a) && a != null);
            Assert.True(HdriRegistry.TryLoadFromFile(pz, out var bimg) && bimg != null);
            Assert.Equal(a!.Data.Length, bimg!.Data.Length);
            for (int i = 0; i < a.Data.Length; i++)
                Assert.Equal(a.Data[i], bimg.Data[i], 4);
        }
        finally { File.Delete(pn); File.Delete(pz); }
    }
}
