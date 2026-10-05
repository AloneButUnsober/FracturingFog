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

// #1122 (epic #1114 S8) — bivariate colour modes. Oracles: OkLab done in the
// test (independent of GradientColorSpaces), smooth counts by test-side
// re-iteration, a KS check on the copula marginals, the Field-mode render for
// κ = 0, and fresh renders for cache reuse.
public sealed class DualOrbitBivariateTests
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

    private static DualOrbitEscapeCalculator Calc(int w, int h, double cx, double cy, double zoom,
        DualOrbitColorMode mode, Action<FractalParameters>? tweak = null, int maxIter = 300)
    {
        var p = new FractalParameters { DualOrbitColorMode = mode, DualOrbitBailout = Bail };
        tweak?.Invoke(p);
        var calc = new DualOrbitEscapeCalculator(w, h)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIter, FractalParameters = p, ColorMap = new RampMap(),
        };
        calc.Calculate();
        return calc;
    }

    private static double? Smooth(Complex u, Complex s)
    {
        for (int n = 0; n < 300; n++)
        {
            if (u.Magnitude > Bail) return n - Math.Log(Math.Log(u.Magnitude) / Math.Log(Bail)) / Math.Log(2);
            u = u * u + s;
        }
        return null;
    }

    // ── test-side OkLab (Ottosson) ───────────────────────────────────────────
    private static (double L, double A, double B) ToOkLab(uint c)
    {
        static double Lin(double v) { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        double r = Lin((c >> 16) & 0xFF), g = Lin((c >> 8) & 0xFF), b = Lin(c & 0xFF);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static uint FromOkLab(double L, double A, double B)
    {
        double l = Math.Pow(L + 0.3963377774 * A + 0.2158037573 * B, 3);
        double m = Math.Pow(L - 0.1055613458 * A - 0.0638541728 * B, 3);
        double s = Math.Pow(L - 0.0894841775 * A - 1.2914855480 * B, 3);
        double r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        static uint Enc(double v) { v = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4) - 0.055; return (uint)Math.Clamp(v * 255 + 0.5, 0, 255); }
        return 0xFF000000u | (Enc(r) << 16) | (Enc(g) << 8) | Enc(b);
    }

    private static bool Near(uint a, uint b, int tol)
        => Enumerable.Range(0, 3).All(k => Math.Abs((int)((a >> (8 * k)) & 0xFF) - (int)((b >> (8 * k)) & 0xFF)) <= tol);

    // Bivariate2D = bilinear OkLab square (near-black, blue, amber, near-white)
    // at u = n_z/(n_z + k), v = n_c/(n_c + k).
    [Fact]
    public void Bivariate2D_IsTheBlueAmberSquareAtTheTwoCounts()
    {
        var corners = new[] { (0.20, 0.0, 0.0), ToOkLab(0xFF0072B2u), ToOkLab(0xFFE69F00u), (0.96, 0.0, 0.0) };
        var r = new Random(1122); int n = 0;
        for (int t = 0; t < 150; t++)
        {
            var s = new Complex(-2.2 + 3 * r.NextDouble(), -1.3 + 2.6 * r.NextDouble());
            var c = new Complex(-1 + 2 * r.NextDouble(), -1 + 2 * r.NextDouble());
            double? nz = Smooth(0, s), nc = Smooth(c, s);
            if (nz == null || nc == null || nz < 2 || nc < 2) continue;
            double k = new FractalParameters().DualOrbitBivariateScale;
            double u = nz.Value / (nz.Value + k), v = nc.Value / (nc.Value + k);
            double Mix(Func<(double, double, double), double> f)
                => (1 - u) * (1 - v) * f(corners[0]) + u * (1 - v) * f(corners[1]) + (1 - u) * v * f(corners[2]) + u * v * f(corners[3]);
            uint expect = FromOkLab(Mix(x => x.Item1), Mix(x => x.Item2), Mix(x => x.Item3));
            var calc = Calc(2, 2, s.Real, s.Imaginary, 1e9, DualOrbitColorMode.Bivariate2D, p => { p.DualOrbitCSeedX = c.Real; p.DualOrbitCSeedY = c.Imaginary; });
            Assert.True(Near(expect, calc.ColorBuffer[Px], 2), $"s={s} c={c}: {calc.ColorBuffer[Px]:X8} vs {expect:X8}");
            n++;
        }
        Assert.True(n > 50);
    }

    [Fact]
    public void ThemeByLightness_IsThemeAtUScaledByV()
    {
        var calc = Calc(48, 36, -0.6, 0, 1, DualOrbitColorMode.Bivariate2D, p => p.DualOrbitPalette2D = DualOrbitPalette2D.ThemeByLightness);
        var map = new RampMap { MaxIterations = 300 };
        for (int i = 0; i < calc.ColorBuffer.Length; i++)
        {
            float u = calc.BivariateU[i], v = calc.BivariateV[i];
            if (float.IsNaN(u)) continue;
            uint baseC = unchecked((uint)map.Map(u * 300, 0f, 300));
            double k = 0.35 + 0.65 * v;
            uint expect = 0xFF000000u | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
            Assert.True(Near(expect, calc.ColorBuffer[i], 1), $"px {i}");
            uint Ch(int sh) => (uint)Math.Clamp(((baseC >> sh) & 0xFF) * k + 0.5, 0, 255);
        }
    }

    // Copula: after JointEqualised each channel's escaped values are uniform.
    [Fact]
    public void JointEqualised_MarginalsAreUniform()
    {
        var calc = Calc(160, 120, -0.6, 0, 1, DualOrbitColorMode.JointEqualised);
        foreach (var ch in new[] { calc.BivariateU, calc.BivariateV })
        {
            var vals = ch.Where(x => !float.IsNaN(x) && x < 1f).OrderBy(x => x).ToArray();
            Assert.True(vals.Length > 5000);
            double ks = 0;
            for (int i = 0; i < vals.Length; i += 37) ks = Math.Max(ks, Math.Abs(vals[i] - (i + 0.5) / vals.Length));
            Assert.True(ks < 0.02, $"KS = {ks}");
        }
    }

    // PerceptualSplit: every pixel round-trips to OkLab a ≈ 0, L = 0.22 + 0.72u,
    // b = 0.30(v − ½) (pixels clipped by the sRGB gamut skipped).
    [Fact]
    public void PerceptualSplit_RoundTripsThroughOkLab()
    {
        var calc = Calc(96, 72, -0.6, 0, 1, DualOrbitColorMode.PerceptualSplit);
        int checkedPx = 0;
        for (int i = 0; i < calc.ColorBuffer.Length; i++)
        {
            float u = calc.BivariateU[i], v = calc.BivariateV[i];
            if (float.IsNaN(u)) continue;
            uint c = calc.ColorBuffer[i];
            if (Enumerable.Range(0, 3).Any(k => ((c >> (8 * k)) & 0xFF) is 0 or 255)) continue;
            var (L, A, B) = ToOkLab(c);
            Assert.True(Math.Abs(A) < 0.02, $"px {i}: a = {A}");
            Assert.True(Math.Abs(L - (0.22 + 0.72 * u)) < 0.02, $"px {i}: L {L} vs {0.22 + 0.72 * u}");
            Assert.True(Math.Abs(B - 0.30 * (v - 0.5)) < 0.02, $"px {i}: b {B} vs {0.30 * (v - 0.5)}");
            checkedPx++;
        }
        Assert.True(checkedPx > 1000);
    }

    // κ = 0 reduces PhaseModulated to the theme at n_z — the EscapeTimeZ field.
    [Fact]
    public void PhaseModulated_KappaZero_IsEscapeTimeZ()
    {
        var pm = Calc(64, 48, -0.6, 0, 1, DualOrbitColorMode.PhaseModulated, p => p.DualOrbitPhaseK = 0);
        var field = Calc(64, 48, -0.6, 0, 1, DualOrbitColorMode.Field, p => p.DualOrbitField = DualOrbitField.EscapeTimeZ);
        int n = 0;
        for (int i = 0; i < pm.ColorBuffer.Length; i++)
            if (field.SmoothBuffer[i] > 2f) { Assert.Equal(field.ColorBuffer[i], pm.ColorBuffer[i]); n++; }
        Assert.True(n > 1000);
    }

    [Fact]
    public void PhaseModulated_IsThemeAtZPlusKappaC()
    {
        var s = new Complex(-0.4, 0.7); var c = new Complex(0.5, 0.1);
        double nz = Smooth(0, s)!.Value, nc = Smooth(c, s)!.Value;
        var calc = Calc(2, 2, s.Real, s.Imaginary, 1e9, DualOrbitColorMode.PhaseModulated, p =>
        {
            p.DualOrbitPhaseK = 1.7; p.DualOrbitCSeedX = c.Real; p.DualOrbitCSeedY = c.Imaginary;
        });
        uint expect = unchecked((uint)new RampMap().Map((float)(nz + 1.7 * nc), 0f, 300));
        Assert.True(Near(expect, calc.ColorBuffer[Px], 2), $"{calc.ColorBuffer[Px]:X8} vs {expect:X8}");
    }

    // Both bounded (M_c) = interior; z bounded but c escaped = coloured.
    [Fact]
    public void OnlyMcIsInterior()
    {
        var both = Calc(2, 2, -0.1, 0.1, 1e9, DualOrbitColorMode.Bivariate2D, p => p.DualOrbitCSeedX = 0.05);
        Assert.Equal(0xFF102030u, both.ColorBuffer[Px]);
        var cOnly = Calc(2, 2, -0.1, 0.1, 1e9, DualOrbitColorMode.Bivariate2D, p => p.DualOrbitCSeedX = 1.8);
        Assert.NotEqual(0xFF102030u, cOnly.ColorBuffer[Px]);
        Assert.Equal(1f, cOnly.BivariateU[Px]);   // bounded channel saturates
    }

    // All four modes share the layer cache: switching between them recolours.
    [Fact]
    public void SwitchingBetweenLayerAndBivariateModes_Recolours()
    {
        var p = new FractalParameters { DualOrbitColorMode = DualOrbitColorMode.PerOrbitLayers, DualOrbitThemeZ = "", DualOrbitThemeC = "" };
        var calc = new DualOrbitEscapeCalculator(48, 36) { CenterX = -0.6, Zoom = 1, MaxIterations = 300, FractalParameters = p, ColorMap = new RampMap() };
        calc.Calculate();
        foreach (var m in new[] { DualOrbitColorMode.Bivariate2D, DualOrbitColorMode.JointEqualised,
                                  DualOrbitColorMode.PerceptualSplit, DualOrbitColorMode.PhaseModulated, DualOrbitColorMode.PerOrbitLayers })
        {
            p.DualOrbitColorMode = m;
            calc.Calculate();
            Assert.True(calc.LastCalculateReusedOrbits, $"{m} re-iterated");
            var fresh = new DualOrbitEscapeCalculator(48, 36) { CenterX = -0.6, Zoom = 1, MaxIterations = 300, FractalParameters = p.Clone(), ColorMap = new RampMap() };
            fresh.Calculate();
            Assert.Equal(fresh.ColorBuffer, calc.ColorBuffer);
        }
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualOrbitColorMode = DualOrbitColorMode.PhaseModulated, DualOrbitPhaseK = -1.25,
            DualOrbitPalette2D = DualOrbitPalette2D.ThemeByLightness, DualOrbitBivariateScale = 35,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.JulibrotPair, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.JulibrotPair, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.JulibrotPair, out _)!.ApplyTo(fresh);
        Assert.Equal(DualOrbitColorMode.PhaseModulated, fresh.DualOrbitColorMode);
        Assert.Equal(-1.25, fresh.DualOrbitPhaseK);
        Assert.Equal(DualOrbitPalette2D.ThemeByLightness, fresh.DualOrbitPalette2D);
        Assert.Equal(35, fresh.DualOrbitBivariateScale);
    }
}
