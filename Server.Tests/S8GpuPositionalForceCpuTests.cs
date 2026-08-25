// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap S8 (#404) — force the CPU shade path on the GPU 3D-fractal calculators
// when a positional (point/spot) light is active. Those GPU kernels resolve only
// a DIRECTIONAL light (from Theta/Phi) and ignore world position, so with the
// GPU path a point light would render position-invariant (silently wrong). The
// calculators now gate the GPU branch on `!fx.HasPositionalLight`, dropping to
// the CPU shade (which honours LightSampler) so positional lighting is correct.
//
// This renders a Mandelbulb with UseGpuRender ON and a Point light at two world
// positions and asserts the images differ: on a GPU host the difference only
// survives if the force-CPU gate fired (the GPU kernel would ignore the move);
// on a CPU-only host it always renders CPU, so the test still documents + locks
// the positional-lighting behaviour. Directional lighting is unchanged.

using System;
using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8GpuPositionalForceCpuTests
{
    private static uint[] RenderBulb(double lightPos)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = true;                 // ask for the GPU path
        var l = fx.Light1;
        l.Type = LightType.Point;               // ...which is directional-only
        l.Intensity = 3.0;
        l.Range = 0.0;                          // pure inverse-square
        l.PosX = lightPos; l.PosY = lightPos; l.PosZ = lightPos;
        fx.Light1 = l;

        var fp = new FractalParameters
        {
            BulbPower = 8,
            BulbIterations = 12,
            BulbCameraDistance = 2.6,
            Lighting = fx,
        };

        var calc = new MandelbulbCalculator(96, 72)
        {
            ColorMap = ColorPalette.BuiltIns[0],
            FractalParameters = fp,
            Zoom = 1.0,
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    [Fact]
    public void Mandelbulb_PointLight_Position_Changes_The_Image()
    {
        uint[] near = RenderBulb(2.0);   // point light beside the bulb
        uint[] far  = RenderBulb(60.0);  // same light, far away (dim)

        Assert.Equal(near.Length, far.Length);

        // The bulb surface must actually be lit differently — i.e. positional
        // lighting reached the pixels (only true on the CPU shade path).
        int diff = 0;
        for (int i = 0; i < near.Length; i++)
            if (near[i] != far[i]) diff++;
        Assert.True(diff > 0,
            "moving a point light must change the Mandelbulb image — GPU force-CPU gate failed or CPU shade ignores position");
    }
}
