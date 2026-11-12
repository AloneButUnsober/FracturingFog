// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Calculators/MathPlane3DOrbit.cs
//
// #1732 (Math Plane 3D fractal bridge B2) — the orbit of a point under a 3D fractal's own
// iteration, for the scene statement
//   orbit NAME TYPE x y z [steps N] [KEY=VALUE ...] [options]
// Design: Docs/Technical/MathPlane3D-FractalBridge-DesignPlan.md §3.2.
//
// Each family's step is an exact CPU twin of its calculator's loop, with the parameters
// derived as RaymarchMeshSampler.For derives them (the render's DE). Kept in step by tests that
// compare against the calculators' own code: the orbit-trap estimators (Mandelbulb, Mandelbox,
// quaternion Julia / Mandelbrot: the closest the orbit passes to the origin) and
// KifsCalculator.ProbeDE (the KIFS DE is read off the orbit's last point). If a calculator's
// loop changes, change its twin here; the tests fail until they agree.
//   Mandelbulb   z0 = c; z -> triplex power of z + c (White / Nylander); stops once |z| > 2.
//   Mandelbox    z0 = c; box fold, sphere fold, z -> s z + c (Lowe); stops once |z|^2 > bailout.
//   Kifs         z0 = p; the fold kind's mirrors, then the scale about the offset (Knighty);
//                runs the full iteration count (Dodecahedron / Mandelbox-rot stop at their
//                escape radius, as their DEs do).
//   Quaternion Julia       q0 = (x, y, z, sliceW); q -> q^2 + c (quaternion Julia sets: Hart,
//                          Sandin & Kauffman, "Ray tracing deterministic 3-D fractals", 1989);
//                          stops once |q|^2 > bailout.
//   Quaternion Mandelbrot  c = (x, y, z, sliceW), q0 = 0; q -> q^2 + c; same stop.
// The quaternion orbits are 4D; the scene draws (x, y, z) and drops w.
//
// Axes: the calculators work in y-up object space; Math Plane 3D is z up. The start point is
// given in scene axes and taken to object space with (x, y, z) -> (x, z, -y); orbit points come
// back with (x, y, z) -> (x, -z, y), the rotation the B1 fractal object uses, so an orbit and a
// fractal object of the same type and settings line up.
//
// Drawing: the orbit is clipped to a display ball of radius min(escape radius, 1.5 x the
// fractal's mesh range), so one escaping step (a Mandelbulb's |z| can reach 2^8) does not
// stretch the scene; Escaped says whether the orbit reached the family's escape test.

using System;
using System.Collections.Generic;
using FracturingFog.Export;
using FracturingFog.Models;

namespace FracturingFog;

/// <summary>#1732 — an orbit in object axes (4 components; w is 0 for the 3D families).</summary>
public sealed class MathPlane3DOrbitResult
{
    public List<(double X, double Y, double Z, double W)> Points { get; } = new();
    /// <summary>True when the family's escape test stopped the orbit.</summary>
    public bool Escaped { get; internal set; }
    /// <summary>The running derivative where the family's DE keeps one (Mandelbox-rot KIFS), else 1.</summary>
    public double Dr { get; internal set; } = 1.0;
    /// <summary>Iterations run (the loop counter when it stopped).</summary>
    public int Iterations { get; internal set; }
}

/// <summary>#1732 — what the scene draws for an orbit (scene axes).</summary>
public sealed class MathPlane3DOrbitPath
{
    /// <summary>Every orbit point, in scene axes (w dropped).</summary>
    public IReadOnlyList<(double X, double Y, double Z)> Points { get; init; } = Array.Empty<(double, double, double)>();
    /// <summary>The orbit clipped to the display ball: polylines.</summary>
    public List<List<(double X, double Y, double Z)>> Pieces { get; init; } = new();
    public bool Escaped { get; init; }
    public double DisplayRadius { get; init; }
    public FractalType Type { get; init; }
}

