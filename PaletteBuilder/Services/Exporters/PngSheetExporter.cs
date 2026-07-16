// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Services/Exporters/PngSheetExporter.cs
//
// Render the palette as a single PNG image: 1-column strip of swatch tiles
// at a fixed pixel width; each tile carries its #HEX + RGB label rendered
// in a luma-aware plate so it's legible on any swatch colour.
//
// Phase X.1 / Slice 1.1 — was System.Drawing.Common (Bitmap + Graphics +
// SolidBrush). Rewritten on SkiaSharp so the exporter builds cross-platform
// alongside PdfPaletteExporter and the rest of PaletteBuilder.Lib once the
// TFM flips (Slice 1.2).

using System.Collections.Generic;
using System.IO;
using FracturingFog.Imaging;
using SkiaSharp;

namespace PaletteBuilder.Services.Exporters
{
    public sealed class PngSheetExporter : IPaletteExporter
    {
        public string Id => "png";
        public string DisplayName => "PNG sheet";
        public string Extension => "png";

        private const int Width = 480;
        private const int TileHeight = 64;

        public void Export(string path,
                           IReadOnlyList<(byte R, byte G, byte B)> swatches,
                           IReadOnlyList<PaletteStop>? stops = null,
                           PaletteExportContext? context = null)
        {
            int n = swatches.Count == 0 ? 1 : swatches.Count;
            int h = n * TileHeight;
            var info = new SKImageInfo(Width, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            using var typeface = SKTypeface.FromFamilyName(
                "Consolas",
                SKFontStyleWeight.Bold,
                SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright);
            // SKTypeface.FromFamilyName returns the platform default if the
            // requested family is unavailable (Linux/macOS hosts without
            // Consolas) — the strip still renders, just in a fallback font.

            using var font = new SKFont(typeface, 14f) { Subpixel = true };
            using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var textPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

            for (int i = 0; i < swatches.Count; i++)
            {
                var c = swatches[i];
                var rect = new SKRect(0, i * TileHeight, Width, (i + 1) * TileHeight);
                fillPaint.Color = new SKColor(c.R, c.G, c.B);
                canvas.DrawRect(rect, fillPaint);

                string text = $"#{c.R:X2}{c.G:X2}{c.B:X2}   RGB({c.R}, {c.G}, {c.B})";
                double luma = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                textPaint.Color = luma < 0.5 ? SKColors.White : SKColors.Black;

                var bounds = new SKRect();
                font.MeasureText(text, out bounds);
                float textX = (Width - bounds.Width) / 2f - bounds.Left;
                var metrics = font.Metrics;
                float baseline = i * TileHeight + (TileHeight - metrics.Descent + metrics.Ascent) / 2f - metrics.Ascent;
                canvas.DrawText(text, textX, baseline, SKTextAlign.Left, font, textPaint);
            }

            canvas.Flush();
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 95);
            using var fs = File.Create(path);
            data.SaveTo(fs);
        }
    }
}
