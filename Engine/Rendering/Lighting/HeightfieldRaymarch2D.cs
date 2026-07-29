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
// AO, PBR spec, IBL, reflections, triplanar texture, Beer–Lambert fog +
// volumetric in-scatter god-rays) all light the 2D fractal for free. See
// Docs/Technical/Heightfield-Relief-Spike.md (approach B).
//
// #132 fidelity wave: supersample AA, orthographic-camera option, bilinear
// albedo, analytic bilinear-patch normals, cone-epsilon + bisection hit-refine,
// a base ground plane, a selectable height tone-curve, optional bicubic height
// sampling, and auto-filled sensible AO/shadow/spec defaults.
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

namespace FracturingFog.Rendering.Lighting;

public static class HeightfieldRaymarch2D
{
    /// <summary>Neutral base-plane albedo (#132 #6). A mid-slate so cast shadows
    /// and AO read clearly on the ground without smearing fractal colour outward.</summary>
    private const uint FloorAlbedo = 0xFF4C4C58u;

    /// <summary>Flat fill for ray-miss pixels when the sky backdrop is off
    /// (<c>LightingFxData.ShowSkyBackdrop == false</c>) — a near-black drop so
    /// the lit 3D object stands alone instead of an HDRI/gradient competing
    /// behind it. Mirrors the 3D calculators' "InSetColor when off" behaviour.</summary>
    private const uint DropColor = 0xFF0A0A0Eu;