public static class MathPlane3DOrbit
{
    /// <summary>Scene (z up) to the calculators' object axes (y up).</summary>
    public static (double X, double Y, double Z) ToObjectAxes((double X, double Y, double Z) p) => (p.X, p.Z, -p.Y);

    /// <summary>The orbit of an orbit object, or null with <paramref name="error"/>.</summary>
    public static MathPlane3DOrbitPath? Build(MathPlaneObject o, out string? error)
    {
        if (!MathPlane3DScene.IsOrbit(o)) { error = $"orbit {o.Name}: not an orbit"; return null; }
        if (!MathPlane3DFractalMesher.TryParameters(o, out var type, out var p, out error)) return null;
        if (!((IList<FractalType>)MathPlane3DScene.OrbitTypes).Contains(type))
        { error = $"orbit {o.Name}: {type} has no orbit here"; return null; }
        var start = ToObjectAxes((o.Coords[0], o.Coords[1], o.Coords[2]));
        int cap = o.Mesh > 0 ? o.Mesh : int.MaxValue;
        var r = Run(type, p, start, cap);
        double display = Math.Min(EscapeRadius(type, p), 1.5 * RaymarchMeshSampler.SuggestedRange(type, p));
        var pts = new List<(double X, double Y, double Z)>(r.Points.Count);
        foreach (var q in r.Points) pts.Add(MathPlane3DFractalMesher.ToSceneAxes((q.X, q.Y, q.Z)));
        return new MathPlane3DOrbitPath
        {
            Points = pts, Pieces = Clip(pts, display), Escaped = r.Escaped, DisplayRadius = display, Type = type,
        };
    }

    /// <summary>Null when <paramref name="o"/> is a valid orbit object, else the reason.</summary>
    public static string? Validate(MathPlaneObject o) => Build(o, out var e) is null ? e : null;

    /// <summary>The radius past which the family's loop stops (infinity for the KIFS folds that run
    /// their full count).</summary>
    public static double EscapeRadius(FractalType type, FractalParameters p) => type switch
    {
        FractalType.Mandelbulb => 2.0,
        FractalType.Mandelbox => Math.Max(16.0, p.MandelboxBailout),
        FractalType.QuaternionJulia => Math.Sqrt(Math.Max(4.0, p.QJuliaBailout)),
        FractalType.QuaternionMandelbrot => Math.Sqrt(Math.Max(4.0, p.QMandelBailout)),
        _ => double.PositiveInfinity,
    };

