// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown
// SPDX-FileCopyrightText: Yu, Zhuoran (GitHub 5E-324), Imagina <https://github.com/5E-324/Imagina>, AGPL-3.0

// Math/LaAt.cs
//
// #1627 (epic #1623, S4) — the approximation transform (AT) over an LA stage table.
// DERIVED CODE: Create / Usable / Select are a C# translation of Imagina's
// LAInfo::CreateAT, ATInfo::Usable and CreateATFromLA (Imagina by GitHub user
// 5E-324, profile name "Yu, Zhuoran", https://github.com/5E-324/Imagina,
// HInfLAEvaluator.{h,cpp} at commit cd046377, read 2026-11-03, AGPL-3.0; see
// THIRD-PARTY-NOTICES.md §3 and Docs/Resources-Bibliography.md#imagina). It came here
// from the S1 probe (Probes/LaProbe.cs, BuildAt). The maths is in
// Docs/Technical/Period-Synchronised-LA-DesignPlan.md §2.5.
//
// The first step of a stage, anchored at Z_0 = 0 with length L and coefficients
// (A, B), maps z_0 = δ_0 to z_L = Z_L + A·z_0² + B·δc. With z' = A·z this is
//      z'_L = z'_0² + c',   c' = A·Z_L + (A·B)·δc,
// the Mandelbrot map in renormalised coordinates: one step per L iterations, valid
// while |z'|² < Esc² = |A|²·Thr and ‖δc‖ < ThrC.
//
// FF departures (design doc §2.6 items 10–11):
//   • Usable needs the view's c' extent to be at least 2^-offsetBits of c''s offset.
//     c' = RefC + C·δc is rounded in double, so a view much smaller than |RefC| feeds
//     the AT a coarser c' than perturbation's exact δc; FF's default is offsetBits = 0
//     (the view no smaller than the offset). Imagina's test is offsetBits = 16
//     (radius²·|C|²·2³² > |RefC|²), which on p15,248 measurably loses accuracy against
//     exact iteration (§4.3).
//   • Select takes a looseness factor s ≥ 1 that scales the AT's validity bounds Thr
//     and ThrC by s, i.e. the AT alone runs at tolerance s·τ (every threshold of the
//     table is proportional to τ). Measured and rejected (§4.3); s = 1 is Imagina's AT.

using System;
using System.Numerics;

namespace FracturingFog.FFMath
{
    /// <summary>#1627 — the approximation transform from one stage's first step
    /// (design doc §2.5).</summary>
    public readonly struct LaAt
    {
        /// <summary>The stage the AT comes from, and its step length L.</summary>
        public readonly int Stage, Len;
        /// <summary>The δc bound (Chebyshev) and the renormalised escape radius squared.</summary>
        public readonly double ThrC, Esc2;
        /// <summary>c' = RefC + C·δc; z = InvZ·z' converts back.</summary>
        public readonly double RefCRe, RefCIm, CRe, CIm, InvZRe, InvZIm;
        /// <summary>The looseness factor the bounds were scaled by (1 = Imagina's).</summary>
        public readonly double Loosen;

        private const double Cap = 1.157920892373162e77;   // 2^256, Imagina's range cap

        private LaAt(int stage, int len, double thrC, double esc2, Complex refC, Complex c, Complex invZ, double loosen)
        {
            Stage = stage; Len = len; ThrC = thrC; Esc2 = esc2;
            RefCRe = refC.Real; RefCIm = refC.Imaginary; CRe = c.Real; CIm = c.Imaginary;
            InvZRe = invZ.Real; InvZIm = invZ.Imaginary; Loosen = loosen;
        }

        private static double Cheb(Complex z) => Math.Max(Math.Abs(z.Real), Math.Abs(z.Imaginary));
        private static double Norm(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;

        /// <summary>The AT from stage <paramref name="stage"/>'s first step (LAInfo::CreateAT).</summary>
        public static LaAt Create(LaStageTable t, int stage, double loosen = 1.0)
        {
            ref readonly var a = ref t.Step(stage, 0);
            ref readonly var next = ref t.Step(stage, 1);   // the next anchor (or the sentinel at Z_R)
            Complex A = a.A, B = a.B;
            Complex c = A * B;
            double esc2 = Math.Min(Norm(A) * a.Thr * loosen, Cap);
            double thrC = Math.Min(a.ThrC * loosen, Cap / Cheb(c));
            return new LaAt(stage, a.Len, thrC, esc2, next.Ref * A, c, 1.0 / A, loosen);
        }

        /// <summary>Imagina's margin in <see cref="Usable"/>: the view may be 2⁻¹⁶ of c''s offset.</summary>
        public const int ImaginaOffsetBits = 16;

        /// <summary>The view's c' extent over c''s offset, radius·|C| / |RefC| (diagnostic).</summary>
        public double ViewRatio(double radius) =>
            radius * Math.Sqrt(CRe * CRe + CIm * CIm) / Math.Sqrt(RefCRe * RefCRe + RefCIm * RefCIm);

        /// <summary>ATInfo::Usable: the renormalised view is not negligible next to c''s
        /// offset (radius²·|C|²·2^(2·offsetBits) &gt; |RefC|²; Imagina's offsetBits is 16, FF's
        /// default 0) and the escape radius exceeds 2. FF: the coefficients must also be
        /// finite.</summary>
        public bool Usable(double radius, int offsetBits = 0)
        {
            double nC = CRe * CRe + CIm * CIm, nR = RefCRe * RefCRe + RefCIm * RefCIm;
            double nI = InvZRe * InvZRe + InvZIm * InvZIm;
            double margin = Math.Pow(2.0, 2 * Math.Clamp(offsetBits, 0, 64));
            return radius * radius * nC * margin > nR && Esc2 > 4.0
                   && double.IsFinite(nC) && double.IsFinite(nR) && double.IsFinite(nI) && nI > 0;
        }

        /// <summary>CreateATFromLA: the highest stage whose AT is usable for a view of
        /// half-diagonal <paramref name="radius"/> (in δc), or null.</summary>
        public static LaAt? Select(LaStageTable t, double radius, double loosen = 1.0, int offsetBits = 0)
        {
            if (!(loosen >= 1.0) || !double.IsFinite(loosen)) loosen = 1.0;
            for (int s = t.Stages - 1; s >= 0; s--)
            {
                if (t.StageCount[s] < 1) continue;
                var at = Create(t, s, loosen);
                if (at.Usable(radius, offsetBits)) return at;
            }
            return null;
        }
    }
}
