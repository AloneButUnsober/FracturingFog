// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/WatermarkLayout.cs
//
// The single source of watermark *geometry*. WatermarkResolver answers "what
// text / colour / edge?"; this answers "what pixels, where?". Every draw
// surface (live Skia overlay, image save, poster/wallpaper, batch video,
// server frames) computes its plan here and then does nothing but blit it.
//
// Before this existed each surface hand-rolled its own placement math and
// they drifted: the export paths positioned the top-line and the sub-line
// independently off the bottom edge instead of stacking them, so the sub-line
// slid under the top-line as font size grew, and they ignored Placement /
// Justify / HighlightColor / BackgroundColor outright.
//
// Pure / stateless. No SkiaSharp, System.Drawing or Avalonia dependency —
// callers inject text measurement via WatermarkMeasure and translate the
// returned plan into their own colour + font types.

using System;
using FracturingFog.Models;

namespace FracturingFog.Imaging
{
    /// <summary>Measure <paramref name="text"/> at <paramref name="fontPx"/>.
    /// Width is the advance width; Height is the full top-to-top line height
    /// (ascent + descent), because the plan's Y coordinates name the top-left
    /// of the glyph box, not the baseline.</summary>
    public delegate (float Width, float Height) WatermarkMeasure(string text, float fontPx);

    /// <summary>One laid-out text line. X/Y name the upper-left of the glyph
    /// box — draw APIs that position at the baseline shift by -ascent.</summary>
    public sealed class WatermarkLine
    {
        public string Text { get; init; } = string.Empty;
        public float FontPx { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
    }

    /// <summary>Fully-resolved draw plan. Everything a surface needs; no
    /// surface-side geometry left to get wrong.</summary>
    public sealed class WatermarkPlan
    {
        /// <summary>Null when the resolved watermark has no top-line text.</summary>
        public WatermarkLine? Top { get; init; }

        /// <summary>Null when the resolved watermark has no sub-line text.</summary>
        public WatermarkLine? Sub { get; init; }

        /// <summary>Backdrop rect, already padded. Null when the user did not
        /// configure a BackgroundColor.</summary>
        public (int X, int Y, int W, int H)? Background { get; init; }
        public RgbaDef? BackgroundColor { get; init; }

        /// <summary>Top-line glyph fill, alpha included.</summary>
        public RgbaDef TopFill { get; init; } = new RgbaDef(255, 255, 255, 255);

        /// <summary>Sub-line glyph fill — same hue as TopFill, lower alpha so
        /// the mandatory program line reads as secondary.</summary>
        public RgbaDef SubFill { get; init; } = new RgbaDef(255, 255, 255, 255);

        /// <summary>Drop-shadow / halo colour. The user's HighlightColor when
        /// set, else the caller's contrast-derived default.</summary>
        public RgbaDef Halo { get; init; } = new RgbaDef(0, 0, 0, 120);

        /// <summary>Shadow draw offset in px (both axes), scaled with the image.</summary>
        public int ShadowOffset { get; init; } = 1;
    }

    public static class WatermarkLayout
    {
        // Base metrics = the live on-screen overlay's numbers. Those are the
        // ones the user signed off on, so they are the definition of correct
        // and every other surface scales off them rather than inventing its own.
        public const float BaseMainFontPx = 14f;
        public const float BaseSubFontPx = 9f;
        private const int BaseEdgePad = 6;
        private const int BaseBgPad = 4;
        private const int BaseShadow = 1;

        /// <summary>Image height the base metrics are authored against. A
        /// 1080-tall export renders the watermark at exactly the size a
        /// 1080-tall window shows.</summary>
        public const int ReferenceHeight = 1080;

        /// <summary>Export scale factor: keeps the watermark's *relative*
        /// footprint constant, so a 4K wallpaper's watermark occupies the same
        /// fraction of the frame as it does on screen. Never shrinks below the
        /// base metrics — a small export gets legible text, not sub-pixel mush.
        /// Live-overlay callers pass scale 1.0 instead of calling this, so the
        /// on-screen appearance is unchanged on high-DPI monitors.</summary>
        public static float ScaleForImage(int imgH)
            => Math.Max(1f, imgH / (float)ReferenceHeight);

