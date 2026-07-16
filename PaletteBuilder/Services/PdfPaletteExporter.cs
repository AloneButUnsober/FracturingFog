// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Services/PdfPaletteExporter.cs
//
// Palette → PDF via QuestPDF (Community licence). Honours PdfExportOptions
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
//
// Phase X.1 / Slice 1.1 — was PDFsharp-gdi (System.Drawing.Common chain,
// net10.0-windows). Rewritten on QuestPDF + SkiaSharp so the exporter
// builds cross-platform alongside the rest of PaletteBuilder.Lib once the
// TFM flips (Slice 1.2). Layout primitives + auto-pagination replace the
// manual point-coord math that the PDFsharp version threaded by hand.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using FracturingFog.Imaging;
using FracturingFog.Imaging.PaletteExtraction;

using QuestPDF;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

using SkiaSharp;

namespace PaletteBuilder.Services
{
    public sealed class PdfPaletteExporter : IPaletteExporter
    {
        public string Id => "pdf";
        public string DisplayName => "PDF document";
        public string Extension => "pdf";

        // ── QuestPDF licensing ────────────────────────────────────────────────
        // Community licence is the free tier (open-source projects or
        // organisations under USD 1M annual revenue). Set process-wide once
        // in the static constructor — idempotent across multiple instances.
        // Operators with a paid Professional/Enterprise key can override this
        // value before constructing the exporter; the last value set wins.
        static PdfPaletteExporter()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        // ── Layout constants ──────────────────────────────────────────────────
        private const float Margin              = 36f;
        private const float ThumbnailHeight     = 120f;
        private const float GradientStripHeight = 30f;
        private const float BandHeight          = 90f;
        private const float MetadataBlockHeight = 44f;
        private const float CvdRowHeight        = 22f;
        private const int   GradientPngWidth    = 800;
        private const int   GradientPngHeight   = 40;

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

            string baseTitle = context?.PaletteName ?? "Palette";

            // Bake the gradient strip to PNG once. Embedding a single image is
            // dramatically cheaper than the N>=64 micro-rectangles the PDFsharp
            // version emitted per page, and SkiaSharp ships cross-platform.
            byte[]? gradientPng = null;
            if (opts.IncludeGradientStrip && stops is { Count: > 0 })
                gradientPng = BakeGradientPng(stops, GradientPngWidth, GradientPngHeight);

            int maxWeight = MaxWeight(stops);

            var metadata = new DocumentMetadata
            {
                Title = baseTitle,
                Creator = "Palette Builder",
                Author = Environment.UserName,
                Subject = string.IsNullOrEmpty(context?.MethodName)
                    ? string.Empty
                    : "Method: " + context.MethodName,
                Keywords = string.Join(", ", new[]
                {
                    "palette",
                    context?.MethodName,
                    context?.PaletteName,
                }.Where(s => !string.IsNullOrEmpty(s))),
            };

            Document.Create(container =>
            {
                if (opts.IncludeCoverPage)
                    container.Page(page => ComposeCoverPage(page, opts, baseTitle, context));

                if (opts.IncludeComparisonPage && opts.ComparisonRows is { Count: > 0 })
                    container.Page(page => ComposeComparisonPage(page, opts, baseTitle, opts.ComparisonRows));

                if (swatches.Count == 0)
                {
                    container.Page(page =>
                    {
                        ApplyPageSize(page, opts);
                        page.Content().Text($"{baseTitle} — (empty)").FontSize(14).Bold();
                    });
                }
                else
                {
                    container.Page(page => ComposeSwatchPages(
                        page, opts, baseTitle, swatches, stops, gradientPng, maxWeight));
                }
            })
            .WithMetadata(metadata)
            .GeneratePdf(path);
        }

        // ── Static legacy shim ────────────────────────────────────────────────

        public static void Export(string path, IReadOnlyList<(byte R, byte G, byte B)> swatches)
            => new PdfPaletteExporter().Export(path, swatches, null, null);

        // ── Page setup ────────────────────────────────────────────────────────

        private static void ApplyPageSize(PageDescriptor page, PdfExportOptions opts)
        {
            var (w, h) = PageDimensionsPoints(opts);
            page.Size((float)w, (float)h, Unit.Point);
            page.Margin(Margin, Unit.Point);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(t => t.FontSize(10).FontFamily(Fonts.Arial));
        }

        private static (double w, double h) PageDimensionsPoints(PdfExportOptions opts)
        {
            (double pW, double pH) = opts.PageSize switch
            {
                PdfPageSize.Legal   => (612.0, 1008.0),
                PdfPageSize.Tabloid => (792.0, 1224.0),
                PdfPageSize.A4      => (595.0, 842.0),
                PdfPageSize.A3      => (842.0, 1191.0),
                _                   => (612.0, 792.0), // Letter
            };
            return opts.Orientation == PdfOrientation.Landscape ? (pH, pW) : (pW, pH);
        }

        // ── Cover page ────────────────────────────────────────────────────────

