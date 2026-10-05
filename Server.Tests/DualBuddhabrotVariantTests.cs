// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1125 (epic #1114 S11) — Dual Buddhabrot variants. Oracles: the classic
// AntiBuddhabrotCalculator (anti z channels), conservation (outcome Nebulabrot
// bands sum to the outcome channel), the c = 0 degeneracy (midpoint / chord ≡ Z),
// and geometry (midpoints / chords inside the radius-2 disc, escape points
// outside it).
public sealed class DualBuddhabrotVariantTests
{
    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF000000u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static FractalParameters Params(double cx = 0.5, double cy = 0.0, int samples = 30_000, int minIter = 0,
        Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters
        {
            BuddhaSamples = samples, BuddhaIterHigh = 300, BuddhaQualityMode = BuddhaQualityMode.Standard,
            BuddhaZoomCompensation = false, BuddhaSeed = 99,
            DualBuddhaCSeedX = cx, DualBuddhaCSeedY = cy, DualBuddhaMinIter = minIter,
        };
        tweak?.Invoke(p);
        return p;
    }

    private static T Render<T>(T calc, FractalParameters p, double zoom = 1.0) where T : BuddhaFamilyCalculator
    {
        calc.CenterX = -0.5; calc.CenterY = 0; calc.Zoom = zoom; calc.MaxIterations = 300;
        calc.FractalParameters = p; calc.ColorMap = new RampMap();
        calc.Calculate();
        return calc;
    }

    private static DualBuddhabrotCalculator Dual(FractalParameters p, int size = 96, double zoom = 1.0)
        => Render(new DualBuddhabrotCalculator(size, size), p, zoom);

    private static uint[] Sum(params uint[][] a) => Enumerable.Range(0, a[0].Length).Select(i => a.Aggregate(0u, (s, x) => s + x[i])).ToArray();

    [Theory]
    [InlineData(DualBuddhaDeposit.Midpoint)]
    [InlineData(DualBuddhaDeposit.PairChord)]
    public void ZeroSeed_MidpointAndChord_EqualZ(DualBuddhaDeposit d)
    {
        var dual = Dual(Params(0, 0, tweak: p => p.DualBuddhaDeposit = d));
        // Pair deposits start at step 1; Z keeps the classic z₀ = 0 hit, so the
        // origin's pixel is the one difference.
        int origin = 48 * 96 + (int)(0.5 / ((3.5 / 96)) + 48);
        var z = (uint[])dual.HitsR.Clone(); var ce = (uint[])dual.HitsB.Clone();
        z[origin] = ce[origin] = 0;
        Assert.Equal(z, ce);
        Assert.All(dual.HitsG, h => Assert.Equal(0u, h));
        Assert.True(dual.HitsR.Sum(h => (long)h) > 1000);
    }

    // Midpoints and chord points are convex combinations of two orbit points
    // inside |u| ≤ 2, so they stay in the disc; escape points lie outside it.
    // A wide view (zoom 0.25) shows everything.
    [Theory]
    [InlineData(DualBuddhaDeposit.Midpoint, true)]
    [InlineData(DualBuddhaDeposit.PairChord, true)]
    [InlineData(DualBuddhaDeposit.EscapeLocation, false)]
    public void DepositGeometry_IsInsideOrOutsideTheDisc(DualBuddhaDeposit d, bool inside)
    {
        const int n = 128; const double zoom = 0.25;
        var dual = Dual(Params(-0.3, 0.6, tweak: p => p.DualBuddhaDeposit = d), n, zoom);
        double scale = (3.5 / n) / zoom;
        var chans = d == DualBuddhaDeposit.EscapeLocation ? new[] { dual.HitsR, dual.HitsG, dual.HitsB } : new[] { dual.HitsG, dual.HitsB };
        long total = 0;
        foreach (var h in chans)
            for (int i = 0; i < h.Length; i++)
            {
                if (h[i] == 0) continue;
                total += h[i];
                // pixel (ix, iy) covers [(ix − n/2)·scale + midX, … + scale)
                double x0 = (i % n - n / 2.0) * scale - 0.5, y0 = (i / n - n / 2.0) * scale;
                double rNear = Math.Sqrt(Math.Pow(Math.Max(0, Math.Max(x0, -(x0 + scale))), 2) + Math.Pow(Math.Max(0, Math.Max(y0, -(y0 + scale))), 2));
                double rFar = Math.Sqrt(Math.Pow(Math.Max(Math.Abs(x0), Math.Abs(x0 + scale)), 2) + Math.Pow(Math.Max(Math.Abs(y0), Math.Abs(y0 + scale)), 2));
                if (inside) Assert.True(rNear <= 2.0, $"{d}: hit at pixel {i}, r ≥ {rNear}");
                else Assert.True(rFar >= 2.0, $"{d}: hit at pixel {i}, r ≤ {rFar}");
            }
        Assert.True(total > 1000, $"total {total}");
    }

