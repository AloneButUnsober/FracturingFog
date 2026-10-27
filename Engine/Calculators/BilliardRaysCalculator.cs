// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BilliardRaysCalculator.cs (#1506, slice S1 of #1505)
//
// The chaotic billiard seen from above. ChaoticBilliard maps launch conditions
// (each pixel is one whole ray); this renders the rays themselves in real space:
// a sweep emits many rays into the disk array, each is traced with its path
// recorded (BilliardScene.Trace, the same physics and scene as ChaoticBilliard,
// including Custom layouts #1504), and every path segment adds its in-pixel length
// to a density grid. The density is log-normalised and coloured like the
// Buddhabrot family's ColorMap composite; disks are drawn on top.
//
// View: the standard 2D plane mapping (ChaoticBilliardCalculator.PlaneAt), with
// world y = -plane y so the picture is y-up like the ray-path inset while pans
// still follow the pointer.
//
// Emission order is the sweep order, but rays do not interact, so a still image
// does not depend on it; it matters only for the crossing slices (#1508+).
//
// Determinism: rays are traced in parallel into per-ray arrays, then rasterised
// in parallel by row band, each band visiting every segment in ray order. Every
// pixel therefore sums its contributions in one fixed order, whatever the thread
// count, so live, batch and export render the same image. Rays are processed in
// chunks so memory stays bounded at large counts.
//
// Rasterisation: each segment is clipped to the row band with the parametric line
// clip of Liang & Barsky (ACM TOG 3(1), 1984), then walked cell by cell with the
// 2D form of the voxel traversal of Amanatides & Woo (Eurographics 1987). Each pixel
// a segment crosses receives exactly the length inside it, so the grid total equals
// the traced length inside the view. (Both in Docs/Resources-Bibliography.md.)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

/// <summary>One emitted ray of a <see cref="BilliardRaySweep"/> (#1506).</summary>
public readonly record struct BilliardRayLaunch(BilliardRayLaunch.LaunchKind Kind, double A, double B, double C, double D)
{
    public enum LaunchKind
    {
        /// <summary>Far field: A = impact parameter b, B = incoming angle phi (radians).</summary>
        FarField,
        /// <summary>From a point: (A, B) start, (C, D) unit direction.</summary>
        Point,
        /// <summary>Off disk 0: A = boundary angle s (radians), B = sin of the outgoing angle.</summary>
        PhaseSpace,
    }
}

