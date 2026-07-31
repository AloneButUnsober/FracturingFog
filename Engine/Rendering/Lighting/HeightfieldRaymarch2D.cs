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

    /// <summary>#159 — the two fixed background/floor colours exposed to the GPU
    /// relief parity twin (<see cref="ReliefUniforms"/>) so it packs the exact
    /// same constants this render uses.</summary>
    internal const uint FloorAlbedoArgb = FloorAlbedo;
    internal const uint DropColorArgb = DropColor;

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
        => Render(albedo, height, w, h, w, h, p, dst, out _);

    /// <summary>As <see cref="Render(uint[],float[],int,int,FractalParameters,uint[])"/>,
    /// also reporting the fraction of pixels that hit the terrain (vs ray-miss
    /// sky / ground). Used by the headless gate to prove a real 3D silhouette.</summary>
    public static void Render(uint[] albedo, float[] height, int w, int h,
                              FractalParameters p, uint[] dst, out double hitFraction)
        => Render(albedo, height, w, h, w, h, p, dst, out hitFraction);

    /// <summary>#143 — decoupled-resolution overload. The height field
    /// (<paramref name="height"/>, dims <paramref name="hw"/>×<paramref name="hh"/>)
    /// may be a HIGHER resolution than the output / albedo grid
    /// (<paramref name="w"/>×<paramref name="h"/>). Relief quality is
    /// window-size dependent because the field is undersampled at small windows
    /// (the fractal boundary collapses into isolated needles); feeding a field
    /// computed at a resolution floor — independent of the display size — makes
    /// every window match the maximized look. HeightDe samples the field by
    /// normalised world coords, so it already works at any field resolution; the
    /// resolution-adaptive spike filters + amplitude pull-down key off the FIELD
    /// dims (hw,hh), so a hi-res field runs them at identity — exactly the
    /// signed-off large-window path. The camera, ray generation, albedo sampling
    /// and output all stay on the OUTPUT grid (w,h). Both grids cover the same
    /// view, so a single output aspect (w/h) drives the world domain.</summary>
    public static void Render(uint[] albedo, float[] height, int w, int h,
                              int hw, int hh,
                              FractalParameters p, uint[] dst, out double hitFraction)
    {
        hitFraction = 0.0;
        int n = w * h;            // OUTPUT / albedo pixel count
        int hn = hw * hh;         // FIELD (height) cell count
        if (w <= 2 || h <= 2 || hw <= 2 || hh <= 2
            || albedo.Length < n || dst.Length < n || height.Length < hn)
        {
            if (!ReferenceEquals(albedo, dst)) Array.Copy(albedo, dst, n);
            return;
        }

        // ── #155 pre-pass cache ───────────────────────────────────────────
        // Everything from the tone-curve through the grid-slope reduction is a
        // deterministic function of the raw field content plus (hw, hh, height
        // curve, edge-fade). It is INDEPENDENT of the camera, lighting, albedo
        // and the height SCALE (which enters only as `sy` afterwards). Camera
        // orbit, progressive-preview restages and re-theme all re-enter Render
        // with the identical field, so re-running the ~8-pass filter chain every
        // frame is wasted work. Hash the inputs; on a match reuse the cached
        // compressed field (`s_compressed`), its max, and the unitless
        // grid-slope maxima. sy / invLip stay per-call (cheap, scale-dependent).
        HeightCurve2D curve = p.Relief2DHeightCurve;
        double edgeFade = Math.Clamp(p.Relief2DEdgeFade, 0.0, 0.5);
        ulong key = PrepassKey(height, hn, hw, hh, curve, edgeFade);

        float[] hbuf;
        float maxH, gMaxX, gMaxZ;
        if (s_prepassValid && key == s_prepassKey
            && s_compressed is { } cached && cached.Length >= hn)
        {
            hbuf = cached;
            maxH = s_prepassMaxH; gMaxX = s_prepassGMaxX; gMaxZ = s_prepassGMaxZ;
        }
        else
        {
            s_prepassValid = false;   // invalid until the recompute below finishes
            hbuf = s_compressed is { } sc && sc.Length >= hn
                ? sc : (s_compressed = new float[hn]);

            // Height tone-curve (#132 #7 / #130). The raw smooth-iteration count
            // is unbounded near the fractal boundary (high dwell) while the
            // interior is 0, so a single boundary needle sets the global max and
            // linear normalisation flattens everything else into thin tall
            // spires — a "hedgehog" that a close camera stretches into distorted
            // streaks. Compress so boundary dwell reads as terrain relief.
            for (int i = 0; i < hn; i++)
            {
                float hv = height[i];
                hbuf[i] = hv <= 0f ? 0f : curve switch
                {
                    HeightCurve2D.Linear => hv,
                    HeightCurve2D.Sqrt   => (float)Math.Sqrt(hv),
                    _                    => (float)Math.Log(1.0 + hv),   // Log (default)
                };
            }

            // Exterior baseline subtraction (#141) — the tone curve (esp. Log)
            // lifts the low far-from-set smooth counts into a raised rectangular
            // PLATEAU (a tabletop) whose clipped domain boundary reads as a
            // persistent rectangle at the fractal plane. Subtract a low
            // percentile of the nonzero heights so the far exterior sits back on
            // the base plane and only the boundary structure rises; the plateau
            // — and its rectangle — disappears, as it does when the user zooms in.
            {
                float hmax = 0f;
                for (int i = 0; i < hn; i++) { float hv = hbuf[i]; if (hv > hmax) hmax = hv; }
                if (hmax > 1e-9f)
                {
                    const int B = 512;
                    Span<int> hist = stackalloc int[B];
                    int nz = 0;
                    for (int i = 0; i < hn; i++)
                    {
                        float hv = hbuf[i];
                        if (hv > 0f) { hist[Math.Clamp((int)(hv / hmax * (B - 1)), 0, B - 1)]++; nz++; }
                    }
                    if (nz > 0)
                    {
                        int target = (int)(0.60 * nz), cum = 0;
                        float baseline = 0f;
                        for (int b = 0; b < B; b++) { cum += hist[b]; if (cum >= target) { baseline = (b + 0.5f) / B * hmax; break; } }
                        if (baseline > 0f)
                            for (int i = 0; i < hn; i++)
                                hbuf[i] = hbuf[i] > baseline ? hbuf[i] - baseline : 0f;
                    }
                }
            }

            // Resolution-adaptive despike (#145). At small window sizes (Mini /
            // Toy mode) the fractal boundary is undersampled, so a lone
            // high-dwell cell whose neighbours all escaped fast reads as an
            // isolated tall NEEDLE — the "hedgehog" the oblique camera stretches
            // into a spike. Clamp every cell to its 8-neighbour max plus a small
            // margin: connected ridges and filaments (a neighbour is nearly as
            // tall) survive; only isolated single-cell peaks are pulled down.
            // Self-gating — at maximized / Span resolution the boundary is
            // connected so nothing clamps and the signed-off view is unchanged.
            DespikeNeighborMax(hbuf, hw, hh);

            // Resolution-adaptive low-pass (#145b). The despike above only
            // removes ISOLATED needles; along the fractal boundary every cell is
            // high but the dwell oscillates wildly, so at Mini (320×240) / Toy
            // (200×150) sizes the undersampled boundary reads as a jagged COMB of
            // tall cells. A neighbour-max clamp can't fix a comb whose cells are
            // all tall; a low-pass can. Blur strength ramps from 0 at ≥480 px
            // (maximized / Span untouched) up to ~3 box passes at Toy size,
            // blended continuously so a window resize never snaps the look.
            LowPassAdaptive(hbuf, hw, hh);

            // Edge fade (#137, #140) — pull tall structure near each image edge
            // down to the base plane so filaments running off the frame taper
            // out instead of extruding into streaky border "arms". A height CAP,
            // not a multiply: cap = window·maxRaw ramps from 0 at the very edge
            // to the field max inside the margin, and only heights ABOVE the cap
            // are lowered. The near-flat exterior stays flat, so the fade no
            // longer lifts the border into a rectangular lip/ridge (#140). 0 = off.
            if (edgeFade > 0.0)
            {
                double mx = Math.Max(1.0, edgeFade * hw);
                double my = Math.Max(1.0, edgeFade * hh);
                for (int y = 0; y < hh; y++)
                {
                    double dy = Math.Min(y, hh - 1 - y);
                    double wy = dy >= my ? 1.0 : Smoothstep(dy / my);
                    int row = y * hw;
                    for (int x = 0; x < hw; x++)
                    {
                        double dx = Math.Min(x, hw - 1 - x);
                        double wx = dx >= mx ? 1.0 : Smoothstep(dx / mx);
                        double f = wx * wy;
                        if (f < 1.0) hbuf[row + x] = (float)(hbuf[row + x] * f);
                    }
                }
            }

            maxH = 0f;
            for (int i = 0; i < hn; i++) { float hv = hbuf[i]; if (hv > maxH) maxH = hv; }

            // Unitless per-cell grid-slope maxima (parallel reduction, #155).
            // Independent of sy / world scale so the cache survives an aspect or
            // height-scale change; the world-space Lipschitz slope is
            // reconstructed per call below.
            (gMaxX, gMaxZ) = GridSlopeMaxima(hbuf, hw, hh);

            s_prepassMaxH = maxH; s_prepassGMaxX = gMaxX; s_prepassGMaxZ = gMaxZ;
            s_prepassKey = key; s_prepassValid = true;
        }

        if (maxH <= 1e-9f)   // dead-flat field (all interior) — nothing to raymarch
        {
            if (!ReferenceEquals(albedo, dst)) Array.Copy(albedo, dst, n);
            return;
        }
        double aspect = (double)w / h;   // OUTPUT aspect (field covers the same view)
        // Resolution-adaptive relief amplitude (#145c). The undersampled boundary
        // is a rough MULTI-cell high-dwell band towering over the zero interior;
        // median / blur widen but can't flatten it, so at Mini / Toy sizes it
        // renders as tall fingers seen side-on. Pulling the height scale down at
        // low resolution turns that band into a gentle mound — matching the
        // (high-res) maximized look, which is itself low relief at this framing.
        // Identity at ≥480 px (maximized / Span unchanged); down to 0.45× at Toy.
        double resT = ResolutionRamp(hw, hh);
        double reliefAmp = 1.0 - 0.72 * resT;
        double sy = 0.35 * reliefAmp * Math.Max(0.0, p.Relief2DHeightScale) / maxH;

        // Lipschitz bound from the max world-space slope, reconstructed from the
        // cached unitless grid maxima × sy / world-cell size (#155). Exactly the
        // old two-pass scan's result: max over both axes.
        double worldDx = aspect / hw, worldDz = 1.0 / hh;
        double maxSlope = Math.Max(gMaxX * sy / worldDx, gMaxZ * sy / worldDz);
        double lip = Math.Sqrt(1.0 + maxSlope * maxSlope);
        double invLip = 1.0 / lip;

        // #135 — isolation cull mask. Drop cells by low local detail and/or
        // matched colour so the kept filaments read as a standalone 3D object.
        // Mask lives on the FIELD grid (hw,hh); colour drop samples the albedo
        // (output grid) by normalised coords.
        byte[]? keep = BuildKeepMask(hbuf, hw, hh, albedo, w, h, p);

        var de = new HeightDe(hbuf, hw, hh, sy, aspect, invLip, p.Relief2DBicubicHeight, keep);

        // Lighting FX (#132 defaults). Copy the struct, then — when auto-shade is
        // on — fill sensible AO / soft-shadow / specular / ambient values wherever
        // the knob is still at zero so Oblique 3D looks good out of the box.
        // Explicit non-zero user values always survive.
        var fx = p.Lighting;
        if (p.Relief2DAutoShade) FillAutoShadeDefaults(ref fx);

        // Oblique camera + AABB + cone-epsilon. Extracted (#159 / Slice 3a) into
        // BuildObliqueCamera so the GPU relief kernel and its CPU parity twin
        // (ReliefRaymarchGpu) drive rays from byte-identical numbers. The math is
        // unchanged — moved verbatim — so this render is bit-for-bit as before.
        ReliefCamera cam = BuildObliqueCamera(w, h, aspect, sy, maxH, p);
        double camX = cam.CamX, camY = cam.CamY, camZ = cam.CamZ;
        double fX = cam.FX, fY = cam.FY, fZ = cam.FZ;
        double rX = cam.RX, rY = 0.0, rZ = cam.RZ;       // right vector has rY == 0
        double uX = cam.UX, uY = cam.UY, uZ = cam.UZ;
        double tanHalf = cam.TanHalf;
        bool ortho = cam.Ortho;
        double orthoHalfV = cam.OrthoHalfV;
        double bx = cam.Bx, bz = cam.Bz, by = cam.By;
        double eps0 = cam.Eps0;
        double pixelAngle = cam.PixelAngle;
        int maxSteps = cam.MaxSteps;
        bool groundPlane = cam.GroundPlane;
        double floorBx = cam.FloorBx, floorBz = cam.FloorBz;
        bool showSky = fx.ShowSkyBackdrop;               // #133 — honour the toggle
        bool isolate = p.Relief2DIsolate;                // #135 — transparent bg

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
                    uint tcol = ShadingPipeline.Shade<HeightDe>(in si, alb, in fx, in de, true);

                    // #141 — dissolve the terrain FOOTPRINT edge into whatever is
                    // behind it. The height field has a rectangular extent
                    // ([-bx,bx]×[-bz,bz] = the image); its flat exterior sits at a
                    // level and is clipped to that rectangle, so the footprint
                    // reads as a persistent rectangle at the fractal plane. Blend
                    // the terrain colour toward the behind-surface over the outer
                    // margin — the floor directly below when the ground plane is
                    // on (seamless, they are coplanar), else the sky/drop — so the
                    // boundary dissolves rather than drawing a rectangle.
                    double edgeT = Math.Max(Math.Abs(hx) / bx, Math.Abs(hz) / bz);
                    if (edgeT > 0.72)
                    {
                        double fade = Smoothstep((edgeT - 0.72) / 0.28);
                        uint behind;
                        if (groundPlane)
                        {
                            var fgi = new ShadingInputs(
                                hx, 0.0, hz, 0.0, 1.0, 0.0, rdx, rdy, rdz,
                                totalT: tf, hitDist: 0.0, hitStep: 0, epsilon: eps0);
                            behind = ShadingPipeline.Shade<HeightDe>(in fgi, FloorAlbedo, in fx, in de, true);
                        }
                        else
                        {
                            behind = showSky ? ShadingPipeline.SkyColorHdri(rdx, rdy, rdz, in fx) : DropColor;
                            if (isolate) behind &= 0x00FFFFFFu;
                        }
                        tcol = BlendArgb(tcol, behind, fade);
                    }
                    return (tcol, true);
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

    /// <summary>#159 (Slice 3a) — the oblique-camera basis, domain AABB and
    /// cone-epsilon that <see cref="Render(uint[],float[],int,int,int,int,FractalParameters,uint[],out double)"/>
    /// marches with. A pure function of the output size, world scale (<paramref name="sy"/>),
    /// field max height and the FractalParameters camera knobs — no lighting,
    /// albedo or field content. Extracted so the GPU relief kernel and its CPU
    /// parity twin (<see cref="ReliefRaymarchGpu"/>) generate rays from the exact
    /// same numbers as the CPU render. rY of the camera right vector is always 0.</summary>
    public readonly struct ReliefCamera
    {
        public readonly double CamX, CamY, CamZ;
        public readonly double FX, FY, FZ;
        public readonly double RX, RZ;
        public readonly double UX, UY, UZ;
        public readonly double TanHalf;
        public readonly bool Ortho;
        public readonly double OrthoHalfV;
        public readonly double Bx, By, Bz;
        public readonly double Eps0, PixelAngle;
        public readonly int MaxSteps;
        public readonly bool GroundPlane;
        public readonly double FloorBx, FloorBz;

        public ReliefCamera(double camX, double camY, double camZ,
            double fX, double fY, double fZ, double rX, double rZ,
            double uX, double uY, double uZ, double tanHalf,
            bool ortho, double orthoHalfV, double bx, double by, double bz,
            double eps0, double pixelAngle, int maxSteps, bool groundPlane,
            double floorBx, double floorBz)
        {
            CamX = camX; CamY = camY; CamZ = camZ;
            FX = fX; FY = fY; FZ = fZ; RX = rX; RZ = rZ;
            UX = uX; UY = uY; UZ = uZ; TanHalf = tanHalf;
            Ortho = ortho; OrthoHalfV = orthoHalfV;
            Bx = bx; By = by; Bz = bz;
            Eps0 = eps0; PixelAngle = pixelAngle; MaxSteps = maxSteps;
            GroundPlane = groundPlane; FloorBx = floorBx; FloorBz = floorBz;
        }
    }

    /// <summary>Build the oblique camera / AABB / epsilon for a relief render.
    /// The body is moved verbatim from <c>Render</c> (see #159) — same
    /// expressions, same order — so both paths stay bit-identical.</summary>
    public static ReliefCamera BuildObliqueCamera(int w, int h, double aspect,
                                                  double sy, double maxH, FractalParameters p)
    {
        // Orbit the terrain centre; frame the whole domain.
        double az = p.Relief2DCameraAzimuthDeg * Math.PI / 180.0;
        double el = Math.Clamp(p.Relief2DCameraElevationDeg, 5.0, 89.0) * Math.PI / 180.0;
        double fov = Math.Clamp(p.Relief2DCameraFovDeg, 15.0, 100.0) * Math.PI / 180.0;
        // Frame the terrain so it FILLS the window. The ground-plane bounding
        // disk (radius = extent) foreshortens vertically to extent·sin(el) when
        // seen at elevation el, so scale the fit distance by sin(el) (#128). A
        // user frame-fill zoom pulls the camera in (>1) or back (<1).
        // #146 — cap the aspect used for CAMERA FRAMING (not ray generation).
        // The bounding-disk radius 0.5·√(aspect²+1) grows without bound as the
        // window widens, so a borderless multi-monitor Span (very wide aspect)
        // pulls the camera far back and the terrain — only 1 unit deep in Z —
        // fills a thin horizontal band, wasting the top and bottom of the frame.
        // Framing on a capped aspect keeps the camera close enough that the
        // terrain fills the height; the true (uncapped) aspect still drives ray
        // directions below, so the wide exterior simply extends past the
        // left/right edges instead of leaving vertical bars. Normal windows
        // (aspect ≤ cap) are unaffected — the signed-off 16:9 framing is
        // byte-identical.
        double framingAspect = Math.Min(aspect, 2.2);
        double extent = 0.5 * Math.Sqrt(framingAspect * framingAspect + 1.0);
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
        double floorBx = bx * 3.0, floorBz = bz * 3.0;   // bounded floor → horizon keeps sky

        return new ReliefCamera(camX, camY, camZ, fX, fY, fZ, rX, rZ,
            uX, uY, uZ, tanHalf, ortho, orthoHalfV, bx, by, bz,
            eps0, pixelAngle, maxSteps, groundPlane, floorBx, floorBz);
    }

    /// <summary>Bilinear sample of the ARGB albedo buffer at UV in [0,1]
    /// (edge-clamped). Keeps the alpha of the nearest texel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint SampleAlbedoBilinear(uint[] a, int w, int h, double u, double v)
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
    private static float[]? s_despikeSrc;   // #145 despike neighbour-read snapshot

    // #155 pre-pass cache. `s_compressed` holds the last computed compressed +
    // filtered field; these describe which inputs produced it and the scalars
    // the (scale-dependent) per-call math needs. Guarded by the same single-
    // entry assumption as the scratch buffers above (host upload gate serialises
    // Render); the Parallel.For inside only reads the field.
    private static ulong s_prepassKey;
    private static bool  s_prepassValid;
    private static float s_prepassMaxH, s_prepassGMaxX, s_prepassGMaxZ;

    /// <summary>#132 — fill sensible AO / soft-shadow / specular / ambient
    /// defaults wherever the knob is still at zero so Oblique 3D looks good out
    /// of the box. Explicit non-zero user values always survive. Shared by
    /// <see cref="Render(uint[],float[],int,int,int,int,FractalParameters,uint[],out double)"/>
    /// (auto-shade path) and <see cref="MakePreviewParams"/> so the two paths
    /// can never drift.</summary>
    internal static void FillAutoShadeDefaults(ref LightingFxData fx)
    {
        if (fx.AoSamples <= 0)        fx.AoSamples = 5;
        if (fx.AoStrength <= 0)       fx.AoStrength = 0.5;
        if (fx.ShadowSteps <= 0)      fx.ShadowSteps = 24;
        if (fx.ShadowLightMask == 0)  fx.ShadowLightMask = 0x1;
        if (fx.ShadowSoftK <= 0)      fx.ShadowSoftK = 8.0;
        if (fx.AmbientStrength <= 0)  fx.AmbientStrength = 0.3;
        if (fx.SpecularStrength <= 0) { fx.SpecularStrength = 0.25; if (fx.Roughness <= 0) fx.Roughness = 0.55; }
    }

    /// <summary>#155 — build a reduced-cost parameter set for the progressive
    /// PREVIEW raymarch. Forces supersample off and drops the heavy per-hit FX
    /// (DE-cone AO, SSAO, reflections, volumetric in-scatter) while keeping the
    /// cheap dominant depth cues (soft shadow + specular + ambient) IDENTICAL to
    /// the final frame — so the 3D preview frames the same as the final (no
    /// flat↔3D flash, #131), it just skips the expensive lighting the eye can't
    /// resolve on a transient preview. Auto-shade is baked in here and then
    /// switched off so <see cref="Render(uint[],float[],int,int,int,int,FractalParameters,uint[],out double)"/>
    /// won't refill the AO we just dropped.</summary>
    public static FractalParameters MakePreviewParams(FractalParameters p)
    {
        var pp = p.Clone();
        pp.Relief2DSupersample = 1;
        var fx = pp.Lighting;
        if (pp.Relief2DAutoShade) FillAutoShadeDefaults(ref fx);
        pp.Relief2DAutoShade = false;   // defaults baked → Render won't refill AO
        fx.AoSamples = 0;               // drop DE-cone AO (5 evals / hit)
        fx.SsaoSamples = 0;             // drop SSAO post-pass
        fx.ReflectionStrength = 0.0;    // drop reflection bounces (~24 evals / hit)
        fx.VolumeSteps = 0;             // drop volumetric in-scatter walk
        pp.Lighting = fx;
        return pp;
    }

    /// <summary>#155 — content + params signature that keys the pre-pass cache.
    /// FNV-1a over the raw field (one O(n) pass — cheap vs the ~8-pass filter
    /// chain it guards) folded with the dims and the two params that change the
    /// filter output (height curve, edge fade). The height SCALE is excluded on
    /// purpose: it enters only as sy afterwards, so a scale tweak reuses the
    /// cache.</summary>
    private static ulong PrepassKey(float[] height, int hn, int hw, int hh,
                                    HeightCurve2D curve, double edgeFade)
    {
        unchecked
        {
            const ulong FnvPrime = 1099511628211UL;
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < hn; i++)
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(height[i]);
                hash = (hash ^ bits) * FnvPrime;
            }
            hash = (hash ^ (uint)hw) * FnvPrime;
            hash = (hash ^ (uint)hh) * FnvPrime;
            hash = (hash ^ (uint)(int)curve) * FnvPrime;
            ulong ef = (ulong)BitConverter.DoubleToInt64Bits(edgeFade);
            hash = (hash ^ (ef & 0xFFFFFFFFUL)) * FnvPrime;
            hash = (hash ^ (ef >> 32)) * FnvPrime;
            return hash;
        }
    }

    /// <summary>#155 — max absolute per-cell height delta along X (horizontal
    /// neighbour) and Z (vertical neighbour), unitless. Parallel row reduction
    /// replacing the old two serial full-field scans. Multiplying by sy / world-
    /// cell-size reconstructs the world-space Lipschitz slope per call.</summary>
    private static (float gx, float gz) GridSlopeMaxima(float[] hbuf, int w, int h)
    {
        float gx = 0f, gz = 0f;
        object gate = new();
        Parallel.For(0, h, () => (0f, 0f), (y, _, local) =>
        {
            float lx = local.Item1, lz = local.Item2;
            int row = y * w;
            for (int x = 1; x < w; x++)
            {
                float d = Math.Abs(hbuf[row + x] - hbuf[row + x - 1]);
                if (d > lx) lx = d;
            }
            if (y > 0)
            {
                int prev = row - w;
                for (int x = 0; x < w; x++)
                {
                    float d = Math.Abs(hbuf[row + x] - hbuf[prev + x]);
                    if (d > lz) lz = d;
                }
            }
            return (lx, lz);
        }, local =>
        {
            lock (gate) { if (local.Item1 > gx) gx = local.Item1; if (local.Item2 > gz) gz = local.Item2; }
        });
        return (gx, gz);
    }

    /// <summary>#145 — clamp every cell to its 8-neighbour max plus a small
    /// margin (5% of the field max). Removes isolated single-cell needles
    /// (undersampled boundary at Mini / Toy sizes) while leaving connected
    /// ridges and filaments — whose crest has an almost-as-tall neighbour —
    /// intact. A no-op on high-resolution fields where the boundary is
    /// connected, so maximized / Span renders are unchanged.</summary>
    private static void DespikeNeighborMax(float[] hbuf, int w, int h)
    {
        int n = w * h;
        float maxH = 0f;
        for (int i = 0; i < n; i++) if (hbuf[i] > maxH) maxH = hbuf[i];
        if (maxH <= 1e-9f) return;
        float margin = 0.05f * maxH;

        // Read neighbours from a snapshot so in-place clamps don't cascade.
        float[] src = s_despikeSrc is { } s && s.Length >= n ? s : (s_despikeSrc = new float[n]);
        Array.Copy(hbuf, src, n);

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int ym = (y > 0 ? y - 1 : 0) * w;
            int yp = (y < h - 1 ? y + 1 : h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                float c = src[row + x];
                if (c <= margin) continue;   // already near the base — nothing to clamp
                int xm = x > 0 ? x - 1 : 0;
                int xp = x < w - 1 ? x + 1 : w - 1;
                float m = src[ym + xm], t;
                t = src[ym + x];  if (t > m) m = t;
                t = src[ym + xp]; if (t > m) m = t;
                t = src[row + xm]; if (t > m) m = t;
                t = src[row + xp]; if (t > m) m = t;
                t = src[yp + xm]; if (t > m) m = t;
                t = src[yp + x];  if (t > m) m = t;
                t = src[yp + xp]; if (t > m) m = t;
                float cap = m + margin;
                if (c > cap) hbuf[row + x] = cap;
            }
        }
    }

    /// <summary>#145 — small-window smoothing ramp. 0 at ≥920 px (min window
    /// dimension → maximized / Span untouched, signed-off view unchanged), rising
    /// to 1 at ≤200 px (Toy). Drives both the low-pass strength and the relief
    /// amplitude pull-down so the undersampled boundary reads gently at Mini /
    /// Toy sizes. Smoothstep so a resize never snaps the look.
    ///
    /// #147 — start raised 640 → 920 px: smoke-testing showed the boundary
    /// spikes actually onset around a ~980×580 window (min dim ≈ 580), well
    /// above the old 640 threshold, where the ramp was still ≈0 (untreated). At
    /// 920 the onset band sits at t ≈ 0.45 so the median + amplitude pull-down
    /// engage right where the comb first appears, while a maximized window (min
    /// dim ≳ 1000, even ~864 on a 125 %-scaled 1080p panel → t &lt; 0.02) stays
    /// visually identical to the signed-off large view. Mini / Toy (t ≈ 0.99 / 1)
    /// are unchanged vs. the 640 ramp.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ResolutionRamp(int w, int h)
        => Smoothstep((920.0 - Math.Min(w, h)) / (920.0 - 200.0));

    private static float[]? s_lowpassSrc;   // #145b box-blur neighbour-read snapshot

    /// <summary>#145b — resolution-adaptive box-blur low-pass of the compressed
    /// height field. Strength ramps from 0 at ≥480 px (min window dimension) up
    /// to ~3 passes at ≤200 px (Toy mode), blended continuously so there is no
    /// visible snap on resize. Zero cost / no-op at maximized / Span resolution,
    /// so the signed-off large-window view is unchanged; at Mini / Toy sizes it
    /// merges the undersampled boundary comb into a smooth ridge.</summary>
    private static void LowPassAdaptive(float[] hbuf, int w, int h)
    {
        double t = ResolutionRamp(w, h);
        if (t <= 0.0) return;

        // MEDIAN first (up to 3 passes). The undersampled boundary is a COMB of
        // alternating tall/short cells; a box blur only averages it (residual
        // ripple survives) whereas a 3×3 median rejects the outlier so the crest
        // collapses to a smooth ridge. Then a light box blur (up to 2 passes)
        // polishes the median's small plateaus. Both ramp with t and blend the
        // fractional last pass so a resize never snaps the look.
        double medAmt = t * 4.0;
        int medFull = (int)medAmt; double medFrac = medAmt - medFull;
        for (int k = 0; k < medFull; k++) Median3x3(hbuf, w, h, 1.0f);
        if (medFrac > 1e-3) Median3x3(hbuf, w, h, (float)medFrac);

        double blurAmt = t * 3.0;
        int bFull = (int)blurAmt; double bFrac = blurAmt - bFull;
        for (int k = 0; k < bFull; k++) BoxBlur3x3(hbuf, w, h, 1.0f);
        if (bFrac > 1e-3) BoxBlur3x3(hbuf, w, h, (float)bFrac);
    }

    /// <summary>One 3×3 median pass, result blended into <paramref name="hbuf"/>
    /// by <paramref name="amt"/>. Edge-clamped; reads from a snapshot (shares the
    /// low-pass scratch) so it is in-place. Rejects single-cell outliers — the
    /// boundary comb — that a linear blur only smears.</summary>
    private static void Median3x3(float[] hbuf, int w, int h, float amt)
    {
        int n = w * h;
        float[] src = s_lowpassSrc is { } s && s.Length >= n ? s : (s_lowpassSrc = new float[n]);
        Array.Copy(hbuf, src, n);
        Span<float> win = stackalloc float[9];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int ym = (y > 0 ? y - 1 : 0) * w;
            int yp = (y < h - 1 ? y + 1 : h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                int xm = x > 0 ? x - 1 : 0;
                int xp = x < w - 1 ? x + 1 : w - 1;
                win[0] = src[ym + xm]; win[1] = src[ym + x]; win[2] = src[ym + xp];
                win[3] = src[row + xm]; win[4] = src[row + x]; win[5] = src[row + xp];
                win[6] = src[yp + xm]; win[7] = src[yp + x]; win[8] = src[yp + xp];
                // Insertion sort of 9 — cheap, no allocation.
                for (int a = 1; a < 9; a++)
                {
                    float key = win[a]; int b = a - 1;
                    while (b >= 0 && win[b] > key) { win[b + 1] = win[b]; b--; }
                    win[b + 1] = key;
                }
                float med = win[4];
                float c = src[row + x];
                hbuf[row + x] = c + (med - c) * amt;
            }
        }
    }

    /// <summary>One 3×3 box-blur pass, result blended into <paramref name="hbuf"/>
    /// by <paramref name="amt"/> (1 = full blur, &lt;1 = partial for a smooth
    /// fractional pass). Edge-clamped; reads from a snapshot so it is in-place.</summary>
    private static void BoxBlur3x3(float[] hbuf, int w, int h, float amt)
    {
        int n = w * h;
        float[] src = s_lowpassSrc is { } s && s.Length >= n ? s : (s_lowpassSrc = new float[n]);
        Array.Copy(hbuf, src, n);
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int ym = (y > 0 ? y - 1 : 0) * w;
            int yp = (y < h - 1 ? y + 1 : h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                int xm = x > 0 ? x - 1 : 0;
                int xp = x < w - 1 ? x + 1 : w - 1;
                float sum = src[ym + xm] + src[ym + x] + src[ym + xp]
                          + src[row + xm] + src[row + x] + src[row + xp]
                          + src[yp + xm] + src[yp + x] + src[yp + xp];
                float avg = sum * (1f / 9f);
                float c = src[row + x];
                hbuf[row + x] = c + (avg - c) * amt;
            }
        }
    }

    /// <summary>Smoothstep on [0,1] (3t²−2t³).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Smoothstep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>Per-channel lerp of two packed ARGB colours by t (0 = a, 1 = b),
    /// alpha included so the terrain can dissolve toward a transparent background.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BlendArgb(uint a, uint b, double t)
    {
        double it = 1.0 - t;
        uint A = (uint)(((a >> 24) & 0xFF) * it + ((b >> 24) & 0xFF) * t + 0.5);
        uint R = (uint)(((a >> 16) & 0xFF) * it + ((b >> 16) & 0xFF) * t + 0.5);
        uint G = (uint)(((a >> 8) & 0xFF) * it + ((b >> 8) & 0xFF) * t + 0.5);
        uint B = (uint)((a & 0xFF) * it + (b & 0xFF) * t + 0.5);
        return (A << 24) | (R << 16) | (G << 8) | B;
    }

    /// <summary>#135 — build the per-cell keep mask (0 = culled). Returns null
    /// when isolation is off or no cull selector is active (keep everything; the
    /// transparent background is applied at the miss site regardless).</summary>
    private static byte[]? BuildKeepMask(float[] hbuf, int w, int h,
                                         uint[] albedo, int aw, int ah, FractalParameters p)
    {
        if (!p.Relief2DIsolate) return null;
        bool byDetail = p.Relief2DIsolateByDetail;
        uint[] drops = ParseDropColors(p.Relief2DDropColorsCsv);
        bool byColor = p.Relief2DIsolateByColor && drops.Length > 0;
        if (!byDetail && !byColor) return null;   // isolate bg only, keep all surface

        int n = w * h;   // FIELD cell count (mask grid)
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
                // Map the field cell to the albedo (output-grid) pixel by
                // normalised coords — the two grids may differ in resolution (#143).
                int ax = Math.Clamp((int)((x + 0.5) / w * aw), 0, aw - 1);
                int ay = Math.Clamp((int)((y + 0.5) / h * ah), 0, ah - 1);
                uint a = albedo[ay * aw + ax];
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
    internal static bool SlabHit(double o, double dcomp, double lo, double hi,
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