    // Midpoint and chord use the same step count per sample (only t differs), so
    // with everything in view their c-channel totals agree; both ≤ the orbits'.
    [Fact]
    public void ChordAndMidpoint_DepositTheSameNumberOfPoints()
    {
        long Total(DualBuddhaDeposit d)
        {
            var c = Dual(Params(-0.3, 0.6, tweak: p => p.DualBuddhaDeposit = d), 64, 0.25);
            return c.HitsG.Sum(h => (long)h) + c.HitsB.Sum(h => (long)h);
        }
        long mid = Total(DualBuddhaDeposit.Midpoint), chord = Total(DualBuddhaDeposit.PairChord), orbits = Total(DualBuddhaDeposit.Orbits);
        Assert.Equal(mid, chord);
        Assert.True(mid <= orbits && mid > 0, $"mid {mid}, orbits {orbits}");
    }

    // Outcome Nebulabrot conserves hits: an outcome's three bands sum to its
    // channel in the default render; each band is populated.
    [Theory]
    [InlineData(DualBuddhaNebula.Z, 0)]
    [InlineData(DualBuddhaNebula.CB, 1)]
    [InlineData(DualBuddhaNebula.CE, 2)]
    public void OutcomeNebulabrot_BandsSumToTheOutcomeChannel(DualBuddhaNebula outcome, int channel)
    {
        var plain = Dual(Params());
        var neb = Dual(Params(tweak: p => { p.DualBuddhaNebula = outcome; p.BuddhaIterLow = 4; p.BuddhaIterMid = 12; }));
        var expect = new[] { plain.HitsR, plain.HitsG, plain.HitsB }[channel];
        Assert.Equal(expect, Sum(neb.HitsR, neb.HitsG, neb.HitsB));
        // At least two bands populated (slow CB escapers can leave the lowest empty).
        int populated = new[] { neb.HitsR, neb.HitsG, neb.HitsB }.Count(h => h.Any(v => v > 0));
        Assert.True(populated >= 2, $"bands populated: {populated}");
    }

    // Anti: the z-orbit channels (R: s ∈ M \ M_c, B: s ∈ M_c) together are the
    // classic Anti-Buddhabrot; c bounded ⇒ z bounded, so G and B have the same
    // sample count.
    [Fact]
    public void Anti_ZChannelsEqualTheClassicAntiBuddhabrot()
    {
        var p = Params(samples: 6_000, tweak: q => q.DualBuddhaAnti = true);
        var dual = Dual(p);
        var classic = new AntiBuddhabrotCalculator(96, 96);
        classic.CenterX = -0.5; classic.CenterY = 0; classic.Zoom = 1; classic.MaxIterations = 300;   // maxOrbit = IterHigh
        classic.FractalParameters = p.Clone(); classic.ColorMap = new RampMap();
        classic.Calculate();
        Assert.Equal(Sum(classic.HitsR, classic.HitsG, classic.HitsB), Sum(dual.HitsR, dual.HitsB));
        Assert.True(dual.HitsG.Sum(h => (long)h) > 0);
        // c bounded ⇒ z bounded (M_c ⊂ M), up to samples whose z escapes but whose
        // c-orbit has not escaped within the iteration cap — exactly OutcomeCounts[3].
        Assert.Equal(dual.OutcomeCounts[1] - dual.OutcomeCounts[2], dual.OutcomeCounts[3]);
    }

    [Fact]
    public void DefaultMode_IsUnchanged_ZStillTheClassicBuddhabrot()
    {
        var p = Params();
        var classic = Render(new BuddhabrotCalculator(96, 96), p.Clone());
        var dual = Dual(p);
        Assert.Equal(Sum(classic.HitsR, classic.HitsG, classic.HitsB), dual.HitsR);
    }

    [Fact]
    public void CSweepPreset_TargetsDualBuddhabrot_WithAnimatableParams()
    {
        var preset = AnimationLibrary.BuiltInPresets().Single(a => a.Name == "Dual Buddhabrot c-sweep");
        Assert.Contains(FractalType.DualBuddhabrot, preset.TargetFractalTypes);
        var names = FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.DualBuddhabrot).Select(d => d.ParamName).ToHashSet();
        Assert.All(preset.Tracks, t => Assert.Contains(t.ParamName, names));
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualBuddhaDeposit = DualBuddhaDeposit.PairChord, DualBuddhaNebula = DualBuddhaNebula.CB, DualBuddhaAnti = true,
            BuddhaIterLow = 7, BuddhaIterMid = 70,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.DualBuddhabrot, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.DualBuddhabrot, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.DualBuddhabrot, out _)!.ApplyTo(fresh);
        Assert.Equal(DualBuddhaDeposit.PairChord, fresh.DualBuddhaDeposit);
        Assert.Equal(DualBuddhaNebula.CB, fresh.DualBuddhaNebula);
        Assert.True(fresh.DualBuddhaAnti);
        Assert.Equal(7, fresh.BuddhaIterLow);
        Assert.Equal(70, fresh.BuddhaIterMid);
    }
}
