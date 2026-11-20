// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MajumdarPapapetrou.cs (#1923, black hole shadows #1922)
//
// Null geodesics in the Majumdar-Papapetrou (MP) spacetime: N extremally charged
// black holes held in static equilibrium (Majumdar 1947; Papapetrou 1947). In
// isotropic Cartesian coordinates
//     ds² = -U⁻² dt² + U² (dx² + dy² + dz²),   U = 1 + Σᵢ mᵢ / |x - xᵢ|
// (Shipley & Dolan 2016, Eqs. 1 and 3; Yumoto, Nitta, Chiba & Sugiyama 2012, Eq. 1).
// The Hamiltonian is H = ½ (-U² p_t² + U⁻² |p|²) (Shipley & Dolan §II.1); with p_t = -1
// and null rays (H = 0), |p| = U². Hamilton's equations:
//     dx/dλ = p / U²,
//     dp/dλ = -∂H/∂x = ∇U (U + |p|² / U³)
// which on shell (|p|² = U⁴) is ∇(U²), Shipley & Dolan Eq. 6. The full form is kept so
// H stays a meaningful conservation check.
//
// Coordinate speed |dx/dλ| = |p| / U² = 1 on shell, so λ is the coordinate path length.
// Each horizon is the point xᵢ in these coordinates (a null surface of finite area, a
// coordinate singularity; Shipley & Dolan §IV.1). Neither paper states a numerical
// capture rule, so FF captures a ray once |x - xᵢ| < CaptureFraction · mᵢ: well inside
// the single-hole photon sphere (isotropic r = m, i.e. areal R = 2m; Yumoto et al. §II.A).
//
// Observer: a distant camera. Rays leave a plane orthogonal to the view direction at
// distance D, travelling inward and parallel, so the image-plane offset (u, v) is the
// ray's impact parameter (Shipley & Dolan §III.2: a planar null congruence through a
// surface centred at distance r_max, typically 50M; θ measured from the -z axis). The
// celestial coordinates of Yumoto et al. (Eq. 12) are the D → ∞ limit of (u, v).

using System;
using System.Runtime.CompilerServices;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>One MP black hole: position and mass (geometric units, G = c = 1).</summary>
public readonly record struct MpHole(double X, double Y, double Z, double M);

/// <summary>The MP null-geodesic system (6-dim state x, y, z, px, py, pz; p_t = -1).
/// A struct so <see cref="ScatteringIntegrator.Trace{TSystem}"/> inlines it.</summary>
public struct MpNullSystem : IScatteringSystem
{
    public const int MaxHoles = 4;

    // Up to four holes, unrolled into fields (no array access on the hot path).
    private readonly int _n;
    private readonly double _x0, _y0, _z0, _m0, _c0;
    private readonly double _x1, _y1, _z1, _m1, _c1;
    private readonly double _x2, _y2, _z2, _m2, _c2;
    private readonly double _x3, _y3, _z3, _m3, _c3;
    private readonly double _escape2;

    /// <summary>Closest approach to each hole so far (coordinate distance), for S2 channels.</summary>
    public double Min0, Min1, Min2, Min3;

    /// <summary>∫ (U² - 1) dλ: coordinate-time delay over flat space (t grows as U² per λ).</summary>
    public double Delay;

    public MpNullSystem(ReadOnlySpan<MpHole> holes, double captureFraction, double escapeRadius)
    {
        _n = Math.Clamp(holes.Length, 1, MaxHoles);
        double cf = Math.Max(1e-6, captureFraction);
        _x0 = _y0 = _z0 = _m0 = _c0 = 0; _x1 = _y1 = _z1 = _m1 = _c1 = 0;
        _x2 = _y2 = _z2 = _m2 = _c2 = 0; _x3 = _y3 = _z3 = _m3 = _c3 = 0;
        for (int i = 0; i < _n; i++)
        {
            var h = holes[i];
            double m = Math.Max(0.0, h.M);
            double c = cf * m; c *= c;   // squared capture radius
            switch (i)
            {
                case 0: _x0 = h.X; _y0 = h.Y; _z0 = h.Z; _m0 = m; _c0 = c; break;
                case 1: _x1 = h.X; _y1 = h.Y; _z1 = h.Z; _m1 = m; _c1 = c; break;
                case 2: _x2 = h.X; _y2 = h.Y; _z2 = h.Z; _m2 = m; _c2 = c; break;
                default: _x3 = h.X; _y3 = h.Y; _z3 = h.Z; _m3 = m; _c3 = c; break;
            }
        }
        _escape2 = escapeRadius * escapeRadius;
        Min0 = Min1 = Min2 = Min3 = double.PositiveInfinity;
        Delay = 0.0;
    }

    public readonly int Dimension => 6;
    public readonly int HoleCount => _n;

