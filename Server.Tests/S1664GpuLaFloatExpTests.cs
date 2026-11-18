// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1664 — the LA stage evaluator on the float-exp GPU kernel (LaKernelSource.BuildFloatExp,
// the #1630 kernel with no double; method from Imagina's HInfLAEvaluator, GitHub user
// 5E-324, AGPL-3.0, see the bibliography #imagina). Bench-kit scenes as S1630 (fractadyne
// bench-kit, WindySnowOwl, from rith's FF 0.7.1 benchmark report; see the bibliography).
// Invariants:
//   • the float-exp encoding: decodes back to the double within a float rounding, the
//     mantissa normalised to [1, 2), zero at the zero exponent; the packed step table
//     decodes to the double table;
//   • the cbuffer struct is the kernel's 112 bytes;
//   • on the Vulkan device (skipped without one): the float-exp LA frame against the CPU
//     LA frame disagrees no more than the plain float-exp kernel's frame does (the #1331
//     error budget; LA must not make it worse), with the AT (p448) and without (path,
//     3E47); the normals agree;
//   • exact iteration (fixed-point BigInteger) on sampled pixels: at least as good as the
//     plain float-exp frame;
//   • past the GPU deep limit (1e50): p951,094 at 2.1e68 runs on the float-exp LA kernel
//     and agrees with the CPU LA frame on the in-set pixels, with a median relative count
//     difference below 1e-4; its colours differ from CPU LA's about as often as the CPU's
//     own BLA frame's do (the counts run to millions; the fp32 mantissa costs a few
//     hundred iterations there); the plain kernels stay on the CPU there;
//   • the route: only with the switch, only where the float-exp kernel is the device's
//     deep kernel; the label and the route name it.

using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1664FloatExpEncodingTests
{
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(-3.5, 1e-3)]
    [InlineData(1.9999999999999998, -1.9999999999999998)]   // rounds up to 2 as a float: renormalised
    [InlineData(2.3e-68, -7.1e-69)]
    [InlineData(-4.4e300, 1e290)]
    [InlineData(5e-310, 0.0)]                                // subnormal double
    [InlineData(0.0, 0.0)]
    public void A_Complex_Decodes_Back_Within_A_Float_Rounding(double re, double im)
    {
        GpuFloatExp.Encode(re, im, out float mr, out float mi, out int e);
        if (re == 0 && im == 0)
        {
            Assert.Equal(GpuFloatExp.ZeroExp, e);
            Assert.Equal(0f, mr); Assert.Equal(0f, mi);
            return;
        }
        float a = Math.Max(Math.Abs(mr), Math.Abs(mi));
        Assert.InRange(a, 1f, 1.9999999f);
        var (dr, di) = GpuFloatExp.Decode(mr, mi, e);
        double mag = Math.Max(Math.Abs(re), Math.Abs(im));
        Assert.True(Math.Abs(dr - re) <= mag * 1.2e-7 && Math.Abs(di - im) <= mag * 1.2e-7, $"{re},{im} -> {dr},{di}");
    }

    [Fact]
    public void The_Packed_Steps_Decode_To_The_Double_Table()
    {
        // A real table: the p448 view's (CPU only).
        var c = S1664Scenes.Calc(S1664Scenes.P448, null, 64, 36);
        Assert.True(c.LastFrameUsedLa, "precondition: the frame built an LA table");
        var t = c.LaTable!;
        uint[] fx = t.GpuStepsFloatExp;
        double[] d = t.GpuSteps;
        Assert.Equal(d.Length / 8 * LaKernelSource.StepWordsFloatExp, fx.Length);
        static float F(uint u) => BitConverter.UInt32BitsToSingle(u);
        static bool Close(double got, double want, double mag) => Math.Abs(got - want) <= Math.Max(mag, 1e-300) * 1.2e-7;
        for (int i = 0; i < d.Length / 8; i++)
        {
            // #1887 — 13 words: Ref, A, B as float-exp complexes, then Thr, ThrC (was 12, Ref as float).
            int o = i * LaKernelSource.StepWordsFloatExp, q = i * 8;
            foreach (var (m, w) in new[] { (0, 0), (3, 2), (6, 4) })
            {
                var (re, im) = GpuFloatExp.Decode(F(fx[o + m]), F(fx[o + m + 1]), (int)fx[o + m + 2]);
                double mag = Math.Max(Math.Abs(d[q + w]), Math.Abs(d[q + w + 1]));
                Assert.True(Close(re, d[q + w], mag) && Close(im, d[q + w + 1], mag), $"step {i} word {m}");
            }
            foreach (var (m, w) in new[] { (9, 6), (11, 7) })
            {
                var (v, _) = GpuFloatExp.Decode(F(fx[o + m]), 0, (int)fx[o + m + 1]);
                Assert.True(Close(v, d[q + w], d[q + w]), $"step {i} threshold {m}");
            }
        }
    }

    [Fact]
    public void The_Params_Struct_Is_The_Kernels_112_Bytes()
    {
        Assert.Equal(GpuLaFxParams.Bytes, Marshal.SizeOf<GpuLaFxParams>());
        Assert.Equal(112, GpuLaFxParams.Bytes);
        Assert.Equal(108, (int)Marshal.OffsetOf<GpuLaFxParams>(nameof(GpuLaFxParams.Pad0)));
        Assert.Equal(64, (int)Marshal.OffsetOf<GpuLaFxParams>(nameof(GpuLaFxParams.AtThrCM)));
        // No AT: every AT exponent is zero's.
        var p = GpuLaFxParams.Make(8, 8, 1e-60, 100, 4, -4, -4, 10, 1, default);
        Assert.Equal(GpuFloatExp.ZeroExp, p.AtThrCE);
        Assert.Equal(-200, p.ScaleE);
    }
}

