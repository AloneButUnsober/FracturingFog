// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// LogisticCalculator.cs
//
// Bifurcation diagram of the discrete logistic map
//     x_{n+1} = r · x_n · (1 − x_n)
// rendered as a density histogram over the (r, x) plane. This is NOT an
// escape-time fractal — each pixel column is a long iteration of the map at
// a single r, and the visited x values are accumulated into a per-pixel
// hit counter. Final tone-map mirrors BuddhaFamilyCalculator.RenderColorMap
// (log-normalised density, theme-driven foreground, alpha-blended toward
// IColorMap.InSetColor).
//
// View convention matches the escape-time families:
//     pixel (W/2, H/2)  ↔  (CenterX, CenterY)
//     scale = (3.5 / max(W, H)) / Zoom
// CenterX = horizontal coord = r-axis. CenterY = vertical coord = x-axis.
// Default frame (CenterX=3.5, CenterY=0.5, Zoom=2.0) shows the period-doubling
// cascade through chaos at r ∈ ~[2.6, 4.4], x ∈ ~[0, 1].
//
// Per-column iteration:
//   • Seed x₀ = 0.5
//   • Burn-in = MaxIterations/2 (settle onto the attractor)
//   • Plot = MaxIterations/2 (each visited x lands in the column's hit
//     buffer)
// Columns whose r falls outside [0, 4] are skipped (orbit diverges).

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class LogisticCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 3.5;
    public double CenterY { get; set; } = 0.5;
    public double Zoom { get; set; } = 2.0;
    public int MaxIterations { get; set; } = 4_000;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    private uint[] _hits = Array.Empty<uint>();

    public LogisticCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        _hits = new uint[n];
    }

    public void Calculate(CancellationToken ct)
    {
        int width = Width;
        int height = Height;
        if (width < 1 || height < 1) return;

        Array.Clear(_hits);

        double scale = (3.5 / Math.Max(width, height)) / Zoom;
        double centerX = CenterX;
        double centerY = CenterY;
        int maxIt = Math.Max(64, MaxIterations);
        int burnIn = Math.Clamp(FractalParameters.LogisticBurnIn, 0, maxIt - 1);
        int plot = Math.Max(1, maxIt - burnIn);
        double seed = Math.Clamp(FractalParameters.LogisticSeed, 1e-6, 1.0 - 1e-6);

        // Per-column iteration. Each thread accumulates into a private
        // column slice of _hits (one column = `height` cells) so there's no
        // cross-thread write contention — each pixel is only written by
        // the column owner.
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, width, po, px =>
        {
            if (ct.IsCancellationRequested) return;

            double r = centerX + (px - width * 0.5) * scale;
            if (r <= 0.0 || r > 4.0) return;

            double x = seed;
            for (int i = 0; i < burnIn; i++)
                x = r * x * (1.0 - x);

            double invScale = 1.0 / scale;
            double halfH = height * 0.5;
            for (int i = 0; i < plot; i++)
            {
                x = r * x * (1.0 - x);
                // x → world y → pixel y.  Frame y matches the escape-time
                // convention: pixel y increases downward in the buffer but
                // world y likewise (Y-flip handled by the render host, not
                // the calculator).
                double py = (x - centerY) * invScale + halfH;
                int ipy = (int)py;
                if ((uint)ipy < (uint)height)
                    _hits[ipy * width + px]++;
            }
        });

        RenderColorMap();
    }

    private void RenderColorMap()
    {
        int n = _hits.Length;
        uint maxHits = 0;
        for (int i = 0; i < n; i++)
            if (_hits[i] > maxHits) maxHits = _hits[i];

        if (maxHits == 0)
        {
            Array.Clear(ColorBuffer);
            return;
        }

        double inv = 1.0 / Math.Log(maxHits + 1.0);
        int iters = MaxIterations;
        var cm = ColorMap;
        cm.MaxIterations = iters;
        uint inSetColor = cm.InSetColor;
        byte bgR = (byte)((inSetColor >> 16) & 0xFF);
        byte bgG = (byte)((inSetColor >>  8) & 0xFF);
        byte bgB = (byte)(inSetColor & 0xFF);

        // Match BuddhaFamilyCalculator.RenderColorMap shape so the theme
        // gradient maps the same way users expect from Buddhabrot.
        for (int i = 0; i < n; i++)
        {
            uint h = _hits[i];
            if (h == 0)
            {
                ColorBuffer[i] = inSetColor;
                continue;
            }
            double norm = Math.Log(h + 1.0) * inv;
            float smooth = (float)((1.0 - norm) * iters);
            uint argb = unchecked((uint)cm.Map(smooth, 0f, iters));
            byte fR = (byte)((argb >> 16) & 0xFF);
            byte fG = (byte)((argb >>  8) & 0xFF);
            byte fB = (byte)(argb & 0xFF);

            double a = norm * norm;
            double oneMa = 1.0 - a;
            byte R = (byte)(fR * a + bgR * oneMa);
            byte G = (byte)(fG * a + bgG * oneMa);
            byte B = (byte)(fB * a + bgB * oneMa);
            ColorBuffer[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
        }
    }
}
