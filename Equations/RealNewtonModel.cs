// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// RealNewtonModel.cs (#1989)
//
// A real scalar potential Phi(x, y) in the equation language, with its symbolic gradient and
// Hessian (RealExpression.Derivative), compiled once per source text, and the multivariate
// Newton-Raphson step on grad Phi = 0 that the User Equation real-Newton mode
// (Engine/Calculators/UserEquationCalculator.RealNewton.cs) runs per pixel. The step, the 1e-12
// step tolerance and the 1e-6 root match follow ThreeBodyScene.Newton (#1943, after Zotos 2016;
// see Docs/Resources-Bibliography.md).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace FracturingFog.Models;

/// <summary>#1989 — a compiled real potential Φ(x, y) with its symbolic gradient and Hessian.</summary>
public sealed class RealNewtonModel
{
    public static readonly string[] Variables = { "x", "y" };
    public const double StepTolerance = 1e-12, MatchRadius = 1e-6;
    public const int MaxRoots = 64;

    public string? Error { get; }
    internal readonly Func<double[], double> Gx = null!, Gy = null!, Hxx = null!, Hxy = null!, Hyy = null!;

    private RealNewtonModel(RealExpression phi)
    {
        var gx = phi.Derivative(0);
        var gy = phi.Derivative(1);
        Gx = gx.Compile(); Gy = gy.Compile();
        Hxx = gx.Derivative(0).Compile();
        Hxy = gx.Derivative(1).Compile();
        Hyy = gy.Derivative(1).Compile();
    }

    private RealNewtonModel(string error) => Error = error;

    private static readonly ConcurrentDictionary<string, RealNewtonModel> s_cache = new();

    /// <summary>The model for this potential (cached); <see cref="Error"/> is set when it does not parse.</summary>
    public static RealNewtonModel For(string source)
    {
        if (s_cache.TryGetValue(source, out var m)) return m;
        if (s_cache.Count > 32) s_cache.Clear();
        RealNewtonModel built;
        if (!RealExpression.TryParse(source, Variables, out var e, out var err) || e == null)
            built = new RealNewtonModel("potential: " + (err ?? "does not parse"));
        else
        {
            try { built = new RealNewtonModel(e); }
            catch (Exception ex) { built = new RealNewtonModel("potential: " + ex.Message); }
        }
        return s_cache[source] = built;
    }

    /// <summary>Newton from (x, y): the end point and the iterations taken; <c>converged</c> false
    /// when the cap ran out, the Hessian was singular or the iterate left |p| ≤ 1e6.</summary>
    public (double X, double Y, int Iterations, bool Converged) Solve(double x, double y, int maxIterations, double[] v)
    {
        for (int k = 1; k <= maxIterations; k++)
        {
            v[0] = x; v[1] = y;
            double gx = Gx(v), gy = Gy(v);
            double hxx = Hxx(v), hxy = Hxy(v), hyy = Hyy(v);
            double det = hxx * hyy - hxy * hxy;
            if (!(Math.Abs(det) > 1e-300) || !double.IsFinite(det)) return (x, y, k, false);
            double sx = (hyy * gx - hxy * gy) / det;
            double sy = (hxx * gy - hxy * gx) / det;
            x -= sx; y -= sy;
            if (!double.IsFinite(x) || !double.IsFinite(y) || x * x + y * y > 1e12) return (x, y, k, false);
            if (sx * sx + sy * sy < StepTolerance * StepTolerance) return (x, y, k, true);
        }
        return (x, y, maxIterations, false);
    }

    /// <summary>Parses "x, y; x, y; ...". Returns false (with a message) on a malformed pair.</summary>
    public static bool TryParseRoots(string? text, out (double X, double Y)[] roots, out string? error)
    {
        roots = Array.Empty<(double, double)>();
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var list = new List<(double, double)>();
        foreach (var raw in text.Split(';'))
        {
            var pair = raw.Trim();
            if (pair.Length == 0) continue;
            var xy = pair.Split(',');
            if (xy.Length != 2
                || !double.TryParse(xy[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rx)
                || !double.TryParse(xy[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ry)
                || !double.IsFinite(rx) || !double.IsFinite(ry))
            {
                error = $"roots: '{pair}' is not 'x, y'";
                return false;
            }
            list.Add((rx, ry));
            if (list.Count > MaxRoots) { error = $"roots: at most {MaxRoots}"; return false; }
        }
        roots = list.ToArray();
        return true;
    }

    /// <summary>Formats roots back to the parameter text (round-trip "R" digits).</summary>
    public static string FormatRoots(IEnumerable<(double X, double Y)> roots)
    {
        var parts = new List<string>();
        foreach (var (x, y) in roots)
            parts.Add(x.ToString("R", CultureInfo.InvariantCulture) + ", " + y.ToString("R", CultureInfo.InvariantCulture));
        return string.Join("; ", parts);
    }

    /// <summary>Index of the nearest root within <see cref="MatchRadius"/>, or −1.</summary>
    public static int Match(double x, double y, (double X, double Y)[] roots)
    {
        int best = -1;
        double bestD = MatchRadius * MatchRadius;
        for (int i = 0; i < roots.Length; i++)
        {
            double ex = x - roots[i].X, ey = y - roots[i].Y;
            double d = ex * ex + ey * ey;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
