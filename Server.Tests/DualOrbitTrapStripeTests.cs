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

// #1117 (epic #1114 S3) — FF's orbit colourings per orbit. Scalar fields are
// checked against a test-side re-iteration with the textbook definitions
// (point trap = min|u_k|, stripe average with Härkönen smoothing, TIA); the
// orbit-theme paths against the theme's own Sample/MapWithOrbit driven by a
// loop in the test.
public sealed class DualOrbitTrapStripeTests
{
    private const int Px = 3;
    private const double Bail = 128.0;

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF102030u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static DualOrbitEscapeCalculator Point(Complex s, Complex c, DualOrbitField f, int maxIter = 300,
        IColorMap? map = null, Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters
        {
            DualOrbitField = f, DualOrbitBailout = Bail, DualOrbitCSeedX = c.Real, DualOrbitCSeedY = c.Imaginary,
        };
        tweak?.Invoke(p);
        var calc = new DualOrbitEscapeCalculator(2, 2)
        {
            CenterX = s.Real, CenterY = s.Imaginary, Zoom = 1e9, MaxIterations = maxIter,
            FractalParameters = p, ColorMap = map ?? new RampMap(),
        };
        calc.Calculate();
        return calc;
    }

    // Orbit u_0..u_N (N = escape index, or maxIter if bounded) and the smooth count.
    private static (Complex[] u, int n, bool esc, double smooth) Orbit(Complex u0, Complex s, int maxIter)
    {
        var u = new Complex[maxIter + 1];
        Complex z = u0;
        for (int n = 0; n < maxIter; n++)
        {
            u[n] = z;
            if (z.Magnitude * z.Magnitude > Bail * Bail)
            {
                double nu = Math.Log(Math.Log(z.Magnitude) / Math.Log(Bail)) / Math.Log(2);
                return (u, n, true, n - nu);
            }
            z = z * z + s;
        }
        u[maxIter] = z;
        return (u, maxIter, false, maxIter);
    }

