// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ChaoticBilliardCalculator.cs (#627)
//
// Chaotic scattering rendered as a fractal. A geometric ray is launched into a
// 2D field of circular mirror disks and reflects specularly until it escapes the
// play region (a large bounding circle) or exceeds the bounce cap. Each pixel
// encodes a launch initial condition — impact parameter b along x, incoming
// angle phi along y — mapped through the standard pan/zoom plane, so zooming any
// 1D slice of parameter space reveals fractal structure at all scales (the
// map from initial condition to outcome is fractal; see Ott & Tel, Chaos 1993).
//
// The recorded outcome is:
//   • escape-gate sector  — categorical (which angular sector the exit
//     direction fell into); -1 == trapped (never escaped within the cap)
//   • bounce count        — reflections before escape; written to SmoothBuffer
//     as the Relief-3D height field (chaotic high-bounce basins rise into ridges)
//   • path length         — total world-space distance, normalised for colouring
//
// #1445 — the far-field (b, phi) view is dominated by each disk's first-hit
// shadow, a sine band in phi (the "twisting strands"); the fractal lives where
// the bands nearly meet. So: near-touching default disks, an ExitGap gate mode
// (sectors centred on the gaps between ring disks = the Wada exit basins), muted
// direct misses, and a PhaseSpace launch mode (Birkhoff section off disk 0) in
// which the chaotic set fills the frame. The physics lives in BilliardScene so
// tests can check it (rotation symmetry, time reversal) on the render path.
//
// #1447 — the ray-path inset traces single launches with the path recorded
// (BilliardScene.Trace's optional point list; null on the per-pixel render path,
// which stays allocation-free) through the same PlaneAt mapping the render uses.
//
// Not escape-time: categorical gate colouring goes through IBilliardColorMap
// (the INewtonColorMap precedent). When no such theme is selected the calculator
// falls back to built-in Okabe-Ito-per-gate shading, so it renders standalone.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class ChaoticBilliardCalculator : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // #139 — Relief 3D height field. The billiard has no escape potential, so the
    // relief height is the bounce count: fast-escaping basin interiors are low,
    // the chaotic basin boundaries (many bounces / trapped) rise into ridges.
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 256;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    public ChaoticBilliardCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
    }

    public void Calculate(CancellationToken ct = default)
    {
        var scene = BilliardScene.Build(FractalParameters);
        int maxBounces = scene.MaxBounces;
        int gateCount = scene.GateCount;
        bool muteMisses = FractalParameters.BilliardMuteMisses;

        ColorMap.MaxIterations = maxBounces;
        var billiardMap = ColorMap as IBilliardColorMap;
        uint trappedFallback = ColorMap.InSetColor;

        int width = Width, height = Height;
        double centerX = CenterX, centerY = CenterY, zoom = Zoom;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowBase = y * width;

            for (int x = 0; x < width; x++)
            {
                var (planeX, planeY) = PlaneAt(x, y, width, height, centerX, centerY, zoom);
                var o = scene.Launch(planeX, planeY);

                int idx = rowBase + x;
                if (!o.Valid)
                {
                    // Outside the phase-space section (|sin alpha| >= 1): no launch.
                    SmoothBuffer[idx] = 0f;
                    ColorBuffer[idx] = OutsideSectionColor;
                    continue;
                }
                SmoothBuffer[idx] = o.Bounces;

                uint col;
                if (billiardMap != null)
                {
                    float pl = (float)Math.Clamp(o.PathLength / scene.PathReference, 0.0, 1.0);
                    col = unchecked((uint)billiardMap.MapBilliard(
                        o.GateId, gateCount, o.Bounces, maxBounces, pl));
                }
                else if (o.GateId < 0)
                {
                    col = trappedFallback;   // trapped -> theme interior colour
                }
                else
                {
                    // Built-in fallback: Okabe-Ito colour per gate (colour-blind safe,
                    // #1445; was an HSV hue wheel), brightness fades with bounces.
                    float shade = 1.0f - Math.Min(o.Bounces / (float)maxBounces, 0.85f);
                    col = unchecked((uint)BilliardColorHelper.Shade(BilliardColorHelper.GateColor(o.GateId), shade));
                }
                // #1445 — a ray that never touched a mirror carries no scattering
                // information; in FarField its gate is set by the launch angle alone
                // and floods the frame with flat bands. Mute it to a dark tint.
                if (muteMisses && o.Bounces == 0 && o.GateId >= 0)
                    col = Mute(col);
                ColorBuffer[idx] = col;
            }
        });
    }

    /// <summary>The plane point (the launch initial condition) at pixel (<paramref name="x"/>,
    /// <paramref name="y"/>) of a <paramref name="width"/> x <paramref name="height"/> frame:
    /// the standard 2D mapping, pitch (4 / width) / zoom about the centre. Scale-free, so a
    /// pointer position in any client size that shows the same frame maps to the same
    /// point (#1447 — the inset traces exactly the launch the render used).</summary>
    public static (double X, double Y) PlaneAt(double x, double y, double width, double height,
                                               double centerX, double centerY, double zoom)
    {
        double pitch = (4.0 / Math.Max(1.0, width)) / Math.Max(1e-12, zoom);
        return (centerX + (x - width * 0.5) * pitch, centerY + (y - height * 0.5) * pitch);
    }

    /// <summary>#1447 — the colour <paramref name="map"/> gives gate <paramref name="gate"/>
    /// on a ray with no bounces (the gate's own, unshaded colour), as the render would draw
    /// it: the billiard theme when one is selected, else the built-in Okabe-Ito gate colour.</summary>
    public static uint GateColor(IColorMap? map, int gate, int gateCount, int maxBounces)
    {
        if (map is IBilliardColorMap b)
            return unchecked((uint)b.MapBilliard(gate, gateCount, 0, maxBounces, 0f)) | 0xFF000000u;
        if (gate < 0) return map != null ? map.InSetColor | 0xFF000000u : 0xFF000000u;
        return unchecked((uint)BilliardColorHelper.GateColor(gate));
    }

    /// <summary>Colour of phase-space pixels with no launch (|sin alpha| &gt;= 1).</summary>
    public const uint OutsideSectionColor = 0xFF0A0A0A;

    /// <summary>#1445 — the muted-miss colour: a dark tint of <paramref name="argb"/>
    /// (18 % of the colour over a near-black floor), so the gate hue stays legible.</summary>
    public static uint Mute(uint argb)
    {
        uint r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
        r = 16 + r * 18 / 100;
        g = 16 + g * 18 / 100;
        b = 16 + b * 18 / 100;
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }
}

