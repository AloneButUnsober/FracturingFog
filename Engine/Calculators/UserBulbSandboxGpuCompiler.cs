// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserBulbSandboxGpuCompiler.cs
//
// Stage 3A: Sandbox-DSL → Roslyn → ILGPU bridge. Takes a parsed Sandbox AST,
// emits a C# step function via UserBulbSandboxEmitter(gpuTarget: true),
// splices it (with the vec / quat SandboxDE around it) into the text of
// UserBulbShadeKernel — the shared User Bulb kernel with the family kernels'
// shading (#1173-A / G2.4) — compiles that to an in-memory assembly, and loads
// the kernel on the User Bulb device (UserBulbGpuDevice).
//
// Cache: source-string + device keyed. Recompile on source or device change.
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

using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;

namespace FracturingFog.Calculators;

public sealed class UserBulbSandboxGpuCompiler : IDisposable
{
    private UserBulbKernel? _kernel;
    private Accelerator? _acc;
    private string _cachedKey = string.Empty;
    private bool _initFailed;
    // #1169 — kernels a device proved too slow for (process-wide). Keyed by device AND the
    // compiled source: equations differ wildly in cost (a power-8 bulb runs software fp64
    // trig per step, a quaternion square doesn't), so one slow equation must not refuse
    // every other User Bulb equation on that device for the session.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator,
        System.Collections.Concurrent.ConcurrentDictionary<string, string>> s_tooSlow = new();
    public string LastError { get; private set; } = string.Empty;

    /// <summary>#1173-M — the device this path renders on, or null before init.</summary>
    public string? DeviceLabel => _acc is { } a ? UserBulbGpuDevice.Label(a) : null;

    public bool TryInit()
    {
        if (_initFailed) return false;
        if (!UserBulbGpuDevice.TryAcquire(out var acc, out var err)) { LastError = err; _initFailed = true; return false; }
        if (!ReferenceEquals(acc, _acc)) { _acc = acc; _kernel = null; _cachedKey = string.Empty; }
        return true;
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

        var method = asm.GetType("FracturingFog.Calculators.Gpu.SandboxBulbShade")?.GetMethod("Kernel");
        if (method == null) { LastError = "Internal: emitted kernel method not found."; return false; }
        return LoadKernel(method, key);
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

        var method = asm.GetType("FracturingFog.Calculators.Gpu.SandboxBulbShade")?.GetMethod("Kernel");
        if (method == null) { LastError = "Internal: emitted kernel method not found (chain)."; return false; }
        return LoadKernel(method, key);
    }

