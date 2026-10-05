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

// #1116 (epic #1114 S2) — pair-native DualOrbitField values. Oracles are
// independent: a System.Numerics.Complex re-iteration in the test, the
// analytic multiplier of the critical cycle (cycle found by plain iteration),
// and the CLI / region round trip for batch parity.
public sealed class DualOrbitPairFieldTests
{
    private const double Bail = 128.0;
    private const int Px = 3;

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF102030u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static readonly DualOrbitField[] PairFields =
    {
        DualOrbitField.SecantLyapunov, DualOrbitField.DivergenceTime, DualOrbitField.ClosestApproach,
        DualOrbitField.ClosestApproachIndex, DualOrbitField.PairWinding, DualOrbitField.MidpointPerturbation,
        DualOrbitField.ItineraryAgreement,
    };

    private static DualOrbitEscapeCalculator Point(Complex s, Complex c, int maxIter, DualOrbitField field,
        Action<FractalParameters>? tweak = null, DualOrbitMap map = DualOrbitMap.ComplexPlane)
    {
        var p = new FractalParameters
        {
            DualOrbitField = field, DualOrbitMap = map, DualOrbitBailout = Bail,
            DualOrbitCSeedX = c.Real, DualOrbitCSeedY = c.Imaginary,
        };
        tweak?.Invoke(p);
        var calc = new DualOrbitEscapeCalculator(2, 2)
        {
            CenterX = s.Real, CenterY = s.Imaginary, Zoom = 1e9, MaxIterations = maxIter,
            FractalParameters = p, ColorMap = new RampMap(),
        };
        calc.Calculate();
        return calc;
    }

    // Both orbits until EITHER passes the bailout.
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

    // The documented separation: |c − z| floored at 1e-14·|(z, c)| (round-off).
    private static double Sep(Complex z, Complex c)
        => Math.Max((c - z).Magnitude, 1e-14 * Math.Sqrt(z.Magnitude * z.Magnitude + c.Magnitude * c.Magnitude));

    private static IEnumerable<(Complex s, Complex c)> Samples(int seed, int count)
    {
        var r = new Random(seed);
        for (int i = 0; i < count; i++)
            yield return (new Complex(-2.2 + 3.0 * r.NextDouble(), -1.3 + 2.6 * r.NextDouble()),
                          new Complex(-1.5 + 3.0 * r.NextDouble(), -1.5 + 3.0 * r.NextDouble()));
    }

    [Fact]
    public void EachPairField_RequestsItsChannel_ShippedFieldsRequestNone()
    {
        foreach (var f in Enum.GetValues<DualOrbitField>())
        {
            var calc = Point(new Complex(-0.5, 0.3), new Complex(0.5, 0), 50, f);
            if (PairFields.Contains(f)) Assert.NotNull(calc.PairPlanes);
            else Assert.Null(calc.PairPlanes);
        }
        // Layer mode reads no field.
        var layered = Point(new Complex(-0.5, 0.3), new Complex(0.5, 0), 50, DualOrbitField.SecantLyapunov,
            p => p.DualOrbitColorMode = DualOrbitColorMode.PerOrbitLayers);
        Assert.Null(layered.PairPlanes);
    }

    // λ = (1/N) Σ ln|z_k + c_k| over the window, mapped ±span → palette ends.
    [Fact]
    public void SecantLyapunov_MatchesReference_AndIsLiveEverywhere()
    {
        const int maxIter = 300; const double span = 2.0;
        foreach (var (s, c) in Samples(11, 150))
        {
            var (zs, cs, n) = Reference(s, c, maxIter);
            double sum = 0;
            for (int k = 0; k < n; k++) sum += Math.Log(Math.Max((zs[k] + cs[k]).Magnitude, 1e-150));
            double expect = n > 0 ? (0.5 + 0.5 * Math.Clamp(sum / n / span, -1, 1)) * maxIter : maxIter;
            expect = Math.Clamp(expect, 1e-3, maxIter);
            var calc = Point(s, c, maxIter, DualOrbitField.SecantLyapunov);
            Assert.True(Math.Abs(calc.SmoothBuffer[Px] - expect) < 1e-3 * maxIter, $"s={s} c={c}: {calc.SmoothBuffer[Px]} vs {expect}");
            Assert.True(calc.SmoothBuffer[Px] > 0);   // live: never the in-set sentinel
        }

        // A whole frame — interior included — has no dead pixel.
        var frame = new DualOrbitEscapeCalculator(96, 64)
        {
            CenterX = -0.6, CenterY = 0, Zoom = 1, MaxIterations = 400, ColorMap = new RampMap(),
            FractalParameters = new FractalParameters { DualOrbitField = DualOrbitField.SecantLyapunov },
        };
        frame.Calculate();
        Assert.All(frame.SmoothBuffer, v => Assert.True(v > 0));
    }

