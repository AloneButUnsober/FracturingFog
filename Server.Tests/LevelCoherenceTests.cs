// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1600 — per-level which-path in the Slit Cascade. Oracles independent of the
// factorised product: a brute-force double sum over every pair of leaves, each pair's
// cross term scaled by (1 − ηₙ) at every level where the two addresses differ (the
// density-matrix rule of #1567 applied per level); the leaf envelope sinc² written out
// here; and the physical invariant that a fully marked level's child phases cannot be
// seen (its cross terms are gone).
public sealed class LevelCoherenceTests
{
    private static FractalParameters P(Action<FractalParameters>? f = null)
    {
        var p = new FractalParameters
        {
            SlitCascadeChildren = 5, SlitCascadeDepth = 3, SlitCascadeRatio = 0.3, SlitCascadeCharge = 1,
            SlitCascadeSeparation = 1.3, SlitCascadeWidth = 0.25, SlitCascadeWavelength = 0.7,
        };
        f?.Invoke(p);
        return p;
    }

    private static SlitCascadeCalculator Render(FractalParameters p, int w = 48, int h = 32)
    {
        var c = new SlitCascadeCalculator(w, h) { Zoom = 0.5, MaxIterations = 400, ColorMap = new HsvPalette(), FractalParameters = p };
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static double Sinc(double t) => t == 0.0 ? 1.0 : Math.Sin(t) / t;

    // Σ_{L,L'} A_L·conj(A_L')·Π_n g_n with g_n = 1 when both leaves took the same child at
    // level n, else 1 − ηₙ. A_L = Π_n √p·e^{iℓₙφ} · e^{−2πi u·x_L} · S(r^D u), x_L the
    // leaf centre Σ rⁿ·b_{iₙ}. O(N^{2D}).
    private static double BruteForce(in SlitCascadeCalculator.Cascade c, double[] eta, double ux, double uy)
    {
        int n = c.P.Length, d = c.Depth, leaves = (int)Math.Pow(n, d);
        var addr = new int[leaves, d];
        var amp = new Complex[leaves];
        double leafScale = Math.Pow(c.Ratio, d);
        double env = Sinc(2 * Math.PI * leafScale * ux) * Sinc(2 * Math.PI * leafScale * uy);
        for (int L = 0; L < leaves; L++)
        {
            int rest = L; double x = 0, y = 0, scale = 1; Complex a = 1;
            for (int lv = 0; lv < d; lv++)
            {
                int i = rest % n; rest /= n; addr[L, lv] = i;
                a *= Math.Sqrt(c.P[i]) * Complex.FromPolarCoordinates(1, c.Charges[lv] * c.Phi[i]);
                x += scale * c.Bx[i]; y += scale * c.By[i]; scale *= c.Ratio;
            }
            amp[L] = a * Complex.FromPolarCoordinates(env, -2 * Math.PI * (ux * x + uy * y));
        }
        double sum = 0;
        for (int L = 0; L < leaves; L++)
            for (int M = 0; M < leaves; M++)
            {
                double g = 1;
                for (int lv = 0; lv < d; lv++) if (addr[L, lv] != addr[M, lv]) g *= 1 - eta[lv];
                sum += g * (amp[L] * Complex.Conjugate(amp[M])).Real;
            }
        return sum;
    }

    [Theory]
    [InlineData(SlitCascadeLayout.Ring, 0.0, 0.0, 0.0)]
    [InlineData(SlitCascadeLayout.Ring, 1.0, 0.0, 0.5)]
    [InlineData(SlitCascadeLayout.Ring, 0.3, 0.8, 1.0)]
    [InlineData(SlitCascadeLayout.Line, 0.6, 0.25, 0.9)]
    [InlineData(SlitCascadeLayout.Line, 1.0, 1.0, 1.0)]
    public void MixedIntensity_MatchesTheBruteForceLeafPairSum(SlitCascadeLayout layout, double e0, double e1, double e2)
    {
        var c = SlitCascadeCalculator.Build(P(q => q.SlitCascadeLayout = layout));
        Assert.True(c.P.Distinct().Count() > 1, "unequal shares, so the weights matter");
        var eta = new[] { e0, e1, e2 };
        var rng = new Random(1600);
        for (int k = 0; k < 24; k++)
        {
            double ux = rng.NextDouble() * 4 - 2, uy = rng.NextDouble() * 4 - 2;
            double got = SlitCascadeCalculator.MixedIntensity(c, eta, ux, uy, out _);
            double want = BruteForce(c, eta, ux, uy);
            Assert.True(Math.Abs(got - want) <= 1e-9 * Math.Max(1.0, Math.Abs(want)), $"u=({ux:F3},{uy:F3}) got {got} want {want}");
        }
    }

    [Fact]
    public void NoWhichPath_IsTheCoherentAmplitude()
    {
        var c = SlitCascadeCalculator.Build(P());
        var rng = new Random(7);
        for (int k = 0; k < 16; k++)
        {
            double ux = rng.NextDouble() * 3 - 1.5, uy = rng.NextDouble() * 3 - 1.5;
            var a = SlitCascadeCalculator.Amplitude(c, ux, uy);
            double i = SlitCascadeCalculator.MixedIntensity(c, new double[3], ux, uy, out var coh);
            Assert.Equal(a.Real * a.Real + a.Imaginary * a.Imaginary, i, 12);
            Assert.Equal(a, coh);
        }
    }

    [Fact]
    public void FullWhichPathEverywhere_LeavesOnlyTheLeafEnvelope()
    {
        var p = P(q => q.SlitCascadeWhichPath = 1.0);
        var c = SlitCascadeCalculator.Build(p);
        var eta = SlitCascadeCalculator.LevelWhichPath(p, c.Depth)!;
        Assert.All(eta, e => Assert.Equal(1.0, e));
        Assert.Equal(0.0, SlitCascadeCalculator.LogPeak(c, eta), 12);   // peak = Σp per level = 1
        double s = Math.Pow(0.3, 3);
        foreach (var (ux, uy) in new[] { (0.0, 0.0), (0.7, -0.2), (5.0, 3.0), (-11.0, 0.4) })
        {
            double env = Sinc(2 * Math.PI * s * ux) * Sinc(2 * Math.PI * s * uy);
            Assert.Equal(env * env, SlitCascadeCalculator.MixedIntensity(c, eta, ux, uy, out _), 12);
        }
    }

    [Theory]
    [InlineData(SlitCascadeView.Intensity)]
    [InlineData(SlitCascadeView.Phase)]
    [InlineData(SlitCascadeView.Domain)]
    [InlineData(SlitCascadeView.Detections)]
    [InlineData(SlitCascadeView.Aperture)]
    public void UnmarkedLevels_AreByteIdentical(SlitCascadeView view)
    {
        var plain = Render(P(q => q.SlitCascadeView = view));
        foreach (var f in new Action<FractalParameters>[]
        {
            q => q.SlitCascadeWhichPath = 0.0,
            q => { q.SlitCascadeWhichPath = 0.0; q.SlitCascadeWhichPathPattern = SlitCascadeWhichPathPattern.Ramp; },
            q => { q.SlitCascadeWhichPath = 0.8; q.SlitCascadeWhichPathPattern = SlitCascadeWhichPathPattern.Chosen; q.SlitCascadeWhichPathMask = 0; },
            q => { q.SlitCascadeWhichPath = 0.8; q.SlitCascadeWhichPathPattern = SlitCascadeWhichPathPattern.DeepOnly; q.SlitCascadeWhichPathLevel = 3; },
            q => { q.SlitCascadeWhichPath = 0.8; q.SlitCascadeWhichPathPattern = SlitCascadeWhichPathPattern.ShallowOnly; q.SlitCascadeWhichPathLevel = 0; },
        })
        {
            var r = Render(P(q => { q.SlitCascadeView = view; f(q); }));
            Assert.Equal(plain.ColorBuffer, r.ColorBuffer);
            Assert.Equal(plain.SmoothBuffer, r.SmoothBuffer);
        }
        var marked = Render(P(q => { q.SlitCascadeView = view; q.SlitCascadeWhichPath = 0.8; }));
        if (view == SlitCascadeView.Aperture) Assert.Equal(plain.ColorBuffer, marked.ColorBuffer);   // the aperture has no far field
        else if (view == SlitCascadeView.Phase)
        {
            // Partly marked levels keep arg F (the coherent part's phase is unchanged);
            // only a fully marked level drops its phase.
            Assert.Equal(plain.ColorBuffer, marked.ColorBuffer);
            Assert.NotEqual(plain.ColorBuffer, Render(P(q => { q.SlitCascadeView = view; q.SlitCascadeWhichPath = 1.0; })).ColorBuffer);
        }
        else Assert.NotEqual(plain.ColorBuffer, marked.ColorBuffer);
    }

    [Fact]
    public void WhiteLight_UsesTheMixedIntensity()
    {
        var plain = Render(P(q => { q.SlitCascadeWhiteLight = WhiteLightMode.BlueAmber; q.WhiteLightSamples = 5; }));
        var marked = Render(P(q => { q.SlitCascadeWhiteLight = WhiteLightMode.BlueAmber; q.WhiteLightSamples = 5; q.SlitCascadeWhichPath = 1.0; }));
        Assert.NotEqual(plain.SmoothBuffer, marked.SmoothBuffer);
        // One wavelength in white light still reproduces the monochrome (mixed) view.
        var mono = Render(P(q => q.SlitCascadeWhichPath = 0.6));
        var one = Render(P(q => { q.SlitCascadeWhiteLight = WhiteLightMode.TrueSpectrum; q.WhiteLightSamples = 1; q.SlitCascadeWhichPath = 0.6; }));
        Assert.Equal(mono.SmoothBuffer, one.SmoothBuffer);
    }

    // A fully marked level adds its children as intensities, so its child phases (here the
    // vortex charges, which differ only at level 1 between Constant and Alternating)
    // cannot change the image. Unmarked, they do.
    [Theory]
    [InlineData(SlitCascadeView.Intensity)]
    [InlineData(SlitCascadeView.Phase)]
    [InlineData(SlitCascadeView.Domain)]
    public void FullyMarkedLevel_HidesItsChildPhases(SlitCascadeView view)
    {
        FractalParameters Q(SlitCascadeChargePattern pattern, double eta) => P(q =>
        {
            q.SlitCascadeView = view; q.SlitCascadeCharge = 2; q.SlitCascadeChargePattern = pattern;
            q.SlitCascadeWhichPath = eta; q.SlitCascadeWhichPathPattern = SlitCascadeWhichPathPattern.Chosen; q.SlitCascadeWhichPathMask = 0b010;
        });
        Assert.NotEqual(Render(Q(SlitCascadeChargePattern.Constant, 0.0)).ColorBuffer, Render(Q(SlitCascadeChargePattern.Alternating, 0.0)).ColorBuffer);
        Assert.NotEqual(Render(Q(SlitCascadeChargePattern.Constant, 0.5)).ColorBuffer, Render(Q(SlitCascadeChargePattern.Alternating, 0.5)).ColorBuffer);
        Assert.Equal(Render(Q(SlitCascadeChargePattern.Constant, 1.0)).ColorBuffer, Render(Q(SlitCascadeChargePattern.Alternating, 1.0)).ColorBuffer);
    }

    [Fact]
    public void Peak_IsTheBrightestValue()
    {
        // No charge: u = 0 is the maximum of every level factor, so the centre pixel
        // (u = 0 exactly) reaches the top of the scale whatever η is.
        foreach (double eta in new[] { 0.0, 0.35, 1.0 })
        {
            var r = Render(P(q => { q.SlitCascadeCharge = 0; q.SlitCascadeWhichPath = eta; q.SlitCascadeWhichPathPattern = SlitCascadeWhichPathPattern.Ramp; }), 48, 32);
            float centre = r.SmoothBuffer[16 * 48 + 24];
            Assert.True(Math.Abs(centre - 400f) < 1e-3f, $"η={eta}: centre {centre}");
            Assert.All(r.SmoothBuffer, v => Assert.True(v <= 400f + 1e-3f));
        }
    }

    [Theory]
    [InlineData(SlitCascadeWhichPathPattern.Constant, 1, 1, new[] { 0.6, 0.6, 0.6, 0.6 })]
    [InlineData(SlitCascadeWhichPathPattern.Ramp, 1, 1, new[] { 0.15, 0.3, 0.45, 0.6 })]
    [InlineData(SlitCascadeWhichPathPattern.DeepOnly, 2, 1, new[] { 0.0, 0.0, 0.6, 0.6 })]
    [InlineData(SlitCascadeWhichPathPattern.ShallowOnly, 1, 1, new[] { 0.6, 0.0, 0.0, 0.0 })]
    [InlineData(SlitCascadeWhichPathPattern.Chosen, 1, 0b1010, new[] { 0.0, 0.6, 0.0, 0.6 })]
    public void Patterns_PickTheLevels(SlitCascadeWhichPathPattern pattern, int cut, int mask, double[] expected)
    {
        var p = new FractalParameters { SlitCascadeWhichPath = 0.6, SlitCascadeWhichPathPattern = pattern, SlitCascadeWhichPathLevel = cut, SlitCascadeWhichPathMask = mask };
        var got = SlitCascadeCalculator.LevelWhichPath(p, 4)!;
        Assert.Equal(expected.Length, got.Length);
        for (int i = 0; i < got.Length; i++) Assert.Equal(expected[i], got[i], 12);
        Assert.Null(SlitCascadeCalculator.LevelWhichPath(new FractalParameters { SlitCascadeWhichPath = double.NaN }, 4));
        Assert.All(SlitCascadeCalculator.LevelWhichPath(new FractalParameters { SlitCascadeWhichPath = 7 }, 3)!, e => Assert.Equal(1.0, e));
    }

    [Fact]
    public void Settings_RoundTripThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            SlitCascadeWhichPath = 0.45, SlitCascadeWhichPathPattern = SlitCascadeWhichPathPattern.Chosen,
            SlitCascadeWhichPathLevel = 2, SlitCascadeWhichPathMask = 5,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.SlitCascade, Parameters = p, FamilyParams = RegionFractalParams.Snapshot(FractalType.SlitCascade, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.SlitCascade, out _)!.ApplyTo(fresh);
        Assert.Equal(0.45, fresh.SlitCascadeWhichPath);
        Assert.Equal(SlitCascadeWhichPathPattern.Chosen, fresh.SlitCascadeWhichPathPattern);
        Assert.Equal(2, fresh.SlitCascadeWhichPathLevel);
        Assert.Equal(5, fresh.SlitCascadeWhichPathMask);
        var clone = p.Clone();
        Assert.Equal(p.SlitCascadeWhichPathMask, clone.SlitCascadeWhichPathMask);
        Assert.Equal(p.SlitCascadeWhichPathPattern, clone.SlitCascadeWhichPathPattern);
    }
}
