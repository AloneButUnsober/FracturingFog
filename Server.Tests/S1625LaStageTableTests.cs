// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1625 (epic #1623, S2) — the LA stage table (FFMath.LaStageTable; method from
// Imagina's HInfLAEvaluator, GitHub user 5E-324, AGPL-3.0, see the bibliography
// #imagina; maths in Docs/Technical/Period-Synchronised-LA-DesignPlan.md §2). Checked
// on the bench-kit minibrot chain (fractadyne bench-kit, WindySnowOwl,
// https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit, from rith's FF 0.7.1
// benchmark report; see the bibliography), each reference stopped at its period:
//   • Independent invariant: a composed step applied to a small δ and δc matches
//     direct perturbation iteration of the full recurrence (δ² kept) over its L
//     iterations, for steps of every stage, to well under the step's tolerance.
//   • The stages sit at the known periods: the top stage is one step of the period;
//     the period-953 copies appear as 953-iteration steps; every stage covers the
//     reference exactly and each step's next index points at the lower-stage step
//     that starts where it starts.
//   • The calculator builds it only when asked, keeps it for the same reference
//     and tolerance, rebuilds on a tolerance change, and renders the same bytes.
// Mutants: dropping "+ b.B" from the composite B fails the direct-iteration test;
// next indices off by one fail the structure test; a stage-0 dip test that never
// fires fails the period test.

