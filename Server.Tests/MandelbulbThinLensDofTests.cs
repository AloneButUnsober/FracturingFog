// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S3 (#400/#567) — physically-based thin-lens DoF on the CPU Mandelbulb camera.
// Averages fx.DofSamples aperture taps (CameraDof.ThinLensRay) instead of the
// screen-space gather. Locks: off = byte-identical; DofThinLens with a 0 aperture
// is still pinhole (byte-identical); an open aperture integrates the lens (the
// image differs in a meaningful number of pixels) and is deterministic.

using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MandelbulbThinLensDofTests
{
    private static uint[] Render(double aperture, bool thinLens, int samples = 8)
    {
        var fx = LightingFxData.CreateDefault();
        fx.DofThinLens = thinLens;
        fx.DofAperture = aperture;
        fx.DofFocusDistance = 2.6;
        fx.DofSamples = samples;

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
    public void ThinLens_Off_Is_Byte_Identical_Baseline()
    {
        var a = Render(aperture: 0.3, thinLens: false);
        var b = Render(aperture: 0.3, thinLens: false);
        Assert.Equal(a, b);   // deterministic single-ray path, aperture ignored
    }

    [Fact]
    public void ThinLens_On_ZeroAperture_Is_Pinhole_Identical()
    {
        // DofThinLens on but aperture 0 → the thin-lens branch is inactive
        // (aperture > 0 required), so it matches the single-ray baseline exactly.
        var baseline = Render(aperture: 0.0, thinLens: false);
        var pinhole = Render(aperture: 0.0, thinLens: true);
        Assert.Equal(baseline, pinhole);
    }

    [Fact]
    public void ThinLens_OpenAperture_Blurs_And_Is_Deterministic()
    {
        var pinhole = Render(aperture: 0.0, thinLens: true);
        var dof1 = Render(aperture: 0.35, thinLens: true);
        var dof2 = Render(aperture: 0.35, thinLens: true);

        Assert.Equal(pinhole.Length, dof1.Length);
        Assert.Equal(dof1, dof2);   // seeded lens jitter → deterministic

        int diff = 0;
        for (int i = 0; i < pinhole.Length; i++)
            if (pinhole[i] != dof1[i]) diff++;
        Assert.True(diff > pinhole.Length / 50,
            $"thin-lens DoF changed too few pixels ({diff}) — lens not integrated");
    }
}
