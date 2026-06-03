// Services/IPaletteExporter.cs
//
// Contract for an output format. The MainWindow asks each registered
// exporter for its filter chip + extension, hands the selected one the
// chosen path + the current palette, and the exporter writes the file.
//
// Future formats (PNG, JSON, CSS, GIMP .gpl, Adobe .ase, etc.) implement
// this and are added to ExporterRegistry — the UI picks them up
// automatically.

using System.Collections.Generic;
using FracturingFog.Imaging;

namespace PaletteBuilder.Services
{
    /// <summary>
    /// Optional context passed to exporters that benefit from extra data
    /// (e.g. PDF cover page wants the source image path; ASE wants a group
    /// name). Properties are nullable — exporters ignore what they don't
    /// need.
    /// </summary>
    public sealed class PaletteExportContext
    {
        public string? SourceImagePath { get; init; }
        public string? PaletteName { get; init; }
        public string? MethodName { get; init; }

        /// <summary>
        /// Format-specific options bag. PDF exporter looks for PdfExportOptions
        /// here; other exporters ignore. Generic exporters never need this.
        /// </summary>
        public object? Extra { get; init; }
    }

    public interface IPaletteExporter
    {
        /// <summary>Stable identifier, e.g. "pdf", "json", "gimp-gpl". Used by VM commands.</summary>
        string Id { get; }

        /// <summary>Human-readable name for the format picker, e.g. "PDF document".</summary>
        string DisplayName { get; }

        /// <summary>File extension without the dot, e.g. "pdf".</summary>
        string Extension { get; }

        /// <summary>
        /// Write <paramref name="swatches"/> + optional <paramref name="stops"/> to
        /// <paramref name="path"/>. <paramref name="context"/> may be null when the
        /// caller has nothing extra to share.
        /// </summary>
        void Export(string path,
                    IReadOnlyList<(byte R, byte G, byte B)> swatches,
                    IReadOnlyList<PaletteStop>? stops = null,
                    PaletteExportContext? context = null);
    }
}
