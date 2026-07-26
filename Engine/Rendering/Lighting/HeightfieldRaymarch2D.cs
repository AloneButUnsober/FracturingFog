// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/HeightfieldRaymarch2D.cs
//
// #102 Phase 2 — oblique heightfield RAYMARCH for escape-time 2D fractals.
//
// Phase 1 (HeightfieldRelief2D) lights the smooth-count height field in screen
// space: real cast shadows, but still a flat, top-down image. Phase 2 extrudes
// the same field into a true 3D surface  y = h(x, z)  and raymarches it from an
// oblique camera, so the fractal reads as a lit 3D landscape with perspective,
// a silhouette, and self-occlusion. Because it is now an actual 3D scene, the
// full raymarcher lighting stack applies: the render routes every hit through
// ShadingPipeline.Shade<TDe>, so the shared LightingFxData knobs (soft shadow,
// AO, PBR spec, and — the Q5 payoff — Beer–Lambert fog + volumetric in-scatter
// god-rays) all light the 2D fractal for free. See
// Docs/Technical/Heightfield-Relief-Spike.md (approach B).
//
// World frame: Y is up (matches the raymarcher / LightingFxData light
// convention, phi from +Y). The height field lives on the ground plane
// X in [-aspect/2, +aspect/2], Z in [-0.5, +0.5]; height rises along +Y. Terrain
// height comes from FractalParameters.Relief2DHeightScale; the sun + fog come
// from FractalParameters.Lighting.

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Rendering.Lighting;

public static class HeightfieldRaymarch2D
{
    /// <summary>
    /// Bilinear height-field distance estimator over the smooth-count buffer.
    /// <c>f(p) = (p.y - h(p.x, p.z)) · invLip</c> — a Lipschitz-normalised lower
    /// bound so a sphere trace can't overshoot thin ridges. Sample coords are
    /// edge-clamped, so outside the domain the border height extends outward.
    /// </summary>
    public readonly struct HeightDe : IDistanceEstimator
    {
        private readonly float[] _h;      // raw smooth counts
        private readonly int _w, _h2;
        private readonly double _sy;      // world height per smooth unit
        private readonly double _aspect;  // w / h
        private readonly double _invLip;

        public HeightDe(float[] h, int w, int hgt, double sy, double aspect, double invLip)
        {
            _h = h; _w = w; _h2 = hgt; _sy = sy; _aspect = aspect; _invLip = invLip;
        }

        /// <summary>World (x,z) → bilinear world-space surface height.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double SampleHeight(double x, double z)
        {
            // Map world (x, z) back to fractional pixel coords.
            double u = x / _aspect + 0.5;   // [0,1]
            double v = z + 0.5;             // [0,1]
            double fx = u * _w - 0.5;
            double fy = v * _h2 - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            x0 = Math.Clamp(x0, 0, _w - 1); x1 = Math.Clamp(x1, 0, _w - 1);
            y0 = Math.Clamp(y0, 0, _h2 - 1); y1 = Math.Clamp(y1, 0, _h2 - 1);
            double h00 = _h[y0 * _w + x0], h10 = _h[y0 * _w + x1];
            double h01 = _h[y1 * _w + x0], h11 = _h[y1 * _w + x1];
            double a = h00 + (h10 - h00) * tx;
            double b = h01 + (h11 - h01) * tx;
            return (a + (b - a) * ty) * _sy;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Evaluate(double x, double y, double z)
            => (y - SampleHeight(x, z)) * _invLip;
    }

    /// <summary>
    /// Raymarch the height field from an oblique camera into <paramref name="dst"/>.
    /// <paramref name="albedo"/> is the flat themed ARGB buffer (sampled at each
    /// hit's source pixel for surface colour). No-op copy when disabled / mis-sized.
    /// </summary>
    public static void Render(uint[] albedo, float[] height, int w, int h,
                              FractalParameters p, uint[] dst)
        => Render(albedo, height, w, h, p, dst, out _);

    /// <summary>As <see cref="Render(uint[],float[],int,int,FractalParameters,uint[])"/>,
    /// also reporting the fraction of pixels that hit the surface (vs ray-miss
    /// sky). Used by the headless gate to prove a real 3D silhouette.</summary>
    public static void Render(uint[] albedo, float[] height, int w, int h,
                              FractalParameters p, uint[] dst, out double hitFraction)
    {
        hitFraction = 0.0;
        int n = w * h;
        if (w <= 2 || h <= 2 || albedo.Length < n || dst.Length < n || height.Length < n)
        {
            if (!ReferenceEquals(albedo, dst)) Array.Copy(albedo, dst, n);
            return;
        }

        // Height field → world scale. Normalise smooth counts to [0,1], then
        // exaggerate by the height-scale knob. 0.35 keeps peaks well inside the
        // unit domain so the oblique camera frames the whole terrain.
        float maxH = 0f;
        for (int i = 0; i < n; i++) { float hv = height[i]; if (hv > maxH) maxH = hv; }
        if (maxH <= 1e-9f)   // dead-flat field (all interior) — nothing to raymarch
        {
            if (!ReferenceEquals(albedo, dst)) Array.Copy(albedo, dst, n);
            return;
        }
        double aspect = (double)w / h;
        double sy = 0.35 * Math.Max(0.0, p.Relief2DHeightScale) / maxH;

        // Lipschitz bound from the actual max world-space slope of the field.
        double worldDx = aspect / w, worldDz = 1.0 / h;
        double maxSlope = 0.0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 1; x < w; x++)
            {
                double s = Math.Abs(height[row + x] - height[row + x - 1]) * sy / worldDx;
                if (s > maxSlope) maxSlope = s;
            }
        }
        for (int y = 1; y < h; y++)
        {
            int row = y * w, prev = row - w;
            for (int x = 0; x < w; x++)
            {
                double s = Math.Abs(height[row + x] - height[prev + x]) * sy / worldDz;
                if (s > maxSlope) maxSlope = s;
            }
        }
        double lip = Math.Sqrt(1.0 + maxSlope * maxSlope);
        double invLip = 1.0 / lip;

