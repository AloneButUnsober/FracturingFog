// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1913 (epic #1253, D8) — the elementary functions under perturbation, in double: given the value V of
// the argument at the reference and its exact difference D, each returns f(V) and f(V + D) − f(V)
// with no large-minus-large step (Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md §4.5):
//     exp:  e^V·expm1(D)
//     log:  log1p(D/V), plus the 2πk that keeps arg(V) + arg in (−π, π] (the principal branch)
//     z^p:  V^p·expm1(p·L), L the principal log difference above
//     sin:  2·cos(V + D/2)·sin(D/2)       cos: −2·sin(V + D/2)·sin(D/2)
// The identities are standard (sum-to-product, Wikipedia "Sine and cosine" / "List of trigonometric
// identities" for the trig ones; the expm1 / log1p forms follow from e^{a+b} = e^a·e^b and
// log(V + D) = log V + log(1 + D/V)). FF found no prior use of these in a fractal renderer (§10; the
// search was not exhaustive).
// Complex expm1 and log1p are built from accurate real ones:
//     expm1(a + ib) = (expm1(a)·cos b − 2·sin²(b/2)) + i·e^a·sin b
//     log1p(w)      = ½·log1p(2·Re w + |w|²) + i·atan2(Im w, 1 + Re w)
// and the real expm1 / log1p use their series below |x| = ½ (Σ xⁿ/n!; 2·atanh(x/(2 + x))), where
// e^x − 1 and log(1 + x) lose digits (Wikipedia, "Exponential function", credits expm1 to a proposal
// by William Kahan). .NET's double.ExpM1 / LogP1 are not relied on.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace FracturingFog.Perturbation;

/// <summary>Four doubles: a complex value and a complex difference (or two complexes).</summary>
public readonly record struct Q4(double A, double B, double C, double D);

public static class PerturbFunctions
{
    /// <summary>e^x − 1, accurate for small x.</summary>
    public static double Expm1(double x)
    {
        if (Math.Abs(x) >= 0.5) return Math.Exp(x) - 1.0;
        double term = x, sum = x;
        for (int n = 2; n < 40; n++)
        {
            term *= x / n;
            sum += term;
            if (Math.Abs(term) < 1e-17 * Math.Abs(sum)) break;
        }
        return sum;
    }

    /// <summary>log(1 + x), accurate for small x (x &gt; −1).</summary>
    public static double Log1p(double x)
    {
        if (Math.Abs(x) >= 0.5) return Math.Log(1.0 + x);
        double s = x / (2.0 + x), s2 = s * s, pow = s, sum = s;
        for (int k = 1; k < 60; k++)
        {
            pow *= s2;
            double t = pow / (2 * k + 1);
            sum += t;
            if (Math.Abs(t) < 1e-17 * Math.Abs(sum)) break;
        }
        return 2.0 * sum;
    }

    public static Complex Expm1(Complex w)
    {
        double a = w.Real, b = w.Imaginary, sh = Math.Sin(0.5 * b);
        return new Complex(Expm1(a) * Math.Cos(b) - 2.0 * sh * sh, Math.Exp(a) * Math.Sin(b));
    }

    public static Complex Log1p(Complex w)
    {
        double a = w.Real, b = w.Imaginary;
        return new Complex(0.5 * Log1p(2.0 * a + a * a + b * b), Math.Atan2(b, 1.0 + a));
    }

    // The principal log(V + D) − log(V): log1p(D/V) + 2πik, k chosen so that arg(V) + arg(1 + D/V) − 2πk
    // lands in (−π, π] (the principal arg of V + D). In double that sum can round onto ±π: for V a hair
    // off the negative real axis arg(V) rounds to π, and a crossing of 1e-40 is lost (#1913's seeded
    // branch-cut view caught it). Inside that band the side of the cut decides instead, by signs: V + D
    // in the left half-plane with Im on the other side from V. Im = 0 counts as the upper side, as the
    // principal arg is π there (BigFloatMath.Atan2; −0.0 is treated as +0).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Complex LogDiff(Complex v, Complex d)
    {
        var l = Log1p(d / v);
        double total = Math.Atan2(v.Imaginary, v.Real) + l.Imaginary;
        const double band = 1e-9;
        if (total > Math.PI + band) return l - new Complex(0, 2 * Math.PI);
        if (total < -Math.PI - band) return l + new Complex(0, 2 * Math.PI);
        if (Math.Abs(Math.Abs(total) - Math.PI) <= band)
        {
            double sr = v.Real + d.Real, si = v.Imaginary + d.Imaginary;
            bool vUp = v.Imaginary >= 0, sUp = si >= 0;
            if (sr < 0 && vUp && !sUp && total > 0) return l - new Complex(0, 2 * Math.PI);
            if (sr < 0 && !vUp && sUp && total < 0) return l + new Complex(0, 2 * Math.PI);
        }
        return l;
    }

