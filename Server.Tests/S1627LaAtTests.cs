// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1627 (epic #1623, S4) — the approximation transform (FFMath.LaAt; method from
// Imagina's HInfLAEvaluator, GitHub user 5E-324, AGPL-3.0, see the bibliography
// #imagina; maths in Docs/Technical/Period-Synchronised-LA-DesignPlan.md §2.5) in the
// engine's LA evaluator. Checked on the bench-kit scenes (fractadyne bench-kit,
// WindySnowOwl, https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit, from
// rith's FF 0.7.1 benchmark report; see the bibliography):
//   • Independent invariant: exact iteration (fixed-point BigInteger) on sampled
//     pixels. With the AT the frame matches it at least as often as BLA, and as often
//     as the stages without the AT.
//   • Interior pixels reach the cap without drift: in / out is identical with the AT
//     on and off over the whole frame, and interior samples are interior exactly.
//   • The derivative survives the AT: normals agree with plain rebasing.
//   • The AT is used where its bounds allow (p953 every pixel; the path scene only its centre,
//     its δc bound being below a pixel) and not on the deepest scenes, whose top-stage
//     escape radius is under 2.
//   • FF's precision margin (design doc §2.6 item 10): Imagina's would take p15,248's
//     stage-1 AT, whose view is 1/2,700 of c''s offset. Against exact smooth values on
//     the pixels where that frame and the stages-only frame disagree, the stages-only
//     frame is closer on most of them, so FF's default rejects that AT.
// Mutants (run by hand against this file): the AT's δc check removed, the
// renormalised escape test removed, and the conversion back without 1/A — see the
// design doc §4.3 for which tests fail.

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1627LaAtTests(ITestOutputHelper output)
{
    private const int Bits = 320, W = 64, H = 36;

    private sealed record Scene(string Name, string X, string Y, double KfZoom, int Iter, int Samples);

    // Centres from the kit's .kfr files (as Probes/DeepCostProbe.cs).
    private static readonly Scene Path = new("path",
        "-0.028041054305504548|1.3292199503012176e-18|-5.27591145479967e-35|2.798737894555097e-51|-1.4491927624390092e-67|-6.233008271562717e-84|4.7734689309385144e-101|2.9022031733306904e-117",
        "-0.6948927538996524|5.189489838305484e-17|5.567870094609155e-34|1.7818449158374648e-50|1.8079413574553354e-68|2.5201987988615068e-85|-7.246511953022119e-102|-6.412330137037785e-118",
        1e33, 2859, 16);
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
        1.682674e66, 3659520, 0);

    public static TheoryData<string> AtScenes => new() { "path", "p953" };
    private static Scene ByName(string n) => n switch { "path" => Path, "p953" => P953, "p15248" => P15248, _ => P121984 };

    private static double FfZoom(Scene s) => s.KfZoom * 3.5 * H / (4.0 * Math.Max(W, H));   // #1615 conversion
    private static double Scale(Scene s) => 3.5 / Math.Max(W, H) / FfZoom(s);

    private static MandelbrotCalculator Calc(Scene s, bool la, bool at = true, Action<MandelbrotCalculator>? cfg = null)
    {
        double[] X = s.X.Split('|').Select(double.Parse).ToArray(), Y = s.Y.Split('|').Select(double.Parse).ToArray();
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = X[0], CenterXLo = X[1], CenterX2 = X[2], CenterX3 = X[3], CenterX4 = X[4], CenterX5 = X[5], CenterX6 = X[6], CenterX7 = X[7],
            CenterY = Y[0], CenterYLo = Y[1], CenterY2 = Y[2], CenterY3 = Y[3], CenterY4 = Y[4], CenterY5 = Y[5], CenterY6 = Y[6], CenterY7 = Y[7],
            Zoom = FfZoom(s), MaxIterations = s.Iter,
            ColorMap = new HsvPalette(), UseLaEvaluator = la, UseLaAt = at, LaAtLoosen = 1.0, LaAtOffsetBits = 0,
            LaClimbOnRebase = true, LaTailBla = false, LaPeriodTest = LaPeriodTest.ThresholdDip,
        };
        cfg?.Invoke(c);
        c.Calculate();
        return c;
    }

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
        var rng = new Random(1627);
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
    [MemberData(nameof(AtScenes))]
    public void The_At_Matches_Exact_Iteration(string name)
    {
        var s = ByName(name);
        var at = Calc(s, la: true);
        var noAt = Calc(s, la: true, at: false);
        var bla = Calc(s, la: false);
        Assert.True(at.LastFrameUsedLa);
        Assert.NotNull(at.LastLaAt);   // precondition: each of these scenes has a usable AT
        Assert.Null(noAt.LastLaAt);
        var aw = at.LastLaAtWork;
        // Where the AT's δc bound covers the view every pixel takes it; on the path
        // scene the bound is below a pixel, so only the centre pixel (δc = 0) does.
        if (name == "path") Assert.InRange(aw.Pixels, 0, 4);
        else Assert.Equal(W * H, aw.Pixels);

        var ex = ExactSamples(s);
        var (okA, errA) = Score(at.IterationBuffer, ex);
        var (okN, errN) = Score(noAt.IterationBuffer, ex);
        var (okB, errB) = Score(bla.IterationBuffer, ex);
        var lat = at.LastLaAt!.Value;
        int diff = at.IterationBuffer.Zip(noAt.IterationBuffer).Count(p => p.First != p.Second);
        output.WriteLine($"{name}: AT stage {lat.Stage} (step {lat.Len}, esc r {Math.Sqrt(lat.Esc2):G3}, δc bound {lat.ThrC:G3}, pixel {Scale(s):G3}), " +
                         $"{aw.Pixels} px, {aw.Steps / (double)(W * H):F1} AT steps/px, LA {at.LastLaWork.LaSteps / (double)(W * H):F1} steps/px " +
                         $"(without AT {noAt.LastLaWork.LaSteps / (double)(W * H):F1}); exact AT {okA}/{ex.Idx.Length} (err {errA:F0}), " +
                         $"no AT {okN} (err {errN:F0}), BLA {okB} (err {errB:F0}); {diff} px differ from the no-AT frame");
        Assert.True(okA >= okB && errA <= errB, $"{name}: AT {okA} (err {errA}) against BLA {okB} (err {errB})");
        Assert.True(okA >= okN && errA <= errN, $"{name}: AT {okA} (err {errA}) against no AT {okN} (err {errN})");
    }

    [Fact]
    public void Interior_Pixels_Reach_The_Cap_Without_Drift()
    {
        var s = P953;
        var at = Calc(s, la: true);
        var noAt = Calc(s, la: true, at: false);
        Assert.NotNull(at.LastLaAt);
        int interior = 0;
        for (int i = 0; i < W * H; i++)
        {
            bool a = at.IterationBuffer[i] >= s.Iter, b = noAt.IterationBuffer[i] >= s.Iter;
            Assert.Equal(b, a);
            Assert.True(at.IterationBuffer[i] <= s.Iter);
            if (a) interior++;
        }
        output.WriteLine($"{interior} interior px of {W * H}; AT steps/px {at.LastLaAtWork.Steps / (double)(W * H):F1}");
        Assert.True(interior > 0, "precondition: interior pixels in view");
        var ex = ExactSamples(s);
        Assert.Contains(ex.Iter, v => v == s.Iter);   // precondition: an interior sample
        for (int k = 0; k < ex.Idx.Length; k++)
            Assert.Equal(ex.Iter[k] >= s.Iter, at.IterationBuffer[ex.Idx[k]] >= s.Iter);
        // An interior pixel finishes in the AT: about maxIter / L steps each.
        var lat = at.LastLaAt!.Value;
        Assert.True(at.LastLaAtWork.Steps >= (long)interior * ((s.Iter - 1) / lat.Len));
    }

    // Exact iteration with the final z, for the smooth value FF computes.
    private static double ExactSmooth(BigInteger cr, BigInteger ci, int maxIter)
    {
        BigInteger er2 = new BigInteger(512 * 512) << Bits, zr = 0, zi = 0;
        for (int n = 0; n < maxIter; n++)
        {
            BigInteger zr2 = (zr * zr) >> Bits, zi2 = (zi * zi) >> Bits;
            if (zr2 + zi2 >= er2)
            {
                double mag = Math.Sqrt((double)((zr2 + zi2) >> (Bits - 40)) / Math.Pow(2, 40));
                return n + 1.0 - Math.Log2(Math.Log2(mag));
            }
            zi = ((zr * zi) >> (Bits - 1)) + ci;
            zr = zr2 - zi2 + cr;
        }
        return double.NaN;
    }

    [Fact]
    public void Imaginas_Margin_Admits_An_At_That_Loses_Precision_So_Ffs_Rejects_It()
    {
        var s = P15248;
        var ff = Calc(s, la: true);                                        // FF's margin
        var im = Calc(s, la: true, cfg: c => c.LaAtOffsetBits = LaAt.ImaginaOffsetBits);
        var none = Calc(s, la: true, at: false);
        Assert.Null(ff.LastLaAt);
        Assert.NotNull(im.LastLaAt);
        var a = im.LastLaAt!.Value;
        double ratio = a.ViewRatio(Scale(s) * Math.Sqrt(W * W + H * H) * 0.5);
        Assert.True(ratio < 1 && ratio > Math.Pow(2, -LaAt.ImaginaOffsetBits), $"precondition: view ratio {ratio:G3} between the two margins");
        Assert.True(ff.IterationBuffer.AsSpan().SequenceEqual(none.IterationBuffer));   // rejected = no AT

        // The pixels where Imagina's AT frame and the stages-only frame disagree, scored
        // against exact smooth values.
        BigInteger cr = s.X.Split('|').Select(double.Parse).Aggregate(BigInteger.Zero, (q, l) => q + Fixed(l));
        BigInteger ci = s.Y.Split('|').Select(double.Parse).Aggregate(BigInteger.Zero, (q, l) => q + Fixed(l));
        double scale = Scale(s);
        var idx = Enumerable.Range(0, W * H)
            .Where(i => im.IterationBuffer[i] < s.Iter && none.IterationBuffer[i] < s.Iter
                        && (im.IterationBuffer[i] != none.IterationBuffer[i] || Math.Abs(im.SmoothBuffer[i] - none.SmoothBuffer[i]) > 1e-3))
            .Take(24).ToArray();
        Assert.True(idx.Length >= 12, $"precondition: {idx.Length} disagreeing pixels");
        var ex = new double[idx.Length];
        Parallel.For(0, idx.Length, k =>
        {
            int x = idx[k] % W, y = idx[k] / W;
            ex[k] = ExactSmooth(cr + Fixed((x - W * 0.5) * scale), ci + Fixed((y - H * 0.5) * scale), s.Iter);
        });
        int atCloser = 0, noneCloser = 0; double eA = 0, eN = 0;
        for (int k = 0; k < idx.Length; k++)
        {
            double da = Math.Abs(im.SmoothBuffer[idx[k]] - ex[k]), dn = Math.Abs(none.SmoothBuffer[idx[k]] - ex[k]);
            eA += da; eN += dn;
            if (da < dn - 1e-3) atCloser++; else if (dn < da - 1e-3) noneCloser++;
        }
        output.WriteLine($"view ratio {ratio:G3}; {idx.Length} disagreeing px against exact smooth: Imagina-margin AT closer {atCloser}, " +
                         $"stages only closer {noneCloser}; mean |err| {eA / idx.Length:G3} against {eN / idx.Length:G3}");
        Assert.True(noneCloser > 2 * atCloser && eN < eA, "the stages-only frame should be the more accurate one");
    }

    [Fact]
    public void The_Derivative_Survives_The_At()
    {
        var s = P953;
        var at = Calc(s, la: true);
        var plain = Calc(s, la: false, cfg: c => c.DisableAcceleration = true);   // exact derivative
        Assert.Equal(W * H, at.LastLaAtWork.Pixels);
        int same = 0, bad = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (at.IterationBuffer[i] != plain.IterationBuffer[i] || at.IterationBuffer[i] >= s.Iter) continue;
            same++;
            double dn = Math.Abs(Math.Atan2(at.NormalYBuffer[i] * plain.NormalXBuffer[i] - at.NormalXBuffer[i] * plain.NormalYBuffer[i],
                                            at.NormalXBuffer[i] * plain.NormalXBuffer[i] + at.NormalYBuffer[i] * plain.NormalYBuffer[i]));
            if (dn > 1e-3) bad++;
        }
        output.WriteLine($"{same} escaped px with equal counts, normal > 1e-3 rad off at {bad}");
        Assert.True(same >= W * H / 4, "precondition: enough escaped pixels agree");
        Assert.True(bad <= same / 20, $"normal off at {bad} of {same} px");
    }

    [Fact]
    public void The_Deepest_Scenes_Have_No_Usable_At()
    {
        // p121,984: the top stage's escape radius is under 2 and the lower stages' views
        // are negligible next to their c' offset (design doc §4.3), so no AT.
        var c = Calc(P121984, la: true, cfg: c => c.MaxIterations = 200_000);
        Assert.True(c.LastFrameUsedLa);
        Assert.Null(c.LastLaAt);
        var t = c.LaTable!;
        var top = LaAt.Create(t, t.Stages - 1);
        output.WriteLine($"top stage AT: step {top.Len}, escape r {Math.Sqrt(top.Esc2):G3}");
        Assert.True(top.Esc2 < 4.0);
    }
}
