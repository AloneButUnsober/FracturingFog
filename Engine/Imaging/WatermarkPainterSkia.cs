// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/WatermarkPainterSkia.cs
//
// The one Skia watermark draw. Every Skia surface — the live overlay
// compositor that bakes into the BGRA frame buffer, the image/poster/wallpaper
// save path, batch video frames, server frames — paints through here, so
// placement, stacking, justification, backdrop and halo cannot drift apart
// again. Geometry comes from WatermarkLayout (shared with the GDI+ path);
// this file only turns a WatermarkPlan into SKCanvas calls.

using System;
using FracturingFog.Models;
using SkiaSharp;

namespace FracturingFog.Imaging
{
    public static class WatermarkPainterSkia
    {
        // Arial matches the live overlay. Skia's font manager maps it to
        // Liberation Sans / Helvetica where it isn't installed, so exports
        // stay consistent across platforms.
        private const string FontFamily = "Arial";

        /// <summary>Measure callback bound to the Skia fonts this painter
        /// draws with — pass to <see cref="WatermarkLayout.Compute"/> so the
        /// plan's metrics match what actually gets rendered.</summary>
        public static WatermarkMeasure Measure { get; } = (text, fontPx) =>
        {
            using var font = MakeFont(fontPx, bold: true);
            var m = font.Metrics;
            return (font.MeasureText(text ?? string.Empty), m.Descent - m.Ascent);
        };

        private static SKFont MakeFont(float sizePx, bool bold)
        {
            var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
            var tf = SKTypeface.FromFamilyName(FontFamily, style) ?? SKTypeface.Default;
            return new SKFont(tf, sizePx) { Edging = SKFontEdging.SubpixelAntialias };
        }

        private static SKColor ToSk(RgbaDef c) => new SKColor(c.R, c.G, c.B, c.A);

        /// <summary>Resolve, lay out and paint in one call — the entry point
        /// for save/export surfaces that hold a <see cref="WatermarkRender"/>
        /// and an image size and want the same result the screen shows.</summary>
        /// <param name="scale">Omit (or pass null) to scale with the image via
        /// <see cref="WatermarkLayout.ScaleForImage"/>. The live overlay passes
        /// 1.0 to keep on-screen metrics fixed.</param>
        public static void Paint(
            SKCanvas canvas, WatermarkRender wm, int imgW, int imgH,
            RgbaDef? defaultHalo = null, float? scale = null)
        {
            if (canvas == null || wm == null) return;

            var halo = defaultHalo
                ?? WatermarkLayout.HaloForInk(wm.TextColor.R, wm.TextColor.G, wm.TextColor.B);

            var plan = WatermarkLayout.Compute(
                wm, imgW, imgH,
                scale ?? WatermarkLayout.ScaleForImage(imgH),
                halo,
                Measure);

            if (plan != null) PaintPlan(canvas, plan);
        }

        /// <summary>Paint a pre-computed plan. Use when the caller already
        /// needed the plan's bounds (e.g. to size a scratch surface).</summary>
        public static void PaintPlan(SKCanvas canvas, WatermarkPlan plan)
        {
            if (canvas == null || plan == null) return;

            if (plan.Background is { } bg && plan.BackgroundColor != null)
            {
                using var bgPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    IsAntialias = false,
                    Color = ToSk(plan.BackgroundColor),
                };
                canvas.DrawRect(bg.X, bg.Y, bg.W, bg.H, bgPaint);
            }

            using var haloPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill, IsAntialias = true, Color = ToSk(plan.Halo),
            };

            DrawLine(canvas, plan.Top, plan.TopFill, haloPaint, plan.ShadowOffset, bold: true);
            DrawLine(canvas, plan.Sub, plan.SubFill, haloPaint, plan.ShadowOffset, bold: false);
        }

        private static void DrawLine(
            SKCanvas canvas, WatermarkLine? line, RgbaDef fill,
            SKPaint haloPaint, int shadow, bool bold)
        {
            if (line == null || string.IsNullOrEmpty(line.Text)) return;

            using var font = MakeFont(line.FontPx, bold);
            using var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill, IsAntialias = true, Color = ToSk(fill),
            };

            // Plan Y is the top of the glyph box; Skia draws from the baseline.
            float baseline = line.Y - font.Metrics.Ascent;
            canvas.DrawText(line.Text, line.X + shadow, baseline + shadow, font, haloPaint);
            canvas.DrawText(line.Text, line.X, baseline, font, fillPaint);
        }

        /// <summary>Paint straight into a tightly-packed BGRA buffer — the
        /// shape batch/video frame paths need. Buffer is modified in place.</summary>
        public static void PaintOntoBgra(
            uint[] bgra, int width, int height, WatermarkRender wm,
            RgbaDef? defaultHalo = null, float? scale = null)
        {
            if (bgra == null || bgra.Length < (long)width * height) return;
            if (width <= 1 || height <= 1) return;

            var handle = System.Runtime.InteropServices.GCHandle.Alloc(
                bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var bmp = new SKBitmap();
                bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
                using var canvas = new SKCanvas(bmp);
                Paint(canvas, wm, width, height, defaultHalo, scale);
                canvas.Flush();
            }
            finally { handle.Free(); }
        }
    }
}
