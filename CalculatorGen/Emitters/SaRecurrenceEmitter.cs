// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SaRecurrenceEmitter.cs
//
// Emits the per-iteration body for the Series Approximation prelude
// for z^d + c, d ∈ {2, 3, 4, 5}. Output is a C# code block that runs
// inside the SA loop, reading (Zr2, Zi2) from the reference orbit and
// (Sr1..SrN, Si1..SiN) as the current coefficient state. Produces new
// locals (SrNew1..SrNewN, SiNew1..SiNewN); the surrounding loop
// assigns those back to the coefficient state.
//
// Recurrence derivation. Let δ_n = Σ_{k=1..N} S_{n,k} · ε^k. Then
//   δ_{n+1} = (Z + δ)^d − Z^d + ε
//           = Σ_{m=1..d}  C(d,m) · Z^(d-m) · δ^m  + ε
// The ε^k coefficient of δ^m is the m-fold convolution of the S
// sequence:
//   (δ^m)_k = Σ_{i₁+…+i_m = k, iⱼ ≥ 1} S_{i₁}·…·S_{i_m}
// computed incrementally as (δ^m)_k = Σ_{i=m-1..k-1} (δ^(m-1))_i · S_{k-i}.
// So:
//   S_{n+1, k} = Σ_{m=1..min(d,k)} C(d,m) · Z_n^(d-m) · (δ_n^m)_k  + [k==1]
//
// Earlier implementation hard-coded N=3 (A, B, C). This version
// generalises: pass `order` ≥ 2 and the emitter generates the full
// polynomial-mult unroll. Bigger N extends the validity range of the
// series, letting per-pixel skip further into the orbit before the
// per-pixel δ-recurrence has to take over. Cost: per-iter SA-build
// work scales O(d · N²). N=8 with d=5 gives ~320 mults/iter — still
// negligible against the per-pixel work that follows.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public static class SaRecurrenceEmitter
{
    /// <summary>Generic-polynomial SA emitter. Accepts the z-only
    /// polynomial F(z) (the part of the equation that isn't <c>+c</c>)
    /// and derives the SA recurrence symbolically via Taylor expansion:
    ///   F(Z + δ) − F(Z) = Σ_{k=1..d} (1/k!) F^(k)(Z) · δ^k
    /// where F^(k)(Z) is the k-th z-derivative of F evaluated at the
    /// reference orbit. Each F^(k) is emitted as scalar code via
    /// <see cref="ScalarEmitter"/>; the convolution structure (δ^k)_K
    /// is identical to the pure z^d+c form.
    ///
    /// Unlocks SA for any polynomial-in-z plus c — e.g. <c>z^2 + a*z + c</c>,
    /// <c>2*z^3 − z + c</c>, <c>z^2 - 0.5*z + c</c>. See
    /// <see cref="AstSaDetector.DetectPolyInZPlusC"/> for the detector.</summary>
    public static string EmitGeneric(AstNode polyZ, int degree, string indent, int order = 8)
    {
        if (degree < 2 || degree > 16)
            return $"{indent}#error generic SA degree {degree} out of range;";
        if (order < 2)
            return $"{indent}#error SA order {order} must be ≥ 2;";

        var sb = new StringBuilder();

        // Rebind Zr2/Zi2 (template scope) as zr/zi for ScalarEmitter.
        // ScalarEmitter's defaults emit `zr`/`zi` for ZRef; the SA loop
        // body uses `Zr2`/`Zi2` for the ref-orbit Hi limb at iter n. The
        // alias is a single assignment, eliminated by JIT — no cost.
        W(sb, indent, "double zr = Zr2, zi = Zi2;");

        // ── Emit p_k(Z) = (1/k!) · ∂^k F / ∂z^k|_{Z_n} for k = 1..d ──
        // Each partial is a polynomial in z (the original F drops one
        // z power per ∂z), simplified, then run through ScalarEmitter
        // to render the (real, imag) expressions at Z_n. The 1/k!
        // Taylor coefficient is folded into the emitted constant.
        var scalar = new ScalarEmitter();
        for (int k = 1; k <= degree; k++)
        {
            // Differentiate k times. Simplify after each step to keep
            // the AST compact.
            AstNode partial = polyZ;
            for (int i = 0; i < k; i++)
            {
                partial = AstDifferentiator.Diff(partial, AstDifferentiator.Var.Z);
                partial = AstSimplifier.Simplify(partial);
            }
            // Skip if the k-th derivative simplified to 0 (e.g. F has
            // total z-degree < k).
            if (partial is RealConst rc && rc.Value == 0.0)
            {
                W(sb, indent, $"double pk{k}_Re = 0.0, pk{k}_Im = 0.0;");
                continue;
            }
            // Scale by 1/k!.
            double inv = 1.0 / Factorial(k);
            AstNode scaled = inv == 1.0 ? partial
                : AstSimplifier.Simplify(new Mul(new RealConst(inv), partial));
            // Render. Emit the AST directly into pk{k}_Re/pk{k}_Im locals
            // that the convolution unroll below reads. Bypass
            // EmitNewValueBody's `prefix r_new` / `prefix i_new` naming —
            // we need `pk{k}_Re` / `pk{k}_Im` to match the references
            // below (the previous `EmitNewValueBody(scaled, "pk1", ...)`
            // path emitted `pk1r_new`/`pk1i_new` instead, which never
            // resolved against the convolution lookup — latent bug
            // surfaced when PR8's `i*z*z + c` first exercised the
            // generic SA path).
            var ev = scalar.Emit(scaled);
            W(sb, indent, $"double pk{k}_Re = {ev.Re};");
            W(sb, indent, $"double pk{k}_Im = {ev.Im};");
        }

        // ── (δ^m)_k convolutions for m = 2..degree, k = m..order ─────
        for (int m = 2; m <= degree; m++)
        {
            for (int k = m; k <= order; k++)
            {
                var reTerms = new List<string>();
                var imTerms = new List<string>();
                for (int i = m - 1; i <= k - 1; i++)
                {
                    string aRe = m == 2 ? $"Sr{i}" : $"dPow{m - 1}_{i}_Re";
                    string aIm = m == 2 ? $"Si{i}" : $"dPow{m - 1}_{i}_Im";
                    int j = k - i;
                    string bRe = $"Sr{j}";
                    string bIm = $"Si{j}";
                    reTerms.Add($"({aRe}*{bRe} - {aIm}*{bIm})");
                    imTerms.Add($"({aRe}*{bIm} + {aIm}*{bRe})");
                }
                W(sb, indent,
                    $"double dPow{m}_{k}_Re = " + string.Join(" + ", reTerms) + ";");
                W(sb, indent,
                    $"double dPow{m}_{k}_Im = " + string.Join(" + ", imTerms) + ";");
            }
        }

        // ── S_K_new = Σ_{k=1..min(d,K)} pk_complex · (δ^k)_K + [K==1] ─
        for (int K = 1; K <= order; K++)
        {
            var reTerms = new List<string>();
            var imTerms = new List<string>();
            int kMax = degree < K ? degree : K;
            for (int k = 1; k <= kMax; k++)
            {
                // (δ^k)_K source: S_K when k=1; dPow{k}_{K}_Re/Im otherwise.
                string dRe = k == 1 ? $"Sr{K}" : $"dPow{k}_{K}_Re";
                string dIm = k == 1 ? $"Si{K}" : $"dPow{k}_{K}_Im";
                // Complex multiply pk · (δ^k)_K.
                reTerms.Add($"(pk{k}_Re * {dRe} - pk{k}_Im * {dIm})");
                imTerms.Add($"(pk{k}_Re * {dIm} + pk{k}_Im * {dRe})");
            }
            if (K == 1) reTerms.Add("1.0");
            string reExpr = reTerms.Count == 0 ? "0.0" : string.Join(" + ", reTerms);
            string imExpr = imTerms.Count == 0 ? "0.0" : string.Join(" + ", imTerms);
            W(sb, indent, $"double SrNew{K} = {reExpr};");
            W(sb, indent, $"double SiNew{K} = {imExpr};");
        }

        return sb.ToString().TrimEnd();
    }

    private static double Factorial(int n)
    {
        double r = 1.0;
        for (int i = 2; i <= n; i++) r *= i;
        return r;
    }

    /// <summary>Build the body. <paramref name="indent"/> is prepended
    /// to every emitted line. <paramref name="order"/> is N — number of
    /// coefficients tracked. Defaults to 8.</summary>
    public static string Emit(int degree, string indent, int order = 8)
    {
        if (degree < 2 || degree > 5)
            return $"{indent}#error SA recurrence for degree {degree} not implemented;";
        if (order < 2)
            return $"{indent}#error SA order {order} must be ≥ 2;";

        var sb = new StringBuilder();

        // ── Z^p for p = 1..degree-1 (Z^0 = 1 handled inline) ─────────
        // Z^1 is just the reference (Zr2, Zi2).
        W(sb, indent, "double Zp1Re = Zr2, Zp1Im = Zi2;");
        for (int p = 2; p <= degree - 1; p++)
        {
            W(sb, indent,
                $"double Zp{p}Re = Zp{p - 1}Re * Zr2 - Zp{p - 1}Im * Zi2;");
            W(sb, indent,
                $"double Zp{p}Im = Zp{p - 1}Re * Zi2 + Zp{p - 1}Im * Zr2;");
        }

        // ── (δ^m)_k for m = 2..degree, k = m..N ──────────────────────
        // dPow{m}_{k}_Re/Im. (δ^1)_k = S_k (no separate emission).
        for (int m = 2; m <= degree; m++)
        {
            for (int k = m; k <= order; k++)
            {
                var reTerms = new List<string>();
                var imTerms = new List<string>();
                for (int i = m - 1; i <= k - 1; i++)
                {
                    string aRe = m == 2 ? $"Sr{i}" : $"dPow{m - 1}_{i}_Re";
                    string aIm = m == 2 ? $"Si{i}" : $"dPow{m - 1}_{i}_Im";
                    int j = k - i;
                    string bRe = $"Sr{j}";
                    string bIm = $"Si{j}";
                    reTerms.Add($"({aRe}*{bRe} - {aIm}*{bIm})");
                    imTerms.Add($"({aRe}*{bIm} + {aIm}*{bRe})");
                }
                W(sb, indent,
                    $"double dPow{m}_{k}_Re = " + string.Join(" + ", reTerms) + ";");
                W(sb, indent,
                    $"double dPow{m}_{k}_Im = " + string.Join(" + ", imTerms) + ";");
            }
        }

        // ── S_k_new = Σ_{m=1..min(d,k)} C(d,m) · Z^(d-m) · (δ^m)_k  + [k==1] ──
        for (int k = 1; k <= order; k++)
        {
            var reTerms = new List<string>();
            var imTerms = new List<string>();
            int mMax = degree < k ? degree : k;
            for (int m = 1; m <= mMax; m++)
            {
                long coef = Binomial(degree, m);
                int zPow = degree - m;
                string dRe = m == 1 ? $"Sr{k}" : $"dPow{m}_{k}_Re";
                string dIm = m == 1 ? $"Si{k}" : $"dPow{m}_{k}_Im";
                string coefLit = coef.ToString(CultureInfo.InvariantCulture) + ".0";
                if (zPow == 0)
                {
                    // Z^0 = 1 — no complex multiply.
                    if (coef == 1)
                    {
                        reTerms.Add(dRe);
                        imTerms.Add(dIm);
                    }
                    else
                    {
                        reTerms.Add($"{coefLit} * {dRe}");
                        imTerms.Add($"{coefLit} * {dIm}");
                    }
                }
                else
                {
                    string zRe = $"Zp{zPow}Re";
                    string zIm = $"Zp{zPow}Im";
                    string reInner = $"({zRe} * {dRe} - {zIm} * {dIm})";
                    string imInner = $"({zRe} * {dIm} + {zIm} * {dRe})";
                    if (coef == 1)
                    {
                        reTerms.Add(reInner);
                        imTerms.Add(imInner);
                    }
                    else
                    {
                        reTerms.Add($"{coefLit} * {reInner}");
                        imTerms.Add($"{coefLit} * {imInner}");
                    }
                }
            }
            if (k == 1) reTerms.Add("1.0");
            string reExpr = reTerms.Count == 0 ? "0.0" : string.Join(" + ", reTerms);
            string imExpr = imTerms.Count == 0 ? "0.0" : string.Join(" + ", imTerms);
            W(sb, indent, $"double SrNew{k} = {reExpr};");
            W(sb, indent, $"double SiNew{k} = {imExpr};");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Stub body for the SA-disabled case. The surrounding
    /// template still needs SrNew1..SrNewN declared so the file
    /// compiles; the JIT discards the whole block via the
    /// <c>SaEnabled</c> const flag.</summary>
    public static string EmitDisabledStub(string indent, int order = 8)
    {
        var sb = new StringBuilder();
        for (int k = 1; k <= order; k++)
            W(sb, indent, $"double SrNew{k} = 0.0, SiNew{k} = 0.0;");
        return sb.ToString().TrimEnd();
    }

    private static long Binomial(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        if (k == 0 || k == n) return 1;
        if (k > n - k) k = n - k;
        long c = 1;
        for (int i = 0; i < k; i++)
        {
            c = c * (n - i) / (i + 1);
        }
        return c;
    }

    private static void W(StringBuilder sb, string indent, string line)
        => sb.Append(indent).Append(line).Append('\n');
}
