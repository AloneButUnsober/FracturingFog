// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Services/Exporters/InkscapeSwatchesExporter.cs
//
// Inkscape uses the GIMP .gpl format for swatches, but also reads an SVG
// palette XML — one <color> element per swatch with sRGB hex. This emits
// the SVG-based palette so it can be dropped into Inkscape's share/palettes
// or any tool that consumes SVG colour lists.

using System.Collections.Generic;
using System.IO;
using System.Text;
using FracturingFog.Imaging;

namespace PaletteBuilder.Services.Exporters
{
    public sealed class InkscapeSwatchesExporter : IPaletteExporter
    {
        public string Id => "inkscape-svg";
        public string DisplayName => "Inkscape SVG swatches";
        public string Extension => "svg";

        public void Export(string path,
                           IReadOnlyList<(byte R, byte G, byte B)> swatches,
                           IReadOnlyList<PaletteStop>? stops = null,
                           PaletteExportContext? context = null)
        {
            int n = swatches.Count;
            int tile = 32;
            int w = n * tile;
            int h = tile;

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">");
            sb.AppendLine($"  <title>{System.Net.WebUtility.HtmlEncode(context?.PaletteName ?? "Palette")}</title>");
            for (int i = 0; i < n; i++)
            {
                var c = swatches[i];
                string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                sb.AppendLine($"  <rect x=\"{i * tile}\" y=\"0\" width=\"{tile}\" height=\"{tile}\" fill=\"{hex}\"><title>{hex}</title></rect>");
            }
            sb.AppendLine("</svg>");
            File.WriteAllText(path, sb.ToString());
        }
    }
}
