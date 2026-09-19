// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/IndrasGroup.cs
//
// Indra's Pearls — 2D limit-set renderer, slice S1 (#892, epic #850 / design
// Docs/Technical/Indras-Pearls-2D-DesignPlan.md).
//
// A two-generator Kleinian group of complex Möbius transformations. Unlike the
// 3D KleinianCalculator (sphere-inversion distance estimator), this group acts
// on the Riemann sphere Ĉ and its limit set is a fractal *curve* on the plane —
// so it is plotted combinatorially (word enumeration → point cloud), not
// raymarched. See design doc §3.4 for why the two live on separate render paths.
//
// This file is the pure math core:
//   • Mobius       — a 2×2 complex matrix acting as z ↦ (az+b)/(cz+d).
//   • IndrasGroup  — the two-generator group builders (Maskit / Grandma / Riley)
//                    and its four-letter alphabet {a, A, b, B}.
//
// The enumeration (BFS word tree → density buffer) lives in the Engine
// calculator; this file has no rendering dependency so tests and the calculator
// share the same primitive.
//
// Sources (Rule B) — Mumford, Series & Wright, "Indra's Pearls: The Vision of
// Felix Klein" (Cambridge UP, 2002):
//   • Maskit recipe   — p. 259  (a: z ↦ μ + 1/z,  b: z ↦ z + 2).
//   • Grandma's recipe — p. 227  (traces ta, tb → matrices a, b; Markov identity
//                                 ta² + tb² + tab² = ta·tb·tab).
//   • Riley's recipe  — p. 258  (two-parabolic slice, single complex c).
// Exact entry formulas cross-checked against Tim Hutton's reference
// implementation of the book (github.com/timhutton/mobius-transforms, GPL).

using System;
using System.Collections.Generic;
using System.Numerics;

namespace FracturingFog.Models;

/// <summary>Which two-generator family the Indra's-Pearls limit set is built
/// from (design doc §3.5). Each is a complex-Möbius group; the shipped default
/// (S1) is <see cref="Maskit"/> at μ = 2i (the MSW "apple", p. 259).</summary>
public enum IndrasGroupFamily
{
    /// <summary>Maskit slice — one complex parameter μ. a: z ↦ μ + 1/z (parabolic
    /// at μ = 2i), b: z ↦ z + 2 (parabolic translation). MSW p. 259.</summary>
    Maskit,
    /// <summary>Grandma's recipe — two complex traces ta, tb (tab derived from the
    /// Markov identity). The canonical Indra's-Pearls parameter space. MSW p. 227.</summary>
    GrandmaRecipe,
    /// <summary>Riley slice — two parabolic generators, one complex parameter c.
    /// a: z ↦ z/(cz+1), b: z ↦ z + 2. MSW p. 258.</summary>
    Riley,
}

/// <summary>How the limit set is drawn (design doc §3.4). <see cref="PointCloud"/>
/// (S1/S2) enumerates reduced words breadth-first and plots the images of on-Λ
/// seed points into a density buffer — robust, slightly blurry.
/// <see cref="CurveTrace"/> (the MSW ch. 9 "special words" algorithm) walks the
/// word tree depth-first tracking fixed-point separation to draw Λ as an ordered
/// crisp curve — lands in S3 (#894); until then it falls back to the point
/// cloud.</summary>
public enum IndrasRenderMode
{
    /// <summary>BFS word enumeration → density point cloud (S1/S2).</summary>
    PointCloud,
    /// <summary>DFS "special words" curve tracer (S3, #894).</summary>
    CurveTrace,
}

