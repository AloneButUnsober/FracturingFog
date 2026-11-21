// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/UserEquationCalculator.RotationDomain.cs
//
// #1984 — rotation-domain colouring (#1981, RotationDomainColoring.cs; idea: pauldelbrot,
// fractalforums.org topic 5693) for User Equations, in the double tier. A post-pass over the
// pixels the render left at the cap (in-set, and the #544 converged / #615 surround pixels, which
// the pass's own orbit stops on and so keeps).
//
//  • f′ is exact: the interpreter's forward-mode dual at fixed c (SandboxExpression.EvalStepDz /
//    EvalStepTangent). f″ is the forward difference of that exact f′ (step 1e-4·max(1, |z|), the
//    step Abstractions/Explore/CriticalPoints uses; one extra evaluation per step, ~1e-4 relative
//    error), which only feeds the distance estimate's shading.
//  • an equation that reads `prev` is a two-variable map (z, prev) → (f, z): λ is its largest
//    exponent from a tangent vector, as for Phoenix, and it gets a flat shade.
//  • a start that does not depend on c (no z0 seed, or a seed whose dz0/dc is 0) is a parameter
//    plane: z₀ is usually a critical point (z² + c from 0), so the product starts at the first iterate.
//  • a non-holomorphic step (abs / conj / re / im / conditions) has no derivative: no pass, the
//    frame is unchanged.

using System;
using System.Numerics;
using System.Threading;

using FracturingFog.Models;

namespace FracturingFog;

public sealed partial class UserEquationCalculator
{
    private uint[]? _rotationStamp;
    private float[]? _rotationDistPx;
    private bool _rotationRan;

    /// <summary>#1984 — per-class pixel counts of the last frame's pass (all 0 when off).</summary>
    public RotationDomainColoring.Stats LastRotationDomainStats { get; private set; }

    /// <summary>#1984 — why the last frame did not run the pass although it was on (null when it ran
    /// or was off).</summary>
    public string? LastRotationDomainSkip { get; private set; }

    private const double FppStep = 1e-4;   // relative step of the difference for f″

    private void ResetRotationDomain()
    {
        _rotationRan = false;
        LastRotationDomainStats = default;
        LastRotationDomainSkip = null;
    }

    private void ApplyRotationDomain(SandboxExpression sbx, SandboxExpression? seed, SandboxExpression? cond,
                                     double effBailout2, bool replacesModulus, double scale, double cosA, double sinA,
                                     byte[]? mask, CancellationToken ct)
    {
        if (!FractalParameters.RotationDomainColoring) return;
        if (!sbx.IsHolomorphic)
        {
            LastRotationDomainSkip = "the equation is not holomorphic (abs / conj / re / im / conditions): no derivative";
            return;
        }
        int w = Width, h = Height, n = w * h;
        if (n <= 0 || ColorBuffer.Length < n) return;
        if (_rotationStamp?.Length != n) _rotationStamp = new uint[n];
        if (_rotationDistPx?.Length != n) _rotationDistPx = new float[n];

        var s = RotationDomainColoring.Settings.From(FractalParameters);
        int maxIt = MaxIterations;
        double cx0 = CenterX, cy0 = CenterY;
        int[] iters = IterationBuffer;
        bool usesPrev = UsesPrev(sbx);
        bool seedHolo = seed == null || seed.IsHolomorphic;

        RotationDomainColoring.Result Classify(int x, int y)
        {
            double dx = (x - w * 0.5) * scale, dy = (y - h * 0.5) * scale;
            var c = new Complex(cx0 + dx * cosA - dy * sinA, cy0 + dx * sinA + dy * cosA);
            var denv = sbx.NewDualEnv();
            Complex z0 = Complex.Zero;
            bool fromFirst = true;
            if (seed != null)
            {
                if (seedHolo)
                {
                    var (sz, sd) = seed.EvalStepD(Complex.Zero, Complex.Zero, c, 0, seed.NewDualEnv());
                    z0 = sz; fromFirst = sd == Complex.Zero;
                }
                else { z0 = seed.EvalStep(Complex.Zero, c, 0, seed.NewEnv()); fromFirst = false; }
            }
            var condEnv = cond?.NewEnv();
            bool Stops(Complex zz, int it, Complex pv)
            {
                double r2 = zz.Real * zz.Real + zz.Imaginary * zz.Imaginary;
                if (r2 >= effBailout2 || double.IsNaN(r2) || double.IsInfinity(r2)) return true;
                if (cond != null)
                {
                    var r = cond.EvalStep(zz, c, it, condEnv!, pv);
                    if (r.Real != 0.0 || r.Imaginary != 0.0) return true;
                }
                return false;
            }
            try
            {
                return usesPrev ? ClassifyTwo(sbx, denv, c, z0, maxIt, Stops, s)
                                : ClassifyOne(sbx, denv, c, z0, maxIt, fromFirst, Stops, s);
            }
            catch (Exception) { return new(RotationDomainColoring.OrbitClass.Attracting, double.NegativeInfinity, double.PositiveInfinity); }
        }

        LastRotationDomainStats = RotationDomainColoring.Fill(_rotationStamp, w, h, mask,
            idx => iters[idx] >= maxIt, Classify, scale, s, ct, _rotationDistPx);
        RotationDomainColoring.Stamp(_rotationStamp, ColorBuffer, mask, InteriorAlpha);   // in-set colour is alpha-scaled
        _rotationRan = true;
    }

