// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1110 — the User Bulb CPU step compiles to a TYPED Expression tree
// (SandboxBulbTypedCompiler): double / Vec3 / Quat values and direct Math /
// Vec3 / Quat calls instead of SbxVal3 and SbxFuncEval.Apply. It must be
// bit-identical to the tree-walking interpreter, the independent reference:
//   • every golden-corpus source, plus the interpreter's odd kind mixes, in
//     BOTH axis modes, on raw IEEE bits (NaN-normalised), with the same
//     exception type where the interpreter throws;
//   • the built-in chains, in both modes;
//   • a construct whose kind can't be fixed statically falls back (dynamic
//     compile or interpreter) and still agrees;
//   • UserBulbCalculator really runs the typed path (non-vacuity), and the
//     typed path is faster than the dynamic one on a transcendental map.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FracturingFog.Calculators;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1110TypedBulbCompileTests(ITestOutputHelper output)
{
    private static readonly string[] Extras = { "t" };
    private static readonly double[] Ts = { 0.7 };

    private static readonly Vec3[] Zs =
    {
        new(0.3, -0.2, 0.1), new(-0.7, 0.4, 0.25), new(1.1, 0.5, -0.6), new(0, 0, 0),
        new(-2.5, 3.1, -0.05), new(1e-300, -0.0, 5.0),
    };
    private static readonly Vec3[] Cs = { new(-0.5, 0.1, 0.2), new(0.3, -0.6, 0.45) };

    // The interpreter's kind mixes and corner rules, one construct each, read in
    // both axis modes (a qvec(...) is a quat even in Vec3 mode).
    private static readonly string[] Mixes =
    {
        "z + 1", "1 - z", "z + qvec(1, 2, 3, 4)", "qvec(1, 2, 3, 4) - z", "qvec(1, 2, 3, 4) + 2",
        "z * qvec(1, 2, 3, 4)", "qvec(1, 2, 3, 4) * z", "2 * qvec(1, 2, 3, 4)", "qvec(1, 2, 3, 4) * 0.5",
        "z / c", "z / 2", "z / qvec(1, 2, 3, 4)", "2 / z", "2 / qvec(1, 2, 3, 4)",
        "qvec(1, 2, 3, 4) / z", "qvec(1, 2, 3, 4) / qvec(2, 3, 4, 5)", "qvec(1, 2, 3, 4) / 2", "z.x / z.y",
        "qmul(z, 2) + c", "qmul(2, z)", "qconj(z) + qconj(1)", "qpow(z, 2) + qpow(2, 3)",
        "length(qvec(1, 2, 3, 4)) + z", "length(2) * z", "dot(z, 2) + z", "cross(z, qvec(1, 2, 3, 4))",
        "abs(qvec(-1, 2, -3, 4)) + z", "abs(-2) * z", "norm(qvec(1, 2, 3, 4)) * z", "norm(-3) * z", "norm(z) * c",
        "vec(z, c, 2)", "qvec(z, c, 2, z.x)", "triplex(2, 3) + z", "boxfold(2, 1) + spherefold(z, 0.5, 1)",
        "z.w + c", "(z * 2).y * c", "vec(1, 2, 3).w * z", "c.x.y * z", "(2).z * z", "qvec(1, 2, 3, 4).w * z",
        "-qvec(1, 2, 3, 4) + z", "-(2) * z", "!z ? c : z", "!(1 - 1) * z",
        "z > c ? z : c", "z <= 1 ? z : c", "z.x <= z.x ? z : c", "z.y >= z.y ? z : c", "z >= qvec(0, 0, 0, 1) ? z : c", "z == c ? z : c", "z != z ? z : c",
        "z.x > 0 && c ? z : c", "0 || z ? z : c", "z ^ c", "2 ^ z", "z ^ z.x", "pow(2, 0.5) * z",
        "let a = 2 in let b = vec(a, a, 1) in a * b + c", "(let a = 2 in a) * (let b = z in b)",
        "let q = qvec(z.x, z.y, z.z, 1) in q * q + c", "x = z * z; x = x + c; return x",
        "if (length(z) > 2) return z; z * z + c", "floor(z.x * 3) * sign(z.y) * z", "clamp(z.x, -1, 1) * c",
        "min(z, c) * z", "max(z.x, c.y) * z", "smin(z, c, 0.5) * z",
        "log(z.x) * z", "sqrt(z) + c", "exp(z * 0.5) + log(abs(z) + 1)", "tan(z) * cosh(c)", "1 / z.x + z",
        "n * z + t * c", "z * n / (n + 1) + c", "mod(z, 0.7) + rot(z, c, t)", "normalize(z) + c",
    };

    // A value whose kind is only known at run time (a ternary with branches of
    // different kinds) and every way of consuming it.
    private const string D = "(z.x > 0 ? z : 2.5)";
    private static readonly string[] DynMixes =
    {
        "z.x > 0 ? z * z + c : 1.5", $"{D} + c", $"c - {D}", $"{D} * c", $"{D} * 2", $"{D} / c", $"2 / {D}",
        $"-{D} + c", $"{D}.x * c + {D}.y * z", $"{D}.w * z", $"{D} ^ 2 + c", $"z ^ {D} + c", $"pow({D}, 2) + c",
        $"sin({D}) + c", $"abs({D}) + c", $"norm({D}) * c", $"length({D}) * z", $"vec({D}, 1, {D}) + c",
        $"qmul({D}, z) + c", $"let d = {D} in d * d + c", $"{D} > 1 ? z : c", $"!{D} ? z : c",
        "(z.y > 0 ? qvec(1, 2, 3, 4) : z) * c", "(z.y > 0 ? qvec(1, 2, 3, 4) : z.x) / z",
        $"triplex({D}, 3) + boxfold({D}, 1)",
    };
    private static ulong Bits(double d) => (ulong)BitConverter.DoubleToInt64Bits(double.IsNaN(d) ? double.NaN : d);

    /// <summary>One sample as bit patterns, or the exception type the step threw.</summary>
    private static string Sample(Func<double[]> f)
    {
        try { return string.Join(",", f().Select(Bits)); }
        catch (Exception ex) { return "throws " + ex.GetType().Name; }
    }

    private static IEnumerable<string> Samples(Func<Vec3, Vec3, int, double[]> step)
    {
        foreach (var z in Zs) foreach (var c in Cs) for (int n = 0; n < 3; n++)
            yield return Sample(() => step(z, c, n));
    }

    private static List<string> ExprSamples(SandboxBulbExpression e, bool quat)
    {
        var env = e.NewEnv();
        return Samples((z, c, n) =>
        {
            if (!quat) { var r = e.EvalStep(z, c, n, env, Ts); return new[] { r.X, r.Y, r.Z }; }
            var q = e.EvalStepQuat(new Quat(0.2, z.X, z.Y, z.Z), new Quat(-0.1, c.X, c.Y, c.Z), n, env, Ts);
            return new[] { q.W, q.X, q.Y, q.Z };
        }).ToList();
    }

    private static string Upgrade(string src) => BulbLanguageMigration.UpgradeFromVersion1(src).Source;

    private static IEnumerable<string> Sources() =>
        BulbLanguageGoldenTests.Corpus().Select(Upgrade).Concat(Mixes).Concat(DynMixes).Distinct();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Typed_Compile_Is_Bit_Identical_To_The_Interpreter(bool quat)
    {
        int typed = 0, total = 0;
        var dynamic = new List<string>();
        var bad = new List<string>();
        foreach (var src in Sources())
        {
            SandboxBulbExpression interp, comp;
            try { interp = SandboxBulbExpression.Parse(src, Extras); comp = SandboxBulbExpression.Parse(src, Extras); }
            catch (FormatException) { continue; }
            total++;
            Assert.True(comp.TryCompile(quat));
            Assert.True(comp.IsTypedCompiled(quat), $"{src}: {comp.TypedFallbackReason}");
            typed++;
            if (comp.TypedDynamicNodes > 0) dynamic.Add($"{src} ({comp.TypedDynamicNodes})");
            Assert.False(interp.IsCompiled);
            var a = ExprSamples(interp, quat);
            var b = ExprSamples(comp, quat);
            int i = a.Zip(b).TakeWhile(p => p.First == p.Second).Count();
            if (i < a.Count) bad.Add($"{src}  sample {i}: interp [{a[i]}] vs typed [{b[i]}]");
        }
        output.WriteLine($"{(quat ? "Quat" : "Vec3")}: {typed}/{total} typed; with SbxVal3 nodes:\n  " + string.Join("\n  ", dynamic));
        Assert.True(bad.Count == 0, "typed compile disagrees with the interpreter:\n" + string.Join("\n", bad.Take(12)));
        Assert.True(total > 150, $"only {total} sources");
        // Non-vacuity of the run-time-kind path: the DynMixes (both modes) and,
        // in Quat mode, sin & co. on a quat.
        Assert.True(dynamic.Count >= DynMixes.Length, $"only {dynamic.Count} sources exercised SbxVal3 nodes");
        Assert.True(dynamic.Count < total / 3, $"{dynamic.Count}/{total} sources fell to SbxVal3 nodes");
    }
    private static IEnumerable<(string Name, List<UserBulbChainStep> Steps)> Chains()
    {
        yield return ("mandelbox-bulb", UserBulbChainPrimitives.MandelboxBulbHybrid());
        yield return ("menger-bulb", UserBulbChainPrimitives.MengerBulbHybrid());
        yield return ("kifs", UserBulbChainPrimitives.KaleidoscopicIfsChain());
        // Prior outputs of every kind, a let reusing slots, a real step.
        yield return ("mixed kinds", new List<UserBulbChainStep>
        {
            new() { OutputName = "r", Source = "length(z) * t" },
            new() { OutputName = "q", Source = "qvec(z.x, z.y, z.z, r)" },
            new() { OutputName = "v", Source = "let a = r in vec(a, q.w, q.x) + z" },
            new() { OutputName = "out", Source = "v * r + q.y * c" },
        });
    }

    private static List<string> ChainSamples(SandboxBulbChain ch, bool quat)
    {
        var env = ch.NewEnv();
        return Samples((z, c, n) =>
        {
            if (!quat) { var r = ch.EvalStep(z, c, n, env, Ts); return new[] { r.X, r.Y, r.Z }; }
            var q = ch.EvalStepQuat(new Quat(0.2, z.X, z.Y, z.Z), new Quat(-0.1, c.X, c.Y, c.Z), n, env, Ts);
            return new[] { q.W, q.X, q.Y, q.Z };
        }).ToList();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Typed_Chains_Are_Bit_Identical_To_The_Interpreter(bool quat)
    {
        foreach (var (name, steps) in Chains())
        {
            var up = steps.Select(s => new UserBulbChainStep { OutputName = s.OutputName, Source = Upgrade(s.Source) }).ToList();
            var interp = SandboxBulbChain.Parse(up, Extras);
            var comp = SandboxBulbChain.Parse(up, Extras);
            Assert.True(comp.TryCompile(quat), $"{name}: {comp.TypedFallbackReason}");
            Assert.True(comp.IsTypedCompiled(quat), $"{name}: {comp.TypedFallbackReason}");
            Assert.False(interp.IsTypedCompiled(quat) || interp.IsCompiled);
            // Menger / KIFS swap folds pick vec(...) or z: in Quat mode a vec /
            // quat ternary, whose consumers run on SbxVal3.
            output.WriteLine($"{name} ({(quat ? "Quat" : "Vec3")}): {comp.TypedDynamicNodes} SbxVal3 nodes");
            if (quat && name is "menger-bulb" or "kifs") Assert.True(comp.TypedDynamicNodes > 0);
            if (!quat) Assert.Equal(0, comp.TypedDynamicNodes);
            var a = ChainSamples(interp, quat);
            var b = ChainSamples(comp, quat);
            Assert.True(a.Distinct().Count() > 4, $"{name}: the samples don't vary");
            for (int i = 0; i < a.Count; i++)
                Assert.True(a[i] == b[i], $"{name} sample {i}: interp [{a[i]}] vs typed [{b[i]}]");
        }
    }

    [Fact]
    public void A_Kind_Mixed_Ternary_Runs_On_SbxVal3_And_Still_Agrees()
    {
        const string src = "z.x > 0 ? z * z + c : 1.5";
        var interp = SandboxBulbExpression.Parse(src, Extras);
        var comp = SandboxBulbExpression.Parse(src, Extras);
        Assert.True(comp.TryCompile(false));
        Assert.True(comp.IsTypedCompiled(false));
        Assert.True(comp.TypedDynamicNodes > 0);
        var a = ExprSamples(interp, false);
        Assert.Contains(a, x => x.Split(',').Distinct().Count() == 1);   // the 1.5 branch, as (r, r, r)
        Assert.Equal(a, ExprSamples(comp, false));

        // A fully static tree has none.
        var st = SandboxBulbExpression.Parse("z.x > 0 ? z * z + c : c", Extras);
        Assert.True(st.TryCompile(false));
        Assert.Equal(0, st.TypedDynamicNodes);

        // sin on a quat throws in the interpreter; the typed compile keeps that.
        var q = SandboxBulbExpression.Parse("sin(z) + c", Extras);
        Assert.True(q.TryCompile(true));
        Assert.True(q.IsTypedCompiled(true));
        Assert.Throws<InvalidOperationException>(() => q.EvalStepQuat(new Quat(1, 2, 3, 4), Quat.Zero, 0, q.NewEnv(), Ts));
    }
    private static UserBulbCalculator Calc(string src, UserBulbAxisModeKind mode, List<UserBulbChainStep>? chain = null)
    {
        var fp = new FractalParameters { UserBulbSource = src, UserBulbAxisMode = mode, UserBulbChain = chain };
        var calc = new UserBulbCalculator(16, 16) { FractalParameters = fp };
        calc.Compile(src);
        Assert.True(calc.IsCompiled, calc.LastError);
        return calc;
    }

    [Fact]
    public void The_Calculator_Runs_The_Typed_Step()
    {
        Assert.True(Calc("vec(sin(z.x)*cosh(z.y), cos(z.x)*cos(z.z)*sinh(z.y), sin(z.z)*cosh(z.y)) + c",
            UserBulbAxisModeKind.Vec3).IsTypedCompiled);
        Assert.True(Calc("qsin(z * z) + c", UserBulbAxisModeKind.Quat).IsTypedCompiled);
        Assert.True(Calc("z", UserBulbAxisModeKind.Vec3, UserBulbChainPrimitives.MengerBulbHybrid()).IsTypedCompiled);
        Assert.True(Calc("z", UserBulbAxisModeKind.Quat, UserBulbChainPrimitives.MandelboxBulbHybrid()).IsTypedCompiled);
        Assert.True(Calc("z.x > 0 ? z * z + c : 1.5", UserBulbAxisModeKind.Vec3).IsTypedCompiled);
        Assert.True(Calc("z", UserBulbAxisModeKind.Quat, UserBulbChainPrimitives.KaleidoscopicIfsChain()).IsTypedCompiled);
    }

    [Fact]
    public void The_Typed_Step_Beats_The_Dynamic_One_On_A_Transcendental_Map()
    {
        const string src = "vec(sin(z.x)*cosh(z.y), cos(z.x)*cos(z.z)*sinh(z.y), sin(z.z)*cosh(z.y)) + c";
        var dyn = SandboxBulbExpression.Parse(src, Extras);
        Assert.True(dyn.TryCompile());
        var typ = SandboxBulbExpression.Parse(src, Extras);
        Assert.True(typ.TryCompile(false));
        Assert.True(typ.IsTypedCompiled(false));
        var envD = dyn.NewEnv(); var envT = typ.NewEnv();
        var c = new Vec3(0.31, -0.22, 0.13);

        double Time(SandboxBulbExpression e, SbxVal3[] env)
        {
            const int N = 200_000;
            double sink = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++) sink += e.EvalStep(new Vec3(0.1 * (i % 7) - 0.3, 0.2, -0.1), c, i & 7, env, Ts).X;
            sw.Stop();
            Assert.False(double.IsNaN(sink));
            return sw.Elapsed.TotalMilliseconds;
        }
        // Interleaved, best of 5: robust to a loaded machine.
        double bestD = double.MaxValue, bestT = double.MaxValue;
        for (int r = 0; r < 5; r++) { bestD = Math.Min(bestD, Time(dyn, envD)); bestT = Math.Min(bestT, Time(typ, envT)); }
        output.WriteLine($"dynamic {bestD:F1} ms, typed {bestT:F1} ms, {bestD / bestT:F1}x");
        Assert.True(bestD > 3 * bestT, $"typed {bestT:F1} ms vs dynamic {bestD:F1} ms");
    }
}
