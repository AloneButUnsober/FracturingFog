// Services/Exporters/GimpGplExporter.cs
//
// GIMP Palette format — plain text. Header:
//   GIMP Palette
//   Name: <palette name>
//   Columns: 0
//   #
// followed by one swatch per line:
//   <r> <g> <b>  <name>

using System.Collections.Generic;
using System.IO;
using System.Text;
using FracturingFog.Imaging;

namespace PaletteBuilder.Services.Exporters
{
    public sealed class GimpGplExporter : IPaletteExporter
    {
        public string Id => "gimp-gpl";
        public string DisplayName => "GIMP palette (.gpl)";
        public string Extension => "gpl";

        public void Export(string path,
                           IReadOnlyList<(byte R, byte G, byte B)> swatches,
                           IReadOnlyList<PaletteStop>? stops = null,
                           PaletteExportContext? context = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("GIMP Palette");
            sb.AppendLine("Name: " + (context?.PaletteName ?? "Palette"));
            sb.AppendLine("Columns: 0");
            sb.AppendLine("#");
            foreach (var c in swatches)
            {
                string name = ColorNamer.Nearest(c.R, c.G, c.B);
                sb.AppendLine($"{c.R,3} {c.G,3} {c.B,3}  {name}");
            }
            File.WriteAllText(path, sb.ToString());
        }
    }
}
