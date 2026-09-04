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
//
// Two blend paths share one backdrop resolver (TryResolveBackdrop):
//   * Composite       — the 8-bit sRGB over-blend (the tested oracle; unchanged).
//   * CompositeLinear  — the S2 (#396) FULL-FLOAT 2D composite: the identical
//     backdrop, blended in LINEAR light inside a LinearFloatImage so a view
//     transform tonemaps the fractal AND the backdrop as one linear image (the
//     8-bit path injects the backdrop AFTER the tonemap, so it pops untonemapped
//     and the alpha edge is a gamma-space blend). Both paths pick the SAME
//     backdrop pixel; only the blend space differs.

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using FracturingFog.Imaging;
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
        /// <summary>Resolved backdrop for one frame: the effective mode + the
        /// top/bottom gradient colours (8-bit channels) + a decoded image plane.
        /// Both blend paths select the same backdrop pixel from this.</summary>
        private struct Backdrop
        {
            public Interior2DBackgroundMode Mode;
            public int TopR, TopG, TopB, BotR, BotG, BotB;
            public uint[]? ImgPx;
            public int ImgW, ImgH;
        }

        /// <summary>Decide whether to composite and, if so, resolve the backdrop.
        /// The single gate + mode/backdrop selection both <see cref="Composite"/>
        /// and <see cref="CompositeLinear"/> route through, so the two blend spaces
        /// can never disagree on WHICH pixels composite over WHAT.</summary>
        private static bool TryResolveBackdrop(
            FractalParameters p, uint inSetArgb, bool alphaPreview, out Backdrop bd)
        {
            bd = default;
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
            if (!want) return false;

            // AlphaPreview always wins with the checkerboard aid.
            var mode = alphaPreview ? Interior2DBackgroundMode.Checkerboard : bgMode;

            uint bgTop = p.Interior2DBgTop, bgBot = p.Interior2DBgBottom;
            bd.TopR = (int)((bgTop >> 16) & 0xFF); bd.TopG = (int)((bgTop >> 8) & 0xFF); bd.TopB = (int)(bgTop & 0xFF);
            bd.BotR = (int)((bgBot >> 16) & 0xFF); bd.BotG = (int)((bgBot >> 8) & 0xFF); bd.BotB = (int)(bgBot & 0xFF);

            // Image backdrop: decode (cached) up front. On any failure fall back to a
            // flat fill (bgTop) so a bad path never blanks the frame.
            if (mode == Interior2DBackgroundMode.Image)
            {
                if (BackgroundImageCache.TryGet(p.Interior2DBgImagePath, out var px, out int iw, out int ih))
                {
                    bd.ImgPx = px; bd.ImgW = iw; bd.ImgH = ih;
                }
                else
                    mode = Interior2DBackgroundMode.SolidColor;
            }

            bd.Mode = mode;
            return true;
        }

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
            if (!TryResolveBackdrop(p, inSetArgb, alphaPreview, out var bd)) return;

            var mode = bd.Mode;
            int topR = bd.TopR, topG = bd.TopG, topB = bd.TopB;
            int botR = bd.BotR, botG = bd.BotG, botB = bd.BotB;
            uint[]? imgPx = bd.ImgPx;
            int imgW = bd.ImgW, imgH = bd.ImgH;
            int denom = h > 1 ? h - 1 : 1;

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

        /// <summary>
        /// S2 (#396) FULL-FLOAT 2D composite — the linear-light twin of
        /// <see cref="Composite"/>. Blends the SAME backdrop (same gate, same mode,
        /// same per-pixel backdrop pixel) over the translucent fractal pixels of
        /// <paramref name="img"/> IN LINEAR LIGHT, in place, and marks composited
        /// pixels opaque (alpha 1). The caller then applies the view transform on the
        /// result, so the fractal and the backdrop are tonemapped together as one
        /// linear image — the fix for the 8-bit path injecting the backdrop AFTER the
        /// tonemap (it popped untonemapped) and blending the alpha edge in gamma space.
        /// </summary>
        /// <param name="img">The linear-light image (built from the graded 8-bit
        /// colour via <see cref="LinearFloatImage.FromBgra(uint[],uint[],int,int)"/>);
        /// composited in place.</param>
        /// <param name="coverage">Buffer supplying the authored coverage (alpha) byte
        /// per pixel — the same coverage source <see cref="Composite"/> reads.</param>
        /// <returns><c>true</c> if compositing ran (so the caller skips the 8-bit
        /// <see cref="Composite"/>); <c>false</c> when there is nothing to composite,
        /// leaving <paramref name="img"/> untouched and byte-identical to the 8-bit
        /// path (which is also a no-op in that case).</returns>
        public static bool CompositeLinear(
            LinearFloatImage img, uint[] coverage,
            FractalParameters? p, uint inSetArgb, bool alphaPreview)
        {
            if (img == null) throw new ArgumentNullException(nameof(img));
            if (p == null) return false;
            int w = img.Width, h = img.Height, n = w * h;
            if (n <= 0 || coverage.Length < n) return false;
            if (!TryResolveBackdrop(p, inSetArgb, alphaPreview, out var bd)) return false;

            var mode = bd.Mode;
            int topR = bd.TopR, topG = bd.TopG, topB = bd.TopB;
            int botR = bd.BotR, botG = bd.BotG, botB = bd.BotB;
            uint[]? imgPx = bd.ImgPx;
            int imgW = bd.ImgW, imgH = bd.ImgH;
            int denom = h > 1 ? h - 1 : 1;
            float[] rgb = img.Rgb;
            float[] alpha = img.Alpha;

            int chunk = h / (Environment.ProcessorCount * 4);
            if (chunk < 1) chunk = 1;
            Parallel.ForEach(Partitioner.Create(0, h, chunk), range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    int rowBase = y * w;
                    int rowBgR = 0, rowBgG = 0, rowBgB = 0;
                    int imgRowBase = 0;
                    if (mode == Interior2DBackgroundMode.SolidColor)
                    {
                        rowBgR = topR; rowBgG = topG; rowBgB = topB;
                    }
                    else if (mode == Interior2DBackgroundMode.Gradient)
                    {
                        int t = (y * 256) / denom;
                        rowBgR = (topR * (256 - t) + botR * t) >> 8;
                        rowBgG = (topG * (256 - t) + botG * t) >> 8;
                        rowBgB = (topB * (256 - t) + botB * t) >> 8;
                    }
                    else if (mode == Interior2DBackgroundMode.Image)
                    {
                        int iy = imgH > 0 ? (int)((long)y * imgH / h) : 0;
                        if (iy >= imgH) iy = imgH - 1;
                        imgRowBase = iy * imgW;
                    }
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowBase + x;
                        int a = (int)((coverage[i] >> 24) & 0xFF);
                        if (a >= 255) continue;   // opaque — leave the linear pixel as-is
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
                        // The backdrop is authored in sRGB display space — decode it to
                        // linear so the over-blend happens in the same linear light the
                        // fractal RGB already lives in. Coverage is a straight alpha.
                        float aF = a / 255f;
                        float inv = 1f - aF;
                        float bgLr = ViewTransformOps.SrgbToLinear(bgR / 255f);
                        float bgLg = ViewTransformOps.SrgbToLinear(bgG / 255f);
                        float bgLb = ViewTransformOps.SrgbToLinear(bgB / 255f);
                        int j = i * 3;
                        rgb[j] = rgb[j] * aF + bgLr * inv;
                        rgb[j + 1] = rgb[j + 1] * aF + bgLg * inv;
                        rgb[j + 2] = rgb[j + 2] * aF + bgLb * inv;
                        alpha[i] = 1f;
                    }
                }
            });
            return true;
        }
    }
}
