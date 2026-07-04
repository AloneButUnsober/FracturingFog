// GpuQD.cs
//
// Wave 2.12 (D-6.27) — ILGPU-friendly Quad-Double (QD) arithmetic. Mirror of
// Abstractions/Math/QuadDouble.cs (CPU QD) but with the ILGPU kernel
// constraints honoured:
//
//   * No 'out' params — return tuples instead (per GpuKernelUtils file
//     comment + ILGPU's IR-level inliner preference).
//   * No managed refs, no exceptions, no Math.Pow.
//   * Math.FusedMultiplyAdd is OK — ILGPU 1.5+ translates to FMA on CUDA /
//     SSE-FMA on CPU. (CPU QD already uses this.)
//
// Algorithms identical to the CPU side (Hida-Li-Bailey sloppy variants).
// Same renormalisation cascade so a host-side parity test can validate
// per-iteration agreement against the CPU implementation.
//
// Used by MandelbrotRefOrbitGpu (Wave 2.12) — the sequential reference orbit
// kernel iterates Z_{n+1} = Z² + C in QD on the GPU when the host opts in.

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.Calculators.Gpu;

/// <summary>
/// Quad-double value type — 4 unevaluated doubles, ~62 decimal digits.
/// Kernel-compatible mirror of <c>FracturingFog.FFMath.QD</c>. Operations
/// live on <see cref="GpuQDMath"/> as static methods so the kernel JIT
/// sees no struct member call indirection.
/// </summary>
public readonly struct GpuQD
{
    public readonly double X0, X1, X2, X3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public GpuQD(double x0, double x1, double x2, double x3)
    { X0 = x0; X1 = x1; X2 = x2; X3 = x3; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public GpuQD(double v) { X0 = v; X1 = 0; X2 = 0; X3 = 0; }

    public static readonly GpuQD Zero = new(0, 0, 0, 0);
}

/// <summary>
/// Kernel-side QD primitives + algebra. All methods are
/// <c>AggressiveInlining</c> so the ILGPU IR sees one flat function per
/// kernel call site — no struct member dispatch, no virtuals, no allocations.
/// </summary>
public static class GpuQDMath
{
    // ── Primitives ────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double s, double e) TwoSum(double a, double b)
    {
        double s = a + b;
        double v = s - a;
        return (s, (a - (s - v)) + (b - v));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double s, double e) QuickTwoSum(double a, double b)
    {
        double s = a + b;
        return (s, b - (s - a));
    }

    // Dekker (1971) splitting constant — 2^27 + 1 for FP64. Splits a double
    // into 27-bit hi/lo halves with exact reconstruction (a = hi + lo).
    private const double SplitFactor = 134217729.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double hi, double lo) Split(double a)
    {
        double t = SplitFactor * a;
        double hi = t - (t - a);
        double lo = a - hi;
        return (hi, lo);
    }

    // Dekker TwoProduct — exact 2-double product without FMA. ILGPU 1.5.3
    // doesn't intercept Math.FusedMultiplyAdd as an intrinsic (causes
    // "internal compiler error" during kernel JIT), so the GPU path uses
    // the split-based form. Slightly more flops than the FMA form but
    // identical numerically when neither operand overflows the split.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double p, double e) TwoProduct(double a, double b)
    {
        double p = a * b;
        var (aHi, aLo) = Split(a);
        var (bHi, bLo) = Split(b);
        double e = ((aHi * bHi - p) + aHi * bLo + aLo * bHi) + aLo * bLo;
        return (p, e);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double s, double e1, double e2) ThreeSum(double a, double b, double c)
    {
        var (t1, t2) = TwoSum(a, b);
        var (s, t3) = TwoSum(c, t1);
        var (e1, e2) = TwoSum(t2, t3);
        return (s, e1, e2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double s, double e) ThreeSum2(double a, double b, double c)
    {
        var (t1, t2) = TwoSum(a, b);
        var (s, t3) = TwoSum(c, t1);
        return (s, t2 + t3);
    }

    // Renormalize a 5-term expansion to canonical 4-term form. Mirrors
    // QuadDouble.Renorm5 line-for-line.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double, double, double, double) Renorm5(
        double c0, double c1, double c2, double c3, double c4)
    {
        (c3, c4) = QuickTwoSum(c3, c4);
        (c2, c3) = QuickTwoSum(c2, c3);
        (c1, c2) = QuickTwoSum(c1, c2);
        (c0, c1) = QuickTwoSum(c0, c1);

        double s0 = c0, s1 = c1, s2 = 0, s3 = 0;
        if (s1 != 0.0)
        {
            (s1, s2) = QuickTwoSum(s1, c2);
            if (s2 != 0.0)
            {
                (s2, s3) = QuickTwoSum(s2, c3);
                if (s3 != 0.0) s3 += c4; else s2 += c4;
            }
            else
            {
                (s1, s2) = QuickTwoSum(s1, c3);
                if (s2 != 0.0) (s2, s3) = QuickTwoSum(s2, c4);
                else (s1, s2) = QuickTwoSum(s1, c4);
            }
        }
        else
        {
            (s0, s1) = QuickTwoSum(s0, c2);
            if (s1 != 0.0)
            {
                (s1, s2) = QuickTwoSum(s1, c3);
                if (s2 != 0.0) (s2, s3) = QuickTwoSum(s2, c4);
                else (s1, s2) = QuickTwoSum(s1, c4);
            }
            else
            {
                (s0, s1) = QuickTwoSum(s0, c3);
                if (s1 != 0.0) (s1, s2) = QuickTwoSum(s1, c4);
                else (s0, s1) = QuickTwoSum(s0, c4);
            }
        }
        return (s0, s1, s2, s3);
    }

    // ── Addition (sloppy variant — mirror of CPU QD operator+) ──────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GpuQD Add(GpuQD a, GpuQD b)
    {
        var (s0, e0) = TwoSum(a.X0, b.X0);
        var (s1, e1) = TwoSum(a.X1, b.X1);
        var (s2, e2) = TwoSum(a.X2, b.X2);
        double s3 = a.X3 + b.X3;

        (s1, e0) = TwoSum(s1, e0);
        double t1, t2;
        (s2, t1, t2) = ThreeSum(s2, e1, e0);
        (s3, e2) = ThreeSum2(s3, e2, t1);
        double s4 = e2 + t2;

        var (r0, r1, r2, r3) = Renorm5(s0, s1, s2, s3, s4);
        return new GpuQD(r0, r1, r2, r3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GpuQD Negate(GpuQD a) => new(-a.X0, -a.X1, -a.X2, -a.X3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GpuQD Sub(GpuQD a, GpuQD b) => Add(a, Negate(b));

    // QD + double — full carry cascade through all limbs (mirrors CPU QD).
    // The sloppy form (s1 = a.X1 + e, no TwoSum) drops the carry into a
    // discarded slot and makes X2/X3 = 0 forever during navigation. At
    // zoom > ~1e40 that makes navigation completely unrepresentable.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GpuQD AddD(GpuQD a, double b)
    {
        var (s0, e0) = TwoSum(a.X0, b);
        var (s1, e1) = TwoSum(a.X1, e0);
        var (s2, e2) = TwoSum(a.X2, e1);
        double s3 = a.X3 + e2;
        var (r0, r1, r2, r3) = Renorm5(s0, s1, s2, s3, 0);
        return new GpuQD(r0, r1, r2, r3);
    }

    // ── Multiplication (sloppy variant — mirror of CPU QD operator*) ───────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GpuQD Mul(GpuQD a, GpuQD b)
    {
        var (p0, q0) = TwoProduct(a.X0, b.X0);
        var (p1, q1) = TwoProduct(a.X0, b.X1);
        var (p2, q2) = TwoProduct(a.X1, b.X0);
        var (p3, q3) = TwoProduct(a.X0, b.X2);
        var (p4, q4) = TwoProduct(a.X1, b.X1);
        var (p5, q5) = TwoProduct(a.X2, b.X0);

        double tmpA, tmpB;
        (p1, tmpA, tmpB) = ThreeSum(p1, p2, q0);
        p2 = tmpA;
        q0 = tmpB;

        (p2, q1, q2) = ThreeSum(p2, q1, q2);
        (p3, p4, p5) = ThreeSum(p3, p4, p5);

        var (s0, t0) = TwoSum(p2, p3);
        var (s1, t1) = TwoSum(q1, p4);
        double s2 = q2 + p5;
        (s1, t0) = TwoSum(s1, t0);
        s2 += t0 + t1;

        s1 += a.X0 * b.X3 + a.X1 * b.X2 + a.X2 * b.X1 + a.X3 * b.X0
            + q0 + q3 + q4 + q5;

        var (r0, r1, r2, r3) = Renorm5(p0, p1, s0, s1, s2);
        return new GpuQD(r0, r1, r2, r3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GpuQD MulD(GpuQD a, double b)
    {
        var (p0, q0) = TwoProduct(a.X0, b);
        var (p1, q1) = TwoProduct(a.X1, b);
        var (p2, q2) = TwoProduct(a.X2, b);
        double p3 = a.X3 * b;

        (p1, q0) = TwoSum(p1, q0);
        double t0, t1;
        (p2, t0, t1) = ThreeSum(p2, q1, q0);
        (p3, q2) = ThreeSum2(p3, q2, t0);
        double p4 = q2 + t1;

        var (r0, r1, r2, r3) = Renorm5(p0, p1, p2, p3, p4);
        return new GpuQD(r0, r1, r2, r3);
    }

    /// <summary>this², saves redundant cross-products vs Mul(a, a).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GpuQD Square(GpuQD a)
    {
        var (p0, q0) = TwoProduct(a.X0, a.X0);
        var (p1, q1) = TwoProduct(2.0 * a.X0, a.X1);
        var (p2, q2) = TwoProduct(2.0 * a.X0, a.X2);
        var (p3, q3) = TwoProduct(a.X1, a.X1);

        (p1, q0) = TwoSum(p1, q0);

        double t0, t1;
        (p2, t0, t1) = ThreeSum(p2, q1, q0);
        (p3, q2) = ThreeSum2(p3, q2, t0);
        double p4 = q2 + t1;

        // O(eps³) self-product corrections
        p4 += 2.0 * a.X0 * a.X3 + 2.0 * a.X1 * a.X2 + q3;

        var (r0, r1, r2, r3) = Renorm5(p0, p1, p2, p3, p4);
        return new GpuQD(r0, r1, r2, r3);
    }
}
