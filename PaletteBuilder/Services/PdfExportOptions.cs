// Services/PdfExportOptions.cs
//
// Per-export tuning for the PDF generator. Lives on PaletteExportContext.Extra
// so the generic IPaletteExporter contract stays oblivious to PDF-specific
// knobs while still letting the caller (MainWindow) pass them through.
//
// All flags default to "off" except Columns=2 and PageSize=Letter+Portrait
// so omitting the object reproduces the legacy output layout.

namespace PaletteBuilder.Services
{
    public enum PdfPageSize
    {
        Letter,
        Legal,
        Tabloid,
        A4,
        A3,
    }

    public enum PdfOrientation
    {
        Portrait,
        Landscape,
    }

    public sealed class PdfExportOptions
    {
        public PdfPageSize PageSize { get; set; } = PdfPageSize.Letter;
        public PdfOrientation Orientation { get; set; } = PdfOrientation.Portrait;

        /// <summary>1..6.</summary>
        public int Columns { get; set; } = 2;

        /// <summary>Render a cover page with source preview + settings before the swatch grid.</summary>
        public bool IncludeCoverPage { get; set; } = false;

        /// <summary>Embed a small source-image thumbnail at the top of the first swatch page.</summary>
        public bool IncludeSourceThumbnail { get; set; } = false;

        /// <summary>Render a gradient strip below the swatch grid (uses palette stops).</summary>
        public bool IncludeGradientStrip { get; set; } = false;

        /// <summary>Show per-swatch metadata block (HSL / CMYK approx / Lab / contrast-vs-white).</summary>
        public bool IncludeSwatchMetadata { get; set; } = false;

        /// <summary>Render protanopia / deuteranopia / tritanopia simulation strips per swatch.</summary>
        public bool IncludeCvdRows { get; set; } = false;

        /// <summary>Optional path to the source image — required when IncludeCoverPage or IncludeSourceThumbnail are on.</summary>
        public string? SourceImagePath { get; set; }

        /// <summary>Free-form settings dump rendered on cover page (extraction params, etc).</summary>
        public string? SettingsDump { get; set; }

        /// <summary>
        /// Render a comparison page showing each extractor's palette on the same
        /// source. The host VM populates <see cref="ComparisonRows"/> via a
        /// pre-export ExtractAll call when this is on.
        /// </summary>
        public bool IncludeComparisonPage { get; set; } = false;

        /// <summary>One row per method for the comparison page.</summary>
        public System.Collections.Generic.IReadOnlyList<PdfComparisonRow>? ComparisonRows { get; set; }
    }

    public sealed class PdfComparisonRow
    {
        public string MethodName { get; init; } = "";
        public System.Collections.Generic.IReadOnlyList<(byte R, byte G, byte B)> Swatches { get; init; }
            = System.Array.Empty<(byte, byte, byte)>();
        public System.Collections.Generic.IReadOnlyList<FracturingFog.Imaging.PaletteStop> Stops { get; init; }
            = System.Array.Empty<FracturingFog.Imaging.PaletteStop>();
    }
}
