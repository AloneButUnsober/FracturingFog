// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1115 (epic #1114 S1) — per-iteration pair accumulator. Oracles are
// INDEPENDENT: a plain System.Numerics.Complex re-iteration in the test, the
// pair identity D_N = c·Π(z_k + c_k), finite differences for the derivatives,
// the analytic period-2 multiplier, and a channels-off render for byte identity.
public sealed class DualOrbitPairAccumulatorTests
{
    private const double Bail = 128.0;

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF102030u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    // One sample at (s, c): a 2×2 SxSy frame whose pixel (1, 1) sits exactly on
    // the frame centre.
    private static DualOrbitEscapeCalculator Point(Complex s, Complex c, int maxIter,
        DualOrbitPairChannels ch = DualOrbitPairChannels.All, double eps = 1e-3)
    {
        var p = new FractalParameters
        {
            DualOrbitCSeedX = c.Real, DualOrbitCSeedY = c.Imaginary, DualOrbitBailout = Bail,
        };
        var calc = new DualOrbitEscapeCalculator(2, 2)
        {
            CenterX = s.Real, CenterY = s.Imaginary, Zoom = 1e9, MaxIterations = maxIter,
            FractalParameters = p, ColorMap = new RampMap(), PairChannels = ch, PairDivergenceEpsilon = eps,
        };
        calc.Calculate();
        return calc;
    }
    private const int Px = 3;

    // Reference: both orbits until EITHER passes the bailout (the accumulation
    // window), recording every state.
    private static (Complex[] z, Complex[] c, int n) Reference(Complex s, Complex c0, int maxIter)
    {
        var zs = new Complex[maxIter + 1]; var cs = new Complex[maxIter + 1];
        Complex z = Complex.Zero, c = c0;
        int n = 0;
        for (; n < maxIter; n++)
        {
            zs[n] = z; cs[n] = c;
            if (z.Magnitude > Bail || c.Magnitude > Bail) break;
            z = z * z + s; c = c * c + s;
        }
        zs[n] = z; cs[n] = c;
        return (zs, cs, n);
    }

    private static Complex Rand(Random r, double x0, double x1, double y0, double y1)
        => new(x0 + r.NextDouble() * (x1 - x0), y0 + r.NextDouble() * (y1 - y0));

    [Fact]
    public void Default_GathersNothing()
    {
        var calc = new DualOrbitEscapeCalculator(8, 8) { ColorMap = new RampMap() };
        calc.Calculate();
        Assert.Null(calc.PairPlanes);
    }

    // The lockstep path must cache exactly what the per-orbit path caches.
    [Theory]
    [InlineData(DualOrbitMap.ComplexPlane, DualOrbitColorMode.Field, DualOrbitField.EscapeSeparation, false, DualOrbitSliceAxes.SxSy)]
    [InlineData(DualOrbitMap.ComplexPlane, DualOrbitColorMode.Field, DualOrbitField.ExternalAngleDelta, false, DualOrbitSliceAxes.SxSy)]
    [InlineData(DualOrbitMap.ComplexPlane, DualOrbitColorMode.Field, DualOrbitField.GreenRatio, true, DualOrbitSliceAxes.SxSy)]
    [InlineData(DualOrbitMap.ComplexPlane, DualOrbitColorMode.Field, DualOrbitField.EscapeTimeC, false, DualOrbitSliceAxes.CxCy)]
    [InlineData(DualOrbitMap.ComplexPlane, DualOrbitColorMode.PerOrbitLayers, DualOrbitField.EscapeSeparation, false, DualOrbitSliceAxes.CxSx)]
    [InlineData(DualOrbitMap.Quaternion, DualOrbitColorMode.Field, DualOrbitField.DualOrbitAngle, false, DualOrbitSliceAxes.SxSy)]
    [InlineData(DualOrbitMap.Quaternion, DualOrbitColorMode.PerOrbitLayers, DualOrbitField.EscapeSeparation, true, DualOrbitSliceAxes.SxSy)]
    public void PairPath_RenderIsByteIdenticalToPlainPath(DualOrbitMap map, DualOrbitColorMode mode,
        DualOrbitField field, bool cEqualsS, DualOrbitSliceAxes axes)
    {
        DualOrbitEscapeCalculator Make(DualOrbitPairChannels ch)
        {
            var p = new FractalParameters
            {
                DualOrbitMap = map, DualOrbitColorMode = mode, DualOrbitField = field,
                DualOrbitCEqualsS = cEqualsS, DualOrbitSliceAxes = axes,
                DualOrbitThemeZ = "", DualOrbitThemeC = "", DualOrbitCSeedZ = 0.3, DualOrbitSZ = 0.2,
            };
            var c = new DualOrbitEscapeCalculator(64, 48)
            {
                CenterX = -0.5, CenterY = 0.1, Zoom = 1.3, MaxIterations = 300,
                FractalParameters = p, ColorMap = new RampMap(), PairChannels = ch,
            };
            c.Calculate();
            return c;
        }
        var plain = Make(DualOrbitPairChannels.None);
        var pair = Make(DualOrbitPairChannels.All);
        Assert.Null(plain.PairPlanes);
        Assert.NotNull(pair.PairPlanes);
        Assert.Equal(plain.ColorBuffer, pair.ColorBuffer);
        Assert.Equal(plain.SmoothBuffer, pair.SmoothBuffer);
    }

