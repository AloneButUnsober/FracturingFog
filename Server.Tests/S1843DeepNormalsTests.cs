// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1843 — FillAuxAndColorHP took dz/dc in complex-plane units, ~1/pixel:
//   • FillNormal squared Re(z·conj(dz/dc)): ∞ past ~1e150, so the normals were 0 / NaN;
//   • FinalDr/Di (float) overflowed past ~1e38;
//   • past ~1e308 the double derivative itself was ±∞ (FloatExp.ToDouble on the FX rows),
//     so the normals and the distance (#1783) were lost there too.
// Now dz/dc enters in the distance unit (· 2^-DistanceUnitExp2, O(1 / pixel) there),
// from the FloatExp derivative before it becomes a double. Checked against z and dz/dc
// iterated directly in BigFloat (S1783's oracle) at 1e150 and 1e280 (double δ) and 1e320
// and 1e600 (float-exp rows):
//   • the normal's direction (exact under the power-of-two scaling) within 1e-4 rad;
//   • FinalDr/Di = dz/dc · 2^-e, finite, within 1e-4 (LA's derivative: ~2e-5 worst);
//   • the distance in pixels within 1e-4 (past 1e308 it was 0 before);
// and a colour map that doesn't opt in to the distance unit still gets dz/dc in
// complex-plane units (a float ∞ here, as before), one that does gets the unit.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1843DeepNormalsTests(ITestOutputHelper output)
{
    private const int W = S1783DeepDistanceUnitTests.W, H = S1783DeepDistanceUnitTests.H;

    // z at escape and dz/dc · pixel (both doubles), iterated directly in BigFloat (bailout
    // 512); null if the point did not escape.
    private static (double Zr, double Zi, double DPr, double DPi)? Direct(
        ExactCentre centre, double ox, double oy, BigFloat pixel, int prec, int maxIter)
    {
        var cr = BigFloat.Add(centre.Re, BigFloat.Mul(pixel, BigFloat.FromDouble(ox, prec), prec), prec);
        var ci = BigFloat.Add(centre.Im, BigFloat.Mul(pixel, BigFloat.FromDouble(oy, prec), prec), prec);
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        BigFloat dr = BigFloat.Zero(prec), di = BigFloat.Zero(prec);
        var one = BigFloat.FromDouble(1, prec);
        for (int n = 0; n < maxIter; n++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= 512.0 * 512.0)
                return (a, b, BigFloat.Mul(dr, pixel, prec).ToDouble(), BigFloat.Mul(di, pixel, prec).ToDouble());
            var ndr = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr, dr, prec), BigFloat.Mul(zi, di, prec), prec).ScaleB(1), one, prec);
            var ndi = BigFloat.Add(BigFloat.Mul(zr, di, prec), BigFloat.Mul(zi, dr, prec), prec).ScaleB(1);
            dr = ndr; di = ndi;
            var zr2 = BigFloat.Mul(zr, zr, prec); var zi2 = BigFloat.Mul(zi, zi, prec); var zri = BigFloat.Mul(zr, zi, prec);
            zi = BigFloat.Add(zri.ScaleB(1), ci, prec);
            zr = BigFloat.Add(BigFloat.Sub(zr2, zi2, prec), cr, prec);
        }
        return null;
    }

    [Theory]
    [InlineData(150, 3)]
    [InlineData(280, 3)]
    [InlineData(320, 3)]
    [InlineData(600, 4)]
    public void Normals_Final_Derivative_And_Distance_Match_Direct_Iteration(int zl, int stride)
    {
        var (calc, centre, prec) = S1783DeepDistanceUnitTests.Render(zl);
        var pixel = BigFloat.Div(BigFloat.Parse($"3.5e-{zl}", prec), BigFloat.FromDouble(W, prec), prec);
        double ps = calc.DistancePixelScale;   // the pixel in the distance unit, [1, 2)
        Assert.True(ps >= 1 && ps < 2, $"pixel scale in the distance unit: {ps:R}");
        if (zl > 290) Assert.EndsWith(" FX", calc.LastPrecisionLabel);   // precondition: the float-exp rows

        int total = 0, badN = 0, badD = 0, badDist = 0; double worstAng = 0, worstD = 0;
        for (int y = 0; y < H; y += stride)
        for (int x = 0; x < W; x += stride)
        {
            int i = y * W + x;
            var r = Direct(centre, x - W / 2, y - H / 2, pixel, prec, calc.MaxIterations);
            if (r is not { } d || calc.IterationBuffer[i] >= calc.MaxIterations) continue;
            total++;
            // The normal: (u, v) = (Re, Im) of z · conj(dz/dc), as FillNormal; any positive
            // scale of dz/dc gives the same direction.
            double u = d.Zr * d.DPr + d.Zi * d.DPi, v = d.Zi * d.DPr - d.Zr * d.DPi, m = Math.Sqrt(u * u + v * v);
            float nx = calc.NormalXBuffer[i], ny = calc.NormalYBuffer[i];
            double ang = Math.Abs(Math.Atan2(ny * (u / m) - nx * (v / m), nx * (u / m) + ny * (v / m)));
            worstAng = Math.Max(worstAng, ang);
            if (!(ang < 1e-4) || !(Math.Abs(nx * nx + ny * ny - 1) < 1e-5)) badN++;
            // dz/dc in the distance unit: dz/dc · 2^-e = (dz/dc · pixel) / ps.
            double wr = d.DPr / ps, wi = d.DPi / ps;
            float fr = calc.FinalDrBuffer[i], fi = calc.FinalDiBuffer[i];
            double rel = Math.Sqrt((fr - wr) * (fr - wr) + (fi - wi) * (fi - wi)) / Math.Sqrt(wr * wr + wi * wi);
            worstD = Math.Max(worstD, rel);
            if (!float.IsFinite(fr) || !float.IsFinite(fi) || !(rel < 1e-4)) badD++;   // LA's derivative: ~2e-5 at 1e150
            // The distance in pixels (#1783), now past 1e308 too.
            double mag = Math.Sqrt(d.Zr * d.Zr + d.Zi * d.Zi);
            double want = mag * Math.Log(mag) / Math.Sqrt(d.DPr * d.DPr + d.DPi * d.DPi);
            double got = calc.DistanceBuffer[i] / ps;
            if (!(Math.Abs(got / want - 1) < 1e-4)) badDist++;
        }
        output.WriteLine($"1e{zl} [{calc.LastPrecisionLabel}] unit 2^-{calc.DistanceUnitExp2}: {total} px; normals worst {worstAng:E2} rad ({badN} bad), " +
            $"dz/dc worst {worstD:E2} ({badD} bad), distance {badDist} bad");
        Assert.True(total >= 20, $"precondition: escaped pixels ({total})");
        Assert.Equal(0, badN);
        Assert.Equal(0, badD);
        Assert.Equal(0, badDist);
    }

    // The map's dz/dc: complex-plane units for a theme that doesn't opt in (|dz/dc| ~ 1e151
    // here, a float ∞, as before #1843), the distance unit (~1e1 … 1e5) for one that does.
    [Fact]
    public void A_Map_Gets_Dz_In_Its_Own_Units()
    {
        var raw = InterpretedColorMap.TryCreate("return rgb(abs(dzr) + abs(dzi) > 1e30 ? 1.0 : 0.0, 0.0, 0.0);", null, out var e1);
        var unit = InterpretedColorMap.TryCreate("let k = pxScale;\nreturn rgb(abs(dzr) + abs(dzi) > 1e30 ? 1.0 : 0.0, 0.0, 0.0);", null, out var e2);
        Assert.True(raw != null && unit != null, $"{e1} {e2}");
        var (calc, _, _) = S1783DeepDistanceUnitTests.Render(150);
        foreach (var (map, wantRed) in new[] { (raw!, true), (unit!, false) })
        {
            calc.ColorMap = map;
            calc.Calculate(CancellationToken.None);
            int esc = 0, red = 0;
            for (int i = 0; i < W * H; i++)
            {
                if (calc.IterationBuffer[i] >= calc.MaxIterations) continue;
                esc++;
                if (((calc.ColorBuffer[i] >> 16) & 0xFF) > 128) red++;
            }
            output.WriteLine($"{(wantRed ? "raw" : "pxScale")} theme: {red} of {esc} escaped px see |dz/dc| > 1e30");
            Assert.True(esc >= 100, $"precondition: escaped pixels ({esc})");
            Assert.Equal(wantRed ? esc : 0, red);
        }
    }
}

