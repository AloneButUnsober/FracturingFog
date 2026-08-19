// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S7 (3D-Rendering-Roadmap.md, parent #389) — the OpenEXR writer.
// The correctness contract for this slice is round-trip fidelity against the
// existing OpenExrReader (the encoder is the mirror of the decoder) plus
// byte-stable uncompressed output (the parity-twin analog for a file format).

using System;
using System.Collections.Generic;
using System.IO;
using FracturingFog.Imaging;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class OpenExrWriterTests
{
    private static string TempExr() =>
        Path.Combine(Path.GetTempPath(), $"ff-exr-{Guid.NewGuid():N}.exr");

    // A FLOAT EXR carries 32-bit samples exactly, so a written value reads back
    // bit-for-bit through OpenExrReader.
    [Fact]
    public void FloatRgb_RoundTrips_Exactly()
    {
        int w = 5, h = 3;
        var r = new float[w * h];
        var g = new float[w * h];
        var b = new float[w * h];
        for (int i = 0; i < w * h; i++)
        {
            r[i] = i * 0.125f;          // exact in float
            g[i] = 1.0f - i * 0.0625f;
            b[i] = i % 2 == 0 ? 2.5f : 0.0f;   // HDR values > 1
        }

        string path = TempExr();
        try
        {
            OpenExrWriter.WriteFile(path, w, h, new List<ExrChannel>
            {
                new("R", r, half: false),
                new("G", g, half: false),
                new("B", b, half: false),
            });

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

    // HALF channels round-trip within binary16 precision (~1e-3 relative).
    [Fact]
    public void HalfRgb_RoundTrips_Within_HalfPrecision()
    {
        int w = 4, h = 4;
        var r = new float[w * h];
        var g = new float[w * h];
        var b = new float[w * h];
        for (int i = 0; i < w * h; i++) { r[i] = i * 0.1f; g[i] = 0.5f; b[i] = 3.0f - i * 0.05f; }

        string path = TempExr();
        try
        {
            OpenExrWriter.WriteFile(path, w, h, new List<ExrChannel>
            {
                new("R", r, half: true),
                new("G", g, half: true),
                new("B", b, half: true),
            });

            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            for (int i = 0; i < w * h; i++)
            {
                // Reference = value quantized to HALF, so the only error is the
                // encode rounding, not the reader.
                Assert.Equal((float)(Half)r[i], img!.Data[i * 3 + 0], 3);
                Assert.Equal((float)(Half)g[i], img.Data[i * 3 + 1], 3);
                Assert.Equal((float)(Half)b[i], img.Data[i * 3 + 2], 3);
            }
        }
        finally { File.Delete(path); }
    }

    // Uncompressed EXR output is deterministic — same input, byte-identical file.
    // This is the format analog of the render parity twin's bit-stability.
    [Fact]
    public void Output_Is_Byte_Stable()
    {
        int w = 8, h = 6;
        var bgra = new uint[w * h];
        for (int i = 0; i < bgra.Length; i++)
            bgra[i] = 0xFF000000u | ((uint)(i * 3 % 256) << 16) | ((uint)(i * 7 % 256) << 8) | (uint)(i * 11 % 256);

        string p1 = TempExr(), p2 = TempExr();
        try
        {
            OpenExrWriter.WriteBgra8(p1, bgra, w, h);
            OpenExrWriter.WriteBgra8(p2, bgra, w, h);
            Assert.Equal(File.ReadAllBytes(p1), File.ReadAllBytes(p2));

            // Magic number sanity — first 4 bytes are the EXR magic (LE).
            var head = File.ReadAllBytes(p1);
            Assert.Equal(0x76, head[0]); Assert.Equal(0x2f, head[1]);
            Assert.Equal(0x31, head[2]); Assert.Equal(0x01, head[3]);
        }
        finally { File.Delete(p1); File.Delete(p2); }
    }

    // The 8-bit bridge linearizes sRGB by default, so a mid-gray byte lands near
    // linear 0.5 (not 0.737), which is the scene-linear value a compositor wants.
    [Fact]
    public void WriteBgra8_Linearizes_Srgb()
    {
        int w = 2, h = 2;
        // 0xBC = 188 → sRGB 0.737 → linear ~0.502.
        var bgra = new uint[w * h];
        for (int i = 0; i < bgra.Length; i++) bgra[i] = 0xFFBCBCBCu;

        string path = TempExr();
        try
        {
            OpenExrWriter.WriteBgra8(path, bgra, w, h, linearize: true, half: false);
            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            Assert.Equal(0.502f, img!.Data[0], 2);   // linearized, not 0.737
        }
        finally { File.Delete(path); }
    }

    // Non-linear (raw) mode keeps the 8-bit value as-is (0.737), for callers that
    // want a display-referred EXR.
    [Fact]
    public void WriteBgra8_Raw_Keeps_DisplayValue()
    {
        int w = 2, h = 2;
        var bgra = new uint[w * h];
        for (int i = 0; i < bgra.Length; i++) bgra[i] = 0xFFBCBCBCu;

        string path = TempExr();
        try
        {
            OpenExrWriter.WriteBgra8(path, bgra, w, h, linearize: false, half: false);
            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            Assert.Equal(188f / 255f, img!.Data[0], 4);
        }
        finally { File.Delete(path); }
    }

    // A named-AOV multi-channel image (beauty RGBA + a Z depth plane) writes and
    // the reader still recovers RGB regardless of the extra channel — proves the
    // channels are emitted in valid alphabetical order (A,B,G,R,Z).
    [Fact]
    public void MultiChannel_With_Extra_Aov_RoundTrips_Rgb()
    {
        int w = 3, h = 2;
        var r = new float[w * h]; var g = new float[w * h];
        var b = new float[w * h]; var a = new float[w * h];
        var z = new float[w * h];
        for (int i = 0; i < w * h; i++) { r[i] = 0.2f * i; g[i] = 0.3f; b[i] = 0.1f; a[i] = 1f; z[i] = 100f + i; }

        string path = TempExr();
        try
        {
            OpenExrWriter.WriteFile(path, w, h, new List<ExrChannel>
            {
                new("R", r, half: false), new("G", g, half: false),
                new("B", b, half: false), new("A", a, half: false),
                new("Z", z, half: false),
            });

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
}
