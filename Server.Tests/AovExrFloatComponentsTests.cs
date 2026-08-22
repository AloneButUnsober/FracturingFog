// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1/S7 deep tail (3D-Rendering-Roadmap.md, #389 / #398 / #394):
// FLOAT lighting-component AOVs. After the float geometry planes (#452), the shade
// pipeline now records the raw diffuse/specular/AO/shadow it resolves at each
// primary hit into a ShadeComponents buffer captured in the beauty pass, so the
// AOV EXR carries those layers at full precision instead of the 8-bit AovView
// re-renders. These lock: (1) supplying a component buffer drops the 8-bit
// Diffuse/Specular/AO/Shadow passes and emits the raw values while StepCount stays
// 8-bit; (2) the relief render fills the component buffer on hits.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AovExrFloatComponentsTests
{
    private static uint[] Fill(int n, uint v)
    {
        var b = new uint[n];
        for (int i = 0; i < n; i++) b[i] = v;
        return b;
    }

    private static PosterRequest ReliefRequest()
    {
        var fp = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            Relief2DGroundPlane = false,
        };
        return new PosterRequest
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0, Zoom = 1.0,
            MaxIterations = 150,
            Width = 96, Height = 72,
            ColorMap = ColorPalette.BuiltIns[0],
            Quality = QualityPreset.Standard,
            FractalParameters = fp,
            Path = "unused.exr",
            Format = ImageFileFormat.Exr,
        };
    }

    // Float components win: the 8-bit Diffuse/Specular/AO/Shadow passes are dropped
    // and the raw values are emitted; StepCount (no float source) stays 8-bit.
    [Fact]
    public void FloatComponents_Replace_EightBit_Lighting_Passes()
    {
        int w = 2, h = 2, n = w * h;
        var beauty = Fill(n, 0xFF000000u);
        var comps = new ShadingPipeline.ShadeComponents[n];
        comps[0] = new ShadingPipeline.ShadeComponents(0.25f, 0.5f, 0.75f, 0.1f, 0.2f, 0.3f, 0.6f, 0.9f);

        var aovs = new Dictionary<AovView, uint[]>
        {
            [AovView.Diffuse] = Fill(n, 0xFF204060u),
            [AovView.Specular] = Fill(n, 0xFF102030u),
            [AovView.AmbientOcclusion] = Fill(n, 0xFF808080u),
            [AovView.Shadow] = Fill(n, 0xFF404040u),
            [AovView.StepCount] = Fill(n, 0xFF603000u),  // no float source → kept 8-bit
        };

        var ch = AovExrExporter.BuildChannels(w, h, beauty, aovs, components: comps);

        // One float set of each lighting component; StepCount still present.
        Assert.Single(ch, c => c.Name == "diffuse.R");
        Assert.Single(ch, c => c.Name == "specular.R");
        Assert.Single(ch, c => c.Name == "AO.V");
        Assert.Single(ch, c => c.Name == "shadow.V");
        Assert.Contains(ch, c => c.Name == "stepcount.V");

        // Raw component values, not the 8-bit decode.
        Assert.Equal(0.5f, ch.First(c => c.Name == "diffuse.G").Data[0], 5);
        Assert.Equal(0.3f, ch.First(c => c.Name == "specular.B").Data[0], 5);
        Assert.Equal(0.6f, ch.First(c => c.Name == "AO.V").Data[0], 5);
        Assert.Equal(0.9f, ch.First(c => c.Name == "shadow.V").Data[0], 5);
    }

    // Without a component buffer the 8-bit lighting passes are emitted as before.
    [Fact]
    public void Without_Components_EightBit_Lighting_Is_Kept()
    {
        var aovs = new Dictionary<AovView, uint[]>
        {
            [AovView.Diffuse] = Fill(1, 0xFF3366CCu),
            [AovView.AmbientOcclusion] = Fill(1, 0xFF808080u),
        };
        var ch = AovExrExporter.BuildChannels(1, 1, Fill(1, 0xFF000000u), aovs);
        Assert.Equal(0x33 / 255f, ch.First(c => c.Name == "diffuse.R").Data[0], 4);
        Assert.Equal(0x80 / 255f, ch.First(c => c.Name == "AO.V").Data[0], 4);
    }

    // The relief render fills the component buffer with normalized values on hits.
    [Fact]
    public void RenderToPixels_Capture_Fills_Float_Components()
    {
        var req = ReliefRequest();
        var geo = new HeightfieldRaymarch2D.ReliefAovBuffers(req.Width, req.Height, captureComponents: true);
        var beauty = PosterRenderer.RenderToPixels(req, default, out int w, out int h, geo);

        Assert.NotNull(geo.Components);
        Assert.Equal(w * h, geo.Components!.Length);

        int lit = 0; bool aoInRange = true, shadowInRange = true;
        var diffVals = new HashSet<float>();
        for (int i = 0; i < geo.Components.Length; i++)
        {
            var c = geo.Components[i];
            if (c.Ao < 0f || c.Ao > 1.001f) aoInRange = false;
            if (c.Shadow < 0f || c.Shadow > 1.001f) shadowInRange = false;
            float d = c.DiffR + c.DiffG + c.DiffB;
            if (d > 0f) { lit++; diffVals.Add(MathF.Round(d, 3)); }
        }
        Assert.True(lit > 0, "some hit pixels carry non-zero diffuse");
        Assert.True(diffVals.Count >= 3, "diffuse should vary across the surface");
        Assert.True(aoInRange, "AO stays in [0,1]");
        Assert.True(shadowInRange, "shadow stays in [0,1]");
    }
}
