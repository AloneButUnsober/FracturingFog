// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1782 — a BLA skip moves δ' = A·δ + B·dc with A, B independent of dc, so the derivative
// is d' = A·d + B. Every BLA path dropped the + B, which put the normals and the distance
// estimate off on about half of a deep frame's pixels. Checked against the plain
// perturbation frame (DisableAcceleration, LA off: the exact derivative each step) on the
// escaped pixels with equal iteration counts, per BLA route:
//   • the scalar PT row, the AVX2 4-lane rows and the 8-lane (Vector512) rows, at p448
//     (5e33) and 3E47;
//   • the LA tail's BLA skips (LaTailBla), at 3E47;
//   • the float-exp rows' BLA (#1845) and the float-exp LA tail (LaTailBla), at 1e320;
//   • the GPU SA / BLA kernel (Vulkan; skipped without a device), at p448.
// Normals within 1e-2 rad and the distance within 1 % on ≥ 99 % of those pixels. Before
// the fix the rows were off on about half. The GPU kernel: ≥ 94 % (230 of 5,535 off, from
// 2,882): the rest is its BLA skips differing from the CPU's on the same table, #1791.
// Mutants (scripted, one site each without its + B): caught — the scalar row, the FX row,
// the AVX2 rows, the 8-lane rows, the GPU kernel. Survive — the LA tail's and the FX LA
// tail's BLA, though the tails take 18k–157k skips here: |B| / |A·d| is about 1 / |dz/dc|
// at the skip, and the tails start after the LA stages, where |dz/dc| is already huge.
// The rows' first skips follow SA at iteration ~16, where it is O(1): there the + B is half
// the frame. The tails are fixed alike; on these views their B is below the tolerances.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes ForceScalarPtPath / ForcePt8Rows / UseGpu*
public sealed class S1782BlaDerivativeTests(ITestOutputHelper output) : IDisposable
{
    private const int W = 96, H = 64;
    private readonly bool _scalar = MandelbrotCalculator.ForceScalarPtPath, _pt8 = MandelbrotCalculator.ForcePt8Rows,
        _gp = MandelbrotCalculator.UseGpuPerturbation, _sa = MandelbrotCalculator.UseGpuSeriesApproximation,
        _bla = MandelbrotCalculator.UseGpuBla, _la = MandelbrotCalculator.UseGpuLa;
    private readonly double _budget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly GpuDeepPrecision _prec = MandelbrotCalculator.GpuDeepPrecisionMode;

    public void Dispose()
    {
        MandelbrotCalculator.ForceScalarPtPath = _scalar;
        MandelbrotCalculator.ForcePt8Rows = _pt8;
        MandelbrotCalculator.UseGpuPerturbation = _gp;
        MandelbrotCalculator.UseGpuSeriesApproximation = _sa;
        MandelbrotCalculator.UseGpuBla = _bla;
        MandelbrotCalculator.UseGpuLa = _la;
        MandelbrotCalculator.GpuDeepPrecisionMode = _prec;
        MandelbrotKernelSource.PerturbBudgetMs = _budget;
    }

    // S1660's views.
    private sealed record View(string Name, double Zoom, double[] Cx, double[] Cy, int MaxIter);
    private static readonly View P448 = new("p448 5e33", 5.176068e33 * 3.5 * H / (4.0 * W),
        [-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50],
        [-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50], 13440);
    private static readonly View E47 = new("3E47", 3e47,
        [-1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51],
        [-5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55], 20000);