// #1843 on the GPU float-exp pixel kernel (#1882): it writes dz/dc · 2^scaleE, which went
// back to a double (±∞ past ~1e308) before the normals. Now straight into the distance
// unit. Against the CPU frame (the derivative in FloatExp) on the escaped pixels with equal
// counts: normals within 1e-2 rad, distance within 1 %, FinalDr/Di finite (fp32 kernel).
[Collection(MandelbrotGpuStaticsCollection.Name)]
public sealed class S1843GpuFloatExpNormalsTests(ITestOutputHelper output) : IDisposable
{
    private const int W = 24, H = 24;
    private readonly double _budget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _gp = MandelbrotCalculator.UseGpuPerturbation, _la = MandelbrotCalculator.UseGpuLa,
        _laFx = MandelbrotCalculator.UseGpuLaFloatExp;
    private readonly GpuDeepPrecision _prec = MandelbrotCalculator.GpuDeepPrecisionMode;

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _budget;
        MandelbrotCalculator.UseGpuPerturbation = _gp;
        MandelbrotCalculator.UseGpuLa = _la;
        MandelbrotCalculator.UseGpuLaFloatExp = _laFx;
        MandelbrotCalculator.GpuDeepPrecisionMode = _prec;
    }

    // S1882's view: c = i (a Misiurewicz point).
    private static MandelbrotCalculator Render(int zl, IGpuKernel? k)
    {
        int prec = BigFloat.PrecisionForZoom(zl);
        var centre = new ExactCentre(BigFloat.Zero(prec), BigFloat.FromDouble(1, prec));
        var d = centre.ToDeep();
        var calc = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme, MaxIterations = 600 + 4 * zl, ColorMap = new HsvPalette(), GpuKernel = k,
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.SetZoomLog10(zl);
        calc.SetDepth(centre);
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    [Theory]
    [InlineData(320)]
    [InlineData(600)]
    public void Gpu_Float_Exp_Normals_And_Distance_Match_The_Cpu_Past_Double(int zl)
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        var k = FracturingFog.Rendering.Vulkan.VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        using var _ = k;
        if (!k.SupportsPerturbationLaFloatExp) Assert.Skip("the Vulkan device lacks the float-exp LA kernel");
        MandelbrotKernelSource.PerturbBudgetMs = 0;
        MandelbrotCalculator.UseGpuPerturbation = false;
        var cpu = Render(zl, null);
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuLa = false;
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Fp32;
        MandelbrotCalculator.UseGpuLaFloatExp = true;
        var gpu = Render(zl, k);
        Assert.True(gpu.LastFrameUsedGpuFloatExpPixels, $"1e{zl}: the float-exp pixel kernel did not run: {gpu.LastGpuRoute.Detail}");
        Assert.False(cpu.LastFrameUsedGpuPerturbation);

        int max = cpu.MaxIterations, same = 0, badN = 0, badD = 0, notFinite = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (gpu.IterationBuffer[i] >= max || gpu.IterationBuffer[i] != cpu.IterationBuffer[i]) continue;
            same++;
            float gx = gpu.NormalXBuffer[i], gy = gpu.NormalYBuffer[i], gd = gpu.DistanceBuffer[i];
            if (!float.IsNormal(gd) || !float.IsFinite(gpu.FinalDrBuffer[i]) || !float.IsFinite(gpu.FinalDiBuffer[i])
                || !(gx * gx + gy * gy > 0.99f)) { notFinite++; continue; }
            float cx = cpu.NormalXBuffer[i], cy = cpu.NormalYBuffer[i], cd = cpu.DistanceBuffer[i];
            double ang = Math.Abs(Math.Atan2(gy * cx - gx * cy, gx * cx + gy * cy));
            if (!(ang <= 1e-2)) badN++;
            if (!(Math.Abs(gd - cd) <= 1e-2 * Math.Abs(cd))) badD++;
        }
        output.WriteLine($"1e{zl}: {same} escaped px with equal counts; {notFinite} without a normal / distance / finite dz; normals off at {badN}, distance at {badD}");
        Assert.True(same >= W * H / 4, $"precondition: pixels to compare ({same})");
        Assert.Equal(0, notFinite);
        Assert.True(badN <= same / 20 && badD <= same / 20, $"normals off at {badN}, distance at {badD} of {same}");
    }
}