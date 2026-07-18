// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Services/Exporters/JsonPaletteExporter.cs
//
// Plain JSON dump — easiest interop with anything scripted. Includes both
// swatches (with HEX + RGB) and the original gradient stops if present.

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FracturingFog.Imaging;

namespace PaletteBuilder.Services.Exporters
{
    public sealed class JsonPaletteExporter : IPaletteExporter
    {
        public string Id => "json";
        public string DisplayName => "JSON";
        public string Extension => "json";

        public void Export(string path,
                           IReadOnlyList<(byte R, byte G, byte B)> swatches,
                           IReadOnlyList<PaletteStop>? stops = null,
                           PaletteExportContext? context = null)
        {
            var doc = new
            {
                name = context?.PaletteName ?? "Palette",
                source = context?.SourceImagePath,
                method = context?.MethodName,
                swatches = ToSwatchDtos(swatches),
                stops = stops is null ? null : ToStopDtos(stops),
            };

            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, opts));
        }

        private static List<object> ToSwatchDtos(IReadOnlyList<(byte R, byte G, byte B)> swatches)
        {
            var list = new List<object>(swatches.Count);
            foreach (var c in swatches)
                list.Add(new
                {
                    hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}",
                    r = c.R, g = c.G, b = c.B,
                });
            return list;
        }

        private static List<object> ToStopDtos(IReadOnlyList<PaletteStop> stops)
        {
            var list = new List<object>(stops.Count);
            foreach (var s in stops)
                list.Add(new
                {
                    position = s.Position,
                    hex = $"#{s.R:X2}{s.G:X2}{s.B:X2}",
                    r = s.R, g = s.G, b = s.B,
                });
            return list;
        }
    }
}
