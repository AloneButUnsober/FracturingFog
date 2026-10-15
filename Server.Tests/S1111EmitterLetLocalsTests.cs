// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1111 — UserBulbSandboxEmitter (the GPU kernels' front half) declares each
// let, and every operand its text reads more than once, as a typed local
// evaluated once, instead of pasting the value's text at every use. Chained
// folds used to recompute their inputs at each reference, so the emitted text
// (and the work) grew exponentially with the chain depth. Checked here:
//   • the Menger fold evaluates abs(z) once (3 Math.Abs, one per component);
//   • over the whole corpus the emitted calls stay linear in the AST size;
//   • a chain emitted step by step (each step's locals prefixed, one scope)
//     matches SandboxBulbChain, the interpreter, value for value;
//   • the chain kernels still compile and JIT on the GPU path.
// BulbEmitterParityTests (now on the full method body) keeps the per-source
// value parity on the CPU and GPU targets.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FracturingFog.Calculators;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1111EmitterLetLocalsTests(ITestOutputHelper output)
{
    private static readonly string[] Extras = { "t" };
    private const string Menger =
        "let v = abs(z) in let v1 = (v.x - v.y < 0 ? vec(v.y, v.x, v.z) : v) in " +
        "let v2 = (v1.x - v1.z < 0 ? vec(v1.z, v1.y, v1.x) : v1) in " +
        "let v3 = (v2.y - v2.z < 0 ? vec(v2.x, v2.z, v2.y) : v2) in v3 * 3.0 - vec(2, 2, 0)";

    private static readonly Regex CallToken = new(@"\b(Math|Vec3|Vec3GpuOps|Quat|QuatGpuOps)\.[A-Z]\w*\(", RegexOptions.Compiled);

    private static int Calls(SbxEmitResult r) => CallToken.Matches(r.MethodBody()).Count;

    private static int Nodes(Sbx3Node n) => n switch
    {
        Sbx3Let l => 1 + Nodes(l.Value) + Nodes(l.Body),
        Sbx3Unary u => 1 + Nodes(u.A),
        Sbx3Binary b => 1 + Nodes(b.A) + Nodes(b.B),
        Sbx3Ternary t => 1 + Nodes(t.Cond) + Nodes(t.Then) + Nodes(t.Else),
        Sbx3Member m => 1 + Nodes(m.Target),
        Sbx3Call c => 1 + c.Args.Sum(Nodes),
        _ => 1,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_Menger_Fold_Evaluates_Abs_Once(bool gpu)
    {
        var e = SandboxBulbExpression.Parse(Menger, Extras);
        var r = UserBulbSandboxEmitter.Emit(e.Root, Extras, quatMode: false, gpuTarget: gpu);
        Assert.True(r.Ok, r.Error);
        string body = r.MethodBody();
        output.WriteLine(body);
        Assert.Equal(3, Regex.Matches(body, @"Math\.Abs\(").Count);
        Assert.True(r.Statements.Count >= 4, "each let should be a local");
    }

    // An expensive operand read per component / per factor must be computed
    // once: the number of Pow / Exp calls in the text equals the AST's.
    [Theory]
    [InlineData("sin(triplex(z, 8)) + c", false, 1)]          // componentwise
    [InlineData("abs(triplex(z, 8)) + c", true, 1)]           // componentwise on a quat (W too)
    [InlineData("triplex(z, 8) * triplex(c, 2)", false, 2)]   // Hadamard
    [InlineData("triplex(z, 3) / triplex(c, 2)", false, 2)]   // vec / vec
    [InlineData("2 / triplex(z, 3)", false, 1)]               // real / vec
    [InlineData("norm(triplex(z, 3)) * z", false, 1)]         // squared length
    [InlineData("dot(length(triplex(z, 3)), z) * z", false, 1)]   // real → vec broadcast
    [InlineData("qpow(qexp(z), 3) + c", true, 1)]             // unfolded qpow
    [InlineData("qexp(z) / 2.0 + c", true, 1)]                // quat / real
    public void A_Multiply_Read_Operand_Is_Computed_Once(string src, bool quat, int expected)
    {
        var e = SandboxBulbExpression.Parse(src, Extras);
        foreach (bool gpu in new[] { false, true })
        {
            var r = UserBulbSandboxEmitter.Emit(e.Root, Extras, quat, gpu);
            Assert.True(r.Ok, r.Error);
            string body = r.MethodBody();
            int n = Regex.Matches(body, @"\b(Vec3|Vec3GpuOps|Quat|QuatGpuOps)\.(Pow|Exp)\(").Count;
            Assert.True(n == expected, $"{src} ({(gpu ? "gpu" : "cpu")}): {n} Pow/Exp calls, expected {expected}\n{body}");
        }
    }

    [Fact]
    public void Emitted_Calls_Stay_Linear_In_The_Ast_Over_The_Corpus()
    {
        int checkedCount = 0;
        var worst = new List<(double Ratio, string Src)>();
        foreach (var raw in BulbLanguageGoldenTests.Corpus().Append(Menger).Distinct())
        {
            string src = BulbLanguageMigration.UpgradeFromVersion1(raw).Source;
            SandboxBulbExpression e;
            try { e = SandboxBulbExpression.Parse(src, Extras); } catch (FormatException) { continue; }
            foreach (bool quat in new[] { false, true })
            {
                var r = UserBulbSandboxEmitter.Emit(e.Root, Extras, quat, gpuTarget: true);
                if (!r.Ok) continue;
                checkedCount++;
                int calls = Calls(r), nodes = Nodes(e.Root);
                worst.Add(((double)calls / nodes, $"{(quat ? "Q" : "V")} {src}: {calls} calls / {nodes} nodes"));
                // A node emits at most one call per component (4 for a quat).
                Assert.True(calls <= 4 * nodes, $"{src} ({(quat ? "Quat" : "Vec3")}): {calls} calls for {nodes} AST nodes");
            }
        }
        foreach (var w in worst.OrderByDescending(w => w.Ratio).Take(5)) output.WriteLine($"{w.Ratio:F2}  {w.Src}");
        Assert.True(checkedCount > 100, $"only {checkedCount} emits checked");
    }

    private static IEnumerable<(string Name, List<UserBulbChainStep> Steps)> Chains()
    {
        yield return ("mandelbox-bulb", UserBulbChainPrimitives.MandelboxBulbHybrid());
        yield return ("menger-bulb", UserBulbChainPrimitives.MengerBulbHybrid());
        yield return ("kifs", UserBulbChainPrimitives.KaleidoscopicIfsChain());
    }

    private static List<UserBulbChainStep> Upgrade(List<UserBulbChainStep> steps) =>
        steps.Select(s => new UserBulbChainStep { OutputName = s.OutputName, Source = BulbLanguageMigration.UpgradeFromVersion1(s.Source).Source }).ToList();

    [Fact]
    public void A_Chain_Emitted_Step_By_Step_Matches_The_Interpreter()
    {
        int id = 0;
        var bodies = new List<(int, string, bool)>();
        var chains = new List<(string Name, SandboxBulbChain Chain, int Id)>();
        foreach (var (name, raw) in Chains())
        {
            var steps = Upgrade(raw);
            var chain = SandboxBulbChain.Parse(steps, Extras);
            // The GPU compiler's chain assembly, on the CPU target: each step's
            // locals (own prefix) then its value into a typed local.
            var prior = new Dictionary<int, (string Name, SbxEmitKind Kind)>();
            var sb = new StringBuilder();
            string last = "z";
            for (int i = 0; i < steps.Count; i++)
            {
                // Emitter param names exclude t (it is always the next slot).
                var r = UserBulbSandboxEmitter.Emit(chain.StepRoots[i], Array.Empty<string>(), false, false, prior, localPrefix: $"__s{i}_");
                Assert.True(r.Ok, $"{name} step {i}: {r.Error}");
                foreach (var st in r.Statements) sb.Append(st).Append(' ');
                last = "o" + i;
                sb.Append("Vec3 ").Append(last).Append(" = ").Append(r.Body).Append("; ");
                prior[chain.StepOutputSlots[i]] = (last, SbxEmitKind.Vec);
            }
            sb.Append("return ").Append(last).Append(';');
            bodies.Add((id, sb.ToString(), false));
            chains.Add((name, chain, id++));
        }
        var errors = new List<string>();
        var type = BulbEmitterParityTests.Compile(bodies, errors);
        Assert.True(type != null, string.Join("\n", errors));

        var zs = new[] { new Vec3(0.3, -0.2, 0.1), new Vec3(-0.7, 0.4, 0.25), new Vec3(1.1, 0.5, -0.6) };
        var cs = new[] { new Vec3(-0.5, 0.1, 0.2), new Vec3(0.3, -0.6, 0.45) };
        foreach (var (name, chain, cid) in chains)
        {
            var m = type!.GetMethod("S" + cid)!;
            var env = chain.NewEnv();
            var seen = new HashSet<Vec3>();
            foreach (var z in zs) foreach (var c in cs)
            {
                var want = chain.EvalStep(z, c, 1, env, new[] { 0.7 });
                var got = (Vec3)m.Invoke(null, new object[] { z, c, 1, 0.7 })!;
                seen.Add(want);
                Assert.True(want == got, $"{name} z={z} c={c}: interpreter {want} vs emitted {got}");
            }
            Assert.True(seen.Count >= zs.Length, $"{name}: outputs don't vary");   // KIFS ignores c
        }
    }

    [Fact]
    public void Chain_Kernels_Compile_And_Load_On_The_Gpu_Path()
    {
        using var gpu = new UserBulbSandboxGpuCompiler();
        foreach (var (name, raw) in Chains())
            foreach (bool quat in new[] { false, true })
            {
                if (quat && name != "mandelbox-bulb") continue;   // swap folds mix vec / quat (emitter refuses, CPU path)
                Assert.True(gpu.TryCompileChain(Upgrade(raw), Array.Empty<string>(), quat), $"{name} ({quat}): {gpu.LastError}");
            }
        Assert.True(gpu.TryCompile(Menger, Array.Empty<string>(), false), gpu.LastError);
        // Two steps that both declare locals share the Step() scope.
        var twoLets = new List<UserBulbChainStep>
        {
            new() { OutputName = "a", Source = "let u = abs(z) in u * u" },
            new() { OutputName = "b", Source = "let w = abs(a) in w * w + c" },
        };
        Assert.True(gpu.TryCompileChain(twoLets, Array.Empty<string>(), false), gpu.LastError);
    }
}