/// <summary>A Möbius transformation of the Riemann sphere Ĉ = ℂ ∪ {∞},
/// <c>z ↦ (A·z + B)/(C·z + D)</c>, stored as its 2×2 complex matrix
/// <c>[[A, B], [C, D]]</c>. Composition is matrix multiplication; the group
/// action ignores an overall scalar (PSL(2,ℂ)), so entries may be normalised to
/// det = 1 without changing the map.</summary>
public readonly struct Mobius : IEquatable<Mobius>
{
    public readonly Complex A, B, C, D;

    public Mobius(Complex a, Complex b, Complex c, Complex d)
    {
        A = a; B = b; C = c; D = d;
    }

    public static readonly Mobius Identity =
        new(Complex.One, Complex.Zero, Complex.Zero, Complex.One);

    /// <summary>Determinant AD − BC.</summary>
    public Complex Determinant => A * D - B * C;

    /// <summary>Matrix product <c>this ∘ other</c> — apply <paramref name="other"/>
    /// first, then <c>this</c>.</summary>
    public Mobius Multiply(in Mobius o) => new(
        A * o.A + B * o.C,
        A * o.B + B * o.D,
        C * o.A + D * o.C,
        C * o.B + D * o.D);

    /// <summary>Matrix inverse. For a Möbius map the adjugate <c>[[D, −B], [−C, A]]</c>
    /// realises the inverse map regardless of scale (the shared 1/det factor is a
    /// PSL(2,ℂ) no-op), so no division is needed.</summary>
    public Mobius Inverse() => new(D, -B, -C, A);

    /// <summary>Divide every entry by √det so det = 1 (up to sign). Keeps entries
    /// bounded across long word products — the numerical-stability step of design
    /// doc §3.4. A near-singular matrix (det ≈ 0) is returned unchanged.</summary>
    public Mobius Normalized()
    {
        Complex det = Determinant;
        if (det.Magnitude < 1e-300) return this;
        Complex s = Complex.Sqrt(det);
        return new(A / s, B / s, C / s, D / s);
    }

    /// <summary>Apply the map to a finite point. Returns false (and NaN) when the
    /// point maps to ∞ (denominator ≈ 0) — the caller skips plotting it.</summary>
    public bool TryApply(Complex z, out Complex result)
    {
        Complex denom = C * z + D;
        if (denom.Magnitude < 1e-18)
        {
            result = new Complex(double.NaN, double.NaN);
            return false;
        }
        result = (A * z + B) / denom;
        return true;
    }

    /// <summary>The fixed point(s) of the map, roots of <c>C·z² + (D−A)·z − B = 0</c>.
    /// Loxodromic / elliptic maps give two; a parabolic map gives one (a double
    /// root). When C ≈ 0 (the map fixes ∞) the single finite fixed point −B/(D−A)
    /// is returned, or none if it also fixes ∞ (a translation).</summary>
    public Complex[] FixedPoints()
    {
        // C z² + (D − A) z − B = 0.
        Complex a = C;
        Complex b = D - A;
        Complex c = -B;

        if (a.Magnitude < 1e-18)
        {
            // Linear: b z + c = 0. b ≈ 0 → fixes ∞ only (pure translation).
            if (b.Magnitude < 1e-18) return Array.Empty<Complex>();
            return new[] { -c / b };
        }

        Complex disc = Complex.Sqrt(b * b - 4.0 * a * c);
        Complex r1 = (-b + disc) / (2.0 * a);
        Complex r2 = (-b - disc) / (2.0 * a);
        if ((r1 - r2).Magnitude < 1e-12) return new[] { r1 };  // parabolic (double root)
        return new[] { r1, r2 };
    }

    public bool Equals(Mobius other) => A == other.A && B == other.B && C == other.C && D == other.D;
    public override bool Equals(object? obj) => obj is Mobius m && Equals(m);
    public override int GetHashCode() => HashCode.Combine(A, B, C, D);
    public override string ToString() => $"[[{A}, {B}], [{C}, {D}]]";
}

/// <summary>A two-generator Möbius group for the Indra's-Pearls limit set. Holds
/// the two generators <c>a</c>, <c>b</c> and exposes the four-letter alphabet
/// {a, A = a⁻¹, b, B = b⁻¹} used by the word-enumeration plotter. Built by the
/// family factories (Maskit / Grandma / Riley); the matrices are *derived* from
/// the scalar parameters, never stored on <c>FractalParameters</c>.</summary>
public sealed class IndrasGroup
{
    /// <summary>Generator letters indexed 0=a, 1=A(a⁻¹), 2=b, 3=B(b⁻¹). The
    /// inverse of letter <c>i</c> is <c>i ^ 1</c> (0↔1, 2↔3) — used by the plotter
    /// to forbid immediate back-tracking (a reduced word never contains aA/bB).</summary>
    public Mobius[] Letters { get; }

    /// <summary>The family this group was built from.</summary>
    public IndrasGroupFamily Family { get; }

    private IndrasGroup(IndrasGroupFamily family, Mobius a, Mobius b)
    {
        Family = family;
        a = a.Normalized();
        b = b.Normalized();
        Letters = new[] { a, a.Inverse(), b, b.Inverse() };
    }

