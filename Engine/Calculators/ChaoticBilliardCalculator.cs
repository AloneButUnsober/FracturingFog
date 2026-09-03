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
// Not escape-time: categorical gate colouring goes through IBilliardColorMap
// (the INewtonColorMap precedent). When no such theme is selected the calculator
// falls back to built-in HSV-per-gate shading, so it renders standalone.

using System;
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

    private readonly struct Disk
    {
        public readonly double Cx, Cy, R;
        public Disk(double cx, double cy, double r) { Cx = cx; Cy = cy; R = r; }
    }

    public void Calculate(CancellationToken ct = default)
    {
        int maxBounces = Math.Clamp(FractalParameters.BilliardMaxBounces, 1, 4096);
        int gateCount = Math.Clamp(FractalParameters.BilliardGateCount, 2, 64);
        double diskR = Math.Max(1e-4, FractalParameters.BilliardDiskRadius);
        double sep = Math.Max(1e-4, FractalParameters.BilliardSeparation);

        Disk[] disks = BuildDisks(FractalParameters, diskR, sep);

        // Play region — a bounding circle the ray escapes through. Big enough to
        // enclose every disk with margin; launches start on its boundary.
        double rPlay = Math.Max(3.0, sep * 1.5 + diskR * 2.0);
        double pathRef = rPlay * 4.0;   // path-length normalisation reference

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
            // phi (incoming angle) along the y axis.
            double phi = centerY + (y - height * 0.5) * pixelPitch;
            double dirX = Math.Cos(phi), dirY = Math.Sin(phi);
            double perpX = -dirY, perpY = dirX;   // unit perpendicular

            for (int x = 0; x < width; x++)
            {
                // impact parameter b along the x axis.
                double b = centerX + (x - width * 0.5) * pixelPitch;

                Trace(disks, rPlay, maxBounces, gateCount,
                      dirX, dirY, perpX, perpY, b,
                      out int gateId, out int bounces, out double pathLen);

                int idx = rowBase + x;
                SmoothBuffer[idx] = bounces;

                uint col;
                if (billiardMap != null)
                {
                    float pl = (float)Math.Clamp(pathLen / pathRef, 0.0, 1.0);
                    col = unchecked((uint)billiardMap.MapBilliard(
                        gateId, gateCount, bounces, maxBounces, pl));
                }
                else if (gateId < 0)
                {
                    col = trappedFallback;   // trapped -> theme interior colour
                }
                else
                {
                    // Built-in fallback: hue per gate, brightness fades with bounces.
                    float hue = (float)gateId / gateCount;
                    float shade = 1.0f - Math.Min(bounces / (float)maxBounces, 0.85f);
                    col = unchecked((uint)HsvToArgb(hue, 0.85f, shade));
                }
                ColorBuffer[idx] = col;
            }
        });
    }

    // Build the mirror-disk field for the selected arrangement.
    private static Disk[] BuildDisks(FractalParameters p, double diskR, double sep)
    {
        switch (p.BilliardGeometry)
        {
            case BilliardGeometry.ThreeDisk:
            {
                // Equilateral: centres at 90 deg, 210 deg, 330 deg on the sep circle.
                var d = new Disk[3];
                for (int k = 0; k < 3; k++)
                {
                    double a = Math.PI / 2.0 + k * (2.0 * Math.PI / 3.0);
                    d[k] = new Disk(sep * Math.Cos(a), sep * Math.Sin(a), diskR);
                }
                return d;
            }
            case BilliardGeometry.Ring:
            {
                int n = Math.Clamp(p.BilliardDiskCount, 1, 64);
                var d = new Disk[n];
                for (int k = 0; k < n; k++)
                {
                    double a = k * (2.0 * Math.PI / n);
                    d[k] = new Disk(sep * Math.Cos(a), sep * Math.Sin(a), diskR);
                }
                return d;
            }
            default: // NDisk — seeded pseudo-random, simple overlap reject.
            {
                int n = Math.Clamp(p.BilliardDiskCount, 1, 64);
                var rng = new Random(p.BilliardSeed);
                var list = new System.Collections.Generic.List<Disk>(n);
                double half = Math.Max(diskR * 2.0, sep);   // placement box half-extent
                int attempts = 0, cap = n * 200;
                while (list.Count < n && attempts++ < cap)
                {
                    double cx = (rng.NextDouble() * 2.0 - 1.0) * half;
                    double cy = (rng.NextDouble() * 2.0 - 1.0) * half;
                    bool ok = true;
                    foreach (var e in list)
                    {
                        double dx = cx - e.Cx, dy = cy - e.Cy;
                        double min = (diskR + e.R) * 1.05;   // keep an escape gap
                        if (dx * dx + dy * dy < min * min) { ok = false; break; }
                    }
                    if (ok) list.Add(new Disk(cx, cy, diskR));
                }
                return list.ToArray();
            }
        }
    }

    // Trace one launch. dir = incoming unit direction; perp = its unit normal;
    // b = signed impact parameter. Start on the play boundary heading inward.
    private static void Trace(
        Disk[] disks, double rPlay, int maxBounces, int gateCount,
        double dirX, double dirY, double perpX, double perpY, double b,
        out int gateId, out int bounces, out double pathLen)
    {
        // Start point: offset perpendicular by b, pulled back to the play
        // boundary along -dir so the ray enters the scene.
        double px = perpX * b - dirX * rPlay;
        double py = perpY * b - dirY * rPlay;
        double dx = dirX, dy = dirY;

        pathLen = 0.0;
        bounces = 0;
        const double eps = 1e-9;

        // If the launch begins inside a disk (grazing geometry), declare trapped.
        for (int i = 0; i < disks.Length; i++)
        {
            double ox = px - disks[i].Cx, oy = py - disks[i].Cy;
            if (ox * ox + oy * oy < disks[i].R * disks[i].R) { gateId = -1; return; }
        }

        while (bounces < maxBounces)
        {
            // Nearest disk intersection ahead.
            double tHit = double.PositiveInfinity;
            int hit = -1;
            for (int i = 0; i < disks.Length; i++)
            {
                double ox = px - disks[i].Cx, oy = py - disks[i].Cy;
                double bb = ox * dx + oy * dy;                 // (oc . dir)
                double cc = ox * ox + oy * oy - disks[i].R * disks[i].R;
                double disc = bb * bb - cc;
                if (disc < 0.0) continue;
                double sq = Math.Sqrt(disc);
                double t = -bb - sq;                           // near root
                if (t > eps && t < tHit) { tHit = t; hit = i; }
            }

            // Play-boundary exit (outward root of |p + t dir| = rPlay).
            double pb = px * dx + py * dy;
            double pc = px * px + py * py - rPlay * rPlay;
            double pdisc = pb * pb - pc;
            double tExit = pdisc > 0.0 ? -pb + Math.Sqrt(pdisc) : double.PositiveInfinity;

            if (hit < 0 || tExit <= tHit)
            {
                // Escapes. Classify the exit direction into a gate sector.
                pathLen += (tExit < double.PositiveInfinity ? tExit : rPlay);
                double ang = Math.Atan2(dy, dx);
                if (ang < 0) ang += 2.0 * Math.PI;
                gateId = (int)(ang / (2.0 * Math.PI) * gateCount);
                if (gateId >= gateCount) gateId = gateCount - 1;
                return;
            }

            // Reflect specularly off the hit disk.
            px += tHit * dx;
            py += tHit * dy;
            pathLen += tHit;
            double nx = (px - disks[hit].Cx) / disks[hit].R;
            double ny = (py - disks[hit].Cy) / disks[hit].R;
            double dot = dx * nx + dy * ny;
            dx -= 2.0 * dot * nx;
            dy -= 2.0 * dot * ny;
            bounces++;
        }

        gateId = -1;   // trapped
    }

    private static int HsvToArgb(float h, float s, float v)
    {
        h = h * 6f;
        int i = (int)Math.Floor(h);
        float f = h - i;
        float p = v * (1 - s);
        float q = v * (1 - s * f);
        float t = v * (1 - s * (1 - f));
        float rF, gF, bF;
        switch (i % 6)
        {
            case 0: rF = v; gF = t; bF = p; break;
            case 1: rF = q; gF = v; bF = p; break;
            case 2: rF = p; gF = v; bF = t; break;
            case 3: rF = p; gF = q; bF = v; break;
            case 4: rF = t; gF = p; bF = v; break;
            case 5: rF = v; gF = p; bF = q; break;
            default: rF = gF = bF = 0; break;
        }
        int r = (int)(rF * 255);
        int g = (int)(gF * 255);
        int bch = (int)(bF * 255);
        return unchecked((int)0xFF000000 | (r << 16) | (g << 8) | bch);
    }
}
