// Services/Exporters/PngSheetExporter.cs
//
// Render the palette as a single PNG image: 1-column strip of swatch tiles
// at a fixed pixel width; each tile carries its #HEX + RGB label rendered
// in a luma-aware plate so it's legible on any swatch colour.

using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using FracturingFog.Imaging;

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
            using var bmp = new Bitmap(Width, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var font = new Font("Consolas", 11f, FontStyle.Bold);
            for (int i = 0; i < swatches.Count; i++)
            {
                var c = swatches[i];
                var rect = new Rectangle(0, i * TileHeight, Width, TileHeight);
                using var brush = new SolidBrush(Color.FromArgb(c.R, c.G, c.B));
                g.FillRectangle(brush, rect);

                string text = $"#{c.R:X2}{c.G:X2}{c.B:X2}   RGB({c.R}, {c.G}, {c.B})";
                double luma = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                using var textBrush = new SolidBrush(luma < 0.5 ? Color.White : Color.Black);
                var sz = g.MeasureString(text, font);
                g.DrawString(text, font, textBrush,
                    (Width - sz.Width) / 2f,
                    i * TileHeight + (TileHeight - sz.Height) / 2f);
            }

            bmp.Save(path, ImageFormat.Png);
        }
    }
}
