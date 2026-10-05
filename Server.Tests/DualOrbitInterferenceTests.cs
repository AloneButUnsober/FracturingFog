// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1126 (epic #1114 S12) — path interference. Oracles: a DIRECT complex sum
// |e^{iκΦ_z} + e^{iκΦ_c}|² / (|·|² + |·|²) built from the S6 Böttcher-domain
// outputs (ΔG, Δθ) and the EscapeTime fields' smooth counts (G per orbit) —
// independent of the closed form the calculator uses; κ = 0 ⇒ I = 2 (the
// normalised "4"); continuity across the Δθ wrap; bailout invariance (G and θ
// are intrinsic, S6); κ / γ colour-only (recolour from the cache).
public sealed class DualOrbitInterferenceTests
{
    private const int W = 64, H = 48;

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF102030u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static FractalParameters P(Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.PathInterference,
            DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2,
        };
        tweak?.Invoke(p);
        return p;
    }

    private static DualOrbitEscapeCalculator Calc(FractalParameters p, double cx = -0.6, double zoom = 1.0)
    {
        var c = new DualOrbitEscapeCalculator(W, H)
        {
            CenterX = cx, CenterY = 0.05, Zoom = zoom, MaxIterations = 400,
            FractalParameters = p, ColorMap = new RampMap(),
        };
        c.Calculate();
        return c;
    }

    // Per-pixel (G_z, G_c, Δθ) from independent paths: EscapeTimeZ / EscapeTimeC
    // smooth counts → G = 2 ln R · 2^(−n); Δθ from the S6 Böttcher domain pass.
    private static (double gz, double gc, double dth, bool ok)[] Reference(FractalParameters basis)
    {
        double twoLogR = 2.0 * Math.Log(basis.DualOrbitBailout);
        FractalParameters With(Action<FractalParameters> t) { var q = basis.Clone(); t(q); return q; }
        var ez = Calc(With(q => { q.DualOrbitField = DualOrbitField.EscapeTimeZ; })).SmoothBuffer;
        var ec = Calc(With(q => { q.DualOrbitField = DualOrbitField.EscapeTimeC; })).SmoothBuffer;
        var dom = Calc(With(q => q.DualOrbitColorMode = DualOrbitColorMode.BoettcherDomain));
        return Enumerable.Range(0, W * H).Select(i =>
        {
            bool ok = ez[i] > 0 && ec[i] > 0 && !float.IsNaN(dom.BoettcherAngleTurns[i]);
            return (twoLogR * Math.Pow(2, -ez[i]), twoLogR * Math.Pow(2, -ec[i]), (double)dom.BoettcherAngleTurns[i], ok);
        }).ToArray();
    }

    private static Complex Amp(double g, double theta, double kappa, double gamma)
        => Complex.Exp(Complex.ImaginaryOne * kappa * new Complex(g, 2 * Math.PI * gamma * theta));

    [Theory]
    [InlineData(20.0, 1.0)]
    [InlineData(7.5, 0.0)]
    [InlineData(35.0, 0.3)]
    public void Intensity_MatchesTheDirectTwoPathSum(double kappa, double gamma)
    {
        var p = P(q => { q.DualOrbitInterferenceK = kappa; q.DualOrbitInterferenceGamma = gamma; });
        var calc = Calc(p);
        var refs = Reference(p);
        int checkedPx = 0;
        for (int i = 0; i < refs.Length; i++)
        {
            var (gz, gc, dth, ok) = refs[i];
            if (!ok) continue;
            // Direct sum with θ_z = 0, θ_c = Δθ wrapped to [−½, ½): the normalised
            // intensity is invariant to a common amplitude / phase factor.
            double w = dth - Math.Round(dth);
            Complex a = Amp(gz, 0, kappa, gamma), b = Amp(gc, w, kappa, gamma);
            double want = (a + b).Magnitude * (a + b).Magnitude / (a.Magnitude * a.Magnitude + b.Magnitude * b.Magnitude);
            double got = 2.0 * calc.ColorScalarBuffer[i] / 400.0;
            Assert.Equal(Math.Max(want, 2.0 * 1e-6), got, 2e-4);   // float storage + LiveFloor
            checkedPx++;
        }
        Assert.True(checkedPx > W * H / 4, $"only {checkedPx} two-path pixels");
    }

    [Fact]
    public void Phase_MatchesTheDirectTwoPathSum()
    {
        const double kappa = 12, gamma = 0.5;
        var p = P(q => { q.DualOrbitField = DualOrbitField.PathInterferencePhase; q.DualOrbitInterferenceK = kappa; q.DualOrbitInterferenceGamma = gamma; });
        var calc = Calc(p);
        var refs = Reference(p);
        for (int i = 0; i < refs.Length; i++)
        {
            var (gz, gc, dth, ok) = refs[i];
            if (!ok) continue;
            double w = dth - Math.Round(dth);
            // Symmetric amplitudes: θ_z = −w/2, θ_c = +w/2.
            Complex s = Amp(gz, -w / 2, kappa, gamma) + Amp(gc, w / 2, kappa, gamma);
            double want = s.Phase / (2 * Math.PI); want -= Math.Floor(want);
            double got = calc.ColorScalarBuffer[i] / 400.0;
            double d = Math.Abs(want - got); d = Math.Min(d, 1 - d);
            Assert.True(d < 2e-4 || got <= 1e-5, $"pixel {i}: phase {got} vs {want}");
        }
    }

    [Fact]
    public void KappaZero_IsConstantTwo_WhereBothPathsExist()
    {
        var calc = Calc(P(q => q.DualOrbitInterferenceK = 0));
        var live = calc.ColorScalarBuffer.Where(v => v > 0).Distinct().ToArray();
        Assert.Single(live);
        Assert.Equal(400f, live[0]);   // I = 2 → I/2 = 1 → maxIter
    }

    [Fact]
    public void ClosedForm_IsContinuousAcrossTheAngleWrap()
    {
        foreach (var (k, g) in new[] { (20.0, 1.0), (3.0, 4.0), (50.0, 0.2) })
            Assert.Equal(DualOrbitEscapeCalculator.InterferenceIntensity(0.3, 0.5 - 1e-12, k, g),
                         DualOrbitEscapeCalculator.InterferenceIntensity(0.3, 0.5 + 1e-12, k, g), 9);
        // Periodic in Δθ (single-valued), unlike the raw e^{−2πκθ} amplitude.
        Assert.Equal(DualOrbitEscapeCalculator.InterferenceIntensity(0.7, 0.13, 9, 1),
                     DualOrbitEscapeCalculator.InterferenceIntensity(0.7, 1.13, 9, 1), 12);
    }

    // G (smooth count) and θ (backward lift) are bailout-independent (S6), so
    // the intensity is too — up to a few lift flips at branch cuts.
    [Fact]
    public void Intensity_IsBailoutInvariant()
    {
        var a = Calc(P(q => q.DualOrbitBailout = 128)).ColorScalarBuffer;
        var b = Calc(P(q => q.DualOrbitBailout = 1e4)).ColorScalarBuffer;
        int both = 0, off = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] <= 0 || b[i] <= 0) continue;
            both++;
            if (Math.Abs(a[i] - b[i]) > 0.01 * 400) off++;
        }
        Assert.True(both > W * H / 4);
        Assert.True(off <= both / 100, $"{off} of {both} pixels changed with the bailout");
    }

    [Fact]
    public void KappaAndGamma_RecolourFromTheCache()
    {
        var p = P();
        var c = Calc(p);
        p.DualOrbitInterferenceK = 33; p.DualOrbitInterferenceGamma = 0.4;
        c.Calculate();
        Assert.True(c.LastCalculateReusedOrbits);
        Assert.Equal(Calc(p.Clone()).ColorBuffer, c.ColorBuffer);
        p.DualOrbitInterferenceK = 5;
        c.Recolor();
        Assert.Equal(Calc(p.Clone()).ColorBuffer, c.ColorBuffer);
    }

    [Fact]
    public void SinglePath_IsInterior_AndQuaternionIsInterior()
    {
        var c = Calc(P());
        var ez = Calc(P(q => q.DualOrbitField = DualOrbitField.EscapeTimeZ)).SmoothBuffer;
        // s ∈ M (z bounded): one path only → no value.
        for (int i = 0; i < ez.Length; i++) if (ez[i] == 0) Assert.Equal(0f, c.ColorScalarBuffer[i]);
        var q = Calc(P(q => q.DualOrbitMap = DualOrbitMap.Quaternion));
        Assert.All(q.ColorBuffer, px => Assert.Equal(0xFF102030u, px));
    }

    // S9 twin: a split height on the interference field follows κ.
    [Fact]
    public void SplitHeight_FollowsKappa()
    {
        var p = P(q => { q.DualOrbitField = DualOrbitField.GreenRatio; q.DualOrbitSplitHeight = true; q.DualOrbitHeightField = DualOrbitField.PathInterference; });
        var c = Calc(p);
        p.DualOrbitInterferenceK = 9;
        c.Recolor();
        Assert.Equal(Calc(P(q => q.DualOrbitInterferenceK = 9)).SmoothBuffer, c.SmoothBuffer);
    }

    [Fact]
    public void Kappa_IsAnimatable()
    {
        var names = FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.JulibrotPair).Select(d => d.ParamName);
        Assert.Contains(nameof(FractalParameters.DualOrbitInterferenceK), names);
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.PathInterferencePhase,
            DualOrbitInterferenceK = 31.5, DualOrbitInterferenceGamma = 0.25,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.JulibrotPair, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.JulibrotPair, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.JulibrotPair, out _)!.ApplyTo(fresh);
        Assert.Equal(DualOrbitField.PathInterferencePhase, fresh.DualOrbitField);
        Assert.Equal(31.5, fresh.DualOrbitInterferenceK);
        Assert.Equal(0.25, fresh.DualOrbitInterferenceGamma);
    }
}
