// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S3 (#400/#567) — thin-lens DOF fanned onto the remaining GPU 3D-fractal kernels
// (Mandelbox, Menger, Sierpinski, QuatJulia, QuatMandelbrot, Kleinian, Bicomplex),
// after the Mandelbulb flagship (#567 slice 1). Each kernel's trace+shade is
// extracted into ShadeRay(origin, dir) and wrapped in the same aperture-tap lens
// loop. On-device output is user-smoke; the executable lock here is that with
// UseGpuRender an open aperture integrates the lens (blurs) and is deterministic,
// while a 0 aperture is the single centre ray. Runs on the ILGPU CPU accelerator
// in CI (or the CPU thin-lens fallback), so the contract holds either way.

using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8Gpu3DThinLensDofFamiliesTests
{
    public enum Family { Mandelbox, Menger, Sierpinski, QuatJulia, QuatMandelbrot, Kleinian, Bicomplex }

    private static uint[] Render(Family fam, double aperture)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = true;
        fx.DofThinLens = aperture > 0.0;
        fx.DofAperture = aperture;
        fx.DofFocusDistance = 0.0;   // auto-focus the fractal centre
        fx.DofSamples = 8;
        fx.ShowSkyBackdrop = true;   // give silhouettes a backdrop to blur against

        var fp = new FractalParameters
        {
            Lighting = fx,
            KifsFold = fam == Family.Sierpinski ? KifsFoldKind.Sierpinski : KifsFoldKind.Menger,
        };

        dynamic calc = fam switch
        {
            Family.Mandelbox      => new MandelboxCalculator(96, 72),
            Family.Menger         => new KifsCalculator(96, 72),
            Family.Sierpinski     => new KifsCalculator(96, 72),
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

    // Menger / Sierpinski (KIFS) are intentionally NOT in this blur Theory: unlike
    // the other families they have no CPU thin-lens path (they were not part of the
    // CPU MultiFamilyThinLensDofTests), so on a GPU-less CI host they fall back to a
    // pinhole CPU render and no blur is producible without a device. Their GPU DOF
    // (same ShadeRay + lens-loop transform as the others) is validated by on-device
    // smoke; the determinism check below still covers them.
    [Theory]
    [InlineData(Family.Mandelbox)]
    [InlineData(Family.QuatJulia)]
    [InlineData(Family.QuatMandelbrot)]
    [InlineData(Family.Kleinian)]
    [InlineData(Family.Bicomplex)]
    public void OpenAperture_Blurs_And_Is_Deterministic(Family fam)
    {
        var pinhole = Render(fam, 0.0);
        var dof1    = Render(fam, 0.30);
        var dof2    = Render(fam, 0.30);

        Assert.Equal(pinhole.Length, dof1.Length);
        Assert.Equal(dof1, dof2);   // HashPair-seeded taps → deterministic

        int diff = 0;
        for (int i = 0; i < pinhole.Length; i++)
            if (pinhole[i] != dof1[i]) diff++;
        Assert.True(diff > pinhole.Length / 200,
            $"{fam}: an open aperture must integrate the lens (blur) — only {diff} px changed");
    }

    // All seven families (KIFS included) must render an open-aperture DOF frame
    // deterministically — the HashPair-seeded taps make the render --batch-stable.
    [Theory]
    [InlineData(Family.Mandelbox)]
    [InlineData(Family.Menger)]
    [InlineData(Family.Sierpinski)]
    [InlineData(Family.QuatJulia)]
    [InlineData(Family.QuatMandelbrot)]
    [InlineData(Family.Kleinian)]
    [InlineData(Family.Bicomplex)]
    public void OpenAperture_Is_Deterministic(Family fam)
    {
        Assert.Equal(Render(fam, 0.30), Render(fam, 0.30));
    }
}