/// <summary>One traced launch of the chaotic billiard (#627 / #1445).</summary>
public readonly struct BilliardOutcome
{
    /// <summary>Escape gate (0 .. GateCount-1), -1 when trapped at the bounce cap.</summary>
    public int GateId { get; init; }
    /// <summary>Reflections before escape (the cap when trapped).</summary>
    public int Bounces { get; init; }
    /// <summary>World-space path length travelled.</summary>
    public double PathLength { get; init; }
    /// <summary>False for a phase-space pixel outside the section (|sin alpha| &gt;= 1).</summary>
    public bool Valid { get; init; }
    /// <summary>Last position (the exit point on the play boundary when escaped).</summary>
    public double EndX { get; init; }
    public double EndY { get; init; }
    /// <summary>Final unit direction.</summary>
    public double EndDx { get; init; }
    public double EndDy { get; init; }
}

/// <summary>
/// The mirror-disk field and launch / gate configuration of one billiard frame,
/// built once from <see cref="FractalParameters"/> and traced per pixel. Public so
/// tests can check the physics (symmetry, time reversal) on the exact render path.
/// </summary>
public sealed class BilliardScene
{
    private readonly double[] _cx, _cy, _r;
    private readonly bool _exitGap;
    private readonly double _gapBase;    // angle of ring disk 0; gap k is centred at _gapBase + (k + 1/2) * 2pi/n
    // #1504 — Custom layout gates: gate k spans [_gateRel[k], _gateRel[k + 1]) radians
    // after _gapBase (the angle of disk k's centre, disks sorted by angle from disk 0).
    // Null for the generated geometries, whose gates are 2pi / n wide.
    private readonly double[]? _gateRel;

    public int DiskCount => _cx.Length;
    public int MaxBounces { get; }
    public int GateCount { get; }
    public double PlayRadius { get; }
    public double PathReference => PlayRadius * 4.0;
    public BilliardLaunchMode LaunchMode { get; }

