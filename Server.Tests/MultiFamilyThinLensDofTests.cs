// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S3 tail (#400/#567) — physically-based thin-lens DoF on the OTHER CPU 3D-fractal
// cameras (Mandelbox, Quaternion Julia / Mandelbrot, Bicomplex, Kleinian). The
// Mandelbulb camera landed first (PR #570); this routes each remaining family
// through the shared ThinLensDof accumulator. Same contract per family: off =
// byte-identical single-ray render; DofThinLens with a 0 aperture is still pinhole
// (byte-identical); an open aperture integrates the lens (a meaningful number of
// pixels change) and is deterministic (seeded lens jitter).

using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MultiFamilyThinLensDofTests
{
    public enum Family { Mandelbox, QuatJulia, QuatMandelbrot, Bicomplex, Kleinian }

    // Auto-focus (DofFocusDistance 0 → the family's camera distance) so the setup
    // needs no per-family camera knowledge; the default params centre each set so
    // a silhouette is present for the aperture to blur.
    private static uint[] Render(Family fam, double aperture, bool thinLens, int samples = 8)
    {
        var fx = LightingFxData.CreateDefault();
        fx.DofThinLens = thinLens;
        fx.DofAperture = aperture;
        fx.DofFocusDistance = 0.0;   // auto-focus the fractal centre
        fx.DofSamples = samples;
        fx.ShowSkyBackdrop = true;   // give the silhouette a background to blur against

        var fp = new FractalParameters { Lighting = fx };

        dynamic calc = fam switch
        {
            Family.Mandelbox      => new MandelboxCalculator(96, 72),
            Family.QuatJulia      => new QuatJuliaCalculator(96, 72),
            Family.QuatMandelbrot => new QuatMandelbrotCalculator(96, 72),
            Family.Bicomplex      => new BicomplexMandelbrotCalculator(96, 72),
            Family.Kleinian       => new KleinianCalculator(96, 72),
            _ => throw new System.ArgumentOutOfRangeException(nameof(fam)),
        };
        calc.ColorMap = ColorPalette.BuiltIns[0];
        calc.FractalParameters = fp;
        calc.Zoom = 1.0;
        calc.Calculate(System.Threading.CancellationToken.None);
        return (uint[])((uint[])calc.ColorBuffer).Clone();
    }

    [Theory]
    [InlineData(Family.Mandelbox)]
    [InlineData(Family.QuatJulia)]
    [InlineData(Family.QuatMandelbrot)]
    [InlineData(Family.Bicomplex)]
    [InlineData(Family.Kleinian)]
    public void ThinLens_Off_Is_Deterministic_Baseline(Family fam)
    {
        var a = Render(fam, aperture: 0.3, thinLens: false);
        var b = Render(fam, aperture: 0.3, thinLens: false);
        Assert.Equal(a, b);   // single-ray path, aperture ignored
    }

    [Theory]
    [InlineData(Family.Mandelbox)]
    [InlineData(Family.QuatJulia)]
    [InlineData(Family.QuatMandelbrot)]
    [InlineData(Family.Bicomplex)]
    [InlineData(Family.Kleinian)]
    public void ThinLens_On_ZeroAperture_Is_Pinhole_Identical(Family fam)
    {
        // DofThinLens on but aperture 0 → the thin-lens branch is inactive
        // (aperture > 0 required), so it matches the single-ray baseline exactly.
        var baseline = Render(fam, aperture: 0.0, thinLens: false);
        var pinhole  = Render(fam, aperture: 0.0, thinLens: true);
        Assert.Equal(baseline, pinhole);
    }

    [Theory]
    [InlineData(Family.Mandelbox)]
    [InlineData(Family.QuatJulia)]
    [InlineData(Family.QuatMandelbrot)]
    [InlineData(Family.Bicomplex)]
    [InlineData(Family.Kleinian)]
    public void ThinLens_OpenAperture_Blurs_And_Is_Deterministic(Family fam)
    {
        var pinhole = Render(fam, aperture: 0.0, thinLens: true);
        var dof1    = Render(fam, aperture: 0.35, thinLens: true);
        var dof2    = Render(fam, aperture: 0.35, thinLens: true);

        Assert.Equal(pinhole.Length, dof1.Length);
        Assert.Equal(dof1, dof2);   // seeded lens jitter → deterministic

        int diff = 0;
        for (int i = 0; i < pinhole.Length; i++)
            if (pinhole[i] != dof1[i]) diff++;
        Assert.True(diff > pinhole.Length / 100,
            $"{fam}: thin-lens DoF changed too few pixels ({diff}) — lens not integrated");
    }
}
