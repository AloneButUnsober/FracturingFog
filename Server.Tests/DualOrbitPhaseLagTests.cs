// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1121 (epic #1114 S7) — interior cycle phase lag. Oracles are independent:
// the exact c = s case (c_n = z_{n+1} ⇒ lag 1), the critical cycle found by
// plain iteration in the test (period, multiplier), the count of lag classes in
// the Julia plane, and the S2 secant Lyapunov field (lag 0 ⇔ λ → ln|μ|/p).
public sealed class DualOrbitPhaseLagTests
{
    private const int Px = 3;

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF102030u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    // One sample (s, c) on a 2×2 SxSy frame (pixel 3 = frame centre).
    private static DualOrbitEscapeCalculator Point(Complex s, Complex c, DualOrbitField f, int maxIter = 3000,
        DualOrbitMap map = DualOrbitMap.ComplexPlane)
    {
        var calc = new DualOrbitEscapeCalculator(2, 2)
        {
            CenterX = s.Real, CenterY = s.Imaginary, Zoom = 1e9, MaxIterations = maxIter, ColorMap = new RampMap(),
            FractalParameters = new FractalParameters
            {
                DualOrbitField = f, DualOrbitMap = map, DualOrbitCSeedX = c.Real, DualOrbitCSeedY = c.Imaginary,
            },
        };
        calc.Calculate();
        return calc;
    }

    // The Julia (CxCy) slice at fixed s: image = c-seed plane.
    private static DualOrbitEscapeCalculator Julia(Complex s, DualOrbitField f, int size = 96, int maxIter = 3000)
    {
        var calc = new DualOrbitEscapeCalculator(size, size)
        {
            // Off-centre so no pixel sits exactly on c = 0 (identical orbits: the
            // documented σ₀ = 0 coalescence case).
            CenterX = 0.0131, CenterY = 0.0077, Zoom = 1.3, MaxIterations = maxIter, ColorMap = new RampMap(),
            FractalParameters = new FractalParameters
            {
                DualOrbitField = f, DualOrbitSliceAxes = DualOrbitSliceAxes.CxCy, DualOrbitSX = s.Real, DualOrbitSY = s.Imaginary,
            },
        };
        calc.Calculate();
        return calc;
    }

    private static (int p, Complex mu) CriticalCycle(Complex s)
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

    public static IEnumerable<object[]> Components() => new[]
    {
        new object[] { 0.2, 0.0 },        // p = 1
        new object[] { -0.9, 0.0 },       // p = 2
        new object[] { -0.12, 0.74 },     // p = 3 (rabbit)
        new object[] { 0.28, 0.53 },      // p = 4
    };

    [Theory]
    [MemberData(nameof(Components))]
    public void CyclePeriod_MatchesTheCriticalCycle(double sRe, double sIm)
    {
        var s = new Complex(sRe, sIm);
        var (p, _) = CriticalCycle(s);
        Assert.Equal((float)p, Point(s, 0.05, DualOrbitField.CyclePeriod).SmoothBuffer[Px]);
    }

    // c = s is z_1, so c_n = z_{n+1} exactly: lag 1 (for p ≥ 2), and a seed near
    // the critical point is in its Fatou component: lag 0.
    [Theory]
    [MemberData(nameof(Components))]
    public void ExactShift_GivesLagOne_NearZeroGivesLagZero(double sRe, double sIm)
    {
        var s = new Complex(sRe, sIm);
        var (p, _) = CriticalCycle(s);
        float near = Point(s, new Complex(0.01, 0.005), DualOrbitField.PhaseLag).SmoothBuffer[Px];
        float shifted = Point(s, s, DualOrbitField.PhaseLag).SmoothBuffer[Px];
        Assert.Equal(1e-3f, near);                                // lag 0 (LiveFloor)
        Assert.Equal(p == 1 ? 1e-3f : 1f, shifted);
        float frac = Point(s, s, DualOrbitField.PhaseLagFraction, 3000).SmoothBuffer[Px];
        Assert.Equal((float)((p == 1 ? 0.5 : 1.5) / p * 3000), frac, 2);
    }

    // The lag partitions the basin into exactly p classes, and is stable in N.
    [Theory]
    [MemberData(nameof(Components))]
    public void JuliaPlane_HasExactlyPLagClasses_StableInN(double sRe, double sIm)
    {
        var s = new Complex(sRe, sIm);
        var (p, _) = CriticalCycle(s);
        var a = Julia(s, DualOrbitField.PhaseLag, maxIter: 3000);
        var b = Julia(s, DualOrbitField.PhaseLag, maxIter: 3000 + p);
        var lags = new HashSet<float>();
        int live = 0;
        for (int i = 0; i < a.SmoothBuffer.Length; i++)
        {
            if (a.SmoothBuffer[i] == 0f) continue;
            live++;
            lags.Add(a.SmoothBuffer[i]);
            Assert.Equal(a.SmoothBuffer[i], b.SmoothBuffer[i]);
        }
        Assert.True(live > 500, $"live {live}");
        Assert.Equal(p, lags.Count);
    }

