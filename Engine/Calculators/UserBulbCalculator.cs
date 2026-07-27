// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserBulbCalculator.cs
//
// CPU distance-estimation raymarcher for a 3D escape-time fractal whose
// per-iteration step function is supplied at runtime as a C# expression body,
// compiled via Roslyn scripting. Conceptually the 3D analogue of
// UserEquationCalculator: that one drives 2D escape-time over Complex; this
// one drives Mandelbulb-style raymarched 3D over Vec3.
//
// User source signature (wrapped before compile):
//   Vec3 Step(Vec3 z, Vec3 c, int n)  -> returns new z
//
// DE estimation: no closed-form |dz/dc| for an arbitrary user step, so we
// approximate the Jacobian numerically. Per DE call, four iteration
// trajectories run in lockstep — base c plus three perturbed-c trajectories
// (c + h·êx, c + h·êy, c + h·êz). After iteration, the three column lengths
// (|z_perturbed − z_base| / h) bound the Jacobian; we take the max column
// norm as a conservative spectral-radius proxy. Final DE: 0.5 · r / |J|
// (Lipschitz form). Hubbard–Douady's log(r)·r/|J| only holds for power maps
// z → z^p + c; arbitrary user steps (trig, hyperbolic, polynomial mixes)
// do not have power-law growth, so the log factor distorts surfaces.
//
// Cost: 4× delegate calls per DE iter (vs 1× for the heuristic path). For
// the typical 96-step raymarch × 8 DE iters × 4 normal probes per pixel
// this is the dominant work item — expect frame times in the 30–60 s range
// on midrange CPUs at 800×600. Accuracy is the trade — surfaces stay
// geometrically correct for highly non-conformal maps where the Lipschitz
// proxy would over- or under-estimate.
//
// Surface normals are forward differences on DE field (3 extra probes per
// shaded pixel; base value reused from raymarch hit).

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

using FracturingFog.Calculators;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog;

