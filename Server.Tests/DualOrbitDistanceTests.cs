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

// #1118 (epic #1114 S4) — per-orbit distance estimates, dual outline, final-z
// decomposition. Oracles: the classic Mandelbrot / Julia DE formulas in the
// test, a brute-force Koebe probe (points closer than DE must escape), finite
// differences of ln|u_N| for the mixed slices, and test-side escape points.
public sealed class DualOrbitDistanceTests
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

    private static DualOrbitEscapeCalculator Calc(int w, int h, double cx, double cy, double zoom, DualOrbitField f,
        Action<FractalParameters>? tweak = null, int maxIter = 500)
    {
        var p = new FractalParameters { DualOrbitField = f, DualOrbitBailout = Bail };
        tweak?.Invoke(p);
        var calc = new DualOrbitEscapeCalculator(w, h)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIter, FractalParameters = p, ColorMap = new RampMap(),
        };
        calc.Calculate();
        return calc;
    }

    private static DualOrbitEscapeCalculator Point(Complex s, Complex c, DualOrbitField f, DualOrbitSliceAxes axes = DualOrbitSliceAxes.SxSy)
    {
        // Image point = (s) on SxSy, (c) on CxCy.
        var img = axes == DualOrbitSliceAxes.CxCy ? c : s;
        return Calc(2, 2, img.Real, img.Imaginary, 1e9, f, p =>
        {
            p.DualOrbitSliceAxes = axes;
            p.DualOrbitCSeedX = c.Real; p.DualOrbitCSeedY = c.Imaginary;
            p.DualOrbitSX = s.Real; p.DualOrbitSY = s.Imaginary;
        });
    }

    // Classic DE: 0.5·|u|·ln|u| / |du/dp|, p = s (Mandelbrot) or u₀ (Julia).
    private static double ClassicDE(Complex u0, Complex s, bool wrtSeed)
    {
        Complex u = u0, d = wrtSeed ? 1 : 0;
        for (int n = 0; n < 500; n++)
        {
            if (u.Magnitude > Bail) return 0.5 * u.Magnitude * Math.Log(u.Magnitude) / d.Magnitude;
            d = wrtSeed ? 2 * u * d : 2 * u * d + 1;
            u = u * u + s;
        }
        return double.NaN;
    }

    private static Random R(int seed) => new(seed);

    [Fact]
    public void DistanceZ_IsTheMandelbrotDE_DistanceC_IsTheMcDE()
    {
        var r = R(1118); int n = 0;
        for (int t = 0; t < 150; t++)
        {
            var s = new Complex(-2.2 + 3 * r.NextDouble(), -1.3 + 2.6 * r.NextDouble());
            var c = new Complex(-1 + 2 * r.NextDouble(), -1 + 2 * r.NextDouble());
            double ez = ClassicDE(0, s, false), ec = ClassicDE(c, s, false);
            if (double.IsNaN(ez) || double.IsNaN(ec)) continue;
            Assert.True(Math.Abs(Point(s, c, DualOrbitField.DistanceZ).DistanceBuffer[Px] - ez) < 1e-4 * ez + 1e-9, $"z s={s}");
            Assert.True(Math.Abs(Point(s, c, DualOrbitField.DistanceC).DistanceBuffer[Px] - ec) < 1e-4 * ec + 1e-9, $"c s={s} c={c}");
            n++;
        }
        Assert.True(n > 50);
    }

    [Fact]
    public void JuliaSlice_DistanceC_IsTheJuliaDE_DistanceZ_HasNoValue()
    {
        var s = new Complex(-0.12, 0.74);
        var r = R(7); int n = 0;
        for (int t = 0; t < 120; t++)
        {
            var c = new Complex(-1.6 + 3.2 * r.NextDouble(), -1.2 + 2.4 * r.NextDouble());
            double e = ClassicDE(c, s, true);
            if (double.IsNaN(e)) continue;
            Assert.True(Math.Abs(Point(s, c, DualOrbitField.DistanceC, DualOrbitSliceAxes.CxCy).DistanceBuffer[Px] - e) < 1e-4 * e + 1e-9, $"c={c}");
            n++;
        }
        Assert.True(n > 30);
        var z = Point(s, new Complex(1.5, 0.5), DualOrbitField.DistanceZ, DualOrbitSliceAxes.CxCy);
        Assert.Equal(0xFF102030u, z.ColorBuffer[Px]);
    }

    // Koebe: the estimate is a lower bound on the distance to the set, so every
    // point closer than DE to an exterior pixel escapes too.
    [Theory]
    [InlineData(DualOrbitField.DistanceZ, DualOrbitSliceAxes.SxSy)]
    [InlineData(DualOrbitField.DistanceC, DualOrbitSliceAxes.SxSy)]
    [InlineData(DualOrbitField.DistanceC, DualOrbitSliceAxes.CxCy)]
    public void Estimate_IsConservative(DualOrbitField f, DualOrbitSliceAxes axes)
    {
        var s0 = new Complex(-0.12, 0.74); var c0 = new Complex(0.5, 0.1);
        var calc = Calc(48, 36, axes == DualOrbitSliceAxes.CxCy ? 0 : -0.6, 0, 1, f, p =>
        {
            p.DualOrbitSliceAxes = axes; p.DualOrbitSX = s0.Real; p.DualOrbitSY = s0.Imaginary;
            p.DualOrbitCSeedX = c0.Real; p.DualOrbitCSeedY = c0.Imaginary;
        });
        double pitch = calc.DistancePixelScale;
        int probed = 0;
        for (int y = 0; y < 36; y += 3)
            for (int x = 0; x < 48; x += 3)
            {
                double de = calc.DistanceBuffer[y * 48 + x];
                if (de < 2 * pitch) continue;
                var center = new Complex((axes == DualOrbitSliceAxes.CxCy ? 0 : -0.6) + (x - 24) * pitch, (y - 18) * pitch);
                for (int k = 0; k < 16; k++)
                {
                    var q = center + Complex.FromPolarCoordinates(0.95 * de, 2 * Math.PI * k / 16);
                    Complex s = axes == DualOrbitSliceAxes.CxCy ? s0 : q;
                    Complex u = f == DualOrbitField.DistanceZ ? 0 : axes == DualOrbitSliceAxes.CxCy ? q : c0;
                    bool escaped = false;
                    for (int i = 0; i < 20000 && !escaped; i++) { u = u * u + s; escaped = u.Magnitude > 2 && u.Magnitude > Math.Sqrt(s.Magnitude) + 2; }
                    Assert.True(escaped, $"{f}/{axes}: point {q} within DE {de} of pixel ({x},{y}) did not escape");
                }
                probed++;
            }
        Assert.True(probed > 20, $"probed {probed}");
    }

    // Mixed slice (CxSx): DE = ½·ln|u| / |∇ln|u||, ∇ from central differences of
    // ln|u_N| at a fixed N (independent of the analytic derivative code).
    [Fact]
    public void MixedSlice_UsesTheImageGradient()
    {
        const double cy = 0.15, sy = 0.2;
        var r = R(3); int n = 0;
        for (int t = 0; t < 120; t++)
        {
            double cx = -1.2 + 2.4 * r.NextDouble(), sx = -2 + 2.5 * r.NextDouble();
            int N = EscapeN(cx, cy, sx, sy);
            if (N < 3 || N > 60) continue;
            const double h = 1e-7;
            double gx = (LogU(cx + h, sx, N) - LogU(cx - h, sx, N)) / (2 * h);
            double gy = (LogU(cx, sx + h, N) - LogU(cx, sx - h, N)) / (2 * h);
            double expect = 0.5 * LogU(cx, sx, N) / Math.Sqrt(gx * gx + gy * gy);
            var calc = Calc(2, 2, cx, sx, 1e9, DualOrbitField.DistanceC, p =>
            {
                p.DualOrbitSliceAxes = DualOrbitSliceAxes.CxSx; p.DualOrbitCSeedY = cy; p.DualOrbitSY = sy;
            });
            Assert.True(Math.Abs(calc.DistanceBuffer[Px] - expect) < 1e-4 * expect, $"cx={cx} sx={sx}: {calc.DistanceBuffer[Px]} vs {expect}");
            n++;
        }
        Assert.True(n > 30);

        static int EscapeN(double cx, double cy, double sx, double sy)
        {
            Complex u = new(cx, cy), s = new(sx, sy);
            for (int k = 0; k < 500; k++) { if (u.Magnitude > Bail) return k; u = u * u + s; }
            return -1;
        }
        double LogU(double cx, double sx, int N)
        {
            Complex u = new(cx, cy), s = new(sx, sy);
            for (int k = 0; k < N; k++) u = u * u + s;
            return Math.Log(u.Magnitude);
        }
    }

    // DualOutline: far from both boundaries the base colour shows; on a boundary
    // the line colour takes over.
    [Fact]
    public void DualOutline_InksBothBoundaries()
    {
        var calc = Calc(96, 72, -0.6, 0, 1, DualOrbitField.DualOutline);
        double pitch = calc.DistancePixelScale, w = 1.5;
        var map = new RampMap { MaxIterations = 500 };
        int far = 0, onZ = 0;
        for (int i = 0; i < calc.ColorBuffer.Length; i++)
        {
            double dz = calc.DistanceBuffer[i] / pitch;
            if (calc.SmoothBuffer[i] > 2 && dz > w + 1)
            {
                // no c line either unless near M_c: compare where the c-orbit also escapes far away
                uint c = calc.ColorBuffer[i];
                uint baseCol = unchecked((uint)map.Map(calc.SmoothBuffer[i], 0f, 500));
                if (c == baseCol) far++;
            }
            if (calc.DistanceBuffer[i] > 0 && dz < 0.2) { Assert.True(Near(calc.ColorBuffer[i], 0xFF0072B2u, 40), $"px {i}"); onZ++; }
        }
        Assert.True(far > 500, $"far {far}");
        Assert.True(onZ > 3, $"onZ {onZ}");

        static bool Near(uint a, uint b, int tol)
            => Enumerable.Range(0, 3).All(k => Math.Abs((int)((a >> (8 * k)) & 0xFF) - (int)((b >> (8 * k)) & 0xFF)) <= tol);
    }

    [Fact]
    public void BinaryXor_AndFinalAngleDelta_MatchTheEscapePoints()
    {
        var r = R(11); int n = 0;
        for (int t = 0; t < 150; t++)
        {
            var s = new Complex(-2.2 + 3 * r.NextDouble(), -1.3 + 2.6 * r.NextDouble());
            var c = new Complex(-1 + 2 * r.NextDouble(), -1 + 2 * r.NextDouble());
            Complex? ez = Escape(0, s), ec = Escape(c, s);
            if (ez == null || ec == null) continue;
            bool x = (ez.Value.Imaginary >= 0) ^ (ec.Value.Imaginary >= 0);
            var xc = Point(s, c, DualOrbitField.BinaryXor);
            Assert.Equal(x ? 1f : 1e-3f, xc.SmoothBuffer[Px]);
            Assert.Equal(x ? 0xFF0072B2u : 0xFFE69F00u, xc.ColorBuffer[Px]);   // categorical class colours
            double d = (ec.Value.Phase - ez.Value.Phase) / (2 * Math.PI); d -= Math.Floor(d);
            Assert.True(Math.Abs(Point(s, c, DualOrbitField.FinalAngleDelta).SmoothBuffer[Px] - Math.Max(1e-3, d * 500)) < 1e-2, $"s={s}");
            n++;
        }
        Assert.True(n > 50);

        static Complex? Escape(Complex u, Complex s)
        {
            for (int k = 0; k < 500; k++) { if (u.Magnitude > Bail) return u; u = u * u + s; }
            return null;
        }
    }

    [Fact]
    public void OnlyDistanceFields_FillTheDistanceBuffer()
    {
        Assert.NotEmpty(Calc(16, 12, -0.6, 0, 1, DualOrbitField.DistanceZ).DistanceBuffer);
        Assert.Empty(Calc(16, 12, -0.6, 0, 1, DualOrbitField.GreenRatio).DistanceBuffer);
        var q = Calc(16, 12, -0.6, 0, 1, DualOrbitField.DistanceZ, p => p.DualOrbitMap = DualOrbitMap.Quaternion);
        Assert.All(q.ColorBuffer, c => Assert.Equal(0xFF102030u, c));
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.DualOutline, DualOrbitOutlineWidth = 2.5, DualOrbitDEScale = 3,
            DualOrbitOutlineColorZ = 0xFF112233u, DualOrbitOutlineColorC = 0xFF445566u,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.JulibrotPair, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.JulibrotPair, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.JulibrotPair, out _)!.ApplyTo(fresh);
        Assert.Equal(DualOrbitField.DualOutline, fresh.DualOrbitField);
        Assert.Equal(2.5, fresh.DualOrbitOutlineWidth);
        Assert.Equal(3, fresh.DualOrbitDEScale);
        Assert.Equal(0xFF112233u, fresh.DualOrbitOutlineColorZ);
        Assert.Equal(0xFF445566u, fresh.DualOrbitOutlineColorC);
    }
}
