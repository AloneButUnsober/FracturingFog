// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S8 GPU 3D lights slice 3 (#487) — Quaternion Julia / Mandelbrot, Kleinian,
// Bicomplex. The #485 spL resolve pattern fans across the last four non-UserBulb
// per-fractal kernels, and each family's !HasPositionalLight force-CPU gate is
// lifted (Bicomplex keeps its GPU path gated to the K slice axis). On-device
// pixel output is validated by user smoke; the executable lock is that moving a
// point light changes each family's image (true on the GPU positional path now,
// and on a CPU-only host where the CPU shade always honoured LightSampler).

using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8Gpu3DPositionalQuatKleinTests
{
    public enum Family { QuatJulia, QuatMandelbrot, Kleinian, Bicomplex }

    private static uint[] Render(Family fam, double lightPos)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = true;
        var l = fx.Light1;
        l.Type = LightType.Point;
        l.Intensity = 3.0;
        l.Range = 0.0;
        l.PosX = lightPos; l.PosY = lightPos; l.PosZ = lightPos;
        fx.Light1 = l;

        var fp = new FractalParameters { Lighting = fx };

        dynamic calc = fam switch
        {
            Family.QuatJulia      => new QuatJuliaCalculator(96, 72),
            Family.QuatMandelbrot => new QuatMandelbrotCalculator(96, 72),
            Family.Kleinian       => new KleinianCalculator(96, 72),
            Family.Bicomplex      => new BicomplexMandelbrotCalculator(96, 72),
            _ => throw new System.ArgumentOutOfRangeException(nameof(fam)),
        };
        calc.ColorMap = ColorPalette.BuiltIns[0];
        calc.FractalParameters = fp;
        calc.Zoom = 1.0;
        calc.Calculate(System.Threading.CancellationToken.None);
        return (uint[])((uint[])calc.ColorBuffer).Clone();
    }

    [Theory]
    [InlineData(Family.QuatJulia)]
    [InlineData(Family.QuatMandelbrot)]
    [InlineData(Family.Kleinian)]
    [InlineData(Family.Bicomplex)]
    public void PointLight_Position_Changes_The_Image(Family fam)
    {
        uint[] near = Render(fam, 2.0);
        uint[] far  = Render(fam, 60.0);

        Assert.Equal(near.Length, far.Length);
        int diff = 0;
        for (int i = 0; i < near.Length; i++)
            if (near[i] != far[i]) diff++;
        Assert.True(diff > 0,
            $"moving a point light must change the {fam} image — the positional resolve did not reach the pixels");
    }

    [Theory]
    [InlineData(Family.QuatJulia)]
    [InlineData(Family.QuatMandelbrot)]
    [InlineData(Family.Kleinian)]
    [InlineData(Family.Bicomplex)]
    public void PointLight_Render_Is_Deterministic(Family fam)
    {
        Assert.Equal(Render(fam, 2.0), Render(fam, 2.0));
    }
}
