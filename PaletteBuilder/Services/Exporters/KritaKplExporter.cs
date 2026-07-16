// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Services/Exporters/KritaKplExporter.cs
//
// Krita .kpl = zip archive with:
//   mimetype       : "application/x-krita-palette" (stored uncompressed)
//   colorset.xml   : palette XML (sRGB swatches)
//   profiles.xml   : empty profile list (acceptable for sRGB-only palettes)
//
// Layout adapted from the Krita resource format docs.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using FracturingFog.Imaging;

namespace PaletteBuilder.Services.Exporters
{
    public sealed class KritaKplExporter : IPaletteExporter
    {
        public string Id => "krita-kpl";
        public string DisplayName => "Krita palette (.kpl)";
        public string Extension => "kpl";

        public void Export(string path,
                           IReadOnlyList<(byte R, byte G, byte B)> swatches,
                           IReadOnlyList<PaletteStop>? stops = null,
                           PaletteExportContext? context = null)
        {
            if (File.Exists(path)) File.Delete(path);
            using var fs = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            // mimetype must be first and uncompressed for some readers.
            var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var ms = mime.Open())
            {
                var b = Encoding.ASCII.GetBytes("application/x-krita-palette");
                ms.Write(b, 0, b.Length);
            }

            var profiles = zip.CreateEntry("profiles.xml", CompressionLevel.Optimal);
            using (var ps = profiles.Open())
            {
                var b = Encoding.UTF8.GetBytes("<Profiles/>");
                ps.Write(b, 0, b.Length);
            }

            var colorset = zip.CreateEntry("colorset.xml", CompressionLevel.Optimal);
            using (var cs = colorset.Open())
            {
                var xml = BuildColorsetXml(swatches, context?.PaletteName ?? "Palette");
                var b = Encoding.UTF8.GetBytes(xml);
                cs.Write(b, 0, b.Length);
            }
        }

        private static string BuildColorsetXml(IReadOnlyList<(byte R, byte G, byte B)> swatches, string name)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<ColorSet name=\"{System.Net.WebUtility.HtmlEncode(name)}\" version=\"1.0\" rows=\"{swatches.Count}\" columns=\"1\">");
            for (int i = 0; i < swatches.Count; i++)
            {
                var c = swatches[i];
                double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
                sb.AppendLine("  <ColorSetEntry spot=\"false\" id=\"\" bitdepth=\"U8\" name=\"\">");
                sb.Append("    <sRGB r=\"").Append(F(r)).Append("\" g=\"").Append(F(g)).Append("\" b=\"").Append(F(b)).AppendLine("\"/>");
                sb.AppendLine($"    <Position row=\"{i}\" column=\"0\"/>");
                sb.AppendLine("  </ColorSetEntry>");
            }
            sb.AppendLine("</ColorSet>");
            return sb.ToString();
        }

        private static string F(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