    public double DiskX(int i) => _cx[i];
    public double DiskY(int i) => _cy[i];
    public double DiskR(int i) => _r[i];

    private BilliardScene(double[] cx, double[] cy, double[] r, int maxBounces, int gateCount,
                          bool exitGap, double gapBase, double playRadius, BilliardLaunchMode launch,
                          double[]? gateRel = null)
    {
        _cx = cx; _cy = cy; _r = r;
        MaxBounces = maxBounces; GateCount = gateCount;
        _exitGap = exitGap; _gapBase = gapBase; PlayRadius = playRadius; LaunchMode = launch;
        _gateRel = gateRel;
    }

    public static BilliardScene Build(FractalParameters p)
    {
        int maxBounces = Math.Clamp(p.BilliardMaxBounces, 1, 4096);
        double diskR = Math.Max(1e-4, p.BilliardDiskRadius);
        double sep = Math.Max(1e-4, p.BilliardSeparation);

        double[] cx, cy;
        double[]? r = null;
        double ringBase = 0.0;
        bool ring = false;
        var geometry = p.BilliardGeometry;
        List<BilliardDisk>? custom = null;
        if (geometry == BilliardGeometry.Custom)
        {
            custom = BilliardLayout.ParseCustomDisks(p.BilliardCustomDisks);
            if (custom.Count == 0) geometry = BilliardGeometry.ThreeDisk;   // no layout yet
        }
        switch (geometry)
        {
            case BilliardGeometry.Custom:
                // #1504 — explicit disks. Disk 0 stays first (the PhaseSpace launch disk);
                // the rest are ordered by centre angle from disk 0, so gate k is the gap
                // between disks k and k + 1 going anticlockwise, as on a ring. A list made
                // from a ring's centres is already in that order and builds the same scene.
                (cx, cy, r) = OrderByAngle(custom!);
                ringBase = Math.Atan2(cy[0], cx[0]);
                ring = true;
                break;
            case BilliardGeometry.Polygon:
            {
                // #1538 — disks on a regular polygon (BilliardLayout.PolygonCentres), then the
                // Custom ordering and angular-gap gates: around a convex polygon the angle order
                // from disk 0 is the perimeter order, so gate k is the gap between perimeter
                // neighbours. One disk per corner gives the ring's centres exactly.
                var pc = BilliardLayout.PolygonCentres(Math.Clamp(p.BilliardDiskCount, 1, 64), p.BilliardPolygonSides, sep);
                var disks = new List<BilliardDisk>(pc.Count);
                foreach (var (x, y) in pc) disks.Add(new BilliardDisk(x, y, diskR));
                (cx, cy, r) = OrderByAngle(disks);
                ringBase = Math.Atan2(cy[0], cx[0]);
                ring = true;
                break;
            }
            case BilliardGeometry.ThreeDisk:
                // Equilateral: centres at 90 deg, 210 deg, 330 deg on the sep circle.
                ringBase = Math.PI / 2.0;
                (cx, cy) = RingCentres(3, sep, ringBase);
                ring = true;
                break;
            case BilliardGeometry.Ring:
                (cx, cy) = RingCentres(Math.Clamp(p.BilliardDiskCount, 1, 64), sep, 0.0);
                ring = true;
                break;
            default: // NDisk — seeded pseudo-random, simple overlap reject.
                (cx, cy) = RandomCentres(Math.Clamp(p.BilliardDiskCount, 1, 64), diskR, sep, p.BilliardSeed);
                break;
        }
        if (r == null)
        {
            r = new double[cx.Length];
            Array.Fill(r, diskR);
        }

        // ExitGap needs a ring (or a Custom layout) with at least two gaps; otherwise
        // compass sectors.
        bool exitGap = p.BilliardGateMode == BilliardGateMode.ExitGap && ring && cx.Length >= 2;
        int gateCount = exitGap ? cx.Length : Math.Clamp(p.BilliardGateCount, 2, 64);
        double[]? gateRel = null;
        if (exitGap && geometry is BilliardGeometry.Custom or BilliardGeometry.Polygon)
        {
            gateRel = new double[cx.Length + 1];
            for (int i = 0; i < cx.Length; i++) gateRel[i] = RelAngle(Math.Atan2(cy[i], cx[i]), ringBase);
            gateRel[cx.Length] = 2.0 * Math.PI;
        }

        // Play region — a bounding circle the ray escapes through. Big enough to
        // enclose every disk with margin; far-field launches start on its boundary.
        // #1503: an NDisk field (or a grown one) can reach past the closed form, so the
        // circle also covers the farthest disk edge. Ring / ThreeDisk never do (sep + R
        // < 1.5 sep + 2R), so their play circle, and their render, are unchanged.
        double rPlay = Math.Max(3.0, sep * 1.5 + diskR * 2.0);
        double reach = 0.0;
        for (int i = 0; i < cx.Length; i++)
            reach = Math.Max(reach, Math.Sqrt(cx[i] * cx[i] + cy[i] * cy[i]) + r[i]);
        rPlay = Math.Max(rPlay, reach * PlayMargin);
        return new BilliardScene(cx, cy, r, maxBounces, gateCount, exitGap, ringBase, rPlay, p.BilliardLaunchMode, gateRel);
    }