        var de = new HeightDe(height, w, h, sy, aspect, invLip);
        var fx = p.Lighting;

        // Oblique camera. Orbit the terrain centre; frame the whole domain.
        double az = p.Relief2DCameraAzimuthDeg * Math.PI / 180.0;
        double el = Math.Clamp(p.Relief2DCameraElevationDeg, 5.0, 89.0) * Math.PI / 180.0;
        double fov = Math.Clamp(p.Relief2DCameraFovDeg, 15.0, 100.0) * Math.PI / 180.0;
        // Frame the terrain: pull the camera to the distance at which the
        // ground-plane bounding disk just fills the vertical FOV (× headroom).
        double extent = 0.5 * Math.Sqrt(aspect * aspect + 1.0);
        double radius = 1.1 * extent / Math.Tan(fov * 0.5);
        double tgtY = 0.35 * sy * maxH;         // aim just above the mean surface
        double camX = radius * Math.Cos(el) * Math.Sin(az);
        double camY = radius * Math.Sin(el);
        double camZ = radius * Math.Cos(el) * Math.Cos(az);
        // forward = normalize(target - cam)
        double fX = -camX, fY = (tgtY - camY), fZ = -camZ;
        double fl = Math.Sqrt(fX * fX + fY * fY + fZ * fZ); fX /= fl; fY /= fl; fZ /= fl;
        // right = normalize(cross(forward, up=(0,1,0)))
        double rX = fZ, rY = 0.0, rZ = -fX;
        double rl = Math.Sqrt(rX * rX + rZ * rZ); if (rl < 1e-9) rl = 1; rX /= rl; rZ /= rl;
        // up = cross(right, forward)
        double uX = rY * fZ - rZ * fY;
        double uY = rZ * fX - rX * fZ;
        double uZ = rX * fY - rY * fX;
        double tanHalf = Math.Tan(fov * 0.5);

        // Domain AABB (with height headroom) for ray-slab entry/exit.
        double bx = aspect * 0.5, bz = 0.5, by = sy * maxH * 1.05 + 1e-3;
        double eps = 0.0009 * radius;
        int maxSteps = 300;