    // Σ log|σ_k| = log|D_N| − log|c| and Σ arg σ_k ≡ arg D_N − arg c (mod 2π),
    // from the product identity D_{n+1} = D_n (z_n + c_n) — checked against a
    // direct re-iteration of D_N.
    [Fact]
    public void SecantSumAndWinding_MatchTheSeparationIdentity()
    {
        var r = new Random(1115);
        int checkedPts = 0;
        for (int t = 0; t < 300; t++)
        {
            var s = Rand(r, -2.2, 0.8, -1.3, 1.3);
            var c = Rand(r, -1.5, 1.5, -1.5, 1.5);
            if (c.Magnitude < 1e-3) continue;
            const int maxIter = 200;
            var (zs, cs, n) = Reference(s, c, maxIter);
            var d = cs[n] - zs[n];
            // The direct D_N oracle is only exact while D stays above round-off of
            // the orbits themselves (the σ-sum keeps going past that — that is why
            // the accumulator uses it). Skip samples whose D ever reached ~ulp.
            bool resolved = true;
            for (int k = 0; k <= n && resolved; k++)
                resolved = (cs[k] - zs[k]).Magnitude > 1e-9 * (1 + zs[k].Magnitude + cs[k].Magnitude);
            if (!resolved) continue;
            var pl = Point(s, c, maxIter).PairPlanes!;
            Assert.Equal(n, pl.PairSteps[Px]);
            double expect = Math.Log(d.Magnitude) - Math.Log(c.Magnitude);
            Assert.True(Math.Abs(pl.SecantLogSum[Px] - expect) <= 1e-4 * Math.Max(1, Math.Abs(expect)),
                $"s={s} c={c}: sum {pl.SecantLogSum[Px]} vs {expect}");
            double turns = (d.Phase - c.Phase) / (2 * Math.PI) - pl.WindingTurns[Px];
            Assert.True(Math.Abs(turns - Math.Round(turns)) < 1e-4, $"s={s} c={c}: winding off by {turns}");
            checkedPts++;
        }
        Assert.True(checkedPts > 150, $"only {checkedPts} usable points");
    }

    [Fact]
    public void SeparationMidpointAndItinerary_MatchDirectIteration()
    {
        var r = new Random(7);
        const double eps = 0.05;
        for (int t = 0; t < 150; t++)
        {
            var s = Rand(r, -2.2, 0.8, -1.3, 1.3);
            var c = Rand(r, -1.5, 1.5, -1.5, 1.5);
            const int maxIter = 150;
            var (zs, cs, n) = Reference(s, c, maxIter);
            var pl = Point(s, c, maxIter, eps: eps).PairPlanes!;

            double min = double.PositiveInfinity; int minIdx = -1, div = -1, agree = 0; bool broken = false;
            for (int k = 0; k < n; k++)
            {
                double dk = (cs[k] - zs[k]).Magnitude;
                if (dk < min) { min = dk; minIdx = k; }
                if (div < 0 && dk > eps) div = k;
                if (k >= 1 && !broken)
                {
                    if ((zs[k].Imaginary >= 0) == (cs[k].Imaginary >= 0)) agree++; else broken = true;
                }
            }
            if (n > 0)
            {
                Assert.Equal(minIdx, pl.MinSeparationIndex[Px]);
                Assert.True(Math.Abs(Math.Log(Math.Max(min, 1e-300)) - pl.MinSeparationLog[Px]) < 1e-4, "min |D|");
            }
            Assert.Equal(div, pl.DivergenceIndex[Px]);
            Assert.Equal(agree, pl.ItineraryAgreement[Px]);

            var m = (zs[n] + cs[n]) / 2; var e = (zs[n] - cs[n]) / 2;
            double mp = e.Magnitude * e.Magnitude / (m.Magnitude * m.Magnitude);
            Assert.True(Math.Abs(pl.MidpointPerturbation[Px] - mp) <= 1e-5 * Math.Max(1, mp), $"midpoint {pl.MidpointPerturbation[Px]} vs {mp}");
        }
    }

