// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// RandomTileCalculator.cs
//
// Random space filling of the plane — Paul Bourke,
// https://paulbourke.net/fractals/randomtile/
//
// Non-escape-time 2D packing. Shapes of power-law-decreasing size are placed at
// random, non-overlapping positions until the plane is filled (or the shape
// count / sub-pixel floor is hit). The i-th shape radius follows
//   r_i = rMax / (i + 1)^(1/α)
// where α (RandomTileSizeExponent) controls the size falloff: larger α yields a
// few big shapes plus a heavy tail of tiny ones. Each candidate position is
// drawn from a single seeded PRNG (RandomTileSeed) and accepted only when it
// clears every previously placed shape by RandomTileGap; rejected candidates
// retry up to a fixed attempt budget, then that index is skipped (the radius
// keeps shrinking, so later shapes slot into the remaining gaps).
//
// Overlap test uses a uniform spatial-hash grid (cell = rMax), so each candidate
// only checks the shapes in its neighbourhood — ~O(N) placement instead of the
// naive O(N²). Same acceleration DlaCalculator uses for aggregate proximity.
//
// Determinism: (Width, Height, Seed, Count, SizeExponent, Gap) uniquely
// determine the output — one seeded Random drives every draw in a fixed order.
// SupportsZoomPan is false: the packing IS the image and pan/zoom can't reuse it
// (mirrors DLA). Relief: each disk is a raised sphere-cap dome written to
// SmoothBuffer, so Relief3D / 3D themes / volumetric ride the shared
// IHeightFieldSource path exactly as ApollonianCalculator does.

