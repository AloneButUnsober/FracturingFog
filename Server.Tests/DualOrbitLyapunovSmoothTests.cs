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

// #1144 — SecantLyapunov / PairWinding smoothed across the escape step. Oracles:
// a test-side loop (window sums, smooth counts, the blend); exact continuity at
// a band edge located by bisection on the test-side step count; a scan whose
// largest pixel-to-pixel jump collapses with smoothing; bounded pairs untouched.
public sealed class DualOrbitLyapunovSmoothTests
{
    private const double Bail = 128, Span = 50;   // wide span: no clamping, scalar linear in λ
    private const int MaxIter = 300;
    private static readonly Complex C0 = new(0.3, 0.2);

    // Window: steps k < N while both orbits are inside the bailout; T = min smooth count.
    private static (int n, double sum, double last, double wind, double lastW, double t) Loop(Complex s, Complex c0)
    {
        Complex z = 0, c = c0;
        double sum = 0, last = 0, wind = 0, lastW = 0;
        int n = 0;
        double sz = double.NaN, sc = double.NaN;
        for (int k = 0; k < MaxIter; k++)
        {
            bool ze = z.Magnitude > Bail, ce = c.Magnitude > Bail;
            if (ze || ce)
            {
                double Sm(Complex u) => k - Math.Log(Math.Log(u.Magnitude) / Math.Log(Bail)) / Math.Log(2);
                if (ze) sz = Sm(z);
                if (ce) sc = Sm(c);
                // the other orbit may escape later; its smooth count is ≥ this one's
                n = k;
                break;
            }
            var sg = z + c;
            last = Math.Log(sg.Magnitude); sum += last;
            lastW = sg.Phase / (2 * Math.PI); wind += lastW;
            z = z * z + s; c = c * c + s;
            n = k + 1;
        }
        double t = double.IsNaN(sz) ? sc : double.IsNaN(sc) ? sz : Math.Min(sz, sc);
        return (n, sum, last, wind, lastW, t);
    }

    private static double Field(DualOrbitField f, Complex s, bool smooth)
    {
        Assert.True(DualOrbitEscapeCalculator.TryFieldAt(f, C0.Real, C0.Imaginary, s.Real, s.Imaginary, MaxIter, Bail, Span, 2, out double v, smooth));
        return v;
    }

    private static double Lambda(double scalar) => (scalar / MaxIter - 0.5) * 2 * Span;

    [Theory]
    [InlineData(-1.1, 1.25)]
    [InlineData(0.45, 0.6)]
    [InlineData(-0.2, 1.4)]
    public void Blend_MatchesTheTestSideLoop(double sx, double sy)
    {
        var s = new Complex(sx, sy);
        var (n, sum, last, wind, lastW, t) = Loop(s, C0);
        Assert.True(n >= 2 && !double.IsNaN(t));
        double w = Math.Clamp(t - (n - 1), 0, 1);
        double want = w * sum / n + (1 - w) * (sum - last) / (n - 1);
        Assert.Equal(want, Lambda(Field(DualOrbitField.SecantLyapunov, s, true)), 6);
        Assert.Equal(sum / n, Lambda(Field(DualOrbitField.SecantLyapunov, s, false)), 6);   // off = original
        double wantW = 0.5 * MaxIter + wind - (1 - w) * lastW;
        Assert.Equal(wantW, Field(DualOrbitField.PairWinding, s, true), 6);
    }

