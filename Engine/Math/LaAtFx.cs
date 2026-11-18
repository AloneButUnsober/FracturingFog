// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown
// SPDX-FileCopyrightText: Yu, Zhuoran (GitHub 5E-324), Imagina <https://github.com/5E-324/Imagina>, AGPL-3.0

// Math/LaAtFx.cs
//
// #1886 (epic #1428, C5g) — the approximation transform (LaAt.cs, #1627) over the
// float-exp LA table (LaStageTableFx, C5c), for the float-exp pixel loop past double.
// DERIVED CODE, as LaAt.cs: Create / Usable / Select are LaAt's (a C# translation of
// Imagina's LAInfo::CreateAT, ATInfo::Usable and CreateATFromLA; Imagina by GitHub user
// 5E-324, profile name "Yu, Zhuoran", https://github.com/5E-324/Imagina,
// HInfLAEvaluator.{h,cpp} at commit cd046377, AGPL-3.0; see THIRD-PARTY-NOTICES.md §3 and
// Docs/Resources-Bibliography.md#imagina) with A, B, C = A·B, 1/A and the δc bound in
// FloatExp.
//
// Why it pays past double: the AT iterates z' ← z'² + c' in renormalised coordinates,
// z' = A·z, c' = RefC + C·δc. z' stays below Esc (≤ 2^128) and c' is O(the view's c'
// extent), so the iteration runs in plain double at any depth; only the conversion in
// (C·δc) and back (z = z'/A, dz/dc = (dz'/dc')·C/A) needs FloatExp. RefC and Esc² must
// therefore be finite doubles; where they are not, the AT is not usable.

using System;

namespace FracturingFog.FFMath
{
    /// <summary>#1886 — <see cref="LaAt"/> from a <see cref="LaStageTableFx"/>.</summary>
    public readonly struct LaAtFx
    {
        public readonly int Stage, Len;
        /// <summary>The δc bound (Chebyshev).</summary>
        public readonly FloatExp ThrC;
        /// <summary>The renormalised escape radius squared (a double: ≤ 2^256).</summary>
        public readonly double Esc2;
        /// <summary>c' = RefC + C·δc (RefC a double); z = InvZ·z' converts back.</summary>
        public readonly double RefCRe, RefCIm;
        public readonly CFx C, InvZ;
        public readonly double Loosen;

        private const double Cap = 1.157920892373162e77;   // 2^256, Imagina's range cap (as LaAt)
        private static readonly FloatExp CapFx = FloatExp.FromDouble(Cap);

        private LaAtFx(int stage, int len, FloatExp thrC, double esc2, double refCRe, double refCIm, CFx c, CFx invZ, double loosen)
        {
            Stage = stage; Len = len; ThrC = thrC; Esc2 = esc2;
            RefCRe = refCRe; RefCIm = refCIm; C = c; InvZ = invZ; Loosen = loosen;
        }

        /// <summary>The AT from stage <paramref name="stage"/>'s first step (LaAt.Create).</summary>
        public static LaAtFx Create(LaStageTableFx t, int stage, double loosen = 1.0)
        {
            int s0 = t.StageStart[stage];
            ref readonly var a = ref t.Data[s0];
            ref readonly var next = ref t.Data[s0 + 1];   // the next anchor (or the sentinel at Z_R)
            CFx A = a.A, B = a.B;
            CFx c = A * B;
            var lo = FloatExp.FromDouble(loosen);
            var esc = FloatExp.Norm2(A.Re, A.Im) * a.Thr * lo;
            double esc2 = FloatExp.AbsLess(esc, CapFx) ? esc.ToDouble() : Cap;
            var capC = CapFx / c.Cheb();
            var thrC = FloatExp.Min(a.ThrC * lo, capC);
            var refC = CFx.Of(next.RefRe, next.RefIm) * A;
            return new LaAtFx(stage, a.Len, thrC, esc2, refC.Re.ToDouble(), refC.Im.ToDouble(), c, A.Reciprocal(), loosen);
        }

        /// <summary>LaAt.Usable: the renormalised view (radius·|C|) is not negligible next
        /// to c''s offset |RefC| (with 2^offsetBits of margin), the escape radius exceeds
        /// 2, and every number is finite (RefC a finite double; C and 1/A finite, 1/A ≠ 0).</summary>
        public bool Usable(FloatExp radius, int offsetBits = 0)
        {
            if (!(double.IsFinite(RefCRe) && double.IsFinite(RefCIm) && C.IsFinite && InvZ.IsFinite && !InvZ.IsZero)) return false;
            if (!(Esc2 > 4.0)) return false;
            var nC = FloatExp.Norm2(C.Re, C.Im);
            var view = radius * radius * nC * FloatExp.FromDouble(1.0).ScaleB(2 * Math.Clamp(offsetBits, 0, 64));
            var nR = FloatExp.FromDouble(RefCRe * RefCRe + RefCIm * RefCIm);
            return FloatExp.AbsLess(nR, view);
        }

        /// <summary>LaAt.Select: the highest stage whose AT is usable for a view of
        /// half-diagonal <paramref name="radius"/> (in δc), or null.</summary>
        public static LaAtFx? Select(LaStageTableFx t, FloatExp radius, double loosen = 1.0, int offsetBits = 0)
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