        /// <summary>Lay out <paramref name="wm"/> over an
        /// <paramref name="imgW"/>×<paramref name="imgH"/> image. Returns null
        /// when there is nothing to draw.</summary>
        /// <param name="scale">1.0 for the live overlay; ScaleForImage(imgH)
        /// for exports.</param>
        /// <param name="defaultHalo">Shadow colour used when the watermark
        /// carries no HighlightColor. Callers derive it from their own contrast
        /// pass.</param>
        public static WatermarkPlan? Compute(
            WatermarkRender wm,
            int imgW, int imgH,
            float scale,
            RgbaDef defaultHalo,
            WatermarkMeasure measure)
        {
            if (wm == null || measure == null) return null;
            if (imgW <= 1 || imgH <= 1) return null;

            bool hasTop = !string.IsNullOrEmpty(wm.TopText);
            bool hasSub = !string.IsNullOrEmpty(wm.SubText);
            if (!hasTop && !hasSub) return null;

            if (scale <= 0f || float.IsNaN(scale)) scale = 1f;

            float mainPx = BaseMainFontPx * scale;
            float subPx = Math.Max(1f, BaseSubFontPx * scale);
            int edgePad = Math.Max(1, (int)MathF.Round(BaseEdgePad * scale));
            int bgPad = Math.Max(1, (int)MathF.Round(BaseBgPad * scale));
            int shadow = Math.Max(1, (int)MathF.Round(BaseShadow * scale));

            var (topWf, topHf) = hasTop ? measure(wm.TopText, mainPx) : (0f, 0f);
            var (subWf, subHf) = hasSub ? measure(wm.SubText, subPx) : (0f, 0f);

            int topW = (int)Math.Ceiling(topWf);
            int topH = (int)Math.Ceiling(topHf);
            int subW = (int)Math.Ceiling(subWf);
            int subH = (int)Math.Ceiling(subHf);

            var (bx, by, bw, bh) = WatermarkResolver.ComputeBlockBounds(
                wm, imgW, imgH, topW, topH, subW, subH, edgePad);

            // The stacking rule the export paths were missing: the sub-line's
            // top edge is the top-line's *bottom* edge. Deriving it from the
            // measured top-line height instead of the image edge is what stops
            // the two lines colliding at large font sizes.
            var top = hasTop
                ? new WatermarkLine
                {
                    Text = wm.TopText,
                    FontPx = mainPx,
                    X = WatermarkResolver.AlignLineX(bx, bw, topW, wm.Justify),
                    Y = by,
                    Width = topW,
                    Height = topH,
                }
                : null;

            var sub = hasSub
                ? new WatermarkLine
                {
                    Text = wm.SubText,
                    FontPx = subPx,
                    X = WatermarkResolver.AlignLineX(bx, bw, subW, wm.Justify),
                    Y = by + topH,
                    Width = subW,
                    Height = subH,
                }
                : null;

            (int X, int Y, int W, int H)? bg = wm.BackgroundColor != null
                ? (bx - bgPad, by - bgPad, bw + bgPad * 2, bh + bgPad * 2)
                : null;

            var halo = wm.HighlightColor ?? defaultHalo ?? new RgbaDef(0, 0, 0, 120);

            byte topA = wm.IsCustom ? (byte)255 : (byte)220;
            byte subA = wm.IsCustom ? (byte)230 : (byte)180;

            return new WatermarkPlan
            {
                Top = top,
                Sub = sub,
                Background = bg,
                BackgroundColor = wm.BackgroundColor,
                TopFill = new RgbaDef(wm.TextColor.R, wm.TextColor.G, wm.TextColor.B, topA),
                SubFill = new RgbaDef(wm.TextColor.R, wm.TextColor.G, wm.TextColor.B, subA),
                Halo = halo,
                ShadowOffset = shadow,
            };
        }

        /// <summary>Contrast-derived default halo: dark ink wants a light
        /// shadow and vice-versa. Shared so surfaces without their own
        /// pre-sampled luma agree with the ones that have it.</summary>
        public static RgbaDef HaloForInk(byte r, byte g, byte b)
        {
            float lum = (r * 0.299f + g * 0.587f + b * 0.114f) / 255f;
            return lum < 0.5f
                ? new RgbaDef(255, 255, 255, 120)
                : new RgbaDef(0, 0, 0, 120);
        }
    }
}
