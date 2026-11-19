// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1909 (epic #1253, D4) — the bilinear approximation for formulas that are not complex-analytic
// (Tricorn, user formulas with conj / re / im). In R² the step's linear part is a real 2×2 matrix
// per input, so a skip of l steps is
//     δ_{n+l} = A·δ_n + B·ε      (A, B real 2×2; δ, ε as (re, im) columns)
// valid while |δ_n| < R. Merging two skips: A = A₂·A₁, B = A₂·B₁ + B₂,
// R = min(R₁, max(0, R₂ − ‖B₁‖·|ε|max) / ‖A₁‖), with ‖·‖ the operator (spectral) norm. This is
// Zhuoran's BLA merge (Engine/Math/Bla.cs) with the complex magnitudes replaced by operator norms, as
// mathr 2022 describes for non-complex-analytic formulas (Docs/Resources-Bibliography.md,
// #mathr-deep-zoom-2022). Design: Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md §4.2.

using System;

namespace FracturingFog.Perturbation;

/// <summary>A real 2×2 matrix [[M11, M12], [M21, M22]] acting on (re, im) columns.</summary>
public readonly record struct RealMat2(double M11, double M12, double M21, double M22)
{
    public static RealMat2 Zero => default;
    public static RealMat2 Identity => new(1, 0, 0, 1);

    /// <summary>Multiplication by the complex number a + bi, as a matrix.</summary>
    public static RealMat2 OfComplex(double a, double b) => new(a, -b, b, a);

    public static RealMat2 operator *(RealMat2 x, RealMat2 y) => new(
        x.M11 * y.M11 + x.M12 * y.M21, x.M11 * y.M12 + x.M12 * y.M22,
        x.M21 * y.M11 + x.M22 * y.M21, x.M21 * y.M12 + x.M22 * y.M22);

    public static RealMat2 operator +(RealMat2 x, RealMat2 y) => new(x.M11 + y.M11, x.M12 + y.M12, x.M21 + y.M21, x.M22 + y.M22);

    // The singular values of a 2×2 matrix: σ² = (s ± √(s² − 4·det²)) / 2, s the squared Frobenius norm.
    private (double Max2, double Min2) Sigma2()
    {
        double s = M11 * M11 + M12 * M12 + M21 * M21 + M22 * M22;
        double det = M11 * M22 - M12 * M21;
        double disc = Math.Sqrt(Math.Max(0.0, s * s - 4.0 * det * det));
        return (0.5 * (s + disc), Math.Max(0.0, 0.5 * (s - disc)));
    }

    /// <summary>The operator norm: the largest singular value.</summary>
    public double Norm => Math.Sqrt(Sigma2().Max2);

    /// <summary>The smallest singular value: min over unit v of |M·v|.</summary>
    public double MinGain
    {
        get
        {
            // σ_min = |det| / σ_max avoids the cancellation in (s − disc) when the two are far apart.
            double max = Norm;
            return max > 0 ? Math.Abs(M11 * M22 - M12 * M21) / max : 0.0;
        }
    }
}

/// <summary>One matrix bilinear approximation of <see cref="L"/> steps.</summary>
public readonly record struct Bla2(RealMat2 A, RealMat2 B, double R2, int L);

/// <summary>The hierarchical table of <see cref="Bla2"/> over a reference orbit: level k holds skips of
/// 2^k steps anchored at multiples of 2^k (the layout and lookup of Engine/Math/Bla.cs's BlaTable).</summary>
public sealed class Bla2Table
{
    public readonly int Levels, RefLen;
    public readonly int[] LevelStart, LevelLen;
    public readonly Bla2[] Data;

    public Bla2Table(Bla2[] level0, int refLen, double dcMax)
    {
        RefLen = refLen;
        int maxLevel = 0;
        while ((1 << (maxLevel + 1)) <= refLen) maxLevel++;
        Levels = Math.Max(1, maxLevel + 1);
        LevelStart = new int[Levels];
        LevelLen = new int[Levels];
        int total = 0;
        for (int k = 0; k < Levels; k++) { LevelStart[k] = total; LevelLen[k] = refLen >> k; total += LevelLen[k]; }
        Data = new Bla2[total];
        for (int n = 0; n < LevelLen[0]; n++) Data[n] = level0[n];
        for (int k = 1; k < Levels; k++)
        {
            int prev = LevelStart[k - 1], cur = LevelStart[k];
            for (int n = 0; n < LevelLen[k]; n++)
                Data[cur + n] = Merge(Data[prev + 2 * n], Data[prev + 2 * n + 1], dcMax, 1 << k);
        }
    }

    /// <summary>Skip x then y: A = A_y·A_x, B = A_y·B_x + B_y, and the radius in which x's output
    /// stays inside y's radius for every |ε| ≤ <paramref name="dcMax"/>.</summary>
    public static Bla2 Merge(in Bla2 x, in Bla2 y, double dcMax, int l)
    {
        var a = y.A * x.A;
        var b = y.A * x.B + y.B;
        double r1 = Math.Sqrt(x.R2), r2 = Math.Sqrt(y.R2);
        double r = Math.Min(r1, Math.Max(0.0, r2 - x.B.Norm * dcMax) / Math.Max(x.A.Norm, 1e-300));
        return new Bla2(a, b, r * r, l);
    }

    /// <summary>The longest skip valid at reference index <paramref name="n"/> for |δ|² =
    /// <paramref name="dMag2"/> that ends by <paramref name="maxIter"/>; −1 for none (BlaTable.Lookup).</summary>
    public int Lookup(int n, double dMag2, int maxIter)
    {
        int top = n == 0 ? Levels - 1 : Math.Min(Levels - 1, System.Numerics.BitOperations.TrailingZeroCount(n));
        for (int k = top; k >= 0; k--)
        {
            int l = 1 << k, idx = n >> k;
            if (idx >= LevelLen[k] || n + l > RefLen || n + l > maxIter) continue;
            ref readonly var b = ref Data[LevelStart[k] + idx];
            if (b.R2 > 0.0 && dMag2 < b.R2) return LevelStart[k] + idx;
        }
        return -1;
    }
}

/// <summary>A plan whose step is not complex-analytic (#1909): the engine keeps δ, ε and the
/// derivative in R² and skips with <see cref="Bla2"/>. <see cref="IPerturbationPlan.Level0"/> and
/// <see cref="IPerturbationPlan.DerivStep"/> are not used for such a plan.</summary>
public interface IRealPerturbationPlan : IPerturbationPlan
{
    /// <summary>The one-step matrix BLA at Z: A = ∂f/∂(z_re, z_im), B = ∂f/∂(c_re, c_im), and the radius
    /// in which the dropped second-order terms stay below ε of the linear one (and, #1910, no abs fold
    /// changes sign for any pixel offset up to <paramref name="dcMax"/>).</summary>
    Bla2 Level0R2(double zr, double zi, double cr, double ci, double epsilon, double dcMax);

    /// <summary>The Jacobian of the full orbit with respect to the pixel, J ← (∂f/∂z)·J + ∂f/∂c (the
    /// last term in the parameter plane only). Column 1 is ∂z/∂Re(pixel), what the sink receives.</summary>
    void JacobianStep(double zr, double zi, double cr, double ci, ref RealMat2 j);
}