    // Angle a measured anticlockwise from base, in [0, 2pi).
    private static double RelAngle(double a, double baseAngle)
    {
        double rel = (a - baseAngle) % (2.0 * Math.PI);
        return rel < 0 ? rel + 2.0 * Math.PI : rel;
    }

    // #1504 — disk 0 first, the rest by centre angle from disk 0 (stable for ties).
    private static (double[] X, double[] Y, double[] R) OrderByAngle(List<BilliardDisk> disks)
    {
        double a0 = Math.Atan2(disks[0].Y, disks[0].X);
        var order = new List<int>(disks.Count);
        for (int i = 1; i < disks.Count; i++) order.Add(i);
        order.Sort((i, j) =>
        {
            int c = RelAngle(Math.Atan2(disks[i].Y, disks[i].X), a0).CompareTo(RelAngle(Math.Atan2(disks[j].Y, disks[j].X), a0));
            return c != 0 ? c : i.CompareTo(j);
        });
        order.Insert(0, 0);
        var x = new double[disks.Count];
        var y = new double[disks.Count];
        var rr = new double[disks.Count];
        for (int k = 0; k < order.Count; k++)
        {
            x[k] = disks[order[k]].X; y[k] = disks[order[k]].Y; rr[k] = disks[order[k]].R;
        }
        return (x, y, rr);
    }

    /// <summary>Play-circle radius as a multiple of the farthest disk edge (#1503).</summary>
    public const double PlayMargin = 1.1;

    /// <summary>Minimum NDisk centre distance as a multiple of 2R: keeps an escape gap.</summary>
    public const double NDiskGapFactor = 1.05;

    /// <summary>Growth of the NDisk placement box per retry (#1503).</summary>
    public const double NDiskBoxGrowth = 1.25;

    /// <summary>
    /// NDisk centres: seeded random rejection sampling in a box of half-extent
    /// max(2R, sep). #1503 — that box does not scale with the count, so it used to
    /// stall after a few disks and silently keep fewer than requested. When a pass
    /// cannot place all <paramref name="n"/>, the box grows by <see cref="NDiskBoxGrowth"/>
    /// and placement restarts from the same seed. A field that fitted the first box
    /// is therefore exactly the field it always was.
    /// </summary>
    public static (double[] X, double[] Y) RandomCentres(int n, double diskR, double sep, int seed)
    {
        double half = Math.Max(diskR * 2.0, sep);   // placement box half-extent
        double min = (diskR + diskR) * NDiskGapFactor;
        var xs = new List<double>(n);
        var ys = new List<double>(n);
        // Each growth multiplies the box area by 1.56, so even 64 disks at the largest
        // radius fit within a handful of passes; the pass cap only guards the loop.
        for (int pass = 0; pass < 32; pass++, half *= NDiskBoxGrowth)
        {
            var rng = new Random(seed);
            xs.Clear(); ys.Clear();
            int attempts = 0, cap = n * 200;
            while (xs.Count < n && attempts++ < cap)
            {
                double x = (rng.NextDouble() * 2.0 - 1.0) * half;
                double y = (rng.NextDouble() * 2.0 - 1.0) * half;
                bool ok = true;
                for (int e = 0; e < xs.Count; e++)
                {
                    double dx = x - xs[e], dy = y - ys[e];
                    if (dx * dx + dy * dy < min * min) { ok = false; break; }
                }
                if (ok) { xs.Add(x); ys.Add(y); }
            }
            if (xs.Count == n) break;
        }
        return (xs.ToArray(), ys.ToArray());
    }

