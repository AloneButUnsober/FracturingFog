// UserBulbSandboxGpuCompiler.cs
//
// Stage 3A: Sandbox-DSL → Roslyn → ILGPU bridge. Takes a parsed Sandbox AST,
// emits a C# step function via UserBulbSandboxEmitter(gpuTarget: true),
// wraps it in a GPU kernel that mirrors UserBulbGpuCalculator.BulbKernel,
// compiles to an in-memory assembly, and loads the kernel via
// LoadAutoGroupedStreamKernel.
//
// Cache: source-string keyed. Recompile on source change. Accelerator + ILGPU
// Context survive across compiles.
//
// Failure modes (Render returns false, LastError populated):
//   - Emitter rejected the AST (e.g., Quat axis mode).
//   - Roslyn compile errors (user wrote something Sandbox parsed but C# rejects).
//   - ILGPU JIT errors (typically NotSupportedException for IL the device-cap
//     can't lower — Math.Clamp/Throw was the canonical example; Vec3GpuOps
//     fixes it, but new device-cap issues may surface later).
// Caller (UserBulbCalculator) falls back to the existing triplex-power GPU
// kernel or the CPU path on any failure.

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

public sealed class UserBulbSandboxGpuCompiler : IDisposable
{
    private Context? _context;
    private Accelerator? _accelerator;
    private Action<Index1D, ArrayView<uint>, ArrayView<double>, GpuRenderParams>? _kernel;
    private string _cachedKey = string.Empty;
    private bool _initFailed;
    /// <summary>Set once we've fallen back to the CPU accelerator after the
    /// preferred device rejected fp64. Skip future device retries.</summary>
    private bool _usingCpuFallback;
    public string LastError { get; private set; } = string.Empty;

    public bool TryInit()
    {
        if (_accelerator != null) return true;
        if (_initFailed) return false;
        try
        {
            _context = Context.Create(b => b.Default());
            _accelerator = _context.GetPreferredDevice(preferCPU: false).CreateAccelerator(_context);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"GPU init failed: {ex.Message}";
            _initFailed = true;
            return false;
        }
    }