    /// <summary>U and ∇U at (x, y, z).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly double Potential(double x, double y, double z, out double gx, out double gy, out double gz)
    {
        double u = 1.0; gx = gy = gz = 0.0;
        Add(x - _x0, y - _y0, z - _z0, _m0, ref u, ref gx, ref gy, ref gz);
        if (_n > 1) Add(x - _x1, y - _y1, z - _z1, _m1, ref u, ref gx, ref gy, ref gz);
        if (_n > 2) Add(x - _x2, y - _y2, z - _z2, _m2, ref u, ref gx, ref gy, ref gz);
        if (_n > 3) Add(x - _x3, y - _y3, z - _z3, _m3, ref u, ref gx, ref gy, ref gz);
        return u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(double dx, double dy, double dz, double m, ref double u, ref double gx, ref double gy, ref double gz)
    {
        if (m == 0.0) return;
        double r2 = dx * dx + dy * dy + dz * dz;
        double r = Math.Sqrt(r2);
        double ir = 1.0 / r;
        u += m * ir;
        double k = -m * ir * ir * ir;   // ∇(m / r) = -m (x - xᵢ) / r³
        gx += k * dx; gy += k * dy; gz += k * dz;
    }

    /// <summary>U at (x, y, z).</summary>
    public readonly double U(double x, double y, double z) => Potential(x, y, z, out _, out _, out _);

    /// <summary>H = ½ (-U² + |p|² / U²) with p_t = -1 (zero on a null ray).</summary>
    public readonly double Hamiltonian(ReadOnlySpan<double> s)
    {
        double u = U(s[0], s[1], s[2]);
        double p2 = s[3] * s[3] + s[4] * s[4] + s[5] * s[5];
        return 0.5 * (-u * u + p2 / (u * u));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Derivative(ReadOnlySpan<double> s, Span<double> d)
    {
        double u = Potential(s[0], s[1], s[2], out double gx, out double gy, out double gz);
        double px = s[3], py = s[4], pz = s[5];
        double iu2 = 1.0 / (u * u);
        double p2 = px * px + py * py + pz * pz;
        double f = u + p2 * iu2 / u;
        d[0] = px * iu2; d[1] = py * iu2; d[2] = pz * iu2;
        d[3] = gx * f; d[4] = gy * f; d[5] = gz * f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly double Dist2(int i, double x, double y, double z)
    {
        double dx, dy, dz;
        switch (i)
        {
            case 0: dx = x - _x0; dy = y - _y0; dz = z - _z0; break;
            case 1: dx = x - _x1; dy = y - _y1; dz = z - _z1; break;
            case 2: dx = x - _x2; dy = y - _y2; dz = z - _z2; break;
            default: dx = x - _x3; dy = y - _y3; dz = z - _z3; break;
        }
        return dx * dx + dy * dy + dz * dz;
    }

    private readonly double Cap2(int i) => i switch { 0 => _c0, 1 => _c1, 2 => _c2, _ => _c3 };

    public readonly ScatterFate Classify(ReadOnlySpan<double> s, out int body)
    {
        double x = s[0], y = s[1], z = s[2];
        for (int i = 0; i < _n; i++)
        {
            if (Dist2(i, x, y, z) < Cap2(i)) { body = i; return ScatterFate.Captured; }
        }
        body = -1;
        double r2 = x * x + y * y + z * z;
        if (r2 > _escape2 && x * s[3] + y * s[4] + z * s[5] > 0.0) return ScatterFate.Escaped;
        return ScatterFate.Running;
    }

    public readonly double MaxStep(ReadOnlySpan<double> s)
    {
        // Coordinate speed is 1, so a step of h moves ~h. Never step more than a quarter of
        // the distance to the nearest hole (or 4 units far out, where nothing happens fast).
        double x = s[0], y = s[1], z = s[2];
        double near = double.PositiveInfinity;
        for (int i = 0; i < _n; i++) near = Math.Min(near, Dist2(i, x, y, z));
        double r = Math.Sqrt(near);
        return Math.Max(1e-9, Math.Min(0.25 * r + 1e-6, 4.0 + 0.05 * r));
    }

    public void Observe(ReadOnlySpan<double> s, double h)
    {
        double x = s[0], y = s[1], z = s[2];
        double d0 = Math.Sqrt(Dist2(0, x, y, z)); if (d0 < Min0) Min0 = d0;
        if (_n > 1) { double d = Math.Sqrt(Dist2(1, x, y, z)); if (d < Min1) Min1 = d; }
        if (_n > 2) { double d = Math.Sqrt(Dist2(2, x, y, z)); if (d < Min2) Min2 = d; }
        if (_n > 3) { double d = Math.Sqrt(Dist2(3, x, y, z)); if (d < Min3) Min3 = d; }
        double u = U(x, y, z);
        Delay += (u * u - 1.0) * h;   // end-point rule; a colouring channel, not physics output
    }
}

/// <summary>Hole layouts of <see cref="FractalType.BlackHoleShadow"/>.</summary>
public static class BlackHoleLayouts
{
    /// <summary>The holes of a frame from <paramref name="p"/>.
    /// Binary: M1, M2 on the z axis, coordinate separation a, centre of mass at the origin
    /// (z± = ±a·M∓/(M₊+M₋), Shipley & Dolan §II). Triangle / Square: equal masses M1 at the
    /// corners of a regular polygon of side a in the x-z plane, centred on the origin.
    /// Custom: "x,y,z,m;..." (up to four).</summary>
    public static MpHole[] Build(FractalParameters p)
    {
        double a = Math.Max(0.0, p.BlackHoleSeparation);
        double m1 = Math.Max(0.0, p.BlackHoleMass1), m2 = Math.Max(0.0, p.BlackHoleMass2);
        switch (p.BlackHoleLayout)
        {
            case BlackHoleLayout.Single:
                return new[] { new MpHole(0, 0, 0, m1) };
            case BlackHoleLayout.Triangle:
                return Polygon(3, a, m1);
            case BlackHoleLayout.Square:
                return Polygon(4, a, m1);
            case BlackHoleLayout.Custom:
            {
                var c = ParseCustom(p.BlackHoleCustom);
                if (c.Length > 0) return c;
                goto default;
            }
            default:
            {
                double mt = m1 + m2;
                if (mt <= 0) return new[] { new MpHole(0, 0, 0, 0) };
                // Hole 1 on +z, hole 2 on -z; centre of mass at the origin.
                return new[] { new MpHole(0, 0, a * m2 / mt, m1), new MpHole(0, 0, -a * m1 / mt, m2) };
            }
        }
    }

    private static MpHole[] Polygon(int n, double side, double m)
    {
        double R = side / (2.0 * Math.Sin(Math.PI / n));
        var h = new MpHole[n];
        for (int i = 0; i < n; i++)
        {
            double t = Math.PI / 2.0 + 2.0 * Math.PI * i / n;
            h[i] = new MpHole(R * Math.Cos(t), 0, R * Math.Sin(t), m);
        }
        return h;
    }

    /// <summary>"x,y,z,m;x,y,z,m;..." → holes (invalid entries skipped, at most four).</summary>
    public static MpHole[] ParseCustom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<MpHole>();
        var list = new System.Collections.Generic.List<MpHole>(4);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = part.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length != 4) continue;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (double.TryParse(f[0], System.Globalization.NumberStyles.Float, inv, out double x)
                && double.TryParse(f[1], System.Globalization.NumberStyles.Float, inv, out double y)
                && double.TryParse(f[2], System.Globalization.NumberStyles.Float, inv, out double z)
                && double.TryParse(f[3], System.Globalization.NumberStyles.Float, inv, out double m)
                && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z) && double.IsFinite(m) && m > 0)
                list.Add(new MpHole(x, y, z, m));
            if (list.Count == MpNullSystem.MaxHoles) break;
        }
        return list.ToArray();
    }
}