    // Inside M_c the secant λ tends to ln|μ|/p where both orbits share cycle
    // phase (c near 0, the critical point's Fatou component) and to 0 where they
    // lag (c = s = z_1, one step ahead) — μ from the critical cycle found by
    // plain iteration. Period-1 has no lag: both seeds give ln|μ|.
    [Theory]
    [InlineData(0.2, 0.0)]          // p = 1
    [InlineData(-0.9, 0.0)]         // p = 2
    [InlineData(-0.12, 0.74)]       // p = 3 (rabbit)
    [InlineData(0.28, 0.53)]        // p = 4
    public void SecantLyapunov_InsideMc_ApproachesMultiplierOrZero(double sRe, double sIm)
    {
        var s = new Complex(sRe, sIm);
        var (p, mu) = CriticalCycle(s);
        double lagZero = Math.Log(mu.Magnitude) / p;
        const int maxIter = 6000;
        foreach (var (c, expect) in new[] { (new Complex(0.02, 0.01), lagZero), (s, p == 1 ? lagZero : 0.0) })
        {
            var pl = Point(s, c, maxIter, DualOrbitField.SecantLyapunov).PairPlanes!;
            Assert.Equal(maxIter, pl.PairSteps[Px]);
            double lambda = pl.SecantLogSum[Px] / pl.PairSteps[Px];
            Assert.True(Math.Abs(lambda - expect) < 5e-3, $"s={s} c={c} p={p}: λ={lambda}, expected {expect}");
        }

        static (int, Complex) CriticalCycle(Complex s)
        {
            Complex z = 0;
            for (int i = 0; i < 20000; i++) z = z * z + s;
            Complex w = z;
            for (int p = 1; p <= 64; p++)
            {
                w = w * w + s;
                if ((w - z).Magnitude < 1e-10)
                {
                    Complex mu = 1, u = z;
                    for (int k = 0; k < p; k++) { mu *= 2 * u; u = u * u + s; }
                    return (p, mu);
                }
            }
            throw new InvalidOperationException("no cycle");
        }
    }

    // T: first step n with |D_n| > ρ|D_0|, log-interpolated from step n − 1.
    [Fact]
    public void DivergenceTime_MatchesReference_NeverDivergedIsInterior()
    {
        const int maxIter = 300; const double rho = 4.0;
        int live = 0, dead = 0;
        foreach (var (s, c) in Samples(5, 200))
        {
            var (zs, cs, n) = Reference(s, c, maxIter);
            double t = -1;
            if (n > 0)
            {
                double d0 = Sep(zs[0], cs[0]), eps = rho * d0, prev = d0;
                for (int k = 0; k < n; k++)
                {
                    double d = Sep(zs[k], cs[k]);
                    if (k > 0 && d > eps)
                    {
                        double l0 = Math.Log(Math.Max(prev, 1e-300)), l1 = Math.Log(d);
                        t = k - 1 + (l1 > l0 ? Math.Clamp((Math.Log(eps) - l0) / (l1 - l0), 0, 1) : 1);
                        break;
                    }
                    prev = d;
                }
            }
            var calc = Point(s, c, maxIter, DualOrbitField.DivergenceTime);
            if (t < 0)
            {
                dead++;
                Assert.Equal(0f, calc.SmoothBuffer[Px]);
                Assert.Equal(0xFF102030u, calc.ColorBuffer[Px]);   // interior colour
            }
            else
            {
                live++;
                Assert.True(Math.Abs(calc.SmoothBuffer[Px] - Math.Clamp(t, 1e-3, maxIter)) < 1e-4, $"s={s} c={c}: {calc.SmoothBuffer[Px]} vs {t}");
            }
        }
        Assert.True(live > 20 && dead > 5, $"live {live}, dead {dead}");
    }

    [Fact]
    public void ClosestApproach_AndIndex_MatchReference()
    {
        const int maxIter = 200;
        foreach (var (s, c) in Samples(9, 120))
        {
            var (zs, cs, n) = Reference(s, c, maxIter);
            if (n == 0) continue;
            double d0 = Sep(zs[0], cs[0]), min = double.PositiveInfinity; int idx = -1;
            for (int k = 0; k < n; k++) { double d = Sep(zs[k], cs[k]); if (d < min * (1 - 1e-9)) { min = d; idx = k; } }
            double depth = Math.Max(0, Math.Log(d0 / Math.Max(min, 1e-300)));
            double expect = Math.Clamp((1 - Math.Exp(-depth / 8.0)) * maxIter, 1e-3, maxIter);
            Assert.True(Math.Abs(Point(s, c, maxIter, DualOrbitField.ClosestApproach).SmoothBuffer[Px] - expect) < 1e-3,
                $"s={s} c={c}: approach");
            Assert.Equal((float)Math.Clamp(idx, 1e-3, maxIter), Point(s, c, maxIter, DualOrbitField.ClosestApproachIndex).SmoothBuffer[Px]);
        }
    }