    public static Q4 ExpD(double vr, double vi, double dr, double di)
    {
        var f = Complex.Exp(new Complex(vr, vi));
        var g = f * Expm1(new Complex(dr, di));
        return new Q4(f.Real, f.Imaginary, g.Real, g.Imaginary);
    }

    public static Q4 LogD(double vr, double vi, double dr, double di)
    {
        var v = new Complex(vr, vi);
        var f = Complex.Log(v);
        var g = LogDiff(v, new Complex(dr, di));
        return new Q4(f.Real, f.Imaginary, g.Real, g.Imaginary);
    }

    /// <summary>V^p (principal) and (V + D)^p − V^p.</summary>
    public static Q4 PowD(double vr, double vi, double dr, double di, double pr, double pi)
    {
        var v = new Complex(vr, vi);
        if (vr == 0 && vi == 0)
        {
            var z = Pow(new Complex(dr, di), pr, pi);
            return new Q4(0, 0, z.Real, z.Imaginary);
        }
        var p = new Complex(pr, pi);
        var f = Complex.Exp(p * Complex.Log(v));
        var g = f * Expm1(p * LogDiff(v, new Complex(dr, di)));
        return new Q4(f.Real, f.Imaginary, g.Real, g.Imaginary);
    }

    public static Q4 SinD(double vr, double vi, double dr, double di)
    {
        var v = new Complex(vr, vi); var h = new Complex(0.5 * dr, 0.5 * di);
        var f = Complex.Sin(v);
        var g = 2.0 * Complex.Cos(v + h) * Complex.Sin(h);
        return new Q4(f.Real, f.Imaginary, g.Real, g.Imaginary);
    }

    public static Q4 CosD(double vr, double vi, double dr, double di)
    {
        var v = new Complex(vr, vi); var h = new Complex(0.5 * dr, 0.5 * di);
        var f = Complex.Cos(v);
        var g = -2.0 * Complex.Sin(v + h) * Complex.Sin(h);
        return new Q4(f.Real, f.Imaginary, g.Real, g.Imaginary);
    }

    /// <summary>The principal z^p (0^p = 0).</summary>
    public static Complex Pow(Complex z, double pr, double pi)
        => z == Complex.Zero ? Complex.Zero : Complex.Exp(new Complex(pr, pi) * Complex.Log(z));

    // ── derivatives for the tangent / second-order jets: (f(V), f′(V)) and f″(V) ──

    /// <summary>(f, f′, f″) at V for kind 0 exp, 1 log, 2 sin, 3 cos, 4 pow p, as (f, f′) in Q4 and f″ separately.</summary>
    public static Q4 Deriv1(int kind, double vr, double vi, double pr, double pi)
    {
        var v = new Complex(vr, vi);
        Complex f, d;
        switch (kind)
        {
            case 0: f = Complex.Exp(v); d = f; break;
            case 1: f = Complex.Log(v); d = 1.0 / v; break;
            case 2: f = Complex.Sin(v); d = Complex.Cos(v); break;
            case 3: f = Complex.Cos(v); d = -Complex.Sin(v); break;
            default: f = Pow(v, pr, pi); d = v == Complex.Zero ? Complex.Zero : new Complex(pr, pi) * f / v; break;
        }
        return new Q4(f.Real, f.Imaginary, d.Real, d.Imaginary);
    }

    public static Q4 Deriv2(int kind, double vr, double vi, double pr, double pi)
    {
        var v = new Complex(vr, vi);
        Complex s = kind switch
        {
            0 => Complex.Exp(v),
            1 => -1.0 / (v * v),
            2 => -Complex.Sin(v),
            3 => -Complex.Cos(v),
            _ => v == Complex.Zero ? Complex.Zero : new Complex(pr, pi) * (new Complex(pr, pi) - 1) * Pow(v, pr, pi) / (v * v),
        };
        return new Q4(s.Real, s.Imaginary, 0, 0);
    }

    /// <summary>Distance from V to log's branch cut (the non-positive real axis, 0 included).</summary>
    public static double CutDistance(double vr, double vi) => vr >= 0 ? Math.Sqrt(vr * vr + vi * vi) : Math.Abs(vi);
}
