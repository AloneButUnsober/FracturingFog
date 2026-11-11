// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlane3DCamera.cs
//
// #1713 (Math Plane 3D T1) — the orbit camera of a Math Plane 3D view: z up,
// eye = target + (distance / Zoom) · (sinφ cosθ, sinφ sinθ, cosφ), looking at the
// target, perspective with a 45° vertical field of view. #1716 (T4) moved it here
// from the calculator so the Control Center's editor can project and un-project
// pointer positions exactly as the renderer does.

using System;
using FracturingFog.Models;

namespace FracturingFog;

/// <summary>#1713 — a point projected to the screen: pixel position and 1/z (view depth).</summary>
public readonly record struct Projected3D(double X, double Y, double InvZ);

/// <summary>#1713 — the orbit camera of a Math Plane 3D view.</summary>
public readonly record struct MathPlane3DCamera(
    (double X, double Y, double Z) Eye,
    (double X, double Y, double Z) Right,
    (double X, double Y, double Z) Up,
    (double X, double Y, double Z) Forward,
    double Focal, int Width, int Height)
{
    /// <summary>Near-plane distance in view space; geometry closer is clipped.</summary>
    public const double Near = 1e-3;

    public const double VerticalFovDegrees = 45.0;

    /// <summary>The camera for <paramref name="p"/> at view <paramref name="zoom"/>, for a
    /// <paramref name="w"/> × <paramref name="h"/> image.</summary>
    public static MathPlane3DCamera For(FractalParameters p, double zoom, int w, int h)
    {
        double dist = System.Math.Max(1e-6, p.MathPlane3DCameraDistance) / System.Math.Max(1e-9, zoom);
        double th = p.MathPlane3DCameraTheta, ph = System.Math.Clamp(p.MathPlane3DCameraPhi, 1e-4, System.Math.PI - 1e-4);
        var target = (X: p.MathPlane3DTargetX, Y: p.MathPlane3DTargetY, Z: p.MathPlane3DTargetZ);
        var eye = (X: target.X + dist * System.Math.Sin(ph) * System.Math.Cos(th),
                   Y: target.Y + dist * System.Math.Sin(ph) * System.Math.Sin(th),
                   Z: target.Z + dist * System.Math.Cos(ph));
        var fwd = Normalize((target.X - eye.X, target.Y - eye.Y, target.Z - eye.Z));
        var right = Normalize(Cross(fwd, (0, 0, 1)));
        var up = Cross(right, fwd);
        double focal = h * 0.5 / System.Math.Tan(VerticalFovDegrees * System.Math.PI / 360.0);
        return new MathPlane3DCamera(eye, right, up, fwd, focal, w, h);
    }

    /// <summary>View-space (right, up, forward) coordinates of a world point.</summary>
    public (double X, double Y, double Z) View(double x, double y, double z)
    {
        double dx = x - Eye.X, dy = y - Eye.Y, dz = z - Eye.Z;
        return (dx * Right.X + dy * Right.Y + dz * Right.Z,
                dx * Up.X + dy * Up.Y + dz * Up.Z,
                dx * Forward.X + dy * Forward.Y + dz * Forward.Z);
    }

    /// <summary>A view-space point to the screen; null behind the near plane.</summary>
    public Projected3D? FromView(double vx, double vy, double vz)
        => vz <= Near ? null
         : new Projected3D(Width * 0.5 + Focal * vx / vz, Height * 0.5 - Focal * vy / vz, 1.0 / vz);

    /// <summary>A world point to the screen; null behind the near plane.</summary>
    public Projected3D? Project(double x, double y, double z)
    {
        var (vx, vy, vz) = View(x, y, z);
        return FromView(vx, vy, vz);
    }

    /// <summary>#1716 — the world direction (not normalised) of the ray through screen point
    /// (<paramref name="px"/>, <paramref name="py"/>): the inverse of <see cref="FromView"/>
    /// at view depth 1.</summary>
    public (double X, double Y, double Z) RayDirection(double px, double py)
    {
        double vx = (px - Width * 0.5) / Focal, vy = -(py - Height * 0.5) / Focal;
        return (Right.X * vx + Up.X * vy + Forward.X,
                Right.Y * vx + Up.Y * vy + Forward.Y,
                Right.Z * vx + Up.Z * vy + Forward.Z);
    }

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        double l = System.Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return l > 0 ? (v.X / l, v.Y / l, v.Z / l) : (0, 0, 0);
    }
}