    [Fact]
    public void PairWinding_MidpointAndItinerary_MatchReference()
    {
        const int maxIter = 200;
        foreach (var (s, c) in Samples(21, 120))
        {
            var (zs, cs, n) = Reference(s, c, maxIter);
            double w = 0; int agree = 0; bool broken = false;
            for (int k = 0; k < n; k++)
            {
                w += (zs[k] + cs[k]).Phase / (2 * Math.PI);
                if (k >= 1 && !broken) { if ((zs[k].Imaginary >= 0) == (cs[k].Imaginary >= 0)) agree++; else broken = true; }
            }
            Assert.True(Math.Abs(Point(s, c, maxIter, DualOrbitField.PairWinding).SmoothBuffer[Px]
                - Math.Clamp(0.5 * maxIter + w, 1e-3, maxIter)) < 1e-3, $"s={s} c={c}: winding");
            Assert.Equal((float)Math.Clamp(agree, 1e-3, maxIter), Point(s, c, maxIter, DualOrbitField.ItineraryAgreement).SmoothBuffer[Px]);

            var m = (zs[n] + cs[n]) / 2; var e = (zs[n] - cs[n]) / 2;
            double r = e.Magnitude * e.Magnitude / (m.Magnitude * m.Magnitude);
            double expect = Math.Clamp(Math.Clamp(0.5 + Math.Log10(Math.Max(r, 1e-300)) / 12.0, 0, 1) * maxIter, 1e-3, maxIter);
            Assert.True(Math.Abs(Point(s, c, maxIter, DualOrbitField.MidpointPerturbation).SmoothBuffer[Px] - expect) < 1e-3,
                $"s={s} c={c}: midpoint");
        }
    }

    [Theory]
    [InlineData(DualOrbitField.PairWinding)]
    [InlineData(DualOrbitField.ItineraryAgreement)]
    public void ComplexOnlyFields_AreInteriorUnderTheQuaternionMap(DualOrbitField f)
    {
        var calc = Point(new Complex(-0.4, 0.5), new Complex(0.5, 0.2), 100, f, map: DualOrbitMap.Quaternion);
        Assert.Equal(0f, calc.SmoothBuffer[Px]);
        Assert.Equal(0xFF102030u, calc.ColorBuffer[Px]);
    }

    [Theory]
    [InlineData(DualOrbitField.SecantLyapunov)]
    [InlineData(DualOrbitField.ClosestApproach)]
    [InlineData(DualOrbitField.MidpointPerturbation)]
    public void QuaternionPairFields_AreLiveAndFinite(DualOrbitField f)
    {
        var frame = new DualOrbitEscapeCalculator(48, 32)
        {
            CenterX = -0.3, CenterY = 0, Zoom = 1, MaxIterations = 200, ColorMap = new RampMap(),
            FractalParameters = new FractalParameters { DualOrbitField = f, DualOrbitMap = DualOrbitMap.Quaternion, DualOrbitSZ = 0.2 },
        };
        frame.Calculate();
        Assert.All(frame.SmoothBuffer, v => Assert.True(float.IsFinite(v) && v > 0));
    }

    [Fact]
    public void LyapunovSpan_RescalesAroundMidPalette()
    {
        var s = new Complex(-0.9, 0); var c = new Complex(0.02, 0.01);
        float a = Point(s, c, 3000, DualOrbitField.SecantLyapunov, p => p.DualOrbitLyapunovSpan = 1.0).SmoothBuffer[Px];
        float b = Point(s, c, 3000, DualOrbitField.SecantLyapunov, p => p.DualOrbitLyapunovSpan = 2.0).SmoothBuffer[Px];
        // λ < 0 here: (0.5 − mid) halves when the span doubles.
        Assert.Equal((1500 - a) / 2, 1500 - b, 1);
    }

    // Batch parity: the live view's new settings reach the CLI command and back.
    [Fact]
    public void PairFieldLiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.DivergenceTime, DualOrbitDivergenceRatio = 7.5, DualOrbitLyapunovSpan = 3.25,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.DualOrbitEscape, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, p)!.ToKeyValues(),
        });
        Assert.Contains("--param DualOrbitDivergenceRatio=7.5", report.Command);
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.DualOrbitEscape, out var e2)!.ApplyTo(fresh);
        Assert.Equal(DualOrbitField.DivergenceTime, fresh.DualOrbitField);
        Assert.Equal(7.5, fresh.DualOrbitDivergenceRatio);
        Assert.Equal(3.25, fresh.DualOrbitLyapunovSpan);
    }

    [Fact]
    public void Defaults_AreOmittedFromTheRegionSnapshot()
    {
        var snap = RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, new FractalParameters());
        Assert.True(snap == null || (snap.DualOrbitLyapunovSpan == null && snap.DualOrbitDivergenceRatio == null));
    }
}
