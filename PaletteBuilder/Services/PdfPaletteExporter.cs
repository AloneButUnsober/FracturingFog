// Services/PdfPaletteExporter.cs
//
// Palette → PDF via PDFsharp 6 (-gdi flavour). Honours PdfExportOptions
// from context.Extra to tune layout:
//   • Page size + orientation (Letter / Legal / Tabloid / A4 / A3).
//   • Column count 1..6.
//   • Optional cover page (source preview + extraction settings dump).
//   • Optional source-image thumbnail above the first swatch grid.
//   • Optional gradient strip below the swatch grid (uses stops).
//   • Optional per-swatch metadata block (HSL / CMYK approx / Lab /
//     contrast-vs-white).
//   • Optional CVD rows (proto / deutero / trito) per swatch.
//   • PDF Info populated with name, method, settings dump.
//
// With no options object the legacy layout (Letter portrait, 2 cols,
// label-on-swatch only) is preserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FracturingFog.Imaging;
using FracturingFog.Imaging.PaletteExtraction;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PaletteBuilder.Services
{
    public sealed class PdfPaletteExporter : IPaletteExporter
    {
        public string Id => "pdf";
        public string DisplayName => "PDF document";
        public string Extension => "pdf";

        private const double Margin = 36;
        private const double Gutter = 18;
        private const double RowGap = 12;
        private const double TitleHeight = 28;
        private const double LabelPad = 4;

        public void Export(string path,
                           IReadOnlyList<(byte R, byte G, byte B)> swatches,
                           IReadOnlyList<PaletteStop>? stops = null,
                           PaletteExportContext? context = null)
        {
            var opts = (context?.Extra as PdfExportOptions) ?? new PdfExportOptions
            {
                SourceImagePath = context?.SourceImagePath,
            };
            opts.SourceImagePath ??= context?.SourceImagePath;

            using var doc = new PdfDocument();
            string baseTitle = context?.PaletteName ?? "Palette";

            // ── PDF Info (6.9) ────────────────────────────────────────────
            doc.Info.Title = baseTitle;
            doc.Info.Creator = "Palette Builder";
            doc.Info.Author = Environment.UserName;
            if (!string.IsNullOrEmpty(context?.MethodName))
                doc.Info.Subject = "Method: " + context.MethodName;
            doc.Info.Keywords = string.Join(", ", new[]
            {
                "palette",
                context?.MethodName,
                context?.PaletteName,
            }.Where(s => !string.IsNullOrEmpty(s)));

            var titleFont = new XFont("Arial", 14, XFontStyleEx.Bold);
            var subtitleFont = new XFont("Arial", 10, XFontStyleEx.Bold);
            var labelFont = new XFont("Consolas", 9, XFontStyleEx.Bold);
            var metaFont = new XFont("Consolas", 7, XFontStyleEx.Regular);

            // ── Cover page (6.8) ─────────────────────────────────────────
            if (opts.IncludeCoverPage)
                DrawCoverPage(doc, opts, baseTitle, context, titleFont, subtitleFont, metaFont);

            // ── Comparison page (6.7) ────────────────────────────────────
            if (opts.IncludeComparisonPage && opts.ComparisonRows is { Count: > 0 })
                DrawComparisonPage(doc, opts, baseTitle, opts.ComparisonRows, titleFont, subtitleFont, labelFont);

            if (swatches.Count == 0)
            {
                var blank = AddSizedPage(doc, opts);
                using var bg = XGraphics.FromPdfPage(blank);
                DrawTitle(bg, $"{baseTitle} — (empty)", titleFont, blank.Width.Point);
                using var s = File.Create(path);
                doc.Save(s);
                return;
            }

            // ── Swatch grid layout ────────────────────────────────────────
            double pageW, pageH;
            (pageW, pageH) = PageDimensions(opts);
            int cols = Math.Clamp(opts.Columns, 1, 6);
            double colWidth = (pageW - 2 * Margin - Gutter * (cols - 1)) / cols;
            double tileHeight = ComputeTileHeight(opts);
            double topUsedFirstPage = Margin + TitleHeight;
            if (opts.IncludeSourceThumbnail) topUsedFirstPage += ThumbnailHeight + 12;
            double usableFirst = pageH - topUsedFirstPage - Margin
                                 - (opts.IncludeGradientStrip ? GradientStripHeight + 8 : 0);
            double usableOther = pageH - 2 * Margin - TitleHeight
                                 - (opts.IncludeGradientStrip ? GradientStripHeight + 8 : 0);

            int rowsFirst = Math.Max(1, (int)Math.Floor((usableFirst + RowGap) / (tileHeight + RowGap)));
            int rowsOther = Math.Max(1, (int)Math.Floor((usableOther + RowGap) / (tileHeight + RowGap)));
            int tilesFirst = rowsFirst * cols;
            int tilesOther = rowsOther * cols;

            int written = 0;
            int pageIndex = 0;
            int maxWeight = MaxWeight(stops);

            while (written < swatches.Count)
            {
                var page = AddSizedPage(doc, opts);
                double pgW = page.Width.Point;
                double pgH = page.Height.Point;
                using var gfx = XGraphics.FromPdfPage(page);

                string title = $"{baseTitle} — {swatches.Count} color{(swatches.Count == 1 ? "" : "s")}";
                if (pageIndex > 0 || opts.IncludeCoverPage)
                    title += $" (page {pageIndex + 1})";
                DrawTitle(gfx, title, titleFont, pgW);

                double y = Margin + TitleHeight;
                if (pageIndex == 0 && opts.IncludeSourceThumbnail && !string.IsNullOrEmpty(opts.SourceImagePath))
                {
                    DrawSourceThumbnail(gfx, opts.SourceImagePath!, Margin, y, pgW - 2 * Margin, ThumbnailHeight);
                    y += ThumbnailHeight + 12;
                }

                int capacity = pageIndex == 0 && opts.IncludeSourceThumbnail ? tilesFirst : tilesOther;
                int end = Math.Min(written + capacity, swatches.Count);

                for (int i = written; i < end; i++)
                {
                    int local = i - written;
                    int row = local / cols;
                    int col = local % cols;
                    double x = Margin + col * (colWidth + Gutter);
                    double tileY = y + row * (tileHeight + RowGap);
                    DrawSwatchTile(gfx, x, tileY, colWidth, tileHeight, swatches[i], labelFont, metaFont, opts,
                                   stops, maxWeight);
                }

                if (opts.IncludeGradientStrip && stops is { Count: > 0 })
                {
                    double stripY = pgH - Margin - GradientStripHeight;
                    DrawGradientStrip(gfx, Margin, stripY, pgW - 2 * Margin, GradientStripHeight, stops);
                }

                written = end;
                pageIndex++;
            }

            using var stream = File.Create(path);
            doc.Save(stream);
        }

        // ── Static legacy shim ─────────────────────────────────────────────

        public static void Export(string path, IReadOnlyList<(byte R, byte G, byte B)> swatches)
            => new PdfPaletteExporter().Export(path, swatches, null, null);

        // ── Layout helpers ─────────────────────────────────────────────────

        private const double ThumbnailHeight = 120;
        private const double GradientStripHeight = 30;

        private static double ComputeTileHeight(PdfExportOptions opts)
        {
            double h = 90;
            if (opts.IncludeSwatchMetadata) h += 48;
            if (opts.IncludeCvdRows) h += 28;
            return h;
        }

        private static (double w, double h) PageDimensions(PdfExportOptions opts)
        {
            (double pW, double pH) = opts.PageSize switch
            {
                PdfPageSize.Legal => (612.0, 1008.0),
                PdfPageSize.Tabloid => (792.0, 1224.0),
                PdfPageSize.A4 => (595.0, 842.0),
                PdfPageSize.A3 => (842.0, 1191.0),
                _ => (612.0, 792.0),  // Letter
            };
            return opts.Orientation == PdfOrientation.Landscape ? (pH, pW) : (pW, pH);
        }

        private static PdfPage AddSizedPage(PdfDocument doc, PdfExportOptions opts)
        {
            var page = doc.AddPage();
            page.Size = opts.PageSize switch
            {
                PdfPageSize.Legal => PdfSharp.PageSize.Legal,
                PdfPageSize.Tabloid => PdfSharp.PageSize.Tabloid,
                PdfPageSize.A4 => PdfSharp.PageSize.A4,
                PdfPageSize.A3 => PdfSharp.PageSize.A3,
                _ => PdfSharp.PageSize.Letter,
            };
            page.Orientation = opts.Orientation == PdfOrientation.Landscape
                ? PdfSharp.PageOrientation.Landscape
                : PdfSharp.PageOrientation.Portrait;
            return page;
        }

        private static void DrawTitle(XGraphics gfx, string text, XFont font, double pageWidth)
        {
            gfx.DrawString(text, font, XBrushes.Black,
                new XRect(Margin, Margin, pageWidth - 2 * Margin, TitleHeight),
                XStringFormats.TopLeft);
        }

        // ── Cover page ─────────────────────────────────────────────────────

        private static void DrawCoverPage(PdfDocument doc, PdfExportOptions opts, string title,
                                          PaletteExportContext? context, XFont titleFont,
                                          XFont subtitleFont, XFont metaFont)
        {
            var page = AddSizedPage(doc, opts);
            double pgW = page.Width.Point;
            double pgH = page.Height.Point;
            using var gfx = XGraphics.FromPdfPage(page);

            DrawTitle(gfx, title, titleFont, pgW);

            double y = Margin + TitleHeight + 12;
            if (!string.IsNullOrEmpty(opts.SourceImagePath))
            {
                double thumbH = Math.Min(pgH * 0.45, 400);
                DrawSourceThumbnail(gfx, opts.SourceImagePath!, Margin, y, pgW - 2 * Margin, thumbH);
                y += thumbH + 16;
            }

            if (!string.IsNullOrEmpty(context?.MethodName))
            {
                gfx.DrawString("Method: " + context.MethodName, subtitleFont, XBrushes.Black,
                    new XRect(Margin, y, pgW - 2 * Margin, 16), XStringFormats.TopLeft);
                y += 20;
            }
            if (!string.IsNullOrEmpty(opts.SettingsDump))
            {
                gfx.DrawString("Settings:", subtitleFont, XBrushes.Black,
                    new XRect(Margin, y, pgW - 2 * Margin, 16), XStringFormats.TopLeft);
                y += 18;

                foreach (var line in opts.SettingsDump.Split('\n'))
                {
                    if (y > pgH - Margin) break;
                    gfx.DrawString(line.TrimEnd('\r'), metaFont, XBrushes.Black,
                        new XRect(Margin, y, pgW - 2 * Margin, 12), XStringFormats.TopLeft);
                    y += 11;
                }
            }
        }

        // ── Comparison page (6.7) ──────────────────────────────────────────

        private static void DrawComparisonPage(PdfDocument doc, PdfExportOptions opts, string baseTitle,
                                               IReadOnlyList<PdfComparisonRow> rows,
                                               XFont titleFont, XFont subtitleFont, XFont labelFont)
        {
            var page = AddSizedPage(doc, opts);
            double pgW = page.Width.Point;
            double pgH = page.Height.Point;
            using var gfx = XGraphics.FromPdfPage(page);
            DrawTitle(gfx, baseTitle + " — Method comparison", titleFont, pgW);

            double y = Margin + TitleHeight + 4;
            double rowW = pgW - 2 * Margin;
            double labelStripH = 16;
            double swatchH = 24;
            double gradientH = 18;
            double rowH = labelStripH + swatchH + gradientH + 10;

            foreach (var r in rows)
            {
                if (y + rowH > pgH - Margin) break;

                gfx.DrawString(r.MethodName + $"  ({r.Swatches.Count})", subtitleFont, XBrushes.Black,
                    new XRect(Margin, y, rowW, labelStripH), XStringFormats.TopLeft);

                double swY = y + labelStripH;
                int n = r.Swatches.Count;
                if (n > 0)
                {
                    double sw = rowW / n;
                    for (int i = 0; i < n; i++)
                    {
                        var c = r.Swatches[i];
                        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(c.R, c.G, c.B)),
                            new XRect(Margin + i * sw, swY, sw, swatchH));
                    }
                    gfx.DrawRectangle(new XPen(XColors.Black, 0.5), new XRect(Margin, swY, rowW, swatchH));
                }

                if (r.Stops.Count > 0)
                    DrawGradientStrip(gfx, Margin, swY + swatchH + 2, rowW, gradientH, r.Stops);

                y += rowH;
            }
        }

        // ── Swatch tile ────────────────────────────────────────────────────

        private static void DrawSwatchTile(XGraphics gfx,
                                           double x, double y, double w, double h,
                                           (byte R, byte G, byte B) c,
                                           XFont labelFont, XFont metaFont,
                                           PdfExportOptions opts,
                                           IReadOnlyList<PaletteStop>? stops,
                                           int maxWeight)
        {
            // Compute sub-rectangles top-down: colour band → metadata → CVD strip.
            double cvdH = opts.IncludeCvdRows ? 22 : 0;
            double metaH = opts.IncludeSwatchMetadata ? 44 : 0;
            double bandH = h - cvdH - metaH;

            var fillBrush = new XSolidBrush(XColor.FromArgb(c.R, c.G, c.B));
            var bandRect = new XRect(x, y, w, bandH);
            gfx.DrawRectangle(fillBrush, bandRect);
            gfx.DrawRectangle(new XPen(XColors.Black, 0.75), bandRect);

            DrawSwatchLabel(gfx, x, y, w, bandH, c, labelFont);

            if (opts.IncludeSwatchMetadata)
                DrawSwatchMetadata(gfx, x, y + bandH, w, metaH, c, metaFont, stops, maxWeight);

            if (opts.IncludeCvdRows)
                DrawCvdStrip(gfx, x, y + bandH + metaH, w, cvdH, c, metaFont);
        }

        private static void DrawSwatchLabel(XGraphics gfx, double x, double y, double w, double h,
                                            (byte R, byte G, byte B) c, XFont font)
        {
            string label = $"RGB({c.R}, {c.G}, {c.B})   #{c.R:X2}{c.G:X2}{c.B:X2}";
            var sz = gfx.MeasureString(label, font);
            double plateW = sz.Width + LabelPad * 2;
            double plateH = sz.Height + LabelPad * 2;
            double plateX = x + (w - plateW) / 2;
            double plateY = y + LabelPad;

            double luma = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            bool darkSwatch = luma < 0.5;
            var plateBrush = new XSolidBrush(darkSwatch
                ? XColor.FromArgb(220, 255, 255, 255)
                : XColor.FromArgb(220, 0, 0, 0));
            var textBrush = darkSwatch ? XBrushes.Black : XBrushes.White;

            gfx.DrawRectangle(plateBrush, new XRect(plateX, plateY, plateW, plateH));
            gfx.DrawString(label, font, textBrush,
                new XRect(plateX, plateY, plateW, plateH), XStringFormats.Center);
        }

        private static void DrawSwatchMetadata(XGraphics gfx, double x, double y, double w, double h,
                                               (byte R, byte G, byte B) c, XFont font,
                                               IReadOnlyList<PaletteStop>? stops, int maxWeight)
        {
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(245, 245, 245)), new XRect(x, y, w, h));

            ColorSpaces.RgbToHsl(c.R, c.G, c.B, out float hh, out float ss, out float ll);
            ColorSpaces.RgbToLab(c.R, c.G, c.B, out float L, out float A, out float B);
            (byte cmK, byte cmY, byte cmM, byte cmC) = RgbToCmykApprox(c.R, c.G, c.B);
            double whiteRatio = WcagContrast.RatioBetween(c.R, c.G, c.B, 255, 255, 255);
            double blackRatio = WcagContrast.RatioBetween(c.R, c.G, c.B, 0, 0, 0);

            double lineH = 10;
            double tx = x + 6;
            double ty = y + 3;
            gfx.DrawString($"HSL {hh:0}°  {ss * 100:0}%  {ll * 100:0}%", font, XBrushes.Black,
                new XRect(tx, ty, w - 12, lineH), XStringFormats.TopLeft);
            gfx.DrawString($"Lab {L:0.0}  {A:0.0}  {B:0.0}", font, XBrushes.Black,
                new XRect(tx, ty + lineH, w - 12, lineH), XStringFormats.TopLeft);
            gfx.DrawString($"CMYK {cmC}/{cmM}/{cmY}/{cmK}", font, XBrushes.Black,
                new XRect(tx, ty + lineH * 2, w - 12, lineH), XStringFormats.TopLeft);
            gfx.DrawString($"vs white {whiteRatio:0.0}:1   vs black {blackRatio:0.0}:1", font, XBrushes.Black,
                new XRect(tx, ty + lineH * 3, w - 12, lineH), XStringFormats.TopLeft);
        }

        private static void DrawCvdStrip(XGraphics gfx, double x, double y, double w, double h,
                                         (byte R, byte G, byte B) c, XFont font)
        {
            double cellW = (w - 6) / 3;
            var kinds = new[]
            {
                (label: "Proto", kind: CvdKind.Protanopia),
                (label: "Deut",  kind: CvdKind.Deuteranopia),
                (label: "Trito", kind: CvdKind.Tritanopia),
            };
            for (int i = 0; i < kinds.Length; i++)
            {
                var (label, kind) = kinds[i];
                var sim = CvdSimulator.Simulate(c.R, c.G, c.B, kind);
                double cx = x + 3 + i * (cellW + 0);
                var rect = new XRect(cx, y + 1, cellW, h - 2);
                gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(sim.R, sim.G, sim.B)), rect);
                gfx.DrawRectangle(new XPen(XColors.Black, 0.5), rect);
                double luma = (0.2126 * sim.R + 0.7152 * sim.G + 0.0722 * sim.B) / 255.0;
                gfx.DrawString(label, font, luma < 0.5 ? XBrushes.White : XBrushes.Black,
                    rect, XStringFormats.Center);
            }
        }

        // ── Gradient strip ─────────────────────────────────────────────────

        private static void DrawGradientStrip(XGraphics gfx, double x, double y, double w, double h,
                                              IReadOnlyList<PaletteStop> stops)
        {
            int slices = Math.Max(64, (int)w);
            double sliceW = w / slices;
            var ordered = new List<PaletteStop>(stops);
            ordered.Sort((a, b) => a.Position.CompareTo(b.Position));
            var tuples = new (float Position, byte R, byte G, byte B)[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
                tuples[i] = (ordered[i].Position, ordered[i].R, ordered[i].G, ordered[i].B);

            var space = GradientRenderSettings.Space;
            for (int i = 0; i < slices; i++)
            {
                double t = (i + 0.5) / slices;
                var c = GradientInterpolation.Sample(tuples, (float)t, space);
                gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(c.R, c.G, c.B)),
                    new XRect(x + i * sliceW, y, sliceW + 0.5, h));
            }
            gfx.DrawRectangle(new XPen(XColors.Black, 0.75), new XRect(x, y, w, h));
        }

        // ── Misc helpers ───────────────────────────────────────────────────

        private static void DrawSourceThumbnail(XGraphics gfx, string path, double x, double y, double maxW, double maxH)
        {
            try
            {
                using var img = XImage.FromFile(path);
                double ratio = Math.Min(maxW / img.PixelWidth, maxH / img.PixelHeight);
                double drawW = img.PixelWidth * ratio;
                double drawH = img.PixelHeight * ratio;
                double drawX = x + (maxW - drawW) / 2;
                gfx.DrawImage(img, drawX, y, drawW, drawH);
                gfx.DrawRectangle(new XPen(XColors.Black, 0.5), new XRect(drawX, y, drawW, drawH));
            }
            catch
            {
                gfx.DrawRectangle(new XPen(XColors.Gray, 0.5), new XRect(x, y, maxW, maxH));
                gfx.DrawString("(source image unavailable)",
                    new XFont("Arial", 10, XFontStyleEx.Italic), XBrushes.Gray,
                    new XRect(x, y, maxW, maxH), XStringFormats.Center);
            }
        }

        private static (byte K, byte Y, byte M, byte C) RgbToCmykApprox(byte r, byte g, byte b)
        {
            double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
            double k = 1 - Math.Max(rf, Math.Max(gf, bf));
            if (k >= 1) return (100, 0, 0, 0);
            double c = (1 - rf - k) / (1 - k);
            double m = (1 - gf - k) / (1 - k);
            double y = (1 - bf - k) / (1 - k);
            return ((byte)Math.Round(k * 100),
                    (byte)Math.Round(y * 100),
                    (byte)Math.Round(m * 100),
                    (byte)Math.Round(c * 100));
        }

        private static int MaxWeight(IReadOnlyList<PaletteStop>? stops)
            => 0; // stops don't carry weight; metadata uses contrast not weight pct
    }
}
