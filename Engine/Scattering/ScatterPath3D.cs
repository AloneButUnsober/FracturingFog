// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ScatterPath3D.cs (#1996, Math Plane 3D paths)
//
// Whole 3D paths of the gravitational-scattering families without a physical 2D plane, for
// the Math Plane 3D statements `geodesic` (black hole shadows, #1922) and `orbit3` (spinning
// binaries #1945, Mixmaster #1946). Each path is traced by the family's own scene code with a
// path recorder (ScatterPath.cs, the #1993 mechanism): the same launch, forces, step control
// and exit rules as the fractal's pixel, so the path ends in that pixel's fate.
//
// The KEY=VALUE pairs of the statement are the family's --param keys, applied through
// RegionFractalParams.FromKeyValues onto default FractalParameters (as the `fractal` and
// `orbit` statements do, #1731 / #1732).
//
// What is drawn (Math Plane 3D is z up, as these families' own coordinates are):
//   geodesic   the ray's position (x, y, z) in the Majumdar–Papapetrou isotropic coordinates,
//              and each hole as a wire sphere of radius m (the isotropic radius of a single
//              hole's photon sphere, Yumoto et al. §II.A; the horizon itself is the point).
//   spinbinary the relative orbit x(t) = x1 − x2 (Kidder 1995's relative coordinate), its
//              plane precessing with the spins.
//   mixmaster  the universe point in Cornish & Levin's Misner-type variables (PRD eqs. 70-72):
//              β+ = ⅓ ln(bc/a²), β− = (1/√3) ln(b/c), Ω = ⅓ ln(abc), drawn as (β+, β−, Ω).

using System;
using System.Collections.Generic;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>#1996 — one traced 3D path.</summary>
public sealed class ScatterPath3D
{
    public FractalType Type { get; init; }
    public bool Launched { get; init; }
    /// <summary>The family's gate (its fractal's colour index); -1 = undecided.</summary>
    public int Gate { get; init; } = -1;
    public string Fate { get; init; } = "";
    /// <summary>Path vertices in Math Plane 3D's axes.</summary>
    public List<(double X, double Y, double Z)> Points { get; init; } = new();
    /// <summary>Everything to stroke: the path, then the holes' wire spheres (geodesics).</summary>
    public List<List<(double X, double Y, double Z)>> Pieces { get; init; } = new();
    /// <summary>The recorded raw states (family state vectors), launch first.</summary>
    public ScatterPathRecorder? States { get; init; }
    /// <summary>The parameters the path was traced with (for invariant checks).</summary>
    public FractalParameters Parameters { get; init; } = new();
}

/// <summary>#1996 — traces a Math Plane 3D path object with its family's scene.</summary>
public static class ScatterPath3DTracer
{
    /// <summary>The family's parameters for <paramref name="o"/>, or false with the reason.</summary>
    public static bool TryParameters(MathPlaneObject o, out FractalType type, out FractalParameters p, out string? error)
    {
        type = MathPlane3DScene.PathTypeOf(o);
        p = new FractalParameters();
        error = null;
        var keys = MathPlane3DScene.FractalKeys(o);
        if (keys.Count > 0)
        {
            var rp = RegionFractalParams.FromKeyValues(keys, type, out string? kerr);
            if (rp == null) { error = $"{o.Name}: {kerr}"; return false; }
            rp.ApplyTo(p);
        }
        return true;
    }

