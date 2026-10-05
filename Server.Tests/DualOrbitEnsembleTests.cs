// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1127 (epic #1114 S13) — basin entropy / uncertainty exponent over a c-seed
// ensemble. Oracles: a test-side ensemble with its own plain escape loop
// (entropy); known boundary dimensions in the Julia plane (CxCy slice) — s = 0:
// J = the unit circle (smooth, α → 1); s = −1: the basilica, D ≈ 1.268
// (α ≈ 0.73 < 1); a smooth external RAY between two angle sectors (α → 1);
// entropy 0 inside one basin and ≤ ln(#classes).
public sealed class DualOrbitEnsembleTests
{
    private const int MaxIter = 300;

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF102030u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    // Julia plane: the image is c, s fixed.
    private static FractalParameters Julia(DualOrbitField f, double sx, double sy, Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters
        {
            DualOrbitField = f, DualOrbitSliceAxes = DualOrbitSliceAxes.CxCy,
            DualOrbitSX = sx, DualOrbitSY = sy, DualOrbitBailout = 16,
        };
        tweak?.Invoke(p);
        return p;
    }

    private static DualOrbitEscapeCalculator Render(FractalParameters p, int w, int h, double cx, double cy, double zoom)
    {
        var c = new DualOrbitEscapeCalculator(w, h)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = MaxIter,
            FractalParameters = p, ColorMap = new RampMap(),
        };
        c.Calculate();
        return c;
    }

    private static double PointValue(FractalParameters p, double cx, double cy)
        => Render(p, 2, 2, cx, cy, 1e9).ColorScalarBuffer[3] / (double)MaxIter;

    private static bool Escapes(double x, double y, double sx, double sy)
    {
        for (int n = 0; n < MaxIter; n++)
        {
            if (x * x + y * y > 16 * 16) return true;
            double nx = x * x - y * y + sx; y = 2 * x * y + sy; x = nx;
        }
        return false;
    }

    [Fact]
    public void SeedLayout_IsDeterministic_InTheUnitDisc_AndAreaUniform()
    {
        const int n = 4000;
        var a = Enumerable.Range(0, n).Select(j => DualOrbitEscapeCalculator.EnsembleOffset(j, n)).ToArray();
        var b = Enumerable.Range(0, n).Select(j => DualOrbitEscapeCalculator.EnsembleOffset(j, n)).ToArray();
        Assert.Equal(a, b);
        Assert.All(a, o => Assert.True(o.X * o.X + o.Y * o.Y <= 1.0));
        double inner = a.Count(o => o.X * o.X + o.Y * o.Y <= 0.25) / (double)n;
        Assert.Equal(0.25, inner, 2);   // area-uniform: P(r ≤ ½) = ¼
        double meanX = a.Average(o => o.X), meanY = a.Average(o => o.Y);
        Assert.True(Math.Abs(meanX) < 0.01 && Math.Abs(meanY) < 0.01);
    }

    [Theory]
    [InlineData(0.3, 0.1)]    // inside the unit disc: every seed bounded
    [InlineData(1.6, -0.4)]   // outside: every seed escapes
    public void Entropy_IsZero_InsideOneBasin(double cx, double cy)
    {
        var p = Julia(DualOrbitField.BasinEntropy, 0, 0, q => q.DualOrbitEnsembleRadius = 0.05);
        Assert.True(PointValue(p, cx, cy) < 1e-5);
    }

    [Fact]
    public void Entropy_MatchesATestSideEnsemble()
    {
        const int n = 24; const double rho = 0.03, sx = -1, sy = 0;
        var p = Julia(DualOrbitField.BasinEntropy, sx, sy, q => { q.DualOrbitEnsembleN = n; q.DualOrbitEnsembleRadius = rho; });
        var calc = Render(p, 40, 30, 0.2, 0.0, 1.2);
        double pitch = 4.0 / 40 / 1.2;
        int boundary = 0;
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
            {
                double cx = 0.2 + (x - 20) * pitch, cy = (y - 15) * pitch;
                int esc = 0;
                for (int j = 0; j < n; j++)
                {
                    var (ox, oy) = DualOrbitEscapeCalculator.EnsembleOffset(j, n);
                    if (Escapes(cx + rho * ox, cy + rho * oy, sx, sy)) esc++;
                }
                double S = 0;
                foreach (int k in new[] { esc, n - esc }) if (k > 0) { double q = (double)k / n; S -= q * Math.Log(q); }
                double want = Math.Max(S / Math.Log(2), 1e-3 / MaxIter);
                Assert.Equal(want, calc.ColorScalarBuffer[y * 40 + x] / (double)MaxIter, 5);
                if (S > 0) boundary++;
            }
        Assert.True(boundary > 20, $"only {boundary} boundary boxes");
        Assert.InRange(calc.BasinBoundaryFraction, boundary / 1200.0 - 1e-9, boundary / 1200.0 + 1e-9);
        Assert.True(calc.BasinEntropyBoundaryMean >= calc.BasinEntropyMean);
    }

    [Fact]
    public void Entropy_IsBoundedByLogClassCount_AndUsesTheSectors()
    {
        var p = Julia(DualOrbitField.BasinEntropy, -1, 0, q => { q.DualOrbitEnsembleSectors = 3; q.DualOrbitEnsembleRadius = 0.08; });
        var calc = Render(p, 40, 30, 0.2, 0, 1.0);
        Assert.All(calc.ColorScalarBuffer, v => Assert.True(v <= MaxIter + 1e-3));
        // Some box sees ≥ 3 of the 4 classes: S > ln 2 (scaled by ln 4: > ½).
        Assert.Contains(calc.ColorScalarBuffer, v => v / MaxIter > 0.5 + 1e-6);
    }

    [Fact]
    public void UncertaintyExponent_IsOne_OnTheUnitCircle()
    {
        // s = 0: J is |c| = 1 — a smooth boundary. 2048 pairs, 5 levels.
        var p = Julia(DualOrbitField.UncertaintyExponent, 0, 0, q =>
        { q.DualOrbitEnsembleN = 2048; q.DualOrbitEnsembleRadius = 0.05; q.DualOrbitUncertaintyLevels = 5; });
        double alpha = 2 * PointValue(p, Math.Cos(0.7), Math.Sin(0.7));
        Assert.InRange(alpha, 0.9, 1.1);
    }

    [Fact]
    public void UncertaintyExponent_IsBelowOne_OnTheBasilica()
    {
        // s = −1: β = (1 + √5)/2 is on J (repelling fixed point); D ≈ 1.268.
        var p = Julia(DualOrbitField.UncertaintyExponent, -1, 0, q =>
        { q.DualOrbitEnsembleN = 2048; q.DualOrbitEnsembleRadius = 0.05; q.DualOrbitUncertaintyLevels = 5; });
        double alpha = 2 * PointValue(p, (1 + Math.Sqrt(5)) / 2, 0);
        Assert.InRange(alpha, 0.5, 0.88);
    }

    [Fact]
    public void UncertaintyExponent_IsOne_OnASmoothExternalRay()
    {
        // s = 0, two sectors: the boundary between θ ∈ [0, ½) and [½, 1) at
        // c = 1.6 is the ray θ = 0 — the positive real axis, a straight line.
        var p = Julia(DualOrbitField.UncertaintyExponent, 0, 0, q =>
        { q.DualOrbitEnsembleN = 2048; q.DualOrbitEnsembleRadius = 0.05; q.DualOrbitUncertaintyLevels = 5; q.DualOrbitEnsembleSectors = 2; });
        double alpha = 2 * PointValue(p, 1.6, 0);
        Assert.InRange(alpha, 0.9, 1.1);
    }

    [Fact]
    public void UncertaintyExponent_HasNoValue_AwayFromEveryBoundary()
    {
        var p = Julia(DualOrbitField.UncertaintyExponent, 0, 0, q => q.DualOrbitEnsembleRadius = 0.05);
        var calc = Render(p, 2, 2, 0.3, 0.1, 1e9);
        Assert.Equal(0f, calc.ColorScalarBuffer[3]);
        Assert.Equal(0xFF102030u, calc.ColorBuffer[3]);
    }

    [Fact]
    public void Render_IsDeterministic_AndQuaternionIsInterior()
    {
        var p = Julia(DualOrbitField.BasinEntropy, -1, 0);
        Assert.Equal(Render(p, 32, 24, 0, 0, 1).ColorBuffer, Render(p.Clone(), 32, 24, 0, 0, 1).ColorBuffer);
        var q = Render(new FractalParameters { DualOrbitField = DualOrbitField.BasinEntropy, DualOrbitMap = DualOrbitMap.Quaternion }, 16, 12, -0.5, 0, 1);
        Assert.All(q.ColorBuffer, px => Assert.Equal(0xFF102030u, px));
        Assert.True(double.IsNaN(Render(new FractalParameters(), 8, 8, -0.5, 0, 1).BasinEntropyMean));
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.UncertaintyExponent, DualOrbitEnsembleN = 64,
            DualOrbitEnsembleRadius = 0.005, DualOrbitEnsembleSectors = 3, DualOrbitUncertaintyLevels = 6,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.DualOrbitEscape, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.DualOrbitEscape, out _)!.ApplyTo(fresh);
        Assert.Equal(DualOrbitField.UncertaintyExponent, fresh.DualOrbitField);
        Assert.Equal(64, fresh.DualOrbitEnsembleN);
        Assert.Equal(0.005, fresh.DualOrbitEnsembleRadius);
        Assert.Equal(3, fresh.DualOrbitEnsembleSectors);
        Assert.Equal(6, fresh.DualOrbitUncertaintyLevels);
    }
}
