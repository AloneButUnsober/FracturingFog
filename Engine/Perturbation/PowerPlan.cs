// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1907 (epic #1253, D2) — the formula plan for f(z) = z^d + c: Multibrot (the pixel is c, z₀ = 0)
// and Julia (the pixel is z₀, c fixed). Design: Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md
// §4.1. The δ recurrence is the binomial expansion of (Z + δ)^d − Z^d, every term of order δ, as the
// perturbation method requires (K. I. Martin, SuperFractalThing; credited in
// Docs/Resources-Bibliography.md). D3 derives plans like this from the equation AST instead.

using System;
using System.Runtime.CompilerServices;
using FracturingFog.FFMath;

namespace FracturingFog.Perturbation;

/// <summary>What a formula supplies to <see cref="PerturbationEngine"/> (the double-δ tier).</summary>
public interface IPerturbationPlan
{
    /// <summary>True when the pixel is the parameter c and z₀ = 0 is the critical point (Mandelbrot
    /// type): δ₀ = 0, ε = the pixel offset, and Zhuoran's rebase to Z₀ is valid. False when the
    /// pixel is z₀ (Julia type): δ₀ = the pixel offset, ε = 0, and there is no rebase.</summary>
    bool ParameterPlane { get; }

    /// <summary>True when Zhuoran's rebase applies: the parameter plane with z₀ = 0 a critical point
    /// of f (∂f/∂z = 0 there for every c), so restarting the reference index at Z₀ = 0 also restarts
    /// next to the critical point. Otherwise glitches go through <see cref="IsGlitch"/> and secondary
    /// references (#1908).</summary>
    bool CanRebase { get; }

    /// <summary>Escape radius squared.</summary>
    double Bailout2 { get; }

    /// <summary>dz/d(pixel) at n = 0: 1 by EscapeTimeCalculator's kernels' convention (Multibrot,
    /// Julia); 0 for z₀ = 0 when the derivative is the true dz/dc (User Equation).</summary>
    (double Re, double Im) InitialDerivative { get; }

    /// <summary>Precision lost (when no rebase applies): the pixel's z sits by a critical point the
    /// reference Z does not. Pauldelbrot's test generalised as |f′(z)|² &lt; τ·|f′(Z)|² (for z² it
    /// is his |z|² &lt; τ·|Z|²). <paramref name="cr"/>, <paramref name="ci"/>: the reference's c.</summary>
    bool IsGlitch(double zr, double zi, double Zr, double Zi, double cr, double ci, double tau2);

    /// <summary>One step of the reference orbit at <paramref name="prec"/> bits.</summary>
    void RefStep(ref BigFloat zr, ref BigFloat zi, BigFloat cr, BigFloat ci, int prec);

    /// <summary>δ ← f(Z + δ, C + ε) − f(Z, C), in double, with every term of order δ or ε.
    /// <paramref name="cr"/>, <paramref name="ci"/>: the reference's c (a coefficient here, so double
    /// is enough).</summary>
    void DeltaStep(double zr, double zi, double cr, double ci, ref double dr, ref double di, double er, double ei);

    /// <summary>The derivative of the full orbit at z (c the pixel's, to double): d ← f′(z)·d (+ ∂f/∂c
    /// in the parameter plane).</summary>
    void DerivStep(double zr, double zi, double cr, double ci, ref double dvr, ref double dvi);

    /// <summary>#1921 — <see cref="DerivStep"/> for a derivative held as d·2^t (t &gt; 0, past double's
    /// range): d ← f′(z)·d + <paramref name="cWeight"/>·∂f/∂c, cWeight = 2^−t. The default takes
    /// f′(z)·d as DerivStep(d) − DerivStep(0); the plans override it with the exact form.</summary>
    void DerivStepScaled(double zr, double zi, double cr, double ci, ref double dvr, ref double dvi, double cWeight)
    {
        double r0 = 0, i0 = 0;
        DerivStep(zr, zi, cr, ci, ref r0, ref i0);
        double r1 = dvr, i1 = dvi;
        DerivStep(zr, zi, cr, ci, ref r1, ref i1);
        dvr = r1 - r0 + cWeight * r0; dvi = i1 - i0 + cWeight * i0;
    }

    /// <summary>The one-step bilinear approximation at Z: A = ∂f/∂z, B = ∂f/∂c (0 in the dynamic
    /// plane), and the radius in which the dropped terms stay below ε of the linear one.</summary>
    Bla Level0(double zr, double zi, double cr, double ci, double epsilon);
}

/// <summary>f(z) = z^d + c, integer d ≥ 2.</summary>
public sealed class PowerPlan : IPerturbationPlan
{
    private readonly int _d;
    private readonly double[] _binom;   // C(d, k), k = 0..d
    private readonly double _jcr, _jci;