    /// <summary>
    /// Height-field distance estimator over the (compressed) smooth-count buffer.
    /// <c>f(p) = (p.y - h(p.x, p.z)) · invLip</c> — a Lipschitz-normalised lower
    /// bound so a sphere trace can't overshoot thin ridges. Sample coords are
    /// edge-clamped, so outside the domain the border height extends outward.
    /// Sampling is bilinear by default, bicubic (Catmull-Rom) when requested.
    /// </summary>
    public readonly struct HeightDe : IDistanceEstimator
    {
        private readonly float[] _h;      // compressed smooth counts
        private readonly int _w, _h2;
        private readonly double _sy;      // world height per height unit
        private readonly double _aspect;  // w / h
        private readonly double _invLip;
        private readonly bool _bicubic;
        private readonly byte[]? _keep;   // #135 — 0 = culled (no surface), else 1

        public HeightDe(float[] h, int w, int hgt, double sy, double aspect,
                        double invLip, bool bicubic, byte[]? keep = null)
        {
            _h = h; _w = w; _h2 = hgt; _sy = sy; _aspect = aspect;
            _invLip = invLip; _bicubic = bicubic; _keep = keep;
        }

        /// <summary>#135 — true when the cell nearest world (x,z) is culled, so the
        /// surface should be treated as absent there (ray passes through).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Culled(double x, double z)
        {
            if (_keep is null) return false;
            int px = (int)Math.Round((x / _aspect + 0.5) * _w - 0.5);
            int pz = (int)Math.Round((z + 0.5) * _h2 - 0.5);
            px = Math.Clamp(px, 0, _w - 1); pz = Math.Clamp(pz, 0, _h2 - 1);
            return _keep[pz * _w + px] == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float Fetch(int x, int y)
            => _h[Math.Clamp(y, 0, _h2 - 1) * _w + Math.Clamp(x, 0, _w - 1)];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Catmull(double pm1, double p0, double p1, double p2, double t)
        {
            double a = -0.5 * pm1 + 1.5 * p0 - 1.5 * p1 + 0.5 * p2;
            double b = pm1 - 2.5 * p0 + 2.0 * p1 - 0.5 * p2;
            double c = -0.5 * pm1 + 0.5 * p1;
            return ((a * t + b) * t + c) * t + p0;
        }

        /// <summary>World (x,z) → world-space surface height.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double SampleHeight(double x, double z)
        {
            double u = x / _aspect + 0.5;   // [0,1]
            double v = z + 0.5;             // [0,1]
            double fx = u * _w - 0.5;
            double fy = v * _h2 - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;

            if (!_bicubic)
            {
                double h00 = Fetch(x0, y0), h10 = Fetch(x0 + 1, y0);
                double h01 = Fetch(x0, y0 + 1), h11 = Fetch(x0 + 1, y0 + 1);
                double a = h00 + (h10 - h00) * tx;
                double b = h01 + (h11 - h01) * tx;
                return (a + (b - a) * ty) * _sy;
            }

            // Bicubic Catmull-Rom over the 4×4 neighbourhood.
            double r0 = Catmull(Fetch(x0 - 1, y0 - 1), Fetch(x0, y0 - 1), Fetch(x0 + 1, y0 - 1), Fetch(x0 + 2, y0 - 1), tx);
            double r1 = Catmull(Fetch(x0 - 1, y0),     Fetch(x0, y0),     Fetch(x0 + 1, y0),     Fetch(x0 + 2, y0),     tx);
            double r2 = Catmull(Fetch(x0 - 1, y0 + 1), Fetch(x0, y0 + 1), Fetch(x0 + 1, y0 + 1), Fetch(x0 + 2, y0 + 1), tx);
            double r3 = Catmull(Fetch(x0 - 1, y0 + 2), Fetch(x0, y0 + 2), Fetch(x0 + 1, y0 + 2), Fetch(x0 + 2, y0 + 2), tx);
            return Catmull(r0, r1, r2, r3, ty) * _sy;
        }

        /// <summary>World-space height gradient (dH/dx, dH/dz). Analytic for the
        /// bilinear patch (#132 #4 — no finite-difference faceting); central
        /// difference for the bicubic path.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (double dHx, double dHz) SampleGrad(double x, double z)
        {
            if (_bicubic)
            {
                double ex = _aspect / _w, ez = 1.0 / _h2;
                double gx = (SampleHeight(x + ex, z) - SampleHeight(x - ex, z)) / (2 * ex);
                double gz = (SampleHeight(x, z + ez) - SampleHeight(x, z - ez)) / (2 * ez);
                return (gx, gz);
            }

            double u = x / _aspect + 0.5;
            double v = z + 0.5;
            double fx = u * _w - 0.5;
            double fy = v * _h2 - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            double h00 = Fetch(x0, y0), h10 = Fetch(x0 + 1, y0);
            double h01 = Fetch(x0, y0 + 1), h11 = Fetch(x0 + 1, y0 + 1);
            // Partials of the bilinear patch in pixel space, chained to world.
            // dfx/dx = _w/_aspect ; dfy/dz = _h2.
            double dHdfx = (h10 - h00) * (1.0 - ty) + (h11 - h01) * ty;
            double dHdfy = (h01 - h00) * (1.0 - tx) + (h11 - h10) * tx;
            double gxb = dHdfx * (_w / _aspect) * _sy;
            double gzb = dHdfy * _h2 * _sy;
            return (gxb, gzb);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Evaluate(double x, double y, double z)
            => Culled(x, z) ? 1e9 : (y - SampleHeight(x, z)) * _invLip;
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
    /// also reporting the fraction of pixels that hit the terrain (vs ray-miss
    /// sky / ground). Used by the headless gate to prove a real 3D silhouette.</summary>
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

        // Height tone-curve (#132 #7 / #130). The raw smooth-iteration count is
        // unbounded near the fractal boundary (high dwell) while the interior is
        // 0, so a single boundary needle sets the global max and linear
        // normalisation flattens everything else into thin tall spires — a
        // "hedgehog" that a close camera stretches into distorted streaks.
        // Compress into a scratch first so boundary dwell reads as terrain relief.
        float[] hbuf = s_compressed is { } sc && sc.Length >= n
            ? sc : (s_compressed = new float[n]);
        HeightCurve2D curve = p.Relief2DHeightCurve;
        for (int i = 0; i < n; i++)
        {
            float hv = height[i];
            hbuf[i] = hv <= 0f ? 0f : curve switch
            {
                HeightCurve2D.Linear => hv,
                HeightCurve2D.Sqrt   => (float)Math.Sqrt(hv),
                _                    => (float)Math.Log(1.0 + hv),   // Log (default)
            };
        }

        // Edge fade (#137) — ramp the height to the base plane over a margin at
        // each image edge so structure running off the frame tapers out instead
        // of extruding into streaky border "arms" (visible when panned/zoomed so
        // the fractal touches an edge). 0 = off.
        double edgeFade = Math.Clamp(p.Relief2DEdgeFade, 0.0, 0.5);
        if (edgeFade > 0.0)
        {
            double mx = Math.Max(1.0, edgeFade * w);
            double my = Math.Max(1.0, edgeFade * h);
            for (int y = 0; y < h; y++)
            {
                double dy = Math.Min(y, h - 1 - y);
                double wy = dy >= my ? 1.0 : Smoothstep(dy / my);
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    double dx = Math.Min(x, w - 1 - x);
                    double wx = dx >= mx ? 1.0 : Smoothstep(dx / mx);
                    double f = wx * wy;
                    if (f < 1.0) hbuf[row + x] = (float)(hbuf[row + x] * f);
                }
            }
        }

        // Compressed height field → world scale. Normalise to [0,1], then
        // exaggerate by the height-scale knob. 0.35 keeps peaks well inside the
        // unit domain so the oblique camera frames the whole terrain.
        float maxH = 0f;
        for (int i = 0; i < n; i++) { float hv = hbuf[i]; if (hv > maxH) maxH = hv; }
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
                double s = Math.Abs(hbuf[row + x] - hbuf[row + x - 1]) * sy / worldDx;
                if (s > maxSlope) maxSlope = s;
            }
        }
        for (int y = 1; y < h; y++)
        {
            int row = y * w, prev = row - w;
            for (int x = 0; x < w; x++)
            {
                double s = Math.Abs(hbuf[row + x] - hbuf[prev + x]) * sy / worldDz;
                if (s > maxSlope) maxSlope = s;
            }
        }
        double lip = Math.Sqrt(1.0 + maxSlope * maxSlope);
        double invLip = 1.0 / lip;

