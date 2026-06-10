// UserBulbSandboxGpuSpike.cs
//
// Stage 3A viability spike. Answers: can ILGPU JIT a kernel that lives in a
// Roslyn-compiled, in-memory-loaded assembly, and that references types from
// the main FracturingFog assembly (Vec3) plus the emitted Sandbox-DSL source?
//
// Tiered probes:
//   T1: minimal kernel in runtime asm, no external types. Validates that
//       ILGPU's metadata resolver accepts a MethodInfo whose declaring
//       assembly was loaded from byte[] (no on-disk location).
//   T2: kernel + Vec3 reference. Validates cross-assembly type resolution
//       (runtime asm references main asm's Vec3).
//   T3: kernel calls a SandboxDE built from the Stage-2 emitter output for
//       `triplex(z, 8) + c`. This is the actual 3A consumption shape.
//
// Each tier reports SUCCESS, COMPILE_ERROR (Roslyn diagnostics), or
// JIT_ERROR (ILGPU exception). The result text is what drives the pivot
// decision documented in Docs/UserBulbSandbox-DevPlan.md §3A.
//
// Wired via Program.cs --ubspike flag. Not used at runtime.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

using ILGPU;
using ILGPU.Runtime;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

using FracturingFog.Models;

namespace FracturingFog.Calculators;

public static class UserBulbSandboxGpuSpike
{
    public static int Run()
    {
        Console.WriteLine("── Stage 3A spike: Sandbox → Roslyn → ILGPU ──");
        Console.WriteLine();

        Context? ctx = null;
        Accelerator? acc = null;
        try
        {
            ctx = Context.Create(b => b.Default());
            // CPU accelerator: JITs IL, supports fp64 + full Math.* surface.
            // Removes device-cap noise so the spike isolates the asm-loading
            // question. Real 3A target is still OpenCL/CUDA — see notes.
            acc = ctx.GetPreferredDevice(preferCPU: true).CreateAccelerator(ctx);
            Console.WriteLine($"Accelerator: {acc.AcceleratorType} / {acc.Name}");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] ILGPU init failed: {ex.Message}");
            return 1;
        }

        int failCount = 0;
        if (!RunT1(acc)) failCount++;
        Console.WriteLine();
        if (!RunT2(acc)) failCount++;
        Console.WriteLine();
        if (!RunT3(acc)) failCount++;
        Console.WriteLine();
        if (!RunT4()) failCount++;
        Console.WriteLine();

        Console.WriteLine("── Spike summary ──");
        Console.WriteLine(failCount == 0
            ? "ALL TIERS PASSED → Stage 3A full commit is viable. Proceed with UserBulbSandboxGpuCompiler."
            : $"{failCount} tier(s) failed → pivot recommendation in the failure output above.");