    /// <param name="degree">The exponent d (≥ 2).</param>
    /// <param name="parameterPlane">Multibrot (true) or Julia (false).</param>
    /// <param name="juliaCr">Julia's fixed c (ignored in the parameter plane).</param>
    /// <param name="juliaCi">See <paramref name="juliaCr"/>.</param>
    /// <param name="bailout2">Escape radius squared (EscapeTimeCalculator's kernels use 512²).</param>
    public PowerPlan(int degree, bool parameterPlane, double juliaCr = 0, double juliaCi = 0, double bailout2 = 512.0 * 512.0)
    {
        if (degree < 2) throw new ArgumentOutOfRangeException(nameof(degree), degree, "The exponent must be at least 2.");
        _d = degree;
        ParameterPlane = parameterPlane;
        _jcr = juliaCr; _jci = juliaCi;
        Bailout2 = bailout2;
        _binom = new double[degree + 1];
        _binom[0] = 1;
        for (int k = 1; k <= degree; k++) _binom[k] = _binom[k - 1] * (degree - k + 1) / k;
    }

    public int Degree => _d;
    public bool ParameterPlane { get; }
    public bool CanRebase => ParameterPlane;   // z^d's critical point is 0 = z₀
    public double Bailout2 { get; }
    public (double Re, double Im) InitialDerivative => (1, 0);

    public bool IsGlitch(double zr, double zi, double Zr, double Zi, double cr, double ci, double tau2)
        => zr * zr + zi * zi < tau2 * (Zr * Zr + Zi * Zi);   // Pauldelbrot

    /// <summary>Julia's c as BigFloat at <paramref name="prec"/> bits (the reference's c).</summary>
    public (BigFloat Re, BigFloat Im) JuliaC(int prec) => (BigFloat.FromDouble(_jcr, prec), BigFloat.FromDouble(_jci, prec));

    public void RefStep(ref BigFloat zr, ref BigFloat zi, BigFloat cr, BigFloat ci, int prec)
    {
        BigFloat pr = zr, pi = zi;
        for (int k = 1; k < _d; k++)
        {
            var nr = BigFloat.Sub(BigFloat.Mul(pr, zr, prec), BigFloat.Mul(pi, zi, prec), prec);
            var ni = BigFloat.Add(BigFloat.Mul(pr, zi, prec), BigFloat.Mul(pi, zr, prec), prec);
            pr = nr; pi = ni;
        }
        zr = BigFloat.Add(pr, cr, prec);
        zi = BigFloat.Add(pi, ci, prec);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DeltaStep(double zr, double zi, double cr, double ci, ref double dr, ref double di, double er, double ei)
    {
        // (Z+δ)^d − Z^d = δ · Σ_{k=1..d} C(d,k) Z^{d−k} δ^{k−1}, by Horner in δ from k = d down.
        // Z^{d−k} from the bottom up as k falls: start at Z^0 for k = d.
        double pr = _binom[_d], pi = 0;   // k = d term: C(d,d)·Z^0
        double zpr = 1, zpi = 0;          // Z^{d−k}
        for (int k = _d - 1; k >= 1; k--)
        {
            double t = zpr * zr - zpi * zi; zpi = zpr * zi + zpi * zr; zpr = t;   // Z^{d−k}
            // P ← P·δ + C(d,k)·Z^{d−k}
            double nr = pr * dr - pi * di + _binom[k] * zpr;
            double ni = pr * di + pi * dr + _binom[k] * zpi;
            pr = nr; pi = ni;
        }
        double ndr = pr * dr - pi * di, ndi = pr * di + pi * dr;
        // ε last, at its own scale (CalcGen's AstPerturbation orders terms the same way).
        dr = ndr + er; di = ndi + ei;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DerivStep(double zr, double zi, double cr, double ci, ref double dvr, ref double dvi)
    {
        PowM1(zr, zi, out double ar, out double ai);   // z^{d−1}
        ar *= _d; ai *= _d;
        double nr = ar * dvr - ai * dvi, ni = ar * dvi + ai * dvr;
        if (ParameterPlane) nr += 1.0;
        dvr = nr; dvi = ni;
    }

    public void DerivStepScaled(double zr, double zi, double cr, double ci, ref double dvr, ref double dvi, double cWeight)
    {
        PowM1(zr, zi, out double ar, out double ai);   // #1921: as DerivStep, ∂f/∂c weighted
        ar *= _d; ai *= _d;
        double nr = ar * dvr - ai * dvi, ni = ar * dvi + ai * dvr;
        if (ParameterPlane) nr += cWeight;
        dvr = nr; dvi = ni;
    }

    public Bla Level0(double zr, double zi, double cr, double ci, double epsilon)
    {
        PowM1(zr, zi, out double ar, out double ai);
        // The dropped terms start with C(d,2)·Z^{d−2}·δ²; keeping it below ε·|d·Z^{d−1}·δ| needs
        // |δ| ≤ 2ε|Z|/(d−1). Half of that leaves room for the higher terms; at d = 2 it is
        // Mandelbrot's ε·|Z| (BlaTable.BuildMandelbrotLevel0).
        double r = epsilon * Math.Sqrt(zr * zr + zi * zi) / (_d - 1);
        return new Bla(_d * ar, _d * ai, ParameterPlane ? 1.0 : 0.0, 0.0, r * r, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PowM1(double zr, double zi, out double pr, out double pi)
    {
        pr = 1; pi = 0;
        for (int k = 1; k < _d; k++) { double t = pr * zr - pi * zi; pi = pr * zi + pi * zr; pr = t; }
    }
}