        // #135 — isolation cull mask. Drop cells by low local detail and/or
        // matched colour so the kept filaments read as a standalone 3D object.
        byte[]? keep = BuildKeepMask(hbuf, albedo, w, h, n, p);

        var de = new HeightDe(hbuf, w, h, sy, aspect, invLip, p.Relief2DBicubicHeight, keep);

        // Lighting FX (#132 defaults). Copy the struct, then — when auto-shade is
        // on — fill sensible AO / soft-shadow / specular / ambient values wherever
        // the knob is still at zero so Oblique 3D looks good out of the box.
        // Explicit non-zero user values always survive.
        var fx = p.Lighting;
        if (p.Relief2DAutoShade)
        {
            if (fx.AoSamples <= 0)        fx.AoSamples = 5;
            if (fx.AoStrength <= 0)       fx.AoStrength = 0.5;
            if (fx.ShadowSteps <= 0)      fx.ShadowSteps = 24;
            if (fx.ShadowLightMask == 0)  fx.ShadowLightMask = 0x1;
            if (fx.ShadowSoftK <= 0)      fx.ShadowSoftK = 8.0;
            if (fx.AmbientStrength <= 0)  fx.AmbientStrength = 0.3;
            if (fx.SpecularStrength <= 0) { fx.SpecularStrength = 0.25; if (fx.Roughness <= 0) fx.Roughness = 0.55; }
        }