    /// <summary>Tear down current accelerator + swap to CPU. Used as fallback
    /// when the preferred device JIT fails for fp64 (Intel UHD OpenCL etc.).
    /// Kernel cache invalidates because the kernel is bound to the old
    /// accelerator.</summary>
    private bool SwitchToCpuAccelerator()
    {
        if (_usingCpuFallback) return false;
        try
        {
            _accelerator?.Dispose();
            _accelerator = null;
            _kernel = null;
            _cachedKey = string.Empty;
            _accelerator = _context!.GetPreferredDevice(preferCPU: true).CreateAccelerator(_context);
            _usingCpuFallback = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Compile (or recompile, if key changes) the Sandbox source into
    /// a kernel. Returns false on emit/Roslyn/ILGPU failure with LastError set.
    /// </summary>
    public bool TryCompile(string source, IReadOnlyList<string> paramNames, bool quatMode)
    {
        if (!TryInit()) return false;
        string key = BuildKey(source, paramNames, quatMode);
        if (_kernel != null && _cachedKey == key) return true;

        // Parse → emit body.
        SandboxBulbExpression expr;
        try
        {
            var extras = new List<string>(paramNames.Count + 1);
            extras.AddRange(paramNames);
            extras.Add("t");
            expr = SandboxBulbExpression.Parse(source, extras);
        }
        catch (Exception ex)
        {
            LastError = $"Sandbox parse failed: {ex.Message}";
            return false;
        }

        var emit = UserBulbSandboxEmitter.Emit(expr.Root, paramNames, quatMode, gpuTarget: true);
        if (!emit.Ok)
        {
            LastError = $"Emitter rejected source: {emit.Error}";
            return false;
        }

        // Vec mode: a stray Quat reference would mean a bug in the emitter
        // (Quat constants in a non-quat tree). Quat mode legitimately emits
        // Quat references — that's the whole point.
        if (!quatMode && emit.Body!.Contains("Quat"))
        {
            LastError = "GPU: vec-mode body unexpectedly references Quat.";
            return false;
        }

        string kernelSrc = BuildKernelSource(emit.Body!, paramNames, quatMode);
        Assembly? asm = TryRoslynCompile(kernelSrc, out var rerr);
        if (asm == null) { LastError = $"Roslyn compile failed: {rerr}"; return false; }

        var method = asm.GetType("FracturingFogDyn.SandboxBulbGpu")?.GetMethod("Kernel");
        if (method == null) { LastError = "Internal: emitted kernel method not found."; return false; }

        var del = (Action<Index1D, ArrayView<uint>, ArrayView<double>, GpuRenderParams>)
            Delegate.CreateDelegate(
                typeof(Action<Index1D, ArrayView<uint>, ArrayView<double>, GpuRenderParams>),
                method);

        try
        {
            _kernel = _accelerator!.LoadAutoGroupedStreamKernel<Index1D, ArrayView<uint>, ArrayView<double>, GpuRenderParams>(del);
            _cachedKey = key;
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsFloat64Failure(ex) && SwitchToCpuAccelerator())
        {
            // Preferred device lacks fp64 (e.g. Intel UHD OpenCL). Retry on
            // CPU accelerator — slower but always supports the full math surface.
            try
            {
                _kernel = _accelerator!.LoadAutoGroupedStreamKernel<Index1D, ArrayView<uint>, ArrayView<double>, GpuRenderParams>(del);
                _cachedKey = key;
                LastError = $"GPU lacks fp64; fell back to CPU accelerator.";
                return true;
            }
            catch (Exception ex2)
            {
                LastError = $"ILGPU JIT failed even on CPU accelerator: {ex2.GetType().Name}: {ex2.Message}";
                _kernel = null;
                _cachedKey = string.Empty;
                return false;
            }
        }
        catch (Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append("ILGPU JIT failed: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            for (var e = ex.InnerException; e != null; e = e.InnerException)
                sb.Append(" | inner: ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            LastError = sb.ToString();
            _kernel = null;
            _cachedKey = string.Empty;
            return false;
        }
    }

    /// <summary>Wave 4.5 — Sandbox chain compile. Each step's body is emitted
    /// independently with a slot-map exposing prior-step output names; bodies
    /// inline into a single Step() with each step output cached in a typed
    /// local. Mirrors <see cref="UserBulbCalculator.WrapUserSourceChain"/> on
    /// the CPU side but reuses the existing GPU kernel scaffolding
    /// (Step → SandboxDE → BulbKernel raymarch).</summary>
    public bool TryCompileChain(
        IReadOnlyList<UserBulbChainStep> steps,
        IReadOnlyList<string> paramNames,
        bool quatMode)
    {
        if (!TryInit()) return false;
        if (steps == null || steps.Count == 0) { LastError = "Chain has no steps."; return false; }
        string key = BuildChainKey(steps, paramNames, quatMode);
        if (_kernel != null && _cachedKey == key) return true;

        // Parse chain (shared scope across steps; output slots tracked).
        SandboxBulbChain chain;
        try
        {
            var extras = new List<string>(paramNames.Count + 1);
            extras.AddRange(paramNames);
            extras.Add("t");
            chain = SandboxBulbChain.Parse(steps, extras);
        }
        catch (Exception ex) { LastError = $"Sandbox chain parse failed: {ex.Message}"; return false; }

        // Per-step emit. Each iteration emits step body referencing prior
        // outputs via the slot-map; after emit, the step's output slot is
        // appended to the map so subsequent steps see it.
        var stepKind = quatMode ? SbxEmitKind.Quat : SbxEmitKind.Vec;
        var priorMap = new Dictionary<int, (string Name, SbxEmitKind Kind)>();
        var stepLocals = new List<string>(steps.Count); // local var names
        var stepBodies = new List<string>(steps.Count); // emitted C# bodies
        for (int i = 0; i < steps.Count; i++)
        {
            string raw = string.IsNullOrWhiteSpace(steps[i].OutputName) ? $"step{i}" : steps[i].OutputName!;
            string localName = SanitizeIdent(raw, i);
            var emit = UserBulbSandboxEmitter.Emit(
                chain.StepRoots[i],
                paramNames,
                quatMode,
                gpuTarget: true,
                priorMap);
            if (!emit.Ok) { LastError = $"Emitter rejected chain step {i}: {emit.Error}"; return false; }
            if (!quatMode && emit.Body!.Contains("Quat"))
            { LastError = $"GPU chain step {i}: vec-mode body unexpectedly references Quat."; return false; }
            stepBodies.Add(emit.Body!);
            stepLocals.Add(localName);
            priorMap[chain.StepOutputSlots[i]] = (localName, stepKind);
        }

        string kernelSrc = BuildChainKernelSource(stepBodies, stepLocals, paramNames, quatMode);
        Assembly? asm = TryRoslynCompile(kernelSrc, out var rerr);
        if (asm == null) { LastError = $"Roslyn compile failed (chain): {rerr}"; return false; }

        var method = asm.GetType("FracturingFogDyn.SandboxBulbGpu")?.GetMethod("Kernel");
        if (method == null) { LastError = "Internal: emitted kernel method not found (chain)."; return false; }

        var del = (Action<Index1D, ArrayView<uint>, ArrayView<double>, GpuRenderParams>)
            Delegate.CreateDelegate(
                typeof(Action<Index1D, ArrayView<uint>, ArrayView<double>, GpuRenderParams>),
                method);

        try
        {
            _kernel = _accelerator!.LoadAutoGroupedStreamKernel<Index1D, ArrayView<uint>, ArrayView<double>, GpuRenderParams>(del);
            _cachedKey = key;
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsFloat64Failure(ex) && SwitchToCpuAccelerator())
        {
            try
            {
                _kernel = _accelerator!.LoadAutoGroupedStreamKernel<Index1D, ArrayView<uint>, ArrayView<double>, GpuRenderParams>(del);
                _cachedKey = key;
                LastError = "GPU lacks fp64; fell back to CPU accelerator.";
                return true;
            }
            catch (Exception ex2)
            {
                LastError = $"ILGPU JIT failed even on CPU accelerator: {ex2.GetType().Name}: {ex2.Message}";
                _kernel = null; _cachedKey = string.Empty;
                return false;
            }
        }
        catch (Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append("ILGPU JIT failed (chain): ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            for (var e = ex.InnerException; e != null; e = e.InnerException)
                sb.Append(" | inner: ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            LastError = sb.ToString();
            _kernel = null; _cachedKey = string.Empty;
            return false;
        }
    }

    private static bool IsFloat64Failure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e.GetType().Name.Contains("CapabilityNotSupported")) return true;
            if (e.Message != null && e.Message.Contains("Float64")) return true;
        }
        return false;
    }

    public bool Render(uint[] outBuffer, double[] pArr, GpuRenderParams p)
    {
        if (_kernel == null || _accelerator == null) return false;
        try
        {
            int total = p.Width * p.Height;
            using var devOut = _accelerator.Allocate1D<uint>(total);
            using var devP = _accelerator.Allocate1D<double>(Math.Max(1, pArr.Length));
            if (pArr.Length > 0) devP.View.CopyFromCPU(pArr);
            _kernel(total, devOut.View, devP.View, p);
            _accelerator.Synchronize();
            devOut.CopyToCPU(outBuffer);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"GPU render failed: {ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        _accelerator?.Dispose();
        _context?.Dispose();
        _accelerator = null;
        _context = null;
        _kernel = null;
    }

    private static string BuildKey(string source, IReadOnlyList<string> paramNames, bool quatMode)
    {
        var sb = new StringBuilder();
        sb.Append(source).Append('|').Append(quatMode ? 'Q' : 'V').Append('|');
        for (int i = 0; i < paramNames.Count; i++) sb.Append(paramNames[i]).Append(',');
        return sb.ToString();
    }

    private static string BuildChainKey(IReadOnlyList<UserBulbChainStep> steps, IReadOnlyList<string> paramNames, bool quatMode)
    {
        var sb = new StringBuilder();
        sb.Append("CHAIN|").Append(quatMode ? 'Q' : 'V').Append('|');
        for (int i = 0; i < paramNames.Count; i++) sb.Append(paramNames[i]).Append(',');
        sb.Append('|');
        for (int i = 0; i < steps.Count; i++)
            sb.Append(steps[i].OutputName ?? string.Empty).Append(':').Append(steps[i].Source ?? string.Empty).Append("##");
        return sb.ToString();
    }

    /// <summary>Map a user-supplied chain step output name to a safe C# local
    /// identifier. Fallback to step{i} on empty / invalid input.</summary>
    private static string SanitizeIdent(string raw, int idx)
    {
        if (string.IsNullOrEmpty(raw)) return "step" + idx;
        var sb = new StringBuilder(raw.Length + 1);
        char c0 = raw[0];
        if (!(char.IsLetter(c0) || c0 == '_')) sb.Append('_');
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            sb.Append((char.IsLetterOrDigit(c) || c == '_') ? c : '_');
        }
        return sb.ToString();
    }

    /// <summary>Compose the full kernel source. Mirrors the structure of
    /// UserBulbGpuCalculator.BulbKernel: sphere-clip → raymarch SandboxDE →
    /// forward-diff normals → cheap palette shade. SandboxDE replaces the
    /// hard-coded TriplexPowerDE with the user step compiled from the AST.
    /// In <paramref name="quatMode"/> the step takes Quat z/c (z.W projects
    /// onto p.QuatSliceW) and the DE loop branches on
    /// <c>p.UseAnalyticDE</c> (power-map) vs 5-trajectory numerical Jacobian,
    /// and on <c>p.JuliaMode</c> (Julia substitution of c).
    /// </summary>
    private static string BuildKernelSource(string stepBody, IReadOnlyList<string> paramNames, bool quatMode)
    {
        var sb = new StringBuilder();
        AppendKernelPrelude(sb);

        // Step function — body comes verbatim from emitter.
        AppendStepFn(sb, stepBody, paramNames, quatMode);

        // DE function — analytic-only for vec; analytic + Jacobian + Julia for quat.
        sb.AppendLine(quatMode ? QuatSandboxDESource : VecSandboxDESource);

        // Kernel: same shape as UserBulbGpuCalculator.BulbKernel.
        sb.AppendLine(KernelBodySource);
        sb.AppendLine("} }");
        return sb.ToString();
    }

    private static void AppendKernelPrelude(StringBuilder sb)
    {
        sb.AppendLine("using System;");
        sb.AppendLine("using ILGPU;");
        sb.AppendLine("using ILGPU.Runtime;");
        sb.AppendLine("using FracturingFog.Models;");
        sb.AppendLine("using FracturingFog.Calculators;");
        sb.AppendLine("namespace FracturingFogDyn {");
        sb.AppendLine("public static class SandboxBulbGpu {");
    }

    private static void AppendStepFn(StringBuilder sb, string stepBody, IReadOnlyList<string> paramNames, bool quatMode)
    {
        string stepType = quatMode ? "Quat" : "Vec3";
        sb.Append("    private static ").Append(stepType).Append(" Step(").Append(stepType).Append(" z, ")
          .Append(stepType).AppendLine(" c, int n, ArrayView<double> __p) {");
        for (int i = 0; i < paramNames.Count; i++)
            sb.Append("        double ").Append(paramNames[i]).Append(" = __p[").Append(i).AppendLine("];");
        sb.Append("        double t = __p[").Append(paramNames.Count).AppendLine("];");
        sb.Append("        return ").Append(stepBody).AppendLine(";");
        sb.AppendLine("    }");
    }

    // Vec mode: analytic-power DE only (Wave 4.6 leaves vec-Julia / vec-numerical
    // on the CPU path — out of scope).
    private const string VecSandboxDESource = @"    private static double SandboxDE(double cx, double cy, double cz, GpuRenderParams p, ArrayView<double> __p) {
        var c = new Vec3(cx, cy, cz);
        var z = new Vec3(0.0, 0.0, 0.0);
        double dr = 1.0, r = 0.0;
        for (int i = 0; i < p.DEIter; i++) {
            r = z.Length;
            if (r > p.Bailout) break;
            dr = p.Power * Math.Pow(r, p.Power - 1.0) * dr + 1.0;
            z = Step(z, c, i, __p);
        }
        if (r < 1e-12 || dr < 1e-12) return 0.5 * r / Math.Max(dr, 1e-10);
        return 0.5 * Math.Log(Math.Max(r, 1.0)) * r / dr;
    }";

    // Wave 4.6 — Quat unified DE: branches on JuliaMode (c constant vs per-pixel)
    // and UseAnalyticDE (power-DE vs 5-trajectory forward-diff Jacobian).
    // Numerical-Jacobian path mirrors CPU UserBulbQuatDE: four perturbed
    // trajectories along {W, X, Y, Z} axes; |z_pert - z|/h gives column lengths
    // of ∂z/∂axis; max column length used as conservative spectral-radius proxy.
    private const string QuatSandboxDESource = @"    private static double SandboxDE(double cx, double cy, double cz, GpuRenderParams p, ArrayView<double> __p) {
        bool julia = p.JuliaMode != 0;
        bool analytic = p.UseAnalyticDE != 0;
        double h = p.JacH;

        if (analytic) {
            Quat c0, z0;
            if (julia) {
                c0 = new Quat(p.JuliaCW, p.JuliaCX, p.JuliaCY, p.JuliaCZ);
                z0 = new Quat(p.QuatSliceW, cx, cy, cz);
            } else {
                c0 = new Quat(p.QuatSliceW, cx, cy, cz);
                z0 = Quat.Zero;
            }
            var z = z0; var c = c0;
            double dr = 1.0, r = 0.0;
            for (int i = 0; i < p.DEIter; i++) {
                r = z.Length;
                if (r > p.Bailout) break;
                dr = p.Power * Math.Pow(r, p.Power - 1.0) * dr + 1.0;
                z = Step(z, c, i, __p);
            }
            if (r < 1e-12 || dr < 1e-12) return 0.5 * r / Math.Max(dr, 1e-10);
            return 0.5 * Math.Log(Math.Max(r, 1.0)) * r / dr;
        }

        // 5-trajectory numerical-Jacobian DE.
        Quat cB, cW, cX, cY, cZc;
        Quat zB, zW, zX, zY, zZc;
        if (julia) {
            var jc = new Quat(p.JuliaCW, p.JuliaCX, p.JuliaCY, p.JuliaCZ);
            cB = cW = cX = cY = cZc = jc;
            zB  = new Quat(p.QuatSliceW,     cx,     cy,     cz);
            zW  = new Quat(p.QuatSliceW + h, cx,     cy,     cz);
            zX  = new Quat(p.QuatSliceW,     cx + h, cy,     cz);
            zY  = new Quat(p.QuatSliceW,     cx,     cy + h, cz);
            zZc = new Quat(p.QuatSliceW,     cx,     cy,     cz + h);
        } else {
            cB  = new Quat(p.QuatSliceW,     cx,     cy,     cz);
            cW  = new Quat(p.QuatSliceW + h, cx,     cy,     cz);
            cX  = new Quat(p.QuatSliceW,     cx + h, cy,     cz);
            cY  = new Quat(p.QuatSliceW,     cx,     cy + h, cz);
            cZc = new Quat(p.QuatSliceW,     cx,     cy,     cz + h);
            zB = zW = zX = zY = zZc = Quat.Zero;
        }
        double rN = 0.0;
        for (int i = 0; i < p.DEIter; i++) {
            rN = zB.Length;
            if (rN > p.Bailout) break;
            zB  = Step(zB,  cB,  i, __p);
            zW  = Step(zW,  cW,  i, __p);
            zX  = Step(zX,  cX,  i, __p);
            zY  = Step(zY,  cY,  i, __p);
            zZc = Step(zZc, cZc, i, __p);
        }
        double invH = 1.0 / Math.Max(h, 1e-12);
        double j0 = (zW  - zB).Length * invH;
        double j1 = (zX  - zB).Length * invH;
        double j2 = (zY  - zB).Length * invH;
        double j3 = (zZc - zB).Length * invH;
        double drN = Math.Max(Math.Max(j0, j1), Math.Max(j2, j3));
        return 0.5 * rN / Math.Max(drN, 1e-10);
    }";

    private const string KernelBodySource = @"    public static void Kernel(Index1D idx, ArrayView<uint> output, ArrayView<double> __p, GpuRenderParams p) {
        int x = idx % p.Width;
        int y = idx / p.Width;
        if (y >= p.Height) return;
        double u = (2.0 * (x + 0.5) / p.Width - 1.0) * p.FovScale * p.Aspect;
        double v = (1.0 - 2.0 * (y + 0.5) / p.Height) * p.FovScale;
        double rdx = p.RightX * u + p.UpX * v + p.FwdX;
        double rdy = p.RightY * u + p.UpY * v + p.FwdY;
        double rdz = p.RightZ * u + p.UpZ * v + p.FwdZ;
        double rl = 1.0 / Math.Sqrt(rdx * rdx + rdy * rdy + rdz * rdz);
        rdx *= rl; rdy *= rl; rdz *= rl;
        double ocx = p.CamX - p.TargetX;
        double ocy = p.CamY - p.TargetY;
        double ocz = p.CamZ - p.TargetZ;
        double bS = ocx * rdx + ocy * rdy + ocz * rdz;
        double cS = ocx * ocx + ocy * ocy + ocz * ocz - p.CullRadiusSq;
        double disc = bS * bS - cS;
        if (disc < 0) { output[idx] = p.InSetColor; return; }
        double sq = Math.Sqrt(disc);
        double tEx = -bS + sq;
        if (tEx < 0) { output[idx] = p.InSetColor; return; }
        double tEn = Math.Max(0.0, -bS - sq);
        double px = p.CamX + rdx * tEn;
        double py = p.CamY + rdy * tEn;
        double pz = p.CamZ + rdz * tEn;
        double tT = tEn;
        bool hit = false;
        int hitStep = 0;
        double hitDist = 0.0;
        for (int step = 0; step < p.MaxSteps; step++) {
            double d = SandboxDE(px, py, pz, p, __p);
            if (d < p.Eps) { hit = true; hitStep = step; hitDist = d; break; }
            if (tT > tEx + 1.0) break;
            px += rdx * d; py += rdy * d; pz += rdz * d;
            tT += d;
        }
        if (!hit) { output[idx] = p.InSetColor; return; }
        double h = p.Eps * 2;
        double invH = 1.0 / h;
        double n0 = (SandboxDE(px + h, py, pz, p, __p) - hitDist) * invH;
        double n1 = (SandboxDE(px, py + h, pz, p, __p) - hitDist) * invH;
        double n2 = (SandboxDE(px, py, pz + h, p, __p) - hitDist) * invH;
        double nl = 1.0 / Math.Sqrt(n0 * n0 + n1 * n1 + n2 * n2 + 1e-20);
        double nx = n0 * nl, ny = n1 * nl, nz = n2 * nl;
        double diffuse = Math.Max(0.0, nx * p.LightX + ny * p.LightY + nz * p.LightZ);
        double ambient = 0.15;
        double shade = ambient + diffuse * (1.0 - ambient);
        double tt = hitStep / (double)p.MaxSteps + tT * 0.05;
        tt -= Math.Floor(tt);
        uint r2 = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(tt * 6.283)));
        uint g2 = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(tt * 6.283 + 2.094)));
        uint b2 = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(tt * 6.283 + 4.188)));
        output[idx] = 0xFF000000u | (r2 << 16) | (g2 << 8) | b2;
    }";

    /// <summary>Build kernel source for chain mode. Each step body is inlined
    /// as a local in <c>Step()</c>, so step N can reference step 0..N-1 by
    /// the local name the emitter resolved them to. Final return is the last
    /// step's local. Surrounding DE / kernel scaffolding is identical to the
    /// single-step <see cref="BuildKernelSource"/>.</summary>
    private static string BuildChainKernelSource(
        IReadOnlyList<string> stepBodies,
        IReadOnlyList<string> stepLocals,
        IReadOnlyList<string> paramNames,
        bool quatMode)
    {
        var sb = new StringBuilder();
        AppendKernelPrelude(sb);

        // Chain Step: each step body inlined as a typed local; prior step
        // outputs reachable by name (emitter resolved via extraSlots).
        string stepType = quatMode ? "Quat" : "Vec3";
        sb.Append("    private static ").Append(stepType).Append(" Step(").Append(stepType).Append(" z, ")
          .Append(stepType).AppendLine(" c, int n, ArrayView<double> __p) {");
        for (int i = 0; i < paramNames.Count; i++)
            sb.Append("        double ").Append(paramNames[i]).Append(" = __p[").Append(i).AppendLine("];");
        sb.Append("        double t = __p[").Append(paramNames.Count).AppendLine("];");
        for (int i = 0; i < stepBodies.Count; i++)
        {
            sb.Append("        ").Append(stepType).Append(' ').Append(stepLocals[i])
              .Append(" = ").Append(stepBodies[i]).AppendLine(";");
        }
        sb.Append("        return ").Append(stepLocals[stepBodies.Count - 1]).AppendLine(";");
        sb.AppendLine("    }");

        // Wave 4.6 — unified DE + Kernel shared with single-step path.
        sb.AppendLine(quatMode ? QuatSandboxDESource : VecSandboxDESource);
        sb.AppendLine(KernelBodySource);
        sb.AppendLine("} }");
        return sb.ToString();
    }

    private static Assembly? TryRoslynCompile(string source, out string error)
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
            MetadataReference.CreateFromFile(typeof(GpuRenderParams).Assembly.Location),
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
            "SandboxBulbGpu_" + Guid.NewGuid().ToString("N"),
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