    // Cross-field invariant with S2: lag 0 ⇔ secant λ ≈ ln|μ|/p, lag ≠ 0 ⇔ λ ≈ 0.
    [Theory]
    [InlineData(-0.9, 0.0)]
    [InlineData(-0.12, 0.74)]
    public void PhaseLag_AgreesWithSecantLyapunov(double sRe, double sIm)
    {
        var s = new Complex(sRe, sIm);
        var (p, mu) = CriticalCycle(s);
        var lag = Julia(s, DualOrbitField.PhaseLag, 48, 4000);
        var pair = new DualOrbitEscapeCalculator(48, 48)
        {
            CenterX = 0.0131, CenterY = 0.0077, Zoom = 1.3, MaxIterations = 4000, ColorMap = new RampMap(),
            PairChannels = DualOrbitPairChannels.SecantLogSum,
            FractalParameters = new FractalParameters
            {
                DualOrbitSliceAxes = DualOrbitSliceAxes.CxCy, DualOrbitSX = s.Real, DualOrbitSY = s.Imaginary,
            },
        };
        pair.Calculate();
        double target0 = Math.Log(mu.Magnitude) / p;
        int checkedPx = 0;
        for (int i = 0; i < lag.SmoothBuffer.Length; i++)
        {
            float l = lag.SmoothBuffer[i];
            if (l == 0f || pair.PairPlanes!.PairSteps[i] < 4000) continue;
            double lambda = pair.PairPlanes.SecantLogSum[i] / pair.PairPlanes.PairSteps[i];
            double expect = l < 0.5f ? target0 : 0.0;
            Assert.True(Math.Abs(lambda - expect) < 0.02, $"px {i}: lag {l}, λ {lambda}, expected {expect}");
            checkedPx++;
        }
        Assert.True(checkedPx > 200);
    }

    // Categorical colours: every live pixel carries the palette colour of its own
    // class, so the p classes are p distinct colours whatever the theme (the
    // smoke render showed a cycling theme folding 250/750/1250/1750 onto one).
    [Theory]
    [MemberData(nameof(Components))]
    public void Categorical_GivesEachLagClassItsOwnColour(double sRe, double sIm)
    {
        var s = new Complex(sRe, sIm);
        var (p, _) = CriticalCycle(s);
        var calc = Julia(s, DualOrbitField.PhaseLag);
        var colours = new HashSet<uint>();
        uint[] palette = { 0xFFE69F00u, 0xFF0072B2u, 0xFFF0E442u, 0xFF56B4E9u, 0xFFD55E00u, 0xFF009E73u, 0xFFCC79A7u };
        for (int i = 0; i < calc.SmoothBuffer.Length; i++)
        {
            float v = calc.SmoothBuffer[i];
            if (v == 0f) continue;
            int k = (int)Math.Round(v);
            Assert.Equal(palette[k], calc.ColorBuffer[i]);
            colours.Add(calc.ColorBuffer[i]);
        }
        Assert.Equal(p, colours.Count);
    }

    [Fact]
    public void ThemeMode_UsesTheTheme()
    {
        var calc = new DualOrbitEscapeCalculator(48, 48)
        {
            CenterX = 0.0131, CenterY = 0.0077, Zoom = 1.3, MaxIterations = 2000, ColorMap = new RampMap(),
            FractalParameters = new FractalParameters
            {
                DualOrbitField = DualOrbitField.PhaseLag, DualOrbitLagColors = DualOrbitCategoricalColors.Theme,
                DualOrbitSliceAxes = DualOrbitSliceAxes.CxCy, DualOrbitSX = -0.12, DualOrbitSY = 0.74,
            },
        };
        calc.Calculate();
        var map = new RampMap { MaxIterations = 2000 };
        for (int i = 0; i < calc.SmoothBuffer.Length; i++)
            if (calc.SmoothBuffer[i] != 0f)
                Assert.Equal(unchecked((uint)map.Map(calc.SmoothBuffer[i], 0f, 2000)), calc.ColorBuffer[i]);
    }

    [Fact]
    public void OutsideTheSet_AndQuaternion_AreInterior()
    {
        var outside = Point(new Complex(0.5, 0.5), 0.05, DualOrbitField.CyclePeriod, 500);
        Assert.Equal(0f, outside.SmoothBuffer[Px]);
        Assert.Equal(0xFF102030u, outside.ColorBuffer[Px]);
        // c escapes while z is bounded: CyclePeriod is live, PhaseLag is not.
        var s = new Complex(-0.9, 0); var farC = new Complex(1.9, 0);
        Assert.Equal(2f, Point(s, farC, DualOrbitField.CyclePeriod).SmoothBuffer[Px]);
        Assert.Equal(0f, Point(s, farC, DualOrbitField.PhaseLag).SmoothBuffer[Px]);
        var quat = Point(s, 0.05, DualOrbitField.PhaseLag, map: DualOrbitMap.Quaternion);
        Assert.Equal(0xFF102030u, quat.ColorBuffer[Px]);
    }
}
