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

// #1120 (epic #1114 S6) — Böttcher-ratio domain colouring. Oracles are
// independent of the backward-lifting / smooth-count code under test: the
// Böttcher PRODUCT formula φ(u) = u·Π(1 + s/u_k²)^(1/2^(k+1)) (principal
// branches, valid far out), bailout invariance, and an OkLab round trip done in
// the test for the colour-blind (a = 0) property.
public sealed class DualOrbitBoettcherDomainTests
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

    private static FractalParameters Domain(Complex c, double bailout = 128, Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters
        {
            DualOrbitColorMode = DualOrbitColorMode.BoettcherDomain,
            DualOrbitCSeedX = c.Real, DualOrbitCSeedY = c.Imaginary, DualOrbitBailout = bailout,
        };
        tweak?.Invoke(p);
        return p;
    }

    private static DualOrbitEscapeCalculator Point(Complex s, FractalParameters p, int maxIter = 400)
    {
        var calc = new DualOrbitEscapeCalculator(2, 2)
        {
            CenterX = s.Real, CenterY = s.Imaginary, Zoom = 1e9, MaxIterations = maxIter,
            FractalParameters = p, ColorMap = new RampMap(),
        };
        calc.Calculate();
        return calc;
    }

    private static DualOrbitEscapeCalculator Frame(FractalParameters p, int w = 96, int h = 72)
    {
        var calc = new DualOrbitEscapeCalculator(w, h)
        {
            CenterX = -0.6, CenterY = 0.05, Zoom = 1, MaxIterations = 400, FractalParameters = p, ColorMap = new RampMap(),
        };
        calc.Calculate();
        return calc;
    }

    // log φ_s(u) by the product formula; valid while |s / u_k²| < 1 along the orbit.
    private static Complex LogPhi(Complex u, Complex s)
    {
        Complex acc = Complex.Log(u);
        double w = 0.5;
        for (int k = 0; k < 80 && u.Magnitude < 1e150; k++)
        {
            acc += w * Complex.Log(1 + s / (u * u));
            u = u * u + s;
            w *= 0.5;
        }
        return acc;
    }

    private static double CircDiff(double a, double b) { double d = Math.Abs(a - b) % 1.0; return Math.Min(d, 1 - d); }

    [Fact]
    public void RatioMatchesTheBoettcherProductFormula()
    {
        var r = new Random(1120);
        int n = 0;
        for (int t = 0; t < 120; t++)
        {
            double rad = 3 + 7 * r.NextDouble(), ang = 2 * Math.PI * r.NextDouble();
            var s = Complex.FromPolarCoordinates(rad, ang);
            var c = new Complex(-0.5 + r.NextDouble(), -0.5 + r.NextDouble());
            var calc = Point(s, Domain(c));
            Complex w = LogPhi(c * c + s, s) - LogPhi(s, s);
            double expTurns = ((w.Imaginary / (2 * Math.PI)) % 1 + 1) % 1;
            Assert.True(CircDiff(calc.BoettcherAngleTurns[Px], expTurns) < 1e-3, $"s={s} c={c}: Δθ {calc.BoettcherAngleTurns[Px]} vs {expTurns}");
            Assert.True(Math.Abs(calc.BoettcherLogModulus[Px] - w.Real) < 1e-4 * (1 + Math.Abs(w.Real)), $"s={s} c={c}: ΔG {calc.BoettcherLogModulus[Px]} vs {w.Real}");
            n++;
        }
        Assert.Equal(120, n);
    }

    // s ∈ M (critical orbit bounded): fall back to log φ_s(c₁) alone.
    [Theory]
    [InlineData(-0.1, 0.1, 1.8, 0.0)]
    [InlineData(-0.5, 0.3, 0.2, 1.9)]
    public void InsideM_FallsBackToTheCOrbitAlone(double sRe, double sIm, double cRe, double cIm)
    {
        var s = new Complex(sRe, sIm); var c = new Complex(cRe, cIm);
        var calc = Point(s, Domain(c));
        Complex w = LogPhi(c * c + s, s);
        double expTurns = ((w.Imaginary / (2 * Math.PI)) % 1 + 1) % 1;
        Assert.True(CircDiff(calc.BoettcherAngleTurns[Px], expTurns) < 1e-3, $"θ_c {calc.BoettcherAngleTurns[Px]} vs {expTurns}");
        Assert.True(Math.Abs(calc.BoettcherLogModulus[Px] - w.Real) < 1e-4 * (1 + Math.Abs(w.Real)), $"G_c {calc.BoettcherLogModulus[Px]} vs {w.Real}");
    }

    [Fact]
    public void BothParts_AreBailoutIndependent()
    {
        var a = Frame(Domain(new Complex(0.5, 0.1), 128));
        var b = Frame(Domain(new Complex(0.5, 0.1), 4096));
        int compared = 0;
        for (int i = 0; i < a.BoettcherAngleTurns.Length; i++)
        {
            float ta = a.BoettcherAngleTurns[i], tb = b.BoettcherAngleTurns[i];
            Assert.Equal(float.IsNaN(ta), float.IsNaN(tb));
            if (float.IsNaN(ta)) continue;
            Assert.True(CircDiff(ta, tb) < 2e-3, $"px {i}: {ta} vs {tb}");
            float ga = a.BoettcherLogModulus[i], gb = b.BoettcherLogModulus[i];
            Assert.True(Math.Abs(ga - gb) < 1e-3 * (1 + Math.Abs(ga)), $"px {i}: {ga} vs {gb}");
            compared++;
        }
        Assert.True(compared > 1000);
    }

    // The built-in cycle never codes on red↔green: every live pixel converts back
    // to OkLab a ≈ 0 (test-side OkLab, independent of the engine's).
    [Fact]
    public void DefaultCycle_IsColourBlindSafe_AEqualsZero()
    {
        var calc = Frame(Domain(new Complex(0.5, 0.0), tweak: p => p.DualOrbitDomainGrid = true));
        int live = 0;
        for (int i = 0; i < calc.ColorBuffer.Length; i++)
        {
            if (float.IsNaN(calc.BoettcherAngleTurns[i])) { Assert.Equal(0xFF102030u, calc.ColorBuffer[i]); continue; }
            uint c = calc.ColorBuffer[i];
            double a = OkLabA((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF);
            Assert.True(Math.Abs(a) < 0.02, $"px {i}: OkLab a = {a} (colour {c:X8})");
            live++;
        }
        Assert.True(live > 1000);

        static double OkLabA(double r8, double g8, double b8)
        {
            static double Lin(double v) { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            double r = Lin(r8), g = Lin(g8), b = Lin(b8);
            double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
            return 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        }
    }

    [Fact]
    public void ThemePalette_NoContours_IsTheThemeAtTheAngle()
    {
        var calc = Frame(Domain(new Complex(0.5, 0.0), tweak: p =>
        {
            p.DualOrbitDomainPalette = DualOrbitDomainPalette.Theme; p.DualOrbitContourDensity = 0;
            p.DualOrbitDomainMarkCuts = false;   // raw mapping: no out-of-domain dimming
        }));
        var map = new RampMap { MaxIterations = 400 };
        for (int i = 0; i < calc.ColorBuffer.Length; i++)
        {
            float t = calc.BoettcherAngleTurns[i];
            if (float.IsNaN(t)) continue;
            // The plane stores Δθ as float; the render used the double — allow the
            // ramp's ×997 amplification of that rounding.
            long expect = unchecked((uint)map.Map(t * 400f, 0f, 400));
            Assert.True(Math.Abs(expect - calc.ColorBuffer[i]) <= 2, $"px {i}: {calc.ColorBuffer[i]:X8} vs {expect:X8}");
        }
    }

    // The ratio is analytic only above the critical level: G(c₁) > G_s(0).
    // Oracle: Green functions by direct iteration to a huge radius (independent
    // of the smooth-count path). The seam the smoke render showed at s ≈
    // −0.42 + 0.9i lies outside the domain; the same column at Im s = 1.2 lies in it.
    [Theory]
    [InlineData(-0.42, 0.9)]
    [InlineData(-0.42, 1.2)]
    [InlineData(-1.9, 0.3)]
    [InlineData(0.6, 0.6)]
    public void InDomain_IsTheCriticalLevelTest(double sRe, double sIm)
    {
        var s = new Complex(sRe, sIm); var c = new Complex(0.5, 0);
        bool expect = Green(c * c + s, s) > Green(Complex.Zero, s);
        var on = Point(s, Domain(c));
        var off = Point(s, Domain(c, tweak: p => p.DualOrbitDomainMarkCuts = false));
        Assert.Equal(expect, on.BoettcherInDomain[Px]);
        if (expect) Assert.Equal(off.ColorBuffer[Px], on.ColorBuffer[Px]);
        else Assert.NotEqual(off.ColorBuffer[Px], on.ColorBuffer[Px]);

        static double Green(Complex u, Complex s)
        {
            for (int n = 0; n < 2000; n++)
            {
                if (u.Magnitude > 1e100) return Math.Log(u.Magnitude) / Math.Pow(2, n);
                u = u * u + s;
            }
            return 0;
        }
    }

    [Fact]
    public void InsideM_IsAlwaysInDomain()
    {
        var calc = Point(new Complex(-0.1, 0.1), Domain(new Complex(1.8, 0)));
        Assert.True(calc.BoettcherInDomain[Px]);
    }

    [Fact]
    public void QuaternionMap_IsInterior()
    {
        var calc = Frame(Domain(new Complex(0.5, 0.2), tweak: p => p.DualOrbitMap = DualOrbitMap.Quaternion), 32, 24);
        Assert.All(calc.ColorBuffer, c => Assert.Equal(0xFF102030u, c));
    }

    [Fact]
    public void SwitchingIntoDomain_ReIterates_DomainKnobs_Recolour()
    {
        var p = new FractalParameters { DualOrbitCSeedX = 0.5 };
        var calc = Frame(p, 48, 36);
        p.DualOrbitColorMode = DualOrbitColorMode.BoettcherDomain;
        calc.Calculate();
        Assert.False(calc.LastCalculateReusedOrbits);
        p.DualOrbitContourDensity = 11; p.DualOrbitDomainGrid = true; p.DualOrbitDomainPalette = DualOrbitDomainPalette.Theme;
        calc.Calculate();
        Assert.True(calc.LastCalculateReusedOrbits);
        Assert.Equal(Frame(p.Clone(), 48, 36).ColorBuffer, calc.ColorBuffer);
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualOrbitColorMode = DualOrbitColorMode.BoettcherDomain, DualOrbitContourDensity = 9.5,
            DualOrbitDomainGrid = true, DualOrbitDomainPalette = DualOrbitDomainPalette.Theme,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.JulibrotPair, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.JulibrotPair, p)!.ToKeyValues(),
        });
        Assert.Contains("--param DualOrbitContourDensity=9.5", report.Command);
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.JulibrotPair, out _)!.ApplyTo(fresh);
        Assert.Equal(DualOrbitColorMode.BoettcherDomain, fresh.DualOrbitColorMode);
        Assert.Equal(9.5, fresh.DualOrbitContourDensity);
        Assert.True(fresh.DualOrbitDomainGrid);
        Assert.Equal(DualOrbitDomainPalette.Theme, fresh.DualOrbitDomainPalette);
    }
}