    private bool LoadKernel(MethodInfo method, string key)
    {
        try
        {
            var del = (Action<Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, GpuRenderParams, ArrayView<double>,
                    ArrayView<uint>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<uint>, ArrayView<uint>>)
                Delegate.CreateDelegate(typeof(Action<Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, GpuRenderParams, ArrayView<double>,
                    ArrayView<uint>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<uint>, ArrayView<uint>>), method);
            _kernel = UserBulbGpuDispatch.Load(_acc!, del);
            _cachedKey = key;
            LastError = string.Empty;
            return true;
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

    /// <summary>Render one frame (#1173-A: the shared kernel contract — colour, optional
    /// G-buffers, palette / albedo LUT / HDRI tables). False = fall back.</summary>
    public bool Render(uint[] outBuffer, double[] pArr, GpuRaymarchParams r, GpuShadingParams sp, GpuRenderParams q,
        uint[]? palette, float[]? depthOut, float[]? normalOut, float[]? hdrOut,
        uint[]? albedoLut, uint[]? hdri, System.Threading.CancellationToken ct = default)
    {
        if (_kernel == null || _acc == null) return false;
        var slowKeys = s_tooSlow.GetOrCreateValue(_acc);
        if (slowKeys.TryGetValue(_cachedKey, out var slow)) { LastError = slow; return false; }
        try
        {
            var run = UserBulbGpuDispatch.Run(_acc, _kernel, outBuffer, r, sp, q, pArr,
                palette, depthOut, normalOut, hdrOut, albedoLut, hdri, ct);
            if (run == GpuDispatchResult.TooSlow)
            {
                LastError = GpuTiledDispatch.TooSlowMessage("User Bulb");
                slowKeys[_cachedKey] = LastError;
                return false;
            }
            return run == GpuDispatchResult.Completed;
        }
        catch (Exception ex)
        {
            LastError = $"GPU render failed: {ex.Message}";
            GpuAcceleratorHost.ReportRenderFault(_acc, ex);
            return false;
        }
    }

    public void Dispose() { _kernel = null; _acc = null; }

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
        AppendStepFn(sb, stepBody, paramNames, quatMode);
        return SpliceIntoTemplate(sb.ToString(), quatMode);
    }

    /// <summary>#1173-A — the shared kernel's source text (UserBulbShadeKernel.cs,
    /// embedded) with its built-in DE region swapped for <paramref name="stepFn"/> +
    /// the vec / quat SandboxDE, and the class renamed.</summary>
    public static string SpliceIntoTemplate(string stepFn, bool quatMode)
    {
        string t = TemplateSource;
        const string begin = "//@@USERDE-BEGIN", end = "//@@USERDE-END";
        int i = t.IndexOf(begin, StringComparison.Ordinal);
        int j = t.IndexOf(end, StringComparison.Ordinal);
        if (i < 0 || j < i) throw new InvalidOperationException("UserBulbShadeKernel template markers missing");
        var region = new StringBuilder();
        region.AppendLine(begin);
        region.Append(stepFn);
        region.AppendLine(quatMode ? QuatSandboxDESource : VecSandboxDESource);
        // Not inlined, as in the template: one DE function, ~16 call sites (JIT ~48 s → ~5 s).
        region.AppendLine("    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        region.AppendLine("    private static double UserDE(double cx, double cy, double cz, in GpuRenderParams q, ArrayView<double> __p)");
        region.AppendLine("        => SandboxDE(cx, cy, cz, q, __p);");
        region.Append("    ");
        string spliced = t.Substring(0, i) + region + t.Substring(j);
        const string cls = "public static class UserBulbShadeKernel";
        if (!spliced.Contains(cls)) throw new InvalidOperationException("UserBulbShadeKernel template class missing");
        return spliced.Replace(cls, "public static class SandboxBulbShade");
    }

    private static string? s_template;
    private static string TemplateSource => s_template ??= LoadTemplate();

    private static string LoadTemplate()
    {
        using var st = typeof(UserBulbShadeKernel).Assembly.GetManifestResourceStream("FracturingFog.UserBulbShadeKernel.cs")
            ?? throw new InvalidOperationException("embedded UserBulbShadeKernel.cs missing");
        using var rd = new StreamReader(st);
        return rd.ReadToEnd();
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

    // Vec mode. UseAnalyticDE 1 = the analytic power DE (twin of
    // UserBulbAnalyticDE.PowerDE); 0 = #1112 the numerical Jacobian, Julia included
    // (twin of UserBulbCalculator.UserBulbDE: three perturbed trajectories, the largest
    // forward-difference column length as |dz/dc|). `!(r <= bailout)` is the CPU's
    // `!IsFinite(r) || r > bailout` for a non-negative r.
    private const string VecSandboxDESource = @"    private static double SandboxDE(double cx, double cy, double cz, GpuRenderParams p, ArrayView<double> __p) {
        if (p.UseAnalyticDE != 0) {
            var c = new Vec3(cx, cy, cz);
            var z = new Vec3(0.0, 0.0, 0.0);
            double dr = 1.0, r = 0.0;
            for (int i = 0; i < p.DEIter; i++) {
                r = z.Length;
                if (!(r <= p.Bailout)) break;
                dr = p.Power * Math.Pow(r, p.Power - 1.0) * dr + 1.0;
                z = Step(z, c, i, __p);
            }
            if (r < 1e-12 || dr < 1e-12) return 0.5 * r / Math.Max(dr, 1e-10);
            return 0.5 * Math.Log(Math.Max(r, 1.0)) * r / dr;
        }

        double h = p.JacH;
        Vec3 cB, cX, cY, cZ;
        Vec3 zB, zX, zY, zZ;
        if (p.JuliaMode != 0) {
            cB = cX = cY = cZ = new Vec3(p.JuliaCX, p.JuliaCY, p.JuliaCZ);
            zB = new Vec3(cx,     cy,     cz);
            zX = new Vec3(cx + h, cy,     cz);
            zY = new Vec3(cx,     cy + h, cz);
            zZ = new Vec3(cx,     cy,     cz + h);
        } else {
            cB = new Vec3(cx,     cy,     cz);
            cX = new Vec3(cx + h, cy,     cz);
            cY = new Vec3(cx,     cy + h, cz);
            cZ = new Vec3(cx,     cy,     cz + h);
            zB = zX = zY = zZ = new Vec3(0.0, 0.0, 0.0);
        }
        double rN = 0.0;
        for (int i = 0; i < p.DEIter; i++) {
            rN = zB.Length;
            if (!(rN <= p.Bailout)) break;
            zB = Step(zB, cB, i, __p);
            zX = Step(zX, cX, i, __p);
            zY = Step(zY, cY, i, __p);
            zZ = Step(zZ, cZ, i, __p);
        }
        double j0 = (zX - zB).Length / h;
        double j1 = (zY - zB).Length / h;
        double j2 = (zZ - zB).Length / h;
        double drN = Math.Max(Math.Max(j0, j1), j2);
        return 0.5 * rN / Math.Max(drN, 1e-10);
    }";

    // Wave 4.6 — Quat unified DE: branches on JuliaMode (c constant vs per-pixel) and
    // UseAnalyticDE: 0 = the 5-trajectory numerical Jacobian (twin of
    // UserBulbCalculator.UserBulbQuatDE), 1 = the analytic power DE, 2 = #1112 the exact
    // full-derivative q²+c DE (twin of UserBulbQuatExactDE; DE mode Analytic on a
    // detected square map, independent of the user Step).
    private const string QuatSandboxDESource = @"    private static double SandboxDE(double cx, double cy, double cz, GpuRenderParams p, ArrayView<double> __p) {
        bool julia = p.JuliaMode != 0;
        double h = p.JacH;

        if (p.UseAnalyticDE == 2) {
            Quat q, c, dq;
            if (julia) {
                q = new Quat(p.QuatSliceW, cx, cy, cz);
                c = new Quat(p.JuliaCW, p.JuliaCX, p.JuliaCY, p.JuliaCZ);
                dq = new Quat(1.0, 0.0, 0.0, 0.0);
            } else {
                q = new Quat(0.0, 0.0, 0.0, 0.0);
                c = new Quat(p.QuatSliceW, cx, cy, cz);
                dq = new Quat(0.0, 0.0, 0.0, 0.0);
            }
            double bail2 = p.Bailout * p.Bailout;
            double q2 = q.LengthSquared;
            for (int i = 0; i < p.DEIter; i++) {
                dq = 2.0 * (q * dq);
                if (!julia) dq = dq + new Quat(1.0, 0.0, 0.0, 0.0);
                q = q * q + c;
                q2 = q.LengthSquared;
                if (!(q2 <= bail2)) break;
            }
            double d2 = dq.LengthSquared;
            if (!(q2 < double.PositiveInfinity) || !(d2 < double.PositiveInfinity) || d2 < 1e-30) return 0.0;
            if (q2 < 1.0) return 0.0;
            double qMag = Math.Sqrt(q2);
            return 0.5 * qMag * Math.Log(qMag) / Math.Sqrt(d2);
        }

        if (p.UseAnalyticDE == 1) {
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
                if (!(r <= p.Bailout)) break;
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
            if (!(rN <= p.Bailout)) break;
            zB  = Step(zB,  cB,  i, __p);
            zW  = Step(zW,  cW,  i, __p);
            zX  = Step(zX,  cX,  i, __p);
            zY  = Step(zY,  cY,  i, __p);
            zZc = Step(zZc, cZc, i, __p);
        }
        double j0 = (zW  - zB).Length / h;
        double j1 = (zX  - zB).Length / h;
        double j2 = (zY  - zB).Length / h;
        double j3 = (zZc - zB).Length / h;
        double drN = Math.Max(Math.Max(j0, j1), Math.Max(j2, j3));
        return 0.5 * rN / Math.Max(drN, 1e-10);
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

        // Wave 4.6 / #1173-A — the same DE + shared kernel as the single-step path.
        return SpliceIntoTemplate(sb.ToString(), quatMode);
    }

    private static Assembly? TryRoslynCompile(string source, out string error)
    {
        error = string.Empty;
        var tree = CSharpSyntaxTree.ParseText(source);
        // S-X7.9 (2026-06-23) — single-file-safe ref gathering via RoslynRefs.
        // The legacy asm.Location path threw ArgumentException in single-file
        // publish (Location is empty there); System.Runtime + netstandard now
        // resolve through the TPA fallback baked into the shared helper.
        var refs = RoslynRefs.GatherRefs(
            typeof(object).Assembly,
            typeof(System.Runtime.CompilerServices.RuntimeHelpers).Assembly,
            typeof(Math).Assembly,
            typeof(Vec3).Assembly,
            typeof(Index1D).Assembly,
            typeof(ArrayView<>).Assembly,
            typeof(GpuRenderParams).Assembly,
            typeof(FracturingFog.Rendering.Lighting.LightingFxData).Assembly);
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
