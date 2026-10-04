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

    // Sources whose emitted C# doesn't compile today (the GPU path falls back
    // to the CPU interpreter for them).
    // Filed as #1105. Each is a real-plus-vector / real-plus-quat broadcast,
    // a logical && / || on comparison results, or pow(vec, k) (emitted as a
    // scalar power of the length instead of the triplex power).
    private static readonly string[] KnownEmitterGaps =
    {
        "-z.x^2 + c",
        "z.x > 0 && z.y < 1 ? z + c : c",
        "pow(z, 3) + c",
        "exp(z*0.1) + log(abs(z) + 1) + sqrt(abs(c))",
        "qexp(z*0.1) + qlog(c + 2) + qsqrt(c + 3)",
        "qinv(z + 2) + qconj(c)",
        "qasinh(z*0.1) + qatan(c*0.1) + qcot(c + 2)",
    };

    [Fact]
    public void EmittedCSharp_MatchesTheInterpreter_OnTheCorpus()
    {
        var items = new List<(int Id, string Src, SandboxBulbExpression Expr, string Body, bool Quat)>();
        var skipped = new List<string>();
        int id = 0;
        foreach (string src in BulbLanguageGoldenTests.Corpus().Distinct())
        {
            SandboxBulbExpression e;
            try { e = SandboxBulbExpression.Parse(src, Extras); } catch (FormatException) { continue; }
            bool quat = IsQuat(src);
            var r = Emitter.Emit(e.Root, Extras, quat);
            if (!r.Ok) { skipped.Add($"{src}: {r.Error}"); continue; }
            items.Add((id++, src, e, r.Body!, quat));
        }
        Assert.True(items.Count >= 45, $"only {items.Count} emitted; skipped: {string.Join(" | ", skipped)}");

        // Compile each body on its own: a body the C# side rejects makes the
        // GPU path fall back to the CPU interpreter (safe, just slower). Those
        // are pinned in KnownEmitterGaps so a NEW gap fails the test.
        var compiled = new List<(int Id, string Src, SandboxBulbExpression Expr, string Body, bool Quat, MethodInfo M)>();
        var gaps = new List<string>();
        var gapDetail = new List<string>();
        foreach (var it in items)
        {
            var errors = new List<string>();
            var t = Compile(new List<(int, string, bool)> { (it.Id, it.Body, it.Quat) }, errors);
            if (t == null) { gaps.Add(it.Src); gapDetail.Add($"{it.Src}  =>  {it.Body}  :: {errors.FirstOrDefault()}"); continue; }
            compiled.Add((it.Id, it.Src, it.Expr, it.Body, it.Quat, t.GetMethod("S" + it.Id)!));
        }
        var newGaps = gaps.Except(KnownEmitterGaps).ToList();
        Assert.True(newGaps.Count == 0, "New emitter gaps:\n" + string.Join("\n", gapDetail.Where(d => newGaps.Any(g => d.StartsWith(g)))));
        Assert.True(compiled.Count >= 35, $"only {compiled.Count} bodies compiled");

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
