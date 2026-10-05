// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Jacobian.cs (#1128, epic #1114 S14)
//
// FTLE / Jacobian anisotropy (Docs/Technical/DualOrbit-Coloring-RnD.md §3.D).
// The pair map (s, c₀) ↦ (z_N, c_N) is holomorphic in both variables, and z
// does not depend on c₀, so its complex Jacobian is lower-triangular:
//
//     J = [[a, 0], [b, d]],  a = ∂z_N/∂s,  b = ∂c_N/∂s,  d = ∂c_N/∂c₀
//     a' = 2z·a + 1,  b' = 2c·b + 1,  d' = 2c·d   (a₀ = b₀ = 0, d₀ = 1)
//
// As a real 4×4 map each complex singular value appears twice; for the 2×2
// complex J, with T = |a|² + |b|² + |d|²:
//     σ₁² = (T + √(T² − 4|ad|²)) / 2,   σ₂ = |a·d| / σ₁   (stable form).
// N = the lockstep step count — until EITHER orbit escapes (maxIter if both are
// bounded). Each derivative carries its OWN log-scale so neither overflows nor
// underflows against the other (at the Misiurewicz point s = −2, |a| ~ 4^N while
// |d| ~ 2^N — a common scale lost d entirely); σ₁, σ₂ are formed in logs.
//
//   Ftle               ln σ₁ / N — finite-time Lyapunov exponent of the pair map;
//                      ridges = coherent-structure analogues (Haller 2015).
//                      Palette: ½ + ½·clamp(FTLE / DualOrbitFtleSpan).
//   JacobianAnisotropy ln(σ₁/σ₂) — how unequally the pair map stretches;
//                      A/(A + DualOrbitAnisotropyScale).
// Interior check (tests): with s in a hyperbolic component and the c-orbit
// attracted to a p-cycle of multiplier μ, d ~ μ^{N/p} → 0 while a, b converge,
// so ln σ₂ / N → ln|μ| / p.
//
// LIC (line integral convolution) is the colour-only post-process in .Lic.cs.
// The separation direction arg(c_N − z_N) it can follow is recorded here.
// Complex map only; the quaternion map gives interior.