    private static MandelbrotCalculator Frame(View v, Action<MandelbrotCalculator> setup, IGpuKernel? k = null)
    {
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterX2 = v.Cx[2], CenterX3 = v.Cx[3],
            CenterY = v.Cy[0], CenterYLo = v.Cy[1], CenterY2 = v.Cy[2], CenterY3 = v.Cy[3],
            Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = new HsvPalette(), GpuKernel = k,
        };
        setup(c);
        c.Calculate(CancellationToken.None);
        return c;
    }

    // The exact-derivative reference: no SA, no BLA, no LA.
    private static void Plain(MandelbrotCalculator c) { c.DisableAcceleration = true; c.LaMode = DeepLaMode.Off; }

    private void Compare(string name, MandelbrotCalculator want, MandelbrotCalculator got, int maxIter, double minShare = 0.99)
    {
        int same = 0, badN = 0, badD = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (got.IterationBuffer[i] >= maxIter || got.IterationBuffer[i] != want.IterationBuffer[i]) continue;
            same++;
            float gx = got.NormalXBuffer[i], gy = got.NormalYBuffer[i], wx = want.NormalXBuffer[i], wy = want.NormalYBuffer[i];
            double ang = Math.Abs(Math.Atan2(gy * wx - gx * wy, gx * wx + gy * wy));
            if (!(ang <= 1e-2)) badN++;
            float gd = got.DistanceBuffer[i], wd = want.DistanceBuffer[i];
            if (!(Math.Abs(gd - wd) <= 1e-2 * Math.Abs(wd))) badD++;
        }
        output.WriteLine($"{name}: {same} escaped px with equal counts; normals off at {badN}, distance off at {badD}");
        Assert.True(same >= W * H / 8, $"{name}: precondition, pixels to compare ({same})");
        int allow = (int)(same * (1 - minShare));
        Assert.True(badN <= allow && badD <= allow, $"{name}: normals off at {badN}, distance at {badD} of {same}");
    }

    public enum Rows { Scalar, Avx2, Pt8 }

    [Theory]
    [InlineData(Rows.Scalar, "p448")]
    [InlineData(Rows.Avx2, "p448")]
    [InlineData(Rows.Pt8, "p448")]
    [InlineData(Rows.Scalar, "3e47")]
    [InlineData(Rows.Avx2, "3e47")]
    [InlineData(Rows.Pt8, "3e47")]
    public void The_Bla_Rows_Carry_The_Derivative(Rows rows, string view)
    {
        var v = view == "p448" ? P448 : E47;
        MandelbrotCalculator.UseGpuPerturbation = false;
        MandelbrotCalculator.ForceScalarPtPath = rows == Rows.Scalar;
        MandelbrotCalculator.ForcePt8Rows = rows == Rows.Pt8;
        var want = Frame(v, Plain);
        var got = Frame(v, c => c.LaMode = DeepLaMode.Off);   // BLA (and SA) on, LA off
        Compare($"{rows}/{v.Name}", want, got, v.MaxIter);
    }

    // The LA tail's BLA (LaTailBla). Without the climb back to the stages after a rebase
    // (LaClimbOnRebase off) the tail runs long enough to take BLA skips on these views;
    // the skip count is checked first so the comparison can't pass on stage steps alone.
    [Theory]
    [InlineData("p448")]
    [InlineData("3e47")]
    public void The_La_Tail_Bla_Carries_The_Derivative(string view)
    {
        var v = view == "p448" ? P448 : E47;
        MandelbrotCalculator.UseGpuPerturbation = false;
        var want = Frame(v, Plain);
        var got = Frame(v, c => { c.LaMode = DeepLaMode.On; c.LaTailBla = true; c.LaClimbOnRebase = false; });
        output.WriteLine($"LA tail/{v.Name}: {got.LastLaWork}");
        Assert.True(got.LastLaWork.BlaSkips > 100, $"precondition: the LA tail took BLA skips ({got.LastLaWork.BlaSkips})");
        Compare($"LA tail BLA/{v.Name}", want, got, v.MaxIter);
    }

    // ── Float-exp rows (past 1e290) ──────────────────────────────────────────

    // S1783 / S1843's view: c = i (a Misiurewicz point) plus offsets within a pixel, zoom 1e320 (a view far from the set has distances past float's range in pixels).
    private static MandelbrotCalculator FxFrame(Action<MandelbrotCalculator> setup)
    {
        const int zl = 320;
        int prec = BigFloat.PrecisionForZoom(zl);
        var centre = new ExactCentre(BigFloat.Parse("3.1415926535e-" + (zl + 1), prec),
            BigFloat.Add(BigFloat.FromDouble(1, prec), BigFloat.Parse("2.7182818284e-" + (zl + 1), prec), prec));
        var d = centre.ToDeep();
        var c = new MandelbrotCalculator(W, H)
        {
            MaxIterations = 600 + 4 * zl, ColorMap = new HsvPalette(),
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        c.SetZoomLog10(zl);
        c.SetDepth(centre);
        setup(c);
        c.Calculate(CancellationToken.None);
        return c;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_Float_Exp_Bla_Carries_The_Derivative(bool laTail)
    {
        MandelbrotCalculator.UseGpuPerturbation = false;
        var want = FxFrame(Plain);
        var got = FxFrame(c => { c.LaMode = laTail ? DeepLaMode.On : DeepLaMode.Off; c.LaTailBla = laTail; c.LaClimbOnRebase = !laTail; });
        Assert.Contains("FX", got.LastPrecisionLabel);
        if (laTail)
        {
            output.WriteLine($"FX LA tail: {got.LastLaWork}");
            Assert.True(got.LastLaWork.BlaSkips > 100, $"precondition: the FX LA tail took BLA skips ({got.LastLaWork.BlaSkips})");
        }
        Compare(laTail ? "FX LA tail BLA/1e320" : "FX BLA rows/1e320", want, got, got.MaxIterations);
    }

    // ── GPU SA / BLA kernel ─────────────────────────────────────────────────

    [Fact]
    public void The_Gpu_Bla_Kernel_Carries_The_Derivative()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        using var _ = k;
        if (!k.SupportsPerturbationSA) Assert.Skip("the Vulkan device lacks the SA / BLA kernel");
        MandelbrotKernelSource.PerturbBudgetMs = 0;
        MandelbrotCalculator.UseGpuPerturbation = false;
        var want = Frame(P448, Plain);
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuSeriesApproximation = true;
        MandelbrotCalculator.UseGpuBla = true;
        MandelbrotCalculator.UseGpuLa = false;
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Double;
        var got = Frame(P448, c => c.LaMode = DeepLaMode.Off, k);
        Assert.True(got.LastFrameUsedGpuBla, $"precondition: the GPU BLA kernel ran ({got.LastGpuRoute.Detail})");
        // 230 of 5,535 pixels stay off (4 %): the GPU kernel takes other BLA skips than the
        // CPU rows on the same table (it is the same 230 against the CPU SA + BLA frame, its
        // SA alone is off on 4, a tighter tolerance leaves 35). That is #1791. Without the
        // + B it was 2,882 (52 %).
        Compare("GPU SA+BLA/p448", want, got, P448.MaxIter, minShare: 0.94);
    }
}