    private static (Complex s, Complex c)[] Samples(int seed, int count)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, count).Select(_ =>
            (new Complex(-2.2 + 3.0 * r.NextDouble(), -1.3 + 2.6 * r.NextDouble()),
             new Complex(-1.2 + 2.4 * r.NextDouble(), -1.2 + 2.4 * r.NextDouble()))).ToArray();
    }

    // Point trap: min over k ≥ 1 (seed excluded) of |u_k|, before escape.
    private static double PointTrap(Complex u0, Complex s, int maxIter)
    {
        var (u, n, _, _) = Orbit(u0, s, maxIter);
        double m = double.PositiveInfinity;
        for (int k = 1; k < n; k++) m = Math.Min(m, u[k].Magnitude);
        return m;
    }

    [Fact]
    public void PointTraps_MatchTheDefinition()
    {
        const int maxIter = 300; const double scale = 0.25;
        foreach (var (s, c) in Samples(17, 120))
        {
            double tz = PointTrap(0, s, maxIter), tc = PointTrap(c, s, maxIter);
            if (double.IsInfinity(tz) || double.IsInfinity(tc)) continue;
            Check(DualOrbitField.TrapZ, tz / (tz + scale));
            Check(DualOrbitField.TrapC, tc / (tc + scale));
            double d = (float)tc - (float)tz;
            Check(DualOrbitField.TrapDelta, 0.5 + 0.5 * d / (Math.Abs(d) + scale));

            void Check(DualOrbitField f, double t)
            {
                float got = Point(s, c, f, maxIter).SmoothBuffer[Px];
                double expect = Math.Clamp(t * maxIter, 1e-3, maxIter);
                Assert.True(Math.Abs(got - expect) < 1e-3 * maxIter, $"{f} s={s} c={c}: {got} vs {expect}");
            }
        }
    }

    // Stripe (Härkönen) and TIA averages over k = 1..N−1, smoothed across the
    // escape step by the fractional smooth count.
    [Fact]
    public void StripeAndTia_MatchTheDefinitions()
    {
        const int maxIter = 300; const double density = 5.0;
        foreach (var (s, c) in Samples(23, 120))
        {
            double sz = Stripe(0, s), sc = Stripe(c, s);
            Check(DualOrbitField.StripeZ, sz);
            Check(DualOrbitField.StripeC, sc);
            Check(DualOrbitField.StripeInterference, sz * sc);
            Check(DualOrbitField.TiaZ, Tia(0, s));
            Check(DualOrbitField.TiaC, Tia(c, s));

            void Check(DualOrbitField f, double t)
            {
                float got = Point(s, c, f, maxIter).SmoothBuffer[Px];
                double expect = Math.Clamp(t * maxIter, 1e-3, maxIter);
                Assert.True(Math.Abs(got - expect) < 1e-3 * maxIter, $"{f} s={s} c={c}: {got} vs {expect}");
            }
        }

        double Stripe(Complex u0, Complex s)
            => Avg(u0, s, (u, k) => 0.5 + 0.5 * Math.Sin(density * u[k].Phase));
        double Tia(Complex u0, Complex s)
            => Avg(u0, s, (u, k) =>
            {
                double prev2 = u[k - 1].Magnitude * u[k - 1].Magnitude, lo = Math.Abs(prev2 - s.Magnitude), hi = prev2 + s.Magnitude;
                return hi > lo ? (u[k].Magnitude - lo) / (hi - lo) : double.NaN;
            });
        double Avg(Complex u0, Complex s, Func<Complex[], int, double> sample)
        {
            var (u, n, esc, smooth) = Orbit(u0, s, maxIter);
            double sum = 0, last = 0; int count = 0;
            for (int k = 1; k < n; k++)
            {
                double v = sample(u, k);
                if (double.IsNaN(v)) continue;
                sum += v; last = v; count++;
            }
            if (count == 0) return 0;
            double a = sum / count;
            if (!esc || count < 2) return a;
            double w = smooth - Math.Floor(smooth);
            return w * a + (1 - w) * (sum - last) / (count - 1);
        }
    }

    [Theory]
    [InlineData(DualOrbitField.TrapZ)]
    [InlineData(DualOrbitField.StripeInterference)]
    [InlineData(DualOrbitField.TiaC)]
    public void ScalarFields_AreLiveEverywhere(DualOrbitField f)
    {
        var frame = new DualOrbitEscapeCalculator(64, 48)
        {
            CenterX = -0.6, CenterY = 0, Zoom = 1, MaxIterations = 300, ColorMap = new RampMap(),
            FractalParameters = new FractalParameters { DualOrbitField = f },
        };
        frame.Calculate();
        Assert.All(frame.SmoothBuffer, v => Assert.True(v > 0));
    }

    [Fact]
    public void TrapFields_FillTheTrapBuffer_OthersDoNot()
    {
        var s = new Complex(-0.4, 0.6); var c = new Complex(0.5, 0.1);
        var trap = Point(s, c, DualOrbitField.TrapC);
        Assert.Equal((float)PointTrap(c, s, 300), trap.TrapBuffer[Px], 4);
        Assert.Empty(Point(s, c, DualOrbitField.GreenRatio).TrapBuffer);
    }

    // The trap shape param reaches FF's own SDF sampler: a Cross trap reads
    // min(|Re u|, |Im u|) — independent of the point trap.
    [Fact]
    public void TrapShape_SelectsTheSampler()
    {
        var s = new Complex(-0.4, 0.6); var c = new Complex(0.5, 0.1);
        var (u, n, _, _) = Orbit(c, s, 300);
        double m = double.PositiveInfinity;
        for (int k = 1; k < n; k++) m = Math.Min(m, Math.Min(Math.Abs(u[k].Real), Math.Abs(u[k].Imaginary)));
        var calc = Point(s, c, DualOrbitField.TrapC, tweak: p => p.DualOrbitTrapShape = OrbitTrapShapeDef.Cross);
        Assert.Equal((float)m, calc.TrapBuffer[Px], 4);
    }

    // OrbitThemeZ / C: an orbit-aware theme colours from that orbit — the test
    // drives the same theme's Sample/MapWithOrbit along its own re-iteration.
    [Theory]
    [InlineData(DualOrbitField.OrbitThemeZ)]
    [InlineData(DualOrbitField.OrbitThemeC)]
    public void OrbitThemeField_ColoursFromTheChosenOrbit(DualOrbitField f)
    {
        var theme = new OrbitTrapPointMap();
        int checkedPx = 0;
        foreach (var (s, c) in Samples(5, 80))
        {
            var u0 = f == DualOrbitField.OrbitThemeZ ? Complex.Zero : c;
            var (u, n, esc, smooth) = Orbit(u0, s, 300);
            var calc = Point(s, c, f, 300, theme);
            if (!esc) { Assert.Equal(((IColorMap)theme).InSetColor, calc.ColorBuffer[Px]); continue; }
            if (n <= 1) continue;   // surround handling
            theme.MaxIterations = 300;
            theme.InitOrbit(out var acc);
            for (int k = 1; k < n; k++) theme.Sample(ref acc, u[k].Real, u[k].Imaginary, s.Real, s.Imaginary, k);
            uint expect = unchecked((uint)theme.MapWithOrbit((float)smooth, 0f, 300, 0f, 0f, in acc));
            Assert.Equal(expect, calc.ColorBuffer[Px]);
            checkedPx++;
        }
        Assert.True(checkedPx > 20);
    }

    [Fact]
    public void OrbitThemeField_WithAPlainTheme_IsTheEscapeTime()
    {
        var s = new Complex(-0.4, 0.7); var c = new Complex(0.5, 0.1);
        Assert.Equal(Point(s, c, DualOrbitField.EscapeTimeC).ColorBuffer[Px], Point(s, c, DualOrbitField.OrbitThemeC).ColorBuffer[Px]);
    }

    // Layer mode: an orbit-aware layer theme samples its own orbit. With the c
    // layer invisible the result is the z layer's orbit colour.
    [Fact]
    public void OrbitAwareLayerTheme_SamplesItsOwnOrbit()
    {
        var theme = new OrbitTrapPointMap();
        var s = new Complex(-0.75, 0.2); var c = new Complex(0.5, 0.0);
        var calc = new DualOrbitEscapeCalculator(2, 2)
        {
            CenterX = s.Real, CenterY = s.Imaginary, Zoom = 1e9, MaxIterations = 300, ColorMap = new RampMap(),
            LayerThemeZ = theme, LayerThemeC = new RampMap(),
            FractalParameters = new FractalParameters
            {
                DualOrbitColorMode = DualOrbitColorMode.PerOrbitLayers, DualOrbitOpacityC = 0.0,
                DualOrbitCSeedX = c.Real, DualOrbitBailout = Bail,
            },
        };
        calc.Calculate();
        var (u, n, esc, smooth) = Orbit(0, s, 300);
        Assert.True(esc && n > 1);
        theme.InitOrbit(out var acc);
        for (int k = 1; k < n; k++) theme.Sample(ref acc, u[k].Real, u[k].Imaginary, s.Real, s.Imaginary, k);
        uint expect = unchecked((uint)theme.MapWithOrbit((float)smooth, 0f, 300, 0f, 0f, in acc));
        Assert.Equal(expect, calc.ColorBuffer[Px]);
    }

    [Fact]
    public void OrbitThemes_AreNeverServedFromTheCache_ScalarFieldsAre()
    {
        var themed = Point(new Complex(-0.4, 0.7), 0.5, DualOrbitField.OrbitThemeZ, map: new OrbitTrapPointMap());
        themed.Calculate();
        Assert.False(themed.LastCalculateReusedOrbits);
        var plain = Point(new Complex(-0.4, 0.7), 0.5, DualOrbitField.TrapZ);
        plain.Calculate();
        Assert.True(plain.LastCalculateReusedOrbits);
    }

    [Theory]
    [InlineData(DualOrbitField.TrapZ)]
    [InlineData(DualOrbitField.OrbitThemeC)]
    public void QuaternionMap_IsInterior(DualOrbitField f)
    {
        var calc = Point(new Complex(-0.4, 0.7), 0.5, f, tweak: p => p.DualOrbitMap = DualOrbitMap.Quaternion);
        Assert.Equal(0xFF102030u, calc.ColorBuffer[Px]);
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.TrapDelta, DualOrbitTrapShape = OrbitTrapShapeDef.Hexagon,
            DualOrbitTrapScale = 0.6, DualOrbitStripeDensity = 9,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.DualOrbitEscape, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.DualOrbitEscape, out _)!.ApplyTo(fresh);
        Assert.Equal(DualOrbitField.TrapDelta, fresh.DualOrbitField);
        Assert.Equal(OrbitTrapShapeDef.Hexagon, fresh.DualOrbitTrapShape);
        Assert.Equal(0.6, fresh.DualOrbitTrapScale);
        Assert.Equal(9, fresh.DualOrbitStripeDensity);
    }
}