        private static void ComposeCoverPage(PageDescriptor page, PdfExportOptions opts,
                                             string title, PaletteExportContext? context)
        {
            ApplyPageSize(page, opts);

            page.Content().Column(col =>
            {
                col.Spacing(12);
                col.Item().Text(title).FontSize(14).Bold();

                if (!string.IsNullOrEmpty(opts.SourceImagePath) && File.Exists(opts.SourceImagePath))
                {
                    col.Item()
                        .MaxHeight(400, Unit.Point)
                        .AlignCenter()
                        .Image(opts.SourceImagePath)
                        .FitArea();
                }

                if (!string.IsNullOrEmpty(context?.MethodName))
                    col.Item().Text("Method: " + context.MethodName).FontSize(10).Bold();

                if (!string.IsNullOrEmpty(opts.SettingsDump))
                {
                    col.Item().Text("Settings:").FontSize(10).Bold();
                    foreach (var line in opts.SettingsDump.Split('\n'))
                    {
                        col.Item().Text(line.TrimEnd('\r'))
                            .FontSize(7).FontFamily(Fonts.CourierNew);
                    }
                }
            });
        }

        // ── Comparison page ───────────────────────────────────────────────────

        private static void ComposeComparisonPage(PageDescriptor page, PdfExportOptions opts,
                                                  string baseTitle,
                                                  IReadOnlyList<PdfComparisonRow> rows)
        {
            ApplyPageSize(page, opts);

            page.Content().Column(col =>
            {
                col.Spacing(10);
                col.Item().Text(baseTitle + " — Method comparison").FontSize(14).Bold();

                foreach (var r in rows)
                {
                    col.Item().Column(sub =>
                    {
                        sub.Spacing(2);
                        sub.Item()
                            .Text(r.MethodName + $"  ({r.Swatches.Count})")
                            .FontSize(10).Bold();

                        if (r.Swatches.Count > 0)
                        {
                            sub.Item()
                                .Height(24)
                                .Border(0.5f).BorderColor(Colors.Black)
                                .Row(rowItems =>
                                {
                                    foreach (var c in r.Swatches)
                                        rowItems.RelativeItem().Background(Hex(c.R, c.G, c.B));
                                });
                        }

                        if (r.Stops.Count > 0)
                        {
                            var rowPng = BakeGradientPng(r.Stops, GradientPngWidth, GradientPngHeight);
                            sub.Item().Height(18).Image(rowPng).FitArea();
                        }
                    });
                }
            });
        }

        // ── Swatch pages (auto-paginated) ─────────────────────────────────────