    [Fact]
    public void DivergenceIndex_IsMonotoneInEpsilon()
    {
        var s = new Complex(-0.75, 0.12); var c = new Complex(0.3, 0.05);
        double prev = -1;
        foreach (double eps in new[] { 1e-6, 1e-3, 0.1, 0.5 })
        {
            double v = Point(s, c, 300, eps: eps).PairPlanes!.DivergenceIndex[Px];
            if (v >= 0) Assert.True(v >= prev, $"eps {eps}: {v} < {prev}");
            prev = Math.Max(prev, v);
        }
    }

    // Derivatives vs central finite differences of the escape state, at each
    // orbit's own escape index.
    [Fact]
    public void Derivatives_MatchFiniteDifferences()
    {
        var r = new Random(42);
        int checkedPts = 0;
        for (int t = 0; t < 400 && checkedPts < 60; t++)
        {
            var s = Rand(r, -2.2, 0.8, -1.3, 1.3);
            var c = Rand(r, -1.5, 1.5, -1.5, 1.5);
            int nz = EscapeIndex(Complex.Zero, s), nc = EscapeIndex(c, s);
            if (nz < 3 || nz > 30 || nc < 3 || nc > 30) continue;
            var pl = Point(s, c, 400).PairPlanes!;
            const double h = 1e-7;
            Complex fdZ = (Iter(Complex.Zero, s + h, nz) - Iter(Complex.Zero, s - h, nz)) / (2 * h);
            Complex fdCs = (Iter(c, s + h, nc) - Iter(c, s - h, nc)) / (2 * h);
            Complex fdCc = (Iter(c + h, s, nc) - Iter(c - h, s, nc)) / (2 * h);
            AssertLogArg(fdZ, pl.LogDzDs[Px], pl.ArgDzDs[Px], "dz/ds");
            AssertLogArg(fdCs, pl.LogDcDs[Px], pl.ArgDcDs[Px], "dc/ds");
            AssertLogArg(fdCc, pl.LogDcDc0[Px], pl.ArgDcDc0[Px], "dc/dc0");
            Assert.True(Math.Abs(Math.Log(Iter(Complex.Zero, s, nz).Magnitude) - pl.LogAbsEscapeZ[Px]) < 1e-5, "ln|z_N|");
            Assert.True(Math.Abs(Math.Log(Iter(c, s, nc).Magnitude) - pl.LogAbsEscapeC[Px]) < 1e-5, "ln|c_N|");
            checkedPts++;
        }
        Assert.True(checkedPts >= 30, $"only {checkedPts} usable points");

        static int EscapeIndex(Complex u, Complex s)
        {
            for (int n = 0; n < 400; n++) { if (u.Magnitude > Bail) return n; u = u * u + s; }
            return -1;
        }
        static Complex Iter(Complex u, Complex s, int n) { for (int k = 0; k < n; k++) u = u * u + s; return u; }
        static void AssertLogArg(Complex fd, float log, float arg, string what)
        {
            Assert.True(Math.Abs(Math.Log(fd.Magnitude) - log) < 1e-3, $"{what}: ln|d| {log} vs {Math.Log(fd.Magnitude)}");
            double da = Math.IEEERemainder(fd.Phase - arg, 2 * Math.PI);
            Assert.True(Math.Abs(da) < 1e-3, $"{what}: arg off by {da}");
        }
    }

    [Fact]
    public void Derivatives_AreNaNForBoundedOrbits()
    {
        // s = -0.9 (period-2 bulb), c = 0.05 in the basin: both bounded.
        var pl = Point(new Complex(-0.9, 0), new Complex(0.05, 0), 500).PairPlanes!;
        Assert.True(float.IsNaN(pl.LogDzDs[Px]) && float.IsNaN(pl.LogDcDs[Px]) && float.IsNaN(pl.LogDcDc0[Px]));
    }

    // Inside M_c both orbits reach the period-2 cycle of s = -0.9, multiplier
    // μ = 4(s + 1) = 0.4 (analytic). Same cycle phase ⇒ secant λ → ln|μ|/2;
    // c = s = z_1 tracks one step ahead (phase lag 1) ⇒ D periodic ⇒ λ → 0.
    [Theory]
    [InlineData(0.05, true)]
    [InlineData(-0.9, false)]
    public void SecantLyapunov_InsideMc_FollowsMultiplierOrPhaseLag(double cRe, bool lagZero)
    {
        const int maxIter = 4000;
        var pl = Point(new Complex(-0.9, 0), new Complex(cRe, 0), maxIter).PairPlanes!;
        Assert.Equal(maxIter, pl.PairSteps[Px]);
        double lambda = pl.SecantLogSum[Px] / pl.PairSteps[Px];
        double expect = lagZero ? Math.Log(0.4) / 2 : 0.0;
        Assert.True(Math.Abs(lambda - expect) < 5e-3, $"λ = {lambda}, expected {expect}");
    }

