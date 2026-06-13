// Math/SeriesApproximation.cs
//
// Series Approximation (SA) — third-order polynomial expansion of the
// perturbation delta around a reference orbit. Classical Kalles Fraktaler /
// Zhuoran formulation.
//
// PT recurrence:  δ_{n+1} = (2·Z_n + δ_n)·δ_n + dc.
//
// Expand δ_n as a power series in the pixel offset dc:
//      δ_n ≈ A_n·dc + B_n·dc² + C_n·dc³
//
// Substituting and equating powers of dc gives the coefficient recurrences
// (each coefficient is complex):
//      A_{n+1} = 2·Z_n·A_n + 1            (linear term)
//      B_{n+1} = 2·Z_n·B_n + A_n²         (quadratic term)
//      C_{n+1} = 2·Z_n·C_n + 2·A_n·B_n    (cubic term)
//
// Per-pixel skip-iter k is the largest n where the truncated 4th-order term
// is bounded by tolerance · |B_n·dc²|, equivalently |C_n|·|dc| ≤ τ·|B_n|.
//
// Where BLA helps in the middle of the perturbation loop (|δ| small but Z
// large), SA jump-starts the loop by skipping the first hundreds-to-thousands
// of iterations from z=0 outright. Combined with BLA: SA covers the prelude,
// BLA covers the middle.

using System;

namespace FracturingFog.FFMath
{
    /// <summary>
    /// Third-order series approximation of perturbation δ around a reference
    /// orbit. Coefficients indexed 0…RefLen — A_n,B_n,C_n at index n describe
    /// δ_n as a polynomial in pixel offset dc.
    /// </summary>
    public sealed class SeriesApproximation
    {
        public readonly int RefLen;
        public readonly double[] AR, AI;   // A_n  (complex linear coeff)
        public readonly double[] BR, BI;   // B_n  (complex quadratic coeff)
        public readonly double[] CR, CI;   // C_n  (complex cubic coeff)
        // D_n — 4th-order coefficient. Not used to extend the polynomial
        // (still truncated at 3rd order to keep EvalDelta cheap), but used in
        // FindSkip to bound the truncation error of dropping the 4th and
        // higher terms. Recurrence: D_{n+1} = 2·Z·D + 2·A·C + B².
        public readonly double[] DR, DI;

        /// <summary>Largest n where all coefficients stay finite (no overflow).</summary>
        public readonly int SafeMax;

        // Coefficient overflow threshold. Above this the polynomial loses
        // numerical meaning; SA must stop and let the regular perturbation
        // loop take over.
        private const double OverflowThreshold = 1e100;

        public SeriesApproximation(double[] refZr, double[] refZi, int refLen)
        {
            RefLen = refLen;
            int n1 = refLen + 1;
            AR = new double[n1]; AI = new double[n1];
            BR = new double[n1]; BI = new double[n1];
            CR = new double[n1]; CI = new double[n1];
            DR = new double[n1]; DI = new double[n1];

            AR[0] = 1.0;       // A_0 = 1+0i
            // B_0 = C_0 = D_0 = 0 (default-initialised)

            int safe = 0;
            const double ovT2 = OverflowThreshold * OverflowThreshold;

            for (int n = 0; n < refLen; n++)
            {
                double Zr = refZr[n], Zi = refZi[n];
                double Ar = AR[n], Ai = AI[n];
                double Br = BR[n], Bi = BI[n];
                double Cr = CR[n], Ci = CI[n];
                double Dr = DR[n], Di = DI[n];

                // A_{n+1} = 2·Z·A + 1
                double twoZAr = 2.0 * (Zr * Ar - Zi * Ai);
                double twoZAi = 2.0 * (Zr * Ai + Zi * Ar);
                double nAr = twoZAr + 1.0;
                double nAi = twoZAi;
                AR[n + 1] = nAr;
                AI[n + 1] = nAi;

                // B_{n+1} = 2·Z·B + A²
                double twoZBr = 2.0 * (Zr * Br - Zi * Bi);
                double twoZBi = 2.0 * (Zr * Bi + Zi * Br);
                double A2r = Ar * Ar - Ai * Ai;
                double A2i = 2.0 * Ar * Ai;
                double nBr = twoZBr + A2r;
                double nBi = twoZBi + A2i;
                BR[n + 1] = nBr;
                BI[n + 1] = nBi;

                // C_{n+1} = 2·Z·C + 2·A·B
                double twoZCr = 2.0 * (Zr * Cr - Zi * Ci);
                double twoZCi = 2.0 * (Zr * Ci + Zi * Cr);
                double twoABr = 2.0 * (Ar * Br - Ai * Bi);
                double twoABi = 2.0 * (Ar * Bi + Ai * Br);
                double nCr = twoZCr + twoABr;
                double nCi = twoZCi + twoABi;
                CR[n + 1] = nCr;
                CI[n + 1] = nCi;

                // D_{n+1} = 2·Z·D + 2·A·C + B²
                // Truncation error of the cubic polynomial is dominated by
                // the omitted D·dc⁴ term; tracking D lets FindSkip bound it
                // explicitly.
                double twoZDr = 2.0 * (Zr * Dr - Zi * Di);
                double twoZDi = 2.0 * (Zr * Di + Zi * Dr);
                double twoACr = 2.0 * (Ar * Cr - Ai * Ci);
                double twoACi = 2.0 * (Ar * Ci + Ai * Cr);
                double B2r = Br * Br - Bi * Bi;
                double B2i = 2.0 * Br * Bi;
                double nDr = twoZDr + twoACr + B2r;
                double nDi = twoZDi + twoACi + B2i;
                DR[n + 1] = nDr;
                DI[n + 1] = nDi;

                // Overflow guard — compare squared magnitudes against ovT2.
                // Compute inline to avoid sqrt cost. D included so SafeMax
                // tracks the most volatile coefficient.
                double maxMag2 = nAr * nAr + nAi * nAi;
                double bm2 = nBr * nBr + nBi * nBi;
                if (bm2 > maxMag2) maxMag2 = bm2;
                double cm2 = nCr * nCr + nCi * nCi;
                if (cm2 > maxMag2) maxMag2 = cm2;
                double dm2 = nDr * nDr + nDi * nDi;
                if (dm2 > maxMag2) maxMag2 = dm2;
                if (maxMag2 < ovT2) safe = n + 1;
                else break;
            }
            SafeMax = safe;
        }