internal static class S1664Scenes
{
    public sealed record Scene(string Name, double[] X, double[] Y, double KfZoom, int Iter, double? FfZoomFixed = null);

    private static double[] L(string s) => s.Split('|').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    // From Probes/DeepCostProbe.cs (the bench-kit .kfr centres, conjugated) and S1630.
    public static readonly Scene P448 = new("p448",
        L("-0.7145519151951202|-2.0790192891565988e-17|1.1753166641509792e-33|-8.102457136962753e-50|-1.0894092006534645e-66|-5.481175082318642e-83|-1.5340609125118276e-99|1.4285946495395532e-116"),
        L("-0.35402073332318235|2.5477179573089096e-17|-6.072997541261718e-34|-1.8911088656798612e-50|-4.820603700098655e-67|-3.1189999875481587e-83|1.2339668272603997e-99|-9.480108552256602e-117"),
        5.176068e33, 13440);
    public static readonly Scene Path = new("path",
        L("-0.028041054305504548|1.3292199503012176e-18|-5.27591145479967e-35|2.798737894555097e-51|-1.4491927624390092e-67|-6.233008271562717e-84|4.7734689309385144e-101|2.9022031733306904e-117"),
        L("-0.6948927538996524|5.189489838305484e-17|5.567870094609155e-34|1.7818449158374648e-50|1.8079413574553354e-68|2.5201987988615068e-85|-7.246511953022119e-102|-6.412330137037785e-118"),
        1e33, 2859);
    public static readonly Scene E47 = new("3e47",
        L("-1.9918151296901943|-7.8219844803880472E-17|1.660139930392911E-34|8.217274172159319E-51|0|0|0|0"),
        L("-5.5240415753972429E-06|-2.8659813126937928E-22|6.6910924119662832E-39|6.2394735914401016E-55|0|0|0|0"),
        0, 20000, 3e47);
    public static readonly Scene P951094 = new("p951094",
        L("-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.2033498374722585e-51|2.1867860643118847e-67|-1.2673249633835861e-83|-2.7319253065659894e-100|-1.9376664330122738e-116"),
        L("-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3765984796783024e-50|7.914053078872335e-67|-5.2156113374567495e-83|-1.2800174978852658e-99|-7.991933193373865e-116"),
        2.142891e68, 28532820);

