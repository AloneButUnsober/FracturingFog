// Services/Exporters/AdobeAseExporter.cs
//
// Adobe Swatch Exchange (.ase) — binary. Documented at
// https://www.cyotek.com/blog/writing-adobe-swatch-exchange-ase-files-using-csharp
//
// File layout (big-endian throughout):
//   "ASEF" magic (4 bytes)
//   uint16 versionMajor = 1, uint16 versionMinor = 0
//   uint32 blockCount    = number of colour blocks (we don't emit groups)
//   per block:
//     uint16 type    = 0x0001  (colour entry)
//     uint32 length  = bytes of remaining block body
//     uint16 nameLen = char count + 1 (terminator)
//     UTF-16BE chars (nameLen × 2 bytes) including trailing 0x0000
//     "RGB " model (4 ASCII bytes)
//     float32 R, float32 G, float32 B    (each in [0,1])
//     uint16 type    = 0x0000  (Global)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FracturingFog.Imaging;

namespace PaletteBuilder.Services.Exporters
{
    public sealed class AdobeAseExporter : IPaletteExporter
    {
        public string Id => "adobe-ase";
        public string DisplayName => "Adobe swatch (.ase)";
        public string Extension => "ase";

        public void Export(string path,
                           IReadOnlyList<(byte R, byte G, byte B)> swatches,
                           IReadOnlyList<PaletteStop>? stops = null,
                           PaletteExportContext? context = null)
        {
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);

            bw.Write(new[] { (byte)'A', (byte)'S', (byte)'E', (byte)'F' });
            WriteUInt16Be(bw, 1);
            WriteUInt16Be(bw, 0);
            WriteUInt32Be(bw, (uint)swatches.Count);

            for (int i = 0; i < swatches.Count; i++)
            {
                var c = swatches[i];
                string name = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                byte[] nameUtf16 = Encoding.BigEndianUnicode.GetBytes(name);
                int nameLen = (nameUtf16.Length / 2) + 1;     // +1 for null
                // Block body: 2(nameLen) + nameUtf16 + 2(null) + 4(model) + 12(RGB floats) + 2(type)
                int bodyLen = 2 + nameUtf16.Length + 2 + 4 + 12 + 2;

                WriteUInt16Be(bw, 0x0001);
                WriteUInt32Be(bw, (uint)bodyLen);
                WriteUInt16Be(bw, (ushort)nameLen);
                bw.Write(nameUtf16);
                bw.Write((byte)0); bw.Write((byte)0);          // UTF-16 null terminator
                bw.Write(new[] { (byte)'R', (byte)'G', (byte)'B', (byte)' ' });
                WriteFloatBe(bw, c.R / 255f);
                WriteFloatBe(bw, c.G / 255f);
                WriteFloatBe(bw, c.B / 255f);
                WriteUInt16Be(bw, 0x0000);                     // Global colour type
            }
        }

        private static void WriteUInt16Be(BinaryWriter bw, ushort v)
        {
            bw.Write((byte)(v >> 8));
            bw.Write((byte)v);
        }

        private static void WriteUInt32Be(BinaryWriter bw, uint v)
        {
            bw.Write((byte)(v >> 24));
            bw.Write((byte)(v >> 16));
            bw.Write((byte)(v >> 8));
            bw.Write((byte)v);
        }

        private static void WriteFloatBe(BinaryWriter bw, float v)
        {
            byte[] le = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(le);
            bw.Write(le);
        }
    }
}