        acc.Dispose();
        ctx.Dispose();
        return failCount == 0 ? 0 : 2;
    }

    // ── T1 ─────────────────────────────────────────────────────────────────
    private static bool RunT1(Accelerator acc)
    {
        Console.WriteLine("[T1] Minimal kernel in runtime asm, no external types.");
        const string src = @"
using ILGPU;
using ILGPU.Runtime;
namespace FracturingFogDyn.Spike {
    public static class T1 {
        public static void Kernel(Index1D idx, ArrayView<double> output) {
            output[idx] = System.Math.Sin((double)idx.X);
        }
    }
}";
        var asm = TryCompile(src, "Spike_T1", out var err);
        if (asm == null) { Console.WriteLine($"  COMPILE_ERROR: {err}"); return false; }
        var method = asm.GetType("FracturingFogDyn.Spike.T1")?.GetMethod("Kernel");
        if (method == null) { Console.WriteLine("  COMPILE_ERROR: Kernel method not found."); return false; }

        try
        {
            var kernel = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>>(
                (Action<Index1D, ArrayView<double>>)Delegate.CreateDelegate(
                    typeof(Action<Index1D, ArrayView<double>>), method));
            const int N = 16;
            using var buf = acc.Allocate1D<double>(N);
            kernel(N, buf.View);
            acc.Synchronize();
            var host = new double[N];
            buf.CopyToCPU(host);
            double sample = host[5];
            double expected = Math.Sin(5.0);
            bool ok = Math.Abs(sample - expected) < 1e-9;
            Console.WriteLine(ok
                ? $"  SUCCESS. host[5]={sample:G6} (expected {expected:G6})."
                : $"  FAIL: host[5]={sample:G6} (expected {expected:G6}).");
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  JIT_ERROR: {ex.GetType().Name}: {ex.Message}");
            for (var e = ex.InnerException; e != null; e = e.InnerException)
                Console.WriteLine($"    inner: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    // ── T2 ─────────────────────────────────────────────────────────────────
    private static bool RunT2(Accelerator acc)
    {
        Console.WriteLine("[T2] Kernel + Vec3 cross-assembly ref.");
        const string src = @"
using ILGPU;
using ILGPU.Runtime;
using FracturingFog.Models;
namespace FracturingFogDyn.Spike {
    public static class T2 {
        public static void Kernel(Index1D idx, ArrayView<double> output) {
            var v = new Vec3((double)idx.X, 0.0, 0.0);
            output[idx] = v.Length;
        }
    }
}";
        var asm = TryCompile(src, "Spike_T2", out var err);
        if (asm == null) { Console.WriteLine($"  COMPILE_ERROR: {err}"); return false; }
        var method = asm.GetType("FracturingFogDyn.Spike.T2")?.GetMethod("Kernel");
        if (method == null) { Console.WriteLine("  COMPILE_ERROR: Kernel method not found."); return false; }

        try
        {
            var kernel = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>>(
                (Action<Index1D, ArrayView<double>>)Delegate.CreateDelegate(
                    typeof(Action<Index1D, ArrayView<double>>), method));
            const int N = 8;
            using var buf = acc.Allocate1D<double>(N);
            kernel(N, buf.View);
            acc.Synchronize();
            var host = new double[N];
            buf.CopyToCPU(host);
            bool ok = Math.Abs(host[5] - 5.0) < 1e-9;
            Console.WriteLine(ok
                ? $"  SUCCESS. host[5]={host[5]:G6} (expected 5.0)."
                : $"  FAIL: host[5]={host[5]:G6}.");
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  JIT_ERROR: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  PIVOT: ILGPU cannot resolve cross-asm types from runtime-loaded asm. Need to either (a) sink Vec3 into runtime asm too, or (b) use plain doubles in the kernel signature.");
            return false;
        }
    }

    // ── T3 ─────────────────────────────────────────────────────────────────
    private static bool RunT3(Accelerator acc)
    {
        Console.WriteLine("[T3] SandboxDE from emitter output for `triplex(z, 8) + c`.");

        // Build emitter output for the canonical source.
        SandboxBulbExpression expr;
        try
        {
            expr = SandboxBulbExpression.Parse("triplex(z, 8) + c", new List<string> { "t" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  SETUP_ERROR: parser failed: {ex.Message}");
            return false;
        }
        var emit = UserBulbSandboxEmitter.Emit(expr.Root, Array.Empty<string>(), quatMode: false);
        if (!emit.Ok)
        {
            Console.WriteLine($"  SETUP_ERROR: emitter failed: {emit.Error}");
            return false;
        }
        Console.WriteLine($"  emitter body: {emit.Body}");

        // The emitter returned a Vec3 expression that calls Vec3.Pow. Vec3.Pow
        // uses Math.Clamp which lowers to a Throw branch ILGPU rejects. The
        // spike inlines a device-safe TriplexPow alongside the kernel and
        // patches the emitter body to call it instead — proving the 3A path
        // is viable once a Vec3GpuOps mirror exists in main asm.
        string patchedBody = emit.Body!.Replace("Vec3.Pow", "TriplexPowSafe");
        string src = $@"
using System;
using ILGPU;
using ILGPU.Runtime;
using FracturingFog.Models;
namespace FracturingFogDyn.Spike {{
    public static class T3 {{
        public static Vec3 TriplexPowSafe(Vec3 v, double n) {{
            double r = v.Length;
            if (r < 1e-12) return Vec3.Zero;
            double theta = Math.Atan2(v.Y, v.X) * n;
            double zr = v.Z / r;
            if (zr > 1.0) zr = 1.0;
            if (zr < -1.0) zr = -1.0;
            double phi = Math.Asin(zr) * n;
            double rn = Math.Pow(r, n);
            double cosp = Math.Cos(phi);
            return new Vec3(rn * cosp * Math.Cos(theta), rn * cosp * Math.Sin(theta), rn * Math.Sin(phi));
        }}
        public static Vec3 Step(Vec3 z, Vec3 c) {{
            return {patchedBody};
        }}
        public static double SandboxDE(double cx, double cy, double cz) {{
            var c = new Vec3(cx, cy, cz);
            var z = new Vec3(0.0, 0.0, 0.0);
            double dr = 1.0, r = 0.0;
            for (int i = 0; i < 64; i++) {{
                r = z.Length;
                if (r > 4.0) break;
                dr = 8.0 * Math.Pow(r, 7.0) * dr + 1.0;
                z = Step(z, c);
            }}
            if (r < 1e-12 || dr < 1e-12) return 0.5 * r / Math.Max(dr, 1e-10);
            return 0.5 * Math.Log(Math.Max(r, 1.0)) * r / dr;
        }}
        public static void Kernel(Index1D idx, ArrayView<double> output, int width) {{
            int x = idx % width;
            int y = idx / width;
            double cx = -1.0 + 2.0 * x / (double)width;
            double cy = -1.0 + 2.0 * y / (double)width;
            output[idx] = SandboxDE(cx, cy, 0.0);
        }}
    }}
}}";
        var asm = TryCompile(src, "Spike_T3", out var err);
        if (asm == null) { Console.WriteLine($"  COMPILE_ERROR: {err}"); return false; }
        var method = asm.GetType("FracturingFogDyn.Spike.T3")?.GetMethod("Kernel");
        if (method == null) { Console.WriteLine("  COMPILE_ERROR: Kernel method not found."); return false; }

        try
        {
            var kernel = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, int>(
                (Action<Index1D, ArrayView<double>, int>)Delegate.CreateDelegate(
                    typeof(Action<Index1D, ArrayView<double>, int>), method));
            const int W = 32;
            const int N = W * W;
            using var buf = acc.Allocate1D<double>(N);
            kernel(N, buf.View, W);
            acc.Synchronize();
            var host = new double[N];
            buf.CopyToCPU(host);

            // Sanity: any finite, any < 0.1 (inside-set candidate), any > 0.1.
            int finite = 0, inSet = 0, outSet = 0;
            for (int i = 0; i < N; i++)
            {
                if (double.IsFinite(host[i])) finite++;
                if (host[i] < 0.01) inSet++;
                else outSet++;
            }
            bool ok = finite == N && inSet > 0 && outSet > 0;
            Console.WriteLine(ok
                ? $"  SUCCESS. finite={finite}/{N}, inSet={inSet}, outSet={outSet}."
                : $"  FAIL: finite={finite}/{N}, inSet={inSet}, outSet={outSet}.");

            // Parity check against CPU Vec3.Pow path on a single pixel.
            double cpu = CpuRefDE(host.Length > 0 ? -1.0 + 2.0 * 16 / 32.0 : 0,
                                  -1.0 + 2.0 * 16 / 32.0, 0);
            double gpu = host[16 * W + 16];
            bool parity = Math.Abs(cpu - gpu) < 1e-6;
            Console.WriteLine(parity
                ? $"  PARITY ok. center pixel cpu={cpu:G6} gpu={gpu:G6}."
                : $"  PARITY drift. center pixel cpu={cpu:G6} gpu={gpu:G6} delta={Math.Abs(cpu - gpu):G3}.");

            return ok && parity;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  JIT_ERROR: {ex.GetType().Name}: {ex.Message}");
            for (var e = ex.InnerException; e != null; e = e.InnerException)
                Console.WriteLine($"    inner: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    // ── T4: end-to-end UserBulbSandboxGpuCompiler smoke ───────────────────
    private static bool RunT4()
    {
        Console.WriteLine("[T4] UserBulbSandboxGpuCompiler end-to-end on `triplex(z, 8) + c`.");
        using var compiler = new UserBulbSandboxGpuCompiler();
        if (!compiler.TryCompile("triplex(z, 8) + c", Array.Empty<string>(), quatMode: false))
        {
            Console.WriteLine($"  COMPILE_FAIL: {compiler.LastError}");
            return false;
        }

        const int W = 32, H = 32;
        var gp = new GpuRenderParams
        {
            Width = W, Height = H,
            CamX = 0.0, CamY = 0.0, CamZ = -3.0,
            TargetX = 0.0, TargetY = 0.0, TargetZ = 0.0,
            FwdX = 0.0, FwdY = 0.0, FwdZ = 1.0,
            RightX = 1.0, RightY = 0.0, RightZ = 0.0,
            UpX = 0.0, UpY = 1.0, UpZ = 0.0,
            FovScale = Math.Tan(0.5 * 60.0 * Math.PI / 180.0), Aspect = 1.0,
            LightX = 0.577, LightY = 0.577, LightZ = -0.577,
            DEIter = 8, MaxSteps = 64,
            Eps = 1e-3, Bailout = 4.0, CullRadiusSq = 4.0,
            Power = 8.0,
            InSetColor = 0xFF000000u,
        };
        var output = new uint[W * H];
        if (!compiler.Render(output, Array.Empty<double>(), gp))
        {
            Console.WriteLine($"  RENDER_FAIL: {compiler.LastError}");
            return false;
        }
        int hit = 0, bg = 0;
        for (int i = 0; i < output.Length; i++)
        {
            if (output[i] == gp.InSetColor) bg++;
            else hit++;
        }
        bool ok = hit > 0 && bg > 0;
        Console.WriteLine(ok
            ? $"  SUCCESS. hit={hit}, bg={bg}."
            : $"  FAIL: hit={hit}, bg={bg}.");
        return ok;
    }

    // Mirror of the SandboxDE loop, evaluated against the real Vec3 path.
    private static double CpuRefDE(double cx, double cy, double cz)
    {
        var c = new Vec3(cx, cy, cz);
        var z = Vec3.Zero;
        double dr = 1.0, r = 0.0;
        for (int i = 0; i < 64; i++)
        {
            r = z.Length;
            if (r > 4.0) break;
            dr = 8.0 * Math.Pow(r, 7.0) * dr + 1.0;
            z = Vec3.Pow(z, 8.0) + c;
        }
        if (r < 1e-12 || dr < 1e-12) return 0.5 * r / Math.Max(dr, 1e-10);
        return 0.5 * Math.Log(Math.Max(r, 1.0)) * r / dr;
    }

    // ── Roslyn compile to in-memory assembly ───────────────────────────────
    private static Assembly? TryCompile(string source, string asmName, out string error)
    {
        error = string.Empty;
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.RuntimeHelpers).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Math).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Vec3).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Index1D).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ArrayView<>).Assembly.Location),
            MetadataReference.CreateFromFile(
                Path.Combine(
                    System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                    "System.Runtime.dll")),
            MetadataReference.CreateFromFile(
                Path.Combine(
                    System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                    "netstandard.dll")),
        };
        var compilation = CSharpCompilation.Create(
            asmName + "_" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            refs,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release));

        using var ms = new MemoryStream();
        EmitResult emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var sb = new StringBuilder();
            foreach (var d in emit.Diagnostics)
                if (d.Severity == DiagnosticSeverity.Error) sb.AppendLine(d.ToString());
            error = sb.ToString();
            return null;
        }
        ms.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(ms.ToArray());
    }
}