    public static ScatterPath3D? Trace(MathPlaneObject o, out string? error, int maxStates = ScatterPathRecorder.DefaultMaxStates)
    {
        if (!TryParameters(o, out var type, out var p, out error)) return null;
        double a = o.Coords.Count > 0 ? o.Coords[0] : 0, b = o.Coords.Count > 1 ? o.Coords[1] : 0;
        switch (type)
        {
            case FractalType.BlackHoleShadow:
            {
                var scene = BlackHoleShadowCalculator.Scene.Build(p);
                var rec = new ScatterPathRecorder(6, maxStates);
                var (outcome, _) = scene.Trace(a, b, ref rec);
                rec.Finish();
                int gate = outcome.Fate switch
                {
                    ScatterFate.Captured => outcome.Body,
                    ScatterFate.Escaped => scene.EscapedGate,
                    _ => -1,
                };
                string fate = outcome.Fate switch
                {
                    ScatterFate.Captured => $"captured by hole {outcome.Body + 1}",
                    ScatterFate.Escaped => "escapes",
                    _ => "undecided (step limit)",
                };
                var pts = Project(rec, (s0, s1, s2) => (s0, s1, s2));
                // The ray starts on the launch plane far away (50 M by default); draw it inside a ball
                // round the holes so they stay visible.
                double extent = 2.0;
                foreach (var h in scene.Holes) extent = Math.Max(extent, Math.Sqrt(h.X * h.X + h.Y * h.Y + h.Z * h.Z) + 4 * h.M);
                var pieces = Clip(pts, 2.5 * extent);
                foreach (var h in scene.Holes)
                    if (h.M > 0) pieces.AddRange(WireSphere(h.X, h.Y, h.Z, h.M));
                return new ScatterPath3D { Type = type, Launched = true, Gate = gate, Fate = fate, Points = pts, Pieces = pieces, States = rec, Parameters = p };
            }
            case FractalType.SpinningBinaryBasins:
            {
                var scene = SpinningBinaryScene.Build(p);
                var rec = new ScatterPathRecorder(12, maxStates);
                Span<double> s = stackalloc double[ScatteringIntegrator.MaxDimension];
                var o2 = scene.Trace(a, b, s, ref rec);
                rec.Finish();
                // Gates as SpinningBinaryScene.Trace: Fates 0 merge / 1 separate / 2 orbit cap;
                // MergerSide 0 / 1 merge above / below the start plane, 2 separate, -1 orbit cap.
                bool side = p.SpinBinOutcome == SpinningBinaryOutcomeMode.MergerSide;
                string still = $"still orbiting after {o2.Orbits:0.#} orbits";
                string fate = (side, o2.Gate) switch
                {
                    (true, 0) => "merges above the start plane",
                    (true, 1) => "merges below the start plane",
                    (true, 2) => "separates",
                    (false, 0) => "merges",
                    (false, 1) => "separates",
                    _ => still,
                };
                var pts = Project(rec, (s0, s1, s2) => (s0, s1, s2));
                return new ScatterPath3D { Type = type, Launched = true, Gate = o2.Gate, Fate = fate, Points = pts,
                                           Pieces = new() { pts }, States = rec, Parameters = p };
            }
            default:
            {
                var scene = MixmasterScene.Build(p);
                var rec = new ScatterPathRecorder(6, maxStates);
                Span<double> s = stackalloc double[ScatteringIntegrator.MaxDimension];
                var o3 = scene.Trace(a, b, s, ref rec);
                rec.Finish();
                if (!o3.Valid)
                    return new ScatterPath3D { Type = type, Launched = false, Fate = "no launch here", States = rec, Parameters = p };
                string fate = o3.Gate >= 0 ? $"leaves through exit {o3.Gate}" : "undecided at the limit";
                var pts = Project(rec, Misner);
                return new ScatterPath3D { Type = type, Launched = true, Gate = o3.Gate, Fate = fate, Points = pts,
                                           Pieces = new() { pts }, States = rec, Parameters = p };
            }
        }
    }

    /// <summary>(ln a, ln b, ln c) → (β+, β−, Ω) (Cornish &amp; Levin PRD eqs. 70-72).</summary>
    public static (double BetaPlus, double BetaMinus, double Omega) Misner(double la, double lb, double lc)
        => ((lb + lc - 2 * la) / 3.0, (lb - lc) / Math.Sqrt(3.0), (la + lb + lc) / 3.0);

    private static List<(double X, double Y, double Z)> Project(ScatterPathRecorder rec,
        Func<double, double, double, (double, double, double)> f)
    {
        var pts = new List<(double X, double Y, double Z)>(rec.Count);
        for (int i = 0; i < rec.Count; i++)
        {
            var q = f(rec[i, 0], rec[i, 1], rec[i, 2]);
            if (double.IsFinite(q.Item1) && double.IsFinite(q.Item2) && double.IsFinite(q.Item3)) pts.Add(q);
        }
        return pts;
    }

    /// <summary>The parts of <paramref name="pts"/> inside the ball of radius <paramref name="r"/>.</summary>
    public static List<List<(double X, double Y, double Z)>> Clip(List<(double X, double Y, double Z)> pts, double r)
    {
        var res = new List<List<(double X, double Y, double Z)>>();
        List<(double X, double Y, double Z)>? cur = null;
        double r2 = r * r;
        foreach (var q in pts)
        {
            if (q.X * q.X + q.Y * q.Y + q.Z * q.Z <= r2) { (cur ??= new()).Add(q); }
            else if (cur != null) { if (cur.Count > 1) res.Add(cur); cur = null; }
        }
        if (cur is { Count: > 1 }) res.Add(cur);
        if (res.Count == 0) res.Add(new());   // keep the path piece first (possibly empty)
        return res;
    }

    /// <summary>Three great circles of a sphere (a light wireframe).</summary>
    public static IEnumerable<List<(double X, double Y, double Z)>> WireSphere(double cx, double cy, double cz, double r, int n = 48)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            var c = new List<(double, double, double)>(n + 1);
            for (int k = 0; k <= n; k++)
            {
                double t = 2 * Math.PI * k / n, u = r * Math.Cos(t), v = r * Math.Sin(t);
                c.Add(axis switch { 0 => (cx + u, cy + v, cz), 1 => (cx + u, cy, cz + v), _ => (cx, cy + u, cz + v) });
            }
            yield return c;
        }
    }
}
