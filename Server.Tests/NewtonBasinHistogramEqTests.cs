// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #146 (slice 2 of #144) — histogram equalization for the root-finding basin
// families (Newton / Halley / Secant). Unlike escape-time HE, these color by
// basin HUE + convergence-count SHADE via INewtonColorMap.MapNewton (or a
// built-in HSV fallback), so they carry a dedicated basin-aware equalizer
// (NewtonBasinColoring) rather than reusing HistogramEqualizer. These tests
// lock the design contract through the public ISupportsHistogramEq surface:
//
//   • strength 0 is an identity recolor (byte-identical to Calculate) — for the
//     built-in fallback, which keys only on basin + iter (no zr/zi);
//   • non-converged interior pixels (basin < 0) are never touched by HE;
//   • a categorical basin-only theme is INVARIANT under HE at full strength —
//     proving HE redistributes the shade term WITHOUT fighting basin hue (the
//     issue's central design worry);
//   • a shaded theme actually changes under HE (the equalizer does something).

using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class NewtonBasinHistogramEqTests
{
    private const int W = 96, H = 72;
    private const double Cx = 0.0, Cy = 0.0, Zoom = 0.5;

    public enum Kind { Newton, Halley, Secant }

    private static (IFractalCalculator calc, ISupportsHistogramEq he) Make(
        Kind kind, IColorMap map, int maxIter)
    {
        var fp = new FractalParameters { NewtonExponent = 3, NewtonRelaxation = 1.0 };
        // FractalParameters is not on IFractalCalculator, so set it on the
        // concrete type before generalizing.
        IFractalCalculator c;
        switch (kind)
        {
            case Kind.Newton: c = new NewtonCalculator(W, H) { FractalParameters = fp }; break;
            case Kind.Halley: c = new HalleyCalculator(W, H) { FractalParameters = fp }; break;
            default:          c = new SecantCalculator(W, H) { FractalParameters = fp }; break;
        }
        c.CenterX = Cx; c.CenterY = Cy; c.Zoom = Zoom; c.MaxIterations = maxIter;
        c.Quality = QualityPreset.Standard; c.ColorMap = map;
        c.Calculate(default);
        return (c, (ISupportsHistogramEq)c);
    }

    // ── strength 0 = identity recolor (byte-identical) ──────────────────────────
    [Theory]
    [InlineData(Kind.Newton)]
    [InlineData(Kind.Halley)]
    [InlineData(Kind.Secant)]
    public void Strength0_Is_Identity(Kind kind)
    {
        // Built-in HSV fallback (HsvPalette is NOT an INewtonColorMap) keys only
        // on basin + iter — no zr/zi — so a strength-0 recolor is bit-exact.
        var (calc, he) = Make(kind, new HsvPalette(), maxIter: 32);
        var baseline = (uint[])calc.ColorBuffer.Clone();

        Assert.True(he.BuildHistogramCdf(out var cdf, out int bins, out int src));
        he.ApplyHistogramEqualizationWithCdf(cdf!, bins, src, strength: 0.0);

        Assert.Equal(baseline, calc.ColorBuffer);
    }

    // ── categorical basin-only theme is invariant under full-strength HE ────────
    // HE remaps the iteration (shade) term; a hue-per-basin theme ignores iter,
    // so the equalized image must be byte-identical — HE does not disturb hue.
    [Theory]
    [InlineData(Kind.Newton)]
    [InlineData(Kind.Halley)]
    [InlineData(Kind.Secant)]
    public void Categorical_Theme_Invariant_Under_HE(Kind kind)
    {
        var (calc, he) = Make(kind, new NewtonBasinClassicMap(), maxIter: 32);
        var baseline = (uint[])calc.ColorBuffer.Clone();

        he.ApplyHistogramEqualization(strength: 1.0);

        Assert.Equal(baseline, calc.ColorBuffer);
    }

    // ── HE at full strength recolors converged pixels but never the interior ────
    [Theory]
    [InlineData(Kind.Newton)]
    [InlineData(Kind.Halley)]
    [InlineData(Kind.Secant)]
    public void Full_Strength_Changes_Exterior_Not_Interior(Kind kind)
    {
        // Shaded theme -> iter drives shade, so equalization must alter it.
        var map = new NewtonBasinShadedMap();
        var (calc, he) = Make(kind, map, maxIter: 32);
        var baseline = (uint[])calc.ColorBuffer.Clone();

        // Interior (basin < 0) with this INewtonColorMap is opaque black.
        uint interior = unchecked((uint)0xFF000000);

        he.ApplyHistogramEqualization(strength: 1.0);

        int changed = 0, interiorCount = 0, exteriorCount = 0;
        for (int i = 0; i < baseline.Length; i++)
        {
            bool wasInterior = baseline[i] == interior;
            if (wasInterior)
            {
                interiorCount++;
                Assert.Equal(interior, calc.ColorBuffer[i]);   // never touched
            }
            else
            {
                exteriorCount++;
                if (calc.ColorBuffer[i] != baseline[i]) changed++;
            }
        }

        Assert.True(interiorCount > 0, "frame should contain non-converged interior pixels");
        Assert.True(exteriorCount > 0, "frame should contain converged exterior pixels");
        Assert.True(changed > 0, "full-strength HE should recolor some converged pixels");
    }

    // ── BuildCdf reports the identity case when nothing converges ───────────────
    [Fact]
    public void No_Converged_Pixels_Is_Identity_Case()
    {
        // Zero relaxation -> the iterate never moves; a far-off center keeps every
        // pixel distant from the unit-circle roots, so no pixel converges: every
        // pixel is non-converged (basin < 0) and the CDF is empty.
        var fp = new FractalParameters { NewtonExponent = 3, NewtonRelaxation = 0.0 };
        var c = new NewtonCalculator(W, H)
        {
            CenterX = 100.0, CenterY = 100.0, Zoom = 1.0, MaxIterations = 32,
            Quality = QualityPreset.Standard, ColorMap = new HsvPalette(), FractalParameters = fp,
        };
        c.Calculate(default);
        var baseline = (uint[])c.ColorBuffer.Clone();

        Assert.False(((ISupportsHistogramEq)c).BuildHistogramCdf(out _, out _, out _));

        // ApplyHistogramEqualization must be a no-op when there is no CDF.
        ((ISupportsHistogramEq)c).ApplyHistogramEqualization(1.0);
        Assert.Equal(baseline, c.ColorBuffer);
    }
}