        // Oblique camera. Orbit the terrain centre; frame the whole domain.
        double az = p.Relief2DCameraAzimuthDeg * Math.PI / 180.0;
        double el = Math.Clamp(p.Relief2DCameraElevationDeg, 5.0, 89.0) * Math.PI / 180.0;
        double fov = Math.Clamp(p.Relief2DCameraFovDeg, 15.0, 100.0) * Math.PI / 180.0;
        // Frame the terrain so it FILLS the window. The ground-plane bounding
        // disk (radius = extent) foreshortens vertically to extent·sin(el) when
        // seen at elevation el, so scale the fit distance by sin(el) (#128). A
        // user frame-fill zoom pulls the camera in (>1) or back (<1).
        double extent = 0.5 * Math.Sqrt(aspect * aspect + 1.0);
        double zoom = Math.Clamp(p.Relief2DCameraZoom, 0.2, 5.0);
        double foreshorten = Math.Clamp(Math.Sin(el), 0.3, 1.0);
        double radius = extent * foreshorten / (Math.Tan(fov * 0.5) * zoom);
        double tgtY = 0.35 * sy * maxH;         // aim just above the mean surface
        double camX = radius * Math.Cos(el) * Math.Sin(az);
        double camY = radius * Math.Sin(el);
        double camZ = radius * Math.Cos(el) * Math.Cos(az);
        // forward = normalize(target - cam)
        double fX = -camX, fY = (tgtY - camY), fZ = -camZ;
        double fl = Math.Sqrt(fX * fX + fY * fY + fZ * fZ); fX /= fl; fY /= fl; fZ /= fl;
        // right = normalize(cross(forward, up=(0,1,0))) = (-fZ, 0, fX). (#129 —
        // the old (fZ,0,-fX) was left-handed, mirroring both screen axes.)
        double rX = -fZ, rY = 0.0, rZ = fX;
        double rl = Math.Sqrt(rX * rX + rZ * rZ); if (rl < 1e-9) rl = 1; rX /= rl; rZ /= rl;
        // up = cross(right, forward)
        double uX = rY * fZ - rZ * fY;
        double uY = rZ * fX - rX * fZ;
        double uZ = rX * fY - rY * fX;
        double tanHalf = Math.Tan(fov * 0.5);

        // Orthographic (#132 #2): parallel rays, no perspective stretch. The
        // vertical half-extent of the view is framed the same way (fill window).
        bool ortho = p.Relief2DCameraOrthographic;
        double orthoHalfV = extent * foreshorten / zoom;

        // Domain AABB (with height headroom) for ray-slab entry/exit.
        double bx = aspect * 0.5, bz = 0.5, by = sy * maxH * 1.05 + 1e-3;
        // Base epsilon + cone growth (#132 #5). Near pixels use a tight
        // tolerance for a crisp silhouette; far pixels loosen with distance so
        // the march doesn't stall (banding). Ortho has no perspective divergence.
        double eps0 = ortho
            ? Math.Max(0.0009 * radius, orthoHalfV / h)
            : 0.0009 * radius;
        double pixelAngle = ortho ? 0.0 : tanHalf / h;
        int maxSteps = 320;
        bool groundPlane = p.Relief2DGroundPlane;
        bool showSky = fx.ShowSkyBackdrop;               // #133 — honour the toggle
        bool isolate = p.Relief2DIsolate;                // #135 — transparent bg
        double floorBx = bx * 3.0, floorBz = bz * 3.0;   // bounded floor → horizon keeps sky

