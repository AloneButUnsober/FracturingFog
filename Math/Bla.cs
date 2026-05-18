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

using System;

namespace FracturingFog.FFMath
{
    /// <summary>
    /// Bilinear approximation of l consecutive perturbation iterations:
    ///   δ_{n+l} = A·δ_n + B·dc
    /// valid while |δ_n|² ≤ R².
    /// </summary>
    public readonly struct Bla
    {
        public readonly double ARe, AIm;
        public readonly double BRe, BIm;
        public readonly double R2;
        public readonly int L;

        public Bla(double aRe, double aIm, double bRe, double bIm, double r2, int l)
        {
            ARe = aRe; AIm = aIm;
            BRe = bRe; BIm = bIm;
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

        // Linearisation tolerance: |δ|·|2Z+δ| dominated by 2Zδ requires |δ| ≤ ε·|Z|.
        // 1e-6 keeps the dropped δ² term ≥ 12 orders of magnitude below 2Zδ —
        // safely below the double-precision floor for the per-pixel δ iteration.
        // (1e-4 was tried but produced visible banding at medium zoom near
        // near-zero |Z| crossings of the reference orbit.)
        private const double Epsilon = 1e-6;

        /// <summary>
        /// Build a BLA hierarchy from a reference orbit (Z values stored as doubles).
        /// </summary>
        /// <param name="refZr">Reference orbit real parts.</param>
        /// <param name="refZi">Reference orbit imaginary parts.</param>
        /// <param name="refLen">Number of valid reference iterations (orbit may have terminated by escape).</param>
        /// <param name="dcMaxAbs">Maximum |dc| over all pixels — needed for merge validity.</param>
        public BlaTable(double[] refZr, double[] refZi, int refLen, double dcMaxAbs)
        {
            RefLen = refLen;
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

            // Level 0 — single perturbation step:
            //   δ' = (2·Z + δ)·δ + dc  ≈  2·Z·δ + dc
            //   A = 2·Z, B = 1, validity r = ε·|Z|
            for (int n = 0; n < LevelLen[0]; n++)
            {
                double zr = refZr[n], zi = refZi[n];
                double zMag = System.Math.Sqrt(zr * zr + zi * zi);
                double r = Epsilon * zMag;
                Data[LevelStart[0] + n] = new Bla(
                    2.0 * zr, 2.0 * zi,
                    1.0, 0.0,
                    r * r, 1);
            }

            // Higher levels — merge two consecutive prior-level BLAs.
            //   A_m = A2 · A1
            //   B_m = A2 · B1 + B2
            //   r_m = min(r1, max(0, r2 − |B2|·|dc_max|) / |A1|)
            // The r2 condition asks: "after applying BLA1, will δ stay within BLA2's
            // radius even with the dc contribution?" — answers conservatively.
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

                    // Complex multiply A2·A1
                    double aRe = b2.ARe * b1.ARe - b2.AIm * b1.AIm;
                    double aIm = b2.ARe * b1.AIm + b2.AIm * b1.ARe;

                    // A2·B1 + B2
                    double bRe = b2.ARe * b1.BRe - b2.AIm * b1.BIm + b2.BRe;
                    double bIm = b2.ARe * b1.BIm + b2.AIm * b1.BRe + b2.BIm;

                    double r1 = System.Math.Sqrt(b1.R2);
                    double r2 = System.Math.Sqrt(b2.R2);
                    double a1Mag = System.Math.Sqrt(b1.ARe * b1.ARe + b1.AIm * b1.AIm);
                    // After BLA1 applies: δ' = A1·δ + B1·dc.  Validity for BLA2
                    // requires |δ'| ≤ r2.  Triangle bound: |A1|·|δ| + |B1|·|dc| ≤ r2,
                    // hence |δ| ≤ (r2 − |B1|·|dc|) / |A1|.  Use |B1| (not |B2|).
                    double b1Mag = System.Math.Sqrt(b1.BRe * b1.BRe + b1.BIm * b1.BIm);
                    double rMerged = System.Math.Min(r1,
                        System.Math.Max(0.0, r2 - b1Mag * dcMaxAbs)
                        / System.Math.Max(a1Mag, 1e-300));

                    Data[curStart + n] = new Bla(aRe, aIm, bRe, bIm,
                        rMerged * rMerged, l);
                }
            }
        }

        /// <summary>
        /// Find the longest BLA at iteration n applicable to current |δ|².
        /// Returns -1 if none usable (caller does a single perturbation step).
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
                if (dMag2 <= b.R2) return LevelStart[k] + idx;
            }
            return -1;
        }
    }
}