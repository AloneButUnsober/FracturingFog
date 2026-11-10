// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1602 — N-path interference. Oracles: the two-path closed forms (#1126 / #1567 /
// #1568) for N = 2; the N-slit grating function sin²(Nφ/2)/(N² sin²(φ/2)) of the
// two-path phase φ read back from an N = 2 render (Feynman Lectures Vol. I §30-1);
// a direct complex sum over orbits iterated here (own escape loop and Green
// function) for the Ray / Segment layouts; and the measured fringe width on a
// cross-section, which must fall roughly as 1/N.
public sealed class NPathInterferenceTests
{
    private const int W = 96, H = 64, MaxIter = 400;
    private const double Cx = -0.6, Cy = 0.05, Zoom = 1.0, SeedX = 0.3, SeedY = 0.2;

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
            DualOrbitCSeedX = SeedX, DualOrbitCSeedY = SeedY, DualOrbitInterferenceK = 20.0,
        };
        tweak?.Invoke(p);
        return p;
    }

    private static DualOrbitEscapeCalculator Calc(FractalParameters p, int w = W, int h = H)
    {
        var c = new DualOrbitEscapeCalculator(w, h)
        {
            CenterX = Cx, CenterY = Cy, Zoom = Zoom, MaxIterations = MaxIter,
            FractalParameters = p, ColorMap = new RampMap(),
        };
        c.Calculate();
        return c;
    }

    [Theory]
    [InlineData(DualOrbitField.PathInterference)]
    [InlineData(DualOrbitField.PathInterferencePhase)]
    [InlineData(DualOrbitField.PathInterferenceDomain)]
    public void TwoPaths_AreByteIdentical_WhateverTheLayout(DualOrbitField field)
    {
        var plain = Calc(P(p => { p.DualOrbitField = field; p.DualOrbitInterferenceGamma = 0.5; p.DualOrbitAngularMomentum = 2; }));
        foreach (var layout in Enum.GetValues<DualOrbitPathLayout>())
        {
            var r = Calc(P(p =>
            {
                p.DualOrbitField = field; p.DualOrbitInterferenceGamma = 0.5; p.DualOrbitAngularMomentum = 2;
                p.DualOrbitInterferencePaths = 2; p.DualOrbitPathLayout = layout;
            }));
            Assert.Equal(plain.ColorBuffer, r.ColorBuffer);
            Assert.Equal(plain.SmoothBuffer, r.SmoothBuffer);
        }
    }

    [Fact]
    public void PathsIntensity_ForTwoPaths_IsTheClosedForm()
    {
        var rng = new Random(1602);
        for (int t = 0; t < 400; t++)
        {
            double gz = rng.NextDouble() * 3, gc = rng.NextDouble() * 3, tz = rng.NextDouble(), tc = rng.NextDouble();
            double kappa = rng.NextDouble() * 40 - 10, gamma = rng.NextDouble() * 2, eta = rng.NextDouble();
            int ell = rng.Next(-4, 5);
            var mode = rng.Next(2) == 0 ? DualOrbitWindingMode.Same : DualOrbitWindingMode.Opposite;
            var (wz, wc) = DualOrbitEscapeCalculator.WindingPhases(ell, mode, tz, tc);
            double w = (tc - tz + 0.5) - Math.Floor(tc - tz + 0.5) - 0.5;
            double i = DualOrbitEscapeCalculator.PathsIntensity(new[] { kappa * gz + wz, kappa * gc + wc }, new[] { 0.0, w },
                gamma * kappa, eta, out double turns);
            Assert.Equal(DualOrbitEscapeCalculator.InterferenceIntensity(gc - gz, tc - tz, kappa, gamma, eta, wc - wz), i, 9);
            double ph = DualOrbitEscapeCalculator.InterferencePhase(gz, gc, tc - tz, kappa, gamma, wz, wc);
            double d = Math.Abs(ph - turns); d = Math.Min(d, 1 - d);
            Assert.True(d < 1e-9, $"phase {turns} vs {ph}");
        }
    }

    [Fact]
    public void PathsIntensity_IsTheDirectSum_WithBlockedSlitsLeftOut()
    {
        double[] phi = { 0.3, 2.1, double.NaN, -1.4, 0.9 };
        double[] off = { 0.0, 0.2, 0.1, -0.3, 0.05 };
        const double gk = 0.7, eta = 0.35;
        var open = Enumerable.Range(0, 5).Where(k => !double.IsNaN(phi[k])).ToArray();
        double mean = open.Average(k => off[k]);
        Complex sum = 0; double norm = 0;
        foreach (int k in open)
        {
            double a = Math.Exp(-2 * Math.PI * gk * (off[k] - mean));
            sum += Complex.FromPolarCoordinates(a, phi[k]); norm += a * a;
        }
        double coh = sum.Magnitude * sum.Magnitude / norm;
        double i = DualOrbitEscapeCalculator.PathsIntensity(phi, off, gk, eta, out double turns);
        Assert.Equal((1 - eta) * coh + eta, i, 12);
        double want = sum.Phase / (2 * Math.PI); want -= Math.Floor(want);
        Assert.Equal(want, turns, 12);
        Assert.True(double.IsNaN(DualOrbitEscapeCalculator.PathsIntensity(new[] { double.NaN }, new[] { 0.0 }, 1, 0, out _)));
    }

    private static double GratingFunction(double cosPhi, int n)
    {
        double phi = Math.Acos(Math.Clamp(cosPhi, -1, 1));
        Complex s = 0;
        for (int k = 0; k < n; k++) s += Complex.FromPolarCoordinates(1, k * phi);
        return s.Magnitude * s.Magnitude / (n * (double)n);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void Grating_IsTheNSlitFunction_OfTheTwoPathPhase(int n)
    {
        var two = Calc(P()).SmoothBuffer;
        var many = Calc(P(p => { p.DualOrbitInterferencePaths = n; p.DualOrbitPathLayout = DualOrbitPathLayout.Grating; })).SmoothBuffer;
        int live = 0;
        for (int i = 0; i < two.Length; i++)
        {
            if (two[i] <= 0) { Assert.Equal(0f, many[i]); continue; }   // same live set
            live++;
            double cosPhi = 2.0 * two[i] / MaxIter - 1.0;   // I/2 = (1 + cos φ)/2
            Assert.True(Math.Abs(many[i] / MaxIter - GratingFunction(cosPhi, n)) < 2e-3, $"px {i}");
        }
        Assert.True(live > two.Length / 2);
    }

    // ---- an independent orbit loop for the real layouts -------------------------

    private static double OwnGreen(double ux, double uy, double sx, double sy, double bailout)
    {
        double x = ux, y = uy, logR = Math.Log(bailout);
        for (int n = 0; n < MaxIter; n++)
        {
            double r2 = x * x + y * y;
            if (r2 > bailout * bailout)
            {
                double sm = n - Math.Log(0.5 * Math.Log(r2) / logR) / Math.Log(2.0);
                return 2 * logR * Math.Pow(2, -sm);
            }
            (x, y) = (x * x - y * y + sx, 2 * x * y + sy);
        }
        return double.NaN;
    }

    [Theory]
    [InlineData(DualOrbitPathLayout.Ray, 4)]
    [InlineData(DualOrbitPathLayout.Ray, 7)]
    [InlineData(DualOrbitPathLayout.Segment, 4)]
    [InlineData(DualOrbitPathLayout.Segment, 6)]
    public void RealLayouts_AreTheDirectSumOverOwnOrbits(DualOrbitPathLayout layout, int n)
    {
        const double kappa = 20.0;
        var r = Calc(P(p => { p.DualOrbitInterferencePaths = n; p.DualOrbitPathLayout = layout; })).SmoothBuffer;
        double pitch = 4.0 / W / Zoom, bail = new FractalParameters().DualOrbitBailout;
        var seeds = new List<double> { 0.0, 1.0 };
        for (int k = 2; k < n; k++) seeds.Add(layout == DualOrbitPathLayout.Ray ? k : (k - 1.0) / (n - 1));
        int checkedPx = 0, blockedPx = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double sx = Cx + (x - W * 0.5) * pitch, sy = Cy + (y - H * 0.5) * pitch;
                var g = seeds.Select(t => OwnGreen(t * SeedX, t * SeedY, sx, sy, bail)).ToArray();
                float v = r[y * W + x];
                if (double.IsNaN(g[0]) || double.IsNaN(g[1])) { Assert.Equal(0f, v); continue; }
                Complex sum = 0; int open = 0;
                foreach (double gk in g) if (!double.IsNaN(gk)) { sum += Complex.FromPolarCoordinates(1, kappa * gk); open++; }
                if (open < n) blockedPx++;
                double want = sum.Magnitude * sum.Magnitude / open / n;
                Assert.True(Math.Abs(v / MaxIter - want) < 2e-3, $"({x},{y}): {v / MaxIter} vs {want}");
                checkedPx++;
            }
        Assert.True(checkedPx > W * H / 2);
    }

    private static double Wrap(double t) => t + 0.5 - Math.Floor(t + 0.5) - 0.5;

    // γ (angle damping) and ℓ (winding) with N > 2, intensity and phase, against a sum
    // assembled here: Green functions from OwnGreen, level-1 angles from the public S6
    // ExternalAngleTurns per seed.
    [Theory]
    [InlineData(DualOrbitPathLayout.Grating, 5, DualOrbitWindingMode.Same)]
    [InlineData(DualOrbitPathLayout.Grating, 4, DualOrbitWindingMode.Opposite)]
    [InlineData(DualOrbitPathLayout.Ray, 4, DualOrbitWindingMode.Same)]
    [InlineData(DualOrbitPathLayout.Ray, 5, DualOrbitWindingMode.Opposite)]
    [InlineData(DualOrbitPathLayout.Segment, 5, DualOrbitWindingMode.Opposite)]
    public void DampingAndWinding_AreTheDirectSum(DualOrbitPathLayout layout, int n, DualOrbitWindingMode mode)
    {
        const double kappa = 6.0, gamma = 0.3; const int ell = 2;
        void Set(FractalParameters p)
        {
            p.DualOrbitInterferenceK = kappa; p.DualOrbitInterferenceGamma = gamma;
            p.DualOrbitAngularMomentum = ell; p.DualOrbitWindingMode = mode;
            p.DualOrbitInterferencePaths = n; p.DualOrbitPathLayout = layout;
        }
        var inten = Calc(P(Set)).SmoothBuffer;
        var phase = Calc(P(p => { Set(p); p.DualOrbitField = DualOrbitField.PathInterferencePhase; })).SmoothBuffer;
        double pitch = 4.0 / W / Zoom, bail = new FractalParameters().DualOrbitBailout;
        int checkedPx = 0;
        for (int y = 0; y < H; y += 3)
            for (int x = 0; x < W; x += 3)
            {
                double sx = Cx + (x - W * 0.5) * pitch, sy = Cy + (y - H * 0.5) * pitch;
                double Th(double t) => DualOrbitEscapeCalculator.ExternalAngleTurns(t * SeedX, t * SeedY, sx, sy, MaxIter, bail);
                double gz = OwnGreen(0, 0, sx, sy, bail), gc = OwnGreen(SeedX, SeedY, sx, sy, bail);
                double tz = Th(0), tc = Th(1);
                int i = y * W + x;
                if (double.IsNaN(gz) || double.IsNaN(gc)) { Assert.Equal(0f, inten[i]); continue; }
                var g = new double[n]; var th = new double[n]; var phi = new double[n]; var off = new double[n];
                double sign = mode == DualOrbitWindingMode.Opposite ? -1 : 1;
                for (int k = 0; k < n; k++)
                {
                    if (layout == DualOrbitPathLayout.Grating)
                    {
                        double wz = 2 * Math.PI * ell * tz, wc = sign * 2 * Math.PI * ell * tc;
                        phi[k] = kappa * (gz + k * (gc - gz)) + wz + k * (wc - wz);
                        off[k] = k * Wrap(tc - tz);
                        continue;
                    }
                    double t = k == 0 ? 0 : k == 1 ? 1 : layout == DualOrbitPathLayout.Ray ? k : (k - 1.0) / (n - 1);
                    g[k] = OwnGreen(t * SeedX, t * SeedY, sx, sy, bail); th[k] = Th(t);
                    if (double.IsNaN(g[k])) { phi[k] = off[k] = double.NaN; continue; }
                    phi[k] = kappa * g[k] + (k == 0 ? 1 : sign) * 2 * Math.PI * ell * th[k];
                    off[k] = Wrap(th[k] - tz);
                }
                var open = Enumerable.Range(0, n).Where(k => !double.IsNaN(phi[k])).ToArray();
                double mean = open.Average(k => off[k]);
                Complex sum = 0; double norm = 0;
                foreach (int k in open)
                {
                    double a = Math.Exp(-2 * Math.PI * gamma * kappa * (off[k] - mean));
                    sum += Complex.FromPolarCoordinates(a, phi[k]); norm += a * a;
                }
                double want = sum.Magnitude * sum.Magnitude / norm / n;
                Assert.True(Math.Abs(inten[i] / MaxIter - want) < 2e-3, $"I ({x},{y}): {inten[i] / MaxIter} vs {want}");
                double turns = sum.Phase / (2 * Math.PI); turns -= Math.Floor(turns);
                double d = Math.Abs(phase[i] / MaxIter - turns); d = Math.Min(d, 1 - d);
                Assert.True(d < 2e-3, $"phase ({x},{y}): {phase[i] / MaxIter} vs {turns}");
                checkedPx++;
            }
        Assert.True(checkedPx > 200);
    }

    [Fact]
    public void RealLayouts_DifferFromTheGrating_AndFromEachOther()
    {
        float[] Of(DualOrbitPathLayout l) => Calc(P(p => { p.DualOrbitInterferencePaths = 5; p.DualOrbitPathLayout = l; })).SmoothBuffer;
        var g = Of(DualOrbitPathLayout.Grating); var ray = Of(DualOrbitPathLayout.Ray); var seg = Of(DualOrbitPathLayout.Segment);
        Assert.NotEqual(g, ray); Assert.NotEqual(g, seg); Assert.NotEqual(ray, seg);
    }

    [Fact]
    public void WhichPath_MixesTowardsTheIncoherentSum()
    {
        foreach (var layout in new[] { DualOrbitPathLayout.Grating, DualOrbitPathLayout.Ray })
        {
            var coh = Calc(P(p => { p.DualOrbitInterferencePaths = 6; p.DualOrbitPathLayout = layout; })).SmoothBuffer;
            var mix = Calc(P(p => { p.DualOrbitInterferencePaths = 6; p.DualOrbitPathLayout = layout; p.DualOrbitWhichPath = 0.4; })).SmoothBuffer;
            for (int i = 0; i < coh.Length; i++)
            {
                if (coh[i] <= 0) continue;
                double iCoh = 6.0 * coh[i] / MaxIter, iMix = 6.0 * mix[i] / MaxIter;
                if (iCoh < 1e-4) continue;   // clamped at LiveFloor
                Assert.True(Math.Abs(iMix - (1 + 0.6 * (iCoh - 1))) < 3e-3, $"{layout} px {i}");
            }
        }
    }

    // Fringe width on a cross-section, measured in phase: along one row, each pixel
    // step is weighted by how far the two-path phase φ moves across it (|Δφ|, φ read
    // from the N = 2 render as acos(2·I/2 − 1)); the bright share is the weight of the
    // steps whose N-path pixel has I/N ≥ ½. Over many fringes that share is the FWHM in
    // φ divided by 2π — independent of how unevenly φ runs along the row.
    internal static double BrightShare(float[] two, float[] many, int w, int row)
    {
        double total = 0, bright = 0;
        for (int x = 0; x + 1 < w; x++)
        {
            int i = row * w + x;
            if (two[i] <= 0 || two[i + 1] <= 0) continue;
            double a = Math.Acos(Math.Clamp(2.0 * two[i] / MaxIter - 1, -1, 1));
            double b = Math.Acos(Math.Clamp(2.0 * two[i + 1] / MaxIter - 1, -1, 1));
            double d = Math.Abs(b - a);
            total += d;
            if (many[i] / MaxIter >= 0.5) bright += d;
        }
        return bright / total;
    }

    [Fact]
    public void Grating_FringesNarrowAsOneOverN_OnACrossSection()
    {
        const int w = 4096, h = 4;
        float[] Row(int n) => Calc(P(p =>
        {
            p.DualOrbitInterferenceK = 200.0; p.DualOrbitInterferencePaths = n;
        }), w, h).SmoothBuffer;
        var two = Row(2);
        double f2 = BrightShare(two, two, w, h / 2), f4 = BrightShare(two, Row(4), w, h / 2), f8 = BrightShare(two, Row(8), w, h / 2);
        // Exact FWHM in φ of sin²(Nφ/2)/(N² sin²(φ/2)): π (N = 2), 1.4307 (4), 0.7005 (8),
        // i.e. shares 0.500, 0.228, 0.111 — N·FWHM within 11 % of constant. (Measured
        // 0.536 / 0.233 / 0.107: the acos fold at φ = 0, π under-counts |Δφ| a little.)
        Assert.True(Math.Abs(f2 - 0.500) < 0.05 && Math.Abs(f4 - 0.228) < 0.03 && Math.Abs(f8 - 0.111) < 0.02,
            $"bright shares {f2}, {f4}, {f8}");
    }

    [Fact]
    public void Kappa_RecoloursFromTheCache_ForEveryLayout()
    {
        foreach (var layout in Enum.GetValues<DualOrbitPathLayout>())
        {
            var p = P(q => { q.DualOrbitInterferencePaths = 5; q.DualOrbitPathLayout = layout; });
            var c = Calc(p);
            p.DualOrbitInterferenceK = 33.0; p.DualOrbitWhichPath = 0.2; p.DualOrbitAngularMomentum = 1;
            c.Calculate();
            Assert.True(c.LastCalculateReusedOrbits, layout.ToString());
            var fresh = Calc(p.Clone());
            Assert.Equal(fresh.ColorBuffer, c.ColorBuffer);
            Assert.Equal(fresh.SmoothBuffer, c.SmoothBuffer);
        }
    }

    // #1591 phase wrap, shared by both passes: a phase a hair below one turn must not
    // round up to maxIter (themes read that as "in the set"); the intensity peak may.
    [Fact]
    public void PhaseJustBelowOneTurn_WrapsToTheFloor()
    {
        float s = DualOrbitEscapeCalculator.InterferenceScalar(1.0 - 1e-12, MaxIter, phase: true);
        Assert.True(s > 0f && s < 1f, $"{s}");
        Assert.Equal((float)MaxIter, DualOrbitEscapeCalculator.InterferenceScalar(1.0, MaxIter, phase: false));
        Assert.Equal(0.25f * MaxIter, DualOrbitEscapeCalculator.InterferenceScalar(0.25, MaxIter, phase: true));
    }

    [Fact]
    public void DomainAndPhase_WorkWithManyPaths()
    {
        var ph = Calc(P(p => { p.DualOrbitField = DualOrbitField.PathInterferencePhase; p.DualOrbitInterferencePaths = 4; p.DualOrbitPathLayout = DualOrbitPathLayout.Ray; }));
        Assert.Contains(ph.SmoothBuffer, v => v > 0);
        Assert.All(ph.SmoothBuffer, v => Assert.True(v < MaxIter));   // phase wrap (#1591)
        var dom = Calc(P(p => { p.DualOrbitField = DualOrbitField.PathInterferenceDomain; p.DualOrbitInterferencePaths = 4;
            p.DualOrbitInterferenceContours = DomainContourMode.Both; }));
        var dom2 = Calc(P(p => { p.DualOrbitField = DualOrbitField.PathInterferenceDomain; p.DualOrbitInterferenceContours = DomainContourMode.Both; }));
        Assert.NotEqual(dom2.ColorBuffer, dom.ColorBuffer);
    }

    [Fact]
    public void Settings_RoundTripThroughTheCommandBuilder()
    {
        var p = new FractalParameters { DualOrbitInterferencePaths = 7, DualOrbitPathLayout = DualOrbitPathLayout.Segment };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.JulibrotPair, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.JulibrotPair, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.JulibrotPair, out _)!.ApplyTo(fresh);
        Assert.Equal(7, fresh.DualOrbitInterferencePaths);
        Assert.Equal(DualOrbitPathLayout.Segment, fresh.DualOrbitPathLayout);
        var clone = p.Clone();
        Assert.Equal(7, clone.DualOrbitInterferencePaths);
        Assert.Equal(DualOrbitPathLayout.Segment, clone.DualOrbitPathLayout);
    }
}
