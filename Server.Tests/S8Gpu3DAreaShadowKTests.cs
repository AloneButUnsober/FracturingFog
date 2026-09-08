// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S8 #492 part 2 — GPU area-light penumbra on the 8 3D-fractal kernels + UserBulb.
// Like the relief kernel (part 1), EffectiveShadowK is constant per light per frame,
// so GpuShadingParams.Build precomputes a per-light area-capped hardness
// (ShadowK1/2/3) that each kernel uses in place of ShadowSoftK at its SoftShadow
// calls. On-device parity is user-smoke; the unit-testable surface is the CPU→GPU
// bridge (Build) + that an area light no longer forces the CPU path.

using System;
using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8Gpu3DAreaShadowKTests
{
    [Fact]
    public void Build_Punctual_ShadowK_Equals_GlobalK()
    {
        var fx = LightingFxData.CreateDefault();
        fx.ShadowSoftK = 8.0;
        var gp = GpuShadingParams.Build(in fx);
        Assert.Equal(8.0, gp.ShadowK1, 12);
        Assert.Equal(8.0, gp.ShadowK2, 12);
        Assert.Equal(8.0, gp.ShadowK3, 12);
    }

    [Fact]
    public void Build_AreaLight_Caps_ShadowK_PerLight()
    {
        var fx = LightingFxData.CreateDefault();
        fx.ShadowSoftK = 24.0;
        fx.Light1.AreaAngularRadius = 12.0;    // cot(12°) ≈ 4.7 < 24 → capped
        fx.Light2.AreaAngularRadius = 0.0;     // punctual → 24
        fx.Light3.AreaAngularRadius = 120.0;   // ≥ 90° → fully soft (0)

        var gp = GpuShadingParams.Build(in fx);
        Assert.Equal(ShadingPipeline.EffectiveShadowK(24.0, 12.0), gp.ShadowK1, 12);
        Assert.True(gp.ShadowK1 < 24.0 && gp.ShadowK1 > 0.0);
        Assert.Equal(24.0, gp.ShadowK2, 12);
        Assert.Equal(0.0, gp.ShadowK3, 12);
    }

    // With the area force-CPU gate lifted, a Mandelbulb with an area light + shadows
    // on still renders (and moving the light still changes the image). Runs whether
    // the frame lands on the GPU (area-aware now) or the CPU fallback.
    [Fact]
    public void Mandelbulb_AreaLight_Renders_And_Responds_To_Light()
    {
        static uint[] Render(double areaDeg)
        {
            var fx = LightingFxData.CreateDefault();
            fx.UseGpuRender = true;
            fx.ShadowSteps = 24;
            fx.ShadowSoftK = 24.0;
            fx.ShadowLightMask = 0x1;
            var l = fx.Light1; l.AreaAngularRadius = areaDeg; fx.Light1 = l;

            var fp = new FractalParameters
            {
                BulbPower = 8, BulbIterations = 12, BulbCameraDistance = 2.6, Lighting = fx,
            };
            var calc = new MandelbulbCalculator(96, 72)
            {
                ColorMap = ColorPalette.BuiltIns[0], FractalParameters = fp, Zoom = 1.0,
            };
            calc.Calculate(default);
            return (uint[])calc.ColorBuffer.Clone();
        }

        uint[] punctual = Render(0.0);
        uint[] area     = Render(15.0);
        Assert.Equal(punctual.Length, area.Length);
        // A finite emitter softens the penumbra → the shadowed band differs from the
        // sharp punctual shadow somewhere on the surface.
        int diff = 0;
        for (int i = 0; i < punctual.Length; i++)
            if (punctual[i] != area[i]) diff++;
        Assert.True(diff > 0, "an area light must change the shadow penumbra vs a punctual light");
    }
}
