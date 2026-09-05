// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S10.2 (PaletteBuilder-Design.md, #392) — the CVD-first suite.
// CvdSimulation (Machado 2009) + PaletteLint (confusability, luminance-lock, the
// Okabe-Ito set). Deterministic → the colour analog of the render parity twin:
//   * simulation is stable + severity 0 is identity + monochromacy is grey;
//   * red↔green collapses under deutan / protan (small ΔE in simulated space) while
//     black↔white survives (luminance-separated);
//   * Okabe-Ito confuses less than the RGB primaries;
//   * luminance monotonicity is detected.

using System.Collections.Generic;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class CvdAnalysisTests
{
    [Fact]
    public void Simulate_Severity0_Is_Identity()
    {
        foreach (var type in new[] { CvdType.Protan, CvdType.Deutan, CvdType.Tritan, CvdType.Monochromacy })
        {
            var (r, g, b) = CvdSimulation.Simulate(200, 120, 40, type, 0f);
            Assert.True(System.Math.Abs(r - 200) <= 1 && System.Math.Abs(g - 120) <= 1 && System.Math.Abs(b - 40) <= 1,
                $"{type} severity 0 should be identity, got {r},{g},{b}");
        }
    }

    [Fact]
    public void Monochromacy_Is_Neutral_Grey()
    {
        var (r, g, b) = CvdSimulation.Simulate(210, 40, 90, CvdType.Monochromacy);
        Assert.Equal(r, g);
        Assert.Equal(g, b);
    }

    [Fact]
    public void Simulate_Is_Deterministic()
    {
        var a = CvdSimulation.Simulate(30, 200, 60, CvdType.Deutan);
        var b = CvdSimulation.Simulate(30, 200, 60, CvdType.Deutan);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RedGreen_Collapses_Under_Deutan_Relative_To_Normal()
    {
        // Normal vision: red vs green is a big perceptual gap.
        float normal = PerceptualRamp.DeltaEOk(255, 0, 0, 0, 255, 0);
        Assert.True(normal > 0.3f);
        // Deuteranope: the gap shrinks substantially (they move toward each other).
        var r = CvdSimulation.Simulate(255, 0, 0, CvdType.Deutan);
        var g = CvdSimulation.Simulate(0, 255, 0, CvdType.Deutan);
        float simDe = PerceptualRamp.DeltaEOk(r.r, r.g, r.b, g.r, g.g, g.b);
        Assert.True(simDe < normal * 0.6f, $"deutan should shrink red/green: sim {simDe} vs normal {normal}");
    }

    [Fact]
    public void Confusables_Flags_RedGreen_Not_BlackWhite()
    {
        // Threshold above the deutan red/green collapse (~0.22) flags the pair …
        var rg = new List<(byte, byte, byte)> { (255, 0, 0), (0, 255, 0) };
        var flagged = PaletteLint.Confusables(rg, 0.3f, CvdType.Deutan, CvdType.Protan);
        Assert.NotEmpty(flagged);
        Assert.Contains(flagged, c => c.I == 0 && c.J == 1);

        // … while black vs white are luminance-separated → survive every CVD type even
        // at that generous threshold.
        var bw = new List<(byte, byte, byte)> { (0, 0, 0), (255, 255, 255) };
        var safe = PaletteLint.Confusables(bw, 0.3f, CvdType.Deutan, CvdType.Protan, CvdType.Tritan, CvdType.Monochromacy);
        Assert.Empty(safe);
    }

    [Fact]
    public void OkabeIto_Clears_A_JND_Under_Deutan_And_Protan()
    {
        Assert.Equal(8, PaletteLint.OkabeIto.Length);
        // The CVD-safe categorical set: every pair stays above a just-noticeable OkLab
        // ΔE under both common deficiencies (no indistinguishable pairs).
        var collapses = PaletteLint.Confusables(PaletteLint.OkabeIto, 0.02f, CvdType.Deutan, CvdType.Protan);
        Assert.True(collapses.Count == 0,
            $"Okabe-Ito should have no sub-JND pairs; worst: {(collapses.Count > 0 ? collapses[0].DeltaE : 0f)}");
    }

    [Fact]
    public void IsLuminanceMonotonic_Detects_Monotonic_And_NonMonotonic()
    {
        // Viridis sampled → monotonic lightness by design.
        var viridis = new List<(byte, byte, byte)>();
        for (int i = 0; i <= 16; i++) viridis.Add(PerceptualRamp.Viridis(i / 16f));
        Assert.True(PaletteLint.IsLuminanceMonotonic(viridis));

        // Dark → light → dark is not monotonic.
        var bump = new List<(byte, byte, byte)> { (10, 10, 10), (240, 240, 240), (40, 40, 40) };
        Assert.False(PaletteLint.IsLuminanceMonotonic(bump));

        // A descending ramp is still monotonic (non-increasing).
        var down = new List<(byte, byte, byte)> { (250, 250, 250), (128, 128, 128), (10, 10, 10) };
        Assert.True(PaletteLint.IsLuminanceMonotonic(down));
    }
}
