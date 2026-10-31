// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1591 — the Phase Wheel (Blue-Amber) theme. Oracles: OkLab read back from the
// rendered RGB (a ≈ 0: no red↔green axis), and the Machado–Oliveira–Fernandes CVD
// simulation (CvdAnalysis) — the wheel must stay distinguishable for deutan and
// protan viewers, not just in principle.
public sealed class PhaseWheelThemeTests
{
    private static readonly PhaseWheelBlueAmberMap Wheel = new();

    private static (byte r, byte g, byte b) Rgb(int argb) => ((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    [Fact]
    public void OneSeamlessTurnOverTheRange_AtAnyIterationCount()
    {
        Assert.Equal(PhaseWheelBlueAmberMap.ColorAt(0), PhaseWheelBlueAmberMap.ColorAt(1));
        Assert.Equal(PhaseWheelBlueAmberMap.ColorAt(0.37), PhaseWheelBlueAmberMap.ColorAt(1.37));
        foreach (double t in new[] { 0.0, 0.1, 0.25, 0.5, 0.77, 0.999 })
            Assert.Equal(Wheel.Map((float)(t * 16), 0f, 16), Wheel.Map((float)(t * 400), 0f, 400));
        // Just below the top of the range meets the bottom (phase 1 ≡ 0): no seam.
        var top = Rgb(Wheel.Map(399.9f, 0f, 400)); var bottom = Rgb(Wheel.Map(0f, 0f, 400));
        Assert.True(PerceptualRamp.DeltaEOk(top.r, top.g, top.b, bottom.r, bottom.g, bottom.b) < 0.01);
        Assert.Equal(unchecked((int)0xFF000000), Wheel.Map(400f, 0f, 400));   // in-set stays black
    }

    // Read the rendered colours back into OkLab: the red↔green axis a stays at zero
    // (8-bit rounding only) while L and b sweep.
    [Fact]
    public void NoRedGreenAxis()
    {
        float minL = 1, maxL = 0, minB = 1, maxB = -1;
        for (int k = 0; k < 64; k++)
        {
            var (r, g, b) = Rgb(Wheel.Map(k * 400f / 64, 0f, 400));
            var (L, a, bb) = PerceptualRamp.RgbToOkLab(r, g, b);
            Assert.True(Math.Abs(a) < 0.01f, $"turn {k}/64: OkLab a = {a}");
            minL = Math.Min(minL, L); maxL = Math.Max(maxL, L); minB = Math.Min(minB, bb); maxB = Math.Max(maxB, bb);
        }
        Assert.True(maxL - minL > 0.3f && maxB - minB > 0.2f, "the wheel should sweep lightness and blue-yellow");
    }

    // Machado 2009 simulation: colours a quarter-turn or more apart stay clearly
    // different (ΔE_OK ≥ 0.15) for deutan and protan viewers, and lose almost
    // nothing against normal vision (≥ 90 %); neighbours a twelfth of a turn apart
    // stay distinct. Tritan (the blue-yellow deficiency this wheel does use) still
    // keeps ≥ 0.1.
    [Theory]
    [InlineData(null, 0.15)]
    [InlineData(CvdType.Deutan, 0.15)]
    [InlineData(CvdType.Protan, 0.15)]
    [InlineData(CvdType.Tritan, 0.10)]
    public void StaysDistinguishable_UnderColourVisionDeficiency(CvdType? type, double minFar)
    {
        const int n = 12;
        var cols = Enumerable.Range(0, n).Select(k => Rgb(Wheel.Map(k * 400f / n, 0f, 400))).ToArray();
        (double adj, double far) Measure(CvdType? t)
        {
            var sim = cols.Select(c => t is { } tt ? CvdSimulation.Simulate(c.r, c.g, c.b, tt) : c).ToArray();
            double adj = double.MaxValue, far = double.MaxValue;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    int sep = Math.Min(j - i, n - (j - i));
                    double de = PerceptualRamp.DeltaEOk(sim[i].r, sim[i].g, sim[i].b, sim[j].r, sim[j].g, sim[j].b);
                    if (sep == 1) adj = Math.Min(adj, de);
                    if (sep >= n / 4) far = Math.Min(far, de);
                }
            return (adj, far);
        }
        var (adjT, farT) = Measure(type);
        Assert.True(farT >= minFar, $"{type}: min ΔE between colours ≥ ¼ turn apart = {farT:F3}");
        Assert.True(adjT >= 0.04, $"{type}: min ΔE between neighbours = {adjT:F3}");
        if (type is CvdType.Deutan or CvdType.Protan)
            Assert.True(farT >= 0.9 * Measure(null).far, $"{type} loses too much against normal vision");
    }

    [Fact]
    public void IsABuiltInTheme_AndPaintsTheSlitCascadePhaseTheSameAtAnyIterationCount()
    {
        Assert.Contains(ColorPalette.BuiltIns, m => m is PhaseWheelBlueAmberMap);
        Assert.IsType<PhaseWheelBlueAmberMap>(ColorPalette.GetPaletteByName("Phase Wheel (Blue-Amber)"));
        uint[] Render(int maxIt)
        {
            var calc = new SlitCascadeCalculator(48, 32)
            {
                Zoom = 1.0, MaxIterations = maxIt, ColorMap = new PhaseWheelBlueAmberMap(),
                FractalParameters = new FractalParameters { SlitCascadeView = SlitCascadeView.Phase, SlitCascadeChildren = 5, SlitCascadeDepth = 3, SlitCascadeCharge = 1 },
            };
            calc.Calculate(CancellationToken.None);
            return (uint[])calc.ColorBuffer.Clone();
        }
        var a = Render(16); var b = Render(400);
        Assert.True(a.Zip(b).Count(p => p.First == p.Second) >= a.Length * 99 / 100);
        Assert.True(a.Distinct().Count() > 20, "the vortex phase should sweep the wheel");
    }

    // A phase just below 1 (≡ 0) must not round up to MaxIterations: themes read
    // smooth ≥ MaxIterations as "in the set" and paint it black, which showed as
    // dotted black lines along the phase-0 contours once a theme honoured it.
    [Fact]
    public void PhaseViews_NeverReachMaxIterations()
    {
        const int maxIt = 400;
        var slit = new SlitCascadeCalculator(240, 135)
        {
            Zoom = 1.0, MaxIterations = maxIt, ColorMap = new PhaseWheelBlueAmberMap(),
            FractalParameters = new FractalParameters { SlitCascadeView = SlitCascadeView.Phase, SlitCascadeChildren = 5, SlitCascadeDepth = 3, SlitCascadeCharge = 1, SlitCascadeWavelength = 1e6, SlitCascadeWidth = 0 },
        };
        slit.Calculate(CancellationToken.None);
        Assert.DoesNotContain(slit.SmoothBuffer, v => v >= maxIt);

        var talbot = new TalbotCarpetCalculator(480, 270)   // the grid of the batch render where the dotted rows showed
        {
            CenterY = 0.5, Zoom = 1.9, MaxIterations = maxIt, ColorMap = new PhaseWheelBlueAmberMap(),
            FractalParameters = new FractalParameters { TalbotView = TalbotView.Phase },
        };
        talbot.Calculate(CancellationToken.None);
        Assert.DoesNotContain(talbot.SmoothBuffer, v => v >= maxIt);

        var dual = new DualOrbitEscapeCalculator(480, 270)
        {
            CenterX = -0.6, CenterY = 0, Zoom = 0.6, MaxIterations = maxIt, ColorMap = new PhaseWheelBlueAmberMap(),
            FractalParameters = new FractalParameters { DualOrbitField = DualOrbitField.PathInterferencePhase, DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.0, DualOrbitInterferenceK = 2, DualOrbitAngularMomentum = 3 },
        };
        dual.Calculate();
        Assert.DoesNotContain(dual.ColorScalarBuffer, v => v >= maxIt);
    }
}
