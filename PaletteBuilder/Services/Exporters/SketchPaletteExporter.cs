// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Services/Exporters/SketchPaletteExporter.cs
//
// Sketch palette format (.sketchpalette / .json). Schema:
//   { "compatibleVersion":"2.0", "pluginVersion":"2.22",
//     "colors":[ {"red":0.5,"green":0.3,"blue":0.7,"alpha":1}, ... ] }
// Floats 0..1, alpha always 1.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FracturingFog.Imaging;

namespace PaletteBuilder.Services.Exporters
{
    public sealed class SketchPaletteExporter : IPaletteExporter
    {
        public string Id => "sketch";
        public string DisplayName => "Sketch palette (.sketchpalette)";
        public string Extension => "sketchpalette";

        public void Export(string path,
                           IReadOnlyList<(byte R, byte G, byte B)> swatches,
                           IReadOnlyList<PaletteStop>? stops = null,
                           PaletteExportContext? context = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\"compatibleVersion\":\"2.0\",\"pluginVersion\":\"2.22\",\"colors\":[");
            for (int i = 0; i < swatches.Count; i++)
            {
                var c = swatches[i];
                if (i > 0) sb.Append(',');
                sb.Append('{')
                  .Append("\"red\":").Append((c.R / 255.0).ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                  .Append("\"green\":").Append((c.G / 255.0).ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                  .Append("\"blue\":").Append((c.B / 255.0).ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                  .Append("\"alpha\":1")
                  .Append('}');
            }
            sb.Append("]}");
            File.WriteAllText(path, sb.ToString());
        }
    }
}
