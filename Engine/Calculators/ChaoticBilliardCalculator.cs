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

        // Standard 2D plane mapping: pixel pitch = (4 / Width) / Zoom, origin at
        // (CenterX, CenterY). The plane coordinate IS the initial condition.
        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        int width = Width, height = Height;
        double centerX = CenterX, centerY = CenterY;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowBase = y * width;
            double planeY = centerY + (y - height * 0.5) * pixelPitch;

            for (int x = 0; x < width; x++)
            {
                double planeX = centerX + (x - width * 0.5) * pixelPitch;
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
                          bool exitGap, double gapBase, double playRadius, BilliardLaunchMode launch)
    {
        _cx = cx; _cy = cy; _r = r;
        MaxBounces = maxBounces; GateCount = gateCount;
        _exitGap = exitGap; _gapBase = gapBase; PlayRadius = playRadius; LaunchMode = launch;
    }

    public static BilliardScene Build(FractalParameters p)
    {
        int maxBounces = Math.Clamp(p.BilliardMaxBounces, 1, 4096);
        double diskR = Math.Max(1e-4, p.BilliardDiskRadius);
        double sep = Math.Max(1e-4, p.BilliardSeparation);

        double[] cx, cy;
        double ringBase = 0.0;
        bool ring = false;
        switch (p.BilliardGeometry)
        {
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
            {
                int n = Math.Clamp(p.BilliardDiskCount, 1, 64);
                var rng = new Random(p.BilliardSeed);
                var xs = new List<double>(n);
                var ys = new List<double>(n);
                double half = Math.Max(diskR * 2.0, sep);   // placement box half-extent
                int attempts = 0, cap = n * 200;
                while (xs.Count < n && attempts++ < cap)
                {
                    double x = (rng.NextDouble() * 2.0 - 1.0) * half;
                    double y = (rng.NextDouble() * 2.0 - 1.0) * half;
                    bool ok = true;
                    for (int e = 0; e < xs.Count; e++)
                    {
                        double dx = x - xs[e], dy = y - ys[e];
                        double min = (diskR + diskR) * 1.05;   // keep an escape gap
                        if (dx * dx + dy * dy < min * min) { ok = false; break; }
                    }
                    if (ok) { xs.Add(x); ys.Add(y); }
                }
                cx = xs.ToArray(); cy = ys.ToArray();
                break;
            }
        }
        var r = new double[cx.Length];
        Array.Fill(r, diskR);

        // ExitGap needs a ring with at least two gaps; otherwise compass sectors.
        bool exitGap = p.BilliardGateMode == BilliardGateMode.ExitGap && ring && cx.Length >= 2;
        int gateCount = exitGap ? cx.Length : Math.Clamp(p.BilliardGateCount, 2, 64);

        // Play region — a bounding circle the ray escapes through. Big enough to
        // enclose every disk with margin; far-field launches start on its boundary.
        double rPlay = Math.Max(3.0, sep * 1.5 + diskR * 2.0);
        return new BilliardScene(cx, cy, r, maxBounces, gateCount, exitGap, ringBase, rPlay, p.BilliardLaunchMode);
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
    public BilliardOutcome Launch(double planeX, double planeY)
        => LaunchMode == BilliardLaunchMode.PhaseSpace
            ? LaunchPhaseSpace(planeX * (Math.PI / 2.0), planeY)
            : LaunchFarField(planeX, planeY);

    /// <summary>Far-field launch: impact parameter <paramref name="b"/>, incoming angle
    /// <paramref name="phi"/>. The ray starts offset perpendicular by b, pulled back to
    /// the play boundary along -dir so it enters the scene.</summary>
    public BilliardOutcome LaunchFarField(double b, double phi)
    {
        double dx = Math.Cos(phi), dy = Math.Sin(phi);
        double px = -dy * b - dx * PlayRadius;
        double py = dx * b - dy * PlayRadius;

        // If the launch begins inside a disk (grazing geometry), declare trapped.
        for (int i = 0; i < _cx.Length; i++)
        {
            double ox = px - _cx[i], oy = py - _cy[i];
            if (ox * ox + oy * oy < _r[i] * _r[i])
                return new BilliardOutcome { GateId = -1, Valid = true, EndX = px, EndY = py, EndDx = dx, EndDy = dy };
        }
        return Trace(px, py, dx, dy, -1);
    }

    /// <summary>Phase-space (Birkhoff) launch off disk 0: <paramref name="s"/> = angle of
    /// the surface point around the disk centre, <paramref name="sinAlpha"/> = sine of the
    /// outgoing angle to the outward normal (positive = counter-clockwise of the normal).
    /// Invalid when |sinAlpha| &gt;= 1 or the scene has no disks.</summary>
    public BilliardOutcome LaunchPhaseSpace(double s, double sinAlpha)
    {
        if (_cx.Length == 0 || !(Math.Abs(sinAlpha) < 1.0))
            return new BilliardOutcome { GateId = -1, Valid = false };
        double nx = Math.Cos(s), ny = Math.Sin(s);
        double cosAlpha = Math.Sqrt(1.0 - sinAlpha * sinAlpha);
        double dx = cosAlpha * nx - sinAlpha * ny;
        double dy = cosAlpha * ny + sinAlpha * nx;
        double px = _cx[0] + _r[0] * nx;
        double py = _cy[0] + _r[0] * ny;
        return Trace(px, py, dx, dy, 0);
    }

    /// <summary>Trace a ray from (<paramref name="px"/>, <paramref name="py"/>) along the
    /// unit direction (<paramref name="dx"/>, <paramref name="dy"/>) until it leaves the
    /// play region or reaches the bounce cap. <paramref name="onDisk"/> names a disk the
    /// start point lies on (its own surface is not a hit), or -1.</summary>
    public BilliardOutcome Trace(double px, double py, double dx, double dy, int onDisk = -1)
    {
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

    /// <summary>Gate of an escaping direction: compass sector from +x (Sector), or the
    /// sector centred on the gap between ring disks k and k+1 (ExitGap).</summary>
    public int Gate(double dx, double dy)
    {
        double ang = Math.Atan2(dy, dx);
        if (_exitGap) ang -= _gapBase;
        ang %= 2.0 * Math.PI;
        if (ang < 0) ang += 2.0 * Math.PI;
        int g = (int)(ang / (2.0 * Math.PI) * GateCount);
        return g >= GateCount ? GateCount - 1 : g;
    }
}