        // One primary sample. Returns the shaded colour + whether it hit the
        // terrain (ground / sky return false so the silhouette metric stays the
        // terrain coverage).
        (uint col, bool terrainHit) SamplePixel(double sxpix, double sypix)
        {
            double ndcx = 2.0 * sxpix / w - 1.0;
            double ndcy = 1.0 - 2.0 * sypix / h;
            double ox, oy, oz, rdx, rdy, rdz;
            if (ortho)
            {
                double sxo = ndcx * aspect * orthoHalfV, syo = ndcy * orthoHalfV;
                ox = camX + rX * sxo + uX * syo;
                oy = camY + rY * sxo + uY * syo;
                oz = camZ + rZ * sxo + uZ * syo;
                rdx = fX; rdy = fY; rdz = fZ;
            }
            else
            {
                ox = camX; oy = camY; oz = camZ;
                double a = ndcx * aspect * tanHalf, b = ndcy * tanHalf;
                rdx = fX + rX * a + uX * b;
                rdy = fY + rY * a + uY * b;
                rdz = fZ + rZ * a + uZ * b;
                double il = 1.0 / Math.Sqrt(rdx * rdx + rdy * rdy + rdz * rdz);
                rdx *= il; rdy *= il; rdz *= il;
            }

            // Ray-slab against the terrain AABB.
            double t0 = 0.0, t1 = double.MaxValue;
            bool inside = SlabHit(ox, rdx, -bx, bx, ref t0, ref t1)
                       && SlabHit(oy, rdy, 0.0, by, ref t0, ref t1)
                       && SlabHit(oz, rdz, -bz, bz, ref t0, ref t1);
            if (inside)
            {
                double t = Math.Max(t0, 0.0) + eps0;
                double tPrev = t, d = 0.0;
                bool hit = false;
                for (int s = 0; s < maxSteps && t < t1 + by; s++)
                {
                    d = de.Evaluate(ox + rdx * t, oy + rdy * t, oz + rdz * t);
                    double epsT = eps0 + pixelAngle * t;
                    if (d < epsT) { hit = true; break; }
                    tPrev = t;
                    t += Math.Max(d, epsT * 0.5);
                }

                if (hit)
                {
                    // Bisection refine between the last outside (tPrev) and the
                    // inside (t) sample → sub-step-accurate silhouette (#132 #5).
                    double tLo = tPrev, tHi = t;
                    for (int b2 = 0; b2 < 5; b2++)
                    {
                        double tm = 0.5 * (tLo + tHi);
                        if (de.Evaluate(ox + rdx * tm, oy + rdy * tm, oz + rdz * tm) > 0.0)
                            tLo = tm; else tHi = tm;
                    }
                    double tf = tHi;
                    double hx = ox + rdx * tf, hy = oy + rdy * tf, hz = oz + rdz * tf;

                    // Analytic surface normal (#132 #4).
                    var (dHx, dHz) = de.SampleGrad(hx, hz);
                    double nx = -dHx, ny = 1.0, nz = -dHz;
                    double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    nx /= nl; ny /= nl; nz /= nl;

                    // Bilinear albedo at the source pixel under the hit (#132 #3).
                    double u = hx / aspect + 0.5, v = hz + 0.5;
                    uint alb = SampleAlbedoBilinear(albedo, w, h, u, v);

                    var si = new ShadingInputs(
                        hx, hy, hz, nx, ny, nz, rdx, rdy, rdz,
                        totalT: tf, hitDist: d, hitStep: 0, epsilon: eps0);
                    return (ShadingPipeline.Shade<HeightDe>(in si, alb, in fx, in de, true), true);
                }
            }

            // Terrain miss → bounded ground plane (#132 #6), else sky.
            if (groundPlane && rdy < -1e-9)
            {
                double tp = (0.0 - oy) / rdy;
                if (tp > 0.0)
                {
                    double gx = ox + rdx * tp, gz = oz + rdz * tp;
                    if (Math.Abs(gx) <= floorBx && Math.Abs(gz) <= floorBz)
                    {
                        var sg = new ShadingInputs(
                            gx, 0.0, gz, 0.0, 1.0, 0.0, rdx, rdy, rdz,
                            totalT: tp, hitDist: 0.0, hitStep: 0, epsilon: eps0);
                        return (ShadingPipeline.Shade<HeightDe>(in sg, FloorAlbedo, in fx, in de, true), false);
                    }
                }
            }
            // #133 — respect Show-sky-backdrop: HDRI/gradient only when on,
            // else a flat drop. #135 — in isolate mode the background is written
            // transparent (alpha 0) so the kept object exports as a cutout.
            uint bg = showSky ? ShadingPipeline.SkyColorHdri(rdx, rdy, rdz, in fx) : DropColor;
            if (isolate) bg &= 0x00FFFFFFu;
            return (bg, false);
        }

