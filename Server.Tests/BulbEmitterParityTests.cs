// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1100 — the User Bulb C# emitter (UserBulbSandboxEmitter, the front half of
// the GPU path) must mean what the interpreter means. Until now it was only
// covered by the `UserBulbSelfTest` CLI. Here every golden-corpus source is
// parsed, emitted (CPU target), compiled with Roslyn into a Step function and
// evaluated on a grid next to the interpreter. A grammar change that the
// emitter missed shows up as a value mismatch.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FracturingFog.Models;
using Emitter = FracturingFog.Calculators.UserBulbSandboxEmitter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class BulbEmitterParityTests
{
    private static readonly string[] Extras = { "t" };
    private const double T = 0.7;

    private static bool IsQuat(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, @"\bq[a-z]+\(|\.w\b");

    internal static Type? Compile(List<(int Id, string Body, bool Quat)> bodies, List<string> errors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System; using FracturingFog.Models; using FracturingFog.Calculators;");
        sb.AppendLine("public static class BulbSteps {");
        foreach (var (id, body, quat) in bodies)
        {
            string ty = quat ? "Quat" : "Vec3";
            sb.Append($"  public static {ty} S{id}({ty} z, {ty} c, int n, double t) {{ return ").Append(body).AppendLine("; }");
        }
        sb.AppendLine("}");

        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat(new[] { typeof(Vec3).Assembly.Location, typeof(SandboxBulbExpression).Assembly.Location })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Select(p => MetadataReference.CreateFromFile(p));
        var comp = CSharpCompilation.Create("BulbEmitterParity_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(sb.ToString()) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = comp.Emit(ms);
        if (!emit.Success)
        {
            errors.AddRange(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
            return null;
        }
        return Assembly.Load(ms.ToArray()).GetType("BulbSteps");
    }

    private static bool Close(double a, double b)
        => (double.IsNaN(a) && double.IsNaN(b)) || a == b
           || Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));

    // Sources whose emitted C# doesn't compile (the GPU path would fall back
    // to the CPU interpreter for them). #1105 closed every gap the corpus and
    // the cases below showed; a NEW gap fails the test.
    private static readonly string[] KnownEmitterGaps = { };

    // #1105 — one case per construct the emitter used to get wrong, plus the
    // neighbouring rules (division forms, !, comparisons on vectors).
    private static readonly string[] GapCases =
    {
        "-z.x^2 + c", "z + 1", "1 - z", "abs(z) + 1", "z.y + c", "z - c.x",
        "z.x > 0 && z.y < 1 ? z + c : c", "z.x > 0 || length(z) > 1 ? z*z : c",
        "!(z.x > 0) ? z : c", "z > c ? z : c", "z ? z*z : c",
        "pow(z, 3) + c", "pow(z, 2.5) + c", "z^length(c) + c", "pow(z.x, 2) + c",
        "z / c + c", "1 / z.x + c", "2 / z + c", "z / 2.0 + c", "c / z + z",
        // quaternion mode
        "qexp(z*0.1) + qlog(c + 2)", "qinv(z + 2) + qconj(c)", "z + 1", "2 - z",
        "z^2 + c", "z^2.5 + c", "pow(z, 3) + c", "qmul(z, z) / 2.0 + c", "z.w > 0 && z.x > 0 ? z*z + c : z",
        "z + vec(1, 2, 3)", "1 / z + c",
    };

    // Quat-mode cases are the ones after "qexp…" in GapCases.
    private static IEnumerable<(string Src, bool Quat)> GapCaseModes()
    {
        bool quat = false;
        foreach (var g in GapCases)
        {
            if (g.StartsWith("qexp")) quat = true;
            yield return (g, quat);
        }
    }

    [Fact]
    public void EmittedCSharp_MatchesTheInterpreter_OnTheCorpus()
        => RunParity(BulbLanguageGoldenTests.Corpus().Distinct().Select(s => (s, IsQuat(s))), gpu: false, minCompiled: 50);

    [Fact]
    public void GpuTargetCSharp_MatchesTheInterpreter_OnTheCorpus()
        => RunParity(BulbLanguageGoldenTests.Corpus().Distinct().Select(s => (s, IsQuat(s))), gpu: true, minCompiled: 50);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FormerGaps_EmitCompileAndMatch(bool gpu)
        => RunParity(GapCaseModes(), gpu, minCompiled: GapCases.Length);

    // The real GPU path: emitted body → kernel source → Roslyn → ILGPU kernel
    // load (JIT) on the preferred device (the CPU accelerator where no GPU).
    // Before #1105 every one of these failed the Roslyn step and fell back.
    [Fact]
    public void FormerGaps_CompileAndLoadAsGpuKernels()
    {
        using var gpu = new FracturingFog.Calculators.UserBulbSandboxGpuCompiler();
        var bad = new List<string>();
        foreach (var (src, quat) in GapCaseModes().Distinct())
            if (!gpu.TryCompile(src, Array.Empty<string>(), quat))
                bad.Add($"{(quat ? "quat" : "vec")} {src}: {gpu.LastError}");
        Assert.True(bad.Count == 0, "GPU kernel compile failed:\n" + string.Join("\n", bad));
    }

    private static void RunParity(IEnumerable<(string Src, bool Quat)> sources, bool gpu, int minCompiled)
    {
        var items = new List<(int Id, string Src, SandboxBulbExpression Expr, string Body, bool Quat)>();
        var skipped = new List<string>();
        int id = 0;
        foreach (var (src, quat) in sources)
        {
            SandboxBulbExpression e;
            try { e = SandboxBulbExpression.Parse(src, Extras); } catch (FormatException) { continue; }
            var r = Emitter.Emit(e.Root, Extras, quat, gpu);
            if (!r.Ok) { skipped.Add($"{src}: {r.Error}"); continue; }
            items.Add((id++, src, e, r.Body!, quat));
        }

        // Compile each body on its own so one gap can't hide behind another.
        var compiled = new List<(int Id, string Src, SandboxBulbExpression Expr, string Body, bool Quat, MethodInfo M)>();
        var gaps = new List<string>();
        foreach (var it in items)
        {
            var errors = new List<string>();
            var t = Compile(new List<(int, string, bool)> { (it.Id, it.Body, it.Quat) }, errors);
            if (t == null)
            {
                if (!KnownEmitterGaps.Contains(it.Src)) gaps.Add($"{it.Src}  =>  {it.Body}  :: {errors.FirstOrDefault()}");
                continue;
            }
            compiled.Add((it.Id, it.Src, it.Expr, it.Body, it.Quat, t.GetMethod("S" + it.Id)!));
        }
        Assert.True(skipped.Count == 0, "Emitter refused:\n" + string.Join("\n", skipped));
        Assert.True(gaps.Count == 0, "Emitted C# doesn't compile:\n" + string.Join("\n", gaps));
        Assert.True(compiled.Count >= minCompiled, $"only {compiled.Count} bodies compiled");

        var zs = new[] { new Vec3(0.3, -0.2, 0.1), new Vec3(-0.7, 0.4, 0.25), new Vec3(1.1, 0.5, -0.6) };
        var cs = new[] { new Vec3(-0.5, 0.1, 0.2), new Vec3(0.3, -0.6, 0.45) };
        var bad = new List<string>();
        foreach (var it in compiled)
        {
            var m = it.M;
            var env = it.Expr.NewEnv();
            foreach (var z in zs) foreach (var c in cs) for (int n = 0; n < 2; n++)
            {
                double[] a, b;
                try
                {
                    if (it.Quat)
                    {
                        var qz = new Quat(0.2, z.X, z.Y, z.Z); var qc = new Quat(-0.1, c.X, c.Y, c.Z);
                        var x = it.Expr.EvalStepQuat(qz, qc, n, env, new[] { T });
                        var y = (Quat)m.Invoke(null, new object[] { qz, qc, n, T })!;
                        a = new[] { x.W, x.X, x.Y, x.Z }; b = new[] { y.W, y.X, y.Y, y.Z };
                    }
                    else
                    {
                        var x = it.Expr.EvalStep(z, c, n, env, new[] { T });
                        var y = (Vec3)m.Invoke(null, new object[] { z, c, n, T })!;
                        a = new[] { x.X, x.Y, x.Z }; b = new[] { y.X, y.Y, y.Z };
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException) { continue; }
                if (!a.Zip(b).All(p => Close(p.First, p.Second)))
                {
                    bad.Add($"{it.Src}  z={z} n={n}: interp [{string.Join(", ", a)}] vs emitted [{string.Join(", ", b)}]");
                    break;
                }
            }
        }
        Assert.True(bad.Count == 0, "Emitter disagrees with the interpreter:\n" + string.Join("\n", bad.Take(10)));
    }
}
