// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Math/BlaFx.cs
//
// #1845 (epic #1428, C5b) — the bilinear approximation table (Bla.cs: Zhuoran's BLA, see
// its header and Docs/Resources-Bibliography.md) with A, B and the validity radius in
// FloatExp, for the floatexp pixel loop past double's range. There the double table can't
// hold its own numbers: B (the dc response, ∏2Z over the skip) overflows, dcMax underflows,
// and the radii (≈ ε·|Z| shrunk by |B|·dcMax / |A| at each merge) underflow.
//
// Same level-0 entries, merge rule and lookup as BlaTable (single-precision form):
//   level 0:  A = 2·Z_n, B = 1, r = ε·|Z_n|
//   merge:    A = A₂·A₁, B = A₂·B₁ + B₂, r = min(r₁, max(0, r₂ − |B₁|·dcMax) / |A₁|)
//   lookup:   the longest level aligned at n with R² > 0 and |δ|² < R².

using System;

namespace FracturingFog.FFMath
{
    /// <summary>#1845 — δ_{n+L} = A·δ_n + B·dc, valid while |δ_n|² &lt; R2, in FloatExp.</summary>
    public readonly struct BlaFx
    {
        public readonly FloatExp ARe, AIm, BRe, BIm, R, R2;
        public readonly int L;

        public BlaFx(FloatExp aRe, FloatExp aIm, FloatExp bRe, FloatExp bIm, FloatExp r, int l)
        {
            ARe = aRe; AIm = aIm; BRe = bRe; BIm = bIm; R = r; R2 = r * r; L = l;
        }
    }

    /// <summary>#1845 — <see cref="BlaTable"/> in FloatExp.</summary>
    public sealed class BlaTableFx
    {
        public readonly int Levels;
        public readonly int RefLen;
        public readonly int[] LevelStart;
        public readonly int[] LevelLen;
        public readonly BlaFx[] Data;
        public readonly double Epsilon;
        public readonly FloatExp DcMaxAbs;

        public BlaTableFx(double[] refZr, double[] refZi, int refLen, FloatExp dcMaxAbs, double epsilon = BlaTable.DefaultEpsilon)
        {
            RefLen = refLen;
            Epsilon = epsilon;
            DcMaxAbs = dcMaxAbs;
            int maxLevel = 0;
            while ((1 << (maxLevel + 1)) <= refLen) maxLevel++;
            Levels = Math.Max(1, maxLevel + 1);
            LevelStart = new int[Levels];
            LevelLen = new int[Levels];
            int total = 0;
            for (int k = 0; k < Levels; k++)
            {
                LevelStart[k] = total;
                LevelLen[k] = refLen >> k;
                total += LevelLen[k];
            }
            Data = new BlaFx[total];

            var eps = FloatExp.FromDouble(epsilon);
            for (int n = 0; n < LevelLen[0]; n++)
            {
                var zr = FloatExp.FromDouble(refZr[n]);
                var zi = FloatExp.FromDouble(refZi[n]);
                Data[n] = new BlaFx(zr.ScaleB(1), zi.ScaleB(1), FloatExp.One, FloatExp.Zero,
                                    eps * FloatExp.Norm2(zr, zi).Sqrt(), 1);
            }
            for (int k = 1; k < Levels; k++)
            {
                int prev = LevelStart[k - 1], cur = LevelStart[k], l = 1 << k;
                for (int n = 0; n < LevelLen[k]; n++)
                    Data[cur + n] = Merge(Data[prev + 2 * n], Data[prev + 2 * n + 1], dcMaxAbs, l);
            }
        }

        private static BlaFx Merge(in BlaFx b1, in BlaFx b2, FloatExp dcMax, int l)
        {
            var aRe = b2.ARe * b1.ARe - b2.AIm * b1.AIm;
            var aIm = b2.ARe * b1.AIm + b2.AIm * b1.ARe;
            var bRe = b2.ARe * b1.BRe - b2.AIm * b1.BIm + b2.BRe;
            var bIm = b2.ARe * b1.BIm + b2.AIm * b1.BRe + b2.BIm;
            var a1 = FloatExp.Norm2(b1.ARe, b1.AIm).Sqrt();
            var b1m = FloatExp.Norm2(b1.BRe, b1.BIm).Sqrt();
            var rest = b2.R - b1m * dcMax;
            if (rest.M < 0) rest = FloatExp.Zero;
            // As the double merge's max(|A₁|, 1e-300): A₁ = 0 leaves r₁ while anything remains.
            var r = a1.IsZero ? (rest.IsZero ? FloatExp.Zero : b1.R) : FloatExp.Min(b1.R, rest / a1);
            return new BlaFx(aRe, aIm, bRe, bIm, r, l);
        }

        /// <summary>The longest BLA at reference index <paramref name="n"/> valid for
        /// |δ|² = <paramref name="dMag2"/> that ends by <paramref name="maxIter"/>; −1 if none
        /// (as <see cref="BlaTable.Lookup"/>).</summary>
        public int Lookup(int n, FloatExp dMag2, int maxIter)
        {
            int top = n == 0 ? Levels - 1 : Math.Min(Levels - 1, System.Numerics.BitOperations.TrailingZeroCount(n));
            for (int k = top; k >= 0; k--)
            {
                int l = 1 << k;
                int idx = n >> k;
                if (idx >= LevelLen[k]) continue;
                if (n + l > RefLen) continue;
                if (n + l > maxIter) continue;
                ref readonly var b = ref Data[LevelStart[k] + idx];
                if (!b.R2.IsZero && FloatExp.AbsLess(dMag2, b.R2)) return LevelStart[k] + idx;
            }
            return -1;
        }
    }
}