        int ss = Math.Clamp(p.Relief2DSupersample, 1, 4);
        double invSS = 1.0 / (ss * ss);

        long hitCount = 0;
        Parallel.For(0, h, () => 0L, (py, _, localHits) =>
        {
            for (int px = 0; px < w; px++)
            {
                double aR = 0, aG = 0, aB = 0, aA = 0;
                int subHits = 0;
                for (int sj = 0; sj < ss; sj++)
                for (int si = 0; si < ss; si++)
                {
                    var (col, hit) = SamplePixel(px + (si + 0.5) / ss, py + (sj + 0.5) / ss);
                    aR += (col >> 16) & 0xFF;
                    aG += (col >> 8) & 0xFF;
                    aB += col & 0xFF;
                    aA += (col >> 24) & 0xFF;   // #135 — average alpha → soft cutout edges
                    if (hit) subHits++;
                }
                byte R = (byte)Math.Clamp(aR * invSS + 0.5, 0, 255);
                byte G = (byte)Math.Clamp(aG * invSS + 0.5, 0, 255);
                byte B = (byte)Math.Clamp(aB * invSS + 0.5, 0, 255);
                byte A = (byte)Math.Clamp(aA * invSS + 0.5, 0, 255);
                dst[py * w + px] = ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;
                if (subHits * 2 >= ss * ss) localHits++;
            }
            return localHits;
        }, localHits => System.Threading.Interlocked.Add(ref hitCount, localHits));

