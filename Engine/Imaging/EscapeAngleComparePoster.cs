// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/EscapeAngleComparePoster.cs
//
// Renderer B (#626 / #629) — the escape-angle compare poster preset.
//
// Renders ONE region three ways side-by-side, per scoping doc §3.2:
//   (i)   classic iteration-count coloring   (Viridis smooth-iteration)
//   (ii)  pure escape-angle coloring         (EscapeAngleDemoMap)
//   (iii) hue(angle) × brightness(iter)      (EscapeAngleIterShadedMap)
//
// No new render path: each panel is a plain PosterRenderer.RenderToPixels of the
// same region with a different IColorMap, then the three panels are tiled
// horizontally into one wide buffer with a neutral gutter. Text labels are left
// to the caller/UI (Panel.Label) so the composite stays cross-platform and
// deterministic for tests — the panels always render left→right in list order.

using System;
using System.Collections.Generic;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Imaging
{
    /// <summary>The escape-angle 3-way compare poster preset (Renderer B, #629).
    /// Tiles one region rendered under several colour maps into a single wide
    /// image so the escape-angle coloring can be read against the classic
    /// iteration-count view and the combined angle×iter view.</summary>
    public static class EscapeAngleComparePoster
    {
        /// <summary>One column of the compare poster: a display label (for the
        /// caller / UI to caption) and the colour map that renders it.</summary>
        public sealed record Panel(string Label, IColorMap ColorMap);

        /// <summary>The canonical three panels, left→right:
        /// iteration-count, pure escape-angle, angle×iter.</summary>
        public static IReadOnlyList<Panel> DefaultPanels() => new[]
        {
            new Panel("Iteration count", new ViridisColorMap()),
            new Panel("Escape angle", new EscapeAngleDemoMap()),
            new Panel("Angle x iter", new EscapeAngleIterShadedMap()),
        };

        /// <summary>Render the compare poster to a pixel buffer. Each panel is a
        /// <paramref name="panelWidth"/>×<paramref name="panelHeight"/> render of
        /// the same region; the composite is
        /// <c>n·panelWidth + (n-1)·gap</c> wide by <c>panelHeight</c> tall, with
        /// the gutters filled by <paramref name="gapColor"/> (ARGB).</summary>
        public static uint[] RenderComposite(
            double centerX, double centerY, double zoom, int maxIterations,
            FractalType fractalType, FractalParameters fractalParameters,
            QualityPreset quality,
            int panelWidth, int panelHeight,
            CancellationToken token,
            out int width, out int height,
            int gap = 8, uint gapColor = 0xFF202020u,
            IReadOnlyList<Panel>? panels = null)
        {
            if (panelWidth <= 0 || panelHeight <= 0)
                throw new ArgumentException("Panel dimensions must be positive.");
            panels ??= DefaultPanels();
            if (panels.Count == 0)
                throw new ArgumentException("At least one panel is required.", nameof(panels));
            if (gap < 0) gap = 0;
            fractalParameters ??= new FractalParameters();
            quality ??= QualityPreset.Standard;

            int n = panels.Count;
            width = n * panelWidth + (n - 1) * gap;
            height = panelHeight;

            var composite = new uint[width * height];
            if (gap > 0 && gapColor != 0)
            {
                // Pre-fill so the gutters carry the gap colour; panels overwrite
                // their columns below.
                for (int i = 0; i < composite.Length; i++) composite[i] = gapColor;
            }

            for (int p = 0; p < n; p++)
            {
                token.ThrowIfCancellationRequested();
                var req = new PosterRequest
                {
                    CenterX = centerX,
                    CenterY = centerY,
                    Zoom = zoom,
                    MaxIterations = maxIterations,
                    FractalType = fractalType,
                    FractalParameters = fractalParameters,
                    Quality = quality,
                    ColorMap = panels[p].ColorMap,
                    Width = panelWidth,
                    Height = panelHeight,
                };

                uint[] panel = PosterRenderer.RenderToPixels(req, token, out int pw, out int ph);
                int xOffset = p * (panelWidth + gap);
                BlitPanel(panel, pw, ph, composite, width, height, xOffset);
            }

            return composite;
        }

        /// <summary>Render the compare poster and write it to
        /// <paramref name="path"/> via <see cref="ImageExport"/> (no watermark;
        /// the composite is the deliverable).</summary>
        public static void RenderToFile(
            double centerX, double centerY, double zoom, int maxIterations,
            FractalType fractalType, FractalParameters fractalParameters,
            QualityPreset quality,
            int panelWidth, int panelHeight,
            string path, ImageFileFormat format,
            CancellationToken token,
            int gap = 8, uint gapColor = 0xFF202020u,
            IReadOnlyList<Panel>? panels = null)
        {
            var pixels = RenderComposite(
                centerX, centerY, zoom, maxIterations, fractalType, fractalParameters,
                quality, panelWidth, panelHeight, token, out int w, out int h,
                gap, gapColor, panels);

            ImageExport.SavePixelsToFile(
                pixels, w, h, path, format,
                (WatermarkRender?)null, poster: true);
        }

        // Copy one panel's rows into the composite at the given x offset. Panels
        // are the composite's full height by construction, so a straight per-row
        // copy suffices; the guard clamps defensively against a short render.
        private static void BlitPanel(
            uint[] panel, int pw, int ph,
            uint[] composite, int cw, int ch, int xOffset)
        {
            int rows = Math.Min(ph, ch);
            int cols = Math.Min(pw, cw - xOffset);
            if (cols <= 0) return;
            for (int y = 0; y < rows; y++)
            {
                Array.Copy(panel, y * pw, composite, y * cw + xOffset, cols);
            }
        }
    }
}
