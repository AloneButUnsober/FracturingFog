// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Interior2DBackgroundCompositor.cs
//
// F10.5 / issue #96 interior-alpha compositor — the SINGLE source of truth for
// compositing translucent 2D pixels over the theme's chosen Interior2DBackground.
//
// The on-screen swap-chain present ignores the alpha channel (always opaque), so
// authored translucency — interior alpha (InSetColor.A / the global InteriorAlpha
// knob) AND per-colour-stop exterior alpha — only becomes visible when we
// composite it in software here. Both the live path
// (FractalRenderHost.UploadProcessedBuffer) and every offscreen export path
// (PosterRenderer, and thus the poster / wallpaper / "Save Image" buttons) call
// this so the exported PNG matches the window pixel-for-pixel.
//
// Interior2DBackgroundMode.Transparent is a deliberate no-op: it keeps straight
// alpha so a user who wants a transparent-interior PNG gets one. Every other mode
// composites the translucent pixels opaque over the backdrop.

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using FracturingFog.Models;

namespace FracturingFog.Rendering
{
    /// <summary>Composites translucent 2D pixels over the interior-alpha backdrop
    /// (F10.5 / #96). Shared by the live upload path, the offscreen export path
    /// (PosterRenderer) and the headless batch video / slideshow paths so none of
    /// them can drift. Public so the batch renderer — which lives in the WinExe
    /// assembly, not Engine — can call the same choke point.</summary>
    public static class Interior2DBackgroundCompositor
    {
        /// <summary>
        /// Composite every translucent pixel (alpha &lt; 255, read from
        /// <paramref name="coverage"/>) over the backdrop selected by
        /// <paramref name="p"/>.Interior2DBackground, writing the opaque result into
        /// <paramref name="rgb"/>. Opaque pixels are left untouched. No-op for
        /// Transparent mode, when nothing is translucent and no explicit backdrop is
        /// set, or when <paramref name="srcAlreadyProcessed"/> is true.
        /// </summary>
        /// <param name="rgb">Buffer whose RGB is composited and written; composited
        /// pixels get alpha 0xFF. For the export path this is the same array as
        /// <paramref name="coverage"/> (the b/c/gamma pass preserves the coverage
        /// byte). For the live path this is the post-FX <c>dst</c> (whose alpha may
        /// have been force-set to 0xFF), so the true coverage is supplied
        /// separately.</param>
        /// <param name="coverage">Buffer supplying the authored coverage (alpha)
        /// byte per pixel.</param>
        /// <param name="inSetArgb">The active theme's <c>InSetColor</c>, used only to
        /// decide whether the interior is translucent (gate parity with the live
        /// path). Pass 0xFF000000 when unknown.</param>
        /// <param name="alphaPreview">Theme-editor see-through aid: force the
        /// Checkerboard backdrop and composite regardless of the saved mode.</param>
        /// <param name="srcAlreadyProcessed">Skip entirely (e.g. a video-record frame
        /// whose colour is already baked).</param>
        public static void Composite(
            uint[] rgb, uint[] coverage, int w, int h,
            FractalParameters? p, uint inSetArgb,
            bool alphaPreview, bool srcAlreadyProcessed)
        {
            if (srcAlreadyProcessed || p == null) return;
            int n = w * h;
            if (n <= 0 || rgb.Length < n || coverage.Length < n) return;

            var bgMode = p.Interior2DBackground;
            bool themeInteriorTranslucent = ((inSetArgb >> 24) & 0xFF) < 255;
            bool interiorTranslucent = p.InteriorAlpha < 255 || themeInteriorTranslucent;
            bool explicitBackdrop =
                bgMode == Interior2DBackgroundMode.SolidColor
                || bgMode == Interior2DBackgroundMode.Gradient
                || bgMode == Interior2DBackgroundMode.Image;
            bool want = alphaPreview
                || (bgMode != Interior2DBackgroundMode.Transparent
                    && (interiorTranslucent || explicitBackdrop));
            if (!want) return;

            // AlphaPreview always wins with the checkerboard aid.
            var mode = alphaPreview ? Interior2DBackgroundMode.Checkerboard : bgMode;

            uint bgTop = p.Interior2DBgTop, bgBot = p.Interior2DBgBottom;
            int topR = (int)((bgTop >> 16) & 0xFF), topG = (int)((bgTop >> 8) & 0xFF), topB = (int)(bgTop & 0xFF);
            int botR = (int)((bgBot >> 16) & 0xFF), botG = (int)((bgBot >> 8) & 0xFF), botB = (int)(bgBot & 0xFF);
            int denom = h > 1 ? h - 1 : 1;

            // Image backdrop: decode (cached) up front. On any failure fall back to a
            // flat fill (bgTop) so a bad path never blanks the frame.
            uint[]? imgPx = null;
            int imgW = 0, imgH = 0;
            if (mode == Interior2DBackgroundMode.Image)
            {
                if (BackgroundImageCache.TryGet(p.Interior2DBgImagePath, out var px, out imgW, out imgH))
                    imgPx = px;
                else
                    mode = Interior2DBackgroundMode.SolidColor;
            }

            int chunk = h / (Environment.ProcessorCount * 4);
            if (chunk < 1) chunk = 1;
            Parallel.ForEach(Partitioner.Create(0, h, chunk), range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    int rowBase = y * w;
                    // Per-row backdrop base for Solid / Gradient / Image
                    // (checker varies per pixel, computed inline below).
                    int rowBgR = 0, rowBgG = 0, rowBgB = 0;
                    int imgRowBase = 0;
                    if (mode == Interior2DBackgroundMode.SolidColor)
                    {
                        rowBgR = topR; rowBgG = topG; rowBgB = topB;
                    }
                    else if (mode == Interior2DBackgroundMode.Gradient)
                    {
                        // t = 0 at top row (bgTop), 1 at bottom row (bgBot).
                        int t = (y * 256) / denom;   // 0..256 fixed-point
                        rowBgR = (topR * (256 - t) + botR * t) >> 8;
                        rowBgG = (topG * (256 - t) + botG * t) >> 8;
                        rowBgB = (topB * (256 - t) + botB * t) >> 8;
                    }
                    else if (mode == Interior2DBackgroundMode.Image)
                    {
                        // Nearest-neighbour stretch to fill the viewport.
                        int iy = imgH > 0 ? (int)((long)y * imgH / h) : 0;
                        if (iy >= imgH) iy = imgH - 1;
                        imgRowBase = iy * imgW;
                    }
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowBase + x;
                        int a = (int)((coverage[i] >> 24) & 0xFF);
                        if (a >= 255) continue;   // opaque — rgb already right
                        uint pc = rgb[i];
                        int R = (int)((pc >> 16) & 0xFF);
                        int G = (int)((pc >> 8) & 0xFF);
                        int B = (int)(pc & 0xFF);
                        int bgR, bgG, bgB;
                        if (mode == Interior2DBackgroundMode.Checkerboard)
                        {
                            int bg = ((((x >> 3) + (y >> 3)) & 1) == 0) ? 200 : 120;
                            bgR = bgG = bgB = bg;
                        }
                        else if (mode == Interior2DBackgroundMode.Image)
                        {
                            int ix = imgW > 0 ? (int)((long)x * imgW / w) : 0;
                            if (ix >= imgW) ix = imgW - 1;
                            uint ipx = imgPx![imgRowBase + ix];
                            bgR = (int)((ipx >> 16) & 0xFF);
                            bgG = (int)((ipx >> 8) & 0xFF);
                            bgB = (int)(ipx & 0xFF);
                        }
                        else
                        {
                            bgR = rowBgR; bgG = rowBgG; bgB = rowBgB;
                        }
                        int inv = 255 - a;
                        R = (R * a + bgR * inv) / 255;
                        G = (G * a + bgG * inv) / 255;
                        B = (B * a + bgB * inv) / 255;
                        rgb[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | (uint)B;
                    }
                }
            });
        }
    }
}
