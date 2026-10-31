// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1569 (double-slit epic #1565 S3) — the slit cascade. Oracles independent of
// the calculator's per-level product: the textbook two-slit pattern, a brute
// sum over every one of the N^D leaf slits, and Parseval (transmitted power).
// #1571 (S4): generalized dimensions against box counting of the rendered
// measure, monofractal equal shares, monotone D_q, α / f(α) of an address.
public sealed class SlitCascadeTests
{
    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF000000u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static FractalParameters P(Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters();
        tweak?.Invoke(p);
        return p;
    }

    private static double Sinc(double t) => t == 0 ? 1.0 : Math.Sin(t) / t;

    // Two slits of half-width r at ±(1 − r), equal weights: the classic
    // A ∝ cos(2π·b·u)·sinc(2π·r·u) (Fraunhofer two-slit, Feynman Vol. III ch. 1).
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.37, 0.0)]
    [InlineData(1.9, -0.4)]
    [InlineData(-5.3, 2.2)]
    public void DepthOne_TwoSlits_IsTheClassicPattern(double ux, double uy)
    {
        const double r = 0.2;
        var c = SlitCascadeCalculator.Build(P(q => { q.SlitCascadeChildren = 2; q.SlitCascadeDepth = 1; q.SlitCascadeRatio = r; q.SlitCascadeLayout = SlitCascadeLayout.Line; }));
        Assert.Equal(0.5, c.P[0], 12);   // symmetric pair: equal shares (design §4.1)
        Assert.Equal(0.5, c.P[1], 12);
        double b = 1 - r;
        double want = Math.Sqrt(2) * Math.Cos(2 * Math.PI * b * ux) * Sinc(2 * Math.PI * r * ux) * Sinc(2 * Math.PI * r * uy);
        var got = SlitCascadeCalculator.Amplitude(c, ux, uy);
        Assert.Equal(want, got.Real, 10);
        Assert.Equal(0.0, got.Imaginary, 10);
    }

    // The per-level product must equal the direct sum over every leaf slit:
    // leaf centre Σ rⁿ·b_{iₙ}, amplitude Π √p_{iₙ}, times the leaf transform.
    [Theory]
    [InlineData(SlitCascadeLayout.Ring, 5, 3, 0.25)]
    [InlineData(SlitCascadeLayout.Line, 4, 4, 0.2)]
    [InlineData(SlitCascadeLayout.Ring, 3, 5, 0.33)]
    public void Product_EqualsTheBruteForceLeafSum(SlitCascadeLayout layout, int n, int depth, double r)
    {
        var c = SlitCascadeCalculator.Build(P(q => { q.SlitCascadeLayout = layout; q.SlitCascadeChildren = n; q.SlitCascadeDepth = depth; q.SlitCascadeRatio = r; q.SlitCascadeWavelength = 0.45; }));
        var rng = new Random(1569);
        for (int k = 0; k < 12; k++)
        {
            double ux = rng.NextDouble() * 16 - 8, uy = rng.NextDouble() * 16 - 8;
            Complex sum = Complex.Zero;
            int leaves = (int)Math.Pow(n, depth);
            for (int leaf = 0; leaf < leaves; leaf++)
            {
                double cx = 0, cy = 0, amp = 1, scale = 1;
                int code = leaf;
                for (int level = 0; level < depth; level++)
                {
                    int i = code % n; code /= n;
                    cx += scale * c.Bx[i]; cy += scale * c.By[i];
                    amp *= Math.Sqrt(c.P[i]);
                    scale *= r;
                }
                sum += amp * Complex.Exp(-2 * Math.PI * Complex.ImaginaryOne * (cx * ux + cy * uy));
            }
            double a = Math.Pow(r, depth);
            sum *= Sinc(2 * Math.PI * a * ux) * Sinc(2 * Math.PI * a * uy);
            var got = SlitCascadeCalculator.Amplitude(c, ux, uy);
            Assert.Equal(sum.Real, got.Real, 9);
            Assert.Equal(sum.Imaginary, got.Imaginary, 9);
        }
    }

    // Parseval: the leaves don't overlap, so ∫|A|² du is the power through the
    // aperture, Σ_leaves Π pᵢ = 1, times ∫sinc²(2πau)du = 1/(2a) for a leaf of
    // half-width a. Line layout is separable, so the u_x integral alone gives
    // 1/(2a). Catches amplitudes that are not √pᵢ (e.g. pᵢ: Σ pᵢ² ≠ 1).
    [Fact]
    public void Parseval_TransmittedPowerIsConserved()
    {
        const double r = 0.2; const int depth = 2;
        var c = SlitCascadeCalculator.Build(P(q => { q.SlitCascadeLayout = SlitCascadeLayout.Line; q.SlitCascadeChildren = 3; q.SlitCascadeDepth = depth; q.SlitCascadeRatio = r; q.SlitCascadeWavelength = 1.2; }));
        Assert.True(c.P.Max() / c.P.Min() > 1.5, "weights should differ for this test to bite");
        double a = Math.Pow(r, depth);
        const double h = 0.004, U = 2500;
        double integral = 0;
        for (double u = -U; u <= U; u += h)
        {
            var amp = SlitCascadeCalculator.Amplitude(c, u, 0);
            integral += (amp.Real * amp.Real + amp.Imaginary * amp.Imaginary) * h;
        }
        Assert.Equal(1.0 / (2 * a), integral, 1.0 / (2 * a) * 0.01);
    }

    [Fact]
    public void ChildrenOnADarkFringe_DropOut()
    {
        // Line N = 3 at x = 0, ±0.8: cos(π·d·x/λ) = 0 at x = ±0.8 when λ = 1.6·d.
        var c = SlitCascadeCalculator.Build(P(q => { q.SlitCascadeLayout = SlitCascadeLayout.Line; q.SlitCascadeChildren = 3; q.SlitCascadeRatio = 0.2; q.SlitCascadeSeparation = 1.0; q.SlitCascadeWavelength = 1.6; q.SlitCascadeWidth = 0; }));
        Assert.Equal(0.0, c.P[0], 12);
        Assert.Equal(1.0, c.P[1], 12);
        Assert.Equal(0.0, c.P[2], 12);
        // Only the centre branch survives: the aperture is one square, so the far
        // field is the leaf's own sinc·sinc envelope.
        var amp = SlitCascadeCalculator.Amplitude(c, 3.1, -1.7);
        double a = Math.Pow(0.2, c.Depth);
        Assert.Equal(Sinc(2 * Math.PI * a * 3.1) * Sinc(2 * Math.PI * a * -1.7), amp.Real, 10);
    }

    [Fact]
    public void ApertureMass_FollowsTheAddress_AndIsZeroOutside()
    {
        var c = SlitCascadeCalculator.Build(P(q => { q.SlitCascadeLayout = SlitCascadeLayout.Ring; q.SlitCascadeChildren = 6; q.SlitCascadeDepth = 3; }));
        double pMax = c.P.Max();
        // The leaf reached by always taking child 2: centre Σ rⁿ·b₂.
        double x = 0, y = 0, s = 1;
        for (int level = 0; level < c.Depth; level++) { x += s * c.Bx[2]; y += s * c.By[2]; s *= c.Ratio; }
        Assert.Equal(Math.Pow(c.P[2] / pMax, c.Depth), SlitCascadeCalculator.ApertureMass(c, x, y), 12);
        Assert.Equal(0.0, SlitCascadeCalculator.ApertureMass(c, 0, 0));       // the ring's empty centre
        Assert.Equal(0.0, SlitCascadeCalculator.ApertureMass(c, 1.5, 1.5));   // outside the aperture
    }

    [Fact]
    public void Render_IsNormalisedToTheCentralMaximum()
    {
        const int w = 64, h = 48;
        var calc = new SlitCascadeCalculator(w, h) { CenterX = 0, CenterY = 0, Zoom = 0.25, MaxIterations = 400, ColorMap = new RampMap(), FractalParameters = P() };
        calc.Calculate(CancellationToken.None);
        // Pixel (W/2, H/2) is u = 0 exactly: I/I(0) = 1 → top of the scale.
        Assert.Equal(400f, calc.SmoothBuffer[(h / 2) * w + w / 2]);
        Assert.All(calc.SmoothBuffer, v => Assert.InRange(v, 0f, 400f));
        Assert.Contains(calc.SmoothBuffer, v => v > 0 && v < 400);
    }

    [Fact]
    public void Views_AllRender()
    {
        foreach (var view in Enum.GetValues<SlitCascadeView>())
        {
            bool aperture = view is SlitCascadeView.Aperture or SlitCascadeView.HolderAlpha or SlitCascadeView.Spectrum;
            var calc = new SlitCascadeCalculator(48, 32) { Zoom = aperture ? 1.6 : 0.25, ColorMap = new RampMap(), FractalParameters = P(q => q.SlitCascadeView = view) };
            calc.Calculate(CancellationToken.None);
            Assert.Contains(calc.SmoothBuffer, v => v > 0);
            Assert.All(calc.SmoothBuffer, v => Assert.False(float.IsNaN(v)));
        }
    }

    [Fact]
    public void BadParameters_AreClampedNotThrown()
    {
        var calc = new SlitCascadeCalculator(16, 16) { ColorMap = new RampMap(), FractalParameters = P(q => { q.SlitCascadeChildren = 999; q.SlitCascadeDepth = -3; q.SlitCascadeRatio = double.NaN; q.SlitCascadeWavelength = 0; q.SlitCascadeDynamicRange = double.PositiveInfinity; }) };
        calc.Calculate(CancellationToken.None);
        Assert.All(calc.SmoothBuffer, v => Assert.False(float.IsNaN(v)));
    }

    [Fact]
    public void Wavelength_IsAnimatable()
    {
        var names = FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.SlitCascade).Select(d => d.ParamName).ToArray();
        Assert.Contains(nameof(FractalParameters.SlitCascadeWavelength), names);
        Assert.Contains(nameof(FractalParameters.SlitCascadeRatio), names);
    }

    // ── #1571 (double-slit S4) — probability sets the dimension ─────────────

    // Equal shares make a monofractal: every D_q is log N / log(1/r).
    [Theory]
    [InlineData(0.2)]
    [InlineData(0.35)]
    public void EqualShares_AreMonofractal(double r)
    {
        var c = SlitCascadeCalculator.Build(P(q => { q.SlitCascadeChildren = 2; q.SlitCascadeLayout = SlitCascadeLayout.Line; q.SlitCascadeRatio = r; }));
        double want = Math.Log(2) / Math.Log(1 / r);
        foreach (double q in new[] { -3.0, 0.0, 0.5, 1.0, 2.0, 7.0 })
            Assert.Equal(want, SlitCascadeCalculator.GeneralizedDimension(c, q), 10);
    }

    // D_q is non-increasing in q, strictly for unequal shares, and continuous at q = 1.
    [Fact]
    public void GeneralizedDimensions_DecreaseWithQ_AndAreContinuousAtOne()
    {
        var c = SlitCascadeCalculator.Build(P());
        Assert.True(c.P.Max() / c.P.Min() > 3, "default shares should be unequal");
        double prev = double.PositiveInfinity;
        for (double q = -4; q <= 8; q += 0.25)
        {
            double d = SlitCascadeCalculator.GeneralizedDimension(c, q);
            Assert.True(d < prev, $"D_{q} = {d} is not below D_{q - 0.25} = {prev}");
            prev = d;
        }
        double d1 = SlitCascadeCalculator.GeneralizedDimension(c, 1);
        Assert.Equal(d1, SlitCascadeCalculator.GeneralizedDimension(c, 1 + 1e-7), 5);
        Assert.Equal(d1, SlitCascadeCalculator.GeneralizedDimension(c, 1 - 1e-7), 5);
        Assert.Equal(Math.Log(6) / Math.Log(1 / 0.3), SlitCascadeCalculator.GeneralizedDimension(c, 0), 10);
    }

    // Independent oracle: box-count the rendered measure. Rasterise the aperture
    // mass, sum it into boxes of side ε, and fit the slopes of log N(ε),
    // −Σμ log μ and −log Σμ² against log(1/ε). No closed form involved.
    [Fact]
    public void BoxCounting_TheRenderedMeasure_MatchesTheClosedForm()
    {
        var c = SlitCascadeCalculator.Build(P(q => q.SlitCascadeDepth = 4));
        Assert.False(SlitCascadeCalculator.ChildrenOverlap(c));
        const int G = 1024;
        var mass = new double[G * G];
        double total = 0;
        for (int y = 0; y < G; y++)
            for (int x = 0; x < G; x++)
            {
                double m = SlitCascadeCalculator.ApertureMass(c, -1 + (x + 0.5) * 2.0 / G, -1 + (y + 0.5) * 2.0 / G);
                mass[y * G + x] = m; total += m;
            }
        var xs = new System.Collections.Generic.List<double>();
        var n0 = new System.Collections.Generic.List<double>();
        var h1 = new System.Collections.Generic.List<double>();
        var s2 = new System.Collections.Generic.List<double>();
        foreach (int boxes in new[] { 8, 16, 32, 64 })   // ε = 2/boxes, between the leaf (r⁴ ≈ 0.008) and r
        {
            int side = G / boxes;
            var mu = new double[boxes * boxes];
            for (int y = 0; y < G; y++)
                for (int x = 0; x < G; x++)
                    mu[(y / side) * boxes + x / side] += mass[y * G + x] / total;
            double count = 0, ent = 0, sq = 0;
            foreach (double m in mu) if (m > 0) { count++; ent -= m * Math.Log(m); sq += m * m; }
            xs.Add(Math.Log(boxes / 2.0)); n0.Add(Math.Log(count)); h1.Add(ent); s2.Add(-Math.Log(sq));
        }
        static double Slope(System.Collections.Generic.List<double> x, System.Collections.Generic.List<double> y)
        {
            double mx = x.Average(), my = y.Average(), num = 0, den = 0;
            for (int i = 0; i < x.Count; i++) { num += (x[i] - mx) * (y[i] - my); den += (x[i] - mx) * (x[i] - mx); }
            return num / den;
        }
        double d0 = SlitCascadeCalculator.GeneralizedDimension(c, 0), d1 = SlitCascadeCalculator.GeneralizedDimension(c, 1), d2 = SlitCascadeCalculator.GeneralizedDimension(c, 2);
        Assert.True(d0 - d2 > 0.3, "the test needs a clearly multifractal measure");
        Assert.Equal(d0, Slope(xs, n0), 0.1 * d0);
        Assert.Equal(d1, Slope(xs, h1), 0.1 * d1);
        Assert.Equal(d2, Slope(xs, s2), 0.1 * d2);
    }

    [Fact]
    public void DarkFringes_LowerTheSupportDimension()
    {
        // λ = 1.6·d puts both outer children of a 3-child line on dark fringes.
        var lit = SlitCascadeCalculator.Build(P(q => { q.SlitCascadeLayout = SlitCascadeLayout.Line; q.SlitCascadeChildren = 3; q.SlitCascadeRatio = 0.2; q.SlitCascadeWavelength = 1.2; }));
        var dark = SlitCascadeCalculator.Build(P(q => { q.SlitCascadeLayout = SlitCascadeLayout.Line; q.SlitCascadeChildren = 3; q.SlitCascadeRatio = 0.2; q.SlitCascadeWavelength = 1.6; q.SlitCascadeWidth = 0; }));
        Assert.Equal(Math.Log(3) / Math.Log(5), SlitCascadeCalculator.GeneralizedDimension(lit, 0), 10);
        Assert.Equal(0.0, SlitCascadeCalculator.GeneralizedDimension(dark, 0), 10);   // one surviving branch: a point
    }

    // α of an address is the mass exponent; f(α) counts the leaves sharing it.
    [Fact]
    public void AlphaAndSpectrum_OfAnAddress()
    {
        var c = SlitCascadeCalculator.Build(P(q => q.SlitCascadeDepth = 4));
        int n = c.P.Length;
        // Address 0,0,1,3: counts k = [2,1,0,1,0,0].
        var counts = new int[n]; counts[0] = 2; counts[1] = 1; counts[3] = 1;
        double wantAlpha = (2 * Math.Log(c.P[0]) + Math.Log(c.P[1]) + Math.Log(c.P[3])) / (4 * Math.Log(c.Ratio));
        Assert.Equal(wantAlpha, SlitCascadeCalculator.Alpha(c, counts), 12);
        // 4!/(2!·1!·1!) = 12 leaves share this composition.
        Assert.Equal(Math.Log(12) / (4 * Math.Log(1 / c.Ratio)), SlitCascadeCalculator.Spectrum(c, counts), 12);
        // A point in that leaf walks back to the same counts.
        double x = 0, y = 0, s = 1;
        foreach (int i in new[] { 0, 0, 1, 3 }) { x += s * c.Bx[i]; y += s * c.By[i]; s *= c.Ratio; }
        var walked = new int[n];
        Assert.True(SlitCascadeCalculator.Address(c, x, y, walked));
        Assert.Equal(counts, walked);
        // A pure address (one child every level) has f = 0: a single leaf.
        var pure = new int[n]; pure[2] = 4;
        Assert.Equal(0.0, SlitCascadeCalculator.Spectrum(c, pure), 12);
    }

    [Fact]
    public void StatusLabel_ShowsTheDimensions()
    {
        var calc = new SlitCascadeCalculator(32, 24) { ColorMap = new RampMap(), FractalParameters = P(q => q.SlitCascadeQ = 4) };
        calc.Calculate(CancellationToken.None);
        var c = SlitCascadeCalculator.Build(calc.FractalParameters);
        string d1 = SlitCascadeCalculator.GeneralizedDimension(c, 1).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("D1 " + d1, calc.LastPrecisionLabel);
        Assert.Contains("Dq(q=4)", calc.LastPrecisionLabel);
        Assert.DoesNotContain("overlap", calc.LastPrecisionLabel);
        var crowded = new SlitCascadeCalculator(8, 8) { ColorMap = new RampMap(), FractalParameters = P(q => q.SlitCascadeRatio = 0.45) };
        crowded.Calculate(CancellationToken.None);
        Assert.Contains("overlap", crowded.LastPrecisionLabel);
    }

    [Fact]
    public void AlphaAndSpectrumViews_StayInRange()
    {
        foreach (var view in new[] { SlitCascadeView.HolderAlpha, SlitCascadeView.Spectrum })
        {
            var calc = new SlitCascadeCalculator(96, 64) { Zoom = 1.6, ColorMap = new RampMap(), FractalParameters = P(q => q.SlitCascadeView = view) };
            calc.Calculate(CancellationToken.None);
            Assert.All(calc.SmoothBuffer, v => Assert.InRange(v, 0f, 400f));
            Assert.True(calc.SmoothBuffer.Where(v => v > 0).Distinct().Count() > 3, $"{view} should show several α classes");
        }
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            SlitCascadeChildren = 7, SlitCascadeRatio = 0.21, SlitCascadeDepth = 5, SlitCascadeSeparation = 1.4,
            SlitCascadeWidth = 0.15, SlitCascadeWavelength = 0.77, SlitCascadeLayout = SlitCascadeLayout.Line,
            SlitCascadeView = SlitCascadeView.Aperture, SlitCascadeDynamicRange = 9, SlitCascadeQ = -1.5,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.SlitCascade, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.SlitCascade, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.SlitCascade, out _)!.ApplyTo(fresh);
        Assert.Equal(7, fresh.SlitCascadeChildren);
        Assert.Equal(0.21, fresh.SlitCascadeRatio);
        Assert.Equal(5, fresh.SlitCascadeDepth);
        Assert.Equal(1.4, fresh.SlitCascadeSeparation);
        Assert.Equal(0.15, fresh.SlitCascadeWidth);
        Assert.Equal(0.77, fresh.SlitCascadeWavelength);
        Assert.Equal(SlitCascadeLayout.Line, fresh.SlitCascadeLayout);
        Assert.Equal(SlitCascadeView.Aperture, fresh.SlitCascadeView);
        Assert.Equal(9.0, fresh.SlitCascadeDynamicRange);
        Assert.Equal(-1.5, fresh.SlitCascadeQ);   // #1571
    }
}
