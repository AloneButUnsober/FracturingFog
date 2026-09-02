// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/ReliefMotionVector.cs
//
// Roadmap slice S1 (3D-Rendering-Roadmap.md, parent #389 / #398): the
// motion-vector AOV. The raymarch already resolves, per primary hit, the world
// position of the surface point (camera origin + ray dir · depth). A motion
// vector is where that same surface point appeared on screen in the PREVIOUS
// frame — the per-pixel screen-space displacement a temporal denoiser (SVGF, the
// S4 tail) reprojects along, and the velocity buffer per-object motion blur
// integrates over. FF discards it today; this promotes it to a first-class,
// deterministic AOV.
//
// This first slice is the pure operator + the AOV channel it fills — the same
// operator-first cadence every S1–S5 slice used (ViewTransformOps, CameraDof,
// DielectricOps, AtrousDenoiser all shipped their math before the render wiring).
// A `CameraView` captures the oblique relief camera as the ray generator sees it
// (origin + orthonormal right/up/fwd basis + fov scale + aspect + pan); `Project`
// is the exact inverse of that ray generation (world point → screen pixel); and
// `ScreenMotion` differences the previous-frame projection against the current
// pixel. Feeding the render's OWN camera + the previous frame's camera into a
// `ReliefAovBuffers.Motion` channel is the wiring follow-up; with the previous
// camera equal to the current one the motion is exactly zero (a still frame).

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Motion-vector AOV math for the oblique relief raymarch (roadmap S1).</summary>
public static class ReliefMotionVector
{
    /// <summary>The oblique relief camera as the ray generator sees it. The ray
    /// through pixel (x,y) is <c>dir = right·u + up·v + fwd</c> with
    /// <c>u = (2x/w − 1)·FovScale·Aspect + PanU</c> and
    /// <c>v = (1 − 2y/h)·FovScale + PanV</c>; right/up/fwd are orthonormal and fwd
    /// is the (normalized) centre view direction. <see cref="Project"/> inverts
    /// exactly this mapping.</summary>
    public readonly struct CameraView
    {
        public readonly double PosX, PosY, PosZ;
        public readonly double RX, RY, RZ;   // right basis
        public readonly double UX, UY, UZ;   // up basis
        public readonly double FX, FY, FZ;   // forward (view) basis
        public readonly double FovScale, Aspect, PanU, PanV;

        public CameraView(
            double posX, double posY, double posZ,
            double rx, double ry, double rz,
            double ux, double uy, double uz,
            double fx, double fy, double fz,
            double fovScale, double aspect, double panU, double panV)
        {
            PosX = posX; PosY = posY; PosZ = posZ;
            RX = rx; RY = ry; RZ = rz;
            UX = ux; UY = uy; UZ = uz;
            FX = fx; FY = fy; FZ = fz;
            FovScale = fovScale; Aspect = aspect; PanU = panU; PanV = panV;
        }
    }

    /// <summary>Project a world point to screen pixel coordinates under
    /// <paramref name="cam"/> — the exact inverse of the ray generation. Returns
    /// pixel-centre coordinates (a hit at pixel (x,y) reprojects to (x,y) under its
    /// own camera). <paramref name="behind"/> is true when the point is on or
    /// behind the camera plane (forward component ≤ 0), where the projection is
    /// undefined; callers treat that as "no motion". The result can lie outside
    /// [0,w)×[0,h) (the point left the frame) — that is valid and preserved.</summary>
    public static (double px, double py) Project(
        double wx, double wy, double wz, in CameraView cam, int w, int h, out bool behind)
    {
        double dx = wx - cam.PosX, dy = wy - cam.PosY, dz = wz - cam.PosZ;
        // Decompose D onto the orthonormal basis: D = right·a + up·b + fwd·c.
        double a = dx * cam.RX + dy * cam.RY + dz * cam.RZ;
        double b = dx * cam.UX + dy * cam.UY + dz * cam.UZ;
        double c = dx * cam.FX + dy * cam.FY + dz * cam.FZ;
        if (c <= 1e-12)
        {
            behind = true;
            return (0.0, 0.0);
        }
        behind = false;
        // Along the ray, D ∝ (u, v, 1)·c, so the pixel's ray-space coords are:
        double u = a / c;
        double v = b / c;
        // Invert the ray generator's pixel → (u,v) map. The generator samples pixel
        // (x,y) at its centre (x+0.5, y+0.5), so the projection is in that same
        // centre coordinate space (a hit sampled at (x+0.5,y+0.5) reprojects there).
        double sx = ((u - cam.PanU) / (cam.FovScale * cam.Aspect) + 1.0) * 0.5 * w;
        double sy = (1.0 - (v - cam.PanV) / cam.FovScale) * 0.5 * h;
        return (sx, sy);
    }

    /// <summary>Screen-space motion vector for a surface point seen at the current
    /// pixel (<paramref name="curPx"/>, <paramref name="curPy"/>): where that point
    /// projected in the PREVIOUS frame minus where it is now — i.e. the
    /// displacement that maps the current pixel back to its previous-frame
    /// location (the reprojection convention SVGF uses). Returns (0,0) when the
    /// point is behind the previous camera. Identical current/previous cameras give
    /// exactly (0,0) for an in-frame hit.</summary>
    public static (double du, double dv) ScreenMotion(
        double wx, double wy, double wz,
        double curPx, double curPy,
        in CameraView prev, int w, int h)
    {
        var (ppx, ppy) = Project(wx, wy, wz, in prev, w, h, out bool behind);
        if (behind) return (0.0, 0.0);
        return (ppx - curPx, ppy - curPy);
    }
}