        long hitCount = 0;
        Parallel.For(0, h, () => 0L, (py, _, localHits) =>
        {
            for (int px = 0; px < w; px++)
            {
                int idx = py * w + px;
                double ndcx = (2.0 * (px + 0.5) / w - 1.0) * aspect * tanHalf;
                double ndcy = (1.0 - 2.0 * (py + 0.5) / h) * tanHalf;
                double rdx = fX + rX * ndcx + uX * ndcy;
                double rdy = fY + rY * ndcx + uY * ndcy;
                double rdz = fZ + rZ * ndcx + uZ * ndcy;
                double rl2 = Math.Sqrt(rdx * rdx + rdy * rdy + rdz * rdz);
                rdx /= rl2; rdy /= rl2; rdz /= rl2;

                // Ray-slab against the terrain AABB [-bx,bx]×[0,by]×[-bz,bz].
                double t0 = 0.0, t1 = double.MaxValue;
                if (!SlabHit(camX, rdx, -bx, bx, ref t0, ref t1) ||
                    !SlabHit(camY, rdy, 0.0, by, ref t0, ref t1) ||
                    !SlabHit(camZ, rdz, -bz, bz, ref t0, ref t1))
                {
                    dst[idx] = ShadingPipeline.SkyColorHdri(rdx, rdy, rdz, in fx);
                    continue;
                }

                double t = Math.Max(t0, 0.0) + eps;
                bool hit = false;
                double d = 0.0;
                for (int s = 0; s < maxSteps && t < t1 + by; s++)
                {
                    double sx = camX + rdx * t, syw = camY + rdy * t, sz = camZ + rdz * t;
                    d = de.Evaluate(sx, syw, sz);
                    if (d < eps) { hit = true; break; }
                    t += Math.Max(d, eps * 0.5);
                }

                if (!hit)
                {
                    dst[idx] = ShadingPipeline.SkyColorHdri(rdx, rdy, rdz, in fx);
                    continue;
                }

                double hx = camX + rdx * t, hy = camY + rdy * t, hz = camZ + rdz * t;
                // Surface normal from the world-space height gradient.
                double e = Math.Max(worldDx, worldDz);
                double hL = de.SampleHeight(hx - e, hz), hR = de.SampleHeight(hx + e, hz);
                double hD = de.SampleHeight(hx, hz - e), hU = de.SampleHeight(hx, hz + e);
                double dHx = (hR - hL) / (2 * e), dHz = (hU - hD) / (2 * e);
                double nx = -dHx, ny = 1.0, nz = -dHz;
                double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                nx /= nl; ny /= nl; nz /= nl;

                // Albedo = themed colour at the source pixel under the hit.
                double u = hx / aspect + 0.5, v = hz + 0.5;
                int spx = Math.Clamp((int)(u * w), 0, w - 1);
                int spy = Math.Clamp((int)(v * h), 0, h - 1);
                uint alb = albedo[spy * w + spx];

                var si = new ShadingInputs(
                    hx, hy, hz, nx, ny, nz, rdx, rdy, rdz,
                    totalT: t, hitDist: d, hitStep: 0, epsilon: eps);
                dst[idx] = ShadingPipeline.Shade<HeightDe>(
                    in si, alb, in fx, in de, hasDe: true);
                localHits++;
            }
            return localHits;
        }, localHits => System.Threading.Interlocked.Add(ref hitCount, localHits));

        hitFraction = (double)hitCount / n;
    }

    /// <summary>1-axis ray-slab clip. Narrows [t0,t1] to the segment inside
    /// [lo,hi] along one axis. Returns false when the ray misses the slab.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SlabHit(double o, double dcomp, double lo, double hi,
                                ref double t0, ref double t1)
    {
        if (Math.Abs(dcomp) < 1e-12)
            return o >= lo && o <= hi;   // parallel: inside the slab or nowhere
        double inv = 1.0 / dcomp;
        double ta = (lo - o) * inv, tb = (hi - o) * inv;
        if (ta > tb) (ta, tb) = (tb, ta);
        if (ta > t0) t0 = ta;
        if (tb < t1) t1 = tb;
        return t0 <= t1;
    }
}
