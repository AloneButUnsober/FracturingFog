// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ApollonianCalculator.cs
//
// Apollonian gasket — recursive circle packing via Descartes Circle Theorem.
// Not escape-time. Generates the integral (−1, 2, 2, 3) seed quadruple, then
// Vieta-jumps each non-outer circle through the other three to spawn child
// quadruples, draws every circle in solid colour, recurses until radius shrinks
// below one device pixel or depth limit is hit.
//
// Descartes (curvature) form:
//   (k₁+k₂+k₃+k₄)² = 2·(k₁²+k₂²+k₃²+k₄²)
// Complex Descartes (centres × curvature):
//   (k₁z₁+k₂z₂+k₃z₃+k₄z₄)² = 2·(k₁²z₁²+k₂²z₂²+k₃²z₃²+k₄²z₄²)
// Vieta jump (replace k₄): k₄' = 2(k₁+k₂+k₃) − k₄
//                          z₄'·k₄' = 2(k₁z₁+k₂z₂+k₃z₃) − k₄·z₄
// Negative curvature = enclosing circle (signed convention).
//
// SupportsZoomPan = true: pan/zoom transforms circles through (CenterX,
// CenterY, Zoom) before raster fill so deeper recursion auto-reveals as the
// user zooms in.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class ApollonianCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 0;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    // Dome relief for the sphere-imposter paint path, read once per Calculate().
    // > 0 (default): every disk is painted through the 5-param Map overload with
    // a per-pixel surface normal, so ANY theme that reads (nx, ny) shows relief —
    // matching how the escape-time calculators pass normals unconditionally
    // (a 3D theme that forgot to self-declare UsesNormals still lit up on
    // Mandelbrot but rendered flat here when this was capability-gated). Flat 2D
    // themes ignore the normal (default interface method) so they're unchanged.
    // == 0: single-colour-per-disk fast path (user explicitly flattened relief).
    private double _relief = 1.0;

    public ApollonianCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    private readonly struct Circle
    {
        public readonly Complex Z;       // centre (complex)
        public readonly double K;         // curvature (1/r, negative if enclosing)
        public readonly int Depth;
        public Circle(Complex z, double k, int depth) { Z = z; K = k; Depth = depth; }
        public double R => 1.0 / Math.Abs(K);
    }

    public void Calculate(CancellationToken ct = default)
    {
        // Clear to background.
        Array.Clear(ColorBuffer, 0, ColorBuffer.Length);

        int maxDepth = Math.Max(0, FractalParameters.ApollonianDepth);
        double minPx  = Math.Max(0.25, FractalParameters.ApollonianMinPixelRadius);

        // World→screen scale matches the standard 2D fractal convention used
        // elsewhere in the engine: pixel pitch = (4 / Width) / Zoom, fractal
        // origin at (CenterX, CenterY).
        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        double minWorldR  = minPx * pixelPitch;

        ColorMap.MaxIterations = Math.Max(8, maxDepth + 4);

        _relief = Math.Clamp(FractalParameters.ApollonianRelief, 0.0, 4.0);

        // Seed: integral (−1, 2, 2, 3) gasket — outer unit disk, two half-radius
        // circles tangent on the diameter, plus the upper third-radius circle
        // tangent to all three (complex Descartes gives ±2i/3).
        var outer = new Circle(Complex.Zero,                   -1.0, 0);
        var left  = new Circle(new Complex(-0.5, 0.0),          2.0, 1);
        var right = new Circle(new Complex( 0.5, 0.0),          2.0, 1);
        var top   = new Circle(new Complex( 0.0,  2.0 / 3.0),   3.0, 1);
        var bot   = new Circle(new Complex( 0.0, -2.0 / 3.0),   3.0, 1);

        var all = new List<Circle>(8192) { outer, left, right, top, bot };

        // Root quadruples are {O, L, R, T} and {O, L, R, B} — the only two
        // mutually tangent 4-tuples in the (−1, 2, 2, 3) seed (T and B are not
        // tangent to each other, so {L, R, T, B} is not mutually tangent and
        // must NOT be used as a Descartes root). From each root, Vieta-jump all
        // 4 circles to seed the recursion tree; subsequent calls skip the just-
        // replaced index so we don't immediately backtrack.
        ExpandQuadruple(outer, left, right, top, -1, all, 2, maxDepth, minWorldR, ct);
        ExpandQuadruple(outer, left, right, bot, -1, all, 2, maxDepth, minWorldR, ct);

        if (ct.IsCancellationRequested) return;

        // Paint biggest first so smaller children overwrite — natural nesting.
        all.Sort((p, q) => q.R.CompareTo(p.R));

        foreach (var c in all)
        {
            if (ct.IsCancellationRequested) return;
            PaintDisk(c);
        }
    }

    // Expand a mutually-tangent 4-tuple by Vieta-jumping each circle in turn
    // (except the one at `skip`, which was just produced by the parent — re-
    // jumping it would step straight back to the parent quadruple). Each new
    // circle becomes the replacement in a child quadruple that recurses with
    // the matching skip index.
    private static void ExpandQuadruple(
        Circle c0, Circle c1, Circle c2, Circle c3,
        int skip,
        List<Circle> all,
        int depth, int maxDepth, double minWorldR,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (depth > maxDepth) return;

        if (skip != 0) TryReflect(0, c0, c1, c2, c3, all, depth, maxDepth, minWorldR, ct);
        if (skip != 1) TryReflect(1, c0, c1, c2, c3, all, depth, maxDepth, minWorldR, ct);
        if (skip != 2) TryReflect(2, c0, c1, c2, c3, all, depth, maxDepth, minWorldR, ct);
        if (skip != 3) TryReflect(3, c0, c1, c2, c3, all, depth, maxDepth, minWorldR, ct);
    }

    private static void TryReflect(
        int replaceIdx,
        Circle c0, Circle c1, Circle c2, Circle c3,
        List<Circle> all,
        int depth, int maxDepth, double minWorldR,
        CancellationToken ct)
    {
        // Pull out the circle being replaced + the sum of the kept three.
        Circle target = replaceIdx switch { 0 => c0, 1 => c1, 2 => c2, _ => c3 };
        double sumK   = c0.K + c1.K + c2.K + c3.K - target.K;
        Complex sumKz = c0.K * c0.Z + c1.K * c1.Z + c2.K * c2.Z + c3.K * c3.Z
                      - target.K * target.Z;

        double newK = 2.0 * sumK - target.K;
        if (newK <= 0.0) return;                       // skip enclosing reflections
        double newR = 1.0 / newK;
        if (newR < minWorldR) return;                  // sub-pixel — stop

        Complex newZ = (2.0 * sumKz - target.K * target.Z) / newK;
        var fresh = new Circle(newZ, newK, depth);
        all.Add(fresh);

        // Recurse: substitute fresh in for target, mark fresh's slot as the
        // one to skip so the next call won't jump back.
        switch (replaceIdx)
        {
            case 0: ExpandQuadruple(fresh, c1,    c2,    c3,    0, all, depth + 1, maxDepth, minWorldR, ct); break;
            case 1: ExpandQuadruple(c0,    fresh, c2,    c3,    1, all, depth + 1, maxDepth, minWorldR, ct); break;
            case 2: ExpandQuadruple(c0,    c1,    fresh, c3,    2, all, depth + 1, maxDepth, minWorldR, ct); break;
            default: ExpandQuadruple(c0,   c1,    c2,    fresh, 3, all, depth + 1, maxDepth, minWorldR, ct); break;
        }
    }

    private void PaintDisk(Circle c)
    {
        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        if (pixelPitch <= 0.0) return;
        double invPitch = 1.0 / pixelPitch;

        // World → screen (pixel) coords.
        double sx = (c.Z.Real - CenterX) * invPitch + Width  * 0.5;
        double sy = (c.Z.Imaginary - CenterY) * invPitch + Height * 0.5;
        double sr = c.R * invPitch;

        int x0 = (int)Math.Floor(sx - sr);
        int x1 = (int)Math.Ceiling(sx + sr);
        int y0 = (int)Math.Floor(sy - sr);
        int y1 = (int)Math.Ceiling(sy + sr);

        if (x1 < 0 || y1 < 0 || x0 >= Width || y0 >= Height) return;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(Width - 1, x1); y1 = Math.Min(Height - 1, y1);

        // Colour driven by depth — wraps the active palette.
        bool byDepth = FractalParameters.ApollonianColorByDepth;
        float t = byDepth
            ? c.Depth % Math.Max(1, ColorMap.MaxIterations)
            : (float)(Math.Log(Math.Max(1e-12, c.R)) * -16.0); // log-radius fallback

        double r2 = sr * sr;

        if (_relief <= 0.0)
        {
            // Flat fast path — one colour for the whole disk (user flattened
            // relief; bit-identical to the pre-3D behaviour).
            uint col = (uint)ColorMap.Map(t, 0f, ColorMap.MaxIterations);
            for (int py = y0; py <= y1; py++)
            {
                double dy = py - sy;
                int row = py * Width;
                for (int px = x0; px <= x1; px++)
                {
                    double dx = px - sx;
                    if (dx * dx + dy * dy <= r2)
                        ColorBuffer[row + px] = col;
                }
            }
            return;
        }

        // Lit sphere-imposter path — each disk is a dome. The in-plane surface
        // normal grows from the centre (flat, facing the viewer) to the rim
        // (grazing): (u, v) = (dx, dy) / sr, scaled by the relief knob. ny is
        // negated to convert screen-space y-down to the complex-plane y-up
        // convention the 3D themes expect (NormalFromRaw negates it back).
        double invSr = sr > 1e-9 ? 1.0 / sr : 0.0;
        for (int py = y0; py <= y1; py++)
        {
            double dy = py - sy;
            int row = py * Width;
            for (int px = x0; px <= x1; px++)
            {
                double dx = px - sx;
                if (dx * dx + dy * dy > r2) continue;
                float nx = (float)(_relief * dx * invSr);
                float ny = (float)(-_relief * dy * invSr);
                ColorBuffer[row + px] =
                    (uint)ColorMap.Map(t, 0f, ColorMap.MaxIterations, nx, ny);
            }
        }
    }
}