        private static void ComposeSwatchPages(PageDescriptor page, PdfExportOptions opts,
                                               string baseTitle,
                                               IReadOnlyList<(byte R, byte G, byte B)> swatches,
                                               IReadOnlyList<PaletteStop>? stops,
                                               byte[]? gradientPng,
                                               int maxWeight)
        {
            ApplyPageSize(page, opts);

            string headerTitle = baseTitle
                + $" — {swatches.Count} color{(swatches.Count == 1 ? "" : "s")}";

            page.Header().Row(row =>
            {
                row.RelativeItem().Text(headerTitle).FontSize(14).Bold();
                row.ConstantItem(90, Unit.Point).AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8));
                    t.Span("page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });

            int cols = Math.Clamp(opts.Columns, 1, 6);

            page.Content().PaddingTop(6).Column(col =>
            {
                col.Spacing(8);

                // First-page-only thumbnail. QuestPDF's Column lays out items
                // in declaration order across the paginated page run — items
                // that fit on the first frame stay on the first frame and the
                // subsequent table picks up on page 2 without the thumbnail
                // reappearing. Matches the PDFsharp version's tilesFirst /
                // tilesOther split without the manual capacity math.
                if (opts.IncludeSourceThumbnail
                    && !string.IsNullOrEmpty(opts.SourceImagePath)
                    && File.Exists(opts.SourceImagePath))
                {
                    col.Item()
                        .Height(ThumbnailHeight, Unit.Point)
                        .AlignCenter()
                        .Image(opts.SourceImagePath!)
                        .FitArea();
                }

                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        for (int i = 0; i < cols; i++) c.RelativeColumn();
                    });

                    for (int i = 0; i < swatches.Count; i++)
                    {
                        int rowIdx = i / cols;
                        int colIdx = i % cols;
                        int captured = i;
                        table.Cell()
                            .Row((uint)(rowIdx + 1))
                            .Column((uint)(colIdx + 1))
                            .Padding(4)
                            .Element(cell => DrawSwatchTile(cell, swatches[captured], opts));
                    }
                });
            });

            if (gradientPng != null)
            {
                page.Footer()
                    .Height(GradientStripHeight + 4, Unit.Point)
                    .PaddingTop(4)
                    .Image(gradientPng)
                    .FitArea();
            }
        }

        // ── Swatch tile ───────────────────────────────────────────────────────

        private static void DrawSwatchTile(IContainer cell,
                                           (byte R, byte G, byte B) c,
                                           PdfExportOptions opts)
        {
            cell.Column(col =>
            {
                col.Item()
                    .Height(BandHeight, Unit.Point)
                    .Layers(layers =>
                    {
                        layers.PrimaryLayer()
                            .Background(Hex(c.R, c.G, c.B))
                            .Border(0.75f).BorderColor(Colors.Black);
                        layers.Layer()
                            .AlignCenter().AlignMiddle()
                            .Element(plate => DrawSwatchPlate(plate, c));
                    });

                if (opts.IncludeSwatchMetadata)
                {
                    col.Item()
                        .Height(MetadataBlockHeight, Unit.Point)
                        .Background("#F5F5F5")
                        .Padding(4)
                        .Element(meta => DrawSwatchMetadata(meta, c));
                }

                if (opts.IncludeCvdRows)
                {
                    col.Item()
                        .Height(CvdRowHeight, Unit.Point)
                        .Padding(2)
                        .Element(cvd => DrawCvdStrip(cvd, c));
                }
            });
        }

        private static void DrawSwatchPlate(IContainer plate, (byte R, byte G, byte B) c)
        {
            double luma = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            bool darkSwatch = luma < 0.5;
            // QuestPDF's hex parser does not preserve alpha consistently across
            // versions, so the legacy semi-transparent plate is rendered as a
            // solid contrast colour. The plate occludes a small patch of the
            // swatch but keeps the RGB / hex label legible on every host.
            string plateBg = darkSwatch ? Colors.White : Colors.Black;
            string textFg  = darkSwatch ? Colors.Black : Colors.White;

            plate
                .Background(plateBg)
                .Padding(4)
                .Text($"RGB({c.R}, {c.G}, {c.B})   #{c.R:X2}{c.G:X2}{c.B:X2}")
                .FontSize(9).Bold().FontColor(textFg);
        }

        private static void DrawSwatchMetadata(IContainer container, (byte R, byte G, byte B) c)
        {
            ColorSpaces.RgbToHsl(c.R, c.G, c.B, out float hh, out float ss, out float ll);
            ColorSpaces.RgbToLab(c.R, c.G, c.B, out float L, out float A, out float B);
            (byte cmK, byte cmY, byte cmM, byte cmC) = RgbToCmykApprox(c.R, c.G, c.B);
            double whiteRatio = WcagContrast.RatioBetween(c.R, c.G, c.B, 255, 255, 255);
            double blackRatio = WcagContrast.RatioBetween(c.R, c.G, c.B, 0, 0, 0);

            container.Column(col =>
            {
                col.Item().Text($"HSL {hh:0}°  {ss * 100:0}%  {ll * 100:0}%")
                    .FontSize(7).FontFamily(Fonts.CourierNew);
                col.Item().Text($"Lab {L:0.0}  {A:0.0}  {B:0.0}")
                    .FontSize(7).FontFamily(Fonts.CourierNew);
                col.Item().Text($"CMYK {cmC}/{cmM}/{cmY}/{cmK}")
                    .FontSize(7).FontFamily(Fonts.CourierNew);
                col.Item().Text($"vs white {whiteRatio:0.0}:1   vs black {blackRatio:0.0}:1")
                    .FontSize(7).FontFamily(Fonts.CourierNew);
            });
        }

        private static void DrawCvdStrip(IContainer container, (byte R, byte G, byte B) c)
        {
            var kinds = new[]
            {
                (label: "Proto", kind: CvdKind.Protanopia),
                (label: "Deut",  kind: CvdKind.Deuteranopia),
                (label: "Trito", kind: CvdKind.Tritanopia),
            };

            container.Row(row =>
            {
                row.Spacing(2);
                foreach (var (label, kind) in kinds)
                {
                    var sim = CvdSimulator.Simulate(c.R, c.G, c.B, kind);
                    double luma = (0.2126 * sim.R + 0.7152 * sim.G + 0.0722 * sim.B) / 255.0;
                    string fg = luma < 0.5 ? Colors.White : Colors.Black;
                    row.RelativeItem()
                        .Background(Hex(sim.R, sim.G, sim.B))
                        .Border(0.5f).BorderColor(Colors.Black)
                        .AlignCenter().AlignMiddle()
                        .Text(label).FontSize(7).FontColor(fg);
                }
            });
        }

        // ── Gradient strip — baked to PNG via SkiaSharp ───────────────────────

        private static byte[] BakeGradientPng(IReadOnlyList<PaletteStop> stops, int w, int h)
        {
            var ordered = new List<PaletteStop>(stops);
            ordered.Sort((a, b) => a.Position.CompareTo(b.Position));
            var tuples = new (float Position, byte R, byte G, byte B)[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
                tuples[i] = (ordered[i].Position, ordered[i].R, ordered[i].G, ordered[i].B);

            var space = GradientRenderSettings.Space;
            using var bitmap = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(bitmap))
            {
                using var paint = new SKPaint();
                for (int x = 0; x < w; x++)
                {
                    float t = (x + 0.5f) / w;
                    var sample = GradientInterpolation.Sample(tuples, t, space);
                    paint.Color = new SKColor(sample.R, sample.G, sample.B);
                    canvas.DrawLine(x, 0, x, h, paint);
                }
                canvas.Flush();
            }
            using var img = SKImage.FromBitmap(bitmap);
            using var data = img.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }

        // ── Misc helpers ──────────────────────────────────────────────────────

        private static string Hex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

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