    public static double FfZoom(Scene s, int w, int h) => s.FfZoomFixed ?? s.KfZoom * 3.5 * h / (4.0 * Math.Max(w, h));

    public static MandelbrotCalculator Calc(Scene s, IGpuKernel? k, int w, int h, DeepLaMode mode = DeepLaMode.Auto, Action<MandelbrotCalculator>? setup = null)
    {
        var c = new MandelbrotCalculator(w, h)
        {
            Quality = QualityPreset.Extreme,
            CenterX = s.X[0], CenterXLo = s.X[1], CenterX2 = s.X[2], CenterX3 = s.X[3], CenterX4 = s.X[4], CenterX5 = s.X[5], CenterX6 = s.X[6], CenterX7 = s.X[7],
            CenterY = s.Y[0], CenterYLo = s.Y[1], CenterY2 = s.Y[2], CenterY3 = s.Y[3], CenterY4 = s.Y[4], CenterY5 = s.Y[5], CenterY6 = s.Y[6], CenterY7 = s.Y[7],
            Zoom = FfZoom(s, w, h), MaxIterations = s.Iter, ColorMap = new HsvPalette(), GpuKernel = k, LaMode = mode,
        };
        setup?.Invoke(c);
        c.Calculate(CancellationToken.None);
        return c;
    }
}

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes UseGpuPerturbation / UseGpuLa / UseGpuLaFloatExp / GpuDeepPrecisionMode
public sealed class S1664GpuLaFloatExpTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation, _savedLa = MandelbrotCalculator.UseGpuLa,
        _savedLaFx = MandelbrotCalculator.UseGpuLaFloatExp;
    private readonly GpuDeepPrecision _savedPrecision = MandelbrotCalculator.GpuDeepPrecisionMode;

    public S1664GpuLaFloatExpTests(ITestOutputHelper output)
    {
        _out = output;
        MandelbrotKernelSource.PerturbBudgetMs = 0;   // as S1630: no too-slow abort under load
    }

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        MandelbrotCalculator.UseGpuLa = _savedLa;
        MandelbrotCalculator.UseGpuLaFloatExp = _savedLaFx;
        MandelbrotCalculator.GpuDeepPrecisionMode = _savedPrecision;
    }

    private const int W = 64, H = 36, Bits = 400;

    public static TheoryData<string> Scenes => new() { "p448", "path", "3e47" };
    private static S1664Scenes.Scene ByName(string n) => n switch
    {
        "p448" => S1664Scenes.P448, "path" => S1664Scenes.Path, "p951094" => S1664Scenes.P951094, _ => S1664Scenes.E47,
    };

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        Assert.True(k.SupportsPerturbationLaFloatExp && k.SupportsPerturbationFloatExp);
        return k;
    }

    private static double Disagree(int[] a, int[] b) => a.Zip(b).Count(p => p.First != p.Second) / (double)a.Length;

    // The three frames: CPU LA, the plain float-exp kernel, the float-exp LA kernel.
    private (MandelbrotCalculator Cpu, MandelbrotCalculator Fx, MandelbrotCalculator LaFx) Frames(S1664Scenes.Scene s, VulkanComputeKernel k, int w = W, int h = H)
    {
        MandelbrotCalculator.UseGpuPerturbation = false;
        var cpu = S1664Scenes.Calc(s, null, w, h);
        Assert.True(cpu.LastFrameUsedLa && !cpu.LastFrameUsedGpuPerturbation, "precondition: CPU LA");
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuLa = false;
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Fp32;
        MandelbrotCalculator.UseGpuLaFloatExp = false;
        var fx = S1664Scenes.Calc(s, k, w, h);
        MandelbrotCalculator.UseGpuLaFloatExp = true;
        var laFx = S1664Scenes.Calc(s, k, w, h);
        Assert.True(laFx.LastFrameUsedGpuPerturbation && laFx.LastFrameUsedGpuLaFloatExp && laFx.LastFrameUsedGpuFloatExp,
            $"the float-exp LA kernel did not run: {laFx.LastGpuRoute.Detail}");
        return (cpu, fx, laFx);
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void The_Float_Exp_La_Frame_Is_No_Further_From_Cpu_La_Than_The_Float_Exp_Frame(string name)
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        var s = ByName(name);
        var (cpu, fx, laFx) = Frames(s, k);
        Assert.True(fx.LastFrameUsedGpuFloatExp && !fx.LastFrameUsedGpuLaFloatExp, "precondition: the plain float-exp kernel");
        Assert.EndsWith(" LA", laFx.LastPrecisionLabel);
        Assert.Contains("LA stages", laFx.LastGpuRoute.Detail);
        Assert.Contains("fp32", laFx.LastGpuRoute.Detail);
        double dLa = Disagree(laFx.IterationBuffer, cpu.IterationBuffer), dFx = Disagree(fx.IterationBuffer, cpu.IterationBuffer);
        int InOut(MandelbrotCalculator g) => Enumerable.Range(0, W * H).Count(i => (g.IterationBuffer[i] >= s.Iter) != (cpu.IterationBuffer[i] >= s.Iter));
        _out.WriteLine($"{name}: AT {(cpu.LastLaAt is { } a ? $"stage {a.Stage}" : "none")}; vs CPU LA: float-exp LA {dLa:P2} (in/out {InOut(laFx)}), float-exp {dFx:P2} (in/out {InOut(fx)})");
        Assert.True(dLa <= dFx + 0.01, $"{name}: float-exp LA {dLa:P2} vs float-exp {dFx:P2}");
        Assert.True(InOut(laFx) <= InOut(fx) + W * H / 200, $"{name}: in/out {InOut(laFx)} vs {InOut(fx)}");

        // The derivative: normals against CPU LA where all three frames agree on the
        // count, the float-exp LA frame no further off than the plain float-exp frame
        // (the float derivative's own error, #1331).
        int same = 0, badLa = 0, badFx = 0;
        static bool Off(MandelbrotCalculator g, MandelbrotCalculator c, int i)
            => !(Math.Abs(Math.Atan2(g.NormalYBuffer[i] * c.NormalXBuffer[i] - g.NormalXBuffer[i] * c.NormalYBuffer[i],
                                     g.NormalXBuffer[i] * c.NormalXBuffer[i] + g.NormalYBuffer[i] * c.NormalYBuffer[i])) <= 2e-2);
        for (int i = 0; i < W * H; i++)
        {
            if (laFx.IterationBuffer[i] != cpu.IterationBuffer[i] || fx.IterationBuffer[i] != cpu.IterationBuffer[i] || cpu.IterationBuffer[i] >= s.Iter) continue;
            same++;
            if (Off(laFx, cpu, i)) badLa++;
            if (Off(fx, cpu, i)) badFx++;
        }
        _out.WriteLine($"{name}: normals off by > 0.02 rad: float-exp LA {badLa}, float-exp {badFx}, of {same} px");
        Assert.True(same >= W * H / 8, $"{name}: precondition, escaped pixels to compare ({same})");
        Assert.True(badLa <= badFx + same / 100, $"{name}: normals off at {badLa} (float-exp {badFx}) of {same} px");
    }

    // ── Exact iteration (fixed-point BigInteger) ─────────────────────────────────

    private static BigInteger Fixed(double v)
    {
        if (v == 0) return BigInteger.Zero;
        long b = BitConverter.DoubleToInt64Bits(v);
        int exp = (int)((b >> 52) & 0x7FF);
        long mant = b & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        int shift = exp - 1075 + Bits;
        BigInteger r = shift >= 0 ? new BigInteger(mant) << shift : new BigInteger(mant) >> -shift;
        return v < 0 ? -r : r;
    }

    private static int Exact(BigInteger cr, BigInteger ci, int maxIter)
    {
        BigInteger er2 = new BigInteger(512 * 512) << Bits, zr = 0, zi = 0;
        for (int n = 0; n < maxIter; n++)
        {
            BigInteger zr2 = (zr * zr) >> Bits, zi2 = (zi * zi) >> Bits;
            if (zr2 + zi2 >= er2) return n;
            zi = ((zr * zi) >> (Bits - 1)) + ci;
            zr = zr2 - zi2 + cr;
        }
        return maxIter;
    }

    [Fact]
    public void Exact_Iteration_Is_Matched_At_Least_As_Well_As_By_The_Float_Exp_Frame()
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        var s = S1664Scenes.P448;
        var (_, fx, laFx) = Frames(s, k);
        BigInteger cr = s.X.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l)), ci = s.Y.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l));
        double scale = 3.5 / Math.Max(W, H) / S1664Scenes.FfZoom(s, W, H);
        var rng = new Random(1664);
        var idx = Enumerable.Range(0, W * H).OrderBy(_ => rng.Next()).Take(24).ToArray();
        var ex = new int[idx.Length];
        Parallel.For(0, idx.Length, q =>
        {
            int x = idx[q] % W, y = idx[q] / W;
            ex[q] = Exact(cr + Fixed((x - W * 0.5) * scale), ci + Fixed((y - H * 0.5) * scale), s.Iter);
        });
        int okLa = 0, okFx = 0; double errLa = 0, errFx = 0;
        for (int q = 0; q < idx.Length; q++)
        {
            if (laFx.IterationBuffer[idx[q]] == ex[q]) okLa++;
            if (fx.IterationBuffer[idx[q]] == ex[q]) okFx++;
            errLa += Math.Abs((double)laFx.IterationBuffer[idx[q]] - ex[q]);
            errFx += Math.Abs((double)fx.IterationBuffer[idx[q]] - ex[q]);
        }
        _out.WriteLine($"exact: float-exp LA {okLa}/{idx.Length} (err {errLa:F0}), float-exp {okFx} (err {errFx:F0})");
        Assert.True(okLa >= okFx, $"float-exp LA {okLa} vs float-exp {okFx}");
    }

    [Fact]
    public void Past_The_Gpu_Deep_Limit_Only_The_Float_Exp_La_Kernel_Runs()
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        var s = S1664Scenes.P951094;
        const int w = 32, h = 18;
        Assert.True(S1664Scenes.FfZoom(s, w, h) > MandelbrotCalculator.MaxGpuPerturbZoom, "precondition: past 1e50");
        var (cpu, fx, laFx) = Frames(s, k, w, h);
        Assert.False(fx.LastFrameUsedGpuPerturbation, "the plain float-exp kernel stays within 1e50");
        Assert.Contains("zoom >", fx.LastGpuRoute.Reason ?? "");
        // 3.9-28.5 million iterations per pixel: neither frame is exact there (CPU LA is
        // off exact iteration by thousands, design doc §4.1), so compare the counts'
        // relative difference, and the pixels in the set.
        // The yardstick: how far the CPU's own BLA frame (LA off) is from the CPU LA frame.
        MandelbrotCalculator.UseGpuPerturbation = false;
        var bla = S1664Scenes.Calc(s, null, w, h, DeepLaMode.Off);
        Assert.False(bla.LastFrameUsedLa);
        int inOut = Enumerable.Range(0, w * h).Count(i => (laFx.IterationBuffer[i] >= s.Iter) != (cpu.IterationBuffer[i] >= s.Iter));
        int inSet = cpu.IterationBuffer.Take(w * h).Count(v => v >= s.Iter);
        double[] Rel(MandelbrotCalculator g) => Enumerable.Range(0, w * h).Where(i => cpu.IterationBuffer[i] < s.Iter)
            .Select(i => Math.Abs((double)g.IterationBuffer[i] - cpu.IterationBuffer[i]) / cpu.IterationBuffer[i]).OrderBy(v => v).ToArray();
        var rel = Rel(laFx); var relBla = Rel(bla);
        double Q(double[] r, int pct) => r[r.Length * pct / 100];
        _out.WriteLine($"p951094 {w}x{h}: in set {inSet}, in/out {inOut}; relative count difference from CPU LA: " +
                       $"float-exp LA median {Q(rel, 50):E2} p95 {Q(rel, 95):E2}, CPU BLA median {Q(relBla, 50):E2} p95 {Q(relBla, 95):E2}; GPU {k.LastDispatchMs:F0} ms");
        int ColourOff(MandelbrotCalculator g) => Enumerable.Range(0, w * h).Count(i =>
        {
            uint x = g.ColorBuffer[i], y = cpu.ColorBuffer[i];
            int m = 0;
            for (int sh = 0; sh < 24; sh += 8) m = Math.Max(m, Math.Abs((int)((x >> sh) & 0xFF) - (int)((y >> sh) & 0xFF)));
            return m > 16;
        });
        _out.WriteLine($"pixels whose colour differs from CPU LA by > 16 levels: float-exp LA {ColourOff(laFx)}, CPU BLA {ColourOff(bla)} of {w * h}");
        Assert.True(inSet > 0 && rel.Length > w * h / 2, "precondition: both in-set and escaping pixels");
        Assert.True(inOut <= w * h / 100, $"in/out {inOut}");
        // Measured (GT 710, Vulkan): float-exp LA median 3.4e-5, p95 2.2e-2; CPU BLA 4.4e-6,
        // 7.6e-3. The fp32 mantissa puts the float-exp counts ~8x (median) / ~3x (p95)
        // further from CPU LA than CPU BLA's: a few hundred iterations in millions. At these
        // counts the colour is chaotic for every method (CPU BLA and CPU LA already differ by
        // > 16 levels on most pixels), so the bar is the colour against that yardstick.
        int offLa = ColourOff(laFx), offBla = ColourOff(bla);
        Assert.True(Q(rel, 50) < 1e-4, $"float-exp LA median relative difference {Q(rel, 50):E2}");
        Assert.True(offLa <= offBla + w * h * 15 / 100, $"colour off at {offLa} px (CPU BLA {offBla})");
    }

    [Fact]
    public void The_Route_Needs_The_Switch_And_The_Float_Exp_Kernel()
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        var s = S1664Scenes.P448;
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuLa = false;
        MandelbrotCalculator.UseGpuLaFloatExp = true;
        // The double kernel is the device's deep kernel: no float-exp LA.
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Double;
        var dbl = S1664Scenes.Calc(s, k, W, H);
        Assert.False(dbl.LastFrameUsedGpuLaFloatExp);
        // Float-exp, LA Off: the plain float-exp kernel.
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Fp32;
        var off = S1664Scenes.Calc(s, k, W, H, DeepLaMode.Off);
        Assert.True(off.LastFrameUsedGpuFloatExp && !off.LastFrameUsedGpuLaFloatExp && !off.LastFrameUsedGpuLa);
        // Forced On takes it below 1e30 too.
        var on = S1664Scenes.Calc(s, k, W, H, DeepLaMode.On);
        Assert.True(on.LastFrameUsedGpuLaFloatExp && on.LastFrameUsedGpuLa);
        // The switch off: the plain float-exp kernel.
        MandelbrotCalculator.UseGpuLaFloatExp = false;
        var sw = S1664Scenes.Calc(s, k, W, H);
        Assert.True(sw.LastFrameUsedGpuFloatExp && !sw.LastFrameUsedGpuLaFloatExp);
        // With both LA kernels on, the float-exp one wins where float-exp is the deep kernel.
        MandelbrotCalculator.UseGpuLaFloatExp = true;
        MandelbrotCalculator.UseGpuLa = true;
        var both = S1664Scenes.Calc(s, k, W, H);
        Assert.True(both.LastFrameUsedGpuLaFloatExp);
    }
}
