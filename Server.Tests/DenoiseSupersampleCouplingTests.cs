// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S4 (#402) — adaptive-supersample coupling. The pure SS→SS policy plus its wiring
// into the CPU relief raymarch: off / denoise-off leaves the supersample (and the
// render) untouched; on + denoising drops the supersample so the render changes
// (fewer rays) yet stays deterministic.

using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DenoiseSupersampleCouplingTests
{
    [Theory]
    [InlineData(4, 0, true, 4)]    // denoise off → unchanged even when adaptive
    [InlineData(4, 3, false, 4)]   // adaptive off → unchanged even when denoising
    [InlineData(4, 3, true, 2)]    // 4 → 2 (16 → 4 rays)
    [InlineData(3, 3, true, 2)]    // 3 → 2
    [InlineData(2, 3, true, 1)]    // 2 → 1
    [InlineData(1, 3, true, 1)]    // 1 → 1 (already minimal)
    [InlineData(9, 3, true, 2)]    // clamps input to 4 first → 2
    public void EffectiveSupersample_Mapping(int ss, int iters, bool adaptive, int expected)
        => Assert.Equal(expected, DenoiseSupersampleCoupling.EffectiveSupersample(ss, iters, adaptive));

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

    private static FractalParameters Relief(int denoise, bool adaptive) => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraElevationDeg = 45,
        Relief2DSupersample = 4,          // high SS so the coupling has room to drop it
        Relief2DDenoiseIterations = denoise,
        Relief2DDenoiseAdaptiveSupersample = adaptive,
    };

    private static uint[] Render(FractalParameters p, int w, int h)
    {
        var (albedo, height) = Mandelbrot(w, h);
        var dst = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, p, dst, out _);
        return dst;
    }

    [Fact]
    public void AdaptiveOff_Is_ByteIdentical_Baseline()
    {
        const int w = 120, h = 90;
        var baseline = Render(Relief(denoise: 2, adaptive: false), w, h);
        var again = Render(Relief(denoise: 2, adaptive: false), w, h);
        Assert.Equal(baseline, again);
    }

    [Fact]
    public void Adaptive_On_But_DenoiseOff_Is_ByteIdentical()
    {
        // Coupling only bites when denoise is on, so adaptive-on with 0 passes must
        // match the plain SS-4 render exactly.
        const int w = 120, h = 90;
        var plain = Render(Relief(denoise: 0, adaptive: false), w, h);
        var adaptiveNoDenoise = Render(Relief(denoise: 0, adaptive: true), w, h);
        Assert.Equal(plain, adaptiveNoDenoise);
    }

    [Fact]
    public void Adaptive_On_WhileDenoising_Changes_Render_Deterministically()
    {
        // With denoise on, adaptive drops SS 4 → 2, so the primary-ray sampling (and
        // thus the image) differs from the full-SS denoise render — and repeats.
        const int w = 120, h = 90;
        var fullSs = Render(Relief(denoise: 2, adaptive: false), w, h);
        var coupled1 = Render(Relief(denoise: 2, adaptive: true), w, h);
        var coupled2 = Render(Relief(denoise: 2, adaptive: true), w, h);

        Assert.Equal(coupled1, coupled2);   // deterministic
        int diff = 0;
        for (int i = 0; i < fullSs.Length; i++)
            if (fullSs[i] != coupled1[i]) diff++;
        Assert.True(diff > 0, "adaptive coupling should change the sampled render");
    }
}
