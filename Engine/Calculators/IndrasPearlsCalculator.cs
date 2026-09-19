// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// IndrasPearlsCalculator.cs
//
// Indra's Pearls — 2D limit set of a two-generator complex-Möbius Kleinian
// group. Slice S1 (#892, epic #850 / design Docs/Technical/Indras-Pearls-2D-
// DesignPlan.md).
//
// Not escape-time and not a 3D distance estimator (the shipped KleinianCalculator
// handles the 3D sphere-inversion solid). Here the limit set Λ is a fractal curve
// on the plane, plotted combinatorially: enumerate reduced group words over the
// alphabet {a, A, b, B} up to a depth cap (no immediate back-tracking), apply each
// word's accumulated matrix to seed points that already lie on Λ (generator fixed
// points), and accumulate the images into a per-pixel density buffer. The buffer
// is then coloured by log-density through the active IColorMap (DLA precedent).
//
// SupportsZoomPan = true (Zoomable2D, Apollonian contract): the plane is mapped
// through (CenterX, CenterY, Zoom) before plotting, so pan/zoom reframes the same
// enumerated set and deeper detail is revealed by the density accumulating in
// fewer, finer cells. Deep zoom is a full re-enumeration, not perturbation
// (combinatorial, not iterative) — see design doc §3.4.
//
// S1 renders the default Maskit μ = 2i "apple" group (MSW p. 259). The group
// family / parameters / word-depth / render-mode become user-facing
// FractalParameters + a params panel in S2 (#893); until then they are the
// constants below.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class IndrasPearlsCalculator : IFractalCalculator
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

    // Safety ceiling on enumerated words (a level that would exceed it is not
    // expanded further) so an unexpected group can't run unbounded. Word count
    // grows ~4·3^(d−1); a full BFS to depth 12 is ~1M nodes.
    private const int NodeBudget = 4_000_000;

    /// <summary>Builds the group from the active FractalParameters (S2, #893).
    /// The generator matrices are derived from the scalar μ / traces / c.</summary>
    private IndrasGroup BuildGroup()
    {
        var p = FractalParameters;
        return p.IndrasFamily switch
        {
            IndrasGroupFamily.GrandmaRecipe => IndrasGroup.Grandma(
                new Complex(p.IndrasGrandmaTaRe, p.IndrasGrandmaTaIm),
                new Complex(p.IndrasGrandmaTbRe, p.IndrasGrandmaTbIm),
                p.IndrasGrandmaSecondSolution),
            IndrasGroupFamily.Riley => IndrasGroup.Riley(
                new Complex(p.IndrasRileyCRe, p.IndrasRileyCIm)),
            _ => IndrasGroup.Maskit(new Complex(p.IndrasMaskitMuRe, p.IndrasMaskitMuIm)),
        };
    }

    public IndrasPearlsCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    public void Calculate(CancellationToken ct = default)
    {
        Array.Clear(ColorBuffer, 0, ColorBuffer.Length);
        if (Width <= 0 || Height <= 0) return;

        IndrasGroup group = BuildGroup();

        // World → screen: same convention as ApollonianCalculator — pixel pitch
        // (4/Width)/Zoom, fractal origin at (CenterX, CenterY), +imag downward.
        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        double invPitch = 1.0 / pixelPitch;
        double halfW = Width * 0.5;
        double halfH = Height * 0.5;

        ColorMap.MaxIterations = 256;

        // Curve trace (S3, #894) — the crisp limit-CURVE via the MSW ch. 9
        // special-words DFS. Returns false when the group is non-discrete (long
        // segments survive to the depth cap); then fall back to the robust point
        // cloud (design doc §3.4 / §7 mitigation).
        if (FractalParameters.IndrasRenderMode == IndrasRenderMode.CurveTrace
            && TryRenderCurveTrace(group, pixelPitch, invPitch, halfW, halfH, ct))
            return;

        if (ct.IsCancellationRequested) return;
        Array.Clear(ColorBuffer, 0, ColorBuffer.Length);   // clear a partial curve
        RenderPointCloud(group, invPitch, halfW, halfH, ct);
    }

    // ── BFS density point cloud (S1/S2) ──────────────────────────────────────
    private void RenderPointCloud(IndrasGroup group, double invPitch,
        double halfW, double halfH, CancellationToken ct)
    {
        int maxDepth = Math.Clamp(FractalParameters.IndrasMaxWordDepth, 2, 16);
        var seeds = new List<Complex>(group.SeedPoints());
        if (seeds.Count == 0) return;

        var density = new int[Width * Height];
        int maxCount = 0;
        Mobius[] letters = group.Letters;

        void Plot(in Mobius m)
        {
            for (int s = 0; s < seeds.Count; s++)
            {
                if (!m.TryApply(seeds[s], out Complex z)) continue;
                double sx = (z.Real - CenterX) * invPitch + halfW;
                double sy = (z.Imaginary - CenterY) * invPitch + halfH;
                int px = (int)sx;
                int py = (int)sy;
                if ((uint)px >= (uint)Width || (uint)py >= (uint)Height) continue;
                int idx = py * Width + px;
                int c = ++density[idx];
                if (c > maxCount) maxCount = c;
            }
        }

        // BFS over the reduced-word tree, level by level (even coverage of Λ).
        var frontier = new List<(Mobius M, int Last)> { (Mobius.Identity, -1) };
        Plot(Mobius.Identity);
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            if (ct.IsCancellationRequested) return;
            var next = new List<(Mobius, int)>(frontier.Count * 3);
            foreach (var (m, last) in frontier)
            {
                int forbidden = last < 0 ? -1 : IndrasGroup.InverseLetter(last);
                for (int letter = 0; letter < 4; letter++)
                {
                    if (letter == forbidden) continue;
                    Mobius child = m.Multiply(letters[letter]).Normalized();
                    Plot(child);
                    next.Add((child, letter));
                }
            }
            if (next.Count > NodeBudget) { frontier = next; break; }
            frontier = next;
        }
        if (ct.IsCancellationRequested) return;

        double logMax = Math.Log(1.0 + Math.Max(1, maxCount));
        double invLogMax = logMax > 0 ? 1.0 / logMax : 0.0;
        for (int i = 0; i < density.Length; i++)
        {
            int c = density[i];
            if (c == 0) continue;
            float t = (float)(Math.Log(1.0 + c) * invLogMax * 255.0);
            ColorBuffer[i] = (uint)ColorMap.Map(t, 0f, 256);
        }
    }

    // ── DFS special-words curve tracer (S3, #894) ────────────────────────────
    // The Mumford–Series–Wright ch. 9 boundary-tracing algorithm. The alphabet is
    // ordered {a, b, A, B} = {0, 1, 2, 3} with inverse(i) = (i + 2) mod 4, so the
    // three continuations of a reduced word are the letters prev+1, prev, prev−1
    // (skipping the inverse prev+2). At every node the accumulated word is applied
    // to a fixed triple of "special word" repelling fixed points; when successive
    // images fall within one pixel the branch has converged onto Λ and the images
    // are joined by line segments (the crisp curve); otherwise the branch recurses.
    // Algorithm + special words verified against Tim Hutton's reference
    // implementation of the book.
    private const int CurveMaxDepth = 80;
    private static int Mod4(int k) => ((k % 4) + 4) % 4;

    private bool TryRenderCurveTrace(IndrasGroup group, double pixelPitch,
        double invPitch, double halfW, double halfH, CancellationToken ct)
    {
        // gens in {a, b, A, B} order (inverse = +2 mod 4).
        Mobius a = group.GenA, b = group.GenB;
        Mobius A = a.Inverse(), B = b.Inverse();
        var gens = new[] { a, b, A, B };

        Mobius W(params int[] letters)
        {
            Mobius m = gens[letters[0]];
            for (int i = 1; i < letters.Length; i++) m = m.Multiply(gens[letters[i]]);
            return m;
        }

        // Repetends per incoming letter (the special-words fixed points, MSW ch. 9).
        // Letters: a=0, b=1, A=2, B=3.
        var repetends = new Complex[4][];
        repetends[0] = new[] { W(1, 2, 3, 0).RepellingFixedPoint(), gens[0].RepellingFixedPoint(), W(3, 2, 1, 0).RepellingFixedPoint() }; // bABa, a, BAba
        repetends[1] = new[] { W(2, 3, 0, 1).RepellingFixedPoint(), gens[1].RepellingFixedPoint(), W(0, 3, 2, 1).RepellingFixedPoint() }; // ABab, b, aBAb
        repetends[2] = new[] { W(3, 0, 1, 2).RepellingFixedPoint(), gens[2].RepellingFixedPoint(), W(1, 0, 3, 2).RepellingFixedPoint() }; // BabA, A, baBA
        repetends[3] = new[] { W(0, 1, 2, 3).RepellingFixedPoint(), gens[3].RepellingFixedPoint(), W(2, 1, 0, 3).RepellingFixedPoint() }; // abAB, B, AbaB

        foreach (var arr in repetends)
            foreach (var rp in arr)
                if (double.IsNaN(rp.Real) || double.IsNaN(rp.Imaginary)) return false;

        double eps2 = pixelPitch * pixelPitch;      // "within one pixel"
        const double maxLine2 = 1.0;                // abort if long lines survive
        var z = new Complex[3];
        int nodes = 0;
        const int NodeCap = 4_000_000;              // keeps a slow group bounded

        // explore_tree; returns false to abort the whole trace (non-discrete group).
        bool Explore(in Mobius x, int prev, int level)
        {
            if (ct.IsCancellationRequested || nodes >= NodeCap) return true;   // stop, keep what's drawn
            nodes++;
            for (int k = prev + 1; k >= prev - 1; k--)
            {
                int iTag = Mod4(k);
                Mobius y = x.Multiply(gens[iTag]);
                var rep = repetends[iTag];
                bool closeEnough = true;
                for (int i = 0; i < 3; i++)
                {
                    z[i] = y.TryApply(rep[i], out var zi) ? zi : (y.A / y.C);
                    if (i > 0)
                    {
                        double dx = z[i].Real - z[i - 1].Real, dy = z[i].Imaginary - z[i - 1].Imaginary;
                        double d2 = dx * dx + dy * dy;
                        if (d2 > eps2) closeEnough = false;
                        if (d2 > maxLine2 && level >= CurveMaxDepth) return false;   // abort
                    }
                }

                if (closeEnough || level >= CurveMaxDepth)
                {
                    if (closeEnough)
                    {
                        uint col = (uint)ColorMap.Map(level * 8 % 256, 0f, 256);
                        for (int i = 0; i < 2; i++)
                            DrawLine(z[i], z[i + 1], invPitch, halfW, halfH, col);
                    }
                    else
                    {
                        // At the depth cap without convergence — plot the points
                        // (a dust component of Λ, e.g. a Cantor set).
                        uint col = (uint)ColorMap.Map(200f, 0f, 256);
                        for (int i = 0; i < 3; i++) PlotPoint(z[i], invPitch, halfW, halfH, col);
                    }
                }
                else if (!Explore(y, iTag, level + 1))
                {
                    return false;
                }
            }
            return true;
        }

        // Start from all four letters (full coverage without the reference's
        // symmetry-duplication shortcut).
        foreach (int start in new[] { 0, 3, 2, 1 })
            if (!Explore(gens[start], start, 1))
                return false;

        return true;
    }

    private void PlotPoint(Complex z, double invPitch, double halfW, double halfH, uint col)
    {
        int px = (int)((z.Real - CenterX) * invPitch + halfW);
        int py = (int)((z.Imaginary - CenterY) * invPitch + halfH);
        if ((uint)px < (uint)Width && (uint)py < (uint)Height) ColorBuffer[py * Width + px] = col;
    }

    // Integer Bresenham line between two world points (clipped to the buffer).
    private void DrawLine(Complex p0, Complex p1, double invPitch, double halfW, double halfH, uint col)
    {
        int x0 = (int)((p0.Real - CenterX) * invPitch + halfW);
        int y0 = (int)((p0.Imaginary - CenterY) * invPitch + halfH);
        int x1 = (int)((p1.Real - CenterX) * invPitch + halfW);
        int y1 = (int)((p1.Imaginary - CenterY) * invPitch + halfH);

        // Cheap reject: both endpoints far outside on the same side.
        if ((x0 < 0 && x1 < 0) || (y0 < 0 && y1 < 0)
            || (x0 >= Width && x1 >= Width) || (y0 >= Height && y1 >= Height)) return;

        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int guard = Width + Height + 4;   // segments are pixel-short; cap anyway
        while (guard-- > 0)
        {
            if ((uint)x0 < (uint)Width && (uint)y0 < (uint)Height) ColorBuffer[y0 * Width + x0] = col;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
