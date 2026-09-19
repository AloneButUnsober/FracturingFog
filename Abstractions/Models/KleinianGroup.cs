// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/KleinianGroup.cs
//
// Kleinian 3D generalization — slice S1 (#874, epic #850 / design #855).
// See Docs/Technical/Kleinian-Generalization-DesignPlan.md.
//
// The shipped KleinianCalculator drew one hard-coded tetrahedral 4-sphere
// Schottky group. This descriptor makes the *group data* — the list of
// generators — the thing the distance estimator consumes, so future slices can
// vary it (user-editable sphere list, alternate presets, Möbius generators)
// without touching the render/shade stack. This slice ships the descriptor plus
// the inversion generator kind; the tetrahedral factory reproduces the previous
// group byte-for-byte (Tier 0 in the design doc's three-tier DE plan).
//
// Design note (§4.2): the representation is a tagged union — one struct per
// generator carrying a Kind byte. Only the Inversion kind exists in S1; Rotation
// and Möbius kinds arrive with the general word-descent DE (S4, #877). The
// alternative uniform 4x4 conformal-matrix (Vahlen) form is deferred to the
// analytic-DE slice (S8, #881) as an internal accumulation form only.

using System;
using System.Collections.Generic;

namespace FracturingFog.Models;

/// <summary>Kind of a Kleinian group generator. S1 (#874) ships only
/// <see cref="Inversion"/>; Rotation / Möbius arrive with the general
/// word-descent DE (#877).</summary>
public enum KleinianGeneratorKind
{
    /// <summary>Inversion in a sphere (centre + radius). An orientation-reversing
    /// conformal involution swapping the sphere interior and exterior.</summary>
    Inversion,
}

/// <summary>One generator of a Kleinian group. Tagged union keyed on
/// <see cref="Kind"/>; for <see cref="KleinianGeneratorKind.Inversion"/> the
/// sphere is (<see cref="Cx"/>, <see cref="Cy"/>, <see cref="Cz"/>) radius
/// <see cref="R"/>.</summary>
public readonly struct KleinianGenerator
{
    public KleinianGeneratorKind Kind { get; }
    public double Cx { get; }
    public double Cy { get; }
    public double Cz { get; }
    public double R { get; }

    private KleinianGenerator(KleinianGeneratorKind kind, double cx, double cy, double cz, double r)
    {
        Kind = kind;
        Cx = cx;
        Cy = cy;
        Cz = cz;
        R = r;
    }

    /// <summary>An inversion generator: the sphere centred at
    /// (<paramref name="cx"/>, <paramref name="cy"/>, <paramref name="cz"/>) with
    /// radius <paramref name="r"/>.</summary>
    public static KleinianGenerator Inversion(double cx, double cy, double cz, double r)
        => new(KleinianGeneratorKind.Inversion, cx, cy, cz, r);
}

/// <summary>A user-editable / serializable inversion sphere — the mutable
/// authoring form of an inversion <see cref="KleinianGenerator"/> (#876). The
/// custom-group editor edits a list of these; the calculator converts them to
/// generators. Public settable props + parameterless ctor so it round-trips
/// through the region JSON.</summary>
public sealed class KleinianSphereDef
{
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double Cz { get; set; }
    public double R { get; set; } = 1.0;

    public KleinianSphereDef() { }
    public KleinianSphereDef(double cx, double cy, double cz, double r)
    { Cx = cx; Cy = cy; Cz = cz; R = r; }

    public KleinianSphereDef Clone() => new(Cx, Cy, Cz, R);
    public KleinianGenerator ToGenerator() => KleinianGenerator.Inversion(Cx, Cy, Cz, R);
}

/// <summary>Immutable descriptor of a Kleinian group: the generator list plus
/// cached fast-path flags. Consumed by the calculator's distance estimator in
/// place of the previously hard-coded 4-sphere arrays. Lives in Abstractions
/// (UI-free contract) so it can later persist / animate like other serializable
/// shell types.</summary>
public sealed class KleinianGroup
{
    private readonly KleinianGenerator[] _generators;

