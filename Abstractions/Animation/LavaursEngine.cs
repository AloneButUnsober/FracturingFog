// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;

namespace FracturingFog.Abstractions.Animation;

/// <summary>The Lavaurs map <c>g_α</c> engine for the faithful parabolic implosion (#918 SG1,
/// epic #911). Evaluates <c>g_α = f ∘ Φ_rep⁻¹ ∘ T_α ∘ Φ_att</c> for the parabolic quadratic
/// <c>f_c(z) = z² + 1/4</c> (parabolic fixed point <c>z* = 1/2</c>, germ <c>f(w) = w + w²</c> in
/// the shifted coordinate <c>w = z − 1/2</c>). This is the production port of the S3/S4/S6
/// numerics (design doc <c>Parabolic-Implosion-DesignPlan.md</c> §9).
/// <para><b>Method.</b> Both Fatou coordinates use the closed asymptotic
/// <c>ψ(w) = Z − log Z + 1/(2Z)</c>, <c>Z = −1/w</c> (the <c>Z = −1/w</c> normal form
/// <c>Z ↦ Z²/(Z−1)</c> gives a pure-power tail). <c>Φ_att</c> is a forward-orbit limit
/// <c>ψ(fⁿw) − n</c> (principal log); <c>Φ_rep</c> a backward-orbit limit <c>ψ(f⁻ⁿw) + n</c> with a
/// <c>[0,2π)</c> log branch and the rationalised inverse <c>f⁻¹(w) = 2w/(1+√(1+4w))</c>.</para>
/// <para><b>The inverse (the S6 subtlety).</b> The Lavaurs target <c>σ = Φ_att(w) + α</c> has large
/// positive real part, so <c>Φ_rep⁻¹(σ)</c> lands not on the repelling petal but on its
/// analytic continuation over the <b>attracting-side</b> range. Rather than track the log branch
/// across the cut, the inverse is built by the <b>functional equation</b>
/// <c>Φ_rep⁻¹(σ) = fᵏ(Φ_rep⁻¹(σ − k))</c>: seed deep in the repelling petal at <c>σ − k</c>
/// (Re ≈ <c>−deepOffset</c>, where a backward-iteration Newton is robust — validated to ~3e-12),
/// then apply <c>f</c> <c>k</c> times, which carries the point through the petal gap onto the
/// attracting side without any manual branch bookkeeping.</para>
/// <para><b>Validity / domain.</b> <c>g_α</c> is defined on the attracting basin; a boundary point
/// drives either <c>Φ_att</c> to divergence or the <c>fᵏ</c> continuation to escape. Every entry
/// point returns a validity flag, so a caller (the word-tree calculator, #930) treats an invalid
/// result as "<c>g_α</c> undefined here → only <c>f</c> applies" (its domain-restricted
/// generator).</para>
/// <para><b>Validation</b> (SG1 spike, deleted): Abel equation ~1e-10; two independent inverse
/// algorithms agree to ~1e-7; Lavaurs periodicity <c>g_{α+1} = f ∘ g_α</c> holds by construction;
/// Douady re-injection reproduced (<c>f</c> alone → <c>z*</c>, <c>g_α</c> re-injects). Per-call
/// cost is milliseconds; the render path uses <see cref="LavaursCoordinateTable"/> (α-independent
/// grids + bilinear interpolation) for the S6 ~µs fast path.</para></summary>
public sealed class LavaursEngine
{
    /// <summary>The parabolic parameter (fixed: the <c>c = 1/4</c> cusp; germ <c>f(w)=w+w²</c>).</summary>
    public const double C = 0.25;
    /// <summary>The parabolic fixed point <c>z* = 1/2</c> (shift between the <c>z</c> and germ
    /// <c>w = z − z*</c> coordinates).</summary>
    public const double FixedPoint = 0.5;

    readonly int _n;
    readonly double _deep;

    /// <param name="iterations">Fatou-coordinate orbit length (the <c>n</c> in <c>ψ(fⁿw)∓n</c>);
    /// the parabolic <c>1/n</c> tail makes the truncation error ~<c>1/n³</c> (1500 → ~2e-10).</param>
    /// <param name="deepOffset">How deep into the repelling petal the inverse seeds
    /// (Re <c>σ₀ ≈ −deepOffset</c>); large enough for a robust seed, small enough that the
    /// <c>fᵏ</c> continuation does not overshoot.</param>
    public LavaursEngine(int iterations = 1500, double deepOffset = 18.0)
    {
        _n = global::System.Math.Max(50, iterations);
        _deep = global::System.Math.Max(6.0, deepOffset);
    }

    // germ f(w) = w + w² and its rationalised inverse (the naïve form cancels, capping accuracy).
    static Complex F(Complex w) => w + w * w;
    static Complex Finv(Complex w) => 2.0 * w / (1.0 + Complex.Sqrt(1.0 + 4.0 * w));

    // asymptotic Fatou coordinate ψ(w) = Z − log Z + 1/(2Z), Z = −1/w, with a per-petal log branch.
    static Complex Psi(Complex w, bool repelling)
    {
        Complex z = -1.0 / w, lg;
        if (repelling)
        {
            double im = global::System.Math.Atan2(z.Imaginary, z.Real);
            if (im < 0) im += 2.0 * global::System.Math.PI;                 // [0,2π) branch for the repelling petal
            lg = new Complex(global::System.Math.Log(z.Magnitude), im);
        }
        else
        {
            lg = Complex.Log(z);                             // principal branch (attracting petal)
        }
        return z - lg + 1.0 / (2.0 * z);
    }

