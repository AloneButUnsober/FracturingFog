// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S2 (#396) — the EXR READ-BACK producer + consumer. A scene-linear
// OpenEXR carries real highlight headroom (values > 1.0); reading one back into the
// LinearFloatImage intermediate and applying a view transform regrades / tonemaps it
// WITHOUT re-rendering. These lock:
//   * FromLinearRgb / FromExr carry values above 1.0 with full headroom;
//   * a plain (None) encode saturates a > 1.0 highlight, while a view transform rolls
//     it off instead of clipping — the recovery the 8-bit path structurally can't do;
//   * ExrRegrade end-to-end reads → tonemaps → writes; a non-EXR input fails cleanly.

using System;
using System.Collections.Generic;
using System.IO;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ExrReadBackTests
{
    // Write a 1x1 scene-linear float EXR with the given RGB (full-float channels so
    // values are exact — half would round 2.0/0.5/1.0 fine but float is unambiguous).
    private static byte[] WriteExr(float r, float g, float b, int w = 1, int h = 1)
    {
        var rc = new float[w * h]; var gc = new float[w * h]; var bc = new float[w * h];
        Array.Fill(rc, r); Array.Fill(gc, g); Array.Fill(bc, b);
        var channels = new List<ExrChannel>
        {
            new ExrChannel("R", rc, half: false),
            new ExrChannel("G", gc, half: false),
            new ExrChannel("B", bc, half: false),
        };
        using var ms = new MemoryStream();
        OpenExrWriter.Write(ms, w, h, channels);
        return ms.ToArray();
    }

    [Fact]
    public void FromLinearRgb_CarriesHeadroom_And_DefaultsOpaque()
    {
        var rgb = new float[] { 2.5f, 0.5f, 0f };
        var img = LinearFloatImage.FromLinearRgb(rgb, 1, 1);
        Assert.Equal(2.5f, img.Rgb[0]);   // > 1.0 survives
        Assert.Equal(0.5f, img.Rgb[1]);
        Assert.Equal(1f, img.Alpha[0]);   // opaque default
    }

    [Fact]
    public void FromLinearRgb_HonoursExplicitAlpha()
    {
        var img = LinearFloatImage.FromLinearRgb(new float[] { 1f, 1f, 1f }, 1, 1, new float[] { 0.25f });
        Assert.Equal(0.25f, img.Alpha[0]);
    }

    [Fact]
    public void FromExr_Reads_LinearRgb_WithHeadroom()
    {
        var bytes = WriteExr(2.0f, 0.5f, 1.0f);
        using var ms = new MemoryStream(bytes);
        var img = LinearFloatImage.FromExr(ms);
        Assert.NotNull(img);
        Assert.Equal(1, img!.Width);
        Assert.Equal(2.0f, img.Rgb[0], 4);   // headroom preserved through read-back
        Assert.Equal(0.5f, img.Rgb[1], 4);
        Assert.Equal(1.0f, img.Rgb[2], 4);
    }

    [Fact]
    public void ReadBack_None_Saturates_But_ViewTransform_RollsOff()
    {
        var bytes = WriteExr(2.0f, 0.5f, 1.0f);

        // None: the > 1.0 red saturates at the sRGB encode (hard clip).
        using (var ms = new MemoryStream(bytes))
        {
            var none = LinearFloatImage.FromExr(ms)!.ApplyViewTransform(ViewTransform.None).ToBgra();
            int r = (int)((none[0] >> 16) & 0xFF);
            Assert.Equal(255, r);
        }

        // Reinhard: 2.0 -> 2/(1+2)=0.667 linear -> rolled off, strictly below 255.
        using (var ms = new MemoryStream(bytes))
        {
            var tone = LinearFloatImage.FromExr(ms)!.ApplyViewTransform(ViewTransform.Reinhard).ToBgra();
            int r = (int)((tone[0] >> 16) & 0xFF);
            Assert.True(r < 255, "a view transform must roll the >1 highlight off, not clip it");
            Assert.True(r > 180, "0.667 linear encodes well up the ramp");
        }
    }

    [Fact]
    public void FromExr_NonExr_ReturnsNull()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Null(LinearFloatImage.FromExr(ms));
    }

    [Fact]
    public void ExrRegrade_RoundTrips_ToFile_And_RejectsNonExr()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ff_exr_readback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string exr = Path.Combine(dir, "src.exr");
            File.WriteAllBytes(exr, WriteExr(2.0f, 0.5f, 1.0f, 4, 4));

            var bgra = ExrRegrade.ToneMapToBgra(exr, ViewTransform.AcesFilmic, 0f, out int w, out int h);
            Assert.NotNull(bgra);
            Assert.Equal(4, w);
            Assert.Equal(4, h);

            string png = Path.Combine(dir, "out.png");
            Assert.True(ExrRegrade.RenderToFile(exr, png, ViewTransform.AcesFilmic, 0f));
            Assert.True(new FileInfo(png).Length > 0);

            // A non-EXR input fails cleanly (no throw, no file).
            string bad = Path.Combine(dir, "bad.exr");
            File.WriteAllBytes(bad, new byte[] { 0, 0, 0, 0 });
            string badOut = Path.Combine(dir, "bad.png");
            Assert.False(ExrRegrade.RenderToFile(bad, badOut, ViewTransform.AcesFilmic, 0f));
            Assert.False(File.Exists(badOut));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