using System;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    internal static bool IsJacobianField(DualOrbitField f)
        => f is DualOrbitField.Ftle or DualOrbitField.JacobianAnisotropy;

    private static double Hypot(double x, double y)
    {
        double ax = Math.Abs(x), ay = Math.Abs(y);
        double mx = Math.Max(ax, ay), mn = Math.Min(ax, ay);
        if (mx == 0) return 0;
        double r = mn / mx;
        return mx * Math.Sqrt(1 + r * r);
    }

    private const double JacRescale = 1e100, LnJacRescale = 230.25850929940458;   // ln 1e100

    /// <summary>#1128 — the pair-map Jacobian after the lockstep run: entries
    /// a, b, d, each a mantissa times its OWN scale e^{LogA|LogB|LogD} (the entries
    /// grow at different rates — e.g. |a| ~ 4^N against |d| ~ 2^N at s = −2 — so a
    /// common scale would underflow the slower one), the step count N and the
    /// separation c_N − z_N.</summary>
    public readonly record struct PairJacobian(
        double Ar, double Ai, double LogA, double Br, double Bi, double LogB, double Dr, double Di, double LogD,
        int N, double SepX, double SepY)
    {
        public double LnAbsA => LnAbs(Ar, Ai, LogA);
        public double LnAbsB => LnAbs(Br, Bi, LogB);
        public double LnAbsD => LnAbs(Dr, Di, LogD);
        private static double LnAbs(double re, double im, double lg) => Math.Log(Hypot(re, im)) + lg;

        /// <summary>ln σ₁, ln σ₂ of the complex 2×2 J = [[a, 0], [b, d]] — all in
        /// logs: σ₁² = (T + √(T² − 4|ad|²))/2 with T = |a|² + |b|² + |d|² taken in
        /// units of the largest entry, σ₂ = |a||d|/σ₁.</summary>
        public (double LnSigma1, double LnSigma2) LnSingularValues()
        {
            double la = LnAbsA, lb = LnAbsB, ld = LnAbsD;
            double m = Math.Max(la, Math.Max(lb, ld));
            if (double.IsNegativeInfinity(m)) return (double.NegativeInfinity, double.NegativeInfinity);
            double an = Math.Exp(la - m), bn = Math.Exp(lb - m), dn = Math.Exp(ld - m);   // ≤ 1; tiny ones → 0 harmlessly
            double t = an * an + bn * bn + dn * dn;
            double det = an * dn;
            double disc = Math.Max(0.0, t * t - 4.0 * det * det);
            double ln1 = 0.5 * Math.Log(0.5 * (t + Math.Sqrt(disc))) + m;
            double ln2 = double.IsNegativeInfinity(la) || double.IsNegativeInfinity(ld) ? double.NegativeInfinity : la + ld - ln1;
            return (ln1, ln2);
        }
    }

    /// <summary>#1128 — run the z-orbit (seed 0) and c-orbit (seed c₀) in lockstep
    /// under u → u² + s with the Jacobian recurrences, until either escapes.</summary>
    public static PairJacobian RunJacobian(double c0x, double c0y, double sx, double sy, int maxIter, double bailout)
        => RunJacobian(c0x, c0y, sx, sy, maxIter, new Bailout(bailout));

    // One scaled entry: value = (re, im)·e^lg; inv = e^−lg (the scaled "+1").
    private struct ScaledEntry
    {
        public double Re, Im, Lg, Inv;
        public void Renormalise()
        {
            double m = Math.Abs(Re) + Math.Abs(Im);
            if (m > JacRescale) { Re /= JacRescale; Im /= JacRescale; Inv /= JacRescale; Lg += LnJacRescale; }
            else if (m < 1.0 / JacRescale && m > 0) { Re *= JacRescale; Im *= JacRescale; Inv *= JacRescale; Lg -= LnJacRescale; }
        }
    }

    private static PairJacobian RunJacobian(double c0x, double c0y, double sx, double sy, int maxIter, in Bailout b)
    {
        double zx = 0, zy = 0, cx = c0x, cy = c0y;
        var a = new ScaledEntry { Inv = 1 };
        var bb = new ScaledEntry { Inv = 1 };
        var d = new ScaledEntry { Re = 1, Inv = 1 };
        int n = 0;
        for (; n < maxIter; n++)
        {
            if (zx * zx + zy * zy > b.R2 || cx * cx + cy * cy > b.R2) break;
            // a' = 2z·a + 1, b' = 2c·b + 1, d' = 2c·d (scaled: the +1 becomes +e^−Lg).
            double nar = 2 * (zx * a.Re - zy * a.Im) + a.Inv, nai = 2 * (zx * a.Im + zy * a.Re);
            double nbr = 2 * (cx * bb.Re - cy * bb.Im) + bb.Inv, nbi = 2 * (cx * bb.Im + cy * bb.Re);
            double ndr = 2 * (cx * d.Re - cy * d.Im), ndi = 2 * (cx * d.Im + cy * d.Re);
            a.Re = nar; a.Im = nai; bb.Re = nbr; bb.Im = nbi; d.Re = ndr; d.Im = ndi;
            a.Renormalise(); bb.Renormalise(); d.Renormalise();
            double nzx = zx * zx - zy * zy + sx; zy = 2 * zx * zy + sy; zx = nzx;
            double ncx = cx * cx - cy * cy + sx; cy = 2 * cx * cy + sy; cx = ncx;
        }
        return new PairJacobian(a.Re, a.Im, a.Lg, bb.Re, bb.Im, bb.Lg, d.Re, d.Im, d.Lg, n, cx - zx, cy - zy);
    }

    // Palette scalar for the Jacobian fields, [LiveFloor, maxIter].
    private static double JacobianScalar(DualOrbitField field, in PairJacobian j, double ftleSpan, double anisoScale, int maxIter)
    {
        var (l1, l2) = j.LnSingularValues();
        double v;
        if (field == DualOrbitField.Ftle)
        {
            double ftle = j.N > 0 && !double.IsInfinity(l1) ? l1 / j.N : 0.0;
            v = 0.5 + 0.5 * Math.Clamp(ftle / ftleSpan, -1.0, 1.0);
        }
        else
        {
            double a = double.IsNegativeInfinity(l2) ? double.PositiveInfinity : Math.Max(0.0, l1 - l2);
            v = double.IsPositiveInfinity(a) ? 1.0 : a / (a + anisoScale);
        }
        return Math.Clamp(v * maxIter, LiveFloor, maxIter);
    }
}
