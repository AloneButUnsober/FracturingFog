// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1601 — Talbot transverse-profile overlay. Oracles: the Ronchi coefficients written
// out here (cₙ = a·sinc(πna)) summed directly, independent of the calculator's
// Coefficients / Field; the half-period shift at ζ = ½ (Talbot's shifted self-image);
// a straight line for the box counter; and the measured dimension's direction of
// travel: the square-wave planes stay near 1, irrational planes rise above them and
// keep rising with the number of modes (Berry & Klein; Berry's box gives 3/2).
public sealed class TalbotSliceTests
{
    private const double Duty = 0.3;
    private static readonly double Golden = (3 - Math.Sqrt(5)) / 2;

    private static TalbotCarpetCalculator Render(FractalParameters p, int w = 96, int h = 64, double cx = 0.1, double cy = 0.5, double zoom = 1.9)
    {
        var c = new TalbotCarpetCalculator(w, h) { CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = 400, ColorMap = new HsvPalette(), FractalParameters = p };
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static double OwnIntensity(int m, double xi, double zeta)
    {
        double re = 0, im = 0;
        for (int n = -m; n <= m; n++)
        {
            double cn = n == 0 ? Duty : Math.Sin(Math.PI * n * Duty) / (Math.PI * n);
            double ph = 2 * Math.PI * (n * xi - (double)n * n * zeta);
            re += cn * Math.Cos(ph); im += cn * Math.Sin(ph);
        }
        return re * re + im * im;
    }

    private static double Dim(int m, double zeta)
    {
        var c = TalbotCarpetCalculator.Coefficients(new FractalParameters { TalbotModes = m, TalbotDuty = Duty });
        return TalbotCarpetCalculator.GraphDimension(TalbotCarpetCalculator.PeriodProfile(c, zeta, TalbotCarpetCalculator.ProfileSamples(m)), m);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.3819660112501051)]
    [InlineData(1.7071)]
    public void Overlay_IsTheDirectSum_DrawnWhereItShouldBe(double zeta)
    {
        int w = 96, h = 64;
        var r = Render(new FractalParameters { TalbotModes = 24, TalbotDuty = Duty, TalbotSliceOverlay = true, TalbotSliceZeta = zeta }, w, h, cy: zeta + 0.05);
        double pitch = (4.0 / w) / 1.9;
        double peak = 0;
        for (int j = 0; j < 512; j++) peak = Math.Max(peak, OwnIntensity(24, j / 512.0, zeta));
        Assert.True(Math.Abs(r.SliceMax - peak) <= 1e-9 * peak, $"peak {r.SliceMax} vs {peak}");
        double baseRow = (zeta - (zeta + 0.05)) / pitch + h * 0.5;
        Assert.Equal(baseRow, r.SliceBaseRow, 9);
        int band = h / 4;
        for (int x = 0; x < w; x++)
        {
            double i = OwnIntensity(24, 0.1 + (x - w * 0.5) * pitch, zeta);
            Assert.True(Math.Abs(r.SliceProfile[x] - i) <= 1e-9 * Math.Max(1, i), $"x={x}: {r.SliceProfile[x]} vs {i}");
            int row = (int)Math.Round(Math.Clamp(Math.Clamp(baseRow, band, h - 1) - Math.Min(1, i / peak) * band, 0, h - 1));
            Assert.Equal(0xFFFFFFFFu, r.ColorBuffer[row * w + x]);
        }
        // The dashed marker sits on the ζ₀ row: both white and black dashes appear on it.
        int mr = (int)Math.Floor(baseRow);
        var marker = Enumerable.Range(0, w).Select(x => r.ColorBuffer[mr * w + x]).ToArray();
        Assert.Contains(0xFFFFFFFFu, marker);
        Assert.Contains(0xFF000000u, marker);
    }

    [Fact]
    public void Overlay_TouchesOnlyColour_AndOffIsByteIdentical()
    {
        var plain = Render(new FractalParameters { TalbotModes = 24 });
        var off = Render(new FractalParameters { TalbotModes = 24, TalbotSliceZeta = 0.2 });
        Assert.Equal(plain.ColorBuffer, off.ColorBuffer);
        Assert.Null(off.LastPrecisionLabel);
        var on = Render(new FractalParameters { TalbotModes = 24, TalbotSliceOverlay = true, TalbotSliceZeta = 0.2 });
        Assert.Equal(plain.SmoothBuffer, on.SmoothBuffer);   // Relief heights untouched
        Assert.NotEqual(plain.ColorBuffer, on.ColorBuffer);
        Assert.Contains("graph dim", on.LastPrecisionLabel);
        // Changed pixels are only the overlay's white / black.
        for (int i = 0; i < plain.ColorBuffer.Length; i++)
            if (plain.ColorBuffer[i] != on.ColorBuffer[i])
                Assert.True(on.ColorBuffer[i] is 0xFFFFFFFFu or 0xFF000000u);
    }