    private static (double[] X, double[] Y) RingCentres(int n, double sep, double baseAngle)
    {
        var x = new double[n];
        var y = new double[n];
        for (int k = 0; k < n; k++)
        {
            double a = baseAngle + k * (2.0 * Math.PI / n);
            x[k] = sep * Math.Cos(a);
            y[k] = sep * Math.Sin(a);
        }
        return (x, y);
    }

    /// <summary>Trace the launch a plane point encodes, per <see cref="LaunchMode"/>.</summary>
    /// <param name="path">#1447 — when non-null, receives the start point, every bounce
    /// point and the end point. Null on the per-pixel render path (no allocation).</param>
    public BilliardOutcome Launch(double planeX, double planeY, List<BilliardPoint>? path = null)
        => LaunchMode == BilliardLaunchMode.PhaseSpace
            ? LaunchPhaseSpace(planeX * (Math.PI / 2.0), planeY, path)
            : LaunchFarField(planeX, planeY, path);

    /// <summary>Far-field launch: impact parameter <paramref name="b"/>, incoming angle
    /// <paramref name="phi"/>. The ray starts offset perpendicular by b, pulled back to
    /// the play boundary along -dir so it enters the scene.</summary>
    public BilliardOutcome LaunchFarField(double b, double phi, List<BilliardPoint>? path = null)
    {
        double dx = Math.Cos(phi), dy = Math.Sin(phi);
        double px = -dy * b - dx * PlayRadius;
        double py = dx * b - dy * PlayRadius;

        // If the launch begins inside a disk (grazing geometry), declare trapped.
        for (int i = 0; i < _cx.Length; i++)
        {
            double ox = px - _cx[i], oy = py - _cy[i];
            if (ox * ox + oy * oy < _r[i] * _r[i])
            {
                path?.Add(new BilliardPoint(px, py));
                return new BilliardOutcome { GateId = -1, Valid = true, EndX = px, EndY = py, EndDx = dx, EndDy = dy };
            }
        }
        return Trace(px, py, dx, dy, -1, path);
    }

    /// <summary>Phase-space (Birkhoff) launch off disk 0: <paramref name="s"/> = angle of
    /// the surface point around the disk centre, <paramref name="sinAlpha"/> = sine of the
    /// outgoing angle to the outward normal (positive = counter-clockwise of the normal).
    /// Invalid when |sinAlpha| &gt;= 1 or the scene has no disks.</summary>
    public BilliardOutcome LaunchPhaseSpace(double s, double sinAlpha, List<BilliardPoint>? path = null)
    {
        if (_cx.Length == 0 || !(Math.Abs(sinAlpha) < 1.0))
            return new BilliardOutcome { GateId = -1, Valid = false };
        double nx = Math.Cos(s), ny = Math.Sin(s);
        double cosAlpha = Math.Sqrt(1.0 - sinAlpha * sinAlpha);
        double dx = cosAlpha * nx - sinAlpha * ny;
        double dy = cosAlpha * ny + sinAlpha * nx;
        double px = _cx[0] + _r[0] * nx;
        double py = _cy[0] + _r[0] * ny;
        return Trace(px, py, dx, dy, 0, path);
    }

