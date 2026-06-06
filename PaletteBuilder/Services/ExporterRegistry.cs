// Services/ExporterRegistry.cs
//
// Static directory of every IPaletteExporter the app ships with. Phase 0
// only registers the PDF exporter; later phases append JSON / PNG / CSS /
// GIMP .gpl / Adobe .ase / Sketch / etc.
//
// The MainWindow uses Exporters to populate the format dropdown and resolves
// a format ID back to its exporter when the user clicks Export.

using System;
using System.Collections.Generic;
using System.Linq;
using PaletteBuilder.Services.Exporters;

namespace PaletteBuilder.Services
{
    public static class ExporterRegistry
    {
        private static readonly List<IPaletteExporter> s_exporters = new()
        {
            new PdfPaletteExporter(),
            new PngSheetExporter(),
            new JsonPaletteExporter(),
            new CssVariablesExporter(),
            new ScssMapExporter(),
            new TailwindConfigExporter(),
            new GimpGplExporter(),
            new SketchPaletteExporter(),
            new InkscapeSwatchesExporter(),
            new AdobeAseExporter(),
            new ProcreateSwatchesExporter(),
            new KritaKplExporter(),
        };

        public static IReadOnlyList<IPaletteExporter> Exporters => s_exporters;

        public static IPaletteExporter? FindById(string id)
            => s_exporters.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Adds an exporter at runtime (tests, plugin path). No dedup — caller
        /// is responsible for unique IDs.
        /// </summary>
        public static void Register(IPaletteExporter exporter)
        {
            if (exporter == null) throw new ArgumentNullException(nameof(exporter));
            s_exporters.Add(exporter);
        }
    }
}
