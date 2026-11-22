// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserLens.cs (#1995, User Lens)
//
// A user-written scalar field U(x, y, z) for the black hole shadow trace (#1922,
// MajumdarPapapetrou.cs). The Majumdar–Papapetrou null-ray Hamiltonian needs only U and ∇U
// (Shipley & Dolan 2016, Eq. 6), so a user U runs the whole shadow pipeline: fates, channels,
// skies, cameras, adaptive AA. ∇U is the SYMBOLIC gradient of the formula (RealExpression,
// #1994), compiled once per source text.
//
// Honesty (shown in the help and the panel): only a HARMONIC U (∇²U = 0 away from its
// sources), with U -> 1 far away, is a Majumdar–Papapetrou vacuum spacetime (Majumdar 1947,
// Papapetrou 1947). Any other U still defines a well-posed optical medium for the rays
// (refractive index n = U² in the optical analogy Trettel 2025 uses), but not a solution of
// Einstein's equations. The model carries the symbolic Laplacian so FF can say which.

using System;
using System.Collections.Concurrent;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>#1995 — the compiled U, ∇U and ∇²U of one source text.</summary>
public sealed class UserLensModel
{
    public static readonly string[] Variables = { "x", "y", "z" };

    public string? Error { get; }
    public RealExpression U { get; } = null!;
    public RealExpression Laplacian { get; } = null!;
    private readonly Func<double[], double> _u = null!, _gx = null!, _gy = null!, _gz = null!, _lap = null!;

    [ThreadStatic] private static double[]? t_args;

    private UserLensModel(string error) { Error = error; }

    private UserLensModel(RealExpression u)
    {
        U = u;
        var gx = u.Derivative(0); var gy = u.Derivative(1); var gz = u.Derivative(2);
        Laplacian = u.Laplacian();
        _u = u.Compile(); _gx = gx.Compile(); _gy = gy.Compile(); _gz = gz.Compile(); _lap = Laplacian.Compile();
    }

    private static readonly ConcurrentDictionary<string, UserLensModel> s_cache = new();

    /// <summary>The model for <paramref name="source"/> (cached); <see cref="Error"/> when it does not parse.</summary>
    public static UserLensModel For(string? source)
    {
        string key = source ?? "";
        if (s_cache.TryGetValue(key, out var m)) return m;
        if (s_cache.Count > 64) s_cache.Clear();
        UserLensModel built;
        if (!RealExpression.TryParse(source, Variables, out var e, out var err)) built = new UserLensModel("U(x, y, z): " + err);
        else
        {
            try { built = new UserLensModel(e!); }
            catch (Exception ex) { built = new UserLensModel("Could not compile U: " + ex.Message); }
        }
        return s_cache[key] = built;
    }

    /// <summary>U and ∇U at (x, y, z).</summary>
    public double Eval(double x, double y, double z, out double gx, out double gy, out double gz)
    {
        var a = t_args ??= new double[3];
        a[0] = x; a[1] = y; a[2] = z;
        gx = _gx(a); gy = _gy(a); gz = _gz(a);
        return _u(a);
    }

    /// <summary>∇²U at (x, y, z) (symbolic).</summary>
    public double LaplacianAt(double x, double y, double z)
    {
        var a = t_args ??= new double[3];
        a[0] = x; a[1] = y; a[2] = z;
        return _lap(a);
    }
}
