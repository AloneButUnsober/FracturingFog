// Math/Bla.cs
//
// Bilinear Approximation (BLA) — Zhuoran's perturbation acceleration
// (https://fractalforums.org/fractal-mathematics-and-new-theories/28/another-solution-to-perturbation-glitches/4360).
//
// PT recurrence:  δ_{n+1} = (2·Z_n + δ_n)·δ_n + dc.
// When |δ| ≪ |Z_n|, the quadratic term δ² is negligible relative to 2·Z·δ,
// and the recurrence linearises:  δ_{n+1} ≈ 2·Z_n·δ_n + dc.
//
// Stacking l linearised steps gives a single bilinear map
//      δ_{n+l} = A · δ_n + B · dc
// valid while the per-step linearisation error stays below tolerance.
//
// BLAs are built bottom-up: level 0 covers single ref steps (l=1).  Each
// higher level merges two consecutive prior BLAs (l=2, 4, 8 …).  Per-pixel
// iteration walks the table picking the largest valid BLA at the current
// (n, |δ|) and applies it in O(1) — skipping potentially thousands of
// iterations, hiding the cost of reference precision and avoiding the
// glitch fallback to per-pixel high-precision.
//
// Validity radius r is conservative: while |δ|² ≤ r² the quadratic term is
// guaranteed below the precision floor.  Outside r, fall back to a single
// perturbation step.
//
// DD-precision tables (Wave 2.10) — each BLA entry stores A and B as
// double-double (Hi + Lo).  At deep zoom the merged A_n is near 1.0 + tiny
// and double precision in the table itself loses ULPs after thousands of
// accumulation steps (16-level merge ≈ 2¹⁶ refs).  Merge math runs in DD
// using TwoSum/TwoProduct; apply-time broadcast reads ARe / AIm / BRe / BIm
// properties which collapse Hi+Lo back to a single double — one add per
// skip, negligible vs the merge-precision win.  Legacy single-precision
// constructors set Lo=0 → behaviour identical for callers (generated
// calcs) that never supplied Lo bits.

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.FFMath
{
    /// <summary>
    /// Bilinear approximation of l consecutive perturbation iterations:
    ///   δ_{n+l} = A·δ_n + B·dc
    /// valid while |δ_n|² ≤ R².
    ///
    /// Storage is double-double: A and B each carry Hi + Lo limbs so merge
    /// chains retain DD precision.  Apply-time accessors (<see cref="ARe"/>
    /// etc.) return <c>Hi + Lo</c> — callers see plain doubles.
    /// </summary>
    public readonly struct Bla
    {
        public readonly double AReHi, AReLo;
        public readonly double AImHi, AImLo;
        public readonly double BReHi, BReLo;
        public readonly double BImHi, BImLo;
        public readonly double R2;
        public readonly int L;

        /// <summary>Apply-time A.Re collapsed from DD storage.</summary>
        public double ARe { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => AReHi + AReLo; }
        public double AIm { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => AImHi + AImLo; }
        public double BRe { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => BReHi + BReLo; }
        public double BIm { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => BImHi + BImLo; }

        /// <summary>Legacy single-precision constructor — Lo limbs zero.</summary>
        public Bla(double aRe, double aIm, double bRe, double bIm, double r2, int l)
        {
            AReHi = aRe; AReLo = 0.0;
            AImHi = aIm; AImLo = 0.0;
            BReHi = bRe; BReLo = 0.0;
            BImHi = bIm; BImLo = 0.0;
            R2 = r2; L = l;
        }

        /// <summary>DD-precision constructor (Wave 2.10).</summary>
        public Bla(
            double aReHi, double aReLo,
            double aImHi, double aImLo,
            double bReHi, double bReLo,
            double bImHi, double bImLo,
            double r2, int l)
        {
            AReHi = aReHi; AReLo = aReLo;
            AImHi = aImHi; AImLo = aImLo;
            BReHi = bReHi; BReLo = bReLo;
            BImHi = bImHi; BImLo = bImLo;
            R2 = r2; L = l;
        }
    }

    /// <summary>
    /// Hierarchical BLA table built over a reference orbit.
    /// Level k holds BLAs of skip length 2^k anchored at multiples of 2^k.
    /// </summary>
    public sealed class BlaTable
    {
        public readonly int Levels;
        public readonly int RefLen;
        public readonly int[] LevelStart;
        public readonly int[] LevelLen;
        public readonly Bla[] Data;
        /// <summary>True when level-0 + merges built using DD-precision ref orbit (Wave 2.10).</summary>
        public readonly bool DdPrecision;

        // Linearisation tolerance: |δ|·|2Z+δ| dominated by 2Zδ requires |δ| ≤ ε·|Z|.
        // 1e-6 keeps the dropped δ² term ≥ 12 orders of magnitude below 2Zδ —
        // safely below the double-precision floor for the per-pixel δ iteration.
        // (1e-4 was tried but produced visible banding at medium zoom near
        // near-zero |Z| crossings of the reference orbit.)
        private const double Epsilon = 1e-6;

        /// <summary>
        /// Build a BLA hierarchy from a reference orbit (Z values stored as doubles).
        /// Mandelbrot-specific: level-0 hardcoded to A = 2·Z, B = 1.
        /// For other polynomials (z³+c, etc.) use the level-0-array overload.
        /// </summary>
        /// <param name="refZr">Reference orbit real parts.</param>
        /// <param name="refZi">Reference orbit imaginary parts.</param>
        /// <param name="refLen">Number of valid reference iterations (orbit may have terminated by escape).</param>
        /// <param name="dcMaxAbs">Maximum |dc| over all pixels — needed for merge validity.</param>
        public BlaTable(double[] refZr, double[] refZi, int refLen, double dcMaxAbs)
            : this(BuildMandelbrotLevel0(refZr, refZi, refLen), refLen, dcMaxAbs, ddPrecision: false)
        {
        }

        /// <summary>
        /// DD-precision Mandelbrot BLA hierarchy (Wave 2.10).  Level-0 seeds
        /// <c>A = 2·Z</c> from the DD reference orbit (Hi + Lo) — multiply by 2
        /// is exact in floating point, so <c>A_Lo = 2·refZLo</c>.  <c>B = 1</c>
        /// exactly.  Merge math runs in DD throughout.  Validity radius uses
        /// the collapsed <c>Hi + Lo</c> magnitude.
        /// </summary>
        public BlaTable(
            double[] refZr, double[] refZrLo,
            double[] refZi, double[] refZiLo,
            int refLen, double dcMaxAbs)
            : this(BuildMandelbrotLevel0Dd(refZr, refZrLo, refZi, refZiLo, refLen),
                   refLen, dcMaxAbs, ddPrecision: true)
        {
        }

        private static Bla[] BuildMandelbrotLevel0(double[] refZr, double[] refZi, int refLen)
        {
            var level0 = new Bla[refLen];
            for (int n = 0; n < refLen; n++)
            {
                double zr = refZr[n], zi = refZi[n];
                double zMag = System.Math.Sqrt(zr * zr + zi * zi);
                double r = Epsilon * zMag;
                level0[n] = new Bla(2.0 * zr, 2.0 * zi, 1.0, 0.0, r * r, 1);
            }
            return level0;
        }

        private static Bla[] BuildMandelbrotLevel0Dd(
            double[] refZr, double[] refZrLo,
            double[] refZi, double[] refZiLo, int refLen)
        {
            var level0 = new Bla[refLen];
            for (int n = 0; n < refLen; n++)
            {
                double zrHi = refZr[n], zrLo = refZrLo[n];
                double ziHi = refZi[n], ziLo = refZiLo[n];
                // |Z| from collapsed DD — Hi alone is fine for the radius
                // (Lo contributes ~1e-16 relative, far below Epsilon=1e-6).
                double zMag = System.Math.Sqrt(zrHi * zrHi + ziHi * ziHi);
                double r = Epsilon * zMag;
                // A = 2·Z exactly in FP (multiply by 2 = exponent bump).
                level0[n] = new Bla(
                    2.0 * zrHi, 2.0 * zrLo,
                    2.0 * ziHi, 2.0 * ziLo,
                    1.0, 0.0,
                    0.0, 0.0,
                    r * r, 1);
            }
            return level0;
        }

        /// <summary>
        /// Build a BLA hierarchy from pre-computed level-0 BLAs.
        /// Used by CalculatorGen-generated calculators that build per-equation
        /// level-0 (A = ∂p/∂z(Z_n), B = ∂p/∂c(Z_n)) via emitted code at ref-
        /// orbit construction time. The merge logic (levels 1..) is the same.
        /// </summary>
        /// <param name="level0">Per-step BLA at iter n (length ≥ refLen).</param>
        /// <param name="refLen">Number of valid reference iterations.</param>
        /// <param name="dcMaxAbs">Maximum |dc| over all pixels.</param>
        public BlaTable(Bla[] level0, int refLen, double dcMaxAbs)
            : this(level0, refLen, dcMaxAbs, ddPrecision: false)
        {
        }

        private BlaTable(Bla[] level0, int refLen, double dcMaxAbs, bool ddPrecision)
        {
            RefLen = refLen;
            DdPrecision = ddPrecision;
            int maxLevel = 0;
            while ((1 << (maxLevel + 1)) <= refLen) maxLevel++;
            Levels = System.Math.Max(1, maxLevel + 1);

            LevelStart = new int[Levels];
            LevelLen = new int[Levels];
            int total = 0;
            for (int k = 0; k < Levels; k++)
            {
                LevelStart[k] = total;
                LevelLen[k] = refLen >> k;
                total += LevelLen[k];
            }
            Data = new Bla[total];

            // Level 0 — caller-provided.
            for (int n = 0; n < LevelLen[0]; n++)
                Data[LevelStart[0] + n] = level0[n];

            // Higher levels — merge two consecutive prior-level BLAs.
            //   A_m = A2 · A1
            //   B_m = A2 · B1 + B2
            //   r_m = min(r1, max(0, r2 − |B1|·|dc_max|) / |A1|)
            // The r2 condition asks: "after applying BLA1, will δ stay within BLA2's
            // radius even with the dc contribution?" — answers conservatively.
            //
            // When ddPrecision=true the complex multiplies / adds run in
            // double-double via Two{Sum,Product}; otherwise they run in
            // single-precision (Lo limbs stay 0, identical to pre-2.10 path).
            for (int k = 1; k < Levels; k++)
            {
                int l = 1 << k;
                int prevStart = LevelStart[k - 1];
                int curStart = LevelStart[k];
                int len = LevelLen[k];
                for (int n = 0; n < len; n++)
                {
                    var b1 = Data[prevStart + 2 * n];
                    var b2 = Data[prevStart + 2 * n + 1];

                    Bla merged;
                    if (ddPrecision)
                        merged = MergeDd(b1, b2, dcMaxAbs, l);
                    else
                        merged = MergeDouble(b1, b2, dcMaxAbs, l);

                    Data[curStart + n] = merged;
                }
            }
        }

        // ── Single-precision merge (legacy path, Lo limbs assumed 0) ────────
        private static Bla MergeDouble(in Bla b1, in Bla b2, double dcMaxAbs, int l)
        {
            double b1ARe = b1.AReHi, b1AIm = b1.AImHi;
            double b1BRe = b1.BReHi, b1BIm = b1.BImHi;
            double b2ARe = b2.AReHi, b2AIm = b2.AImHi;
            double b2BRe = b2.BReHi, b2BIm = b2.BImHi;

            // A2·A1
            double aRe = b2ARe * b1ARe - b2AIm * b1AIm;
            double aIm = b2ARe * b1AIm + b2AIm * b1ARe;

            // A2·B1 + B2
            double bRe = b2ARe * b1BRe - b2AIm * b1BIm + b2BRe;
            double bIm = b2ARe * b1BIm + b2AIm * b1BRe + b2BIm;

            double r1 = System.Math.Sqrt(b1.R2);
            double r2 = System.Math.Sqrt(b2.R2);
            double a1Mag = System.Math.Sqrt(b1ARe * b1ARe + b1AIm * b1AIm);
            double b1Mag = System.Math.Sqrt(b1BRe * b1BRe + b1BIm * b1BIm);
            double rMerged = System.Math.Min(r1,
                System.Math.Max(0.0, r2 - b1Mag * dcMaxAbs)
                / System.Math.Max(a1Mag, 1e-300));

            return new Bla(aRe, aIm, bRe, bIm, rMerged * rMerged, l);
        }

        // ── DD-precision merge primitives ──────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double s, double e) TwoSum(double a, double b)
        {
            double s = a + b;
            double v = s - a;
            double e = (a - (s - v)) + (b - v);
            return (s, e);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double s, double e) QuickTwoSum(double a, double b)
        {
            double s = a + b;
            double e = b - (s - a);
            return (s, e);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double p, double e) TwoProduct(double a, double b)
        {
            double p = a * b;
            double e = System.Math.FusedMultiplyAdd(a, b, -p);
            return (p, e);
        }

        // DD + DD (sloppy Knuth-Priest variant — 6 flops, sufficient for the
        // merge accumulation; see Abstractions/Math/DoubleDouble.cs for the
        // canonical reference impl).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double hi, double lo) DdAdd(double aHi, double aLo, double bHi, double bLo)
        {
            var (s1, s2) = TwoSum(aHi, bHi);
            var (t1, t2) = TwoSum(aLo, bLo);
            s2 += t1;
            var (r1, r2) = QuickTwoSum(s1, s2);
            r2 += t2;
            var (p1, p2) = QuickTwoSum(r1, r2);
            return (p1, p2);
        }

        // DD - DD
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double hi, double lo) DdSub(double aHi, double aLo, double bHi, double bLo)
            => DdAdd(aHi, aLo, -bHi, -bLo);

        // DD × DD
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double hi, double lo) DdMul(double aHi, double aLo, double bHi, double bLo)
        {
            var (p1, p2) = TwoProduct(aHi, bHi);
            p2 += aHi * bLo + aLo * bHi;
            var (r1, r2) = QuickTwoSum(p1, p2);
            return (r1, r2);
        }

        // Complex DD × DD:  (aRe + i·aIm) · (bRe + i·bIm)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double reHi, double reLo, double imHi, double imLo) DdComplexMul(
            double aReHi, double aReLo, double aImHi, double aImLo,
            double bReHi, double bReLo, double bImHi, double bImLo)
        {
            var (rrHi, rrLo) = DdMul(aReHi, aReLo, bReHi, bReLo);   // aRe·bRe
            var (iiHi, iiLo) = DdMul(aImHi, aImLo, bImHi, bImLo);   // aIm·bIm
            var (riHi, riLo) = DdMul(aReHi, aReLo, bImHi, bImLo);   // aRe·bIm
            var (irHi, irLo) = DdMul(aImHi, aImLo, bReHi, bReLo);   // aIm·bRe
            var (reHi, reLo) = DdSub(rrHi, rrLo, iiHi, iiLo);
            var (imHi, imLo) = DdAdd(riHi, riLo, irHi, irLo);
            return (reHi, reLo, imHi, imLo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double DdToDouble(double hi, double lo) => hi + lo;

        private static Bla MergeDd(in Bla b1, in Bla b2, double dcMaxAbs, int l)
        {
            // A_m = A2 · A1
            var (aReHi, aReLo, aImHi, aImLo) = DdComplexMul(
                b2.AReHi, b2.AReLo, b2.AImHi, b2.AImLo,
                b1.AReHi, b1.AReLo, b1.AImHi, b1.AImLo);

            // tmp = A2 · B1
            var (tReHi, tReLo, tImHi, tImLo) = DdComplexMul(
                b2.AReHi, b2.AReLo, b2.AImHi, b2.AImLo,
                b1.BReHi, b1.BReLo, b1.BImHi, b1.BImLo);

            // B_m = tmp + B2
            var (bReHi, bReLo) = DdAdd(tReHi, tReLo, b2.BReHi, b2.BReLo);
            var (bImHi, bImLo) = DdAdd(tImHi, tImLo, b2.BImHi, b2.BImLo);

            // Validity radius — magnitudes from collapsed DD (precision of
            // r itself is not critical, only the merge result is).
            double r1 = System.Math.Sqrt(b1.R2);
            double r2 = System.Math.Sqrt(b2.R2);
            double b1Re = DdToDouble(b1.BReHi, b1.BReLo);
            double b1Im = DdToDouble(b1.BImHi, b1.BImLo);
            double a1Re = DdToDouble(b1.AReHi, b1.AReLo);
            double a1Im = DdToDouble(b1.AImHi, b1.AImLo);
            double a1Mag = System.Math.Sqrt(a1Re * a1Re + a1Im * a1Im);
            double b1Mag = System.Math.Sqrt(b1Re * b1Re + b1Im * b1Im);
            double rMerged = System.Math.Min(r1,
                System.Math.Max(0.0, r2 - b1Mag * dcMaxAbs)
                / System.Math.Max(a1Mag, 1e-300));

            return new Bla(aReHi, aReLo, aImHi, aImLo,
                           bReHi, bReLo, bImHi, bImLo,
                           rMerged * rMerged, l);
        }

        /// <summary>
        /// Find the longest BLA at iteration n applicable to current |δ|².
        /// Returns -1 if none usable (caller does a single perturbation step).
        ///
        /// Validity check is strict (dMag2 &lt; R2) and requires R2 &gt; 0. A merged
        /// BLA whose validity radius collapsed to zero — happens at iter=0
        /// (Z_0 = 0 makes level-0 R² = 0, cascading through merges) and at
        /// reference-orbit zero-crossings where |Z| momentarily approaches the
        /// precision floor — must NOT be applied. The previous <c>dMag2 &lt;= R2</c>
        /// allowed δ=0 to trivially satisfy R²=0, producing wildly wrong
        /// skips: at iter=0 with no SA prelude, the largest-level merged BLA
        /// (whose R cascaded to 0 through the n=0 chain) would be applied,
        /// producing an unphysical δ = B·dc at iter L. Result was an almost
        /// uniformly-coloured image since every pixel jumped to roughly the
        /// same wrong iteration count.
        /// </summary>
        public int Lookup(int n, double dMag2, int maxIter)
        {
            // Walk levels top-down — first valid match wins.
            for (int k = Levels - 1; k >= 0; k--)
            {
                int l = 1 << k;
                if ((n & (l - 1)) != 0) continue;     // n must be aligned to l
                int idx = n >> k;
                if (idx >= LevelLen[k]) continue;
                if (n + l > RefLen) continue;
                if (n + l > maxIter) continue;
                ref readonly var b = ref Data[LevelStart[k] + idx];
                if (b.R2 > 0.0 && dMag2 < b.R2) return LevelStart[k] + idx;
            }
            return -1;
        }
    }
}
