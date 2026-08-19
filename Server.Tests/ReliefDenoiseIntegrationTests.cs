// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S4 integration (3D-Rendering-Roadmap.md, #389): wiring the pure
// guided À-Trous denoiser (AtrousDenoiser) into the relief-raymarch path via
// ReliefDenoisePass, keyed on the render's own float normal + depth AOVs (#416).
// These lock the INTEGRATION contract, not the operator (that has its own tests):
//   • off (0 passes) ⇒ MakeCapture null ⇒ Apply no-op ⇒ the relief beauty is
//     byte-for-byte identical to a plain render (the default stays untouched);
//   • on ⇒ a capture target is allocated, the raymarch fills the guides, and the
//     denoise measurably changes the beauty while leaving it deterministic.

using System;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefDenoiseIntegrationTests
{
    private static (uint[] albedo, float[] height) Mandelbrot(int w, int h)
    {
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, MaxIterations = 400,
            ColorMap = new MonoBandMap(),
        };
        calc.Calculate(default);
        return ((uint[])calc.ColorBuffer.Clone(), (float[])calc.SmoothBuffer.Clone());
    }

    private static FractalParameters Relief(int denoisePasses)
    {
        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            Relief2DGroundPlane = false,
            Relief2DSupersample = 2,
            Relief2DDenoiseIterations = denoisePasses,
        };
        return p;
    }

    // Render the relief beauty the same way every wired site does: capture (null
    // when off), Render, Apply.
    private static uint[] RenderRelief(uint[] albedo, float[] height, int w, int h, FractalParameters p)
    {
        var dst = new uint[w * h];
        var aov = ReliefDenoisePass.MakeCapture(p, w, h);
        HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, p, dst, out _, null, aov);
        ReliefDenoisePass.Apply(dst, aov, w, h, p);
        return dst;
    }

    [Fact]
    public void MakeCapture_Null_When_Off_NonNull_When_On()
    {
        Assert.Null(ReliefDenoisePass.MakeCapture(Relief(0), 64, 48));
        Assert.NotNull(ReliefDenoisePass.MakeCapture(Relief(3), 64, 48));
    }

    [Fact]
    public void MakeCapture_Null_When_Not_Raymarch()
    {
        // Denoise needs the raymarch guides; an emboss-relief scene never captures.
        var p = Relief(3);
        p.Relief2DRaymarch = false;
        Assert.Null(ReliefDenoisePass.MakeCapture(p, 64, 48));
    }

    [Fact]
    public void Apply_Is_NoOp_On_Null_Aov()
    {
        var buf = new uint[] { 0xFF112233u, 0xFF445566u, 0xFF778899u, 0xFFAABBCCu };
        var copy = (uint[])buf.Clone();
        ReliefDenoisePass.Apply(buf, null, 2, 2, Relief(3));
        Assert.Equal(copy, buf);
    }

    [Fact]
    public void Off_Is_ByteIdentical_To_Plain_Render()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);

        // Plain render, no capture, no denoise.
        var plain = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, Relief(0), plain, out _, null);

        // Wired path with denoise off — must match the plain render exactly.
        var wired = RenderRelief(albedo, height, w, h, Relief(0));
        Assert.Equal(plain, wired);
    }

    [Fact]
    public void On_Changes_Beauty_And_Is_Deterministic()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);

        var off = RenderRelief(albedo, height, w, h, Relief(0));
        var on1 = RenderRelief(albedo, height, w, h, Relief(4));
        var on2 = RenderRelief(albedo, height, w, h, Relief(4));

        Assert.Equal(w * h, on1.Length);
        Assert.Equal(on1, on2);   // deterministic

        int changed = 0;
        for (int i = 0; i < off.Length; i++) if (off[i] != on1[i]) changed++;
        Assert.True(changed > off.Length / 20,
            $"denoise should visibly alter the relief beauty (changed {changed}/{off.Length})");
    }
}