    // One variable: f, exact f′, f″ by central difference of f′; the #1981 recurrences.
    private static RotationDomainColoring.Result ClassifyOne(SandboxExpression sbx, CxDual[] denv, Complex c, Complex z0,
        int maxIt, bool fromFirst, Func<Complex, int, Complex, bool> stops, in RotationDomainColoring.Settings s)
    {
        Complex z = z0, prev = Complex.Zero, d1 = Complex.One, d2 = Complex.Zero;
        double mul = 1, dist = double.PositiveInfinity; long mulE = 0;
        int counted = 0; bool track = true;
        for (int it = 0; it < maxIt; it++)
        {
            if (stops(z, it, prev)) return new(RotationDomainColoring.OrbitClass.Attracting, double.NegativeInfinity, double.PositiveInfinity);
            var (f, fz) = sbx.EvalStepDz(z, c, it, denv, prev);
            if (it > 0 || !fromFirst)
            {
                FracturingFog.Perturbation.PerturbationEngine.MulIn(ref mul, ref mulE, fz.Real * fz.Real + fz.Imaginary * fz.Imaginary);
                counted++;
                if (track)
                {
                    double hh = FppStep * Math.Max(1, z.Magnitude);
                    Complex fzz = (sbx.EvalStepDz(z + hh, c, it, denv, prev).dfdz - fz) / hh;   // forward difference: one extra evaluation
                    d2 = fzz * d1 * d1 + fz * d2;
                    d1 = fz * d1;
                    double m1 = d1.Magnitude, m2 = d2.Magnitude;
                    if (!(m1 < 1e150 && m1 > 1e-150 && m2 < 1e300)) track = false;
                    else if (m2 > 0) dist = Math.Min(dist, m1 / m2);
                }
            }
            prev = z; z = f;
        }
        if (counted == 0) return new(RotationDomainColoring.OrbitClass.Attracting, 0, double.PositiveInfinity);
        double lambda = mul > 0 ? 0.5 * (Math.Log2(mul) + mulE) * Math.Log(2) / counted : double.NegativeInfinity;
        return RotationDomainColoring.Verdict(lambda, counted, track ? dist : double.PositiveInfinity, s);
    }

    // Two variables (z, prev) → (f(z, prev), z): the largest exponent from a tangent vector (a, b).
    private static RotationDomainColoring.Result ClassifyTwo(SandboxExpression sbx, CxDual[] denv, Complex c, Complex z0,
        int maxIt, Func<Complex, int, Complex, bool> stops, in RotationDomainColoring.Settings s)
    {
        Complex z = z0, prev = Complex.Zero, a = Complex.One, b = Complex.Zero;
        double mul = 1; long mulE = 0;
        for (int it = 0; it < maxIt; it++)
        {
            if (stops(z, it, prev)) return new(RotationDomainColoring.OrbitClass.Attracting, double.NegativeInfinity, double.PositiveInfinity);
            var (f, na) = sbx.EvalStepTangent(z, a, c, it, denv, prev, b);
            Complex nb = a;
            double n2 = na.Real * na.Real + na.Imaginary * na.Imaginary + nb.Real * nb.Real + nb.Imaginary * nb.Imaginary;
            if (!(n2 > 0) || !double.IsFinite(n2))
                return new(RotationDomainColoring.OrbitClass.Attracting, double.NegativeInfinity, double.PositiveInfinity);
            FracturingFog.Perturbation.PerturbationEngine.MulIn(ref mul, ref mulE, n2);
            double inv = 1 / Math.Sqrt(n2);
            a = na * inv; b = nb * inv;
            prev = z; z = f;
        }
        double lambda = 0.5 * (Math.Log2(mul) + mulE) * Math.Log(2) / maxIt;
        return RotationDomainColoring.Verdict(lambda, maxIt, double.PositiveInfinity, s);
    }

    /// <summary>#1984 — whether the step reads <c>prev</c>: its exact ∂f/∂prev at a few points. A
    /// holomorphic expression that never reads the slot has a derivative of exactly 0 there.</summary>
    public static bool UsesPrev(SandboxExpression sbx)
    {
        var denv = sbx.NewDualEnv();
        foreach (var (z, c, p) in new[] { (new Complex(0.31, 0.17), new Complex(-0.23, 0.41), new Complex(0.53, -0.29)),
                                          (new Complex(-0.7, 0.2), new Complex(0.1, -0.3), new Complex(-0.2, 0.6)) })
        {
            try
            {
                var (_, d) = sbx.EvalStepTangent(z, Complex.Zero, c, 1, denv, p, Complex.One);
                if (d != Complex.Zero) return true;
            }
            catch (Exception) { return true; }
        }
        return false;
    }

    /// <summary>#1984 / #1981 part 2 — the rotation-domain distances (pixels) for the adaptive AA
    /// mask, when that colouring ran.</summary>
    public float[]? AdaptiveAaBoundaryDistancePx()
    {
        if (!_rotationRan || _rotationDistPx is not { } r || r.Length != Width * Height) return null;
        var b = new float[r.Length];
        FracturingFog.Rendering.AdaptiveAaDistance.MergeRotation(b, r);
        return b;
    }
}
