// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1 (3D-Rendering-Roadmap.md, parent #389) — the AOV multi-layer
// EXR packer. Contract: beauty is always the bare RGBA layer; each AOV adds a
// correctly-named, correctly-decoded sub-layer; the file the reader accepts
// still recovers the beauty RGB. Built on the S7 OpenExrWriter.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FracturingFog.Imaging;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AovExrExporterTests
{
    private static string TempExr() =>
        Path.Combine(Path.GetTempPath(), $"ff-aov-{Guid.NewGuid():N}.exr");

    private static uint[] Fill(int n, uint v)
    {
        var b = new uint[n];
        for (int i = 0; i < n; i++) b[i] = v;
        return b;
    }

    // Beauty is always the bare R/G/B/A layer, even with no AOVs.
    [Fact]
    public void Beauty_Always_Emits_Rgba()
    {
        var ch = AovExrExporter.BuildChannels(2, 2, Fill(4, 0xFF808080u),
            new Dictionary<AovView, uint[]>());
        var names = ch.Select(c => c.Name).ToArray();
        Assert.Contains("R", names);
        Assert.Contains("G", names);
        Assert.Contains("B", names);
        Assert.Contains("A", names);
        Assert.Equal(4, ch.Count);
    }

    // Beauty RGB is linearized (0xBC ≈ sRGB 0.737 → linear ~0.502); alpha raw.
    [Fact]
    public void Beauty_Rgb_Is_Linearized_Alpha_Raw()
    {
        var ch = AovExrExporter.BuildChannels(1, 1, Fill(1, 0x80BCBCBCu),
            new Dictionary<AovView, uint[]>());
        float r = ch.First(c => c.Name == "R").Data[0];
        float a = ch.First(c => c.Name == "A").Data[0];
        Assert.Equal(0.502f, r, 2);
        Assert.Equal(0x80 / 255f, a, 4);
    }

    // The normal AOV is decoded from packed [0,1] back to [-1,1].
    [Fact]
    public void Normal_Aov_Decodes_To_SignedRange()
    {
        // R=0xFF → +1, G=0x80 → ~0, B=0x00 → -1.
        var aovs = new Dictionary<AovView, uint[]> { [AovView.Normals] = Fill(1, 0xFFFF8000u) };
        var ch = AovExrExporter.BuildChannels(1, 1, Fill(1, 0xFF000000u), aovs);
        Assert.Equal(1f, ch.First(c => c.Name == "normal.R").Data[0], 2);
        Assert.Equal(0f, ch.First(c => c.Name == "normal.G").Data[0], 1);
        Assert.Equal(-1f, ch.First(c => c.Name == "normal.B").Data[0], 2);
    }

    // Depth → single Z plane; AO → AO.V; each named per the multi-layer convention.
    [Fact]
    public void Scalar_Aovs_Get_Named_Data_Planes()
    {
        var aovs = new Dictionary<AovView, uint[]>
        {
            [AovView.Depth] = Fill(1, 0xFF404040u),
            [AovView.AmbientOcclusion] = Fill(1, 0xFFC0C0C0u),
            [AovView.Shadow] = Fill(1, 0xFF000000u),
        };
        var ch = AovExrExporter.BuildChannels(1, 1, Fill(1, 0xFF000000u), aovs);
        var names = ch.Select(c => c.Name).ToArray();
        Assert.Contains("Z", names);
        Assert.Contains("AO.V", names);
        Assert.Contains("shadow.V", names);
        Assert.Equal(0x40 / 255f, ch.First(c => c.Name == "Z").Data[0], 4);
        Assert.Equal(0xC0 / 255f, ch.First(c => c.Name == "AO.V").Data[0], 4);
    }

    // A multi-layer file the reader accepts still recovers the beauty RGB — the
    // extra AOV channels don't corrupt the default layer.
    [Fact]
    public void MultiLayer_File_RoundTrips_Beauty_Rgb()
    {
        int w = 4, h = 3;
        var beauty = new uint[w * h];
        for (int i = 0; i < beauty.Length; i++)
            beauty[i] = 0xFF000000u | ((uint)(i * 9 % 256) << 16) | ((uint)(i * 5 % 256) << 8) | (uint)(i * 3 % 256);

        var aovs = new Dictionary<AovView, uint[]>
        {
            [AovView.Normals] = Fill(w * h, 0xFF8080FFu),
            [AovView.Depth] = Fill(w * h, 0xFF606060u),
            [AovView.AmbientOcclusion] = Fill(w * h, 0xFFA0A0A0u),
        };

        string path = TempExr();
        try
        {
            AovExrExporter.Write(path, w, h, beauty, aovs);
            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            Assert.Equal(w, img!.Width);
            Assert.Equal(h, img.Height);
            for (int i = 0; i < w * h; i++)
            {
                // Reader returns the linear beauty RGB we wrote (half-precision).
                float er = ViewTransformOps.SrgbToLinear(((beauty[i] >> 16) & 0xFF) / 255f);
                Assert.Equal((float)(Half)er, img.Data[i * 3 + 0], 3);
            }
        }
        finally { File.Delete(path); }
    }

    // Beauty view passed inside the AOV map is ignored (already emitted once).
    [Fact]
    public void Beauty_In_Aov_Map_Is_Not_Duplicated()
    {
        var aovs = new Dictionary<AovView, uint[]> { [AovView.Beauty] = Fill(1, 0xFFFFFFFFu) };
        var ch = AovExrExporter.BuildChannels(1, 1, Fill(1, 0xFF000000u), aovs);
        Assert.Equal(4, ch.Count);   // still just R,G,B,A
    }
}
