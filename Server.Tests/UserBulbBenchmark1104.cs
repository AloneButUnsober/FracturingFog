// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1104 — the measurements behind Docs/Technical/UserBulb-CalcGen-Feasibility.md.
// A manual benchmark, skipped in normal runs. To re-measure (e.g. after one of
// the follow-up slices), remove the Skip and run in Release:
//   dotnet test Server.Tests -c Release --filter FullyQualifiedName~UserBulbBenchmark1104
// Results go to %TEMP%\userbulb-bench-1104.txt.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FracturingFog.Calculators;
using FracturingFog.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class UserBulbBenchmark1104
{
    private static readonly string Out = Path.Combine(Path.GetTempPath(), "userbulb-bench-1104.txt");

    private static readonly (string Name, string Src)[] Vec =
    {
        ("triplex p8 (z^8+c)", "z^8 + c"),
        ("square triplex", "vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c"),
        ("mandelbox", "var v = spherefold(boxfold(z, 1.0), 0.5, 1.0); v * 2.0 + c"),
        ("menger fold (KIFS)", "let v = abs(z) in let v1 = (v.x - v.y < 0 ? vec(v.y, v.x, v.z) : v) in let v2 = (v1.x - v1.z < 0 ? vec(v1.z, v1.y, v1.x) : v1) in let v3 = (v2.y - v2.z < 0 ? vec(v2.x, v2.z, v2.y) : v2) in v3 * 3.0 - vec(2, 2, 0)"),
        ("quartic + sin", "z^4 + sin(z) * 0.5 + c"),
        ("amoser sine", "vec(sin(z.x)*cosh(z.y), cos(z.x)*cos(z.z)*sinh(z.y), sin(z.z)*cosh(z.y)) + c"),
    };

    private static Func<Vec3, Vec3, int, double, Vec3> CompileRoslyn(string body)
    {
        string src = "using System; using FracturingFog.Models; using FracturingFog.Calculators;\n" +
                     "public static class B { public static Vec3 S(Vec3 z, Vec3 c, int n, double t) { return " + body + "; } }";
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(new[] { typeof(Vec3).Assembly.Location }).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists).Select(p => MetadataReference.CreateFromFile(p));
        var comp = CSharpCompilation.Create("b" + Guid.NewGuid().ToString("N"), new[] { CSharpSyntaxTree.ParseText(src) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using var ms = new MemoryStream();
        var r = comp.Emit(ms);
        if (!r.Success) throw new Exception(string.Join("\n", r.Diagnostics));
        var m = Assembly.Load(ms.ToArray()).GetType("B")!.GetMethod("S")!;
        return (Func<Vec3, Vec3, int, double, Vec3>)Delegate.CreateDelegate(typeof(Func<Vec3, Vec3, int, double, Vec3>), m);
    }

    private static double NsPerStep(Func<Vec3, Vec3, int, Vec3> step, int n)
    {
        var c = new Vec3(0.31, -0.22, 0.13);
        Vec3 z = default; double sink = 0;
        for (int i = 0; i < 20000; i++) { z = step(new Vec3(0.1 * (i % 7), 0.2, -0.1), c, i & 7); sink += z.X; }
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            z = step(new Vec3(0.1 * (i % 7) - 0.3, 0.2 - 0.01 * (i % 5), -0.1), c, i & 7);
            sink += z.X;
        }
        sw.Stop();
        if (sink == 42.4242) Console.WriteLine();
        return sw.Elapsed.TotalMilliseconds * 1e6 / n;
    }

    private static double FrameMs(FractalParameters fp, int w, int h, int reps, out string err)
    {
        var calc = new UserBulbCalculator(w, h) { MaxIterations = 256, FractalParameters = fp };
        calc.Compile(fp.UserBulbSource!);
        err = calc.LastError;
        if (!calc.IsCompiled) return double.NaN;
        calc.Calculate();            // warm-up (JIT, GPU compile)
        err = calc.LastError;
        var times = new List<double>();
        for (int i = 0; i < reps; i++)
        {
            fp.UserBulbCameraTheta += 0.001;   // defeat any caching
            var sw = Stopwatch.StartNew();
            calc.Calculate();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }

    [Fact(Skip = "Manual benchmark for #1104 (minutes; machine-dependent). See the file header.")]
    public void Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Machine: {Environment.ProcessorCount} logical CPUs, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, .NET {Environment.Version}");
        try
        {
            using var ctx = FracturingFog.Calculators.Gpu.GpuAcceleratorHost.CreateContext();
            foreach (var d in ctx.Devices) sb.AppendLine($"ILGPU device: {d.AcceleratorType} {d.Name}");
        }
        catch (Exception ex) { sb.AppendLine("ILGPU: " + ex.Message); }
#if DEBUG
        sb.AppendLine("BUILD: DEBUG (re-run in Release for meaningful numbers)");
#else
        sb.AppendLine("BUILD: RELEASE");
#endif
        sb.AppendLine();
        sb.AppendLine("== Per-step cost (ns/step, single thread, Vec3 mode) ==");
        sb.AppendLine("equation | tree-walk | expression-tree (today) | Roslyn typed C# (emitter) | gain vs today");
        const int N = 3_000_000;
        foreach (var (name, src) in Vec)
        {
            var e1 = SandboxBulbExpression.Parse(src, new[] { "t" });
            var env1 = e1.NewEnv();
            double walk = NsPerStep((z, c, n) => e1.EvalStep(z, c, n, env1, new[] { 0.5 }), N);
            var e2 = SandboxBulbExpression.Parse(src, new[] { "t" });
            bool compiled = e2.TryCompile();
            var env2 = e2.NewEnv();
            var extras = new[] { 0.5 };
            double tree = NsPerStep((z, c, n) => e2.EvalStep(z, c, n, env2, extras), N);
            var em = UserBulbSandboxEmitter.Emit(e2.Root, new[] { "t" }, false);
            string roslyn = "n/a", gain = "n/a";
            if (em.Ok)
            {
                var f = CompileRoslyn(em.Body!);
                double r = NsPerStep((z, c, n) => f(z, c, n, 0.5), N);
                roslyn = r.ToString("0.0");
                gain = (tree / r).ToString("0.0") + "x";
            }
            sb.AppendLine($"{name} | {walk:0.0} | {tree:0.0}{(compiled ? "" : " (not compiled)")} | {roslyn} | {gain}");
        }

        sb.AppendLine();
        sb.AppendLine("== End-to-end frame (ms, median of 5, 640x480, Iter 8, MaxSteps 96) ==");
        sb.AppendLine("case | CPU | GPU");
        var cases = new (string Name, string Src, UserBulbAxisModeKind Mode, UserBulbDEModeKind De, bool Julia)[]
        {
            ("z^8+c analytic DE", "z^8 + c", UserBulbAxisModeKind.Vec3, UserBulbDEModeKind.Analytic, false),
            ("z^8+c numerical DE", "z^8 + c", UserBulbAxisModeKind.Vec3, UserBulbDEModeKind.Numerical, false),
            ("mandelbox numerical", "var v = spherefold(boxfold(z, 1.0), 0.5, 1.0); v * 2.0 + c", UserBulbAxisModeKind.Vec3, UserBulbDEModeKind.Numerical, false),
            ("quartic+sin numerical", "z^4 + sin(z) * 0.5 + c", UserBulbAxisModeKind.Vec3, UserBulbDEModeKind.Numerical, false),
            ("quat julia z*z+c numerical", "z * z + c", UserBulbAxisModeKind.Quat, UserBulbDEModeKind.Numerical, true),
            ("quat julia z*z+c analytic (exact)", "z * z + c", UserBulbAxisModeKind.Quat, UserBulbDEModeKind.Analytic, true),
            ("quat qsin(z*z)+c numerical", "qsin(z * z) + c", UserBulbAxisModeKind.Quat, UserBulbDEModeKind.Numerical, true),
        };
        foreach (var cs in cases)
        {
            string RunOne(UserBulbBackendKind be)
            {
                var fp = new FractalParameters
                {
                    UserBulbSource = cs.Src, UserBulbAxisMode = cs.Mode, UserBulbDEMode = cs.De, UserBulbJuliaMode = cs.Julia,
                    UserBulbBackend = be, UserBulbTemporalReuse = false, UserBulbIterations = 8, UserBulbMaxSteps = 96,
                    UserBulbCullRadius = cs.Name.StartsWith("mandelbox") ? 6.0 : 2.5,
                    UserBulbCameraDistance = cs.Name.StartsWith("mandelbox") ? 8.0 : 3.0,
                };
                double ms = FrameMs(fp, 640, 480, 5, out string err);
                return double.IsNaN(ms) ? "fail: " + err : ms.ToString("0") + (string.IsNullOrEmpty(err) ? "" : " [" + err + "]");
            }
            sb.AppendLine($"{cs.Name} | {RunOne(UserBulbBackendKind.CPU)} | {RunOne(UserBulbBackendKind.GPU)}");
        }
        File.WriteAllText(Out, sb.ToString());
    }
}
