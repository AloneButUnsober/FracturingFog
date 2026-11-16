// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1614 — per-pixel cost of the LA stages on the high-period minibrots (design doc
// Docs/Technical/Period-Synchronised-LA-DesignPlan.md §4.9). Two FF departures from
// Imagina's HInfLAEvaluator (GitHub user 5E-324, AGPL-3.0, bibliography #imagina):
//   • LaAscend: after a stage-k step that ends where a stage-(k + 1) step begins, the
//     pixel tries that step (LaStageTable.Up). Imagina only ever drops a stage. The idea
//     is BLA's (Zhuoran, bibliography): take the longest valid skip at every index.
//   • LaStageBits = 3: stages 1 and up are cut at round(x^(1/round(log8 x))) instead of
//     Imagina's log16, so there are shorter steps to climb back to.
// Checked on the bench-kit minibrot chain (fractadyne bench-kit, WindySnowOwl,
// https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit, from rith's FF 0.7.1
// benchmark report; see the bibliography):
//   • Independent invariant for Up: a stage-k index maps to a stage-(k + 1) step only
//     when both begin at the same reference iteration (prefix sums of Len), and every
//     such shared boundary is mapped.
//   • The stage tops follow the fixed-length formula for each bits value.
//   • Exact iteration (fixed-point BigInteger) on sampled pixels: the new default matches
//     it at least as often as the BLA rows, with no larger total error, and gets in / out
//     right on every sample; in / out agrees with Imagina's stages over the frame on
//     every scene.
//   • p951,094: fewer LA steps per pixel than Imagina's stages.
// Mutants (run by hand against this file): see the PR.

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1614LaAscendTests(ITestOutputHelper output)
{
    private const int Bits = 320, W = 64, H = 36;

    private sealed record Scene(string Name, string X, string Y, double KfZoom, int Iter, int Samples);

    // Centres from the kit's .kfr files (as Probes/DeepCostProbe.cs and S1626LaEvaluatorTests).
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
        1.682674e66, 3659520, 12);
    private static readonly Scene P951094 = new("p951094",
        "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.2033498374722585e-51|2.1867860643118847e-67|-1.2673249633835861e-83|-2.7319253065659894e-100|-1.9376664330122738e-116",
        "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3765984796783024e-50|7.914053078872335e-67|-5.2156113374567495e-83|-1.2800174978852658e-99|-7.991933193373865e-116",
        2.142891e68, 28532820, 0);

    public static TheoryData<string> ExactScenes => new() { "p953", "p15248", "p121984" };
    public static TheoryData<string> AllScenes => new() { "p953", "p15248", "p121984", "p951094" };
    private static Scene ByName(string n) => n switch
    {
        "p953" => P953, "p15248" => P15248, "p121984" => P121984, _ => P951094,
    };

    private static double FfZoom(Scene s) => s.KfZoom * 3.5 * H / (4.0 * Math.Max(W, H));   // #1615 conversion
    private static double Scale(Scene s) => 3.5 / Math.Max(W, H) / FfZoom(s);

    // Imagina's stages: no climb at shared boundaries, 4 stage bits.
    private static void Imagina(MandelbrotCalculator c) { c.LaAscend = false; c.LaStageBits = LaStageTable.ImaginaStageBits; }

    private static MandelbrotCalculator Calc(Scene s, Action<MandelbrotCalculator>? cfg = null, bool la = true)
    {
        double[] X = s.X.Split('|').Select(double.Parse).ToArray(), Y = s.Y.Split('|').Select(double.Parse).ToArray();
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = X[0], CenterXLo = X[1], CenterX2 = X[2], CenterX3 = X[3], CenterX4 = X[4], CenterX5 = X[5], CenterX6 = X[6], CenterX7 = X[7],
            CenterY = Y[0], CenterYLo = Y[1], CenterY2 = Y[2], CenterY3 = Y[3], CenterY4 = Y[4], CenterY5 = Y[5], CenterY6 = Y[6], CenterY7 = Y[7],
            Zoom = FfZoom(s), MaxIterations = s.Iter,
            ColorMap = new HsvPalette(), UseLaEvaluator = true, LaClimbOnRebase = true, LaTailBla = false,
            LaPeriodTest = LaPeriodTest.ThresholdDip, LaAscend = true, LaStageBits = LaStageTable.DefaultStageBits,
        };
        cfg?.Invoke(c);
        c.Calculate();
        Assert.Equal(la, c.LastFrameUsedLa);
        return c;
    }

    // ── Exact iteration (fixed-point BigInteger), as S1626LaEvaluatorTests ──────────

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

    private static (int[] Idx, int[] Iter) ExactSamples(Scene s) => s_exact.GetOrAdd(s.Name, _ =>
    {
        BigInteger cr = s.X.Split('|').Select(double.Parse).Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l));
        BigInteger ci = s.Y.Split('|').Select(double.Parse).Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l));
        var rng = new Random(1614);
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

    // ── Up ───────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllScenes))]
    public void Up_Maps_Exactly_The_Boundaries_Two_Stages_Share(string name)
    {
        foreach (int bits in new[] { LaStageTable.DefaultStageBits, LaStageTable.ImaginaStageBits })
        {
            var t = Calc(ByName(name), c => c.LaStageBits = bits).LaTable!;
            var up = t.Up;
            Assert.Equal(t.Data.Length, up.Length);
            int mapped = 0;
            for (int k = 0; k < t.Stages; k++)
            {
                // Reference iteration at which each step of stage k begins (prefix sums of Len).
                var startK = Starts(t, k);
                var startUp = k + 1 < t.Stages ? Starts(t, k + 1) : null;
                for (int j = 0; j <= t.StageCount[k]; j++)   // the sentinel included
                {
                    int u = up[t.StageStart[k] + j];
                    if (startUp == null || j == t.StageCount[k])
                    {
                        Assert.Equal(-1, u);
                        continue;
                    }
                    int at = Array.IndexOf(startUp, startK[j]);
                    // A shared boundary is one where a stage-(k + 1) step (not its sentinel) begins.
                    int expect = at >= 0 && at < t.StageCount[k + 1] ? at : -1;
                    Assert.Equal(expect, u);
                    if (u >= 0) mapped++;
                }
            }
            // Every step above stage 0 begins at a stage-below boundary, so each is mapped once.
            int above = Enumerable.Range(1, t.Stages - 1).Sum(k => t.StageCount[k]);
            Assert.Equal(above, mapped);
            output.WriteLine($"{name} bits {bits}: {t.Stages} stages, {mapped} shared boundaries");
        }
    }

    private static long[] Starts(LaStageTable t, int k)
    {
        var a = new long[t.StageCount[k] + 1];
        for (int j = 0; j < t.StageCount[k]; j++) a[j + 1] = a[j] + t.Step(k, j).Len;
        return a;
    }

    // ── Stage bits ───────────────────────────────────────────────────────────────

    // FixedLen(x, b) = round(x^(1/round(log2(x)/b))) on the stage-1 step count; the stage
    // after a fixed-length cut finds the period dip when it is within x64 (design doc §2.3).
    [Theory]
    [InlineData("p953", 4, new[] { 31, 953 })]
    [InlineData("p953", 3, new[] { 31, 953 })]
    [InlineData("p15248", 4, new[] { 31, 953, 15248 })]
    [InlineData("p15248", 3, new[] { 31, 953, 15248 })]
    [InlineData("p121984", 4, new[] { 31, 953, 10483, 121984 })]   // 128 stage-1 steps: round(128^(1/2)) = 11
    [InlineData("p121984", 3, new[] { 31, 953, 10483, 121984 })]   // round(7/3) = 2 as well
    [InlineData("p951094", 4, new[] { 31, 953, 30496, 951094 })]   // 998 steps: round(998^(1/2)) = 32
    [InlineData("p951094", 3, new[] { 31, 953, 9530, 95300, 951094 })]   // round(998^(1/3)) = 10, then round(99.8^(1/2)) = 10
    public void Stage_Tops_Follow_The_Fixed_Length_Rule(string name, int bits, int[] tops)
    {
        var t = Calc(ByName(name), c => c.LaStageBits = bits).LaTable!;
        Assert.Equal(bits, t.StageBits);
        Assert.Equal(tops, Enumerable.Range(0, t.Stages).Select(k => t.Step(k, 0).Len).ToArray());
    }

    // ── Accuracy ─────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ExactScenes))]
    // The epic's yardstick (design doc §4.7, target 2): no worse than the BLA rows against
    // exact iteration. Against Imagina's stages the new default is not always as exact on
    // the chaotic p121,984 (longer steps take fewer exact first iterations; design doc
    // §4.9 measures 28 / 48 against 33 / 48, BLA 12 / 48), so that comparison is reported,
    // not asserted. Where the two build the same steps and take the same ones (p953,
    // p15,248) the frames are identical.
    public void Matches_Exact_Iteration_At_Least_As_Well_As_Bla(string name)
    {
        var s = ByName(name);
        var ff = Calc(s);
        var im = Calc(s, Imagina);
        var bla = Calc(s, c => c.UseLaEvaluator = false, la: false);
        var ex = ExactSamples(s);
        var (ok, err) = Score(ff.IterationBuffer, ex);
        var (okI, errI) = Score(im.IterationBuffer, ex);
        var (okB, errB) = Score(bla.IterationBuffer, ex);
        output.WriteLine($"{name}: exact {ok}/{ex.Idx.Length} err {err:F0} (Imagina's stages {okI}, err {errI:F0}; BLA {okB}, err {errB:F0}); " +
                         $"LA steps {ff.LastLaWork.LaSteps:N0} vs {im.LastLaWork.LaSteps:N0}");
        Assert.True(ok >= okB, $"exact {ok} < BLA {okB}");
        Assert.True(err <= errB, $"error {err} > BLA {errB}");
        for (int k = 0; k < ex.Idx.Length; k++)
            Assert.Equal(ex.Iter[k] >= s.Iter, ff.IterationBuffer[ex.Idx[k]] >= s.Iter);
        if (ff.LastLaWork.LaSteps == im.LastLaWork.LaSteps)
            Assert.True(ff.IterationBuffer.AsSpan().SequenceEqual(im.IterationBuffer));
    }

    [Theory]
    [MemberData(nameof(AllScenes))]
    public void In_Out_Agrees_With_Imagina_Stages_And_Each_Change_Alone(string name)
    {
        var s = ByName(name);
        var im = Calc(s, Imagina);
        foreach (var (label, cfg) in new (string, Action<MandelbrotCalculator>?)[]
                 {
                     ("default", null),
                     ("climb only", c => c.LaStageBits = LaStageTable.ImaginaStageBits),
                     ("3 bits only", c => c.LaAscend = false),
                 })
        {
            var c = Calc(s, cfg);
            int io = 0, diff = 0;
            for (int i = 0; i < W * H; i++)
            {
                if ((c.IterationBuffer[i] >= s.Iter) != (im.IterationBuffer[i] >= s.Iter)) io++;
                if (c.IterationBuffer[i] != im.IterationBuffer[i]) diff++;
            }
            output.WriteLine($"{name} {label}: in/out {io}, counts differ {diff}/{W * H}, LA steps {c.LastLaWork.LaSteps:N0} vs {im.LastLaWork.LaSteps:N0}");
            Assert.Equal(0, io);
        }
    }

    // ── Work ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void P951094_Takes_Fewer_La_Steps_Than_Imagina_Stages()
    {
        var ff = Calc(P951094);
        var asc = Calc(P951094, c => c.LaStageBits = LaStageTable.ImaginaStageBits);
        var im = Calc(P951094, Imagina);
        long f = ff.LastLaWork.LaSteps, a = asc.LastLaWork.LaSteps, i = im.LastLaWork.LaSteps;
        output.WriteLine($"p951094 LA steps: default {f:N0}, climb only {a:N0}, Imagina's stages {i:N0}");
        Assert.True(a < i * 0.9, $"climb only {a} vs {i}");
        Assert.True(f < i * 0.7, $"default {f} vs {i}");
    }
}