public sealed class UserBulbCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 96;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    public string LastError { get; private set; } = string.Empty;
    /// <summary>0-based character index into the most-recent source where the
    /// last parser error occurred. -1 when no error or error has no position.</summary>
    public int LastErrorPosition { get; private set; } = -1;
    /// <summary>Length of the offending substring at <see cref="LastErrorPosition"/>.</summary>
    public int LastErrorLength { get; private set; } = 0;
    public bool IsCompiled => _compiled != null || _compiledQuat != null;

    /// <summary>Closed-form DE pattern detected for the currently-compiled
    /// source. <see cref="AnalyticDEKind.None"/> when no pattern matched
    /// (numerical-Jacobian DE is used in that case).</summary>
    public AnalyticDEPattern AnalyticPattern => _analyticPattern;

    /// <summary>Sample DE for mesh export. Uses currently-compiled fn + params.</summary>
    public double SampleDE(double x, double y, double z)
    {
        if (_compiled == null && _compiledQuat == null) return double.PositiveInfinity;
        double[] pArr = new double[_compiledParamNames.Length + 1];
        var ps = FractalParameters.UserBulbParams;
        if (ps != null)
        {
            for (int i = 0; i < _compiledParamNames.Length; i++)
                pArr[i] = ps.Find(q => q.Name == _compiledParamNames[i])?.Value ?? 0.0;
        }
        pArr[_compiledParamNames.Length] = FractalParameters.UserBulbTime;
        int iter = Math.Max(2, FractalParameters.UserBulbIterations);
        double bailout = Math.Max(1, FractalParameters.UserBulbBailout);
        double jacH = Math.Max(1e-8, FractalParameters.UserBulbJacobianH);
        bool jul = FractalParameters.UserBulbJuliaMode;
        if (_compiledQuat != null)
        {
            return UserBulbQuatDE(_compiledQuat, FractalParameters.UserBulbQuatSliceW,
                x, y, z, iter, bailout, jacH, pArr,
                jul, FractalParameters.UserBulbJuliaCW, FractalParameters.UserBulbJuliaCX,
                FractalParameters.UserBulbJuliaCY, FractalParameters.UserBulbJuliaCZ);
        }
        return UserBulbDE(_compiled!, x, y, z, iter, bailout, jacH, pArr,
            jul, FractalParameters.UserBulbJuliaCX, FractalParameters.UserBulbJuliaCY, FractalParameters.UserBulbJuliaCZ,
            FractalParameters.UserBulbKifsScale);
    }

    /// <summary>Build a mesh-export distance sampler that SNAPSHOTS the current
    /// compiled kernel + params, with caller-supplied iteration count and
    /// Jacobian step so an export can resolve more geometric detail than the
    /// live render (native raymarchers use analytic DEs; UserBulb's numerical
    /// Jacobian DE is smoother, so higher iterations + a smaller step close the
    /// gap — #112). The returned delegate is independent of later
    /// <see cref="FractalParameters"/> mutation, so it is safe to run on the
    /// off-thread export while the render keeps going. Returns <c>null</c> when
    /// no kernel is compiled.</summary>
    public Func<double, double, double, double>? MakeExportSampler(int iterations, double jacobianH)
    {
        if (_compiled == null && _compiledQuat == null) return null;

        double[] pArr = new double[_compiledParamNames.Length + 1];
        var ps = FractalParameters.UserBulbParams;
        if (ps != null)
            for (int i = 0; i < _compiledParamNames.Length; i++)
                pArr[i] = ps.Find(q => q.Name == _compiledParamNames[i])?.Value ?? 0.0;
        pArr[_compiledParamNames.Length] = FractalParameters.UserBulbTime;

        int iter = Math.Max(2, iterations);
        double jacH = Math.Max(1e-8, jacobianH);
        double bailout = Math.Max(1, FractalParameters.UserBulbBailout);
        bool jul = FractalParameters.UserBulbJuliaMode;

        // #114 — prefer the analytic DE for export when a power-map pattern was
        // detected. A mesh wants the smooth field (crisp geometry), not the
        // noisy numerical Jacobian; DE Mode Analytic forces it, Auto uses it
        // when it tracks the numerical probe, matching the render.
        bool wantAnalytic =
            _analyticPattern.Kind != AnalyticDEKind.None
            && FractalParameters.UserBulbDEMode != UserBulbDEModeKind.Numerical;
        double power = _analyticPattern.Power;

        if (_compiledQuat != null)
        {
            var fn = _compiledQuat;
            double sliceW = FractalParameters.UserBulbQuatSliceW;
            double jcW = FractalParameters.UserBulbJuliaCW, jcX = FractalParameters.UserBulbJuliaCX;
            double jcY = FractalParameters.UserBulbJuliaCY, jcZ = FractalParameters.UserBulbJuliaCZ;
            bool analytic = wantAnalytic &&
                (FractalParameters.UserBulbDEMode == UserBulbDEModeKind.Analytic
                 || AcceptQuatAnalytic(fn, sliceW, iter, bailout, power, jacH, pArr, jul, jcW, jcX, jcY, jcZ));
            if (analytic)
                return (x, y, z) => UserBulbQuatPowerDE(fn, sliceW, x, y, z, iter, bailout, power, pArr,
                    jul, jcW, jcX, jcY, jcZ);
            return (x, y, z) => UserBulbQuatDE(fn, sliceW, x, y, z, iter, bailout, jacH, pArr,
                jul, jcW, jcX, jcY, jcZ);
        }
        else
        {
            var fn = _compiled!;
            double jcX = FractalParameters.UserBulbJuliaCX, jcY = FractalParameters.UserBulbJuliaCY, jcZ = FractalParameters.UserBulbJuliaCZ;
            double kifsScale = FractalParameters.UserBulbKifsScale;
            bool analytic = wantAnalytic && kifsScale <= 0.0 && !jul &&
                (FractalParameters.UserBulbDEMode == UserBulbDEModeKind.Analytic
                 || UserBulbAnalyticDE.AcceptAuto(fn, _analyticPattern, iter, bailout, jacH, pArr));
            if (analytic)
                return (x, y, z) => UserBulbAnalyticDE.PowerDE(fn, x, y, z, iter, bailout, power, pArr);
            return (x, y, z) => UserBulbDE(fn, x, y, z, iter, bailout, jacH, pArr,
                jul, jcX, jcY, jcZ, kifsScale);
        }
    }

    /// <summary>When true, render at half resolution and nearest-upscale into
    /// ColorBuffer. Used by MainForm during camera drag for interactive frame
    /// rate; full-res render fires after drag ends.</summary>
    public bool LowResPreview { get; set; } = false;

    private Func<Vec3, Vec3, int, double[], Vec3>? _compiled;
    private Func<Quat, Quat, int, double[], Quat>? _compiledQuat;
    private string[] _compiledParamNames = Array.Empty<string>();
    private string _compiledSource = string.Empty;
    private UserBulbAxisModeKind _compiledAxisMode = UserBulbAxisModeKind.Vec3;
    private UserBulbCompilerKind _compiledCompiler = UserBulbCompilerKind.Roslyn;
    private AnalyticDEPattern _analyticPattern = new(AnalyticDEKind.None, 0);
    // True when the compiled step references the sample point `c`. Maps that
    // don't (pure z-folds: KIFS Menger/Sierpinski, Kaleidoscopic-IFS chains)
    // are position-independent under the Mandelbrot seeding (z=0, c=point) and
    // render blank; they need the orbit seeded AT the sample point instead.
    // Default true = classic z=0 + c seeding (safe for z^p+c style maps).
    private bool _mapUsesC = true;
    private readonly UserBulbTemporalCache _cache = new();
    private UserBulbGpuCalculator? _gpu;
    private UserBulbSandboxGpuCompiler? _sandboxGpu;

    public UserBulbCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    /// <summary>
    /// Compiles the user source. Body of:
    ///   Vec3 Step(Vec3 z, Vec3 c, int n) { ... }
    /// Available APIs: full System.Math (re-exported via static import) and
    /// Vec3 (FracturingFog.Models). Vec3 exposes Sin/Cos/Sinh/Cosh/etc as
    /// component-wise statics so users can write Vec3.Sin(z) or just use
    /// Math.Sin(z.X) inside a `new Vec3(...)`.
    /// </summary>
    /// <summary>True when <paramref name="source"/> references the sample
    /// point `c` as a standalone identifier (ignoring `//` and block comments).
    /// Used to pick the orbit seeding convention: maps that use `c` seed at
    /// z=0 (Mandelbrot style); maps that don't seed at the sample point (KIFS
    /// style) so they aren't position-invariant.</summary>
    private static bool SourceReferencesC(string? source)
    {
        if (string.IsNullOrEmpty(source)) return false;
        // Strip comments so `c` in prose ("try c instead of...") doesn't count.
        string stripped = Regex.Replace(source, @"//[^\n]*", " ");
        stripped = Regex.Replace(stripped, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.IsMatch(stripped, @"\bc\b");
    }

    public void Compile(string source)
    {
        LastErrorPosition = -1;
        LastErrorLength = 0;
        var chain = FractalParameters.UserBulbChain;
        bool useChain = chain != null && chain.Count > 0;

        // Decide the orbit seeding convention. A step that never references the
        // sample point `c` (KIFS folds, Kaleidoscopic-IFS) is position-invariant
        // under z=0 seeding and would render blank; seed such maps at the point.
        if (useChain)
        {
            _mapUsesC = false;
            foreach (var st in chain!)
                if (SourceReferencesC(st.Source)) { _mapUsesC = true; break; }
        }
        else
        {
            _mapUsesC = SourceReferencesC(source);
        }

        if (!useChain && string.IsNullOrWhiteSpace(source))
        {
            _compiled = null;
            _compiledQuat = null;
            LastError = "Source is empty";
            return;
        }

        var axisMode = FractalParameters.UserBulbAxisMode;
        var paramNames = ValidateAndExtractParamNames(FractalParameters.UserBulbParams);
        var compiler = FractalParameters.UserBulbCompiler;

        if (compiler == UserBulbCompilerKind.Sandbox)
        {
            if (axisMode == UserBulbAxisModeKind.Quat)
            {
                if (useChain) CompileSandboxChainQuat(chain!, paramNames);
                else CompileSandboxQuat(source, paramNames);
            }
            else
            {
                if (useChain) CompileSandboxChain(chain!, paramNames);
                else CompileSandbox(source, paramNames);
            }
            return;
        }

        try
        {
            string code;
            if (useChain)
            {
                if (axisMode == UserBulbAxisModeKind.Quat)
                {
                    LastError = "Chain mode currently Vec3-only. Switch axis or clear chain.";
                    _compiled = null; _compiledQuat = null;
                    return;
                }
                code = WrapUserSourceChain(chain!, paramNames);
            }
            else
            {
                code = axisMode == UserBulbAxisModeKind.Quat
                    ? WrapUserSourceQuat(source, paramNames)
                    : WrapUserSource(source, paramNames);
            }
            var tree = CSharpSyntaxTree.ParseText(code);
            // S-X7.6 (2026-06-23) — typeof(T).Assembly.Location returns "" in
            // single-file self-contained publish (the assembly is loaded from
            // the embedded bundle, not disk). MetadataReference.CreateFromFile("")
            // throws ArgumentException "value cannot be an empty string
            // (Parameter 'path')" which surfaces under the equation entry as
            // the compile error.
            //
            // S-X7.10 (2026-06-23) — broadened to include every TPA assembly
            // (mirrors CalculatorGenHotLoad). The narrow marker list left
            // generated code with unresolved namespace errors because Roslyn
            // could not see forwarder assemblies it needed to compose the BCL
            // primitives across single-file boundaries.
            var refs = RoslynRefs.GatherAllTpaRefs();
            var compilation = CSharpCompilation.Create(
                "UserBulbDyn_" + Guid.NewGuid().ToString("N"),
                new[] { tree },
                refs,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release));

            using var ms = new System.IO.MemoryStream();
            EmitResult emit = compilation.Emit(ms);
            if (!emit.Success)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var diag in emit.Diagnostics)
                    if (diag.Severity == DiagnosticSeverity.Error) sb.AppendLine(diag.ToString());
                LastError = sb.ToString();
                _compiled = null;
                return;
            }
            ms.Seek(0, System.IO.SeekOrigin.Begin);
            var asm = System.Reflection.Assembly.Load(ms.ToArray());
            var type = asm.GetType("FracturingFogDyn.UserBulbStep");
            var method = type?.GetMethod("Step");
            if (method == null)
            {
                LastError = "Internal: emit produced no Step method.";
                _compiled = null;
                _compiledQuat = null;
                return;
            }
            Func<Vec3, Vec3, int, double[], Vec3>? fn = null;
            Func<Quat, Quat, int, double[], Quat>? fnQ = null;
            if (axisMode == UserBulbAxisModeKind.Quat)
            {
                fnQ = (Func<Quat, Quat, int, double[], Quat>)Delegate.CreateDelegate(
                    typeof(Func<Quat, Quat, int, double[], Quat>), method);
            }
            else
            {
                fn = (Func<Vec3, Vec3, int, double[], Vec3>)Delegate.CreateDelegate(
                    typeof(Func<Vec3, Vec3, int, double[], Vec3>), method);
            }

            // Smoke test: invoke once with finite inputs; reject if it throws
            // or returns non-finite components. Lets the raymarch inner loop
            // drop its try/catch.
            double[] probeParams = new double[paramNames.Length + 1];
            try
            {
                if (axisMode == UserBulbAxisModeKind.Quat)
                {
                    var pq = fnQ!(Quat.Zero, new Quat(0.5, 0.5, 0.5, 0.5), 0, probeParams);
                    if (!double.IsFinite(pq.W) || !double.IsFinite(pq.X) || !double.IsFinite(pq.Y) || !double.IsFinite(pq.Z))
                    {
                        LastError = "Step function returned non-finite components on probe input.";
                        _compiledQuat = null;
                        return;
                    }
                }
                else
                {
                    var probe = fn!(Vec3.Zero, new Vec3(0.5, 0.5, 0.5), 0, probeParams);
                    if (!double.IsFinite(probe.X) || !double.IsFinite(probe.Y) || !double.IsFinite(probe.Z))
                    {
                        LastError = "Step function returned non-finite components on probe input.";
                        _compiled = null;
                        return;
                    }
                }
            }
            catch (Exception probeEx)
            {
                LastError = $"Step function threw on probe: {probeEx.Message}";
                _compiled = null;
                _compiledQuat = null;
                return;
            }

            if (axisMode == UserBulbAxisModeKind.Quat) { _compiledQuat = fnQ; _compiled = null; }
            else { _compiled = fn; _compiledQuat = null; }
            _compiledSource = source;
            _compiledAxisMode = axisMode;
            _compiledCompiler = UserBulbCompilerKind.Roslyn;
            _compiledParamNames = paramNames;
            // #114 — Detect() matches `z*z + c` / `Vec3.Pow(z, N) + c`; the
            // `z*z + c` form is also the quaternion square (Hamilton product),
            // so run it for Quat mode too and let the analytic quaternion DE
            // replace the noisy numerical Jacobian.
            _analyticPattern = UserBulbAnalyticDE.Detect(source);
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (ex is SbxParseException spe) { LastErrorPosition = spe.Position; LastErrorLength = spe.Length; }
            _compiled = null;
            _compiledQuat = null;
        }
    }

    /// <summary>Compile via SandboxBulbExpression interpreter. Adapter delegate
    /// matches Roslyn signature so the raymarch loop stays compiler-agnostic.
    /// Per-thread env scratch avoids per-Step allocation.</summary>
    private void CompileSandbox(string source, string[] paramNames)
    {
        try
        {
            var extras = new System.Collections.Generic.List<string>(paramNames.Length + 1);
            extras.AddRange(paramNames);
            extras.Add("t");
            var expr = SandboxBulbExpression.Parse(source, extras);
            int envSize = expr.EnvSize;
            var envLocal = new System.Threading.ThreadLocal<SbxVal3[]>(() => new SbxVal3[envSize]);
            Func<Vec3, Vec3, int, double[], Vec3> fn = (z, c, n, pp) =>
                expr.EvalStep(z, c, n, envLocal.Value!, pp.AsSpan());

            // Probe.
            double[] probeParams = new double[paramNames.Length + 1];
            Vec3 probe;
            try { probe = fn(Vec3.Zero, new Vec3(0.5, 0.5, 0.5), 0, probeParams); }
            catch (Exception probeEx)
            { LastError = $"Step function threw on probe: {probeEx.Message}"; _compiled = null; _compiledQuat = null; return; }
            if (!double.IsFinite(probe.X) || !double.IsFinite(probe.Y) || !double.IsFinite(probe.Z))
            { LastError = "Step function returned non-finite components on probe input."; _compiled = null; _compiledQuat = null; return; }

            _compiled = fn;
            _compiledQuat = null;
            _compiledSource = source;
            _compiledAxisMode = UserBulbAxisModeKind.Vec3;
            _compiledCompiler = UserBulbCompilerKind.Sandbox;
            _compiledParamNames = paramNames;
            _analyticPattern = UserBulbAnalyticDE.DetectSandbox(expr.Root);
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (ex is SbxParseException spe) { LastErrorPosition = spe.Position; LastErrorLength = spe.Length; }
            _compiled = null;
            _compiledQuat = null;
        }
    }

    /// <summary>Sandbox compiler for Quat axis mode. Parses the same DSL, but
    /// evaluates with Quat-tagged z/c slots and returns a Quat→Quat delegate.</summary>
    private void CompileSandboxQuat(string source, string[] paramNames)
    {
        try
        {
            var extras = new System.Collections.Generic.List<string>(paramNames.Length + 1);
            extras.AddRange(paramNames);
            extras.Add("t");
            var expr = SandboxBulbExpression.Parse(source, extras);
            int envSize = expr.EnvSize;
            var envLocal = new System.Threading.ThreadLocal<SbxVal3[]>(() => new SbxVal3[envSize]);
            Func<Quat, Quat, int, double[], Quat> fnQ = (z, c, n, pp) =>
                expr.EvalStepQuat(z, c, n, envLocal.Value!, pp.AsSpan());

            double[] probeParams = new double[paramNames.Length + 1];
            Quat probe;
            try { probe = fnQ(Quat.Zero, new Quat(0.5, 0.5, 0.5, 0.5), 0, probeParams); }
            catch (Exception probeEx)
            { LastError = $"Step function threw on probe: {probeEx.Message}"; _compiled = null; _compiledQuat = null; return; }
            if (!double.IsFinite(probe.W) || !double.IsFinite(probe.X) || !double.IsFinite(probe.Y) || !double.IsFinite(probe.Z))
            { LastError = "Step function returned non-finite components on probe input."; _compiled = null; _compiledQuat = null; return; }

            _compiled = null;
            _compiledQuat = fnQ;
            _compiledSource = source;
            _compiledAxisMode = UserBulbAxisModeKind.Quat;
            _compiledCompiler = UserBulbCompilerKind.Sandbox;
            _compiledParamNames = paramNames;
            // #114 — detect quaternion power maps (q^K + c / q*q + c) so Quat
            // mode can run the analytic DE instead of the numerical Jacobian.
            _analyticPattern = UserBulbAnalyticDE.DetectSandbox(expr.Root);
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (ex is SbxParseException spe) { LastErrorPosition = spe.Position; LastErrorLength = spe.Length; }
            _compiled = null;
            _compiledQuat = null;
        }
    }

    private void CompileSandboxChainQuat(System.Collections.Generic.List<UserBulbChainStep> steps, string[] paramNames)
    {
        try
        {
            var extras = new System.Collections.Generic.List<string>(paramNames.Length + 1);
            extras.AddRange(paramNames);
            extras.Add("t");
            var chain = SandboxBulbChain.Parse(steps, extras);
            int envSize = chain.EnvSize;
            var envLocal = new System.Threading.ThreadLocal<SbxVal3[]>(() => new SbxVal3[envSize]);
            Func<Quat, Quat, int, double[], Quat> fnQ = (z, c, n, pp) =>
                chain.EvalStepQuat(z, c, n, envLocal.Value!, pp.AsSpan());

            double[] probeParams = new double[paramNames.Length + 1];
            Quat probe;
            try { probe = fnQ(Quat.Zero, new Quat(0.5, 0.5, 0.5, 0.5), 0, probeParams); }
            catch (Exception probeEx)
            { LastError = $"Step function threw on probe: {probeEx.Message}"; _compiled = null; _compiledQuat = null; return; }
            if (!double.IsFinite(probe.W) || !double.IsFinite(probe.X) || !double.IsFinite(probe.Y) || !double.IsFinite(probe.Z))
            { LastError = "Step function returned non-finite components on probe input."; _compiled = null; _compiledQuat = null; return; }

            _compiled = null;
            _compiledQuat = fnQ;
            _compiledSource = string.Join("\n##\n", steps.ConvertAll(s => s.OutputName + ":" + s.Source));
            _compiledAxisMode = UserBulbAxisModeKind.Quat;
            _compiledCompiler = UserBulbCompilerKind.Sandbox;
            _compiledParamNames = paramNames;
            _analyticPattern = new AnalyticDEPattern(AnalyticDEKind.None, 0);
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (ex is SbxParseException spe) { LastErrorPosition = spe.Position; LastErrorLength = spe.Length; }
            _compiled = null;
            _compiledQuat = null;
        }
    }

    /// <summary>Compile a chain of Sandbox steps. Each step references prior
    /// step outputs by name. Final z = last step's return.</summary>
    private void CompileSandboxChain(System.Collections.Generic.List<UserBulbChainStep> steps, string[] paramNames)
    {
        try
        {
            var extras = new System.Collections.Generic.List<string>(paramNames.Length + 1);
            extras.AddRange(paramNames);
            extras.Add("t");
            var chain = SandboxBulbChain.Parse(steps, extras);
            int envSize = chain.EnvSize;
            var envLocal = new System.Threading.ThreadLocal<SbxVal3[]>(() => new SbxVal3[envSize]);
            Func<Vec3, Vec3, int, double[], Vec3> fn = (z, c, n, pp) =>
                chain.EvalStep(z, c, n, envLocal.Value!, pp.AsSpan());

            double[] probeParams = new double[paramNames.Length + 1];
            Vec3 probe;
            try { probe = fn(Vec3.Zero, new Vec3(0.5, 0.5, 0.5), 0, probeParams); }
            catch (Exception probeEx)
            { LastError = $"Step function threw on probe: {probeEx.Message}"; _compiled = null; _compiledQuat = null; return; }
            if (!double.IsFinite(probe.X) || !double.IsFinite(probe.Y) || !double.IsFinite(probe.Z))
            { LastError = "Step function returned non-finite components on probe input."; _compiled = null; _compiledQuat = null; return; }

            _compiled = fn;
            _compiledQuat = null;
            _compiledSource = string.Join("\n##\n", steps.ConvertAll(s => s.OutputName + ":" + s.Source));
            _compiledAxisMode = UserBulbAxisModeKind.Vec3;
            _compiledCompiler = UserBulbCompilerKind.Sandbox;
            _compiledParamNames = paramNames;
            // Chain analytic-DE detection: applies when the final step's
            // power-map operand traces back through Lipschitz-≤1 folds.
            // AcceptAuto in DE mode = Auto catches mis-detects at runtime.
            _analyticPattern = UserBulbAnalyticDE.DetectSandboxChain(chain);
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (ex is SbxParseException spe) { LastErrorPosition = spe.Position; LastErrorLength = spe.Length; }
            _compiled = null;
            _compiledQuat = null;
        }
    }

    private static string WrapUserSource(string body, string[] paramNames)
    {
        string wrappedBody = body.Contains("return") ? body : $"return {body};";
        return $@"
using System;
using System.Numerics;
using static System.Math;
using FracturingFog.Models;

namespace FracturingFogDyn
{{
    public static class UserBulbStep
    {{
        public static Vec3 Step(Vec3 z, Vec3 c, int n, double[] __p)
        {{
            {ParamLocals(paramNames)}
            {wrappedBody}
        }}
    }}
}}
";
    }

    private static string WrapUserSourceQuat(string body, string[] paramNames)
    {
        string wrappedBody = body.Contains("return") ? body : $"return {body};";
        return $@"
using System;
using System.Numerics;
using static System.Math;
using FracturingFog.Models;

namespace FracturingFogDyn
{{
    public static class UserBulbStep
    {{
        public static Quat Step(Quat z, Quat c, int n, double[] __p)
        {{
            {ParamLocals(paramNames)}
            {wrappedBody}
        }}
    }}
}}
";
    }

    private static string WrapUserSourceChain(System.Collections.Generic.List<UserBulbChainStep> steps, string[] paramNames)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Numerics;");
        sb.AppendLine("using static System.Math;");
        sb.AppendLine("using FracturingFog.Models;");
        sb.AppendLine("namespace FracturingFogDyn {");
        sb.AppendLine("public static class UserBulbStep {");
        for (int i = 0; i < steps.Count; i++)
        {
            string body = steps[i].Source ?? "return z;";
            string wrappedBody = body.Contains("return") ? body : $"return {body};";
            sb.AppendLine($"    static Vec3 Step_{i}(Vec3 z, Vec3 c, int n, double[] __p, ChainCtx ctx) {{");
            sb.Append(ParamLocals(paramNames));
            // Expose every prior step's output as a Vec3 local so the user
            // source can reference it as a bare identifier (matches the
            // chain editor docs and the Sandbox chain compiler's behavior).
            for (int j = 0; j < i; j++)
            {
                string priorName = string.IsNullOrWhiteSpace(steps[j].OutputName) ? $"step{j}" : steps[j].OutputName;
                if (!IdentRe.IsMatch(priorName)) continue;
                sb.AppendLine($"        Vec3 {priorName} = ctx.Get(\"{priorName}\");");
            }
            sb.AppendLine($"        {wrappedBody}");
            sb.AppendLine("    }");
        }
        sb.AppendLine("    public static Vec3 Step(Vec3 z, Vec3 c, int n, double[] __p) {");
        sb.AppendLine("        var ctx = new ChainCtx();");
        sb.AppendLine("        Vec3 last = z;");
        for (int i = 0; i < steps.Count; i++)
        {
            string name = string.IsNullOrWhiteSpace(steps[i].OutputName) ? $"step{i}" : steps[i].OutputName;
            sb.AppendLine($"        last = Step_{i}(z, c, n, __p, ctx); ctx.Set(\"{name}\", last);");
        }
        sb.AppendLine("        return last;");
        sb.AppendLine("    } } }");
        return sb.ToString();
    }

    private static string ParamLocals(string[] names)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < names.Length; i++)
            sb.AppendLine($"            double {names[i]} = __p[{i}];");
        // `t` is reserved: animation time, always at end of __p.
        sb.AppendLine($"            double t = __p[__p.Length - 1];");
        return sb.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex IdentRe =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$");

    private static readonly System.Collections.Generic.HashSet<string> ReservedParamNames = new() { "t", "z", "c", "n" };

    private static string[] ValidateAndExtractParamNames(System.Collections.Generic.List<UserBulbParam> ps)
    {
        if (ps == null || ps.Count == 0) return Array.Empty<string>();
        var seen = new System.Collections.Generic.HashSet<string>();
        var names = new System.Collections.Generic.List<string>(ps.Count);
        foreach (var p in ps)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            if (!IdentRe.IsMatch(p.Name)) continue;
            if (ReservedParamNames.Contains(p.Name)) continue;
            if (!seen.Add(p.Name)) continue;
            names.Add(p.Name);
        }
        return names.ToArray();
    }

    public void Calculate(CancellationToken ct = default)
    {
        // Lazy compile / recompile when source, chain, or axis mode changes.
        string chainKey = FractalParameters.UserBulbChain == null || FractalParameters.UserBulbChain.Count == 0
            ? string.Empty
            : string.Join("\n##\n", FractalParameters.UserBulbChain.ConvertAll(s => s.OutputName + ":" + s.Source));
        string effectiveSource = string.IsNullOrEmpty(chainKey)
            ? (FractalParameters.UserBulbSource ?? string.Empty)
            : chainKey;
        bool needsCompile =
            (_compiled == null && _compiledQuat == null)
            || _compiledAxisMode != FractalParameters.UserBulbAxisMode
            || _compiledCompiler != FractalParameters.UserBulbCompiler
            || effectiveSource != _compiledSource;
        if (needsCompile && (!string.IsNullOrWhiteSpace(FractalParameters.UserBulbSource) || !string.IsNullOrEmpty(chainKey)))
        {
            Compile(FractalParameters.UserBulbSource ?? string.Empty);
            _compiledSource = effectiveSource;
        }

        var fn = _compiled;
        var fnQ = _compiledQuat;
        bool quatMode = FractalParameters.UserBulbAxisMode == UserBulbAxisModeKind.Quat;
        if ((quatMode && fnQ == null) || (!quatMode && fn == null))
        {
            Array.Clear(ColorBuffer);
            uint bg = ColorMap.InSetColor;
            for (int i = 0; i < ColorBuffer.Length; i++) ColorBuffer[i] = bg;
            return;
        }
        double sliceW = FractalParameters.UserBulbQuatSliceW;

        // Build param-value array matching compiled names plus trailing time
        // slot. Out-of-band edits (added/removed params since compile) result
        // in 0-fills; recompile triggers when names change via dialog.
        double[] pArr = new double[_compiledParamNames.Length + 1];
        var ps = FractalParameters.UserBulbParams;
        if (ps != null)
        {
            for (int i = 0; i < _compiledParamNames.Length; i++)
            {
                var entry = ps.Find(q => q.Name == _compiledParamNames[i]);
                pArr[i] = entry?.Value ?? 0.0;
            }
        }
        pArr[_compiledParamNames.Length] = FractalParameters.UserBulbTime;

        bool juliaMode = FractalParameters.UserBulbJuliaMode;
        // A map that never references `c` is position-invariant under z=0
        // seeding (blank render). Seed it at the sample point instead — this is
        // exactly the Julia-mode seeding path (perturb the point, c held const),
        // and with jc defaulting to 0 the unused `c` is harmless. Real Julia
        // mode still forces point-seeding regardless.
        if (!_mapUsesC) juliaMode = true;
        double jcX = FractalParameters.UserBulbJuliaCX;
        double jcY = FractalParameters.UserBulbJuliaCY;
        double jcZ = FractalParameters.UserBulbJuliaCZ;
        double jcW = FractalParameters.UserBulbJuliaCW;
        var colorDriver = FractalParameters.UserBulbColorDriver;
        double trapX = FractalParameters.UserBulbOrbitTrapX;
        double trapY = FractalParameters.UserBulbOrbitTrapY;
        double trapZ = FractalParameters.UserBulbOrbitTrapZ;
        int iterAxis = FractalParameters.UserBulbIterComponentAxis;

        bool clipEnabled = FractalParameters.UserBulbClipPlaneEnabled;
        var (clipNX, clipNY, clipNZ) = Normalize3(
            FractalParameters.UserBulbClipPlaneNX,
            FractalParameters.UserBulbClipPlaneNY,
            FractalParameters.UserBulbClipPlaneNZ);
        double clipD = FractalParameters.UserBulbClipPlaneD;

        // Lighting
        var (light2X, light2Y, light2Z) = Normalize3(
            Math.Sin(FractalParameters.UserBulbLight2Phi) * Math.Cos(FractalParameters.UserBulbLight2Theta),
            Math.Cos(FractalParameters.UserBulbLight2Phi),
            Math.Sin(FractalParameters.UserBulbLight2Phi) * Math.Sin(FractalParameters.UserBulbLight2Theta));
        var (light3X, light3Y, light3Z) = Normalize3(
            Math.Sin(FractalParameters.UserBulbLight3Phi) * Math.Cos(FractalParameters.UserBulbLight3Theta),
            Math.Cos(FractalParameters.UserBulbLight3Phi),
            Math.Sin(FractalParameters.UserBulbLight3Phi) * Math.Sin(FractalParameters.UserBulbLight3Theta));
        double L1I = FractalParameters.UserBulbLight1Intensity;
        double L2I = FractalParameters.UserBulbLight2Intensity;
        double L3I = FractalParameters.UserBulbLight3Intensity;
        uint L1C = FractalParameters.UserBulbLight1Color;
        uint L2C = FractalParameters.UserBulbLight2Color;
        uint L3C = FractalParameters.UserBulbLight3Color;
        double shadowSoft = FractalParameters.UserBulbShadowSoft;
        int aoSamples = FractalParameters.UserBulbAOSamples;
        double aoStrength = FractalParameters.UserBulbAOStrength;
        double fogDensity = FractalParameters.UserBulbFogDensity;
        uint bgTop = FractalParameters.UserBulbBgTopColor;
        uint bgBot = FractalParameters.UserBulbBgBottomColor;

        ColorMap.MaxIterations = 256;

        bool lowRes = LowResPreview;
        int fullW = Width;
        int fullH = Height;
        int ss = lowRes ? 1 : Math.Clamp(FractalParameters.UserBulbSuperSample, 1, 4);
        int width = lowRes ? Math.Max(1, fullW / 2) : fullW * ss;
        int height = lowRes ? Math.Max(1, fullH / 2) : fullH * ss;
        uint[] renderBuffer = (lowRes || ss > 1) ? new uint[width * height] : ColorBuffer;
        int deIter = Math.Max(2, FractalParameters.UserBulbIterations);
        int maxSteps = Math.Max(16, FractalParameters.UserBulbMaxSteps);
        double eps = Math.Max(1e-5, FractalParameters.UserBulbEpsilon);
        double bailout = Math.Max(1.0, FractalParameters.UserBulbBailout);
        double jacH = Math.Max(1e-8, FractalParameters.UserBulbJacobianH);
        double cullRadius = Math.Max(0.1, FractalParameters.UserBulbCullRadius);
        double cullRadiusSq = cullRadius * cullRadius;
        // > 0 engages the scalar KIFS/Mandelbox running-derivative DE (Vec3
        // path only) instead of the numerical Jacobian. See UserBulbDE.
        double kifsScale = quatMode ? 0.0 : FractalParameters.UserBulbKifsScale;

        // DE mode selection. Auto: use analytic if pattern detected AND probe agrees.
        var deMode = FractalParameters.UserBulbDEMode;
        bool useAnalytic =
            !juliaMode && kifsScale <= 0.0 && (
                (deMode == UserBulbDEModeKind.Analytic && _analyticPattern.Kind != AnalyticDEKind.None)
                || (deMode == UserBulbDEModeKind.Auto && _analyticPattern.Kind != AnalyticDEKind.None
                    && UserBulbAnalyticDE.AcceptAuto(fn!, _analyticPattern, deIter, bailout, jacH, pArr)));
        double analyticPower = _analyticPattern.Power;

        // #114 — quaternion analytic DE. Enabled (incl. Julia mode) when a
        // power-map pattern is detected and either the user forced Analytic or
        // Auto's probe agrees with the numerical Jacobian. Replaces the noisy
        // finite-difference DE with a smooth single-trajectory one.
        bool useQuatAnalytic =
            quatMode && _analyticPattern.Kind != AnalyticDEKind.None && (
                deMode == UserBulbDEModeKind.Analytic
                || (deMode == UserBulbDEModeKind.Auto
                    && AcceptQuatAnalytic(fnQ!, sliceW, deIter, bailout, analyticPower, jacH, pArr,
                                          juliaMode, jcW, jcX, jcY, jcZ)));

        double camDist = FractalParameters.UserBulbCameraDistance / Math.Max(0.05, Zoom);
        double camTheta = FractalParameters.UserBulbCameraTheta;
        double camPhi = FractalParameters.UserBulbCameraPhi;

        // Orbit camera around target = (CenterX, -CenterY, 0). CenterX/Y is the
        // user pan in world units. Camera distance from target shrinks with
        // Zoom so the same world point stays centered on screen at every zoom
        // level. Previous build added pan to ray-angle u/v instead, which made
        // the apparent center drift as camDist changed.
        double targetX = CenterX;
        double targetY = -CenterY;
        double targetZ = 0.0;

        double camX = targetX + camDist * Math.Sin(camPhi) * Math.Cos(camTheta);
        double camY = targetY + camDist * Math.Cos(camPhi);
        double camZ = targetZ + camDist * Math.Sin(camPhi) * Math.Sin(camTheta);

        var fwd = Normalize3(targetX - camX, targetY - camY, targetZ - camZ);
        const double worldUpX = 0, worldUpY = 1, worldUpZ = 0;
        var right = Normalize3(
            fwd.Y * worldUpZ - fwd.Z * worldUpY,
            fwd.Z * worldUpX - fwd.X * worldUpZ,
            fwd.X * worldUpY - fwd.Y * worldUpX);
        var up = (
            X: right.Y * fwd.Z - right.Z * fwd.Y,
            Y: right.Z * fwd.X - right.X * fwd.Z,
            Z: right.X * fwd.Y - right.Y * fwd.X);

        // Phase 20b — true per-eye camera offset along the right basis.
        double eyeOffset = FractalParameters.Lighting.StereoEyeOffset;
        if (eyeOffset != 0)
        {
            camX += right.X * eyeOffset;
            camY += right.Y * eyeOffset;
            camZ += right.Z * eyeOffset;
        }

        double aspect = (double)width / height;
        double fovRad = FractalParameters.UserBulbFovDegrees * Math.PI / 180.0;
        double fovScale = Math.Tan(0.5 * Math.Clamp(fovRad, 0.05, Math.PI - 0.05));

        var light = Normalize3(
            Math.Sin(FractalParameters.UserBulbLightPhi) * Math.Cos(FractalParameters.UserBulbLightTheta),
            Math.Cos(FractalParameters.UserBulbLightPhi),
            Math.Sin(FractalParameters.UserBulbLightPhi) * Math.Sin(FractalParameters.UserBulbLightTheta));

        // Phase 1c — Lighting struct is authoritative. Legacy
        // FractalParameters.UserBulb{Light*,Ambient,Ao*,Fog*,Bg*} fields are
        // no longer copied here — the FractalParamsView Lighting & FX block
        // drives Light1/2/3, ambient, AO, fog, and sky colours. Bulb-specific
        // AO/fog/bg knobs that aren't yet bound through the Lighting struct
        // (legacy bulb dialog) are kept below as opt-in overrides only when
        // the Lighting struct value is at its untouched default.
        var fx = FractalParameters.Lighting;
        // Treat fx.AoSamples == 0 as "user hasn't dialled this in via the
        // Lighting block" and respect the legacy bulb AO knob; otherwise
        // the Lighting value wins. Same logic for fog.
        if (fx.AoSamples == 0) fx.AoSamples = aoSamples;
        if (fx.AoStrength == 0) fx.AoStrength = aoStrength;
        if (fx.FogDensity == 0) fx.FogDensity = fogDensity;
        // Sky colours: keep legacy bg fallback so first-time bulb scenes
        // render against the same dark sky they always have.
        if (fx.BgTopColor == 0) fx.BgTopColor = bgTop;
        if (fx.BgBottomColor == 0) fx.BgBottomColor = bgBot;

        // DE delegate captured once for ShadingPipeline AO / shadow / volume
        // walks. Mode dispatch matches the primary raymarch above so AO walks
        // sample the same surface (quat / analytic-power / numeric Jacobian).
        // #114 — quaternion DE picker: analytic power-DE when detected+accepted,
        // else the numerical Jacobian. Shared by the primary raymarch, normal
        // estimation, and the AO/shadow/volume walks so they sample one surface.
        double QuatDe(double x, double y, double z) => useQuatAnalytic
            ? UserBulbQuatPowerDE(fnQ!, sliceW, x, y, z, deIter, bailout, analyticPower, pArr, juliaMode, jcW, jcX, jcY, jcZ)
            : UserBulbQuatDE(fnQ!, sliceW, x, y, z, deIter, bailout, jacH, pArr, juliaMode, jcW, jcX, jcY, jcZ);

        DistanceEstimator deDelegate = (x, y, z) => quatMode
            ? QuatDe(x, y, z)
            : useAnalytic
                ? UserBulbAnalyticDE.PowerDE(fn!, x, y, z, deIter, bailout, analyticPower, pArr)
                : UserBulbDE(fn!, x, y, z, deIter, bailout, jacH, pArr, juliaMode, jcX, jcY, jcZ, kifsScale);

        // Phase 4 — G-buffer for SSAO post-pass. Skipped during low-res preview
        // because the SSAO pass is much heavier than the preview budget allows.
        // Allocated at render-buffer dimensions so SSAO runs before downsample.
        float[]? depthBuf = null;
        float[]? normalBuf = null;
        if (!lowRes && fx.SsaoSamples > 0)
        {
            depthBuf = new float[width * height];
            normalBuf = new float[3 * width * height];
            ScreenSpacePost.ClearGBuffer(depthBuf, normalBuf);
        }
        // Phase 7 — HDR buffer for tonemap/bloom. Same low-res gate as SSAO.
        float[]? hdrBuf = null;
        bool wantPost = !lowRes && (fx.ToneMap != ToneMapOperator.None || fx.BloomStrength > 0);
        if (wantPost)
        {
            hdrBuf = new float[3 * width * height];
            ScreenSpacePost.ClearHdrBuffer(hdrBuf);
        }

        // GPU path. Three routes:
        //   (a) Sandbox-DSL quat-mode (Wave 4.6) — kernel runs analytic power-DE
        //       when an analytic pattern is detected AND !juliaMode; otherwise
        //       falls into a 5-trajectory numerical-Jacobian DE (and accepts
        //       Julia mode by holding c constant at the Julia parameter).
        //   (b) Sandbox-DSL vec-mode — analytic-power only (vec-Julia /
        //       vec-numerical on GPU is out of scope this wave).
        //   (c) Legacy Roslyn-source path — UserBulbGpuCalculator's hardcoded
        //       TriplexPowerDE (vec only, !juliaMode, analytic only).
        // Falls through to CPU on any failure.
        bool sandboxQuatGpu = _compiledCompiler == UserBulbCompilerKind.Sandbox && quatMode;
        bool vecAnalyticGpuOk = !juliaMode && _analyticPattern.Kind != AnalyticDEKind.None;
        if (FractalParameters.UserBulbBackend == UserBulbBackendKind.GPU
            && !lowRes
            && kifsScale <= 0.0   // scalar KIFS DE is CPU-only
            && (sandboxQuatGpu || vecAnalyticGpuOk))
        {
            // Quat-mode allows analytic only when the pattern matched and
            // we're not in Julia mode — matches the CPU `useAnalytic` gate.
            bool gpuUseAnalytic = !juliaMode && _analyticPattern.Kind != AnalyticDEKind.None;
            var gp = new GpuRenderParams
            {
                Width = width, Height = height,
                CamX = camX, CamY = camY, CamZ = camZ,
                TargetX = targetX, TargetY = targetY, TargetZ = targetZ,
                FwdX = fwd.X, FwdY = fwd.Y, FwdZ = fwd.Z,
                RightX = right.X, RightY = right.Y, RightZ = right.Z,
                UpX = up.X, UpY = up.Y, UpZ = up.Z,
                FovScale = fovScale, Aspect = aspect,
                LightX = light.X, LightY = light.Y, LightZ = light.Z,
                DEIter = deIter, MaxSteps = maxSteps,
                Eps = eps, Bailout = bailout, CullRadiusSq = cullRadiusSq,
                Power = analyticPower,
                QuatSliceW = FractalParameters.UserBulbQuatSliceW,
                InSetColor = ColorMap.InSetColor,
                // Wave 4.6 — quat-mode Julia + numerical-Jacobian fields.
                JuliaMode = juliaMode ? 1 : 0,
                JuliaCW = jcW, JuliaCX = jcX, JuliaCY = jcY, JuliaCZ = jcZ,
                JacH = jacH,
                UseAnalyticDE = gpuUseAnalytic ? 1 : 0,
            };

            // (a) Sandbox path: vec + quat. Wave 4.5 — chain mode now compiles
            // each step body via the emitter, inlines all step bodies into a
            // single Step() with prior-step outputs visible by name as typed
            // locals. CPU fallback on any failure.
            bool useChainPath = FractalParameters.UserBulbChain != null
                                 && FractalParameters.UserBulbChain.Count > 0;
            if (_compiledCompiler == UserBulbCompilerKind.Sandbox)
            {
                _sandboxGpu ??= new UserBulbSandboxGpuCompiler();
                bool compiled = useChainPath
                    ? _sandboxGpu.TryCompileChain(
                          FractalParameters.UserBulbChain!,
                          _compiledParamNames,
                          quatMode: quatMode)
                    : _sandboxGpu.TryCompile(
                          FractalParameters.UserBulbSource ?? string.Empty,
                          _compiledParamNames,
                          quatMode: quatMode);
                if (compiled && _sandboxGpu.Render(ColorBuffer, pArr, gp))
                {
                    // #84 — GPU path skips the CPU post stack; draw the debug HUD.
                    ScreenSpacePost.ApplyDebugHud(ColorBuffer, fullW, fullH, in fx);
                    return;
                }
                LastError = _sandboxGpu.LastError;
                // Fall through to legacy GPU (vec only) + then CPU.
            }

            // (b) Legacy Roslyn-source path — vec only.
            if (!quatMode)
            {
                var trans = UserBulbIlgpuTranslator.Translate(FractalParameters.UserBulbSource);
                if (trans.Ok)
                {
                    _gpu ??= new UserBulbGpuCalculator();
                    if (_gpu.Render(ColorBuffer, gp))
                    {
                        // #84 — GPU path skips the CPU post stack; draw the HUD.
                        ScreenSpacePost.ApplyDebugHud(ColorBuffer, fullW, fullH, in fx);
                        return;
                    }
                    LastError = _gpu.LastError;
                }
            }
        }

        // Temporal cache: identity blit on unchanged scene+camera.
        string sceneKey = lowRes ? string.Empty : BuildSceneKey();
        bool tempReuse = FractalParameters.UserBulbTemporalReuse && !lowRes;
        if (tempReuse)
        {
            var decision = _cache.Decide(sceneKey, width, height, camX, camY, camZ, fwd.X, fwd.Y, fwd.Z);
            if (decision == ReuseDecision.Identity && _cache.Buffer != null)
            {
                Array.Copy(_cache.Buffer, ColorBuffer, ColorBuffer.Length);
                // #84 — cache stores a HUD-free frame; redraw the HUD on replay.
                ScreenSpacePost.ApplyDebugHud(ColorBuffer, fullW, fullH, in fx);
                return;
            }
        }

        // Cone-march prepass: for each tile, march a cone with widened eps
        // along the tile center ray and cache tMin (entry distance to surface
        // candidate). Per-pixel raymarch starts there with a 5% safety margin.
        const int tileSize = 16;
        int tilesX = (width + tileSize - 1) / tileSize;
        int tilesY = (height + tileSize - 1) / tileSize;
        double[] tileTMin = new double[tilesX * tilesY];
        double coneEps = eps * tileSize * 0.5;
        Parallel.For(0, tilesY, new ParallelOptions { CancellationToken = ct }, ty =>
        {
            if (ct.IsCancellationRequested) return;
            int cy = Math.Min(height - 1, ty * tileSize + tileSize / 2);
            double v = (1.0 - 2.0 * (cy + 0.5) / height) * fovScale;
            for (int tx = 0; tx < tilesX; tx++)
            {
                int cx = Math.Min(width - 1, tx * tileSize + tileSize / 2);
                double u = (2.0 * (cx + 0.5) / width - 1.0) * fovScale * aspect;
                double rdx = right.X * u + up.X * v + fwd.X;
                double rdy = right.Y * u + up.Y * v + fwd.Y;
                double rdz = right.Z * u + up.Z * v + fwd.Z;
                var dn = Normalize3(rdx, rdy, rdz);
                rdx = dn.X; rdy = dn.Y; rdz = dn.Z;

                double ocx = camX - targetX;
                double ocy = camY - targetY;
                double ocz = camZ - targetZ;
                double bS = ocx * rdx + ocy * rdy + ocz * rdz;
                double cS = ocx * ocx + ocy * ocy + ocz * ocz - cullRadiusSq;
                double disc = bS * bS - cS;
                if (disc < 0) { tileTMin[ty * tilesX + tx] = double.PositiveInfinity; continue; }
                double sq = Math.Sqrt(disc);
                double tEn = Math.Max(0.0, -bS - sq);
                double tEx = -bS + sq;
                if (tEx < 0) { tileTMin[ty * tilesX + tx] = double.PositiveInfinity; continue; }

                double px = camX + rdx * tEn;
                double py = camY + rdy * tEn;
                double pz = camZ + rdz * tEn;
                double tT = tEn;
                double tMin = double.PositiveInfinity;
                int coneSteps = Math.Max(8, maxSteps / 4);
                for (int s = 0; s < coneSteps; s++)
                {
                    if (ct.IsCancellationRequested) return;
                    double d = quatMode
                        ? QuatDe(px, py, pz)
                        : useAnalytic
                            ? UserBulbAnalyticDE.PowerDE(fn!, px, py, pz, deIter, bailout, analyticPower, pArr)
                            : UserBulbDE(fn!, px, py, pz, deIter, bailout, jacH, pArr, juliaMode, jcX, jcY, jcZ, kifsScale);
                    if (d < coneEps) { tMin = tT; break; }
                    if (tT > tEx + 1.0) break;
                    px += rdx * d; py += rdy * d; pz += rdz * d;
                    tT += d;
                }
                tileTMin[ty * tilesX + tx] = tMin;
            }
        });

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double v = (1.0 - 2.0 * (y + 0.5) / height) * fovScale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double u = (2.0 * (x + 0.5) / width - 1.0) * fovScale * aspect;
                double rdx = right.X * u + up.X * v + fwd.X;
                double rdy = right.Y * u + up.Y * v + fwd.Y;
                double rdz = right.Z * u + up.Z * v + fwd.Z;
                var dn = Normalize3(rdx, rdy, rdz);
                rdx = dn.X; rdy = dn.Y; rdz = dn.Z;

                // Bounding sphere clip: ray vs sphere centered on target,
                // radius = cullRadius. Skip raymarch entirely if miss.
                double ocx = camX - targetX;
                double ocy = camY - targetY;
                double ocz = camZ - targetZ;
                double bSphere = ocx * rdx + ocy * rdy + ocz * rdz;
                double cSphere = ocx * ocx + ocy * ocy + ocz * ocz - cullRadiusSq;
                double discSphere = bSphere * bSphere - cSphere;
                int idx = rowBase + x;
                if (discSphere < 0)
                {
                    renderBuffer[idx] = SkyColor(rdy, bgBot, bgTop);
                    continue;
                }
                double sqrtDisc = Math.Sqrt(discSphere);
                double tEnter = -bSphere - sqrtDisc;
                double tExit = -bSphere + sqrtDisc;
                if (tExit < 0)
                {
                    renderBuffer[idx] = SkyColor(rdy, bgBot, bgTop);
                    continue;
                }
                double tStart = Math.Max(0.0, tEnter);

                // Cone-march tile hint: start at tile tMin with safety margin.
                // Infinity = center ray missed; corners may still hit, so do
                // NOT skip — fall back to sphere entry.
                int tileIdx = (y / tileSize) * tilesX + (x / tileSize);
                double hint = tileTMin[tileIdx];
                if (!double.IsInfinity(hint))
                    tStart = Math.Max(tStart, hint * 0.9);

                double px = camX + rdx * tStart;
                double py = camY + rdy * tStart;
                double pz = camZ + rdz * tStart;
                double tTotal = tStart;
                bool hit = false;
                int hitStep = 0;
                double hitDist = 0.0;

                for (int step = 0; step < maxSteps; step++)
                {
                    if (ct.IsCancellationRequested) return;
                    double dist = quatMode
                        ? QuatDe(px, py, pz)
                        : useAnalytic
                            ? UserBulbAnalyticDE.PowerDE(fn!, px, py, pz, deIter, bailout, analyticPower, pArr)
                            : UserBulbDE(fn!, px, py, pz, deIter, bailout, jacH, pArr, juliaMode, jcX, jcY, jcZ, kifsScale);
                    if (dist < eps)
                    {
                        // Clip plane: if surface point is on positive side of plane, skip past.
                        if (clipEnabled && (px * clipNX + py * clipNY + pz * clipNZ - clipD) > 0)
                        {
                            double skip = Math.Max(eps * 2, 0.01);
                            px += rdx * skip; py += rdy * skip; pz += rdz * skip;
                            tTotal += skip;
                            continue;
                        }
                        hit = true;
                        hitStep = step;
                        hitDist = dist;
                        break;
                    }
                    if (tTotal > tExit + 1.0) break;
                    px += rdx * dist; py += rdy * dist; pz += rdz * dist;
                    tTotal += dist;
                }

                if (!hit)
                {
                    // Ray-miss → sky backdrop when toggle on, ColorMap
                    // InSetColor when off. SkyColorHdri routes through HDRI
                    // sample when SkyMode=Hdri + HDRI loaded, gradient
                    // BgBottomColor → BgTopColor otherwise.
                    renderBuffer[idx] = fx.ShowSkyBackdrop
                        ? ShadingPipeline.SkyColorHdri(rdx, rdy, rdz, in fx)
                        : ColorMap.InSetColor;
                    continue;
                }

                // Forward-diff normals: reuse hitDist as f(p), 3 extra probes.
                double h = eps * 2;
                double invH = 1.0 / h;
                double dxp = quatMode
                    ? QuatDe(px + h, py, pz)
                    : useAnalytic
                        ? UserBulbAnalyticDE.PowerDE(fn!, px + h, py, pz, deIter, bailout, analyticPower, pArr)
                        : UserBulbDE(fn!, px + h, py, pz, deIter, bailout, jacH, pArr, juliaMode, jcX, jcY, jcZ, kifsScale);
                double dyp = quatMode
                    ? QuatDe(px, py + h, pz)
                    : useAnalytic
                        ? UserBulbAnalyticDE.PowerDE(fn!, px, py + h, pz, deIter, bailout, analyticPower, pArr)
                        : UserBulbDE(fn!, px, py + h, pz, deIter, bailout, jacH, pArr, juliaMode, jcX, jcY, jcZ, kifsScale);
                double dzp = quatMode
                    ? QuatDe(px, py, pz + h)
                    : useAnalytic
                        ? UserBulbAnalyticDE.PowerDE(fn!, px, py, pz + h, deIter, bailout, analyticPower, pArr)
                        : UserBulbDE(fn!, px, py, pz + h, deIter, bailout, jacH, pArr, juliaMode, jcX, jcY, jcZ, kifsScale);
                double n0 = (dxp - hitDist) * invH;
                double n1 = (dyp - hitDist) * invH;
                double n2 = (dzp - hitDist) * invH;
                var nrm = Normalize3(n0, n1, n2);

                // Color driver: feeds ColorMap.Map. Default StepDepth = step + depth.
                float smooth;
                float nA = (float)nrm.X, nB = (float)nrm.Y;
                if (colorDriver == BulbColorDriver.StepDepth)
                {
                    smooth = (float)hitStep * (256f / Math.Max(1, maxSteps))
                           + (float)(tTotal * 4.0);
                }
                else
                {
                    var cm = EvalColorMetrics(fn, fnQ, quatMode, sliceW, px, py, pz, deIter, bailout, pArr,
                        juliaMode, jcW, jcX, jcY, jcZ, trapX, trapY, trapZ);
                    switch (colorDriver)
                    {
                        case BulbColorDriver.OrbitTrap:
                            smooth = (float)(double.IsInfinity(cm.TrapMin) ? 0 : cm.TrapMin * 128.0);
                            break;
                        case BulbColorDriver.EscapeAngle:
                            smooth = (float)((cm.EscapeAngle + Math.PI) * (128.0 / Math.PI));
                            break;
                        case BulbColorDriver.FinalMagnitude:
                            smooth = (float)Math.Min(255.0, cm.FinalR * 32.0);
                            break;
                        case BulbColorDriver.IterComponent:
                            double comp = iterAxis == 1 ? cm.FinalZ.Y : iterAxis == 2 ? cm.FinalZ.Z : cm.FinalZ.X;
                            smooth = (float)((comp + 2.0) * 64.0);
                            break;
                        case BulbColorDriver.Normal:
                            smooth = (float)((nrm.X + 1.0) * 128.0);
                            nA = (float)nrm.Y; nB = (float)nrm.Z;
                            break;
                        default:
                            smooth = (float)hitStep * (256f / Math.Max(1, maxSteps))
                                   + (float)(tTotal * 4.0);
                            break;
                    }
                }
                uint baseColor = (uint)ColorMap.Map(smooth, 0f, 256, nA, nB);

                // Phase 1b — shading delegated to shared pipeline. Lambert +
                // 3-light + DE-cone AO + exp fog computed inside
                // ShadingPipeline.Shade. Bit-identical to the inline path it
                // replaced (uses the packed-albedo overload to avoid
                // byte→float→byte roundtrip quantization).
                var inputs = new ShadingInputs(
                    px, py, pz, nrm.X, nrm.Y, nrm.Z,
                    rdx, rdy, rdz, tTotal, hitDist, hitStep, eps);
                renderBuffer[idx] = ShadingPipeline.Shade(
                    in inputs, baseColor, in fx, deDelegate,
                    idx, depthBuf, normalBuf, hdrBuf);
            }
        });

        // Phase 4 — SSAO post-pass on the render-resolution buffer (before
        // downsample/upscale composites it into ColorBuffer).
        if (depthBuf is not null && normalBuf is not null)
            ScreenSpacePost.ApplySsao(renderBuffer, depthBuf, normalBuf, width, height, in fx);

        // Phase 21b — HDR DoF (hex-bokeh 3-pass) runs before tonemap so bright
        // highlights bloom into proper bokeh discs instead of clipping first.
        if (hdrBuf is not null && depthBuf is not null)
            ScreenSpacePost.ApplyHdrDof(hdrBuf, depthBuf, width, height, in fx);

        // Phase 7 — Tonemap + bloom. Operates on renderBuffer (pre-downsample).
        if (hdrBuf is not null)
            ScreenSpacePost.ApplyToneMapBloom(renderBuffer, hdrBuf, width, height, in fx);

        // Phase 23 — Sobel-on-normal edge ink. Operates on tonemapped bytes
        // pre-downsample so ink lines stay sharp through the upscale pass.
        if (depthBuf is not null && normalBuf is not null)
            ScreenSpacePost.ApplyEdgeInk(renderBuffer, depthBuf, normalBuf, width, height, in fx);

        if (lowRes)
        {
            UpscaleNearest(renderBuffer, width, height, ColorBuffer, fullW, fullH);
        }
        else if (ss > 1)
        {
            DownsampleBox(renderBuffer, width, height, ColorBuffer, fullW, fullH, ss);
        }

        if (!lowRes && ss == 1 && tempReuse)
        {
            // Save buffer for identity blit. Hit-data arrays empty (reproject
            // path not wired yet — Identity decision doesn't need them).
            var empty = Array.Empty<double>();
            var emptyB = Array.Empty<bool>();
            _cache.Save(ColorBuffer, empty, empty, empty, emptyB,
                width, height, sceneKey,
                camX, camY, camZ,
                targetX, targetY, targetZ,
                fwd.X, fwd.Y, fwd.Z,
                right.X, right.Y, right.Z,
                up.X, up.Y, up.Z,
                fovScale, aspect);
        }

        // #84 — light-direction compass etc. Drawn last on the final full-res
        // buffer, AFTER the temporal-cache save above so the cached frame stays
        // HUD-free (the identity-blit path redraws the HUD on replay). Self-guards.
        ScreenSpacePost.ApplyDebugHud(ColorBuffer, fullW, fullH, in fx);
    }

    private string BuildSceneKey()
    {
        var p = FractalParameters;
        // Hash params bank values (named scalars passed to user fn).
        double paramsHash = 0;
        if (p.UserBulbParams != null)
            foreach (var pp in p.UserBulbParams) paramsHash = paramsHash * 31.0 + pp.Value;
        return string.Join("|",
            _compiledSource ?? "",
            p.UserBulbIterations, p.UserBulbBailout,
            p.UserBulbMaxSteps, p.UserBulbEpsilon, p.UserBulbJacobianH,
            p.UserBulbCullRadius, (int)p.UserBulbDEMode,
            (int)p.UserBulbAxisMode, p.UserBulbQuatSliceW,
            // Animation time — required so playback invalidates cache each frame.
            p.UserBulbTime,
            // Julia mode + c
            p.UserBulbJuliaMode, p.UserBulbJuliaCX, p.UserBulbJuliaCY, p.UserBulbJuliaCZ, p.UserBulbJuliaCW,
            // Color driver inputs
            (int)p.UserBulbColorDriver, p.UserBulbOrbitTrapX, p.UserBulbOrbitTrapY, p.UserBulbOrbitTrapZ,
            p.UserBulbIterComponentAxis,
            // 3-light + AO + fog + sky
            p.UserBulbLightTheta, p.UserBulbLightPhi, p.UserBulbLight1Intensity, p.UserBulbLight1Color,
            p.UserBulbLight2Theta, p.UserBulbLight2Phi, p.UserBulbLight2Intensity, p.UserBulbLight2Color,
            p.UserBulbLight3Theta, p.UserBulbLight3Phi, p.UserBulbLight3Intensity, p.UserBulbLight3Color,
            p.UserBulbAOSamples, p.UserBulbAOStrength, p.UserBulbFogDensity,
            p.UserBulbBgTopColor, p.UserBulbBgBottomColor,
            // View + clip
            p.UserBulbFovDegrees, p.UserBulbSuperSample,
            p.UserBulbClipPlaneEnabled, p.UserBulbClipPlaneNX, p.UserBulbClipPlaneNY, p.UserBulbClipPlaneNZ, p.UserBulbClipPlaneD,
            paramsHash,
            ColorMap?.GetType().Name ?? "");
    }

    private static void DownsampleBox(uint[] src, int srcW, int srcH, uint[] dst, int dstW, int dstH, int ss)
    {
        Parallel.For(0, dstH, y =>
        {
            int dRow = y * dstW;
            for (int x = 0; x < dstW; x++)
            {
                int sx0 = x * ss;
                int sy0 = y * ss;
                int rSum = 0, gSum = 0, bSum = 0;
                int cnt = 0;
                for (int dy = 0; dy < ss; dy++)
                {
                    int sRow = (sy0 + dy) * srcW;
                    for (int dx = 0; dx < ss; dx++)
                    {
                        uint p = src[sRow + sx0 + dx];
                        rSum += (int)((p >> 16) & 0xFF);
                        gSum += (int)((p >> 8) & 0xFF);
                        bSum += (int)(p & 0xFF);
                        cnt++;
                    }
                }
                byte rb = (byte)(rSum / cnt);
                byte gb = (byte)(gSum / cnt);
                byte bb = (byte)(bSum / cnt);
                dst[dRow + x] = 0xFF000000u | ((uint)rb << 16) | ((uint)gb << 8) | bb;
            }
        });
    }

    /// <summary>Nearest-neighbor upscale of src into dst. Used by LowResPreview
    /// path; cheap (one indexed read per output pixel).</summary>
    private static void UpscaleNearest(
        uint[] src, int srcW, int srcH,
        uint[] dst, int dstW, int dstH)
    {
        Parallel.For(0, dstH, y =>
        {
            int sy = Math.Min(srcH - 1, y * srcH / dstH);
            int sRow = sy * srcW;
            int dRow = y * dstW;
            for (int x = 0; x < dstW; x++)
            {
                int sx = Math.Min(srcW - 1, x * srcW / dstW);
                dst[dRow + x] = src[sRow + sx];
            }
        });
    }

    /// <summary>
    /// DE for user-supplied step. Iterates z_{n+1} = fn(z_n, c, n) from z = 0
    /// with c = world-space sample point. Numerical Jacobian: three parallel
    /// trajectories run with c perturbed by +h on each axis. Column lengths
    /// of (z_perturbed − z_base) / h bound dz/dc; max column length acts as
    /// the spectral-radius proxy. Final DE: 0.5 · r / |J| (Lipschitz form,
    /// works for arbitrary growth profiles).
    ///
    /// Cost = 4× delegate calls per DE iteration (1 base + 3 perturbed).
    ///
    /// Caller (Compile) smoke-tests the delegate for throw/non-finite so this
    /// hot loop omits try/catch; non-finite r breaks early.
    /// </summary>
    /// <summary>#114 — analytic quaternion power-map DE (single trajectory,
    /// scalar Hubbard–Douady running-derivative), the quaternion twin of
    /// <see cref="UserBulbAnalyticDE.PowerDE"/>. One user-step call per iteration
    /// (vs 5 for the numerical Jacobian) and — crucially — a SMOOTH distance
    /// field, so the quaternion Julia render loses the finite-difference
    /// interference/mottling and the mesh export resolves detail instead of a
    /// blob. Works in Julia mode (z = sample point, c = the Julia constant),
    /// which the concrete <c>QuatJuliaCalculator</c> also does and the old vec
    /// analytic path refused.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double UserBulbQuatPowerDE(
        Func<Quat, Quat, int, double[], Quat> fn,
        double sliceW, double cx, double cy, double cz,
        int iter, double bailout, double power, double[] pArr,
        bool juliaMode, double jcW, double jcX, double jcY, double jcZ)
    {
        Quat q, c;
        if (juliaMode)
        {
            c = new Quat(jcW, jcX, jcY, jcZ);
            q = new Quat(sliceW, cx, cy, cz);
        }
        else
        {
            c = new Quat(sliceW, cx, cy, cz);
            q = Quat.Zero;
        }

        // Julia: differentiate wrt the seed (c constant) → dr = N·r^(N-1)·dr,
        // NO +1 term (matches the concrete QuatJuliaCalculator's dq = 2·q·dq).
        // Mandelbrot: differentiate wrt c → the +1 (dc/dc = 1) applies. Using the
        // +1 in Julia mode inflates dr, underestimates the DE, and collapses the
        // set to a fat smooth ball with washed-out structure.
        double dcTerm = juliaMode ? 0.0 : 1.0;
        double dr = 1.0;
        double r = 0.0;
        for (int i = 0; i < iter; i++)
        {
            r = q.Length;
            if (!double.IsFinite(r) || r > bailout) break;
            dr = power * Math.Pow(r, power - 1) * dr + dcTerm;
            q = fn(q, c, i, pArr);
        }

        if (r < 1e-12 || dr < 1e-12) return 0.5 * r / Math.Max(dr, 1e-10);
        return 0.5 * Math.Log(Math.Max(r, 1.0)) * r / dr;
    }

    /// <summary>#114 — Auto-mode gate for the analytic quaternion DE. Unlike the
    /// vec path, the scalar Hubbard–Douady recurrence is EXACT in magnitude for a
    /// quaternion power map — quaternion norm is multiplicative, so
    /// <c>|q·dq| = |q|·|dq|</c> and <c>dr = N·r^(N-1)·dr</c> holds exactly. The
    /// analytic DE is therefore the ground truth, NOT something to validate
    /// against the numerical Jacobian (which over-smooths a quaternion Julia into
    /// a featureless ball — the reported bug). So accept it whenever it yields a
    /// finite, non-negative field over a few probe points; only a genuinely
    /// broken (blown-up / negative) analytic falls back to numerical. DE Mode =
    /// Analytic bypasses this entirely.</summary>
    private static bool AcceptQuatAnalytic(
        Func<Quat, Quat, int, double[], Quat> fn,
        double sliceW, int iter, double bailout, double power, double jacH, double[] pArr,
        bool juliaMode, double jcW, double jcX, double jcY, double jcZ)
    {
        Span<(double, double, double)> probes = stackalloc (double, double, double)[]
        {
            (0.4, 0.3, 0.2), (0.9, 0.0, 0.0), (0.2, 0.6, 0.4),
        };
        foreach (var (px, py, pz) in probes)
        {
            double d = UserBulbQuatPowerDE(fn, sliceW, px, py, pz, iter, bailout, power, pArr,
                juliaMode, jcW, jcX, jcY, jcZ);
            if (!double.IsFinite(d) || d < 0.0) return false;
        }
        return true;
    }

    /// <summary>Quaternion numerical-Jacobian DE. Perturb c.W/X/Y/Z (5
    /// trajectories total). Project z to Vec3 (X/Y/Z) for raymarch position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double UserBulbQuatDE(
        Func<Quat, Quat, int, double[], Quat> fn,
        double sliceW, double cx, double cy, double cz,
        int iter, double bailout, double h, double[] pArr,
        bool juliaMode, double jcW, double jcX, double jcY, double jcZ)
    {
        Quat cBase, cPw, cPx, cPy, cPz;
        Quat z, zw, zx, zy, zz;
        if (juliaMode)
        {
            cBase = cPw = cPx = cPy = cPz = new Quat(jcW, jcX, jcY, jcZ);
            z = new Quat(sliceW, cx, cy, cz);
            zw = new Quat(sliceW + h, cx, cy, cz);
            zx = new Quat(sliceW, cx + h, cy, cz);
            zy = new Quat(sliceW, cx, cy + h, cz);
            zz = new Quat(sliceW, cx, cy, cz + h);
        }
        else
        {
            cBase = new Quat(sliceW, cx, cy, cz);
            cPw = new Quat(sliceW + h, cx, cy, cz);
            cPx = new Quat(sliceW, cx + h, cy, cz);
            cPy = new Quat(sliceW, cx, cy + h, cz);
            cPz = new Quat(sliceW, cx, cy, cz + h);
            z = zw = zx = zy = zz = Quat.Zero;
        }
        double r = 0;
        for (int i = 0; i < iter; i++)
        {
            r = z.Length;
            if (!double.IsFinite(r) || r > bailout) break;
            z  = fn(z,  cBase, i, pArr);
            zw = fn(zw, cPw,   i, pArr);
            zx = fn(zx, cPx,   i, pArr);
            zy = fn(zy, cPy,   i, pArr);
            zz = fn(zz, cPz,   i, pArr);
        }
        double j0 = (zw - z).Length / h;
        double j1 = (zx - z).Length / h;
        double j2 = (zy - z).Length / h;
        double j3 = (zz - z).Length / h;
        double dr = Math.Max(Math.Max(j0, j1), Math.Max(j2, j3));
        return 0.5 * r / Math.Max(dr, 1e-10);
    }

    /// <summary>Scalar KIFS/Mandelbox distance estimator. The user map is a
    /// composition of isometric folds/rotations (|det| = 1) and a uniform linear
    /// scale, so a small perturbation grows by |scale| per iteration; track that
    /// with a scalar running derivative instead of a finite-difference Jacobian.
    /// This is the only DE that handles per-iteration rotation across a fold's
    /// discontinuity planes — the numerical Jacobian straddles those planes and
    /// blows up. Orbit is seeded at the sample point (KIFS maps ignore c).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double UserBulbKifsDE(
        Func<Vec3, Vec3, int, double[], Vec3> fn,
        double cx, double cy, double cz,
        int iter, double bailout, double scale, double[] pArr)
    {
        Vec3 z = new(cx, cy, cz);
        Vec3 cWorld = z;              // held constant; KIFS maps don't read c
        double absScale = Math.Abs(scale);
        double dr = 1.0;
        double r = 0.0;
        for (int i = 0; i < iter; i++)
        {
            r = z.Length;
            if (!double.IsFinite(r) || r > bailout) break;
            z = fn(z, cWorld, i, pArr);
            dr *= absScale;
        }
        // Linear escape estimate: distance to the fractal ≈ |z| / dr.
        return r / Math.Max(dr, 1e-30);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double UserBulbDE(
        Func<Vec3, Vec3, int, double[], Vec3> fn,
        double cx, double cy, double cz,
        int iter, double bailout, double h, double[] pArr,
        bool juliaMode, double jcX, double jcY, double jcZ, double kifsScale)
    {
        if (kifsScale > 0.0)
            return UserBulbKifsDE(fn, cx, cy, cz, iter, bailout, kifsScale, pArr);

        Vec3 cBase, cPx, cPy, cPz;
        Vec3 z, zx, zy, zz;
        if (juliaMode)
        {
            cBase = cPx = cPy = cPz = new Vec3(jcX, jcY, jcZ);
            z  = new Vec3(cx,     cy,     cz);
            zx = new Vec3(cx + h, cy,     cz);
            zy = new Vec3(cx,     cy + h, cz);
            zz = new Vec3(cx,     cy,     cz + h);
        }
        else
        {
            cBase = new Vec3(cx, cy, cz);
            cPx = new Vec3(cx + h, cy, cz);
            cPy = new Vec3(cx, cy + h, cz);
            cPz = new Vec3(cx, cy, cz + h);
            z = zx = zy = zz = Vec3.Zero;
        }
        double r = 0.0;
        for (int i = 0; i < iter; i++)
        {
            r = z.Length;
            if (!double.IsFinite(r) || r > bailout) break;
            z  = fn(z,  cBase, i, pArr);
            zx = fn(zx, cPx,   i, pArr);
            zy = fn(zy, cPy,   i, pArr);
            zz = fn(zz, cPz,   i, pArr);
        }

        // Forward-diff Jacobian column lengths: |∂z/∂c_axis| ≈ |z_pert − z| / h.
        double j0 = (zx - z).Length / h;
        double j1 = (zy - z).Length / h;
        double j2 = (zz - z).Length / h;
        double dr = Math.Max(Math.Max(j0, j1), j2);

        return 0.5 * r / Math.Max(dr, 1e-10);
    }

    private readonly struct ColorMetrics
    {
        public readonly double TrapMin;
        public readonly double EscapeAngle;
        public readonly double FinalR;
        public readonly Vec3 FinalZ;
        public readonly int EscapeIter;
        public ColorMetrics(double trapMin, double escapeAngle, double finalR, Vec3 finalZ, int escapeIter)
        {
            TrapMin = trapMin; EscapeAngle = escapeAngle; FinalR = finalR; FinalZ = finalZ; EscapeIter = escapeIter;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ColorMetrics EvalColorMetrics(
        Func<Vec3, Vec3, int, double[], Vec3>? fn,
        Func<Quat, Quat, int, double[], Quat>? fnQ,
        bool quatMode, double sliceW,
        double px, double py, double pz,
        int iter, double bailout, double[] pArr,
        bool juliaMode, double jcW, double jcX, double jcY, double jcZ,
        double trapX, double trapY, double trapZ)
    {
        double trapMin = double.PositiveInfinity;
        Vec3 finalZ = Vec3.Zero;
        double finalR = 0.0;
        int escapeIter = iter;
        if (quatMode && fnQ != null)
        {
            Quat c, z;
            if (juliaMode) { c = new Quat(jcW, jcX, jcY, jcZ); z = new Quat(sliceW, px, py, pz); }
            else { c = new Quat(sliceW, px, py, pz); z = Quat.Zero; }
            for (int i = 0; i < iter; i++)
            {
                finalR = z.Length;
                if (!double.IsFinite(finalR) || finalR > bailout) { escapeIter = i; break; }
                var zv = z.ToVec3();
                double td = Math.Sqrt((zv.X - trapX) * (zv.X - trapX) + (zv.Y - trapY) * (zv.Y - trapY) + (zv.Z - trapZ) * (zv.Z - trapZ));
                if (td < trapMin) trapMin = td;
                z = fnQ(z, c, i, pArr);
            }
            finalZ = z.ToVec3();
        }
        else if (fn != null)
        {
            Vec3 c, z;
            if (juliaMode) { c = new Vec3(jcX, jcY, jcZ); z = new Vec3(px, py, pz); }
            else { c = new Vec3(px, py, pz); z = Vec3.Zero; }
            for (int i = 0; i < iter; i++)
            {
                finalR = z.Length;
                if (!double.IsFinite(finalR) || finalR > bailout) { escapeIter = i; break; }
                double td = Math.Sqrt((z.X - trapX) * (z.X - trapX) + (z.Y - trapY) * (z.Y - trapY) + (z.Z - trapZ) * (z.Z - trapZ));
                if (td < trapMin) trapMin = td;
                z = fn(z, c, i, pArr);
            }
            finalZ = z;
        }
        double escapeAngle = Math.Atan2(finalZ.Y, finalZ.X);
        return new ColorMetrics(trapMin, escapeAngle, finalR, finalZ, escapeIter);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateLight(
        double intensity, uint color,
        double lx, double ly, double lz,
        (double X, double Y, double Z) nrm,
        ref double sR, ref double sG, ref double sB)
    {
        if (intensity <= 0) return;
        double diffuse = Math.Max(0.0, nrm.X * lx + nrm.Y * ly + nrm.Z * lz) * intensity;
        sR += ((color >> 16) & 0xFF) * diffuse;
        sG += ((color >> 8) & 0xFF) * diffuse;
        sB += (color & 0xFF) * diffuse;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SkyColor(double rdy, uint bgBot, uint bgTop)
    {
        double t = Math.Clamp(0.5 * (rdy + 1.0), 0, 1);
        byte rb = (byte)((1 - t) * ((bgBot >> 16) & 0xFF) + t * ((bgTop >> 16) & 0xFF));
        byte gb = (byte)((1 - t) * ((bgBot >> 8) & 0xFF) + t * ((bgTop >> 8) & 0xFF));
        byte bb = (byte)((1 - t) * (bgBot & 0xFF) + t * (bgTop & 0xFF));
        return 0xFF000000u | ((uint)rb << 16) | ((uint)gb << 8) | bb;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double X, double Y, double Z) Normalize3(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-10) return (0.0, 0.0, 0.0);
        double inv = 1.0 / len;
        return (x * inv, y * inv, z * inv);
    }

    private static MetadataReference[] GatherRefs(params System.Reflection.Assembly[] markers)
        => RoslynRefs.GatherRefs(markers);
}
