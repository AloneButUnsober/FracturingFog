// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// CalculatorGenApi.cs
//
// Library-facing entry into CalculatorGen. Same pipeline as Program.cs's
// CLI Main, exposed as a static method so the main app (UserEquation
// dialog) can invoke generation without spawning a child process.
//
// Returns:
//   GenerateResult.Source     — the rendered calculator .cs source
//   GenerateResult.SelfTest   — the rendered self-test .cs source (null when not requested)
//   GenerateResult.ClassName  — normalised class name (suffix "Calculator" appended if missing)
//   GenerateResult.Error      — parse-error message; non-null indicates failure

using System;
using System.Reflection;
using FracturingFog.CalculatorGen.Emitters;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen;

public readonly record struct GenerateResult(
    string ClassName,
    string Source,
    string? SelfTest,
    string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Lightweight projection of CalcGen's analysis pass for live-preview UIs.
/// Carries the parsed AST in printable form, the symbolic ∂/∂z and ∂/∂c,
/// and every feature flag the generator gates emitters on. No file I/O,
/// no template rendering — safe to call on every keystroke (post-debounce).
/// </summary>
public readonly record struct PreviewResult(
    string AstText,
    string DpDzText,
    string DpDcText,
    int SaFastDegree,
    int SaGenericDegree,
    bool SaEnabled,
    bool SupportsPerturbation,
    bool SupportsDe,
    bool HasPrev,
    bool HasIter,
    bool HasConj,
    bool HasFolded,
    bool HasDiv,
    bool HasTrans,
    bool HasCond,
    string? Error)
{
    public bool Ok => Error is null;
}

public static class CalculatorGenApi
{
    /// <summary>
    /// Render a calculator from an equation string. No file I/O — returns
    /// the source as a string so callers can write, compile, hash, or
    /// preview as they choose. Parse failures return a result with
    /// <see cref="GenerateResult.Error"/> set and empty source.
    /// </summary>
    /// <param name="equation">Right-hand side of <c>z_{n+1} = …</c>.</param>
    /// <param name="name">Class name. Suffix "Calculator" appended if absent.</param>
    /// <param name="includeSelfTest">When true, also renders the
    /// <c>{Name}SelfTest</c> validator class.</param>
    public static GenerateResult Generate(string equation, string name, bool includeSelfTest = false, double bailoutRadius = 512.0)
    {
        if (string.IsNullOrWhiteSpace(equation))
            return new GenerateResult(name ?? "", "", null, "Equation is empty.");
        if (string.IsNullOrWhiteSpace(name))
            return new GenerateResult("", "", null, "Class name is empty.");

        if (!name.EndsWith("Calculator", StringComparison.Ordinal))
            name += "Calculator";

        AstNode root;
        try
        {
            root = EquationParser.Parse(equation);
        }
        catch (Exception ex)
        {
            return new GenerateResult(name, "", null, $"Parse error: {ex.Message}");
        }

        var dpdz   = AstDifferentiator.DpDz(root);
        var dpdc   = AstDifferentiator.DpDc(root);
        var derivUpdate = AstDifferentiator.BuildDerivativeUpdate(root);

        string scalarZBody = new ScalarEmitter().EmitNewValueBody(root,        "z", indent: "            ");
        string scalarDBody = new ScalarEmitter().EmitNewValueBody(derivUpdate, "d", indent: "            ");
        string avxZBody    = new Avx2Emitter("                ", tempPrefix: "z_").EmitNewValueBody(root,        "z");
        string avxDBody    = new Avx2Emitter("                ", tempPrefix: "d_").EmitNewValueBody(derivUpdate, "d");

        // Feature detection: equations using anti-holomorphic ops (Conj /
        // Folded) or rational ops (Div) break the perturbation Taylor
        // expansion and / or the distance-estimate chain rule. CalcGen
        // emits compile-time flags the template gates on; the
        // perturbation body is replaced with a no-op when unsupported so
        // the file still compiles and the runtime simply never enters
        // that path. Distance estimate is disabled separately (Conj /
        // Folded only — Div keeps DE via quotient rule).
        bool hasConj   = AstHelpers.Contains<Conj>(root);
        bool hasFolded = AstHelpers.Contains<Folded>(root);
        bool hasDiv    = AstHelpers.Contains<Div>(root);
        // Transcendentals (sin/cos/exp/log): holomorphic so DE is fine
        // via symbolic chain rule, but perturbation Taylor expansion
        // isn't derived for non-polynomial nodes (would need closed
        // form for δ-step around Z), so perturbation/BLA/SA all stay
        // off. Generated calc falls back to scalar / AVX2 z-update;
        // HpDirect (DD/QD) handles deep zoom with scalar-promoted
        // transcendental calls inside the QD/DD chain (precision
        // degrades inside the call; surrounding +-* preserved).
        bool hasTrans  = AstHelpers.Contains<Sin>(root)
                      || AstHelpers.Contains<Cos>(root)
                      || AstHelpers.Contains<Exp>(root)
                      || AstHelpers.Contains<Log>(root)
                      || AstHelpers.Contains<Arg>(root)
                      || AstHelpers.Contains<Atan2>(root)
                      || AstHelpers.Contains<Min>(root)
                      || AstHelpers.Contains<Max>(root)
                      || AstHelpers.Contains<Mod>(root)
                      // #27 Phase 6 — re/im/abs/clamp are non-holomorphic; disable
                      // perturbation + SA (they render on the direct scalar/AVX/
                      // DD/QD paths at shallow zoom, which is where parity holds).
                      || AstHelpers.Contains<ReOp>(root)
                      || AstHelpers.Contains<ImOp>(root)
                      || AstHelpers.Contains<AbsOp>(root)
                      || AstHelpers.Contains<Clamp>(root)
                      // #27 Phase 6 (tranche 2) — general pow(base,exp) is
                      // transcendental (exp·log) + non-holomorphic under the
                      // δ-Taylor expansion; disable perturbation/SA + analytic DE.
                      || AstHelpers.Contains<PowC>(root)
                      // #27 Phase 6 (tranche 2) — inverse trig / hyperbolic are
                      // non-polynomial (no closed-form δ-Taylor / SA) and have no
                      // analytic-DE rules implemented; disable perturbation/SA + DE.
                      || AstHelpers.Contains<Asin>(root)
                      || AstHelpers.Contains<Acos>(root)
                      || AstHelpers.Contains<Atan>(root)
                      || AstHelpers.Contains<Asinh>(root)
                      || AstHelpers.Contains<Acosh>(root)
                      || AstHelpers.Contains<Atanh>(root)
                      // #27 Phase 6 (tranche 2) — per-component floor/round/ceil/
                      // trunc/fract/sign are piecewise-constant (non-holomorphic);
                      // disable perturbation/SA + analytic DE.
                      || AstHelpers.Contains<Floor>(root)
                      || AstHelpers.Contains<Round>(root)
                      || AstHelpers.Contains<Ceil>(root)
                      || AstHelpers.Contains<Trunc>(root)
                      || AstHelpers.Contains<Fract>(root)
                      || AstHelpers.Contains<Sign>(root);
        // arg / atan2 / min / max / mod are non-holomorphic. Distance
        // estimate breaks like Conj/Folded — feed all into the DE gate.
        bool hasArg    = AstHelpers.Contains<Arg>(root)
                      || AstHelpers.Contains<Atan2>(root)
                      || AstHelpers.Contains<Min>(root)
                      || AstHelpers.Contains<Max>(root)
                      || AstHelpers.Contains<Mod>(root)
                      // #27 Phase 6 — re/im/abs/clamp are non-holomorphic → the
                      // dz/dc chain is meaningless; disable analytic DE.
                      || AstHelpers.Contains<ReOp>(root)
                      || AstHelpers.Contains<ImOp>(root)
                      || AstHelpers.Contains<AbsOp>(root)
                      || AstHelpers.Contains<Clamp>(root)
                      // #27 Phase 6 (tranche 2) — general pow(base,exp) is
                      // transcendental (exp·log) + non-holomorphic under the
                      // δ-Taylor expansion; disable perturbation/SA + analytic DE.
                      || AstHelpers.Contains<PowC>(root)
                      // #27 Phase 6 (tranche 2) — inverse trig / hyperbolic are
                      // non-polynomial (no closed-form δ-Taylor / SA) and have no
                      // analytic-DE rules implemented; disable perturbation/SA + DE.
                      || AstHelpers.Contains<Asin>(root)
                      || AstHelpers.Contains<Acos>(root)
                      || AstHelpers.Contains<Atan>(root)
                      || AstHelpers.Contains<Asinh>(root)
                      || AstHelpers.Contains<Acosh>(root)
                      || AstHelpers.Contains<Atanh>(root)
                      // #27 Phase 6 (tranche 2) — per-component floor/round/ceil/
                      // trunc/fract/sign are piecewise-constant (non-holomorphic);
                      // disable perturbation/SA + analytic DE.
                      || AstHelpers.Contains<Floor>(root)
                      || AstHelpers.Contains<Round>(root)
                      || AstHelpers.Contains<Ceil>(root)
                      || AstHelpers.Contains<Trunc>(root)
                      || AstHelpers.Contains<Fract>(root)
                      || AstHelpers.Contains<Sign>(root);
        // Conditional / piecewise (If): branches are individually
        // holomorphic so DE survives inside each side (a discontinuity
        // along the boundary locus is the only cost), but the δ-Taylor
        // expansion has no closed form across the branch — perturbation
        // / BLA / SA all stay off.
        bool hasCond  = AstHelpers.Contains<If>(root);
        // Phoenix prev (z_{n-1}): differentiator treats prev as opaque so
        // ∂step/∂z misses the prev coupling — distance estimate would be
        // wrong. Perturbation Taylor expansion would need a parallel δ
        // companion for prev. Both deferred — gate off when present.
        bool hasPrev  = AstHelpers.Contains<PrevRef>(root);
        // Iteration index `n`/`iter`: real scalar, differentiator returns
        // 0 directly but the chain rule pulls n into the derivative
        // (d/dz sin(z*n) = cos(z*n)·n). PerturbDeriv / Avx2Deriv /
        // Avx512Deriv emitters walk that derivUpdate AST and hit
        // IterRef, but the template doesn't inject ITER_DECL at the
        // PERTURB_DERIV_BODY insertion sites, so the emitters throw
        // "IterRef not bound" during generation. Gate DE off when
        // iter is present until the template grows iter declarations
        // at every deriv-body site (a multi-site template change for
        // a rare equation shape — escape-time render still works,
        // surface normals just degrade to flat-shaded for iter-
        // dependent equations).
        bool hasIter  = AstHelpers.Contains<IterRef>(root);
        bool supportsDe = !(hasConj || hasFolded || hasPrev || hasArg || hasIter);
        bool supportsPerturbation = !(hasConj || hasFolded || hasDiv || hasTrans || hasCond || hasPrev || hasIter);

        string perturbBody, perturbDdBody, perturbAvx2Body, perturbAvx512Body,
               perturbDerivBody, perturbDerivAvx2Body, perturbDerivAvx512Body;
        if (supportsPerturbation)
        {
            var perturbDelta = AstPerturbation.BuildDeltaUpdate(root);
            perturbBody = new PerturbationEmitter()
                .EmitDeltaBody(perturbDelta, indent: "                    ");
            perturbDdBody = new DdEmitter()
                .EmitDdDeltaBody(perturbDelta, indent: "                    ");
            perturbAvx2Body   = new Avx2PerturbationEmitter().EmitDeltaBody(perturbDelta);
            perturbAvx512Body = new Avx512PerturbationEmitter().EmitDeltaBody(perturbDelta);
        }
        else
        {
            // No-op stubs so the generated file still compiles. Runtime
            // paths are skipped by SupportsPerturbation = false.
            perturbBody       = "                    double dr_new = 0.0; double di_new = 0.0;";
            perturbDdBody     = "                    DD dr_dd_new = DD.Zero; DD di_dd_new = DD.Zero;";
            perturbAvx2Body   = "                    Vector256<double> dr_new = Vector256<double>.Zero; Vector256<double> di_new = Vector256<double>.Zero;";
            perturbAvx512Body = "                    Vector512<double> dr_new = Vector512<double>.Zero; Vector512<double> di_new = Vector512<double>.Zero;";
        }
        if (supportsDe)
        {
            perturbDerivBody = new PerturbDerivEmitter()
                .EmitDerivBody(derivUpdate, indent: "                    ");
            perturbDerivAvx2Body   = new Avx2DerivEmitter().EmitDerivBody(derivUpdate);
            perturbDerivAvx512Body = new Avx512DerivEmitter().EmitDerivBody(derivUpdate);
        }
        else
        {
            perturbDerivBody       = "                    double drv_new = 0.0; double div_new = 0.0;";
            perturbDerivAvx2Body   = "                    Vector256<double> drv_new = Vector256<double>.Zero; Vector256<double> div_new = Vector256<double>.Zero;";
            perturbDerivAvx512Body = "                    Vector512<double> drv_new = Vector512<double>.Zero; Vector512<double> div_new = Vector512<double>.Zero;";
        }

        // BLA bodies emit references to ScalarEmitter's IterRe="iter"
        // when the derivative chain pulls n into the polynomial (e.g.
        // d/dz sin(z*n) = cos(z*n)*n). The template doesn't declare
        // `iter` at the BLA insertion sites — pre-existing latent. Stub
        // when hasIter so the generated file still compiles; UseBla is
        // already forced off at the runtime gate path for iter-
        // dependent equations via supportsPerturbation = false.
        string blaABody, blaBBody;
        if (hasIter)
        {
            blaABody = "                    double Ar_new = 0.0, Ai_new = 0.0;";
            blaBBody = "                    double Br_new = 0.0, Bi_new = 0.0;";
        }
        else
        {
            blaABody = new ScalarEmitter().EmitNewValueBody(dpdz, "A", indent: "                    ");
            blaBBody = new ScalarEmitter().EmitNewValueBody(dpdc, "B", indent: "                    ");
        }

        string qdZBody = new QdEmitter().EmitQdBody(root, indent: "                ");
        string ddDirectBody = new DdDirectEmitter().EmitDdDirectBody(root, indent: "                    ");
        string qdDirectBody = new QdDirectEmitter().EmitQdDirectBody(root, indent: "                    ");

        // ── AVX-2 DD4 vectorised body for whole-frame DD-direct ──────────
        //
        // The DD direct body uses only +/-/* operators and FromCenterOffset
        // — all of which exist with identical signatures on DD4. So the
        // SAME expression compiles for either type. Build the DD4 body by
        // textual substitution of the DD body:
        //   zr_dd  → zr_dd4   (variable rename)
        //   zi_dd  → zi_dd4
        //   cr_dd  → cr_dd4
        //   ci_dd  → ci_dd4
        //   "DD "  → "DD4 "   (declaration type rewrite; doesn't touch
        //                       "(DD)" casts the emitter never emits for
        //                       plain-polynomial bodies)
        // The DD4 path is gated on AstSaDetector.DetectZdPlusC >= 2 —
        // pure z^d+c only — because non-polynomial emitter outputs
        // (Conj/Fold/transcendental/piecewise) use type-specific .Hi
        // accesses and (DD) casts that DD4 doesn't support.
        int polyDegree = AstSaDetector.DetectZdPlusC(root);
        bool supportsDd4 = polyDegree >= 2;
        // For supported (polynomial) equations, substitute the DD body
        // into DD4 form. For non-polynomial equations (Conj / Fold /
        // transcendental / piecewise / prev) the DD4 method is gated
        // off at runtime by SupportsDd4Direct=false, but the body
        // placeholder still needs to substitute into compilable C# so
        // the dead method JITs without errors. Emit a no-op stub in
        // that case — the JIT DCEs the whole method body.
        string dd4DirectBody = supportsDd4
            ? ddDirectBody
                .Replace("zr_dd", "zr_dd4")
                .Replace("zi_dd", "zi_dd4")
                .Replace("cr_dd", "cr_dd4")
                .Replace("ci_dd", "ci_dd4")
                .Replace("DD ", "DD4 ")
            : "                DD4 zr_dd4_new = zr_dd4; DD4 zi_dd4_new = zi_dd4;";

        // Series Approximation gating. Two detector tiers:
        //   1. DetectZdPlusC — pure z^d+c (d in 2..5). Fast path: the
        //      original hardcoded emitter inlines binomial coefficients
        //      and Z^p chain, producing the most compact code.
        //   2. DetectPolyInZPlusC — generic polynomial in z plus c
        //      (covers items 1 plus mixed cases like z²+az+c, 2z³-z+c,
        //      z^6+c — anything z-poly with no CRef-mult or Conj/Folded).
        //      Emitter derives Taylor coefficients symbolically via
        //      AstDifferentiator and renders each F^(k)(Z) via
        //      ScalarEmitter. Slower codegen but unblocks SA for the
        //      broader equation class.
        // Other shapes — divisions, anti-holomorphic ops, c appearing
        // multiplicatively — keep SA off and run the regular
        // perturbation prelude.
        int saDegreeFast = supportsPerturbation ? AstSaDetector.DetectZdPlusC(root) : 0;
        (AstNode? saPolyZ, int saDegreeGeneric) = supportsPerturbation
            ? AstSaDetector.DetectPolyInZPlusC(root) : (null, 0);
        bool saFast = saDegreeFast >= 2;
        bool saGeneric = !saFast && saPolyZ != null && saDegreeGeneric >= 2;
        bool saEnabled = saFast || saGeneric;
        string saEnabledLit = saEnabled ? "true" : "false";
        // When SA disabled the SaEnabled const folds the surrounding
        // if-block to dead code, but the post-body assignments
        // `Sr1 = SrNew1; …` still need the new-value locals declared
        // for the file to compile. Emit zero stubs in the disabled
        // case so the JIT discards them along with everything else.
        // Wave 2.1 — SA order bumped 8 → 16. Must match the const SaOrders
        // declared in the template (Calculator.template.cs) and the manual
        // unroll sites that read Sr/Si/SrNew across the SA build + per-pixel
        // evaluation loops.
        const int SaOrderTarget = 16;
        string saRecurrenceBody;
        if (saFast)
            saRecurrenceBody = SaRecurrenceEmitter.Emit(saDegreeFast, indent: "                ", order: SaOrderTarget);
        else if (saGeneric)
            saRecurrenceBody = SaRecurrenceEmitter.EmitGeneric(saPolyZ!, saDegreeGeneric, indent: "                ", order: SaOrderTarget);
        else
            saRecurrenceBody = SaRecurrenceEmitter.EmitDisabledStub(indent: "                ", order: SaOrderTarget);

        string template = LoadTemplate("Calculator.template.cs");
        string rendered = template
            .Replace("{{CLASS_NAME}}",      name)
            // The template embeds the source in a single-line `//` comment, so a
            // multi-line equation must be collapsed to one line or its 2nd+ lines
            // escape the comment and become code (CS1002 / CS1529). #27 Phase 5a.
            .Replace("{{EQUATION_SOURCE}}", OneLine(equation))
            .Replace("{{DPDZ_TEXT}}",       AstPrinter.Print(dpdz))
            .Replace("{{DPDC_TEXT}}",       AstPrinter.Print(dpdc))
            .Replace("{{DERIV_TEXT}}",      AstPrinter.Print(derivUpdate))
            .Replace("{{TIMESTAMP}}",       DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"))
            .Replace("{{SCALAR_Z_BODY}}",   scalarZBody)
            .Replace("{{SCALAR_D_BODY}}",   scalarDBody)
            .Replace("{{AVX2_Z_BODY}}",     avxZBody)
            .Replace("{{AVX2_D_BODY}}",     avxDBody)
            .Replace("{{PERTURB_DELTA_BODY}}", perturbBody)
            .Replace("{{PERTURB_DELTA_DD_BODY}}", perturbDdBody)
            .Replace("{{PERTURB_DELTA_AVX2_BODY}}", perturbAvx2Body)
            .Replace("{{PERTURB_DERIV_AVX2_BODY}}", perturbDerivAvx2Body)
            .Replace("{{PERTURB_DELTA_AVX512_BODY}}", perturbAvx512Body)
            .Replace("{{PERTURB_DERIV_AVX512_BODY}}", perturbDerivAvx512Body)
            .Replace("{{PERTURB_DERIV_BODY}}", perturbDerivBody)
            .Replace("{{PERTURB_DELTA_TEXT}}", supportsPerturbation
                ? AstPrinter.Print(AstPerturbation.BuildDeltaUpdate(root))
                : "(perturbation disabled — non-holomorphic or rational equation)")
            .Replace("{{BLA_A_BODY}}", blaABody)
            .Replace("{{BLA_B_BODY}}", blaBBody)
            .Replace("{{QD_Z_BODY}}", qdZBody)
            .Replace("{{DD_DIRECT_BODY}}", ddDirectBody)
            .Replace("{{DD4_DIRECT_BODY}}", dd4DirectBody)
            .Replace("{{SUPPORTS_DD4_DIRECT}}", supportsDd4 ? "true" : "false")
            .Replace("{{QD_DIRECT_BODY}}", qdDirectBody)
            .Replace("{{SA_Z2C_ENABLED}}", saEnabledLit)
            .Replace("{{SA_RECURRENCE_BODY}}", saRecurrenceBody)
            .Replace("{{SUPPORTS_PERTURBATION}}", supportsPerturbation ? "true" : "false")
            .Replace("{{SUPPORTS_DE}}", supportsDe ? "true" : "false")
            .Replace("{{HAS_PREV}}", hasPrev ? "true" : "false")
            // Phoenix prev-state injection. When `prev` appears in the
            // equation, scalar and AVX2 loops carry an extra (pr, pi)
            // state vector initialised to zero and assigned from the
            // pre-step (zr, zi) before each new-z commit. When prev
            // isn't used, every substitution below is empty so the
            // generated body matches the non-Phoenix layout byte-for-
            // byte (no perf cost on the common path). For QD/DD direct
            // the extra state lives in (pr_q, pi_q) / (pr_dd, pi_dd).
            .Replace("{{PREV_DECL_SCALAR}}", hasPrev
                ? "        double pr = 0.0, pi = 0.0;\n" : "")
            .Replace("{{PREV_UPDATE_SCALAR}}", hasPrev
                ? "            pr = zr; pi = zi;\n" : "")
            .Replace("{{PREV_DECL_AVX2}}", hasPrev
                ? "        Vector256<double> pr = Vector256<double>.Zero;\n" +
                  "        Vector256<double> pi = Vector256<double>.Zero;\n"
                : "")
            // AVX2 prev update must respect the per-lane active mask so
            // escaped lanes keep their pre-escape prev (matches the
            // BlendVariable freeze applied to zr/zi on the next line).
            .Replace("{{PREV_UPDATE_AVX2}}", hasPrev
                ? "            pr = Avx.BlendVariable(pr, zr, keepD);\n" +
                  "            pi = Avx.BlendVariable(pi, zi, keepD);\n"
                : "")
            .Replace("{{PREV_UPDATE_AVX2_RAW}}", hasPrev
                ? "            pr = Avx.BlendVariable(pr, zr, activeMaskL.AsDouble());\n" +
                  "            pi = Avx.BlendVariable(pi, zi, activeMaskL.AsDouble());\n"
                : "")
            .Replace("{{PREV_DECL_DD_DIRECT}}", hasPrev
                ? "        DD pr_dd = DD.Zero, pi_dd = DD.Zero;\n" : "")
            .Replace("{{PREV_UPDATE_DD_DIRECT}}", hasPrev
                ? "            pr_dd = zr_dd; pi_dd = zi_dd;\n" : "")
            .Replace("{{PREV_DECL_QD_DIRECT}}", hasPrev
                ? "        QD pr_q = QD.Zero, pi_q = QD.Zero;\n" : "")
            .Replace("{{PREV_UPDATE_QD_DIRECT}}", hasPrev
                ? "            pr_q = zr_q; pi_q = zi_q;\n" : "")
            .Replace("{{PREV_DECL_QD_REF}}", hasPrev
                ? "            QD pr_q = QD.Zero, pi_q = QD.Zero;\n" : "")
            .Replace("{{PREV_UPDATE_QD_REF}}", hasPrev
                ? "                    pr_q = zr_q; pi_q = zi_q;\n" : "")
            .Replace("{{PREV_DECL_SCALAR_REF}}", hasPrev
                ? "            double pr = 0.0, pi = 0.0;\n" : "")
            .Replace("{{PREV_UPDATE_SCALAR_REF}}", hasPrev
                ? "                pr = zr; pi = zi;\n" : "")
            // Iteration-index injection. Pulled from whatever loop
            // counter is in scope at each site (`it` in scalar/HpDirect
            // loops, `n` in AVX2 lane + QD/scalar ref orbit). Per-emitter
            // IterRe binding resolves to the locally-injected name.
            // Empty when !hasIter so non-iter calcs are byte-identical.
            .Replace("{{ITER_DECL_SCALAR}}", hasIter
                ? "            double iter = (double)it;\n" : "")
            .Replace("{{ITER_DECL_AVX2}}", hasIter
                ? "            Vector256<double> iter_v = Vector256.Create((double)n);\n" : "")
            .Replace("{{ITER_DECL_SCALAR_REF}}", hasIter
                ? "                double iter = (double)n;\n" : "")
            .Replace("{{ITER_DECL_QD_REF}}", hasIter
                ? "                    QD iter_q = (QD)(double)n;\n" : "")
            .Replace("{{ITER_DECL_DD_DIRECT}}", hasIter
                ? "            DD iter_dd = (DD)(double)it;\n" : "")
            .Replace("{{ITER_DECL_QD_DIRECT}}", hasIter
                ? "            QD iter_q = (QD)(double)it;\n" : "")
            .Replace("{{BAILOUT_RADIUS_SQ}}",
                (bailoutRadius * bailoutRadius).ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        string? selfTestRendered = null;
        if (includeSelfTest)
        {
            string stTmpl = LoadTemplate("SelfTest.template.cs");
            selfTestRendered = stTmpl.Replace("{{CLASS_NAME}}", name);
        }

        return new GenerateResult(name, rendered, selfTestRendered, null);
    }

    /// <summary>
    /// Parse <paramref name="equation"/> and return its AST + symbolic
    /// derivatives + the generator's feature flags. No code emission.
    /// Used by the UserEquation dialog's live-preview panel (Wave 2.4
    /// D-6.24) so the user sees `dz/dc`, SA gating, and DE/perturbation
    /// availability as they type. Parse failures populate Error and
    /// leave every other field at its default.
    /// </summary>
    public static PreviewResult Preview(string equation)
    {
        if (string.IsNullOrWhiteSpace(equation))
            return new PreviewResult { Error = "Equation is empty." };

        AstNode root;
        try
        {
            root = EquationParser.Parse(equation);
        }
        catch (Exception ex)
        {
            return new PreviewResult { Error = $"Parse error: {ex.Message}" };
        }

        var dpdz = AstDifferentiator.DpDz(root);
        var dpdc = AstDifferentiator.DpDc(root);

        bool hasConj   = AstHelpers.Contains<Conj>(root);
        bool hasFolded = AstHelpers.Contains<Folded>(root);
        bool hasDiv    = AstHelpers.Contains<Div>(root);
        bool hasTrans  = AstHelpers.Contains<Sin>(root)
                      || AstHelpers.Contains<Cos>(root)
                      || AstHelpers.Contains<Exp>(root)
                      || AstHelpers.Contains<Log>(root)
                      || AstHelpers.Contains<Arg>(root)
                      || AstHelpers.Contains<Atan2>(root)
                      || AstHelpers.Contains<Min>(root)
                      || AstHelpers.Contains<Max>(root)
                      || AstHelpers.Contains<Mod>(root)
                      // #27 Phase 6 — re/im/abs/clamp are non-holomorphic; disable
                      // perturbation + SA (they render on the direct scalar/AVX/
                      // DD/QD paths at shallow zoom, which is where parity holds).
                      || AstHelpers.Contains<ReOp>(root)
                      || AstHelpers.Contains<ImOp>(root)
                      || AstHelpers.Contains<AbsOp>(root)
                      || AstHelpers.Contains<Clamp>(root)
                      // #27 Phase 6 (tranche 2) — general pow(base,exp) is
                      // transcendental (exp·log) + non-holomorphic under the
                      // δ-Taylor expansion; disable perturbation/SA + analytic DE.
                      || AstHelpers.Contains<PowC>(root)
                      // #27 Phase 6 (tranche 2) — inverse trig / hyperbolic are
                      // non-polynomial (no closed-form δ-Taylor / SA) and have no
                      // analytic-DE rules implemented; disable perturbation/SA + DE.
                      || AstHelpers.Contains<Asin>(root)
                      || AstHelpers.Contains<Acos>(root)
                      || AstHelpers.Contains<Atan>(root)
                      || AstHelpers.Contains<Asinh>(root)
                      || AstHelpers.Contains<Acosh>(root)
                      || AstHelpers.Contains<Atanh>(root)
                      // #27 Phase 6 (tranche 2) — per-component floor/round/ceil/
                      // trunc/fract/sign are piecewise-constant (non-holomorphic);
                      // disable perturbation/SA + analytic DE.
                      || AstHelpers.Contains<Floor>(root)
                      || AstHelpers.Contains<Round>(root)
                      || AstHelpers.Contains<Ceil>(root)
                      || AstHelpers.Contains<Trunc>(root)
                      || AstHelpers.Contains<Fract>(root)
                      || AstHelpers.Contains<Sign>(root);
        bool hasArg    = AstHelpers.Contains<Arg>(root)
                      || AstHelpers.Contains<Atan2>(root)
                      || AstHelpers.Contains<Min>(root)
                      || AstHelpers.Contains<Max>(root)
                      || AstHelpers.Contains<Mod>(root)
                      // #27 Phase 6 — re/im/abs/clamp are non-holomorphic → the
                      // dz/dc chain is meaningless; disable analytic DE.
                      || AstHelpers.Contains<ReOp>(root)
                      || AstHelpers.Contains<ImOp>(root)
                      || AstHelpers.Contains<AbsOp>(root)
                      || AstHelpers.Contains<Clamp>(root)
                      // #27 Phase 6 (tranche 2) — general pow(base,exp) is
                      // transcendental (exp·log) + non-holomorphic under the
                      // δ-Taylor expansion; disable perturbation/SA + analytic DE.
                      || AstHelpers.Contains<PowC>(root)
                      // #27 Phase 6 (tranche 2) — inverse trig / hyperbolic are
                      // non-polynomial (no closed-form δ-Taylor / SA) and have no
                      // analytic-DE rules implemented; disable perturbation/SA + DE.
                      || AstHelpers.Contains<Asin>(root)
                      || AstHelpers.Contains<Acos>(root)
                      || AstHelpers.Contains<Atan>(root)
                      || AstHelpers.Contains<Asinh>(root)
                      || AstHelpers.Contains<Acosh>(root)
                      || AstHelpers.Contains<Atanh>(root)
                      // #27 Phase 6 (tranche 2) — per-component floor/round/ceil/
                      // trunc/fract/sign are piecewise-constant (non-holomorphic);
                      // disable perturbation/SA + analytic DE.
                      || AstHelpers.Contains<Floor>(root)
                      || AstHelpers.Contains<Round>(root)
                      || AstHelpers.Contains<Ceil>(root)
                      || AstHelpers.Contains<Trunc>(root)
                      || AstHelpers.Contains<Fract>(root)
                      || AstHelpers.Contains<Sign>(root);
        bool hasCond  = AstHelpers.Contains<If>(root);
        bool hasPrev  = AstHelpers.Contains<PrevRef>(root);
        bool hasIter  = AstHelpers.Contains<IterRef>(root);
        bool supportsDe = !(hasConj || hasFolded || hasPrev || hasArg || hasIter);
        bool supportsPerturbation = !(hasConj || hasFolded || hasDiv || hasTrans || hasCond || hasPrev || hasIter);

        int saFast = supportsPerturbation ? AstSaDetector.DetectZdPlusC(root) : 0;
        (AstNode? saPolyZ, int saGeneric) = supportsPerturbation
            ? AstSaDetector.DetectPolyInZPlusC(root) : (null, 0);
        bool saFastOn = saFast >= 2;
        bool saGenOn = !saFastOn && saPolyZ != null && saGeneric >= 2;

        return new PreviewResult(
            AstText: AstPrinter.Print(root),
            DpDzText: AstPrinter.Print(dpdz),
            DpDcText: AstPrinter.Print(dpdc),
            SaFastDegree: saFastOn ? saFast : 0,
            SaGenericDegree: saGenOn ? saGeneric : 0,
            SaEnabled: saFastOn || saGenOn,
            SupportsPerturbation: supportsPerturbation,
            SupportsDe: supportsDe,
            HasPrev: hasPrev,
            HasIter: hasIter,
            HasConj: hasConj,
            HasFolded: hasFolded,
            HasDiv: hasDiv,
            HasTrans: hasTrans,
            HasCond: hasCond,
            Error: null);
    }

    private static string LoadTemplate(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        string resName = System.Linq.Enumerable.First(asm.GetManifestResourceNames(),
            n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resName)
            ?? throw new InvalidOperationException($"Embedded template missing: {fileName}");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Collapse CR/LF (and any following comment-continuation
    /// whitespace) to single spaces so a multi-line source can be embedded in a
    /// one-line `//` comment without its later lines escaping the comment.</summary>
    private static string OneLine(string s)
        => string.IsNullOrEmpty(s)
            ? string.Empty
            : s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
}
