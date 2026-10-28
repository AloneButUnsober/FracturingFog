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
// #1507 (S2) — colour sources. Brightness stays the log density; the hue comes from
// BilliardRaysColorSource. Gate: each ray carries its exit gate's colour (the theme's
// billiard gate colour, else Okabe-Ito; trapped grey) and the pixel shows the
// length-weighted mix. Attributes (bounces, path length, entry / exit angle, entry-exit
// gate distance, self-intersection): each ray carries one value in [0, 1] and the pixel
// shows the length-weighted mean through the theme gradient. Both ride the same splat
// as the density, so the weights are exactly the in-pixel lengths.
//
// #1508 (S3) — crossings (BilliardCrossingFinder): where each ray crosses the K rays drawn
// just before it, and, counted separately, itself. Each crossing in view adds 1 to a
// crossing-count grid and its value (delta N, angle, or path length from entry / to exit)
// to a sum grid, splatted in ray order. Crossings mode draws them alone, Both draws them
// over the paths; the crossing value is then the relief height field.
//
// #1509 (S4) — neighbour divergence: each ray against the ray emitted just before it,
// when their launches are neighbours (same BSweep beam; consecutive AngleSweep /
// PointSource rays; random sweeps have no neighbours). Their disk-hit sequences are
// compared. The pair parts at the last mirror point both bounced off; the current ray's
// next leg (from that point to its next hit or exit) is drawn, length-weighted like the
// paths, valued by the bounce difference, a gate change, the separation of their next
// hits, or the number of mirrors shared. (A point mark would sit on a disk surface,
// under the disk.) Neighbouring launches that split are where the exit basins meet, so
// the legs trace the fractal basin boundary in real space. Every valid ray takes part
// (Min bounces only filters what is drawn).
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

/// <summary>What one ray adds to the colour channels (#1507): up to three values (gate RGB in
/// [0, 1], or one attribute in <see cref="C0"/>), weighted by its in-pixel length. A ray with
/// <see cref="Defined"/> false adds density only.</summary>
public readonly record struct BilliardRayTint(bool Defined, double C0, double C1, double C2);