    [Fact]
    public void HalfTalbotPlane_IsTheGratingShiftedByHalfAPeriod()
    {
        var c = TalbotCarpetCalculator.Coefficients(new FractalParameters { TalbotModes = 64, TalbotDuty = Duty });
        int s = TalbotCarpetCalculator.ProfileSamples(64);
        var y0 = TalbotCarpetCalculator.PeriodProfile(c, 0.0, s);
        var yh = TalbotCarpetCalculator.PeriodProfile(c, 0.5, s);
        var y1 = TalbotCarpetCalculator.PeriodProfile(c, 1.0, s);
        for (int j = 0; j < s; j++)
        {
            Assert.Equal(y0[(j + s / 2) % s], yh[j], 9);
            Assert.Equal(y0[j], y1[j], 9);
        }
    }

    [Fact]
    public void BoxCounter_GivesOneForALine()
    {
        var line = Enumerable.Range(0, 1024).Select(j => j / 1024.0).ToArray();
        Assert.InRange(TalbotCarpetCalculator.GraphDimension(line, 256), 0.98, 1.05);
        Assert.Equal(1.0, TalbotCarpetCalculator.GraphDimension(new double[1024], 256));
        Assert.True(double.IsNaN(TalbotCarpetCalculator.GraphDimension(line, 1)),
            "M = 1 is smooth below 1/2: no box sizes between 1/8 and 1/(2M)");
    }

    // A unit step, 0 on the first half of the period and 1 on the second: every column
    // of width ε is flat (one box) except the two that hold a jump (the middle, and the
    // wrap back to 0), which need 1/ε + 1 boxes each. So N(ε) = 1/ε − 2 + 2(1/ε + 1) = 3/ε.
    [Fact]
    public void BoxCounts_OfAStep_AreExactlyThreeOverEpsilon()
    {
        var step = Enumerable.Range(0, 1024).Select(j => j < 512 ? 0.0 : 1.0).ToArray();
        var (eps, boxes) = TalbotCarpetCalculator.BoxCounts(step, 256);
        Assert.Equal(new[] { 1 / 8.0, 1 / 16.0, 1 / 32.0, 1 / 64.0, 1 / 128.0, 1 / 256.0, 1 / 512.0 }, eps);
        for (int i = 0; i < eps.Length; i++) Assert.Equal(3.0 / eps[i], boxes[i], 9);
    }

    [Fact]
    public void Dimension_IsLowAtTalbotPlanes_AndRisesAtIrrationalDistances()
    {
        const int m = 1024;
        double d0 = Dim(m, 0.0), dh = Dim(m, 0.5), d1 = Dim(m, 1.0);
        double dGolden = Dim(m, Golden), dSilver = Dim(m, Math.Sqrt(2) - 1), dThird = Dim(m, 1.0 / 3);
        Assert.All(new[] { d0, dh, d1 }, d => Assert.InRange(d, 0.98, 1.2));
        Assert.True(dGolden > d0 + 0.2 && dSilver > d0 + 0.2, $"golden {dGolden}, √2−1 {dSilver}, ζ=0 {d0}");
        Assert.True(dGolden < 1.5 && dSilver < 1.5, "a finite-mode estimate stays below the 3/2 limit");
        Assert.True(dThird < dGolden - 0.1, $"ζ = 1/3 (three shifted copies) {dThird} vs golden {dGolden}");
        // More modes: the irrational profile gets rougher, the rational one smoother.
        Assert.True(Dim(1024, Golden) > Dim(32, Golden) + 0.03);
        Assert.True(Dim(1024, 1.0 / 3) < Dim(32, 1.0 / 3) - 0.1);
    }

    [Fact]
    public void Settings_RoundTripThroughTheCommandBuilder()
    {
        var p = new FractalParameters { TalbotSliceOverlay = true, TalbotSliceZeta = 0.4142 };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.TalbotCarpet, Parameters = p, FamilyParams = RegionFractalParams.Snapshot(FractalType.TalbotCarpet, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.TalbotCarpet, out _)!.ApplyTo(fresh);
        Assert.True(fresh.TalbotSliceOverlay);
        Assert.Equal(0.4142, fresh.TalbotSliceZeta);
        var clone = p.Clone();
        Assert.True(clone.TalbotSliceOverlay);
        Assert.Equal(0.4142, clone.TalbotSliceZeta);
    }
}