using System;
using System.Linq;
using System.Numerics;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1625LaStageTableTests(ITestOutputHelper output)
{
    private const int Bits = 320;

    private sealed record Scene(string Name, string X, string Y, int Period, int[] FirstLens);

    // Centres from the kit's .kfr files (as Probes/DeepCostProbe.cs).
    private static readonly Scene P953 = new("p953",
        "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.203746728154813e-51|-4.269596053996866e-68|1.9389949392385534e-84|6.25819023760954e-101|-2.6646795788290983e-117",
        "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3771597857226995e-50|-5.482812918750436e-67|4.692321383846679e-84|-2.313052339214723e-100|2.1139231852108862e-116",
        953, [31, 953]);
    private static readonly Scene P15248 = new("p15248",
        "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.198613498328184e-51|-2.6338819725348693e-68|-1.2531186861084395e-84|8.265175494285413e-101|-2.8428099960154152e-117",
        "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3770883977626e-50|-1.6692666809801752e-67|-4.583534842942509e-84|4.2910480785722876e-100|-1.016687227891797e-116",
        15248, [31, 953, 15248]);
    // 10,483 = 953 x 11: the first dip past stage 1 is at 121,984, beyond the x64
    // cap, so that stage is cut at a fixed length (design doc §2.3).
    private static readonly Scene P121984 = new("p121984",
        "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.2016807295614754e-51|1.7176952655143277e-67|1.0180672183123456e-83|-1.5782574900160777e-100|8.451632690787226e-117",
        "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3766027088999295e-50|9.932823572218873e-67|2.5207480153941874e-83|-8.913322115858611e-100|2.182658649920256e-116",
        121984, [31, 953, 10483, 121984]);

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

    private static double ToDouble(BigInteger v)
    {
        if (v.IsZero) return 0;
        int drop = (int)Math.Max(0, (long)BigInteger.Abs(v).GetBitLength() - 62);
        return (double)(v >> drop) * Math.Pow(2, drop - Bits);
    }

    // Z_0 .. Z_p in fixed point, rounded to double (the reference the table is built from).
    private static (double[] Zr, double[] Zi) Reference(Scene s)
    {
        BigInteger cr = s.X.Split('|').Select(double.Parse).Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l));
        BigInteger ci = s.Y.Split('|').Select(double.Parse).Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l));
        var zr = new double[s.Period + 1];
        var zi = new double[s.Period + 1];
        BigInteger r = 0, i = 0;
        for (int n = 1; n <= s.Period; n++)
        {
            BigInteger r2 = (r * r) >> Bits, i2 = (i * i) >> Bits;
            i = ((r * i) >> (Bits - 1)) + ci;
            r = r2 - i2 + cr;
            zr[n] = ToDouble(r); zi[n] = ToDouble(i);
        }
        return (zr, zi);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double[], double[], LaStageTable)> s_cache = new();

    private static (double[] Zr, double[] Zi, LaStageTable T) Table(Scene s) => s_cache.GetOrAdd(s.Name, _ =>
    {
        var (zr, zi) = Reference(s);
        return (zr, zi, LaStageTable.Build(zr, zi, s.Period)!);
    });

    // The reference index each step of stage k starts at.
    private static int[] Starts(LaStageTable t, int k)
    {
        var st = t.Stage(k);
        var s = new int[st.Length];
        for (int j = 1; j < st.Length; j++) s[j] = s[j - 1] + st[j - 1].Len;
        return s;
    }

    public static TheoryData<string> Scenes => new() { "p953", "p15248", "p121984" };
    private static Scene ByName(string n) => n switch { "p953" => P953, "p15248" => P15248, _ => P121984 };

    [Theory]
    [MemberData(nameof(Scenes))]
    public void Stages_Sit_At_The_Known_Periods_And_Line_Up(string name)
    {
        var s = ByName(name);
        var (_, _, t) = Table(s);
        output.WriteLine($"{name}: {t.Stages} stages, {t.Data.Length:N0} steps, {t.Bytes / 1024.0:N0} KiB, built in {t.BuildMs:F1} ms; first lengths "
                         + string.Join(", ", Enumerable.Range(0, t.Stages).Select(k => t.Stage(k)[0].Len)));
        Assert.Equal(s.FirstLens, Enumerable.Range(0, t.Stages).Select(k => t.Stage(k)[0].Len).ToArray());
        Assert.Equal(1, t.StageCount[^1]);
        Assert.Equal(s.Period, t.Stage(t.Stages - 1)[0].Len);
        // The period-953 copies: every stage-1 step is 953 long.
        if (t.Stages > 2) Assert.All(t.Stage(1).ToArray(), st => Assert.Equal(953, st.Len));

        for (int k = 0; k < t.Stages; k++)
        {
            var st = t.Stage(k);
            long sum = 0;
            foreach (var x in st) sum += x.Len;
            Assert.Equal(s.Period, sum);   // each stage covers the reference exactly
            var starts = Starts(t, k);
            if (k == 0)
                for (int j = 0; j < st.Length; j++) Assert.Equal(starts[j], st[j].Next);
            else
            {
                var lower = Starts(t, k - 1);
                for (int j = 0; j < st.Length; j++) Assert.Equal(starts[j], lower[st[j].Next]);
            }
            // The sentinel sits at Z_R.
            Assert.Equal(t.Step(0, t.StageCount[0]).RefRe, t.Step(k, t.StageCount[k]).RefRe);
        }
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void A_Step_Matches_Direct_Perturbation_Iteration(string name)
    {
        var s = ByName(name);
        var (zr, zi, t) = Table(s);
        var rng = new Random(1625);
        double worst = 0;
        int checkedSteps = 0;
        for (int k = 0; k < t.Stages; k++)
        {
            var st = t.Stage(k);
            var starts = Starts(t, k);
            foreach (int j in new[] { 0, st.Length / 2, st.Length - 1 }.Distinct())
            {
                var step = st[j];
                if (!(step.Thr > 0) || !(step.ThrC > 0)) continue;
                int at = starts[j];
                var zs = new Complex(zr[at], zi[at]);
                // δc and δ_s with ‖w‖, ‖δc‖ a quarter of the step's thresholds.
                double ang = rng.NextDouble() * 2 * Math.PI, ang2 = rng.NextDouble() * 2 * Math.PI;
                var dc = Complex.FromPolarCoordinates(0.25 * step.ThrC, ang);
                double rho = Math.Sqrt(0.25 * step.Thr);   // |w| ≈ ρ² where Z_s ≈ 0 (the anchor of a stage's first step)
                if (zs.Magnitude > 0) rho = Math.Min(rho, 0.25 * step.Thr / (2 * zs.Magnitude));
                var d = Complex.FromPolarCoordinates(rho, ang2);
                Complex w = d * (2 * zs + d);
                for (int g = 0; g < 2000 && Math.Max(Math.Abs(w.Real), Math.Abs(w.Imaginary)) >= 0.25 * step.Thr; g++)
                { d *= 0.5; w = d * (2 * zs + d); }

                Complex la = w * step.A + dc * step.B;
                Complex direct = d;
                for (int m = at; m < at + step.Len; m++)
                    direct = 2 * new Complex(zr[m], zi[m]) * direct + direct * direct + dc;
                double rel = (la - direct).Magnitude / direct.Magnitude;
                worst = Math.Max(worst, rel);
                checkedSteps++;
                // At a quarter of the thresholds the dropped δ² terms leave an error of order τ.
                Assert.True(rel < 8 * t.Tolerance, $"{name} stage {k} step {j} (L {step.Len:N0}): relative error {rel:G3}");
            }
        }
        output.WriteLine($"{name}: {checkedSteps} steps checked, worst relative error {worst:G3} (τ = {t.Tolerance:G3})");
        Assert.True(checkedSteps >= t.Stages, "precondition: a step of every stage checked");
    }

    [Fact]
    public void The_Tolerance_Follows_The_Bla_Tolerance()
    {
        Assert.Equal(LaStageTable.DefaultTolerance, LaStageTable.ToleranceFor(BlaTable.DefaultEpsilon));
        Assert.Equal(LaStageTable.DefaultTolerance / 1e4, LaStageTable.ToleranceFor(1e-10), 1e-20);
        Assert.Equal(LaStageTable.DefaultTolerance, LaStageTable.ToleranceFor(double.NaN));
        Assert.Equal(Math.Pow(2, -8), LaStageTable.ToleranceFor(1.0));
    }

    private static MandelbrotCalculator P953Calc(bool build)
    {
        double[] X = P953.X.Split('|').Select(double.Parse).ToArray(), Y = P953.Y.Split('|').Select(double.Parse).ToArray();
        return new MandelbrotCalculator(64, 36)
        {
            Quality = QualityPreset.Extreme,
            CenterX = X[0], CenterXLo = X[1], CenterX2 = X[2], CenterX3 = X[3], CenterX4 = X[4], CenterX5 = X[5], CenterX6 = X[6], CenterX7 = X[7],
            CenterY = Y[0], CenterYLo = Y[1], CenterY2 = Y[2], CenterY3 = Y[3], CenterY4 = Y[4], CenterY5 = Y[5], CenterY6 = Y[6], CenterY7 = Y[7],
            Zoom = 1.342765e53 * 3.5 * 36 / (4.0 * 64), MaxIterations = 28590,
            ColorMap = new HsvPalette(), BuildLaStages = build,
        };
    }

    [Fact]
    public void The_Calculator_Builds_And_Keeps_It_Only_When_Asked()
    {
        var off = P953Calc(build: false);
        off.Calculate();
        Assert.Null(off.LaTable);

        var on = P953Calc(build: true);
        on.Calculate();
        var t1 = on.LaTable;
        Assert.NotNull(t1);
        Assert.Equal(953, on.ReferencePeriod);   // precondition: the periodic reference (#1614)
        Assert.Equal(953, t1!.RefLen);
        Assert.Equal(new[] { 31, 953 }, Enumerable.Range(0, t1.Stages).Select(k => t1.Stage(k)[0].Len).ToArray());

        on.Calculate();
        Assert.Same(t1, on.LaTable);             // same reference, same tolerance: kept

        on.BlaTolerance = 1e-8;
        on.Calculate();
        Assert.NotSame(t1, on.LaTable);
        Assert.Equal(LaStageTable.ToleranceFor(1e-8), on.LaTable!.Tolerance);

        // Nothing renders from it yet (S3): the frames are the same bytes.
        Assert.True(off.IterationBuffer.AsSpan().SequenceEqual(P953Built().IterationBuffer));
    }

    private static MandelbrotCalculator P953Built() { var c = P953Calc(build: true); c.Calculate(); return c; }
}
