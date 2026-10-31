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
// #1567: which-path η against the density-matrix sum.
// #1568: winding ℓ against direct sums, the ℓ-fold phase winding round the set,
// and the conjugate mirror ℓ ↔ −ℓ.
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

    // ── #1567 (double-slit S1) — which-path measurement η ──────────────────
    // Oracle: the two-path density matrix with its off-diagonal scaled by
    // (1 − η), summed directly from the reference amplitudes — not the closed
    // form the calculator uses.
    [Theory]
    [InlineData(20.0, 0.0, 0.3)]
    [InlineData(20.0, 1.0, 0.75)]
    [InlineData(9.0, 0.3, 0.5)]
    public void WhichPath_MatchesTheDensityMatrixSum(double kappa, double gamma, double eta)
    {
        var p = P(q => { q.DualOrbitInterferenceK = kappa; q.DualOrbitInterferenceGamma = gamma; q.DualOrbitWhichPath = eta; });
        var calc = Calc(p);
        var refs = Reference(p);
        int checkedPx = 0;
        for (int i = 0; i < refs.Length; i++)
        {
            var (gz, gc, dth, ok) = refs[i];
            if (!ok) continue;
            double w = dth - Math.Round(dth);
            Complex a = Amp(gz, 0, kappa, gamma), b = Amp(gc, w, kappa, gamma);
            double diag = a.Magnitude * a.Magnitude + b.Magnitude * b.Magnitude;
            double cross = 2.0 * (a * Complex.Conjugate(b)).Real;
            double want = (diag + (1.0 - eta) * cross) / diag;
            double got = 2.0 * calc.ColorScalarBuffer[i] / 400.0;
            Assert.Equal(Math.Max(want, 2.0 * 1e-6), got, 2e-4);
            checkedPx++;
        }
        Assert.True(checkedPx > W * H / 4, $"only {checkedPx} two-path pixels");
    }

    [Fact]
    public void WhichPathOne_RemovesEveryFringe()
    {
        var live = Calc(P(q => q.DualOrbitWhichPath = 1.0)).ColorScalarBuffer.Where(v => v > 0).Distinct().ToArray();
        Assert.Single(live);
        Assert.Equal(200f, live[0]);   // I = 1 → I/2 = 0.5 → maxIter/2
    }

    // Fringe visibility (I − 1) shrinks by exactly (1 − η) at every pixel.
    [Fact]
    public void WhichPath_ScalesFringeVisibilityLinearly()
    {
        var coherent = Calc(P()).ColorScalarBuffer;
        var mixed = Calc(P(q => q.DualOrbitWhichPath = 0.6)).ColorScalarBuffer;
        int n = 0;
        for (int i = 0; i < coherent.Length; i++)
        {
            if (coherent[i] <= 0 || mixed[i] <= 0) continue;
            double v0 = 2.0 * coherent[i] / 400.0 - 1.0, v1 = 2.0 * mixed[i] / 400.0 - 1.0;
            if (Math.Abs(v0) < 0.01) continue;   // LiveFloor clamps near I = 0
            Assert.Equal(0.4 * v0, v1, 1e-4);
            n++;
        }
        Assert.True(n > W * H / 4);
    }

    [Fact]
    public void WhichPath_RecoloursFromTheCache_AndLeavesPhaseAlone()
    {
        var p = P();
        var c = Calc(p);
        p.DualOrbitWhichPath = 0.5;
        c.Calculate();
        Assert.True(c.LastCalculateReusedOrbits);
        Assert.Equal(Calc(p.Clone()).ColorBuffer, c.ColorBuffer);

        // A partly-mixed state has no single phase: η does not touch the phase field.
        var phase0 = Calc(P(q => q.DualOrbitField = DualOrbitField.PathInterferencePhase)).ColorScalarBuffer;
        var phase1 = Calc(P(q => { q.DualOrbitField = DualOrbitField.PathInterferencePhase; q.DualOrbitWhichPath = 0.8; })).ColorScalarBuffer;
        Assert.Equal(phase0, phase1);
    }

    [Fact]
    public void WhichPath_NaNActsAsCoherent()
    {
        Assert.Equal(Calc(P()).ColorScalarBuffer, Calc(P(q => q.DualOrbitWhichPath = double.NaN)).ColorScalarBuffer);
    }

    [Fact]
    public void WhichPath_IsAnimatable()
    {
        var names = FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.JulibrotPair).Select(d => d.ParamName);
        Assert.Contains(nameof(FractalParameters.DualOrbitWhichPath), names);
    }

    // ── #1568 (double-slit S2) — angular-momentum winding ℓ ────────────────

    // Same winding: the cross term turns by 2πℓ·Δθ. Oracle = direct complex sum
    // with Δθ from the S6 domain pass (not the per-orbit angle cache).
    [Theory]
    [InlineData(20.0, 0.0, 0.0, 1)]
    [InlineData(9.0, 0.5, 0.3, 3)]
    [InlineData(1.0, 0.0, 0.0, -2)]
    public void AngularMomentum_Same_MatchesTheDirectSum(double kappa, double gamma, double eta, int ell)
    {
        var p = P(q => { q.DualOrbitInterferenceK = kappa; q.DualOrbitInterferenceGamma = gamma; q.DualOrbitWhichPath = eta; q.DualOrbitAngularMomentum = ell; });
        var calc = Calc(p);
        var refs = Reference(p);
        int checkedPx = 0;
        for (int i = 0; i < refs.Length; i++)
        {
            var (gz, gc, dth, ok) = refs[i];
            if (!ok) continue;
            double w = dth - Math.Round(dth);
            Complex a = Amp(gz, 0, kappa, gamma);
            Complex b = Amp(gc, w, kappa, gamma) * Complex.Exp(Complex.ImaginaryOne * 2 * Math.PI * ell * w);
            double diag = a.Magnitude * a.Magnitude + b.Magnitude * b.Magnitude;
            double want = (diag + (1.0 - eta) * 2.0 * (a * Complex.Conjugate(b)).Real) / diag;
            double got = 2.0 * calc.ColorScalarBuffer[i] / 400.0;
            Assert.Equal(Math.Max(want, 2.0 * 1e-6), got, 2e-4);
            checkedPx++;
        }
        Assert.True(checkedPx > W * H / 4, $"only {checkedPx} two-path pixels");
    }

    // Opposite winding and both phase forms need each path's own angle. Oracle:
    // re-run each pixel's two orbits through ExternalAngleTurns (double, per
    // pixel — not the float cache) and sum the amplitudes directly.
    [Theory]
    [InlineData(DualOrbitField.PathInterference, DualOrbitWindingMode.Opposite, 3)]
    [InlineData(DualOrbitField.PathInterference, DualOrbitWindingMode.Opposite, -1)]
    [InlineData(DualOrbitField.PathInterferencePhase, DualOrbitWindingMode.Same, 2)]
    [InlineData(DualOrbitField.PathInterferencePhase, DualOrbitWindingMode.Opposite, 3)]
    public void AngularMomentum_PerPathAngles_MatchTheDirectSum(DualOrbitField field, DualOrbitWindingMode mode, int ell)
    {
        const double kappa = 6, gamma = 0.25, cx = -0.6, cy = 0.05;
        var p = P(q => { q.DualOrbitField = field; q.DualOrbitInterferenceK = kappa; q.DualOrbitInterferenceGamma = gamma; q.DualOrbitAngularMomentum = ell; q.DualOrbitWindingMode = mode; });
        var calc = Calc(p);
        var refs = Reference(p);
        double pitch = 4.0 / W;
        double sgnC = mode == DualOrbitWindingMode.Opposite ? -1 : 1;
        int checkedPx = 0;
        for (int i = 0; i < refs.Length; i += 3)
        {
            var (gz, gc, _, ok) = refs[i];
            if (!ok) continue;
            double sx = cx + (i % W - W * 0.5) * pitch, sy = cy + (i / W - H * 0.5) * pitch;
            double tz = DualOrbitEscapeCalculator.ExternalAngleTurns(0, 0, sx, sy, 400, p.DualOrbitBailout);
            double tc = DualOrbitEscapeCalculator.ExternalAngleTurns(p.DualOrbitCSeedX, p.DualOrbitCSeedY, sx, sy, 400, p.DualOrbitBailout);
            if (double.IsNaN(tz) || double.IsNaN(tc)) continue;
            double w = (tc - tz) - Math.Round(tc - tz);
            Complex wz = Complex.Exp(Complex.ImaginaryOne * 2 * Math.PI * ell * tz);
            Complex wc = Complex.Exp(Complex.ImaginaryOne * 2 * Math.PI * ell * sgnC * tc);
            Complex a = Amp(gz, -w / 2, kappa, gamma) * wz, b = Amp(gc, w / 2, kappa, gamma) * wc;
            if (field == DualOrbitField.PathInterference)
            {
                double want = (a + b).Magnitude * (a + b).Magnitude / (a.Magnitude * a.Magnitude + b.Magnitude * b.Magnitude);
                Assert.Equal(Math.Max(want, 2.0 * 1e-6), 2.0 * calc.ColorScalarBuffer[i] / 400.0, 2e-4);
            }
            else
            {
                double want = (a + b).Phase / (2 * Math.PI); want -= Math.Floor(want);
                double got = calc.ColorScalarBuffer[i] / 400.0;
                double d = Math.Abs(want - got); d = Math.Min(d, 1 - d);
                Assert.True(d < 5e-4 || got <= 1e-5 || (a + b).Magnitude < 1e-3, $"pixel {i}: phase {got} vs {want}");
            }
            checkedPx++;
        }
        Assert.True(checkedPx > W * H / 16, $"only {checkedPx} pixels checked");
    }

    // Topological check: with Same winding the phase field winds exactly ℓ times
    // round a loop enclosing the set (the "|ℓ| arms"); Opposite cancels to 0.
    [Theory]
    [InlineData(DualOrbitWindingMode.Same, 1, 1)]
    [InlineData(DualOrbitWindingMode.Same, 3, 3)]
    [InlineData(DualOrbitWindingMode.Same, -2, -2)]
    [InlineData(DualOrbitWindingMode.Opposite, 3, 0)]
    [InlineData(DualOrbitWindingMode.Same, 0, 0)]
    public void PhaseField_WindsEllTimesRoundTheSet(DualOrbitWindingMode mode, int ell, int expected)
    {
        const int w = 160, h = 120;
        var c = new DualOrbitEscapeCalculator(w, h)
        {
            CenterX = -0.6, CenterY = 0, Zoom = 0.6, MaxIterations = 400, ColorMap = new RampMap(),
            FractalParameters = P(q => { q.DualOrbitField = DualOrbitField.PathInterferencePhase; q.DualOrbitInterferenceK = 1; q.DualOrbitAngularMomentum = ell; q.DualOrbitWindingMode = mode; }),
        };
        c.Calculate();
        var s = c.ColorScalarBuffer;
        var loop = new System.Collections.Generic.List<int>();
        for (int x = 2; x < w - 2; x++) loop.Add(2 * w + x);
        for (int y = 2; y < h - 2; y++) loop.Add(y * w + w - 3);
        for (int x = w - 3; x >= 2; x--) loop.Add((h - 3) * w + x);
        for (int y = h - 3; y >= 2; y--) loop.Add(y * w + 2);
        double total = 0;
        for (int k = 0; k < loop.Count; k++)
        {
            double d = (s[loop[(k + 1) % loop.Count]] - s[loop[k]]) / 400.0;
            d -= Math.Round(d);
            total += d;
        }
        Assert.Equal(expected, total, 0.05);
    }

    // Conjugation: f_{s̄}(ū) = conj f_s(u), so with a real c-seed every angle
    // flips sign (θ → −θ) and G is unchanged. The winding 2πℓθ is then the same
    // at (−ℓ, s̄) as at (ℓ, s), and κG is real, so at γ = 0 (equal amplitudes)
    // both the intensity and the phase are equal under the mirror. Rows y and
    // H − y are exact mirrors when CenterY = 0. Lift ties may flip a few pixels.
    [Theory]
    [InlineData(DualOrbitField.PathInterference, DualOrbitWindingMode.Same)]
    [InlineData(DualOrbitField.PathInterference, DualOrbitWindingMode.Opposite)]
    [InlineData(DualOrbitField.PathInterferencePhase, DualOrbitWindingMode.Same)]
    public void AngularMomentum_ConjugateMirror_FlipsTheSign(DualOrbitField field, DualOrbitWindingMode mode)
    {
        float[] Run(int ell)
        {
            var c = new DualOrbitEscapeCalculator(W, H)
            {
                CenterX = -0.6, CenterY = 0, Zoom = 1, MaxIterations = 400, ColorMap = new RampMap(),
                FractalParameters = P(q => { q.DualOrbitField = field; q.DualOrbitCSeedY = 0; q.DualOrbitInterferenceK = 7; q.DualOrbitAngularMomentum = ell; q.DualOrbitWindingMode = mode; }),
            };
            c.Calculate();
            return (float[])c.ColorScalarBuffer.Clone();
        }
        var plus = Run(3);
        var minus = Run(-3);
        bool phase = field == DualOrbitField.PathInterferencePhase;
        int both = 0, off = 0;
        for (int y = 1; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = plus[y * W + x], v = minus[(H - y) * W + x];
                if (u <= 0 || v <= 0) continue;
                both++;
                double d = (u - v) / 400.0;
                if (phase) d -= Math.Round(d);   // cyclic
                if (Math.Abs(d) > 1e-3) off++;
            }
        Assert.True(both > W * H / 4);
        Assert.True(off <= both / 50, $"{off} of {both} mirrored pixels disagree");
        Assert.NotEqual(plus, Run(0));   // ℓ actually did something
    }

    [Fact]
    public void AngularMomentumZero_IsBitIdentical_InEitherMode()
    {
        foreach (var f in new[] { DualOrbitField.PathInterference, DualOrbitField.PathInterferencePhase })
        {
            var baseline = Calc(P(q => q.DualOrbitField = f)).ColorBuffer;
            Assert.Equal(baseline, Calc(P(q => { q.DualOrbitField = f; q.DualOrbitWindingMode = DualOrbitWindingMode.Opposite; })).ColorBuffer);
        }
    }

    [Fact]
    public void Winding_IsSingleValuedAcrossAWholeTurn()
    {
        foreach (var mode in new[] { DualOrbitWindingMode.Same, DualOrbitWindingMode.Opposite })
            foreach (int ell in new[] { 1, 2, -5 })
            {
                var (z0, c0) = DualOrbitEscapeCalculator.WindingPhases(ell, mode, 0.13, 0.71);
                var (z1, c1) = DualOrbitEscapeCalculator.WindingPhases(ell, mode, 1.13, -0.29);
                Assert.Equal(Math.Cos(c0 - z0), Math.Cos(c1 - z1), 9);
                Assert.Equal(Math.Sin(z0), Math.Sin(z1), 9);
                Assert.Equal(Math.Sin(c0), Math.Sin(c1), 9);
            }
    }

    [Fact]
    public void AngularMomentum_RecoloursFromTheCache_AndIsAnimatable()
    {
        var p = P();
        var c = Calc(p);
        p.DualOrbitAngularMomentum = 2; p.DualOrbitWindingMode = DualOrbitWindingMode.Opposite;
        c.Calculate();
        Assert.True(c.LastCalculateReusedOrbits);
        Assert.Equal(Calc(p.Clone()).ColorBuffer, c.ColorBuffer);
        var names = FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.JulibrotPair).Select(d => d.ParamName);
        Assert.Contains(nameof(FractalParameters.DualOrbitAngularMomentum), names);
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.PathInterferencePhase,
            DualOrbitInterferenceK = 31.5, DualOrbitInterferenceGamma = 0.25,
            DualOrbitWhichPath = 0.35,
            DualOrbitAngularMomentum = -4, DualOrbitWindingMode = DualOrbitWindingMode.Opposite,
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
        Assert.Equal(0.35, fresh.DualOrbitWhichPath);   // #1567
        Assert.Equal(-4, fresh.DualOrbitAngularMomentum);   // #1568
        Assert.Equal(DualOrbitWindingMode.Opposite, fresh.DualOrbitWindingMode);
    }
}
