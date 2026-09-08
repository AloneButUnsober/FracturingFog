// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S3 (#400/#567) — physically-based thin-lens DoF on the GPU 3D-fractal kernels,
// starting with the Mandelbulb (flagship). The GPU kernel used to ignore DOF (it
// ran the single centre ray regardless); #567 wraps its trace+shade in the same
// aperture-tap lens loop the relief kernel uses (jitter the ray origin across the
// aperture disc, re-aim through the focal point, average). Off / aperture 0 →
// the single centre ray → byte-identical. Runs on the ILGPU CPU accelerator in
// CI when no GPU device is present (or the CPU thin-lens fallback), so the
// contract holds either way.

using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8Gpu3DThinLensDofTests
{
    private static uint[] Render(double aperture, bool thinLens, int samples = 8)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = true;                 // exercise the GPU kernel path
        fx.DofThinLens = thinLens;
        fx.DofAperture = aperture;
        fx.DofFocusDistance = 2.6;
        fx.DofSamples = samples;

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

    [Fact]
    public void Gpu_ThinLens_ZeroAperture_Is_Pinhole_Identical()
    {
        // Aperture 0 → dofN 1 → the single centre ray, identical to DOF off.
        var off     = Render(aperture: 0.0, thinLens: false);
        var pinhole = Render(aperture: 0.0, thinLens: true);
        Assert.Equal(off, pinhole);
    }

    [Fact]
    public void Gpu_ThinLens_OpenAperture_Blurs_And_Is_Deterministic()
    {
        var pinhole = Render(aperture: 0.0, thinLens: true);
        var dof1    = Render(aperture: 0.30, thinLens: true);
        var dof2    = Render(aperture: 0.30, thinLens: true);

        Assert.Equal(pinhole.Length, dof1.Length);
        Assert.Equal(dof1, dof2);   // HashPair-seeded taps → deterministic (--batch stable)

        int diff = 0;
        for (int i = 0; i < pinhole.Length; i++)
            if (pinhole[i] != dof1[i]) diff++;
        Assert.True(diff > pinhole.Length / 100,
            $"an open aperture must integrate the lens (blur) — only {diff} px changed");
    }
}