    /// <summary>The attracting Fatou coordinate <c>Φ_att(w)</c> (germ coordinate <c>w = z − 1/2</c>),
    /// a forward-orbit limit. Diverges (returns non-finite) outside the attracting basin.</summary>
    public Complex PhiAtt(Complex w)
    {
        Complex z = w;
        for (int k = 0; k < _n; k++) z = F(z);
        return Psi(z, false) - _n;
    }

    /// <summary>The repelling Fatou coordinate <c>Φ_rep(w)</c>, a backward-orbit limit
    /// (repelling petal <c>Re w &gt; 0</c>).</summary>
    public Complex PhiRep(Complex w)
    {
        Complex z = w;
        for (int k = 0; k < _n; k++) z = Finv(z);
        return Psi(z, true) + _n;
    }

    // invert Φ_rep DEEP in the repelling petal (Re σ ≪ 0) by Newton — the robust regime.
    Complex RepInvDeep(Complex s, out double residual)
    {
        // seed: σ ≈ Z − log Z (repelling branch) ⇒ Z ≈ σ + log Z; w = −1/Z.
        Complex zc = s;
        for (int i = 0; i < 120; i++)
        {
            double im = global::System.Math.Atan2(zc.Imaginary, zc.Real);
            if (im < 0) im += 2.0 * global::System.Math.PI;
            zc = s + new Complex(global::System.Math.Log(zc.Magnitude), im);
        }
        Complex w = -1.0 / zc;
        residual = double.PositiveInfinity;
        for (int i = 0; i < 120; i++)
        {
            Complex fv = PhiRep(w) - s;
            residual = fv.Magnitude;
            if (residual < 1e-13) break;
            Complex h = 1e-7 * global::System.Math.Max(1.0, w.Magnitude);
            Complex d = (PhiRep(w + h) - PhiRep(w)) / h;
            if (d.Magnitude < 1e-30) break;
            w -= fv / d;
        }
        return w;
    }

    /// <summary>Invert the repelling Fatou coordinate onto the attracting-side continuation:
    /// find <c>w</c> with <c>Φ_rep(w) = sigma</c> via the functional-equation continuation
    /// (deep seed + <c>fᵏ</c>). Returns false (and <paramref name="w"/> = NaN) when the deep seed
    /// fails to converge or the continuation escapes — i.e. <c>sigma</c> is outside the
    /// well-conditioned range (a basin-boundary point).</summary>
    public bool TryPhiRepInv(Complex sigma, out Complex w)
    {
        w = new Complex(double.NaN, double.NaN);
        if (!double.IsFinite(sigma.Real) || !double.IsFinite(sigma.Imaginary)) return false;
        int k = (int)global::System.Math.Ceiling(sigma.Real + _deep);
        if (k < 1) k = 1;
        Complex s0 = sigma - k;
        Complex wv = RepInvDeep(s0, out double res);
        if (!(res < 1e-10)) return false;                    // deep seed did not converge
        for (int j = 0; j < k; j++)
        {
            wv = F(wv);
            if (!double.IsFinite(wv.Real) || wv.Magnitude > 1e6) return false;   // continuation escaped
        }
        w = wv;
        return true;
    }

    /// <summary>The Lavaurs map <c>g_α</c> in the <b>z-plane</b> (<c>z² + 1/4</c>'s parabolic
    /// implosion), <c>G_α(z) = g_α^w(z − 1/2) + 1/2</c> with <c>g_α^w = f ∘ Φ_rep⁻¹ ∘ T_α ∘ Φ_att</c>.
    /// Returns false when <paramref name="z"/> is outside the attracting basin (<c>Φ_att</c>
    /// diverges) or the inverse continuation is ill-conditioned — the effective domain of
    /// <c>g_α</c>.</summary>
    public bool TryGAlpha(Complex z, double alpha, out Complex gz)
    {
        gz = new Complex(double.NaN, double.NaN);
        Complex w = z - FixedPoint;
        Complex tau = PhiAtt(w);
        if (!double.IsFinite(tau.Real) || !double.IsFinite(tau.Imaginary)) return false;
        if (!TryPhiRepInv(tau + alpha, out Complex wr)) return false;
        Complex gw = F(wr);
        if (!double.IsFinite(gw.Real)) return false;
        gz = gw + FixedPoint;
        return true;
    }

    /// <summary>Whether <paramref name="z"/> is in the attracting basin of the parabolic fixed
    /// point (the domain of <c>g_α</c>): the <c>f_c</c> orbit stays bounded and settles toward
    /// <c>z* = 1/2</c>. A cheap predicate for the word-tree generator's domain gate.</summary>
    public bool InAttractingBasin(Complex z, int probe = 400)
    {
        Complex zz = z;
        for (int k = 0; k < probe; k++)
        {
            zz = zz * zz + C;
            if (!double.IsFinite(zz.Real) || zz.Magnitude > 2.0) return false;
        }
        return (zz - FixedPoint).Magnitude < 0.35;           // converged into the parabolic petal
    }

    // exposed for the coordinate table (α-independent building blocks).
    internal int Iterations => _n;
}