    /// <summary>The orbit of <paramref name="start"/> (object axes) under <paramref name="type"/>'s
    /// iteration with parameters <paramref name="p"/>, at most <paramref name="cap"/> steps beyond
    /// the family's own iteration count's limit.</summary>
    public static MathPlane3DOrbitResult Run(FractalType type, FractalParameters p, (double X, double Y, double Z) start, int cap = int.MaxValue)
    {
        var r = new MathPlane3DOrbitResult();
        switch (type)
        {
            case FractalType.Mandelbulb: Mandelbulb(r, start, p.BulbPower, Math.Min(cap, Math.Max(2, p.BulbIterations))); break;
            case FractalType.Mandelbox:
            {
                double fixedR = Math.Max(1e-3, p.MandelboxFixedRadius), minR = Math.Max(1e-3, p.MandelboxMinRadius);
                double bail = Math.Max(16.0, p.MandelboxBailout);
                Mandelbox(r, start, p.MandelboxScale, fixedR * fixedR, minR * minR, bail * bail,
                          Math.Min(cap, Math.Max(2, p.MandelboxIterations)));
                break;
            }
            case FractalType.Kifs:
            {
                var fold = p.KifsFold;
                double scale = KifsCalculator.ResolveScale(fold, p.KifsScale);
                var (ox, oy, oz) = KifsCalculator.FoldOffset(fold, p.KifsOffsetX, p.KifsOffsetY, p.KifsOffsetZ);
                Kifs(r, fold, start, scale, ox, oy, oz, Math.Min(cap, Math.Max(2, p.KifsIterations)));
                break;
            }
            case FractalType.QuaternionJulia:
                Quaternion(r, (start.X, start.Y, start.Z, p.QJuliaSliceW), (p.QJuliaCX, p.QJuliaCY, p.QJuliaCZ, p.QJuliaCW),
                           Math.Max(4.0, p.QJuliaBailout), Math.Min(cap, Math.Max(2, p.QJuliaIterations)));
                break;
            case FractalType.QuaternionMandelbrot:
                Quaternion(r, (0, 0, 0, 0), (start.X, start.Y, start.Z, p.QMandelSliceW),
                           Math.Max(4.0, p.QMandelBailout), Math.Min(cap, Math.Max(2, p.QMandelIterations)));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(type), type, "no orbit for this type");
        }
        return r;
    }

    // Twin of MandelbulbCalculator.MandelbulbDE / MandelbulbDe.OrbitTrap.
    private static void Mandelbulb(MathPlane3DOrbitResult res, (double X, double Y, double Z) c, double power, int iter)
    {
        double x = c.X, y = c.Y, z = c.Z;
        double zx = x, zy = y, zz = z;
        res.Points.Add((zx, zy, zz, 0));
        int i = 0;
        for (; i < iter; i++)
        {
            double r = Math.Sqrt(zx * zx + zy * zy + zz * zz);
            if (r > 2.0) { res.Escaped = true; break; }
            double theta = Math.Acos(zz / r);
            double phi = Math.Atan2(zy, zx);
            double rPow = Math.Pow(r, power);
            double newTheta = theta * power;
            double newPhi = phi * power;
            double sinT = Math.Sin(newTheta);
            zx = rPow * sinT * Math.Cos(newPhi) + x;
            zy = rPow * sinT * Math.Sin(newPhi) + y;
            zz = rPow * Math.Cos(newTheta) + z;
            res.Points.Add((zx, zy, zz, 0));
        }
        res.Iterations = i;
    }

    // Twin of MandelboxCalculator.MandelboxDE / MandelboxTrap.
    private static void Mandelbox(MathPlane3DOrbitResult res, (double X, double Y, double Z) c,
                                  double scale, double fixedR2, double minR2, double bailout2, int iter)
    {
        double cx = c.X, cy = c.Y, cz = c.Z;
        double zx = cx, zy = cy, zz = cz;
        res.Points.Add((zx, zy, zz, 0));
        int i = 0;
        for (; i < iter; i++)
        {
            if (zx > 1.0) zx = 2.0 - zx; else if (zx < -1.0) zx = -2.0 - zx;
            if (zy > 1.0) zy = 2.0 - zy; else if (zy < -1.0) zy = -2.0 - zy;
            if (zz > 1.0) zz = 2.0 - zz; else if (zz < -1.0) zz = -2.0 - zz;
            double r2 = zx * zx + zy * zy + zz * zz;
            if (r2 < minR2) { double f = fixedR2 / minR2; zx *= f; zy *= f; zz *= f; }
            else if (r2 < fixedR2) { double f = fixedR2 / r2; zx *= f; zy *= f; zz *= f; }
            zx = scale * zx + cx; zy = scale * zy + cy; zz = scale * zz + cz;
            res.Points.Add((zx, zy, zz, 0));
            if (zx * zx + zy * zy + zz * zz > bailout2) { res.Escaped = true; i++; break; }
        }
        res.Iterations = i;
    }

    // Twins of KifsCalculator's MengerDE / SierpDE / OctaDE / DodecaDE / MandelboxRotDE loops.
    private static void Kifs(MathPlane3DOrbitResult res, KifsFoldKind fold, (double X, double Y, double Z) p0,
                             double scale, double ox, double oy, double oz, int iter)
    {
        double zx = p0.X, zy = p0.Y, zz = p0.Z;
        res.Points.Add((zx, zy, zz, 0));
        double k = scale - 1.0;
        double offX = k * ox, offY = k * oy, offZ = k * oz;
        double t;
        int i = 0;
        switch (fold)
        {
            case KifsFoldKind.Sierpinski:
                for (; i < iter; i++)
                {
                    if (zx + zy < 0) { t = -zy; zy = -zx; zx = t; }
                    if (zx + zz < 0) { t = -zz; zz = -zx; zx = t; }
                    if (zy + zz < 0) { t = -zz; zz = -zy; zy = t; }
                    zx = scale * zx - offX; zy = scale * zy - offY; zz = scale * zz - offZ;
                    res.Points.Add((zx, zy, zz, 0));
                }
                break;
            case KifsFoldKind.Octahedron:
                for (; i < iter; i++)
                {
                    if (zx + zy < 0) { t = -zy; zy = -zx; zx = t; }
                    if (zx + zz < 0) { t = -zz; zz = -zx; zx = t; }
                    if (zx - zy < 0) { t = zy; zy = zx; zx = t; }
                    if (zx - zz < 0) { t = zz; zz = zx; zx = t; }
                    zx = scale * zx - offX; zy = scale * zy - offY; zz = scale * zz - offZ;
                    res.Points.Add((zx, zy, zz, 0));
                }
                break;
            case KifsFoldKind.Dodecahedron:
            {
                var n = KifsCalculator.DodecaNormals;
                for (; i < iter; i++)
                {
                    for (int rep = 0; rep < 3; rep++)
                        for (int m = 0; m < 3; m++)
                        {
                            double d = zx * n[m].X + zy * n[m].Y + zz * n[m].Z;
                            if (d < 0) { zx -= 2 * d * n[m].X; zy -= 2 * d * n[m].Y; zz -= 2 * d * n[m].Z; }
                        }
                    zx = scale * zx - offX; zy = scale * zy - offY; zz = scale * zz - offZ;
                    res.Points.Add((zx, zy, zz, 0));
                    if (zx * zx + zy * zy + zz * zz > 1e9) { res.Escaped = true; break; }
                }
                break;
            }
            case KifsFoldKind.MandelboxRot:
            {
                double ccx = p0.X + ox, ccy = p0.Y + oy, ccz = p0.Z + oz;
                double dr = 1.0, absScale = Math.Abs(scale);
                double cosR = Math.Cos(KifsCalculator.MandelboxRotAngle), sinR = Math.Sin(KifsCalculator.MandelboxRotAngle);
                for (; i < iter; i++)
                {
                    if (zx > 1.0) zx = 2.0 - zx; else if (zx < -1.0) zx = -2.0 - zx;
                    if (zy > 1.0) zy = 2.0 - zy; else if (zy < -1.0) zy = -2.0 - zy;
                    if (zz > 1.0) zz = 2.0 - zz; else if (zz < -1.0) zz = -2.0 - zz;
                    double r2 = zx * zx + zy * zy + zz * zz;
                    if (r2 < 0.25) { zx *= 4.0; zy *= 4.0; zz *= 4.0; dr *= 4.0; }
                    else if (r2 < 1.0) { double f = 1.0 / r2; zx *= f; zy *= f; zz *= f; dr *= f; }
                    double nx = cosR * zx + sinR * zz, nz = -sinR * zx + cosR * zz;
                    zx = nx; zz = nz;
                    zx = scale * zx + ccx; zy = scale * zy + ccy; zz = scale * zz + ccz;
                    dr = dr * absScale + 1.0;
                    res.Points.Add((zx, zy, zz, 0));
                    if (zx * zx + zy * zy + zz * zz > 1e6) { res.Escaped = true; break; }
                }
                res.Dr = dr;
                break;
            }
            default:   // Menger
            {
                double mirrorThresh = -0.5 * offZ;
                for (; i < iter; i++)
                {
                    zx = Math.Abs(zx); zy = Math.Abs(zy); zz = Math.Abs(zz);
                    if (zx - zy < 0) { t = zx; zx = zy; zy = t; }
                    if (zx - zz < 0) { t = zx; zx = zz; zz = t; }
                    if (zy - zz < 0) { t = zy; zy = zz; zz = t; }
                    zx = scale * zx - offX; zy = scale * zy - offY; zz = scale * zz;
                    if (zz < mirrorThresh) zz += offZ;
                    res.Points.Add((zx, zy, zz, 0));
                }
                break;
            }
        }
        res.Iterations = i;
    }

    // Twin of QuatJuliaCalculator / QuatMandelbrotCalculator's q^2 + c loops (and their traps).
    private static void Quaternion(MathPlane3DOrbitResult res, (double X, double Y, double Z, double W) q0,
                                   (double X, double Y, double Z, double W) c, double bailout2, int iter)
    {
        double qx = q0.X, qy = q0.Y, qz = q0.Z, qw = q0.W;
        res.Points.Add((qx, qy, qz, qw));
        int i = 0;
        for (; i < iter; i++)
        {
            double nqx = qx * qx - qy * qy - qz * qz - qw * qw;
            double nqy = 2.0 * qx * qy;
            double nqz = 2.0 * qx * qz;
            double nqw = 2.0 * qx * qw;
            qx = nqx + c.X; qy = nqy + c.Y; qz = nqz + c.Z; qw = nqw + c.W;
            res.Points.Add((qx, qy, qz, qw));
            if (qx * qx + qy * qy + qz * qz + qw * qw > bailout2) { res.Escaped = true; i++; break; }
        }
        res.Iterations = i;
    }

    /// <summary>The polyline through <paramref name="pts"/> clipped to the ball |p| &lt;= <paramref name="radius"/>.</summary>
    public static List<List<(double X, double Y, double Z)>> Clip(IReadOnlyList<(double X, double Y, double Z)> pts, double radius)
    {
        var pieces = new List<List<(double X, double Y, double Z)>>();
        List<(double X, double Y, double Z)>? cur = null;
        double r2 = radius * radius;
        static bool Finite((double X, double Y, double Z) q) => double.IsFinite(q.X) && double.IsFinite(q.Y) && double.IsFinite(q.Z);
        if (pts.Count == 1 && Finite(pts[0]) && Len2(pts[0]) <= r2) pieces.Add(new() { pts[0] });
        for (int k = 1; k < pts.Count; k++)
        {
            var a = pts[k - 1]; var b = pts[k];
            if (!Finite(a) || !Finite(b)) { cur = null; continue; }
            // |a + t (b - a)|^2 = r^2  ->  A t^2 + 2 B t + C = 0
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            double A = dx * dx + dy * dy + dz * dz, B = a.X * dx + a.Y * dy + a.Z * dz, C = Len2(a) - r2;
            double t0, t1;
            if (A < 1e-300) { if (C > 0) { cur = null; continue; } t0 = 0; t1 = 1; }
            else
            {
                double disc = B * B - A * C;
                if (disc < 0) { cur = null; continue; }
                double s = Math.Sqrt(disc);
                t0 = Math.Max(0, (-B - s) / A); t1 = Math.Min(1, (-B + s) / A);
                if (t0 > t1) { cur = null; continue; }
            }
            var p0 = t0 <= 0 ? a : (a.X + t0 * dx, a.Y + t0 * dy, a.Z + t0 * dz);
            var p1 = t1 >= 1 ? b : (a.X + t1 * dx, a.Y + t1 * dy, a.Z + t1 * dz);
            if (cur == null || t0 > 0) { cur = new() { p0 }; pieces.Add(cur); }
            cur.Add(p1);
            if (t1 < 1) cur = null;
        }
        return pieces;
    }

    private static double Len2((double X, double Y, double Z) q) => q.X * q.X + q.Y * q.Y + q.Z * q.Z;
}