/// <summary>The distant camera of <see cref="FractalType.BlackHoleShadow"/>: a launch plane
/// at distance D, normal to the view direction. θ from the -z axis (Shipley & Dolan's
/// convention: θ = 90° is side-on to a binary on the z axis), φ about z.</summary>
public readonly struct MpCamera
{
    public readonly double Cx, Cy, Cz;          // plane centre (= camera position)
    public readonly double Fx, Fy, Fz;          // forward (towards the origin)
    public readonly double Rx, Ry, Rz;          // image-plane u axis
    public readonly double Ux, Uy, Uz;          // image-plane v axis
    public readonly double Distance;

    public MpCamera(double distance, double inclinationDeg, double azimuthDeg)
    {
        Distance = distance;
        double th = inclinationDeg * Math.PI / 180.0, ph = azimuthDeg * Math.PI / 180.0;
        Cx = distance * Math.Sin(th) * Math.Cos(ph);
        Cy = distance * Math.Sin(th) * Math.Sin(ph);
        Cz = -distance * Math.Cos(th);
        Fx = -Cx / distance; Fy = -Cy / distance; Fz = -Cz / distance;
        // u axis: the z axis projected onto the image plane, so a binary on z lies along u
        // (horizontal). Looking straight down z it degenerates; use x instead.
        double rx = -Fz * Fx, ry = -Fz * Fy, rz = 1.0 - Fz * Fz;
        double rl = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        if (rl < 1e-9)
        {
            rx = 1.0 - Fx * Fx; ry = -Fx * Fy; rz = -Fx * Fz;
            rl = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        }
        Rx = rx / rl; Ry = ry / rl; Rz = rz / rl;
        // v = F × R (right-handed image frame).
        Ux = Fy * Rz - Fz * Ry; Uy = Fz * Rx - Fx * Rz; Uz = Fx * Ry - Fy * Rx;
    }

    /// <summary>Launch state for image-plane point (u, v): on the plane, momentum along the
    /// view direction with |p| = U² (null, p_t = -1).</summary>
    public void Launch(in MpNullSystem sys, double u, double v, Span<double> s)
    {
        double x = Cx + u * Rx + v * Ux, y = Cy + u * Ry + v * Uy, z = Cz + u * Rz + v * Uz;
        double uu = sys.U(x, y, z);
        double pm = uu * uu;
        s[0] = x; s[1] = y; s[2] = z;
        s[3] = pm * Fx; s[4] = pm * Fy; s[5] = pm * Fz;
    }
}