    /// <summary>The generators. In S1 (#874) every entry is an inversion.</summary>
    public IReadOnlyList<KleinianGenerator> Generators => _generators;

    /// <summary>Inversion-iteration cap for the descent DE (the old
    /// <c>KleinianIterations</c> role).</summary>
    public int MaxWordLength { get; }

    /// <summary>True when every generator is an inversion — gates the descent
    /// fast path (the only path in S1).</summary>
    public bool AllInversions { get; }

    /// <summary>True when every generator shares one radius — gates the fixed
    /// 4-sphere GPU kernel (which passes a single <c>Radius</c>).</summary>
    public bool UniformRadius { get; }

    /// <summary>The shared radius when <see cref="UniformRadius"/>; otherwise the
    /// first generator's radius (or 0 for an empty group).</summary>
    public double SphereRadius { get; }

    public KleinianGroup(IReadOnlyList<KleinianGenerator> generators, int maxWordLength)
    {
        if (generators is null) throw new ArgumentNullException(nameof(generators));
        _generators = new KleinianGenerator[generators.Count];
        for (int i = 0; i < _generators.Length; i++) _generators[i] = generators[i];

        MaxWordLength = Math.Max(2, maxWordLength);

        bool allInv = true, uniform = true;
        double r0 = _generators.Length > 0 ? _generators[0].R : 0.0;
        for (int i = 0; i < _generators.Length; i++)
        {
            if (_generators[i].Kind != KleinianGeneratorKind.Inversion) allInv = false;
            if (_generators[i].R != r0) uniform = false;
        }
        AllInversions = allInv;
        UniformRadius = uniform;
        SphereRadius = r0;
    }

    /// <summary>The generator array (internal representation) for the hot DE
    /// loop. Returns the backing array directly — callers must not mutate it.</summary>
    public KleinianGenerator[] ToArray() => _generators;

    /// <summary>The generators as editable sphere defs — used to seed the custom
    /// editor from a built-in preset (#876). Non-inversion generators are
    /// skipped (none exist in S1–S2).</summary>
    public List<KleinianSphereDef> ToSphereDefs()
    {
        var list = new List<KleinianSphereDef>(_generators.Length);
        foreach (var g in _generators)
            if (g.Kind == KleinianGeneratorKind.Inversion)
                list.Add(new KleinianSphereDef(g.Cx, g.Cy, g.Cz, g.R));
        return list;
    }

    /// <summary>Build a group from a user-authored inversion sphere list (#876).
    /// Empty / null falls back to the tetrahedral preset so the renderer always
    /// has a valid group.</summary>
    public static KleinianGroup FromSpheres(IReadOnlyList<KleinianSphereDef>? spheres, int maxWordLength)
    {
        if (spheres is null || spheres.Count == 0) return Tetrahedral(1.0, maxWordLength);
        var gens = new KleinianGenerator[spheres.Count];
        for (int i = 0; i < gens.Length; i++) gens[i] = spheres[i].ToGenerator();
        return new KleinianGroup(gens, maxWordLength);
    }

    /// <summary>The shipped fixed tetrahedral 4-sphere preset: spheres of radius
    /// √2·scale centred at the four even-parity ±scale cube corners, in the exact
    /// order the pre-#874 calculator used (so the DE is byte-identical at Tier 0).
    /// At scale 1 the four spheres are mutually tangent and overlap at the origin.
    /// </summary>
    public static KleinianGroup Tetrahedral(double scale, int maxWordLength)
    {
        double s = scale;
        double r = Math.Sqrt(2.0) * s;
        var gens = new[]
        {
            KleinianGenerator.Inversion(+s, +s, +s, r),
            KleinianGenerator.Inversion(+s, -s, -s, r),
            KleinianGenerator.Inversion(-s, +s, -s, r),
            KleinianGenerator.Inversion(-s, -s, +s, r),
        };
        return new KleinianGroup(gens, maxWordLength);
    }