using System;
using System.Collections.Generic;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class RandomTileCalculator : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // Relief 3D height field. RandomTile has no escape/iteration count, so the
    // relief is synthesised geometrically: each shape is a raised dome
    // (sphere-cap profile, unit amplitude), matching the Apollonian sphere-
    // imposter shading. Written during rasterisation; smallest (last-painted)
    // shape wins per pixel.
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 0;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    // The packing IS the image; pan/zoom would invalidate the cached tiling.
    public bool SupportsZoomPan => false;

    public FractalParameters FractalParameters { get; set; } = new();

    // Dome relief amplitude for the sphere-imposter paint path (see PaintDisk).
    private double _relief = 1.0;

    // #338 — placement cache. The rejection-sampling placement is O(N) and
    // depends only on the geometry + placement params (NOT relief / colour), so
    // shading-only re-renders (relief-knob drag, relief/colour animation) reuse
    // the tile list and re-run just the paint pass.
    private List<Tile>? _cachedTiles;
    private PlacementKey _cacheKey;
    private bool _cacheValid;

    private readonly struct PlacementKey : IEquatable<PlacementKey>
    {
        public readonly int W, H, Seed, Count;
        public readonly double CX, CY, Zoom, Alpha, Gap, MinPx;
        public readonly RandomTileShape Shape;
        public PlacementKey(int w, int h, double cx, double cy, double zoom,
            int seed, int count, double alpha, double gap, double minPx,
            RandomTileShape shape)
        { W = w; H = h; CX = cx; CY = cy; Zoom = zoom; Seed = seed; Count = count;
          Alpha = alpha; Gap = gap; MinPx = minPx; Shape = shape; }

        public bool Equals(PlacementKey o) =>
            W == o.W && H == o.H && Seed == o.Seed && Count == o.Count &&
            CX == o.CX && CY == o.CY && Zoom == o.Zoom && Alpha == o.Alpha &&
            Gap == o.Gap && MinPx == o.MinPx && Shape == o.Shape;
        public override bool Equals(object? o) => o is PlacementKey k && Equals(k);
        public override int GetHashCode() =>
            HashCode.Combine(W, H, Seed, Count, CX, CY, Zoom,
                HashCode.Combine(Alpha, Gap, MinPx, (int)Shape));
    }

    public RandomTileCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
    }

    private readonly struct Tile
    {
        public readonly double X;      // world centre
        public readonly double Y;
        public readonly double R;      // world circumradius
        public readonly double Angle;  // rotation (rad); 0 for circles
        public readonly int Index;     // placement order (0 = biggest)
        public Tile(double x, double y, double r, double angle, int index)
        { X = x; Y = y; R = r; Angle = angle; Index = index; }
    }

    public void Calculate(CancellationToken ct = default)
    {
        Array.Clear(ColorBuffer, 0, ColorBuffer.Length);
        Array.Clear(SmoothBuffer, 0, SmoothBuffer.Length);   // relief base plane

        int count = Math.Max(0, FractalParameters.RandomTileCount);
        double alpha = Math.Max(0.2, FractalParameters.RandomTileSizeExponent);
        double gap = Math.Max(0.0, FractalParameters.RandomTileGap);
        double minPx = Math.Max(0.25, FractalParameters.RandomTileMinPixelRadius);
        RandomTileShape shape = FractalParameters.RandomTileShape;
        _relief = Math.Clamp(FractalParameters.RandomTileRelief, 0.0, 4.0);

        // World→screen scale matches the standard 2D convention: pixel pitch =
        // (4 / Width) / Zoom, fractal origin at (CenterX, CenterY).
        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        double minWorldR = minPx * pixelPitch;

        // Visible world half-extents (the domain we fill).
        double halfW = Width * 0.5 * pixelPitch;
        double halfH = Height * 0.5 * pixelPitch;
        double rMax = 0.5 * Math.Min(halfW, halfH);
        if (rMax < minWorldR || count == 0) return;

        // Colour spans the palette across placement order (big → small) when
        // colouring by index; the log-radius fallback emphasises scale instead.
        // Colour-only param — never part of the placement cache key.
        ColorMap.MaxIterations = Math.Max(16, count);

        // #338 — reuse the cached placement when nothing that affects it changed.
        var key = new PlacementKey(Width, Height, CenterX, CenterY, Zoom,
            FractalParameters.RandomTileSeed, count, alpha, gap, minPx, shape);
        List<Tile>? tiles = (_cacheValid && _cachedTiles != null && _cacheKey.Equals(key))
            ? _cachedTiles
            : null;

        if (tiles == null)
        {
            tiles = BuildTiles(count, alpha, gap, minPx, rMax,
                CenterX - halfW, CenterX + halfW, CenterY - halfH, CenterY + halfH,
                minWorldR, shape, ct);
            if (ct.IsCancellationRequested) return;   // don't cache a partial pass
            _cachedTiles = tiles;
            _cacheKey = key;
            _cacheValid = true;
        }

        // Tiles are generated in strictly-decreasing-radius order, so painting in
        // list order draws biggest first and smaller shapes nest on top.
        foreach (var t in tiles)
        {
            if (ct.IsCancellationRequested) return;
            PaintDisk(t);
        }
    }

    // Rejection-sample the non-overlapping packing. Pure function of the geometry
    // + placement params (the PlacementKey fields); relief / colour never enter
    // here, which is what lets Calculate cache the result across shading changes.
    private List<Tile> BuildTiles(
        int count, double alpha, double gap, double minPx, double rMax,
        double x0, double x1, double y0, double y1, double minWorldR,
        RandomTileShape shape, CancellationToken ct)
    {
        // Uniform spatial hash. Cell = rMax; two tiles overlap only if their
        // centres are within r_a + r_b ≤ 2·rMax, so a candidate need only probe
        // cells within ⌈(r_cand + rMax)/cell⌉ = 2 of its own. Cell indices are
        // domain-relative (offset by x0/y0) and packed into a long key.
        double cell = Math.Max(rMax, 1e-12);
        var grid = new Dictionary<long, List<int>>(Math.Max(64, count));
        var tiles = new List<Tile>(count);

        static long PackCell(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
        int CellX(double wx) => (int)Math.Floor((wx - x0) / cell);
        int CellY(double wy) => (int)Math.Floor((wy - y0) / cell);

        int vertCount = shape == RandomTileShape.Square ? 4
                      : shape == RandomTileShape.Triangle ? 3 : 0;

        // Overlap test. Broad phase is the circumcircle (cell-bucketed): if the
        // circumcircles clear by the margin, the shapes cannot touch. Circles
        // stop there (the circumcircle IS the shape — byte-identical to before).
        // Polygons then run an SAT narrow phase against the ACTUAL rotated shape,
        // so squares/triangles pack tight instead of each reserving a full circle
        // of empty space. cvx/cvy carry the candidate's world vertices; nvx/nvy
        // is scratch for the neighbour (both are the same shape).
        bool Overlaps(double cx, double cy, double r, double margin, int cn,
            Span<double> cvx, Span<double> cvy, Span<double> nvx, Span<double> nvy)
        {
            int ccx = CellX(cx), ccy = CellY(cy);
            for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
            {
                if (!grid.TryGetValue(PackCell(ccx + dx, ccy + dy), out var bucket))
                    continue;
                foreach (int ti in bucket)
                {
                    var t = tiles[ti];
                    double ex = t.X - cx, ey = t.Y - cy;
                    double circ = t.R + r + margin;
                    if (ex * ex + ey * ey >= circ * circ) continue;   // circumcircles clear
                    if (cn == 0) return true;                          // circle — real overlap
                    BuildVerts(t.X, t.Y, t.R, t.Angle, shape, nvx, nvy);
                    if (!PolysClear(cvx, cvy, cn, nvx, nvy, cn, margin)) return true;
                }
            }
            return false;
        }

        var rng = new Random(FractalParameters.RandomTileSeed);
        const int maxAttempts = 200;

        // Vertex scratch buffers — allocated ONCE (never inside the attempt loop,
        // which would grow the stack). Max 4 verts (square).
        Span<double> cvx = stackalloc double[4];
        Span<double> cvy = stackalloc double[4];
        Span<double> nvx = stackalloc double[4];
        Span<double> nvy = stackalloc double[4];

        for (int i = 0; i < count; i++)
        {
            if ((i & 0x3FF) == 0 && ct.IsCancellationRequested) return tiles;

            double r = rMax / Math.Pow(i + 1, 1.0 / alpha);
            if (r < minWorldR) break;                       // sub-pixel — done

            double margin = gap * r;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                // Draw a centre so the shape fits inside the visible domain.
                double cxw = x0 + r + rng.NextDouble() * Math.Max(0.0, (x1 - x0) - 2 * r);
                double cyw = y0 + r + rng.NextDouble() * Math.Max(0.0, (y1 - y0) - 2 * r);

                // Random per-tile rotation for polygons, drawn BEFORE the overlap
                // test (the rotation decides whether the shape actually fits).
                // Circles are rotation-invariant, so the draw is skipped — keeping
                // the circle RNG stream byte-identical to the shape-less path.
                double ang = shape == RandomTileShape.Circle
                    ? 0.0
                    : rng.NextDouble() * (2.0 * Math.PI);
                if (vertCount > 0) BuildVerts(cxw, cyw, r, ang, shape, cvx, cvy);

                if (Overlaps(cxw, cyw, r, margin, vertCount, cvx, cvy, nvx, nvy)) continue;

                int idx = tiles.Count;
                tiles.Add(new Tile(cxw, cyw, r, ang, i));
                long ckey = PackCell(CellX(cxw), CellY(cyw));
                if (!grid.TryGetValue(ckey, out var b))
                    grid[ckey] = b = new List<int>(4);
                b.Add(idx);
                break;
                // A skipped index (all attempts overlapped) is fine — the radius
                // keeps shrinking so smaller shapes slot into the remaining gaps.
            }
        }

        return tiles;
    }

    // World-space vertices of a tile inscribed in circumradius R (square corners
    // at Angle+45°+k·90°; triangle vertices at Angle+90°+k·120°). Matches the
    // paint mask so collision and pixels agree.
    private static void BuildVerts(double cx, double cy, double r, double ang,
        RandomTileShape shape, Span<double> vx, Span<double> vy)
    {
        if (shape == RandomTileShape.Square)
        {
            for (int k = 0; k < 4; k++)
            {
                double a = ang + Math.PI / 4.0 + k * (Math.PI / 2.0);
                vx[k] = cx + r * Math.Cos(a);
                vy[k] = cy + r * Math.Sin(a);
            }
        }
        else // Triangle
        {
            for (int k = 0; k < 3; k++)
            {
                double a = ang + Math.PI / 2.0 + k * (2.0 * Math.PI / 3.0);
                vx[k] = cx + r * Math.Cos(a);
                vy[k] = cy + r * Math.Sin(a);
            }
        }
    }

    // Separating Axis Theorem for two convex polygons. Returns true when a
    // separating axis exists with at least `margin` clearance (i.e. the shapes
    // are apart by the requested gap). Axes are the edge normals of both polys.
    private static bool PolysClear(
        Span<double> ax, Span<double> ay, int na,
        Span<double> bx, Span<double> by, int nb, double margin)
    {
        for (int poly = 0; poly < 2; poly++)
        {
            Span<double> ex = poly == 0 ? ax : bx;
            Span<double> ey = poly == 0 ? ay : by;
            int ne = poly == 0 ? na : nb;
            for (int k = 0; k < ne; k++)
            {
                int k2 = (k + 1) % ne;
                double nx = ey[k2] - ey[k];        // edge normal = (dy, −dx)
                double ny = -(ex[k2] - ex[k]);
                double len = Math.Sqrt(nx * nx + ny * ny);
                if (len < 1e-12) continue;
                nx /= len; ny /= len;

                double amin = double.PositiveInfinity, amax = double.NegativeInfinity;
                for (int j = 0; j < na; j++)
                {
                    double p = ax[j] * nx + ay[j] * ny;
                    if (p < amin) amin = p;
                    if (p > amax) amax = p;
                }
                double bmin = double.PositiveInfinity, bmax = double.NegativeInfinity;
                for (int j = 0; j < nb; j++)
                {
                    double p = bx[j] * nx + by[j] * ny;
                    if (p < bmin) bmin = p;
                    if (p > bmax) bmax = p;
                }

                // Separation along this axis (positive = apart). If any axis
                // clears by the margin, the shapes are acceptably apart.
                double sep = Math.Max(amin - bmax, bmin - amax);
                if (sep >= margin) return true;
            }
        }
        return false;
    }

    private void PaintDisk(in Tile t)
    {
        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        if (pixelPitch <= 0.0) return;
        double invPitch = 1.0 / pixelPitch;

        // World → screen (pixel) coords.
        double sx = (t.X - CenterX) * invPitch + Width * 0.5;
        double sy = (t.Y - CenterY) * invPitch + Height * 0.5;
        double sr = t.R * invPitch;

        int x0 = (int)Math.Floor(sx - sr);
        int x1 = (int)Math.Ceiling(sx + sr);
        int y0 = (int)Math.Floor(sy - sr);
        int y1 = (int)Math.Ceiling(sy + sr);

        if (x1 < 0 || y1 < 0 || x0 >= Width || y0 >= Height) return;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(Width - 1, x1); y1 = Math.Min(Height - 1, y1);

        // Colour by placement index (palette sweep big→small) or log-radius.
        bool byIndex = FractalParameters.RandomTileColorByIndex;
        float tt = byIndex
            ? t.Index % Math.Max(1, ColorMap.MaxIterations)
            : (float)(Math.Log(Math.Max(1e-12, t.R)) * -16.0);

        double r2 = sr * sr;

        RandomTileShape shape = FractalParameters.RandomTileShape;

        // ── Circle — radial sphere-cap dome (byte-identical to P1/P2). ──
        if (shape == RandomTileShape.Circle)
        {
            if (_relief <= 0.0)
            {
                uint col0 = (uint)ColorMap.Map(tt, 0f, ColorMap.MaxIterations);
                for (int py = y0; py <= y1; py++)
                {
                    double dy = py - sy;
                    int row = py * Width;
                    for (int px = x0; px <= x1; px++)
                    {
                        double dx = px - sx;
                        double dd = dx * dx + dy * dy;
                        if (dd <= r2)
                        {
                            ColorBuffer[row + px] = col0;
                            SmoothBuffer[row + px] = (float)Math.Sqrt(1.0 - dd / r2);
                        }
                    }
                }
                return;
            }

            double invSr = sr > 1e-9 ? 1.0 / sr : 0.0;
            for (int py = y0; py <= y1; py++)
            {
                double dy = py - sy;
                int row = py * Width;
                for (int px = x0; px <= x1; px++)
                {
                    double dx = px - sx;
                    double dd = dx * dx + dy * dy;
                    if (dd > r2) continue;
                    float nx = (float)(_relief * dx * invSr);
                    float ny = (float)(-_relief * dy * invSr);
                    ColorBuffer[row + px] =
                        (uint)ColorMap.Map(tt, 0f, ColorMap.MaxIterations, nx, ny);
                    SmoothBuffer[row + px] = (float)Math.Sqrt(1.0 - dd / r2);
                }
            }
            return;
        }

        // ── Square / Triangle — shape-correct SDF cap (#336). ──
        // Test in the tile-local frame (rotate the pixel offset by −Angle). Height
        // is a dome over the normalised distance-to-nearest-edge (peak at the
        // incentre, 0 at the boundary) — NOT the circle's radial dome — so corners
        // no longer sink to zero. The in-plane normal is the nearest edge's OUTWARD
        // normal (rotated back to screen), tilting downslope toward that edge and
        // growing to the rim — the polygon analogue of the circle rim normal.
        const double C30 = 0.86602540378443865; // cos30 = √3/2
        double cosA = Math.Cos(t.Angle), sinA = Math.Sin(t.Angle);
        double halfSq = sr * 0.70710678118654752; // 1/√2 — square inscribed in circumradius
        double rInTri = 0.5 * sr;                  // equilateral inradius = R/2

        // (h, sox, soy) for a pixel offset, or false when outside the shape.
        bool CapPoint(double dx, double dy, out float h, out double sox, out double soy)
        {
            h = 0f; sox = 0; soy = 0;
            double lx = dx * cosA + dy * sinA;      // screen → local (rotate −Angle)
            double ly = -dx * sinA + dy * cosA;
            double dEdge, rIn, enx, eny;

            if (shape == RandomTileShape.Square)
            {
                double ax = Math.Abs(lx), ay = Math.Abs(ly);
                if (ax > halfSq || ay > halfSq) return false;
                rIn = halfSq;
                if (halfSq - ax <= halfSq - ay) { dEdge = halfSq - ax; enx = lx >= 0 ? 1 : -1; eny = 0; }
                else { dEdge = halfSq - ay; enx = 0; eny = ly >= 0 ? 1 : -1; }
            }
            else // Triangle — upward equilateral, circumradius sr, edges' outward
            {    // normals at 270° / 30° / 150°, each an inradius from the centre.
                double d0 = rInTri + ly;                    // bottom  n = (0,−1)
                double d1 = rInTri - (C30 * lx + 0.5 * ly); // right   n = (C30, 0.5)
                double d2 = rInTri - (-C30 * lx + 0.5 * ly);// left    n = (−C30, 0.5)
                if (d0 < 0 || d1 < 0 || d2 < 0) return false;
                rIn = rInTri;
                dEdge = d0; enx = 0; eny = -1;
                if (d1 < dEdge) { dEdge = d1; enx = C30; eny = 0.5; }
                if (d2 < dEdge) { dEdge = d2; enx = -C30; eny = 0.5; }
            }

            double dNorm = dEdge / rIn;
            if (dNorm > 1.0) dNorm = 1.0; else if (dNorm < 0.0) dNorm = 0.0;
            h = (float)Math.Sqrt(dNorm * (2.0 - dNorm));    // dome: 0 at edge, 1 at incentre
            double mag = _relief * (1.0 - dNorm);           // rim-growing, like the circle
            sox = mag * (enx * cosA - eny * sinA);          // local → screen (rotate +Angle)
            soy = mag * (enx * sinA + eny * cosA);
            return true;
        }

        uint flatCol = _relief <= 0.0 ? (uint)ColorMap.Map(tt, 0f, ColorMap.MaxIterations) : 0u;
        for (int py = y0; py <= y1; py++)
        {
            double dy = py - sy;
            int row = py * Width;
            for (int px = x0; px <= x1; px++)
            {
                double dx = px - sx;
                if (!CapPoint(dx, dy, out float h, out double sox, out double soy)) continue;
                ColorBuffer[row + px] = _relief <= 0.0
                    ? flatCol
                    : (uint)ColorMap.Map(tt, 0f, ColorMap.MaxIterations, (float)sox, (float)-soy);
                SmoothBuffer[row + px] = h;
            }
        }
    }
}