    [Fact]
    public void ChangingChannels_ReIterates_SameChannels_ReusesCache()
    {
        var calc = new DualOrbitEscapeCalculator(16, 16) { ColorMap = new RampMap(), PairChannels = DualOrbitPairChannels.Winding };
        calc.Calculate();
        calc.Calculate();
        Assert.True(calc.LastCalculateReusedOrbits);
        calc.PairChannels = DualOrbitPairChannels.Winding | DualOrbitPairChannels.Separation;
        calc.Calculate();
        Assert.False(calc.LastCalculateReusedOrbits);
        Assert.NotEmpty(calc.PairPlanes!.MinSeparationLog);
        calc.PairDivergenceEpsilon = 0.5;   // Separation requested ⇒ ε is geometry
        calc.Calculate();
        Assert.False(calc.LastCalculateReusedOrbits);
        calc.PairChannels = DualOrbitPairChannels.Winding;
        calc.Calculate();
        calc.PairDivergenceEpsilon = 0.25;  // not requested ⇒ ε is inert
        calc.Calculate();
        Assert.True(calc.LastCalculateReusedOrbits);
        calc.PairChannels = DualOrbitPairChannels.None;
        calc.Calculate();
        Assert.Null(calc.PairPlanes);
    }

    [Fact]
    public void OnlyRequestedPlanesAreAllocated()
    {
        var pl = Point(new Complex(-0.5, 0.5), new Complex(0.5, 0), 100, DualOrbitPairChannels.Separation).PairPlanes!;
        Assert.NotEmpty(pl.MinSeparationLog);
        Assert.Empty(pl.SecantLogSum);
        Assert.Empty(pl.LogDzDs);
        Assert.True(DualOrbitPairPlanes.BytesPerPixel(DualOrbitPairChannels.All & ~DualOrbitPairChannels.Derivatives) <= 40);
        Assert.Equal(64, DualOrbitPairPlanes.BytesPerPixel(DualOrbitPairChannels.All));
    }

    // Quaternion: the product identity fails (non-commutative), so the secant
    // sum is the telescoped log|D_N| − log|D_0| — checked against a direct
    // double-precision Hamilton-square re-iteration. Complex-only channels are NaN.
    [Fact]
    public void Quaternion_SecantSumIsTelescopedSeparation_ComplexOnlyChannelsNaN()
    {
        var s = new Complex(-0.3, 0.6); var c = new Complex(0.5, 0.2);
        var p = new FractalParameters
        {
            DualOrbitMap = DualOrbitMap.Quaternion, DualOrbitBailout = Bail,
            DualOrbitCSeedX = c.Real, DualOrbitCSeedY = 0, DualOrbitCSeedZ = 0, DualOrbitSZ = 0,
        };
        var calc = new DualOrbitEscapeCalculator(2, 2)
        {
            CenterX = s.Real, CenterY = 0, Zoom = 1e9, MaxIterations = 200,
            FractalParameters = p, ColorMap = new RampMap(), PairChannels = DualOrbitPairChannels.All,
        };
        calc.Calculate();
        var pl = calc.PairPlanes!;
        Assert.True(float.IsNaN(pl.WindingTurns[Px]));
        Assert.True(float.IsNaN(pl.ItineraryAgreement[Px]));
        Assert.True(float.IsNaN(pl.LogDzDs[Px]));
        Assert.True(float.IsFinite(pl.MidpointPerturbation[Px]));

        var (n, logD) = QuatReference(s.Real, c.Real, 200);
        Assert.Equal(n, pl.PairSteps[Px]);
        double expect = logD - Math.Log(Math.Abs(c.Real));
        Assert.True(Math.Abs(pl.SecantLogSum[Px] - expect) < 1e-4 * Math.Max(1, Math.Abs(expect)), $"{pl.SecantLogSum[Px]} vs {expect}");

        static (int n, double logD) QuatReference(double sx, double cx, int maxIter)
        {
            double[] a = { 0, 0, 0, 0 }, q = { 0, cx, 0, 0 }, k = { 0, sx, 0, 0 };
            int n = 0;
            for (; n < maxIter; n++)
            {
                if (Norm(a) > Bail || Norm(q) > Bail) break;
                a = Sq(a, k); q = Sq(q, k);
            }
            double[] d = { q[0] - a[0], q[1] - a[1], q[2] - a[2], q[3] - a[3] };
            return (n, Math.Log(Norm(d)));
        }
        static double Norm(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2] + v[3] * v[3]);
        // Hamilton square q² = (w² − |v|², 2w·v) plus C.
        static double[] Sq(double[] v, double[] k) => new[]
        {
            v[0] * v[0] - v[1] * v[1] - v[2] * v[2] - v[3] * v[3] + k[0],
            2 * v[0] * v[1] + k[1], 2 * v[0] * v[2] + k[2], 2 * v[0] * v[3] + k[3],
        };
    }
}
