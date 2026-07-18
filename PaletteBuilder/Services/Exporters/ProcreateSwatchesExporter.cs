// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Services/Exporters/ProcreateSwatchesExporter.cs
//
// Procreate .swatches = zip archive with a single Swatches.json inside.
// JSON is an array of "swatch sets" (palettes), each carrying:
//   { "name": "...", "swatches": [ { "hue":..., "saturation":..., "brightness":...,
//                                    "alpha":1, "colorSpace":0 }, ... ] }
// Values are HSV doubles in [0,1]. colorSpace 0 = sRGB.
//
// Procreate's import is forgiving — empty / missing names are accepted.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using FracturingFog.Imaging;
using FracturingFog.Imaging.PaletteExtraction;

namespace PaletteBuilder.Services.Exporters
{
    public sealed class ProcreateSwatchesExporter : IPaletteExporter
    {
        public string Id => "procreate";
        public string DisplayName => "Procreate (.swatches)";
        public string Extension => "swatches";

        public void Export(string path,
                           IReadOnlyList<(byte R, byte G, byte B)> swatches,
                           IReadOnlyList<PaletteStop>? stops = null,
                           PaletteExportContext? context = null)
        {
            string json = BuildJson(swatches, context?.PaletteName ?? "Palette");
            if (File.Exists(path)) File.Delete(path);
            using var fs = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("Swatches.json", CompressionLevel.Optimal);
            using var es = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            es.Write(bytes, 0, bytes.Length);
        }

        private static string BuildJson(IReadOnlyList<(byte R, byte G, byte B)> swatches, string name)
        {
            var sb = new StringBuilder();
            sb.Append("[{\"name\":\"").Append(Escape(name)).Append("\",\"swatches\":[");
            for (int i = 0; i < swatches.Count; i++)
            {
                var c = swatches[i];
                RgbToHsv(c.R, c.G, c.B, out double h, out double s, out double v);
                if (i > 0) sb.Append(',');
                sb.Append('{')
                  .Append("\"hue\":").Append(F(h)).Append(',')
                  .Append("\"saturation\":").Append(F(s)).Append(',')
                  .Append("\"brightness\":").Append(F(v)).Append(',')
                  .Append("\"alpha\":1,\"colorSpace\":0")
                  .Append('}');
            }
            sb.Append("]}]");
            return sb.ToString();
        }

        private static string F(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

        private static string Escape(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // Standard sRGB → HSV (0..1 on each channel).
        private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
            double max = System.Math.Max(rf, System.Math.Max(gf, bf));
            double min = System.Math.Min(rf, System.Math.Min(gf, bf));
            v = max;
            double delta = max - min;
            s = max <= 0 ? 0 : delta / max;
            if (delta < 1e-9) { h = 0; return; }
            double hueDeg;
            if (max == rf) hueDeg = 60 * (((gf - bf) / delta) % 6);
            else if (max == gf) hueDeg = 60 * (((bf - rf) / delta) + 2);
            else hueDeg = 60 * (((rf - gf) / delta) + 4);
            if (hueDeg < 0) hueDeg += 360;
            h = hueDeg / 360.0;
        }
    }
}
