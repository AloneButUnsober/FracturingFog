// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/ThreeMfMeshWriter.cs
//
// Roadmap slice S9 (3D-Rendering-Roadmap.md §S9, #391) — 3MF export: the format
// colour 3D-printers and modern slicers (PrusaSlicer, Bambu, Cura, Windows 3D
// Builder) prefer over STL. It carries what STL cannot: real PRINT UNITS (an
// explicit millimetre model unit, so the slicer imports at a defined scale instead
// of guessing) and PER-VERTEX COLOUR (via the 3MF Materials & Properties colour
// group), on a watertight solid. Completes FF's colour-carry format matrix — STL
// (geometry only), PLY (vertex colour), glTF/GLB (vertex colour + PBR material),
// 3MF (vertex colour + units for print).
//
// A 3MF file is an OPC (Open Packaging Conventions) ZIP holding three parts:
//   • [Content_Types].xml — declares the .rels and .model content types.
//   • _rels/.rels         — the package relationship to the 3D model part.
//   • 3D/3dmodel.model    — the XML mesh (vertices + triangles + optional colours).
// Per-vertex colour is emitted as an <m:colorgroup> of DISTINCT colours; each
// triangle references one colour index per corner (p1/p2/p3), so the gradient is
// carried without duplicating shared colours.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FracturingFog.Export;

/// <summary>Self-contained 3MF (.3mf) writer — a watertight solid with a millimetre
/// print unit and optional per-vertex colour (roadmap S9, #391).</summary>
public static class ThreeMfMeshWriter
{
    private const string CoreNs = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private const string MatNs = "http://schemas.microsoft.com/3dmanufacturing/material/2015/02";

    /// <summary>Write positions + optional per-vertex ARGB colour + triangles to a
    /// 3MF package at <paramref name="path"/>. <paramref name="unit"/> is the model
    /// unit string (default "millimeter"); colour alpha is ignored (printed opaque).
    /// </summary>
    public static void Write(
        string path,
        IReadOnlyList<(double X, double Y, double Z)> positions,
        IReadOnlyList<uint>? colorsArgb,
        IReadOnlyList<(int A, int B, int C)> triangles,
        string unit = "millimeter")
    {
        bool hasColor = colorsArgb != null && colorsArgb.Count == positions.Count;

        // Distinct-colour palette + per-vertex index into it (shared colours fold).
        var palette = new List<uint>();
        int[]? colorIndex = null;
        if (hasColor)
        {
            var map = new Dictionary<uint, int>();
            colorIndex = new int[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                uint c = colorsArgb![i] | 0xFF000000u;   // opaque
                if (!map.TryGetValue(c, out int idx))
                {
                    idx = palette.Count;
                    palette.Add(c);
                    map[c] = idx;
                }
                colorIndex[i] = idx;
            }
        }

        string model = BuildModelXml(positions, triangles, unit, hasColor, palette, colorIndex);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        WriteEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/>" +
            "</Types>");
        WriteEntry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" " +
            "Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/>" +
            "</Relationships>");
        WriteEntry(zip, "3D/3dmodel.model", model);
    }

    private static string BuildModelXml(
        IReadOnlyList<(double X, double Y, double Z)> pos,
        IReadOnlyList<(int A, int B, int C)> tris,
        string unit, bool hasColor, List<uint> palette, int[]? colorIndex)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(pos.Count * 48 + tris.Count * 40 + 512);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n");
        sb.Append("<model unit=\"").Append(unit).Append("\" xml:lang=\"en-US\" xmlns=\"").Append(CoreNs).Append('"');
        if (hasColor) sb.Append(" xmlns:m=\"").Append(MatNs).Append('"');
        sb.Append("><resources>");

        // Colour group (resource id 1) before the object that references it.
        if (hasColor)
        {
            sb.Append("<m:colorgroup id=\"1\">");
            foreach (uint c in palette)
                sb.Append("<m:color color=\"").Append(HexRgba(c)).Append("\"/>");
            sb.Append("</m:colorgroup>");
        }

        // Object (id 2). pid/pindex give it a default colour (first palette entry).
        sb.Append("<object id=\"2\" type=\"model\"");
        if (hasColor) sb.Append(" pid=\"1\" pindex=\"0\"");
        sb.Append("><mesh><vertices>");
        foreach (var v in pos)
            sb.Append("<vertex x=\"").Append(v.X.ToString("0.######", ci))
              .Append("\" y=\"").Append(v.Y.ToString("0.######", ci))
              .Append("\" z=\"").Append(v.Z.ToString("0.######", ci)).Append("\"/>");
        sb.Append("</vertices><triangles>");
        foreach (var t in tris)
        {
            sb.Append("<triangle v1=\"").Append(t.A).Append("\" v2=\"").Append(t.B)
              .Append("\" v3=\"").Append(t.C).Append('"');
            if (hasColor && colorIndex != null)
                sb.Append(" pid=\"1\" p1=\"").Append(colorIndex[t.A])
                  .Append("\" p2=\"").Append(colorIndex[t.B])
                  .Append("\" p3=\"").Append(colorIndex[t.C]).Append('"');
            sb.Append("/>");
        }
        sb.Append("</triangles></mesh></object></resources>");
        sb.Append("<build><item objectid=\"2\"/></build></model>");
        return sb.ToString();
    }

    private static string HexRgba(uint argb)
    {
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        return $"#{r:X2}{g:X2}{b:X2}{a:X2}";
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }
}