/// <summary>Where two sweep-neighbour rays part (#1509). <see cref="Current"/> and <see cref="Previous"/>
/// are emission indices (previous = current - 1). (<see cref="X"/>, <see cref="Y"/>) is the current ray's
/// last shared mirror point: its bounce number <see cref="CommonBounces"/>.</summary>
public readonly record struct BilliardRayDivergence(double X, double Y, int Current, int Previous, int CommonBounces,
                                                    int BounceDifference, bool GateChanged, double Separation);

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

    /// <summary>#1507 — length-weighted sums of the rays' tints (<see cref="BilliardRayTint"/>)
    /// and of the lengths of rays whose tint is defined. Empty in Density mode.</summary>
    public double[] Tint0 { get; private set; } = Array.Empty<double>();
    public double[] Tint1 { get; private set; } = Array.Empty<double>();
    public double[] Tint2 { get; private set; } = Array.Empty<double>();
    public double[] TintWeight { get; private set; } = Array.Empty<double>();

    /// <summary>#1508 — crossings per pixel and the sum of their values. Empty in Paths mode.</summary>
    public double[] CrossCount { get; private set; } = Array.Empty<double>();
    public double[] CrossSum { get; private set; } = Array.Empty<double>();

    /// <summary>#1509 — length of diverging legs per pixel and the length-weighted sum of their values.
    /// Empty unless a divergence mode is on.</summary>
    public double[] DivCount { get; private set; } = Array.Empty<double>();
    public double[] DivSum { get; private set; } = Array.Empty<double>();

    /// <summary>#1509 — divergence markers placed (in view or not) by the last render.</summary>
    public int LastDivergences { get; private set; }

    /// <summary>#1509 — keep every divergence of the last render in <see cref="Divergences"/>.</summary>
    public bool RecordDivergences { get; set; }
    public IReadOnlyList<BilliardRayDivergence> Divergences { get; private set; } = Array.Empty<BilliardRayDivergence>();

    /// <summary>#1508 — ray-ray and self-crossings found in view by the last render.</summary>
    public int LastCrossings { get; private set; }
    public int LastSelfCrossings { get; private set; }

    /// <summary>#1508 — keep every crossing of the last render in <see cref="Crossings"/> (tests, probes).</summary>
    public bool RecordCrossings { get; set; }
    public IReadOnlyList<BilliardRayCrossing> Crossings { get; private set; } = Array.Empty<BilliardRayCrossing>();

    /// <summary>Rays traced per chunk (default <see cref="ChunkSize"/>). Settable so tests can put a
    /// crossing window across chunk boundaries.</summary>
    public int ChunkRays { get; set; } = ChunkSize;

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

    /// <summary>#1507 — Gate mode colour of trapped rays (never escaped within the cap).</summary>
    public const uint TrappedColor = 0xFF9A9A9A;

    /// <summary>#1507 — hue of pixels crossed only by rays with no value for the attribute.</summary>
    public const uint UndefinedColor = 0xFF5A5A5A;

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
        var source = p.BilliardRaysColorSource;
        bool tinted = source != BilliardRayColorSource.Density;
        int n = w * h;
        if (tinted)
        {
            if (Tint0.Length != n) { Tint0 = new double[n]; TintWeight = new double[n]; }
            else { Array.Clear(Tint0); Array.Clear(TintWeight); }
            if (source == BilliardRayColorSource.Gate)
            {
                if (Tint1.Length != n) { Tint1 = new double[n]; Tint2 = new double[n]; }
                else { Array.Clear(Tint1); Array.Clear(Tint2); }
            }
            else { Tint1 = Tint2 = Array.Empty<double>(); }
        }
        else { Tint0 = Tint1 = Tint2 = TintWeight = Array.Empty<double>(); }
        var map = ColorMap;
        var t0 = tinted ? Tint0 : null;
        var t1 = source == BilliardRayColorSource.Gate ? Tint1 : null;
        var t2 = source == BilliardRayColorSource.Gate ? Tint2 : null;
        var tw = tinted ? TintWeight : null;

        var mode = p.BilliardRaysMode;
        bool crossings = mode is BilliardRayMode.Crossings or BilliardRayMode.Both;
        bool divergence = mode is BilliardRayMode.Divergence or BilliardRayMode.DivergenceOverPaths;
        bool splatPaths = mode is BilliardRayMode.Paths or BilliardRayMode.Both or BilliardRayMode.DivergenceOverPaths;
        var divValue = p.BilliardRaysDivergenceValue;
        if (divergence)
        {
            if (DivCount.Length != n) { DivCount = new double[n]; DivSum = new double[n]; }
            else { Array.Clear(DivCount); Array.Clear(DivSum); }
        }
        else { DivCount = DivSum = Array.Empty<double>(); }
        var divRecorded = RecordDivergences ? new List<BilliardRayDivergence>() : null;
        int divCount = 0;
        // The previous chunk's last ray, so a neighbour pair can span a chunk boundary.
        RayTrace? carryTrace = null;
        int window = Math.Clamp(p.BilliardRaysCrossingWindow, 0, BilliardCrossingFinder.MaxWindow);
        bool drawSelf = p.BilliardRaysSelfCrossings;
        var crossValue = p.BilliardRaysCrossingValue;
        if (crossings)
        {
            if (CrossCount.Length != n) { CrossCount = new double[n]; CrossSum = new double[n]; }
            else { Array.Clear(CrossCount); Array.Clear(CrossSum); }
        }
        else { CrossCount = CrossSum = Array.Empty<double>(); }
        var recorded = RecordCrossings ? new List<BilliardRayCrossing>() : null;
        int crossCount = 0, selfCount = 0;
        // View rectangle in world space (pixel i spans [i, i + 1) in pixel units).
        double vx0 = cx - (w * 0.5 + 0.5) * pitch, vx1 = cx + (w * 0.5 - 0.5) * pitch;
        double vy0 = -(cy + (h * 0.5 - 0.5) * pitch), vy1 = -(cy - (h * 0.5 + 0.5) * pitch);
        var finder = crossings ? new BilliardCrossingFinder(window, vx0, vx1, vy0, vy1, 4.0 * pitch) : null;
        var found = new List<BilliardRayCrossing>();
        int chunkRays = Math.Max(1, ChunkRays);

        // Bands of rows for the parallel rasterise. The count depends only on the frame
        // height, never on the core count: a band edge clips segments, and the clip
        // point rounds differently from the walk's own row crossing.
        int bands = BandCount(h);
        int drawn = 0;
        var paths = new BilliardPoint[]?[Math.Min(chunkRays, Math.Max(1, launches.Count))];
        var tints = new BilliardRayTint[paths.Length];
        var traces = divergence ? new RayTrace?[paths.Length] : null;
        var sweep = p.BilliardRaysSweep;

        for (int start = 0; start < launches.Count; start += chunkRays)
        {
            if (ct.IsCancellationRequested) return;
            int count = Math.Min(chunkRays, launches.Count - start);

            Parallel.For(0, count, new ParallelOptions { CancellationToken = ct },
                () => new List<BilliardPoint>(64),
                (i, _, list) =>
                {
                    list.Clear();
                    var o = Launch(scene, launches[start + i], list);
                    bool keep = o.Valid && o.Bounces >= minBounces && list.Count >= 2;
                    paths[i] = keep ? list.ToArray() : null;
                    if (keep && tinted) tints[i] = TintOf(source, scene, map, o, list);
                    if (traces != null)
                        traces[i] = o.Valid && list.Count >= 2
                            ? new RayTrace(paths[i] ?? list.ToArray(), HitSequence(scene, list, o.Bounces), o.GateId)
                            : null;
                    return list;
                },
                _ => { });

            if (finder != null)
            {
                double pathRef = scene.PathReference;
                for (int i = 0; i < count; i++)
                {
                    if (paths[i] == null) continue;
                    if ((i & 255) == 0 && ct.IsCancellationRequested) return;
                    found.Clear();
                    finder.Add(paths[i]!, found);
                    foreach (var c in found)
                    {
                        if (c.IsSelf) { selfCount++; if (!drawSelf) continue; }
                        else crossCount++;
                        recorded?.Add(c);
                        int px = Math.Clamp((int)Math.Floor(ToPixelX(c.X, cx, pitch, w)), 0, w - 1);
                        int py = Math.Clamp((int)Math.Floor(ToPixelY(c.Y, cy, pitch, h)), 0, h - 1);
                        CrossCount[py * w + px] += 1.0;
                        CrossSum[py * w + px] += CrossingValue(crossValue, c, pathRef);
                    }
                }
            }

            if (traces != null)
            {
                double pathRef = scene.PathReference;
                for (int i = 0; i < count; i++)
                {
                    int g = start + i;
                    var prevTrace = i > 0 ? traces[i - 1] : carryTrace;
                    var curTrace = traces[i];
                    if (g == 0 || prevTrace == null || curTrace == null
                        || !AreNeighbours(sweep, launches[g - 1], launches[g])) continue;
                    if (Diverge(prevTrace, curTrace, g) is not { } d) continue;
                    divCount++;
                    divRecorded?.Add(d);
                    var a = curTrace.Points[d.CommonBounces];
                    var b = curTrace.Points[Math.Min(d.CommonBounces + 1, curTrace.Points.Length - 1)];
                    var leg = new BilliardRayTint(true, DivergenceValue(divValue, d, pathRef), 0, 0);
                    Splat(DivCount, DivSum, null, null, null, leg, w, 0, h, pitch,
                          ToPixelX(a.X, cx, pitch, w), ToPixelY(a.Y, cy, pitch, h),
                          ToPixelX(b.X, cx, pitch, w), ToPixelY(b.Y, cy, pitch, h));
                }
                carryTrace = traces[count - 1];
                Array.Clear(traces);
            }

            for (int i = 0; i < count; i++) if (paths[i] != null) drawn++;

            if (splatPaths)
            Parallel.For(0, bands, new ParallelOptions { CancellationToken = ct }, band =>
            {
                int y0 = (int)((long)h * band / bands), y1 = (int)((long)h * (band + 1) / bands);
                if (y1 <= y0) return;
                for (int i = 0; i < count; i++)
                {
                    var pts = paths[i];
                    if (pts == null) continue;
                    var tint = tints[i];
                    for (int k = 1; k < pts.Length; k++)
                        Splat(density, t0, t1, t2, tw, tint, w, y0, y1, pitch,
                              ToPixelX(pts[k - 1].X, cx, pitch, w), ToPixelY(pts[k - 1].Y, cy, pitch, h),
                              ToPixelX(pts[k].X, cx, pitch, w), ToPixelY(pts[k].Y, cy, pitch, h));
                }
            });
            Array.Clear(paths);
            Array.Clear(tints);
        }

        LastRaysEmitted = launches.Count;
        LastRaysDrawn = drawn;
        LastCrossings = crossCount;
        LastSelfCrossings = selfCount;
        Crossings = (IReadOnlyList<BilliardRayCrossing>?)recorded ?? Array.Empty<BilliardRayCrossing>();
        LastDivergences = divCount;
        Divergences = (IReadOnlyList<BilliardRayDivergence>?)divRecorded ?? Array.Empty<BilliardRayDivergence>();
        Overlay? overlay = crossings
            ? new Overlay(CrossCount, CrossSum, crossValue switch
              {
                  BilliardCrossingValue.Count => OverlayScale.Count,
                  BilliardCrossingValue.DeltaN => OverlayScale.Signed,
                  _ => OverlayScale.Unit,
              }, 1.0)
            : divergence
                ? new Overlay(DivCount, DivSum, divValue is BilliardDivergenceValue.BounceDifference or BilliardDivergenceValue.CommonBounces
                    ? OverlayScale.Positive : OverlayScale.Unit, pitch)
                : null;
        Composite(scene, p.BilliardRaysShowDisks, pitch, cx, cy, source, overlay);
    }

    // #1509 — what a ray keeps for the neighbour comparison: its points, which disk each
    // bounce hit, and its exit gate (-1 trapped).
    private sealed record RayTrace(BilliardPoint[] Points, int[] Hits, int Gate);

    /// <summary>#1509 — the disk each bounce of <paramref name="path"/> hit (points 1..bounces), found as
    /// the disk whose surface the point lies on.</summary>
    public static int[] HitSequence(BilliardScene scene, IReadOnlyList<BilliardPoint> path, int bounces)
    {
        int nb = Math.Min(bounces, path.Count - 1);
        var hits = new int[Math.Max(0, nb)];
        for (int k = 1; k <= nb; k++)
        {
            double best = double.PositiveInfinity;
            for (int d = 0; d < scene.DiskCount; d++)
            {
                double dx = path[k].X - scene.DiskX(d), dy = path[k].Y - scene.DiskY(d);
                double err = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - scene.DiskR(d));
                if (err < best) { best = err; hits[k - 1] = d; }
            }
        }
        return hits;
    }

    /// <summary>#1509 — are launches <paramref name="a"/> then <paramref name="b"/> neighbours in
    /// <paramref name="sweep"/>? Same BSweep beam (same angle); consecutive AngleSweep / PointSource
    /// rays; never for the random sweeps.</summary>
    public static bool AreNeighbours(BilliardRaySweep sweep, BilliardRayLaunch a, BilliardRayLaunch b) => sweep switch
    {
        BilliardRaySweep.BSweep => a.B == b.B,
        BilliardRaySweep.AngleSweep or BilliardRaySweep.PointSource => true,
        _ => false,
    };

    // Where two neighbours part: null when their hit sequences and gates agree, or when they
    // share no mirror at all (they never travelled together).
    private static BilliardRayDivergence? Diverge(RayTrace prev, RayTrace cur, int current)
    {
        int common = 0, m = Math.Min(prev.Hits.Length, cur.Hits.Length);
        while (common < m && prev.Hits[common] == cur.Hits[common]) common++;
        if (common == prev.Hits.Length && common == cur.Hits.Length && prev.Gate == cur.Gate) return null;
        if (common == 0) return null;
        var at = cur.Points[common];
        double sep = common + 1 < prev.Points.Length && common + 1 < cur.Points.Length
            ? Math.Sqrt(Math.Pow(prev.Points[common + 1].X - cur.Points[common + 1].X, 2)
                      + Math.Pow(prev.Points[common + 1].Y - cur.Points[common + 1].Y, 2))
            : 0.0;
        return new BilliardRayDivergence(at.X, at.Y, current, current - 1, common,
            Math.Abs(cur.Hits.Length - prev.Hits.Length), cur.Gate != prev.Gate, sep);
    }

    /// <summary>#1509 — the value divergence <paramref name="d"/> adds to <see cref="DivSum"/>.</summary>
    public static double DivergenceValue(BilliardDivergenceValue v, BilliardRayDivergence d, double pathReference) => v switch
    {
        BilliardDivergenceValue.GateChange => d.GateChanged ? 1.0 : 0.0,
        BilliardDivergenceValue.Separation => Math.Min(1.0, d.Separation / pathReference),
        BilliardDivergenceValue.CommonBounces => d.CommonBounces,
        _ => d.BounceDifference,
    };

    // How an overlay's mean value becomes a gradient position.
    private enum OverlayScale { Count, Signed, Unit, Positive }
    // Count is in units of Unit (1 for crossing counts, one pixel width for leg lengths).
    private sealed record Overlay(double[] Count, double[] Sum, OverlayScale Scale, double Unit);

    /// <summary>#1508 — the value crossing <paramref name="c"/> adds to <see cref="CrossSum"/>: 1 for
    /// Count, the signed delta N, the angle as a fraction of a right angle, or a path length over
    /// the scene's path reference (clamped to 1).</summary>
    public static double CrossingValue(BilliardCrossingValue v, BilliardRayCrossing c, double pathReference) => v switch
    {
        BilliardCrossingValue.DeltaN => c.DeltaN,
        BilliardCrossingValue.Angle => c.Angle / (Math.PI / 2.0),
        BilliardCrossingValue.PreviousFromEntry => Math.Min(1.0, c.PreviousFromEntry / pathReference),
        BilliardCrossingValue.PreviousToExit => Math.Min(1.0, c.PreviousToExit / pathReference),
        BilliardCrossingValue.CurrentFromEntry => Math.Min(1.0, c.CurrentFromEntry / pathReference),
        BilliardCrossingValue.CurrentToExit => Math.Min(1.0, c.CurrentToExit / pathReference),
        _ => 1.0,
    };

    /// <summary>#1507 — the tint ray <paramref name="o"/> (recorded <paramref name="path"/>) carries
    /// for <paramref name="source"/>.</summary>
    public static BilliardRayTint TintOf(BilliardRayColorSource source, BilliardScene scene, IColorMap? map,
                                         BilliardOutcome o, IReadOnlyList<BilliardPoint> path)
    {
        switch (source)
        {
            case BilliardRayColorSource.Gate:
            {
                uint c = o.GateId < 0 ? TrappedColor
                    : ChaoticBilliardCalculator.GateColor(map, o.GateId, scene.GateCount, scene.MaxBounces);
                return new(true, ((c >> 16) & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, (c & 0xFF) / 255.0);
            }
            case BilliardRayColorSource.Bounces:
                return Value(Math.Log(1.0 + o.Bounces) / Math.Log(1.0 + scene.MaxBounces));
            case BilliardRayColorSource.PathLength:
                return Value(Math.Clamp(o.PathLength / scene.PathReference, 0.0, 1.0));
            case BilliardRayColorSource.EntryAngle:
                return path.Count < 2 ? default
                    : Value(Turn(path[1].X - path[0].X, path[1].Y - path[0].Y));
            case BilliardRayColorSource.ExitAngle:
                return o.GateId < 0 ? default : Value(Turn(o.EndDx, o.EndDy));
            case BilliardRayColorSource.GateDistance:
            {
                int e = EntryGate(scene, path);
                if (e < 0 || o.GateId < 0) return default;
                int n = scene.GateCount, d = Math.Abs(e - o.GateId);
                return Value(Math.Min(d, n - d) / (double)Math.Max(1, n / 2));
            }
            case BilliardRayColorSource.SelfIntersects:
                return Value(SelfIntersects(path) ? 1.0 : 0.0);
            default:
                return default;
        }

        static BilliardRayTint Value(double v) => new(true, v, 0, 0);
    }

    // Direction as a fraction of a turn anticlockwise from +x, in [0, 1).
    private static double Turn(double dx, double dy)
    {
        double a = Math.Atan2(dy, dx) / (2.0 * Math.PI);
        return a < 0 ? a + 1.0 : a;
    }

    /// <summary>#1507 — the gate a ray came in through: the gate of the direction it came
    /// from, for a ray that starts on or outside the play circle; -1 for one launched inside
    /// (a point source or a disk surface).</summary>
    public static int EntryGate(BilliardScene scene, IReadOnlyList<BilliardPoint> path)
    {
        if (path.Count < 2) return -1;
        double r = Math.Sqrt(path[0].X * path[0].X + path[0].Y * path[0].Y);
        if (r < scene.PlayRadius * (1.0 - 1e-9)) return -1;
        return scene.Gate(path[0].X - path[1].X, path[0].Y - path[1].Y);
    }

    /// <summary>#1507 — true when two non-adjacent segments of the polyline cross.</summary>
    public static bool SelfIntersects(IReadOnlyList<BilliardPoint> path)
    {
        for (int i = 1; i < path.Count; i++)
            for (int j = i + 2; j < path.Count; j++)
                if (SegmentsCross(path[i - 1], path[i], path[j - 1], path[j])) return true;
        return false;
    }

    // Proper crossing of a-b and c-d: solve a + s (b - a) = c + t (d - c) with s, t in (0, 1).
    private static bool SegmentsCross(BilliardPoint a, BilliardPoint b, BilliardPoint c, BilliardPoint d)
    {
        double rx = b.X - a.X, ry = b.Y - a.Y, qx = d.X - c.X, qy = d.Y - c.Y;
        double den = rx * qy - ry * qx;
        if (Math.Abs(den) < 1e-300) return false;
        double ex = c.X - a.X, ey = c.Y - a.Y;
        double s = (ex * qy - ey * qx) / den, t = (ex * ry - ey * rx) / den;
        const double eps = 1e-12;
        return s > eps && s < 1 - eps && t > eps && t < 1 - eps;
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
    private static void Splat(double[] grid, double[]? g0, double[]? g1, double[]? g2, double[]? gw,
                              BilliardRayTint tint, int w, int y0r, int y1r, double pitch,
                              double x0, double y0, double x1, double y1)
    {
        bool tinted = g0 != null && tint.Defined;
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
            int cell = iy * w + ix;
            double dl = (tn - t) * len;
            grid[cell] += dl;
            if (tinted)
            {
                g0![cell] += dl * tint.C0;
                if (g1 != null) { g1[cell] += dl * tint.C1; g2![cell] += dl * tint.C2; }
                if (gw != null) gw[cell] += dl;
            }
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
    private void Composite(BilliardScene scene, bool showDisks, double pitch, double cx, double cy,
                           BilliardRayColorSource source, Overlay? overlay)
    {
        int w = Width, h = Height, n = w * h;
        double max = 0.0;
        for (int i = 0; i < n; i++) if (Density[i] > max) max = Density[i];

        int iters = Math.Max(16, MaxIterations);
        // A billiard theme colours gates only (its gradient Map is black), so it feeds the
        // Gate mode; the gradient modes then use the default gradient instead.
        var cm = ColorMap is IBilliardColorMap ? new HsvPalette() : ColorMap;
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
                uint f;
                if (source == BilliardRayColorSource.Density)
                    f = unchecked((uint)cm.Map((float)((1.0 - norm) * iters), 0f, iters));
                else if (TintWeight[i] <= 0)
                    f = UndefinedColor;
                else if (source == BilliardRayColorSource.Gate)
                {
                    double k = 255.0 / TintWeight[i];
                    f = 0xFF000000u | ((uint)Math.Min(255.0, Tint0[i] * k) << 16)
                                    | ((uint)Math.Min(255.0, Tint1[i] * k) << 8)
                                    | (uint)Math.Min(255.0, Tint2[i] * k);
                }
                else
                    f = unchecked((uint)cm.Map((float)(Tint0[i] / TintWeight[i] * iters), 0f, iters));
                double a = norm, b = 1.0 - a;
                uint R = (uint)(((f >> 16) & 0xFF) * a + ((bg >> 16) & 0xFF) * b);
                uint G = (uint)(((f >> 8) & 0xFF) * a + ((bg >> 8) & 0xFF) * b);
                uint B = (uint)((f & 0xFF) * a + (bg & 0xFF) * b);
                ColorBuffer[i] = 0xFF000000u | (R << 16) | (G << 8) | B;
            }
        });

        if (overlay != null) CompositeOverlay(cm, iters, overlay);

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

    // #1508 / #1509 — crossings or divergence markers over whatever the paths drew (the
    // background when paths are off): brightness / alpha the log marker count, hue the
    // theme gradient over the mean value (signed delta N centred, so equal bounce counts
    // sit mid-gradient; positive values over the frame's largest). The value becomes the
    // relief height (SmoothBuffer).
    private void CompositeOverlay(IColorMap cm, int iters, Overlay ov)
    {
        int n = Width * Height;
        var count = ov.Count; var sum = ov.Sum;
        double maxCount = 0, maxAbs = 0;
        for (int i = 0; i < n; i++)
        {
            if (count[i] <= 0) { if (Density.Length == n && Density[i] <= 0) SmoothBuffer[i] = 0f; continue; }
            maxCount = Math.Max(maxCount, count[i]);
            maxAbs = Math.Max(maxAbs, Math.Abs(sum[i] / count[i]));
        }
        if (maxCount <= 0) return;
        double inv = 1.0 / Math.Log(1.0 + maxCount / ov.Unit);
        maxAbs = Math.Max(maxAbs, 1e-12);
        Parallel.For(0, Height, y =>
        {
            for (int x = 0; x < Width; x++)
            {
                int i = y * Width + x;
                double c = count[i];
                if (c <= 0) { SmoothBuffer[i] = 0f; continue; }
                double norm = Math.Log(1.0 + c / ov.Unit) * inv;
                double mean = sum[i] / c;
                double t = ov.Scale switch
                {
                    OverlayScale.Count => norm,
                    OverlayScale.Signed => 0.5 + 0.5 * mean / maxAbs,
                    OverlayScale.Positive => Math.Clamp(mean / maxAbs, 0.0, 1.0),
                    _ => Math.Clamp(mean, 0.0, 1.0),
                };
                SmoothBuffer[i] = (float)t;
                uint f = unchecked((uint)cm.Map((float)((ov.Scale == OverlayScale.Count ? 1.0 - t : t) * iters), 0f, iters));
                // At least half opacity, so a single crossing is visible over bright paths.
                double a = 0.5 + 0.5 * norm, b = 1.0 - a;
                uint under = ColorBuffer[i];
                uint R = (uint)(((f >> 16) & 0xFF) * a + ((under >> 16) & 0xFF) * b);
                uint G = (uint)(((f >> 8) & 0xFF) * a + ((under >> 8) & 0xFF) * b);
                uint B = (uint)((f & 0xFF) * a + (under & 0xFF) * b);
                ColorBuffer[i] = 0xFF000000u | (R << 16) | (G << 8) | B;
            }
        });
    }
}
