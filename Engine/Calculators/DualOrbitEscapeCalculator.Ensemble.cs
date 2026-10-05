// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Ensemble.cs (#1127, epic #1114 S13)
//
// Ensemble fields (Docs/Technical/DualOrbit-Coloring-RnD.md §3.D): the c-orbit
// generalised from one seed to N seeds in a disc of radius ρ round c
// (DualOrbitEnsembleN, DualOrbitEnsembleRadius), all iterated under the pixel's
// u → u² + s. Each seed lands in an outcome class:
//   0      bounded (did not escape within maxIter, or settled on a cycle);
//   1      escaped                         (DualOrbitEnsembleSectors = 0);
//   1 + j  escaped in external-angle sector j of K   (K ≥ 1, level-1 angle by
//          backward lifting — bailout-independent, as ExternalAngleDelta).
//
// BasinEntropy — Shannon entropy S = −Σ p ln p of the class histogram (Daza et
//   al. 2016 "basin entropy" of the box = the ρ-disc), scaled by its bound
//   ln(#classes) → [0, 1]. 0 = one basin; > 0 = the disc straddles a basin
//   boundary. Image aggregates: S_b (mean over pixels), S_bb (mean over boundary
//   boxes, S > 0) and the boundary fraction — Daza's log 2 criterion: S_bb > ln 2
//   is sufficient for a fractal boundary (for 2 classes the bound IS ln 2, so it
//   is never met; use sectors).
//
// UncertaintyExponent — Grebogi–McDonald–Ott–Yorke: pair every seed with a
//   partner at distance ε_k = ρ·2^{−(k+1)} (k < DualOrbitUncertaintyLevels, one
//   fixed direction per seed); f(ε) = fraction of pairs in different classes ~
//   ε^α, α = least-squares slope of ln f vs ln ε over the levels with f > 0
//   (≥ 2 needed, else no value → interior). α = 2 − D_boundary in the plane:
//   1 for a smooth boundary, < 1 for a fractal one. Scaled α/2 → [0, 1].
//
// Seed layout: deterministic sunflower (Vogel) spiral — r = √((j + ½)/N),
// angle = j·golden angle — area-uniform, no RNG, so renders are reproducible
// and tiles agree. Partner directions: the plastic-number sequence (decorrelated
// from the golden-angle layout).
//
// Cost / budget: N·(1 + L) extra orbits per pixel (defaults 32·(1 + 4) = 160,
// ≈ ×80 a plain dual-orbit pixel). Bounded seeds are the expensive ones (they
// run to maxIter), so a Brent cycle check stops them as soon as they settle on a
// cycle — interior seeds in hyperbolic components then cost tens of steps, not
// maxIter. Only the selected field pays: BasinEntropy skips the partners. The
// host has no progressive refinement for this calculator (Mandelbrot only), so
// lower N / L for interactive work and raise them for export.
// Complex map only; the quaternion map gives interior.