    /// <summary>Six inversion spheres at the ±axis face centres (±s,0,0),
    /// (0,±s,0), (0,0,±s), radius s/√2 so each sphere is tangent to its four
    /// neighbours (adjacent centre distance s·√2 = 2r). Octahedral-symmetry
    /// Schottky group — a denser cocoon than the tetrahedral preset.</summary>
    public static KleinianGroup Octahedral6(double scale, int maxWordLength)
    {
        double s = scale;
        double r = s / Math.Sqrt(2.0);
        var gens = new[]
        {
            KleinianGenerator.Inversion(+s, 0, 0, r),
            KleinianGenerator.Inversion(-s, 0, 0, r),
            KleinianGenerator.Inversion(0, +s, 0, r),
            KleinianGenerator.Inversion(0, -s, 0, r),
            KleinianGenerator.Inversion(0, 0, +s, r),
            KleinianGenerator.Inversion(0, 0, -s, r),
        };
        return new KleinianGroup(gens, maxWordLength);
    }

    /// <summary>Eight inversion spheres at all ±s cube corners, radius s so each
    /// sphere is tangent to its three edge-neighbours (edge centre distance 2s =
    /// 2r); face-diagonal and body-diagonal pairs stay disjoint. Cubic-symmetry
    /// Schottky group.</summary>
    public static KleinianGroup CubeCorner8(double scale, int maxWordLength)
    {
        double s = scale;
        double r = s;
        var gens = new[]
        {
            KleinianGenerator.Inversion(+s, +s, +s, r),
            KleinianGenerator.Inversion(+s, +s, -s, r),
            KleinianGenerator.Inversion(+s, -s, +s, r),
            KleinianGenerator.Inversion(+s, -s, -s, r),
            KleinianGenerator.Inversion(-s, +s, +s, r),
            KleinianGenerator.Inversion(-s, +s, -s, r),
            KleinianGenerator.Inversion(-s, -s, +s, r),
            KleinianGenerator.Inversion(-s, -s, -s, r),
        };
        return new KleinianGroup(gens, maxWordLength);
    }

    /// <summary>A planar ring of <paramref name="count"/> inversion spheres on a
    /// circle of radius <paramref name="scale"/> in the z = 0 plane, each tangent
    /// to its two neighbours (neighbour distance 2·scale·sin(π/N) = 2r). The
    /// Indra's-Pearls "necklace" group (Mumford–Series–Wright); its limit set is a
    /// closed fractal loop threading the tangency points. <paramref name="count"/>
    /// is clamped to [3, 24].</summary>
    public static KleinianGroup Necklace(double scale, int count, int maxWordLength)
    {
        int n = Math.Clamp(count, 3, 24);
        double a = scale;
        double r = a * Math.Sin(Math.PI / n);
        var gens = new KleinianGenerator[n];
        for (int k = 0; k < n; k++)
        {
            double ang = 2.0 * Math.PI * k / n;
            gens[k] = KleinianGenerator.Inversion(a * Math.Cos(ang), a * Math.Sin(ang), 0.0, r);
        }
        return new KleinianGroup(gens, maxWordLength);
    }

    /// <summary>Build the group for a preset. <paramref name="count"/> is used
    /// only by <see cref="KleinianPreset.NecklaceN"/>.</summary>
    public static KleinianGroup FromPreset(
        KleinianPreset preset, double scale, int count, int maxWordLength)
        => preset switch
        {
            KleinianPreset.Octahedral6 => Octahedral6(scale, maxWordLength),
            KleinianPreset.CubeCorner8 => CubeCorner8(scale, maxWordLength),
            KleinianPreset.NecklaceN => Necklace(scale, count, maxWordLength),
            _ => Tetrahedral(scale, maxWordLength),
        };
}
