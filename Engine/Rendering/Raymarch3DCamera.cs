// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/Raymarch3DCamera.cs
//
// #1733 (Math Plane 3D fractal bridge B3) — the exact camera a 3D raymarcher cast its rays
// with in its last frame, so overlays (B4) can draw world points where the fractal is drawn.
// Design: Docs/Technical/MathPlane3D-FractalBridge-DesignPlan.md §3.3.
//
// Every raymarcher (Mandelbulb, Mandelbox, KIFS, quaternion Julia / Mandelbrot, bicomplex,
// coquaternion, Kleinian, Julibrot / DualOrbitVolume, User Bulb) casts the ray of pixel (x, y)
// in a W x H frame as
//   u = (2 (x + 1/2) / W - 1) * fovScale * aspect + panU
//   v = (1 - 2 (y + 1/2) / H) * fovScale + panV
//   dir = normalize(F + u R + v U)        from the eye
// with the eye after the distance clamp and the stereo offset, fovScale after the zoom lens,
// aspect = W / H, and y up in object space. Those exact values already go to
// ScreenSpacePost.Froxel3DViewOf (#1079) and to the GPU kernels' parameters, so the camera is
// built from that view: one source of truth per calculator. Coordinates here are continuous
// pixel positions: the centre of pixel (i, j) is (i + 1/2, j + 1/2).

using System;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Rendering;

/// <summary>#1733 — a raymarcher's pinhole camera for one frame (object space, y up).</summary>
public readonly record struct Raymarch3DCamera(
    (double X, double Y, double Z) Eye,
    (double X, double Y, double Z) Forward,
    (double X, double Y, double Z) Right,
    (double X, double Y, double Z) Up,
    double FovScale, double PanU, double PanV, int Width, int Height)
{
    public double Aspect => (double)Width / Math.Max(1, Height);

    /// <summary>The camera a calculator handed to <see cref="ScreenSpacePost.Froxel3DViewOf"/>.</summary>
    public static Raymarch3DCamera From(in ScreenSpacePost.Froxel3DView view, int width, int height)
    {
        var c = view.Camera;
        return new Raymarch3DCamera((c.PosX, c.PosY, c.PosZ), (c.Fx, c.Fy, c.Fz), (c.Rx, c.Ry, c.Rz), (c.Ux, c.Uy, c.Uz),
                                    c.TanHalf, view.PanU, view.PanV, width, height);
    }

    /// <summary>The unit ray direction through continuous pixel position (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public (double X, double Y, double Z) RayDirection(double x, double y)
    {
        double u = (2.0 * x / Width - 1.0) * FovScale * Aspect + PanU;
        double v = (1.0 - 2.0 * y / Height) * FovScale + PanV;
        double dx = Right.X * u + Up.X * v + Forward.X;
        double dy = Right.Y * u + Up.Y * v + Forward.Y;
        double dz = Right.Z * u + Up.Z * v + Forward.Z;
        double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return (dx / l, dy / l, dz / l);
    }

    /// <summary>The point <paramref name="distance"/> along the ray through (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public (double X, double Y, double Z) PointAt(double x, double y, double distance)
    {
        var d = RayDirection(x, y);
        return (Eye.X + d.X * distance, Eye.Y + d.Y * distance, Eye.Z + d.Z * distance);
    }

    /// <summary>The continuous pixel position of world point <paramref name="p"/> and its ray distance
    /// from the eye (the depth a raymarcher publishes); null behind the eye.</summary>
    public (double X, double Y, double Distance)? Project((double X, double Y, double Z) p)
    {
        double dx = p.X - Eye.X, dy = p.Y - Eye.Y, dz = p.Z - Eye.Z;
        double f = dx * Forward.X + dy * Forward.Y + dz * Forward.Z;
        if (!(f > 1e-12)) return null;
        double u = (dx * Right.X + dy * Right.Y + dz * Right.Z) / f;
        double v = (dx * Up.X + dy * Up.Y + dz * Up.Z) / f;
        double x = ((u - PanU) / (FovScale * Aspect) + 1.0) * 0.5 * Width;
        double y = (1.0 - (v - PanV) / FovScale) * 0.5 * Height;
        return (x, y, Math.Sqrt(dx * dx + dy * dy + dz * dz));
    }
}

/// <summary>#1733 — a calculator that exposes the camera of its last frame.</summary>
public interface IRaymarch3DCameraSource
{
    /// <summary>The camera of the last frame (null before the first).</summary>
    Raymarch3DCamera? LastCamera { get; }
}
