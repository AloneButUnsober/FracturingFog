// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserEquationCalculator.cs
//
// Renders an escape-time fractal whose per-iteration step function is supplied
// at runtime by the user as a `Complex` expression. #27 Phase 3 — the source
// runs exclusively on the safe `SandboxExpression` interpreter (no BCL surface,
// no assembly load): the historical C# `Complex.*` form is translated to the
// Sandbox DSL by `EquationPreprocessor` and evaluated by the interpreter. The
// raw-C# Roslyn compile path was removed here (see the surface-reduction plan);
// a source with no DSL representation now surfaces an editable error instead of
// executing. Runs scalar (no SIMD) — interpreter overhead per pixel means it is
// slower than the typed kernels, but interactive at 800×600 with modest
// iteration counts.

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.CalculatorGen;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class UserEquationCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // ── Phase 11 — surface normals via numerical Jacobian ──────────────────
    //
    // Mandelbrot's typed kernel computes ∂z/∂c analytically (dz' = 2z·dz + 1
    // for z² + c). User-supplied equations have no closed-form derivative
    // available, so we run a parallel-perturbation trajectory per pixel:
    // base (z, c) + perturbed (zP, c + h). At escape, dz/dc ≈ (zP − z) / h.
    // Cost: 2× delegate calls per iteration vs the analytic path.
    //
    // Analytic functions are conformal so a single Re-axis perturbation is
    // enough — Cauchy-Riemann gives dz/dIm(c) = i · dz/dRe(c). Hubbard-Douady
    // escape-potential gradient then yields (nx, ny) routed to the
    // five-parameter ColorMap.Map overload. 2D themes ignore them; 3D Phong
    // themes light the user equation's escape surface for free.

    /// <summary>X component of the escape-potential gradient at escape, in
    /// [-1, 1]. 0 for in-set pixels. Consumed by 3D Phong themes via the
    /// five-parameter ColorMap.Map overload.</summary>
    public float[] NormalXBuffer { get; private set; } = Array.Empty<float>();

    /// <summary>Y component of the escape-potential gradient. See
    /// <see cref="NormalXBuffer"/>.</summary>
    public float[] NormalYBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 256;

    /// <summary>#96/#382 — global interior alpha (0..255). Scales the alpha of
    /// the in-set colour so the interior can composite over the chosen
    /// <c>Interior2DBackground</c>. 255 = opaque (bit-identical to before). Set
    /// from <c>FractalParameters.InteriorAlpha</c> by the render host / poster
    /// builder, mirroring the Mandelbrot canonical path.</summary>
    public int InteriorAlpha { get; set; } = 255;

    // ── High-precision centre limbs ─────────────────────────────────────────
    //
    // The input controller anchors box-zoom, double-click recenter, and
    // wheel zoom in DD/QD precision so the cursor pixel stays under the
    // cursor across the operation. Plain `CenterX` (Hi only) drops the
    // Lo / L2 / L3 limbs on the way to render — at zoom > ~1e15, one Hi
    // ULP is ~100 pixels, so the rendered centre snaps to a coarse grid
    // and clicked-pixel anchoring stops working (the user sees the box
    // zoom land in a "different nearby location"). These extra limbs let
    // Calculate() sum the per-pixel coord as DD or QD and cast the Hi
    // limb back to double for the per-pixel iteration body — the
    // iteration itself stays plain double (delegate-bound Complex), but
    // the per-pixel anchor maps correctly into the complex plane.
    public double CenterXLo { get; set; }
    public double CenterX2 { get; set; }
    public double CenterX3 { get; set; }
    public double CenterYLo { get; set; }
    public double CenterY2 { get; set; }
    public double CenterY3 { get; set; }

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <summary>Most recent compile error, or empty string when last compile succeeded.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>True if last compile produced a usable step function on the
    /// safe DSL interpreter.</summary>
    public bool IsCompiled => _sbx != null;

    // #27 Phase 3 — the source's only execution path. The C# `Complex.*` form
    // is translated to the Sandbox DSL and parsed into _sbx; the render loop
    // walks it (no BCL surface, no assembly load). The raw-C# Roslyn delegate
    // was removed in Phase 3.
    private SandboxExpression? _sbx;
    // Reason the DSL path could not represent the source (untranslatable
    // construct or parse failure). Surfaced to the user as the compile error.
    private string? _dslError;
    private string _compiledSource = string.Empty;

    // #542 — optional seed expression for z0. Empty ⇒ z0 = 0 (Mandelbrot orbit,
    // legacy). A DSL expression over `c` (and constants) evaluated once per
    // pixel before iteration: `c` ⇒ z0 = pixel (Julia), a critical-point
    // expression ⇒ the correct Mandelbrot for non-polynomial families. Parsed
    // as bare DSL (no C# preprocessor). A parse failure leaves it null (z0 = 0)
    // and is recorded in <see cref="SeedError"/>.
    private SandboxExpression? _seedSbx;
    private string _compiledSeed = string.Empty;

    /// <summary>Parse error for the z0 seed expression, or empty when the seed
    /// is empty or parsed cleanly. Surfaced by the editor alongside the main
    /// compile status.</summary>
    public string SeedError { get; private set; } = string.Empty;

    // #544 — optional convergence bailout: a boolean DSL condition over z / prev
    // / c / n / iter. Empty ⇒ escape-radius test only. When it becomes true the
    // orbit stops early and the pixel is classified "converged" (coloured by
    // convergence speed), enabling Newton / Magnet / Nova style maps whose
    // interesting region converges rather than escapes. Typical form:
    // `abs(z - prev) &lt; 0.0001`.
    private SandboxExpression? _condSbx;
    private string _compiledCond = string.Empty;

    /// <summary>Parse error for the convergence bailout condition, or empty when
    /// it is unset or parsed cleanly.</summary>
    public string BailoutConditionError { get; private set; } = string.Empty;

    /// <summary>True when the last successful compile runs on the safe DSL
    /// interpreter. Always true after a successful compile now that the DSL is
    /// the only path; retained for callers that branch on it.</summary>
    public bool UsingDsl => _sbx != null;

    public UserEquationCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        NormalXBuffer = new float[n];
        NormalYBuffer = new float[n];
    }

    /// <summary>
    /// Compiles the user source. The source is a `Complex`-valued expression in
    /// the historical System.Numerics.Complex form (Complex.Sin/Cos/Pow/…,
    /// new Complex(a,b), arithmetic on z/c/n), optionally wrapped in
    /// `return … ;`.
    ///
    /// #27 Phase 3 — the source is translated to the safe Sandbox DSL by
    /// <see cref="EquationPreprocessor"/> and, when it translates and parses,
    /// executed by an interpreter with no BCL surface. There is no raw-C#
    /// Roslyn path any more: a construct with no DSL form surfaces an editable
    /// error (<see cref="LastError"/>) and does not execute.
    /// </summary>
    public void Compile(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            _sbx = null;
            LastError = "Source is empty";
            return;
        }

        // The safe interpreted DSL is the only execution path. Translate the
        // historical C# `Complex.*` form to the Sandbox DSL and, when it
        // translates cleanly and parses, run it through SandboxExpression:
        // no Roslyn, no BCL surface, no assembly load.
        if (TryCompileDsl(source)) return;

        // #27 Phase 3 — no Roslyn fallback. A source the DSL cannot represent
        // (member access, an unsupported member, a statement block) no longer
        // executes; surface why, so the user can rewrite it in DSL terms.
        _sbx = null;
        LastError = string.IsNullOrEmpty(_dslError)
            ? "This equation can't be expressed in the safe expression language. " +
              "Use DSL forms: sin cos tan exp log sqrt conj re im arg pow, `^` for powers, and z / c / n."
            : _dslError;
    }

    /// <summary>
    /// Translate the source to the safe DSL via
    /// <see cref="EquationPreprocessor"/> and, if no unsupported construct is
    /// flagged and the result parses, install the parsed
    /// <see cref="SandboxExpression"/>. Returns true on success. On failure
    /// sets <see cref="_dslError"/> (why the DSL declined) and leaves _sbx null.
    /// </summary>
    private bool TryCompileDsl(string source)
    {
        _dslError = null;

        // Translate C# → DSL. A diagnostic means an unsupported construct
        // (member access, Complex.Abs, a statement block, …) — no DSL form.
        string dsl = EquationPreprocessor.Preprocess(source, out PreprocessDiagnostic? diag);
        if (diag != null)
        {
            _dslError = diag.Message;
            _sbx = null;
            return false;
        }

        try
        {
            var expr = SandboxExpression.Parse(dsl);
            _sbx = expr;
            _compiledSource = source;
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            // Parsed to DSL text but the DSL grammar rejected it (e.g. an
            // operator with no DSL form). Keep it editable; report crisply.
            _dslError = ex.Message;
            _sbx = null;
            return false;
        }
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (_sbx == null)
        {
            // Try compiling from FractalParameters.UserEquationSource lazily.
            if (!string.IsNullOrWhiteSpace(FractalParameters.UserEquationSource)
                && FractalParameters.UserEquationSource != _compiledSource)
            {
                Compile(FractalParameters.UserEquationSource);
            }
        }

        var sbx = _sbx;   // #27 Phase 3 — safe DSL interpreter is the only path
        if (sbx == null)
        {
            // No compiled equation — fill with theme InSetColor so the screen
            // is at least not stale.
            Array.Clear(ColorBuffer);
            uint bg = ColorMap.InSetColor;
            for (int i = 0; i < ColorBuffer.Length; i++) ColorBuffer[i] = bg;
            return;
        }

        // #542 — (re)compile the z0 seed lazily when its source changes. Empty
        // ⇒ no seed (z0 = 0). Parsed as bare DSL; a parse failure is recorded
        // and the seed is dropped (falls back to z0 = 0) rather than aborting.
        string seedSrc = FractalParameters.UserEquationSeed?.Trim() ?? string.Empty;
        if (seedSrc != _compiledSeed)
        {
            _compiledSeed = seedSrc;
            if (seedSrc.Length == 0) { _seedSbx = null; SeedError = string.Empty; }
            else
            {
                try { _seedSbx = SandboxExpression.Parse(seedSrc); SeedError = string.Empty; }
                catch (Exception ex) { _seedSbx = null; SeedError = $"z0 seed: {ex.Message}"; }
            }
        }
        var seed = _seedSbx;

        // #544 — (re)compile the convergence bailout condition lazily. Empty ⇒
        // no early stop. Parse failure recorded; condition dropped.
        string condSrc = FractalParameters.UserEquationBailoutCondition?.Trim() ?? string.Empty;
        if (condSrc != _compiledCond)
        {
            _compiledCond = condSrc;
            if (condSrc.Length == 0) { _condSbx = null; BailoutConditionError = string.Empty; }
            else
            {
                try { _condSbx = SandboxExpression.Parse(condSrc); BailoutConditionError = string.Empty; }
                catch (Exception ex) { _condSbx = null; BailoutConditionError = $"bailout condition: {ex.Message}"; }
            }
        }
        var cond = _condSbx;

        ColorMap.MaxIterations = MaxIterations;
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        double centerX = CenterX;
        double centerY = CenterY;
        int width = Width;
        int height = Height;
        // #541 — escape radius from FractalParameters (0 = legacy |z|² = 1024).
        double bailout2 = FractalParameters.EscapeRadius > 0.0
            ? FractalParameters.EscapeRadius * FractalParameters.EscapeRadius
            : 1024.0; // generous default for arbitrary maps
        // #615 Phase 1 — optional dedicated colour for the beyond-escape-radius
        // surround (the flat disk seen when zoomed out). null ⇒ paint the escape
        // gradient as before (byte-identical). escapeRadius = sqrt(bailout2).
        uint? oobColor = ColorMap.OutOfBoundsColor;
        double escapeRadius = Math.Sqrt(bailout2);

        // Precision-tier selection. When the low limbs carry real data
        // (input controller is anchoring in DD or QD) the per-pixel coord
        // must be summed in matching precision or the rendered image
        // disagrees with where the user clicked. Iteration body stays
        // plain double — only the (cx, cy) starting point benefits.
        bool useQD = CenterX2 != 0.0 || CenterX3 != 0.0
                  || CenterY2 != 0.0 || CenterY3 != 0.0;
        bool useDD = !useQD && (CenterXLo != 0.0 || CenterYLo != 0.0);

        QD cxQd = useQD ? new QD(centerX, CenterXLo, CenterX2, CenterX3) : default;
        QD cyQd = useQD ? new QD(centerY, CenterYLo, CenterY2, CenterY3) : default;
        DD cxDd = useDD ? new DD(centerX, CenterXLo) : default;
        DD cyDd = useDD ? new DD(centerY, CenterYLo) : default;

        double rot = FractalParameters.UserEquationRotationDegrees * Math.PI / 180.0;
        double cosA = Math.Cos(rot);
        double sinA = Math.Sin(rot);
        bool skipJacobian = FractalParameters.UserEquationSkipJacobian;

        // #545 — when the step map (and any seed) is holomorphic we carry an
        // EXACT dz/dc via forward-mode duals instead of the finite-difference
        // second trajectory. Non-holomorphic maps (abs/conj/re/im/arg/ternary…)
        // keep the numeric Jacobian, so abs-family equations are unchanged.
        bool useAnalytic = !skipJacobian
                        && sbx.IsHolomorphic
                        && (seed == null || seed.IsHolomorphic);

        // P5: gate orbit sampling once per render. Non-orbit themes pay nothing.
        var orbitMap = ColorMap as IOrbitAwareColorMap;

        // #583 — colour in-set (non-escaping) pixels by the accumulated orbit
        // instead of a flat interior fill. Only meaningful for orbit-aware
        // themes (they already sample the orbit every iteration, so `acc` holds
        // a full-orbit statistic even on bounded pixels). Off ⇒ byte-identical.
        // #583 flag OR #590 (F14) theme-driven request. Either turns on
        // interior-orbit colouring for an orbit-aware theme.
        bool colorInterior = orbitMap != null
            && (FractalParameters.UserEquationColorInterior || orbitMap.WantsInteriorColor);

        // #382: pre-scale the in-set colour's alpha by the global InteriorAlpha
        // knob once (multiplies any alpha the theme's InSetColor already carries).
        // InteriorAlpha == 255 leaves the colour bit-identical.
        uint inSet = ColorMap.InSetColor;
        if (InteriorAlpha < 255)
        {
            uint a = (inSet >> 24) & 0xFFu;
            uint na = (a * (uint)InteriorAlpha) / 255u;
            inSet = (inSet & 0x00FFFFFFu) | (na << 24);
        }

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double dy = (y - height * 0.5) * scale;
            double dyCos = dy * cosA;
            double dySin = dy * sinA;
            int rowBase = y * width;

            // One env per row for the DSL interpreter (holds z/c/n + let-slots;
            // mutated in place per step).
            SbxVal[] env = sbx.NewEnv();
            // #543 — pv is the previous iterate z_{n-1}, bound to the `prev` slot.
            Complex Step(Complex zz, Complex cc, int it, Complex pv) => sbx.EvalStep(zz, cc, it, env, pv);
            // #542 — per-row env for the z0 seed (null when no seed). Evaluated
            // with z=0, n=0 and the pixel's c: `c` ⇒ z0 = pixel (Julia).
            SbxVal[]? seedEnv = seed?.NewEnv();
            Complex Seed(Complex cc) => seed!.EvalStep(Complex.Zero, cc, 0, seedEnv!);
            // #544 — per-row env for the convergence bailout condition. True when
            // the (boolean) condition fires on the current iterate.
            SbxVal[]? condEnv = cond?.NewEnv();
            bool Cond(Complex zz, Complex cc, int it, Complex pv)
            {
                var r = cond!.EvalStep(zz, cc, it, condEnv!, pv);
                return r.Real != 0.0 || r.Imaginary != 0.0;
            }
            // #545 — per-row dual env for the exact forward-mode dz/dc. StepD
            // carries (z, dz) and (prev, dprev); SeedD gives (z0, dz0/dc).
            CxDual[]? dualEnv = useAnalytic ? sbx.NewDualEnv() : null;
            (Complex z, Complex dz) StepD(Complex zz, Complex dzz, Complex cc, int it, Complex pv, Complex dpv)
                => sbx.EvalStepD(zz, dzz, cc, it, dualEnv!, pv, dpv);
            CxDual[]? seedDualEnv = (useAnalytic && seed != null) ? seed!.NewDualEnv() : null;
            (Complex z, Complex dz) SeedD(Complex cc)
                => seed!.EvalStepD(Complex.Zero, Complex.Zero, cc, 0, seedDualEnv!);
            for (int x = 0; x < width; x++)
            {
                double dx = (x - width * 0.5) * scale;
                double cx, cy;
                if (useQD)
                {
                    // Sum centre + per-pixel offset in QD, take Hi limb.
                    // dx and dy are plain doubles (offset within ~1 pixel
                    // of scale); QD's implicit double promotion handles
                    // the addition.
                    var cxFull = cxQd + (dx * cosA - dySin);
                    var cyFull = cyQd + (dx * sinA + dyCos);
                    cx = cxFull.X0;
                    cy = cyFull.X0;
                }
                else if (useDD)
                {
                    var cxFull = cxDd + (dx * cosA - dySin);
                    var cyFull = cyDd + (dx * sinA + dyCos);
                    cx = cxFull.Hi;
                    cy = cyFull.Hi;
                }
                else
                {
                    cx = centerX + dx * cosA - dySin;
                    cy = centerY + dx * sinA + dyCos;
                }
                var c = new Complex(cx, cy);
                const double h = 1e-6;
                var cP = new Complex(cx + h, cy);
                // #542 — seed z0 (default 0). zP seeds at the perturbed cP so the
                // numerical Jacobian stays consistent with the seeded trajectory.
                // #545 — in analytic mode dz/dz0 come from the seed's dual; zP is
                // unused (no second trajectory).
                Complex z, dz = Complex.Zero, prevDz = Complex.Zero;
                Complex zP = Complex.Zero;
                if (useAnalytic)
                {
                    if (seed != null) (z, dz) = SeedD(c);
                    else z = Complex.Zero;      // dz0 = 0: z0 is c-independent
                }
                else
                {
                    z = seed != null ? Seed(c) : Complex.Zero;
                    zP = seed != null ? Seed(cP) : Complex.Zero;
                }
                Complex prevZ = Complex.Zero, prevZP = Complex.Zero;   // #543 z_{n-1}
                bool converged = false;                                 // #544
                OrbitAccumulator acc = default;
                if (orbitMap != null) orbitMap.InitOrbit(out acc);
                int iter;
                if (skipJacobian)
                {
                    // Skip parallel-perturbation trajectory — halves delegate
                    // call cost. 3D Phong themes degrade to flat lighting
                    // because surface normals come out zero, but 2D themes
                    // are unaffected.
                    for (iter = 0; iter < maxIt; iter++)
                    {
                        double r2 = z.Real * z.Real + z.Imaginary * z.Imaginary;
                        if (r2 >= bailout2) break;
                        if (orbitMap != null && iter > 0)
                            orbitMap.Sample(ref acc, z.Real, z.Imaginary, cx, cy, iter);
                        if (cond != null && Cond(z, c, iter, prevZ)) { converged = true; break; }   // #544
                        try { var zn = Step(z, c, iter, prevZ); prevZ = z; z = zn; }
                        catch { iter = maxIt; break; }
                    }
                }
                else if (useAnalytic)
                {
                    // #545 — single trajectory carrying the exact dz/dc dual.
                    for (iter = 0; iter < maxIt; iter++)
                    {
                        double r2 = z.Real * z.Real + z.Imaginary * z.Imaginary;
                        if (r2 >= bailout2) break;
                        if (orbitMap != null && iter > 0)
                            orbitMap.Sample(ref acc, z.Real, z.Imaginary, cx, cy, iter);
                        if (cond != null && Cond(z, c, iter, prevZ)) { converged = true; break; }   // #544
                        try
                        {
                            var (zn, dzn) = StepD(z, dz, c, iter, prevZ, prevDz);
                            prevZ = z; prevDz = dz;
                            z = zn; dz = dzn;
                        }
                        catch { iter = maxIt; break; }
                    }
                }
                else
                {
                    for (iter = 0; iter < maxIt; iter++)
                    {
                        double r2 = z.Real * z.Real + z.Imaginary * z.Imaginary;
                        if (r2 >= bailout2) break;
                        if (orbitMap != null && iter > 0)
                            orbitMap.Sample(ref acc, z.Real, z.Imaginary, cx, cy, iter);
                        if (cond != null && Cond(z, c, iter, prevZ)) { converged = true; break; }   // #544
                        try
                        {
                            var zn = Step(z, c, iter, prevZ);
                            var zpn = Step(zP, cP, iter, prevZP);
                            prevZ = z; prevZP = zP;
                            z = zn; zP = zpn;
                        }
                        catch { iter = maxIt; break; }
                    }
                }
                int idx = rowBase + x;
                if (iter >= maxIt)
                {
                    NormalXBuffer[idx] = 0f;
                    NormalYBuffer[idx] = 0f;
                    if (colorInterior)
                    {
                        // #583 — bounded pixel: colour from the full-orbit
                        // accumulator (trap min / stripe / TIA). Honour the
                        // global InteriorAlpha knob for parity with the flat path.
                        uint oc = (uint)orbitMap!.MapInteriorWithOrbit(maxIt, in acc);
                        if (InteriorAlpha < 255)
                        {
                            uint a = (oc >> 24) & 0xFFu;
                            uint na = (a * (uint)InteriorAlpha) / 255u;
                            oc = (oc & 0x00FFFFFFu) | (na << 24);
                        }
                        ColorBuffer[idx] = oc;
                    }
                    else
                    {
                        ColorBuffer[idx] = inSet;   // #382: alpha pre-scaled above
                    }
                }
                else if (converged)
                {
                    // #544 — converged (bailout condition fired): |z| is small so
                    // the log-log smoothing is invalid; band by convergence speed
                    // (raw iteration). Normals are undefined here → flat.
                    NormalXBuffer[idx] = 0f;
                    NormalYBuffer[idx] = 0f;
                    ColorBuffer[idx] = orbitMap != null
                        ? (uint)orbitMap.MapWithOrbit(iter, 0f, maxIt, 0f, 0f, in acc)
                        : (uint)ColorMap.Map(iter, 0f, maxIt, 0f, 0f);
                }
                else
                {
                    double mag = Math.Sqrt(z.Real * z.Real + z.Imaginary * z.Imaginary);
                    float smooth = (float)(iter + 1.0 - Math.Log2(Math.Max(1e-10, Math.Log2(Math.Max(mag, 1.0 + 1e-10)))));

                    // #588 — carry dz/dc out of the normal block so the escape
                    // value AND derivative reach the nine-param Map overload.
                    // finalZ themes (Potential, Binary/Argument Decomposition,
                    // Iter+FinalZ, domain/field-line) were previously dead on the
                    // interpreter path because it only ever called the 5-param
                    // overload. 0 for the skip-Jacobian case (dz unavailable).
                    double dzdcR = 0.0, dzdcI = 0.0;
                    float nx, ny;
                    if (skipJacobian)
                    {
                        nx = 0f;
                        ny = 0f;
                    }
                    else
                    {
                        // Hubbard-Douady normal: u = Re(conj(z) · dz/dc),
                        // v = -Im(conj(z) · dz/dc). #545 — analytic mode carries
                        // the EXACT dz/dc in `dz`; otherwise fall back to the
                        // finite difference (zP − z) / h (Cauchy-Riemann gives the
                        // Im column for free on the analytic fn).
                        dzdcR = useAnalytic ? dz.Real : (zP.Real - z.Real) / h;
                        dzdcI = useAnalytic ? dz.Imaginary : (zP.Imaginary - z.Imaginary) / h;
                        double u = z.Real * dzdcR + z.Imaginary * dzdcI;          // Re(z̄ · dzdc)
                        double v = -(z.Real * dzdcI - z.Imaginary * dzdcR);       // -Im(z̄ · dzdc)
                        double m = Math.Sqrt(u * u + v * v);
                        if (m > 1e-12) { nx = (float)(u / m); ny = (float)(v / m); }
                        else { nx = 0f; ny = 0f; }
                    }
                    NormalXBuffer[idx] = nx;
                    NormalYBuffer[idx] = ny;

                    // #588 — pass finalZ (z at escape) + dz/dc to the nine-param
                    // overload. Themes that ignore them default back to the
                    // five-param path (byte-identical); finalZ themes light up.
                    ColorBuffer[idx] = orbitMap != null
                        ? (uint)orbitMap.MapWithOrbit(smooth, 0f, maxIt, nx, ny, in acc)
                        : (uint)ColorMap.Map(smooth, 0f, maxIt, nx, ny,
                                             (float)z.Real, (float)z.Imaginary,
                                             (float)dzdcR, (float)dzdcI);

                    // #615 — beyond-escape-radius surround: paint the flat OOB
                    // colour and flatten normals so downstream post-process (emboss
                    // / AO) does not re-shade the surround.
                    if (oobColor is uint oob &&
                        Interefaces.IColorMap.IsOutOfBounds(cx, cy, escapeRadius))
                    {
                        ColorBuffer[idx] = oob;
                        NormalXBuffer[idx] = 0f;
                        NormalYBuffer[idx] = 0f;
                    }
                }
            }
        });
    }
}