    /// <summary>Trace a ray from (<paramref name="px"/>, <paramref name="py"/>) along the
    /// unit direction (<paramref name="dx"/>, <paramref name="dy"/>) until it leaves the
    /// play region or reaches the bounce cap. <paramref name="onDisk"/> names a disk the
    /// start point lies on (its own surface is not a hit), or -1. <paramref name="path"/>,
    /// when non-null, receives the start, each bounce point and the end (#1447).</summary>
    public BilliardOutcome Trace(double px, double py, double dx, double dy, int onDisk = -1,
                                 List<BilliardPoint>? path = null)
    {
        path?.Add(new BilliardPoint(px, py));
        double pathLen = 0.0;
        int bounces = 0;
        const double eps = 1e-9;
        int last = onDisk;

        while (bounces < MaxBounces)
        {
            // Nearest disk intersection ahead. A convex mirror cannot be hit twice
            // in a row, so the disk just left is skipped (no self-hit from rounding).
            double tHit = double.PositiveInfinity;
            int hit = -1;
            for (int i = 0; i < _cx.Length; i++)
            {
                if (i == last) continue;
                double ox = px - _cx[i], oy = py - _cy[i];
                double bb = ox * dx + oy * dy;                 // (oc . dir)
                double cc = ox * ox + oy * oy - _r[i] * _r[i];
                double disc = bb * bb - cc;
                if (disc < 0.0) continue;
                double t = -bb - Math.Sqrt(disc);              // near root
                if (t > eps && t < tHit) { tHit = t; hit = i; }
            }

            // Play-boundary exit (outward root of |p + t dir| = rPlay).
            double pb = px * dx + py * dy;
            double pc = px * px + py * py - PlayRadius * PlayRadius;
            double pdisc = pb * pb - pc;
            double tExit = pdisc > 0.0 ? -pb + Math.Sqrt(pdisc) : double.PositiveInfinity;

            if (hit < 0 || tExit <= tHit)
            {
                // Escapes. Classify the exit direction into a gate.
                double tOut = tExit < double.PositiveInfinity ? tExit : PlayRadius;
                path?.Add(new BilliardPoint(px + tOut * dx, py + tOut * dy));
                return new BilliardOutcome
                {
                    GateId = Gate(dx, dy),
                    Bounces = bounces,
                    PathLength = pathLen + tOut,
                    Valid = true,
                    EndX = px + tOut * dx, EndY = py + tOut * dy, EndDx = dx, EndDy = dy,
                };
            }

            // Reflect specularly off the hit disk.
            px += tHit * dx;
            py += tHit * dy;
            pathLen += tHit;
            path?.Add(new BilliardPoint(px, py));
            double nx = (px - _cx[hit]) / _r[hit];
            double ny = (py - _cy[hit]) / _r[hit];
            double dot = dx * nx + dy * ny;
            dx -= 2.0 * dot * nx;
            dy -= 2.0 * dot * ny;
            last = hit;
            bounces++;
        }

        return new BilliardOutcome   // trapped
        {
            GateId = -1, Bounces = bounces, PathLength = pathLen, Valid = true,
            EndX = px, EndY = py, EndDx = dx, EndDy = dy,
        };
    }

    /// <summary>#1447 — start angle (radians from +x) of gate <paramref name="gate"/>'s sector
    /// of exit directions; the sector spans 2pi / GateCount from there (see <see cref="Gate"/>).</summary>
    public double GateStartAngle(int gate)
        => _gateRel != null
            ? _gapBase + _gateRel[gate]
            : (_exitGap ? _gapBase : 0.0) + gate * (2.0 * Math.PI / GateCount);

    /// <summary>#1504 — angular width of gate <paramref name="gate"/>'s sector (2pi / GateCount
    /// except for a Custom layout's exit gaps).</summary>
    public double GateWidth(int gate)
        => _gateRel != null ? _gateRel[gate + 1] - _gateRel[gate] : 2.0 * Math.PI / GateCount;

    /// <summary>True when gates are the gaps between ring disks (ExitGap mode on a ring).</summary>
    public bool ExitGap => _exitGap;

    /// <summary>Gate of an escaping direction: compass sector from +x (Sector), or the
    /// sector centred on the gap between ring disks k and k+1 (ExitGap).</summary>
    public int Gate(double dx, double dy)
    {
        double ang = Math.Atan2(dy, dx);
        if (_exitGap) ang -= _gapBase;
        ang %= 2.0 * Math.PI;
        if (ang < 0) ang += 2.0 * Math.PI;
        if (_gateRel != null)
        {
            // Last gate whose start is at or before ang (gates are sorted, gate 0 at 0).
            int k = Array.BinarySearch(_gateRel, 0, GateCount, ang);
            if (k < 0) k = ~k - 1;
            return Math.Clamp(k, 0, GateCount - 1);
        }
        int g = (int)(ang / (2.0 * Math.PI) * GateCount);
        return g >= GateCount ? GateCount - 1 : g;
    }
}
