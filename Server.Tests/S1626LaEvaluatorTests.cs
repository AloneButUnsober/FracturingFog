// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1626 (epic #1623, S3) — the LA stage evaluator (MandelbrotCalculator.UseLaEvaluator;
// method from Imagina's HInfLAEvaluator, GitHub user 5E-324, AGPL-3.0, see the
// bibliography #imagina; maths in Docs/Technical/Period-Synchronised-LA-DesignPlan.md
// §2.4). Checked on the bench-kit minibrot chain (fractadyne bench-kit, WindySnowOwl,
// https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit, from rith's FF 0.7.1
// benchmark report; see the bibliography):
//   • Independent invariant: exact iteration (fixed-point BigInteger, no perturbation)
//     on sampled pixels. The LA frame matches it at least as often as today's BLA
//     frame, and its errors are no larger in total.
//   • Smooth value and normal (z over dz/dc) agree with the acceleration-off frame
//     (plain rebased perturbation, exact derivative) wherever the iteration counts
//     agree: smooth at least as often as the BLA frame, normals on 95 %.
//   • Imagina's minimum-|Z| period test builds stages that cover the reference and
//     render as accurately.
//   • Off, or with acceleration off, the frame is today's.
// Mutants (run by hand against this file): caught — the drop to the next stage one
// step off ("j = s.Next + 1", 6 tests fail), the validity check on w removed (6), and
// "+ B" dropped from the derivative step (the 2 smooth / normal tests). Equivalent by
// construction — a skipped stage keeping its index j instead of mapping it down: a
// stage's first-step δc bound is never above the one below (a composite's ThrC is a
// min over its first part's), so stages are only skipped from the top, at j = 0,
// where every next index is 0. The exact-iteration test asserts that ordering.

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1626LaEvaluatorTests(ITestOutputHelper output)
{
    private const int Bits = 320, W = 64, H = 36;

    private sealed record Scene(string Name, string X, string Y, double KfZoom, int Iter, int Samples);

    // Centres from the kit's .kfr files (as Probes/DeepCostProbe.cs and S1625LaStageTableTests).
    private static readonly Scene P953 = new("p953",
        "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.203746728154813e-51|-4.269596053996866e-68|1.9389949392385534e-84|6.25819023760954e-101|-2.6646795788290983e-117",
        "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3771597857226995e-50|-5.482812918750436e-67|4.692321383846679e-84|-2.313052339214723e-100|2.1139231852108862e-116",
        1.342765e53, 28590, 16);
    private static readonly Scene P15248 = new("p15248",
        "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.198613498328184e-51|-2.6338819725348693e-68|-1.2531186861084395e-84|8.265175494285413e-101|-2.8428099960154152e-117",
        "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3770883977626e-50|-1.6692666809801752e-67|-4.583534842942509e-84|4.2910480785722876e-100|-1.016687227891797e-116",
        2.142891e57, 457440, 8);
    private static readonly Scene P121984 = new("p121984",
        "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.2016807295614754e-51|1.7176952655143277e-67|1.0180672183123456e-83|-1.5782574900160777e-100|8.451632690787226e-117",
        "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3766027088999295e-50|9.932823572218873e-67|2.5207480153941874e-83|-8.913322115858611e-100|2.182658649920256e-116",
        1.682674e66, 3659520, 4);

    public static TheoryData<string> Scenes => new() { "p953", "p15248", "p121984" };
    private static Scene ByName(string n) => n switch { "p953" => P953, "p15248" => P15248, _ => P121984 };

    private static double FfZoom(Scene s) => s.KfZoom * 3.5 * H / (4.0 * Math.Max(W, H));   // #1615 conversion
    private static double Scale(Scene s) => 3.5 / Math.Max(W, H) / FfZoom(s);

    private static MandelbrotCalculator Calc(Scene s, bool la, Action<MandelbrotCalculator>? cfg = null)
    {
        double[] X = s.X.Split('|').Select(double.Parse).ToArray(), Y = s.Y.Split('|').Select(double.Parse).ToArray();
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = X[0], CenterXLo = X[1], CenterX2 = X[2], CenterX3 = X[3], CenterX4 = X[4], CenterX5 = X[5], CenterX6 = X[6], CenterX7 = X[7],
            CenterY = Y[0], CenterYLo = Y[1], CenterY2 = Y[2], CenterY3 = Y[3], CenterY4 = Y[4], CenterY5 = Y[5], CenterY6 = Y[6], CenterY7 = Y[7],
            Zoom = FfZoom(s), MaxIterations = s.Iter,
            ColorMap = new HsvPalette(), UseLaEvaluator = la, LaClimbOnRebase = true, LaTailBla = false,
            LaPeriodTest = LaPeriodTest.ThresholdDip,
        };
        cfg?.Invoke(c);
        c.Calculate();
        return c;
    }

    // ── Exact iteration (fixed-point BigInteger) ─────────────────────────────────

    private static BigInteger Fixed(double d)
    {
        if (d == 0) return BigInteger.Zero;
        long b = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((b >> 52) & 0x7FF);
        long mant = b & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        int shift = exp - 1075 + Bits;
        BigInteger v = shift >= 0 ? new BigInteger(mant) << shift : new BigInteger(mant) >> -shift;
        return d < 0 ? -v : v;
    }

    private static int Exact(BigInteger cr, BigInteger ci, int maxIter)
    {
        BigInteger er2 = new BigInteger(512 * 512) << Bits, zr = 0, zi = 0;   // FF's escape radius
        for (int n = 0; n < maxIter; n++)
        {
            BigInteger zr2 = (zr * zr) >> Bits, zi2 = (zi * zi) >> Bits;
            if (zr2 + zi2 >= er2) return n;
            zi = ((zr * zi) >> (Bits - 1)) + ci;
            zr = zr2 - zi2 + cr;
        }
        return maxIter;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int[] Idx, int[] Iter)> s_exact = new();

    // Distinct sampled pixels and their exact iteration counts (cached per scene).
    private static (int[] Idx, int[] Iter) ExactSamples(Scene s) => s_exact.GetOrAdd(s.Name, _ =>
    {
        BigInteger cr = s.X.Split('|').Select(double.Parse).Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l));
        BigInteger ci = s.Y.Split('|').Select(double.Parse).Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l));
        var rng = new Random(1626);
        var idx = Enumerable.Range(0, W * H).OrderBy(_ => rng.Next()).Take(s.Samples).ToArray();
        var it = new int[idx.Length];
        double scale = Scale(s);
        Parallel.For(0, idx.Length, k =>
        {
            int x = idx[k] % W, y = idx[k] / W;
            it[k] = Exact(cr + Fixed((x - W * 0.5) * scale), ci + Fixed((y - H * 0.5) * scale), s.Iter);
        });
        return (idx, it);
    });

    private static (int Ok, double Err) Score(int[] frame, (int[] Idx, int[] Iter) ex)
    {
        int ok = 0; double err = 0;
        for (int k = 0; k < ex.Idx.Length; k++)
        {
            int v = frame[ex.Idx[k]];
            if (v == ex.Iter[k]) ok++;
            err += Math.Abs((double)v - ex.Iter[k]);
        }
        return (ok, err);
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void La_Matches_Exact_Iteration_At_Least_As_Well_As_Bla(string name)
    {
        var s = ByName(name);
        var bla = Calc(s, la: false);
        var la = Calc(s, la: true);
        // Preconditions: the LA frame really went through the stages, the BLA one did not.
        Assert.True(la.LastFrameUsedLa);
        Assert.False(bla.LastFrameUsedLa);
        var wk = la.LastLaWork;
        Assert.Equal(W * H, wk.Pixels);
        Assert.True(wk.LaSteps > 0);
        var t = la.LaTable!;
        for (int k = 0; k + 1 < t.Stages; k++)
            Assert.True(t.Step(k + 1, 0).ThrC <= t.Step(k, 0).ThrC, $"stage {k + 1}'s first δc bound above stage {k}'s");

        var ex = ExactSamples(s);
        var (laOk, laErr) = Score(la.IterationBuffer, ex);
        var (blaOk, blaErr) = Score(bla.IterationBuffer, ex);
        int diff = la.IterationBuffer.Zip(bla.IterationBuffer).Count(p => p.First != p.Second);
        output.WriteLine($"{name}: exact LA {laOk}/{ex.Idx.Length} (sum |err| {laErr:F0}), BLA {blaOk}/{ex.Idx.Length} (sum |err| {blaErr:F0}); " +
                         $"per px LA {wk.LaSteps / (double)wk.Pixels:F1} + plain {wk.PlainSteps / (double)wk.Pixels:F1} + BLA {wk.BlaSkips / (double)wk.Pixels:F1}; " +
                         $"LA vs BLA frame: {diff} px differ; samples exact/LA/BLA: " +
                         string.Join(" ", ex.Idx.Select((i, k) => $"{ex.Iter[k]}/{la.IterationBuffer[i]}/{bla.IterationBuffer[i]}")));
        Assert.True(laOk >= blaOk, $"{name}: LA {laOk} exact against BLA {blaOk}");
        Assert.True(laErr <= blaErr, $"{name}: LA error {laErr} against BLA {blaErr}");
        // In and out agree with exact iteration on every sample.
        for (int k = 0; k < ex.Idx.Length; k++)
            Assert.Equal(ex.Iter[k] >= s.Iter, la.IterationBuffer[ex.Idx[k]] >= s.Iter);
    }

    // Escaped pixels whose iteration count matches the reference frame's: how many
    // have a smooth value off by more than 1e-3, and a normal (the direction of z/(dz/dc),
    // which needs the derivative; the float distance buffer underflows at these zooms)
    // more than 1e-3 rad off.
    private static (int Same, int SmBad, int NBad, double SmWorst, double NWorst) Agreement(MandelbrotCalculator a, MandelbrotCalculator r, int maxIter)
    {
        int same = 0, smBad = 0, nBad = 0;
        double smWorst = 0, nWorst = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (a.IterationBuffer[i] != r.IterationBuffer[i] || a.IterationBuffer[i] >= maxIter) continue;
            same++;
            double ds = Math.Abs(a.SmoothBuffer[i] - r.SmoothBuffer[i]);
            double dn = Math.Abs(Math.Atan2(a.NormalYBuffer[i] * r.NormalXBuffer[i] - a.NormalXBuffer[i] * r.NormalYBuffer[i],
                                            a.NormalXBuffer[i] * r.NormalXBuffer[i] + a.NormalYBuffer[i] * r.NormalYBuffer[i]));
            smWorst = Math.Max(smWorst, ds); nWorst = Math.Max(nWorst, dn);
            if (ds > 1e-3) smBad++;
            if (dn > 1e-3) nBad++;
        }
        return (same, smBad, nBad, smWorst, nWorst);
    }

    [Theory]
    [InlineData("p953")]
    [InlineData("p15248")]
    public void Smooth_And_Normals_Agree_With_Plain_Rebasing_Where_Iterations_Agree(string name)
    {
        var s = ByName(name);
        var la = Calc(s, la: true);
        var bla = Calc(s, la: false);
        var plain = Calc(s, la: false, c => c.DisableAcceleration = true);   // exact derivative, no skips
        Assert.True(la.LastFrameUsedLa);
        Assert.False(plain.LastFrameUsedLa);
        var l = Agreement(la, plain, s.Iter);
        var b = Agreement(bla, plain, s.Iter);
        output.WriteLine($"{name} against plain rebasing: LA {l.Same} equal-count px, smooth >1e-3 {l.SmBad} (worst {l.SmWorst:G3}), normal >1e-3 rad {l.NBad} (worst {l.NWorst:G3}); " +
                         $"BLA {b.Same}, smooth {b.SmBad} (worst {b.SmWorst:G3}), normal {b.NBad} (worst {b.NWorst:G3})");
        Assert.True(l.Same >= W * H / 4, "precondition: enough escaped pixels agree");
        // The smooth value is no worse than the BLA frame's; the derivative rides through
        // each step exactly (d <- A*2*z_s*d + B), so the normals agree on all but a few
        // pixels (the BLA rows' d <- A*d drops B and misses nearly every normal).
        Assert.True(l.SmBad <= b.SmBad, $"{name}: LA smooth off at {l.SmBad} px, BLA at {b.SmBad}");
        Assert.True(l.NBad <= l.Same / 20, $"{name}: LA normal off at {l.NBad} of {l.Same} px");
    }

    [Fact]
    public void The_MinMagnitude_Period_Test_Covers_The_Reference_And_Renders_As_Accurately()
    {
        var s = P15248;
        var mm = Calc(s, la: true, c => c.LaPeriodTest = LaPeriodTest.MinMagnitude);
        var dip = Calc(s, la: true);
        var t = mm.LaTable!;
        Assert.Equal(LaPeriodTest.MinMagnitude, t.PeriodTest);
        Assert.Equal(LaPeriodTest.ThresholdDip, dip.LaTable!.PeriodTest);
        Assert.True(mm.LastFrameUsedLa);
        output.WriteLine($"min-|Z|: {t.Stages} stages, first lengths {string.Join(", ", Enumerable.Range(0, t.Stages).Select(k => t.Stage(k)[0].Len))}, {t.Data.Length} steps; " +
                         $"dip: {dip.LaTable.Stages} stages, first lengths {string.Join(", ", Enumerable.Range(0, dip.LaTable.Stages).Select(k => dip.LaTable.Stage(k)[0].Len))}, {dip.LaTable.Data.Length} steps");
        for (int k = 0; k < t.Stages; k++)
        {
            long sum = 0;
            foreach (var st in t.Stage(k)) sum += st.Len;
            Assert.Equal(t.RefLen, sum);
        }
        Assert.Equal(15248, t.Stage(t.Stages - 1)[0].Len);   // the top stage is the period here too
        var ex = ExactSamples(s);
        var (mmOk, mmErr) = Score(mm.IterationBuffer, ex);
        var bla = Calc(s, la: false);
        var (blaOk, blaErr) = Score(bla.IterationBuffer, ex);
        output.WriteLine($"exact: min-|Z| {mmOk}/{ex.Idx.Length} (err {mmErr:F0}), BLA {blaOk} (err {blaErr:F0})");
        Assert.True(mmOk >= blaOk && mmErr <= blaErr);
    }

    [Fact]
    public void Off_Or_Without_Acceleration_The_Frame_Is_Todays()
    {
        var s = P953;
        var today = Calc(s, la: false);
        Assert.False(today.LastFrameUsedLa);
        Assert.Null(today.LaTable);
        var noAccel = Calc(s, la: true, c => c.DisableAcceleration = true);
        var noAccelRef = Calc(s, la: false, c => c.DisableAcceleration = true);
        Assert.False(noAccel.LastFrameUsedLa);
        Assert.True(noAccel.IterationBuffer.AsSpan().SequenceEqual(noAccelRef.IterationBuffer));
    }

    [Fact]
    public void Climbing_Back_Off_Still_Matches_Exact_Iteration()
    {
        var s = P953;
        var la = Calc(s, la: true, c => c.LaClimbOnRebase = false);
        var climb = Calc(s, la: true);
        Assert.True(la.LastFrameUsedLa);
        var ex = ExactSamples(s);
        var (ok, _) = Score(la.IterationBuffer, ex);
        var (okC, _) = Score(climb.IterationBuffer, ex);
        output.WriteLine($"climb off: exact {ok}/{ex.Idx.Length}, {la.LastLaWork.LaSteps + la.LastLaWork.PlainSteps + la.LastLaWork.BlaSkips} steps; " +
                         $"climb on: {okC}, {climb.LastLaWork.LaSteps + climb.LastLaWork.PlainSteps + climb.LastLaWork.BlaSkips} steps");
        Assert.Equal(okC, ok);
    }
}
