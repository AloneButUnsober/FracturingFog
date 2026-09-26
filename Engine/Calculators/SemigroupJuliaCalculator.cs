// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SemigroupJuliaCalculator.cs (#918 SG3, epic #911)
//
// The faithful parabolic-implosion renderer: the limit set J(g_α) of the
// rational semigroup ⟨f_c, g_α⟩ (Lavaurs's theorem), for the parabolic quadratic
// f_c(z) = z² + 1/4. A point is coloured by the SHORTEST word in {f_c, g_α} that
// escapes (SemigroupEscape word-tree, S7); a single deterministic orbit
// undercounts and would render the set as all-bounded (the S6 finding).
//
// g_α is the Lavaurs map (LavaursEngine / LavaursCoordinateTable, SG1). Its two
// Fatou grids are α-INDEPENDENT, so the table is built ONCE (the germ is fixed at
// c = 1/4) and cached process-wide; the Lavaurs phase α only shifts the σ lookup,
// so sweeping α (SG4) never rebuilds the table. g_α is defined only on the
// attracting basin — its domain-restricted generator prunes its branch where the
// table lookup is invalid, so the word tree there falls back to f_c alone.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Abstractions.Animation;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class SemigroupJuliaCalculator : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // Escape-depth field (shortest escaping word length; 0 = bounded/in-set),
    // doubles as the Relief-3D height field.
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 256;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public bool SupportsZoomPan => true;
    public FractalParameters FractalParameters { get; set; } = new();

    // The parabolic germ f_c(z) = z² + 1/4 (LavaursEngine.C).
    const double C = LavaursEngine.C;

    // Process-wide α-independent Fatou tables (germ fixed at c = 1/4). Built once
    // on first use — the expensive step (~tens of k inverse solves); cheap after.
    static LavaursCoordinateTable? _table;
    static readonly object _tableLock = new();

    static LavaursCoordinateTable Table()
    {
        if (_table != null) return _table;
        lock (_tableLock)
        {
            // one-time parallel build (a few seconds); cached for the process. The
            // engine iteration count is modest because the bilinear interpolation
            // error dominates the tabulated coordinates anyway. Resolution trades
            // first-render latency against interpolation crispness.
            _table ??= new LavaursCoordinateTable(
                new LavaursEngine(iterations: 500, deepOffset: 18.0),
                attNx: 176, attNy: 176, invNx: 208, invNy: 208);
        }
        return _table;
    }

    public SemigroupJuliaCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
    }

    public void Calculate(CancellationToken ct = default)
    {
        var p = FractalParameters;
        double alpha = p.SemigroupAlpha;
        int maxDepth = Math.Max(4, p.SemigroupWordDepth);
        int beam = Math.Max(1, p.SemigroupBeam);
        double escapeR = Math.Max(2.0, p.SemigroupEscapeRadius);

        ColorMap.MaxIterations = maxDepth;
        LavaursCoordinateTable table = Table();

        // Build the two generators ONCE (shared read-only across threads): the
        // parabolic map f_c, and the Lavaurs map g_α restricted to its basin domain.
        var gens = new SemigroupEscape.Generator[]
        {
            new SemigroupEscape.Generator(z => z * z + C),
            new SemigroupEscape.Generator(
                z => { table.TryGAlpha(z, alpha, out Complex g); return g; },
                z => table.TryGAlpha(z, alpha, out _)),
        };

        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        int width = Width, height = Height;
        double centerX = CenterX, centerY = CenterY;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowBase = y * width;
            double zy = centerY + (y - height * 0.5) * pixelPitch;
            for (int x = 0; x < width; x++)
            {
                double zx = centerX + (x - width * 0.5) * pixelPitch;
                int depth = SemigroupEscape.EscapeDepth(new Complex(zx, zy), gens, escapeR, maxDepth, beam);
                float smooth = depth < 0 ? 0f : depth;      // bounded (in-set) → 0
                int idx = rowBase + x;
                SmoothBuffer[idx] = smooth;
                ColorBuffer[idx] = unchecked((uint)ColorMap.Map(smooth, 0f, maxDepth));
            }
        });
    }
}
