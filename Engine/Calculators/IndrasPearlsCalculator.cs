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

    // S1 defaults (become FractalParameters fields + a UI panel in S2 / #893).
    // Maskit μ = 2i is the MSW p. 259 "apple" — a is parabolic, fixed point i.
    private static readonly Complex DefaultMaskitMu = new(0, 2);
    // Word-tree depth cap. Reduced-word count grows ~4·3^(d−1); a full BFS to
    // depth 12 is ~1M nodes — even coverage of Λ, bounded cost.
    private const int DefaultMaxWordDepth = 12;
    // Safety ceiling on enumerated words (a level that would exceed it is not
    // expanded further) so an unexpected group can't run unbounded.
    private const int NodeBudget = 4_000_000;

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

        // Build the default group and the seed points (fixed points on Λ).
        IndrasGroup group = IndrasGroup.Maskit(DefaultMaskitMu);
        var seeds = new List<Complex>(group.SeedPoints());
        if (seeds.Count == 0) return;

        // World → screen: same convention as ApollonianCalculator — pixel pitch
        // (4/Width)/Zoom, fractal origin at (CenterX, CenterY), +imag downward.
        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        double invPitch = 1.0 / pixelPitch;
        double halfW = Width * 0.5;
        double halfH = Height * 0.5;

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

        // BFS over the reduced-word tree, level by level (design doc §3.4). Each
        // frontier entry is (accumulated matrix, last letter); the next letter may
        // not be its inverse (no aA / bB), so every enumerated word is reduced.
        // Level-order (not DFS) gives even coverage of the limit set within the
        // depth cap. Periodic renormalisation keeps long products well-scaled.
        var frontier = new List<(Mobius M, int Last)> { (Mobius.Identity, -1) };
        Plot(Mobius.Identity);
        for (int depth = 1; depth <= DefaultMaxWordDepth; depth++)
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

        // Colour pass — log-density through the active palette (DLA precedent).
        // Vacuum cells keep the pre-cleared 0 (transparent black background).
        ColorMap.MaxIterations = 256;
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
}