        /// <summary>
        /// Largest skip iter k ∈ [0, min(SafeMax, maxAttempt)] where the
        /// truncation error of the 3rd-order series stays bounded.
        ///
        /// The polynomial keeps A·dc + B·dc² + C·dc³ and drops D·dc⁴ and
        /// higher. The dropped tail's magnitude is bounded by the geometric
        /// continuation of D·dc⁴ when the coefficients grow no faster than
        /// the previous level; we enforce that by requiring BOTH
        ///   |C·dc³| ≤ tolerance · |B·dc²|   (3rd term controlled vs 2nd)
        ///   |D·dc⁴| ≤ tolerance · |C·dc³|   (4th term controlled vs 3rd)
        /// which simplifies to |C|·|dc| ≤ tol·|B| AND |D|·|dc| ≤ tol·|C|.
        /// The second test catches the "deep-k overskip" failure mode that
        /// the original single bound missed: at deep iterations D grows
        /// faster than C, the truncation tail dominates the kept terms, and
        /// the SA seed diverges from the true δ even when |C·dc| ≤ tol·|B|
        /// is satisfied.
        ///
        /// Cost: O(log SafeMax) via binary search over precomputed
        /// coefficients.
        /// </summary>
        public int FindSkip(double dcR, double dcI, double tolerance, int maxAttempt)
        {
            int hi = System.Math.Min(SafeMax, maxAttempt);
            if (hi <= 0) return 0;
            double dcMag = System.Math.Sqrt(dcR * dcR + dcI * dcI);
            if (dcMag == 0.0) return hi;     // centre pixel — full skip safe

            int lo = 0, best = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                double Bm = System.Math.Sqrt(BR[mid] * BR[mid] + BI[mid] * BI[mid]);
                double Cm = System.Math.Sqrt(CR[mid] * CR[mid] + CI[mid] * CI[mid]);
                double Dm = System.Math.Sqrt(DR[mid] * DR[mid] + DI[mid] * DI[mid]);
                bool cubicOk = Cm * dcMag <= tolerance * Bm;
                bool quarticOk = Dm * dcMag <= tolerance * Cm;
                if (cubicOk && quarticOk)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return best;
        }

        /// <summary>
        /// Evaluate δ_k(dc) = A_k·dc + B_k·dc² + C_k·dc³.
        /// </summary>
        public void EvalDelta(int k, double dcR, double dcI,
            out double deltaR, out double deltaI)
        {
            double dc2R = dcR * dcR - dcI * dcI;
            double dc2I = 2.0 * dcR * dcI;
            double dc3R = dc2R * dcR - dc2I * dcI;
            double dc3I = dc2R * dcI + dc2I * dcR;

            double aR = AR[k] * dcR - AI[k] * dcI;
            double aI = AR[k] * dcI + AI[k] * dcR;
            double bR = BR[k] * dc2R - BI[k] * dc2I;
            double bI = BR[k] * dc2I + BI[k] * dc2R;
            double cR = CR[k] * dc3R - CI[k] * dc3I;
            double cI = CR[k] * dc3I + CI[k] * dc3R;

            deltaR = aR + bR + cR;
            deltaI = aI + bI + cI;
        }

        /// <summary>
        /// Evaluate dδ_k/dc = A_k + 2·B_k·dc + 3·C_k·dc².
        /// Seeds the per-pixel surface-normal derivative at skip start.
        /// </summary>
        public void EvalDDelta(int k, double dcR, double dcI,
            out double dDeltaR, out double dDeltaI)
        {
            double dc2R = dcR * dcR - dcI * dcI;
            double dc2I = 2.0 * dcR * dcI;

            double twoBR = 2.0 * (BR[k] * dcR - BI[k] * dcI);
            double twoBI = 2.0 * (BR[k] * dcI + BI[k] * dcR);
            double threeCR = 3.0 * (CR[k] * dc2R - CI[k] * dc2I);
            double threeCI = 3.0 * (CR[k] * dc2I + CI[k] * dc2R);

            dDeltaR = AR[k] + twoBR + threeCR;
            dDeltaI = AI[k] + twoBI + threeCI;
        }
    }
}
