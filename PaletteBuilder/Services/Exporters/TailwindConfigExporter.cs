// Services/Exporters/TailwindConfigExporter.cs
//
// A Tailwind v3 colors fragment. User pastes into theme.extend.colors of
// their tailwind.config.js.

using System.Collections.Generic;
using System.IO;
using System.Text;
using FracturingFog.Imaging;

namespace PaletteBuilder.Services.Exporters
{
    public sealed class TailwindConfigExporter : IPaletteExporter
    {
        public string Id => "tailwind";
        public string DisplayName => "Tailwind colors snippet";
        public string Extension => "js";

        public void Export(string path,
                           IReadOnlyList<(byte R, byte G, byte B)> swatches,
                           IReadOnlyList<PaletteStop>? stops = null,
                           PaletteExportContext? context = null)
        {
            string key = SafeKey(context?.PaletteName ?? "palette");
            var sb = new StringBuilder();
            sb.AppendLine("// Tailwind v3 — paste into theme.extend.colors");
            sb.AppendLine("module.exports = {");
            sb.AppendLine($"  {key}: {{");
            for (int i = 0; i < swatches.Count; i++)
            {
                var c = swatches[i];
                string sep = i == swatches.Count - 1 ? "" : ",";
                sb.AppendLine($"    \"{(i + 1):00}\": \"#{c.R:X2}{c.G:X2}{c.B:X2}\"{sep}");
            }
            sb.AppendLine("  }");
            sb.AppendLine("};");
            File.WriteAllText(path, sb.ToString());
        }

        private static string SafeKey(string input)
        {
            var sb = new StringBuilder();
            foreach (var ch in input.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            string k = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(k) ? "palette" : k;
        }
    }
}