using System;
using System.Threading;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    private const double GoldenAngle = 2.399963229728653;          // π(3 − √5)
    private const double PlasticInv = 0.7548776662466927;          // 1/ρ_plastic

    internal static bool IsEnsembleField(DualOrbitField f)
        => f is DualOrbitField.BasinEntropy or DualOrbitField.UncertaintyExponent;

    /// <summary>#1127 — mean BasinEntropy (scaled, [0, 1]) over the last image (S_b).</summary>
    public double BasinEntropyMean { get; private set; } = double.NaN;
    /// <summary>#1127 — mean BasinEntropy over boundary boxes (S &gt; 0) (S_bb).</summary>
    public double BasinEntropyBoundaryMean { get; private set; } = double.NaN;
    /// <summary>#1127 — fraction of pixels whose ρ-disc straddles a basin boundary.</summary>
    public double BasinBoundaryFraction { get; private set; } = double.NaN;

    /// <summary>#1127 — unit-disc offset of ensemble seed <paramref name="j"/> of
    /// <paramref name="n"/> (sunflower spiral; deterministic, area-uniform).</summary>
    public static (double X, double Y) EnsembleOffset(int j, int n)
    {
        double r = Math.Sqrt((j + 0.5) / n), a = j * GoldenAngle;
        return (r * Math.Cos(a), r * Math.Sin(a));
    }

    /// <summary>#1127 — the fixed partner direction (unit vector) of seed j.</summary>
    public static (double X, double Y) EnsemblePartnerDirection(int j)
    {
        double t = (j + 1) * PlasticInv; t -= Math.Floor(t);
        double a = 2.0 * Math.PI * t;
        return (Math.Cos(a), Math.Sin(a));
    }

    /// <summary>#1127 — number of outcome classes for <paramref name="sectors"/>.</summary>
    public static int EnsembleClassCount(int sectors) => sectors <= 0 ? 2 : 1 + sectors;

    // Outcome class of the orbit u0 → u² + s (see header). args: scratch of
    // length ≥ maxIter + 1 (used only when sectors > 0).
    private static int Classify(double u0x, double u0y, double sx, double sy, int maxIter, in Bailout b,
        int sectors, double[] args)
    {
        double x = u0x, y = u0y, px = x, py = y;
        int lam = 0, pow = 1;
        bool track = sectors > 0;
        for (int n = 0; n < maxIter; n++)
        {
            if (track) args[n] = Math.Atan2(y, x);
            if (x * x + y * y > b.R2)
            {
                if (!track) return 1;
                int j = (int)(LiftToLevel1(args, n) * sectors);
                return 1 + Math.Clamp(j, 0, sectors - 1);
            }
            double nx = x * x - y * y + sx;
            y = 2.0 * x * y + sy;
            x = nx;
            // Brent: settled on a cycle → bounded for good.
            if (Math.Abs(x - px) + Math.Abs(y - py) < 1e-13) return 0;
            if (++lam == pow) { px = x; py = y; pow <<= 1; lam = 0; }
        }
        return 0;
    }

    // The ensemble field's scalar in [LiveFloor, maxIter]; live = false where the
    // uncertainty exponent has fewer than two disagreeing levels.
    private static double EnsembleScalar(DualOrbitField field, double cx, double cy, double sx, double sy,
        int maxIter, in Bailout b, int nSeeds, double rho, int sectors, int levels,
        int[] classes, int[] counts, double[] args, out bool live)
    {
        live = true;
        int nc = EnsembleClassCount(sectors);
        for (int j = 0; j < nSeeds; j++)
        {
            var (ox, oy) = EnsembleOffset(j, nSeeds);
            classes[j] = Classify(cx + rho * ox, cy + rho * oy, sx, sy, maxIter, b, sectors, args);
        }
        if (field == DualOrbitField.BasinEntropy)
        {
            Array.Clear(counts, 0, nc);
            for (int j = 0; j < nSeeds; j++) counts[classes[j]]++;
            double s = 0.0;
            for (int k = 0; k < nc; k++)
                if (counts[k] > 0) { double p = (double)counts[k] / nSeeds; s -= p * Math.Log(p); }
            return Math.Clamp(s / Math.Log(nc) * maxIter, LiveFloor, maxIter);
        }

        // Uncertainty exponent: least squares on (ln ε_k, ln f_k), f_k > 0.
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0; int m = 0;
        for (int k = 0; k < levels; k++)
        {
            double eps = rho * Math.Pow(2.0, -(k + 1));
            int bad = 0;
            for (int j = 0; j < nSeeds; j++)
            {
                var (ox, oy) = EnsembleOffset(j, nSeeds);
                var (dx, dy) = EnsemblePartnerDirection(j);
                int cls = Classify(cx + rho * ox + eps * dx, cy + rho * oy + eps * dy, sx, sy, maxIter, b, sectors, args);
                if (cls != classes[j]) bad++;
            }
            if (bad == 0) continue;
            double lx = Math.Log(eps), ly = Math.Log((double)bad / nSeeds);
            sumX += lx; sumY += ly; sumXX += lx * lx; sumXY += lx * ly; m++;
        }
        if (m < 2) { live = false; return 0.0; }
        double alpha = (m * sumXY - sumX * sumY) / (m * sumXX - sumX * sumX);
        return Math.Clamp(0.5 * alpha * maxIter, LiveFloor, maxIter);
    }

    // Image aggregates S_b / S_bb / boundary fraction from the entropy scalars.
    private void EnsembleAggregates(int maxIter)
    {
        double sum = 0, sumB = 0; int nB = 0, n = _scalar.Length;
        double floor = LiveFloor * 1.0000001;
        for (int i = 0; i < n; i++)
        {
            double v = _scalar[i] / maxIter;
            sum += v;
            if (_scalar[i] > floor) { sumB += v; nB++; }
        }
        BasinEntropyMean = n > 0 ? sum / n : double.NaN;
        BasinEntropyBoundaryMean = nB > 0 ? sumB / nB : 0.0;
        BasinBoundaryFraction = n > 0 ? (double)nB / n : double.NaN;
    }
}
