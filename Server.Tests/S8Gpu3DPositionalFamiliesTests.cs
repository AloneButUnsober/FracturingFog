// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S8 GPU 3D lights slice 2 (#486) — Mandelbox + KIFS (Menger + Sierpinski). The
// #485 spL resolve pattern fans across three more per-fractal kernels, and each
// family's !HasPositionalLight force-CPU gate is lifted. On-device pixel output
// is validated by user smoke (like the rest of the GPU 3D shade path); what is
// executable here is that moving a point light changes the image on each family
// — true whether the render lands on the GPU (positional-aware now) or on a
// CPU-only host (the CPU shade always honoured LightSampler). Directional
// lighting stays byte-identical (Type 0 → the resolve is a no-op).

using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8Gpu3DPositionalFamiliesTests
{
    public enum Family { Mandelbox, Menger, Sierpinski }

    private static uint[] Render(Family fam, double lightPos)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = true;                 // request the GPU path
        var l = fx.Light1;
        l.Type = LightType.Point;
        l.Intensity = 3.0;
        l.Range = 0.0;                          // pure inverse-square
        l.PosX = lightPos; l.PosY = lightPos; l.PosZ = lightPos;
        fx.Light1 = l;

        var fp = new FractalParameters
        {
            Lighting = fx,
            KifsFold = fam == Family.Sierpinski ? KifsFoldKind.Sierpinski : KifsFoldKind.Menger,
        };

        dynamic calc = fam switch
        {
            Family.Mandelbox => new MandelboxCalculator(96, 72),
            _                => new KifsCalculator(96, 72),   // Menger / Sierpinski
        };
        calc.ColorMap = ColorPalette.BuiltIns[0];
        calc.FractalParameters = fp;
        calc.Zoom = 1.0;
        calc.Calculate(System.Threading.CancellationToken.None);
        return (uint[])((uint[])calc.ColorBuffer).Clone();
    }

    [Theory]
    [InlineData(Family.Mandelbox)]
    [InlineData(Family.Menger)]
    [InlineData(Family.Sierpinski)]
    public void PointLight_Position_Changes_The_Image(Family fam)
    {
        uint[] near = Render(fam, 2.0);    // point light beside the fractal
        uint[] far  = Render(fam, 60.0);   // same light, far away (dim)

        Assert.Equal(near.Length, far.Length);
        int diff = 0;
        for (int i = 0; i < near.Length; i++)
            if (near[i] != far[i]) diff++;
        Assert.True(diff > 0,
            $"moving a point light must change the {fam} image — the positional resolve did not reach the pixels");
    }

    [Theory]
    [InlineData(Family.Mandelbox)]
    [InlineData(Family.Menger)]
    [InlineData(Family.Sierpinski)]
    public void PointLight_Render_Is_Deterministic(Family fam)
    {
        Assert.Equal(Render(fam, 2.0), Render(fam, 2.0));
    }
}