        hitFraction = (double)hitCount / n;
    }

    /// <summary>Bilinear sample of the ARGB albedo buffer at UV in [0,1]
    /// (edge-clamped). Keeps the alpha of the nearest texel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SampleAlbedoBilinear(uint[] a, int w, int h, double u, double v)
    {
        double fx = u * w - 0.5, fy = v * h - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        int x1 = Math.Clamp(x0 + 1, 0, w - 1), y1 = Math.Clamp(y0 + 1, 0, h - 1);
        x0 = Math.Clamp(x0, 0, w - 1); y0 = Math.Clamp(y0, 0, h - 1);
        uint c00 = a[y0 * w + x0], c10 = a[y0 * w + x1];
        uint c01 = a[y1 * w + x0], c11 = a[y1 * w + x1];
        double Lerp(double p, double q, double t) => p + (q - p) * t;
        double r = Lerp(Lerp((c00 >> 16) & 0xFF, (c10 >> 16) & 0xFF, tx),
                        Lerp((c01 >> 16) & 0xFF, (c11 >> 16) & 0xFF, tx), ty);
        double g = Lerp(Lerp((c00 >> 8) & 0xFF, (c10 >> 8) & 0xFF, tx),
                        Lerp((c01 >> 8) & 0xFF, (c11 >> 8) & 0xFF, tx), ty);
        double b = Lerp(Lerp(c00 & 0xFF, c10 & 0xFF, tx),
                        Lerp(c01 & 0xFF, c11 & 0xFF, tx), ty);
        return 0xFF000000u
             | ((uint)(r + 0.5) << 16) | ((uint)(g + 0.5) << 8) | (uint)(b + 0.5);
    }

    // Tone-curve-compressed height scratch (#130). Reused across frames on the
    // render thread that calls Render (serialised by the host upload gate); the
    // Parallel.For only reads it via the HeightDe.
    private static float[]? s_compressed;
    private static byte[]? s_keep;   // #135 isolation cull mask scratch

    /// <summary>Smoothstep on [0,1] (3t²−2t³).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Smoothstep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>#135 — build the per-cell keep mask (0 = culled). Returns null
    /// when isolation is off or no cull selector is active (keep everything; the
    /// transparent background is applied at the miss site regardless).</summary>
    private static byte[]? BuildKeepMask(float[] hbuf, uint[] albedo,
                                         int w, int h, int n, FractalParameters p)
    {
        if (!p.Relief2DIsolate) return null;
        bool byDetail = p.Relief2DIsolateByDetail;
        uint[] drops = ParseDropColors(p.Relief2DDropColorsCsv);
        bool byColor = p.Relief2DIsolateByColor && drops.Length > 0;
        if (!byDetail && !byColor) return null;   // isolate bg only, keep all surface

        byte[] keep = s_keep is { } sk && sk.Length >= n ? sk : (s_keep = new byte[n]);
        // Detail threshold is a DROP FRACTION: cull the flattest `thr` share of
        // cells (by local gradient). A quantile — not a fraction of the max — so
        // a single sharp boundary spike can't skew it. Higher = keep only the
        // sharpest filaments.
        double thr = Math.Clamp(p.Relief2DDetailThreshold, 0.0, 1.0);
        double tol = Math.Clamp(p.Relief2DColorTolerance, 0.0, 1.0) * 441.6729; // √(3·255²)

        double keepDetail = 0.0;
        if (byDetail)
        {
            const int BINS = 512;
            Span<int> hist = stackalloc int[BINS];
            double maxDetail = 1e-9;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float c = hbuf[i];
                double dx = hbuf[y * w + Math.Min(x + 1, w - 1)] - c;
                double dz = hbuf[Math.Min(y + 1, h - 1) * w + x] - c;
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d > maxDetail) maxDetail = d;
            }
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float c = hbuf[i];
                double dx = hbuf[y * w + Math.Min(x + 1, w - 1)] - c;
                double dz = hbuf[Math.Min(y + 1, h - 1) * w + x] - c;
                double d = Math.Sqrt(dx * dx + dz * dz);
                int b = (int)(d / maxDetail * (BINS - 1));
                hist[Math.Clamp(b, 0, BINS - 1)]++;
            }
            int target = (int)(thr * n), cum = 0, tb = 0;
            for (int b = 0; b < BINS; b++) { cum += hist[b]; if (cum >= target) { tb = b; break; } }
            keepDetail = (tb + 1) / (double)BINS * maxDetail;
        }

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            bool drop = false;
            if (byDetail)
            {
                float c = hbuf[i];
                double dx = hbuf[y * w + Math.Min(x + 1, w - 1)] - c;
                double dz = hbuf[Math.Min(y + 1, h - 1) * w + x] - c;
                if (Math.Sqrt(dx * dx + dz * dz) < keepDetail) drop = true;
            }
            if (!drop && byColor)
            {
                uint a = albedo[i];
                double ar = (a >> 16) & 0xFF, ag = (a >> 8) & 0xFF, ab = a & 0xFF;
                foreach (uint dc in drops)
                {
                    double dr = ar - ((dc >> 16) & 0xFF);
                    double dg = ag - ((dc >> 8) & 0xFF);
                    double db = ab - (dc & 0xFF);
                    if (Math.Sqrt(dr * dr + dg * dg + db * db) <= tol) { drop = true; break; }
                }
            }
            keep[i] = (byte)(drop ? 0 : 1);
        }
        return keep;
    }

    /// <summary>Parse a comma-separated list of 6- or 8-hex-digit colours into
    /// packed 0xAARRGGBB (alpha ignored for matching). Bad tokens are skipped.</summary>
    internal static uint[] ParseDropColors(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<uint>();
        string[] toks = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new System.Collections.Generic.List<uint>(toks.Length);
        foreach (string t in toks)
        {
            string s = t.StartsWith("#", StringComparison.Ordinal) ? t[1..] : t;
            if ((s.Length == 6 || s.Length == 8) &&
                uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out uint v))
                list.Add(v);
        }
        return list.ToArray();
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