    /// <summary>The inverse letter index (0↔1, 2↔3).</summary>
    public static int InverseLetter(int letter) => letter ^ 1;

    /// <summary>Maskit slice (MSW p. 259): a: z ↦ μ + 1/z = (μ·z + 1)/z,
    /// b: z ↦ z + 2. The shipped default is μ = 2i (the "apple" limit set, where
    /// a is parabolic with fixed point i).</summary>
    public static IndrasGroup Maskit(Complex mu)
    {
        var a = new Mobius(mu, Complex.One, Complex.One, Complex.Zero);
        var b = new Mobius(Complex.One, new Complex(2, 0), Complex.Zero, Complex.One);
        return new IndrasGroup(IndrasGroupFamily.Maskit, a, b);
    }

    /// <summary>Grandma's recipe (MSW p. 227). Given traces ta, tb, solve the
    /// Markov quadratic <c>x² − ta·tb·x + (ta² + tb²) = 0</c> for tab (root
    /// <paramref name="secondSolution"/> picks which), then build a, b from the
    /// book's closed form. Produces a limit set with 180°-rotational symmetry.</summary>
    public static IndrasGroup Grandma(Complex ta, Complex tb, bool secondSolution = false)
    {
        // Markov quadratic: x² − (ta·tb)·x + (ta² + tb²) = 0.
        Complex p = -(ta * tb);
        Complex q = ta * ta + tb * tb;
        Complex disc = Complex.Sqrt(p * p - 4.0 * q);
        Complex tab = secondSolution ? (-p - disc) / 2.0 : (-p + disc) / 2.0;

        Complex i2 = new(0, 2);
        Complex i4 = new(0, 4);

        // z0 = ((tab − 2)·tb) / (tb·tab − 2·ta + 2i·tab).
        Complex z0 = ((tab - 2.0) * tb)
                   / (tb * tab - 2.0 * ta + i2 * tab);

        // a = [[ ta/2,                          (ta·tab − 2·tb + 4i)/((2·tab + 4)·z0) ],
        //      [ (ta·tab − 2·tb − 4i)·z0/(2·tab − 4),   ta/2 ]]
        var a = new Mobius(
            ta / 2.0,
            (ta * tab - 2.0 * tb + i4) / ((2.0 * tab + 4.0) * z0),
            (ta * tab - 2.0 * tb - i4) * z0 / (2.0 * tab - 4.0),
            ta / 2.0);

        // b = [[ (tb − 2i)/2,  tb/2 ], [ tb/2,  (tb + 2i)/2 ]]
        var b = new Mobius(
            (tb - i2) / 2.0,
            tb / 2.0,
            tb / 2.0,
            (tb + i2) / 2.0);

        return new IndrasGroup(IndrasGroupFamily.GrandmaRecipe, a, b);
    }

    /// <summary>Riley slice (MSW p. 258): two parabolic generators. a: z ↦ z/(c·z + 1)
    /// = [[1, 0], [c, 1]], b: z ↦ z + 2 = [[1, 2], [0, 1]]. One complex parameter c.</summary>
    public static IndrasGroup Riley(Complex c)
    {
        var a = new Mobius(Complex.One, Complex.Zero, c, Complex.One);
        var b = new Mobius(Complex.One, new Complex(2, 0), Complex.Zero, Complex.One);
        return new IndrasGroup(IndrasGroupFamily.Riley, a, b);
    }

    /// <summary>A small set of finite seed points that lie on the limit set Λ: the
    /// fixed points of a, b, ab and aB (a·b⁻¹). Λ is group-invariant, so applying
    /// any enumerated word to one of these yields another point of Λ — the basis
    /// of the point-cloud plot. Degenerate (∞ / NaN) fixed points are dropped.</summary>
    public IReadOnlyList<Complex> SeedPoints()
    {
        var seeds = new List<Complex>(8);
        void Add(Mobius m)
        {
            foreach (var z in m.FixedPoints())
                if (!double.IsNaN(z.Real) && !double.IsNaN(z.Imaginary)
                    && !double.IsInfinity(z.Real) && !double.IsInfinity(z.Imaginary))
                    seeds.Add(z);
        }
        Mobius a = Letters[0], b = Letters[2], B = Letters[3];
        Add(a);
        Add(b);
        Add(a.Multiply(b));   // ab
        Add(a.Multiply(B));   // aB
        if (seeds.Count == 0) seeds.Add(Complex.Zero);
        return seeds;
    }
}