    // Locate a band edge (the window length N changes) by bisection along a line;
    // the smoothed field must meet itself there, the raw field jumps.
    [Theory]
    [InlineData(DualOrbitField.SecantLyapunov)]
    [InlineData(DualOrbitField.PairWinding)]
    public void SmoothedField_IsContinuousAcrossABandEdge(DualOrbitField f)
    {
        int edges = 0;
        // Just right of the cusp s = ¼: escape counts pile up as s → ¼⁺, so the
        // line crosses many band edges.
        double y = 0.02;
        double prevX = 1.5; int prevN = Loop(new Complex(prevX, y), C0).n;
        for (double x = 1.5 - 0.005; x >= 0.27 && edges < 6; x -= 0.005)
        {
            int nx = Loop(new Complex(x, y), C0).n;
            if (nx != prevN)
            {
                double a = prevX, b = x;
                for (int i = 0; i < 60; i++)
                {
                    double m = 0.5 * (a + b);
                    if (Loop(new Complex(m, y), C0).n == prevN) a = m; else b = m;
                }
                var sa = new Complex(a, y); var sb = new Complex(b, y);
                double smoothJump = Math.Abs(Field(f, sa, true) - Field(f, sb, true));
                double rawJump = Math.Abs(Field(f, sa, false) - Field(f, sb, false));
                Assert.True(smoothJump < 1e-4 * Math.Max(1, rawJump), $"{f} at x = {a}: smoothed jump {smoothJump} (raw {rawJump})");
                Assert.True(rawJump > 1e3 * smoothJump || rawJump < 1e-9);
                edges++;
            }
            prevX = x; prevN = nx;
        }
        Assert.True(edges >= 3, $"only {edges} band edges crossed");
    }

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF102030u;
        public int Map(float smooth, float distance, int iterations) => unchecked((int)0xFF000000u);
    }

    private static float[] Row(bool smooth)
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.SecantLyapunov, DualOrbitLyapunovSmooth = smooth, DualOrbitLyapunovSpan = Span,
            DualOrbitBailout = Bail, DualOrbitCSeedX = C0.Real, DualOrbitCSeedY = C0.Imaginary,
        };
        var c = new DualOrbitEscapeCalculator(2000, 1) { CenterX = -0.3, CenterY = 1.3, Zoom = 1.5, MaxIterations = MaxIter, FractalParameters = p, ColorMap = new RampMap() };
        c.Calculate();
        return (float[])c.ColorScalarBuffer.Clone();
    }

    // A 2000-pixel scan across the escape bands: the raw field's largest jump is
    // a band edge; smoothed, the largest step is the field's own slope.
    [Fact]
    public void Scan_LargestJump_CollapsesWithSmoothing()
    {
        static double MaxJump(float[] r) => Enumerable.Range(1, r.Length - 1).Max(i => Math.Abs(r[i] - r[i - 1]));
        double raw = MaxJump(Row(false)), sm = MaxJump(Row(true));
        Assert.True(sm < 0.2 * raw, $"max jump smoothed {sm} vs raw {raw}");
    }

    [Fact]
    public void BoundedPairs_AreUnchanged()
    {
        var s = new Complex(-0.2, 0.1);   // main cardioid; c-orbit bounded too
        Assert.Equal(Field(DualOrbitField.SecantLyapunov, s, false), Field(DualOrbitField.SecantLyapunov, s, true));
    }

    // Quaternion map in the C_i subalgebra is the complex map (S15): the smoothed
    // values must agree too (the quaternion pair runner records StopT as well).
    [Theory]
    [InlineData(0.4, 0.3)]
    [InlineData(0.7, -0.5)]
    [InlineData(1.1, 0.6)]
    public void Quaternion_MatchesComplex_InCi_WithSmoothing(double s, double c)
    {
        float Render(bool quat)
        {
            var p = new FractalParameters
            {
                DualOrbitField = DualOrbitField.SecantLyapunov, DualOrbitLyapunovSmooth = true, DualOrbitLyapunovSpan = Span,
                DualOrbitMap = quat ? DualOrbitMap.Quaternion : DualOrbitMap.ComplexPlane, DualOrbitSZ = 0, DualOrbitCSeedZ = 0,
                DualOrbitCSeedX = quat ? c : 0, DualOrbitCSeedY = quat ? 0 : c,
            };
            var k = new DualOrbitEscapeCalculator(2, 2) { CenterX = quat ? s : 0, CenterY = quat ? 0 : s, Zoom = 1e9, MaxIterations = MaxIter, FractalParameters = p, ColorMap = new RampMap() };
            k.Calculate();
            return k.ColorScalarBuffer[3];
        }
        float q = Render(true), z = Render(false);
        Assert.True(Math.Abs(q - z) <= 1e-3 * (1 + Math.Abs(z)), $"quaternion {q} vs complex {z}");
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters { DualOrbitField = DualOrbitField.SecantLyapunov, DualOrbitLyapunovSmooth = true };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.DualOrbitEscape, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.DualOrbitEscape, out _)!.ApplyTo(fresh);
        Assert.True(fresh.DualOrbitLyapunovSmooth);
    }
}