public sealed class BilliardRaysCalculator : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    /// <summary>Normalised log density in [0, 1] (the Relief 3D height field).</summary>
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    /// <summary>Length-weighted density of the last render, world units per pixel.</summary>
    public double[] Density { get; private set; } = Array.Empty<double>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 0.6;
    public int MaxIterations { get; set; } = 256;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <summary>Rays emitted / drawn (enough bounces) in the last render.</summary>
    public int LastRaysEmitted { get; private set; }
    public int LastRaysDrawn { get; private set; }

    /// <summary>Rays traced and rasterised together; bounds the stored paths.</summary>
    public const int ChunkSize = 16_384;

    public const int MaxRays = 2_000_000;

    public const uint DiskFillColor = 0xFF262626;
    public const uint DiskEdgeColor = 0xFF8C8C8C;

    public BilliardRaysCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
        Density = new double[width * height];
    }

    /// <summary>World point at the centre of pixel (x, y) is PlaneAt(x, y) with y negated.</summary>
    public static (double X, double Y) WorldAt(double x, double y, double width, double height,
                                               double centerX, double centerY, double zoom)
    {
        var (px, py) = ChaoticBilliardCalculator.PlaneAt(x, y, width, height, centerX, centerY, zoom);
        return (px, -py);
    }

    public void Calculate(CancellationToken ct = default)
    {
        var p = FractalParameters;
        var scene = BilliardScene.Build(p);
        var launches = BuildSweep(p, scene);
        int minBounces = Math.Max(0, p.BilliardRaysMinBounces);

        int w = Width, h = Height;
        double pitch = (4.0 / Math.Max(1.0, w)) / Math.Max(1e-12, Zoom);
        double cx = CenterX, cy = CenterY;
        var density = Density;
        Array.Clear(density);

        // Bands of rows for the parallel rasterise. The count depends only on the frame
        // height, never on the core count: a band edge clips segments, and the clip
        // point rounds differently from the walk's own row crossing.
        int bands = BandCount(h);
        int drawn = 0;
        var paths = new BilliardPoint[]?[Math.Min(ChunkSize, Math.Max(1, launches.Count))];

        for (int start = 0; start < launches.Count; start += ChunkSize)
        {
            if (ct.IsCancellationRequested) return;
            int count = Math.Min(ChunkSize, launches.Count - start);

            Parallel.For(0, count, new ParallelOptions { CancellationToken = ct },
                () => new List<BilliardPoint>(64),
                (i, _, list) =>
                {
                    list.Clear();
                    var o = Launch(scene, launches[start + i], list);
                    paths[i] = o.Valid && o.Bounces >= minBounces && list.Count >= 2 ? list.ToArray() : null;
                    return list;
                },
                _ => { });

            for (int i = 0; i < count; i++) if (paths[i] != null) drawn++;

            Parallel.For(0, bands, new ParallelOptions { CancellationToken = ct }, band =>
            {
                int y0 = (int)((long)h * band / bands), y1 = (int)((long)h * (band + 1) / bands);
                if (y1 <= y0) return;
                for (int i = 0; i < count; i++)
                {
                    var pts = paths[i];
                    if (pts == null) continue;
                    for (int k = 1; k < pts.Length; k++)
                        Splat(density, w, y0, y1, pitch,
                              ToPixelX(pts[k - 1].X, cx, pitch, w), ToPixelY(pts[k - 1].Y, cy, pitch, h),
                              ToPixelX(pts[k].X, cx, pitch, w), ToPixelY(pts[k].Y, cy, pitch, h));
                }
            });
            Array.Clear(paths);
        }

        LastRaysEmitted = launches.Count;
        LastRaysDrawn = drawn;
        Composite(scene, p.BilliardRaysShowDisks, pitch, cx, cy);
    }

    /// <summary>Row bands the rasterise splits a frame of height <paramref name="h"/> into.</summary>
    public static int BandCount(int h) => Math.Clamp(h / 16, 1, 128);

    // Pixel coordinates (continuous; pixel i spans [i, i + 1)) of a world point.
    private static double ToPixelX(double wx, double cx, double pitch, int w) => (wx - cx) / pitch + w * 0.5 + 0.5;
    private static double ToPixelY(double wy, double cy, double pitch, int h) => (-wy - cy) / pitch + h * 0.5 + 0.5;

    /// <summary>Trace one launch with its path recorded into <paramref name="path"/>.</summary>
    public static BilliardOutcome Launch(BilliardScene scene, BilliardRayLaunch l, List<BilliardPoint>? path)
    {
        switch (l.Kind)
        {
            case BilliardRayLaunch.LaunchKind.FarField:
                return scene.LaunchFarField(l.A, l.B, path);
            case BilliardRayLaunch.LaunchKind.PhaseSpace:
                return scene.LaunchPhaseSpace(l.A, l.B, path);
            default:
                // A point inside a mirror emits nothing.
                for (int i = 0; i < scene.DiskCount; i++)
                {
                    double ox = l.A - scene.DiskX(i), oy = l.B - scene.DiskY(i);
                    if (ox * ox + oy * oy < scene.DiskR(i) * scene.DiskR(i)) return default;
                }
                return scene.Trace(l.A, l.B, l.C, l.D, -1, path);
        }
    }

    /// <summary>The rays a sweep emits, in emission order (#1506). Always exactly the
    /// clamped <see cref="FractalParameters.BilliardRaysCount"/>.</summary>
    public static IReadOnlyList<BilliardRayLaunch> BuildSweep(FractalParameters p, BilliardScene scene)
    {
        int n = Math.Clamp(p.BilliardRaysCount, 1, MaxRays);
        double r = scene.PlayRadius;
        double angle = p.BilliardRaysAngle * (Math.PI / 180.0);
        double spread = Math.Clamp(p.BilliardRaysSpread, 0.0, 360.0) * (Math.PI / 180.0);
        var list = new List<BilliardRayLaunch>(n);
        switch (p.BilliardRaysSweep)
        {
            case BilliardRaySweep.AngleSweep:
            {
                // Emitter positions evenly round the play circle; each aim offset steps by
                // the golden-ratio fraction, so offsets cover the spread evenly without
                // lining up with the positions. The seed shifts the sequence.
                double phase = Frac(p.BilliardRaysSeed * GoldenFraction * GoldenFraction);
                for (int i = 0; i < n; i++)
                {
                    double t = angle + (i + 0.5) * (2.0 * Math.PI / n);
                    double aim = t + Math.PI + spread * (Frac(phase + i * GoldenFraction) - 0.5);
                    list.Add(new(BilliardRayLaunch.LaunchKind.Point, r * Math.Cos(t), r * Math.Sin(t), Math.Cos(aim), Math.Sin(aim)));
                }
                break;
            }
            case BilliardRaySweep.PointSource:
                for (int i = 0; i < n; i++)
                {
                    double a = angle + spread * ((i + 0.5) / n - 0.5);
                    list.Add(new(BilliardRayLaunch.LaunchKind.Point, p.BilliardRaysSourceX, p.BilliardRaysSourceY, Math.Cos(a), Math.Sin(a)));
                }
                break;
            case BilliardRaySweep.RandomFarField:
            {
                var rng = new Random(p.BilliardRaysSeed);
                for (int i = 0; i < n; i++)
                {
                    double b = (rng.NextDouble() * 2.0 - 1.0) * r;
                    double phi = rng.NextDouble() * 2.0 * Math.PI;
                    list.Add(new(BilliardRayLaunch.LaunchKind.FarField, b, phi, 0, 0));
                }
                break;
            }
            case BilliardRaySweep.PhaseSpace:
            {
                var rng = new Random(p.BilliardRaysSeed);
                for (int i = 0; i < n; i++)
                {
                    double s = (rng.NextDouble() * 2.0 - 1.0) * Math.PI;
                    double sinA = rng.NextDouble() * 2.0 - 1.0;
                    list.Add(new(BilliardRayLaunch.LaunchKind.PhaseSpace, s, sinA, 0, 0));
                }
                break;
            }
            default: // BSweep
            {
                int beams = Math.Clamp(p.BilliardRaysAngleCount, 1, 360);
                for (int k = 0; k < beams; k++)
                {
                    int m = n / beams + (k < n % beams ? 1 : 0);
                    double phi = angle + k * (2.0 * Math.PI / beams);
                    for (int j = 0; j < m; j++)
                        list.Add(new(BilliardRayLaunch.LaunchKind.FarField, -r + (j + 0.5) * (2.0 * r / m), phi, 0, 0));
                }
                break;
            }
        }
        return list;
    }

    /// <summary>Fractional part of the golden ratio, (sqrt 5 - 1) / 2.</summary>
    public const double GoldenFraction = 0.6180339887498949;

    private static double Frac(double v) => v - Math.Floor(v);

    // Add segment (x0, y0)-(x1, y1) (pixel coordinates) to rows [y0r, y1r): every pixel
    // it crosses gets the world length inside it. Liang-Barsky clip to the band, then
    // the Amanatides-Woo voxel walk.
    private static void Splat(double[] grid, int w, int y0r, int y1r, double pitch,
                              double x0, double y0, double x1, double y1)
    {
        if ((y0 < y0r && y1 < y0r) || (y0 >= y1r && y1 >= y1r)) return;
        if ((x0 < 0 && x1 < 0) || (x0 >= w && x1 >= w)) return;
        double dx = x1 - x0, dy = y1 - y0;
        double t0 = 0.0, t1 = 1.0;
        if (!Clip(-dx, x0, ref t0, ref t1) || !Clip(dx, w - x0, ref t0, ref t1)
            || !Clip(-dy, y0 - y0r, ref t0, ref t1) || !Clip(dy, y1r - y0, ref t0, ref t1))
            return;
        if (t1 <= t0) return;
        double len = Math.Sqrt(dx * dx + dy * dy) * pitch;   // world length of the whole segment

        // Start cell: the point just inside the clipped piece.
        double tm = t0 + Math.Min(1e-9, (t1 - t0) * 0.5);
        int ix = Math.Clamp((int)Math.Floor(x0 + dx * tm), 0, w - 1);
        int iy = Math.Clamp((int)Math.Floor(y0 + dy * tm), y0r, y1r - 1);
        int sx = dx > 0 ? 1 : dx < 0 ? -1 : 0;
        int sy = dy > 0 ? 1 : dy < 0 ? -1 : 0;
        double tMaxX = sx != 0 ? ((ix + (sx > 0 ? 1 : 0)) - x0) / dx : double.PositiveInfinity;
        double tMaxY = sy != 0 ? ((iy + (sy > 0 ? 1 : 0)) - y0) / dy : double.PositiveInfinity;
        double tDx = sx != 0 ? Math.Abs(1.0 / dx) : double.PositiveInfinity;
        double tDy = sy != 0 ? Math.Abs(1.0 / dy) : double.PositiveInfinity;

        double t = t0;
        int guard = w + (y1r - y0r) + 4;
        while (t < t1 && guard-- > 0)
        {
            double tn = Math.Min(Math.Min(tMaxX, tMaxY), t1);
            grid[iy * w + ix] += (tn - t) * len;
            t = tn;
            if (t >= t1) break;
            if (tMaxX <= tMaxY) { ix += sx; tMaxX += tDx; }
            else { iy += sy; tMaxY += tDy; }
            if (ix < 0 || ix >= w || iy < y0r || iy >= y1r) break;
        }
    }

    // One Liang-Barsky edge: keep t where p t <= q.
    private static bool Clip(double pp, double q, ref double t0, ref double t1)
    {
        if (pp == 0) return q >= 0;
        double r = q / pp;
        if (pp < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
        else { if (r < t0) return false; if (r < t1) t1 = r; }
        return true;
    }

    // Log-normalised density to colour, after the Buddhabrot ColorMap composite: the
    // theme colour at smooth = (1 - norm) * iters, alpha-blended over the theme's
    // InSetColor. Alpha is norm, not the Buddhabrot's norm^2: the exit fans are single
    // crossings, the chaotic spreading worth seeing, and norm^2 fades them out. Then
    // the disks.
    private void Composite(BilliardScene scene, bool showDisks, double pitch, double cx, double cy)
    {
        int w = Width, h = Height, n = w * h;
        double max = 0.0;
        for (int i = 0; i < n; i++) if (Density[i] > max) max = Density[i];

        int iters = Math.Max(16, MaxIterations);
        var cm = ColorMap;
        cm.MaxIterations = iters;
        uint bg = cm.InSetColor | 0xFF000000u;
        double inv = max > 0 ? 1.0 / Math.Log(1.0 + max / pitch) : 0.0;

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                double d = Density[i];
                if (d <= 0 || max <= 0) { SmoothBuffer[i] = 0f; ColorBuffer[i] = bg; continue; }
                double norm = Math.Log(1.0 + d / pitch) * inv;
                SmoothBuffer[i] = (float)norm;
                uint f = unchecked((uint)cm.Map((float)((1.0 - norm) * iters), 0f, iters));
                double a = norm, b = 1.0 - a;
                uint R = (uint)(((f >> 16) & 0xFF) * a + ((bg >> 16) & 0xFF) * b);
                uint G = (uint)(((f >> 8) & 0xFF) * a + ((bg >> 8) & 0xFF) * b);
                uint B = (uint)((f & 0xFF) * a + (bg & 0xFF) * b);
                ColorBuffer[i] = 0xFF000000u | (R << 16) | (G << 8) | B;
            }
        });

        if (!showDisks) return;
        for (int k = 0; k < scene.DiskCount; k++)
        {
            double dxw = scene.DiskX(k), dyw = scene.DiskY(k), rr = scene.DiskR(k);
            double pxc = (dxw - cx) / pitch + w * 0.5, pyc = (-dyw - cy) / pitch + h * 0.5, rp = rr / pitch;
            int xa = Math.Max(0, (int)Math.Floor(pxc - rp - 1)), xb = Math.Min(w - 1, (int)Math.Ceiling(pxc + rp + 1));
            int ya = Math.Max(0, (int)Math.Floor(pyc - rp - 1)), yb = Math.Min(h - 1, (int)Math.Ceiling(pyc + rp + 1));
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++)
                {
                    var (wx, wy) = WorldAt(x, y, w, h, cx, cy, 4.0 / Math.Max(1.0, w) / pitch);
                    double dist = Math.Sqrt((wx - dxw) * (wx - dxw) + (wy - dyw) * (wy - dyw));
                    if (dist <= rr - pitch) ColorBuffer[y * w + x] = DiskFillColor;
                    else if (dist <= rr) ColorBuffer[y * w + x] = DiskEdgeColor;
                }
        }
    }
}
