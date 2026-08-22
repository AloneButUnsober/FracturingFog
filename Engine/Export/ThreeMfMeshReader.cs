// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/ThreeMfMeshReader.cs
//
// Roadmap slice S9 (3D-Rendering-Roadmap.md §S9, #391) — read a 3MF package back
// into positions + optional per-vertex colour + index triples, so the 3MF export
// (ThreeMfMeshWriter) can be validated end-to-end: geometry through MeshValidator
// (still watertight / manifold / outward) and the colours as the theme baked in.
// Opens the OPC ZIP, parses 3D/3dmodel.model, resolves each triangle's per-corner
// colour index (p1/p2/p3) against the <m:colorgroup>. Also exposes the model unit.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace FracturingFog.Export;

/// <summary>Minimal 3MF reader — positions, per-vertex RGB (0 when absent), index
/// triples, and the model unit — for validating a 3MF export (roadmap S9, #391).</summary>
public static class ThreeMfMeshReader
{
    public static (List<(double X, double Y, double Z)> positions,
                   List<(byte R, byte G, byte B)> colors,
                   List<(int A, int B, int C)> triangles,
                   string unit)
        Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var modelEntry = zip.GetEntry("3D/3dmodel.model")
            ?? throw new InvalidDataException("3MF: missing 3D/3dmodel.model part.");

        XDocument doc;
        using (var s = modelEntry.Open()) doc = XDocument.Load(s);

        var model = doc.Root ?? throw new InvalidDataException("3MF: empty model.");
        XNamespace core = model.Name.Namespace;
        string unit = (string?)model.Attribute("unit") ?? "millimeter";
        XNamespace mat = "http://schemas.microsoft.com/3dmanufacturing/material/2015/02";

        var resources = model.Element(core + "resources")
            ?? throw new InvalidDataException("3MF: missing resources.");

        // Colour palette (first colorgroup) → RGB list, indexed by p#.
        var palette = new List<(byte R, byte G, byte B)>();
        var colorgroup = resources.Elements(mat + "colorgroup").FirstOrDefault();
        if (colorgroup != null)
            foreach (var c in colorgroup.Elements(mat + "color"))
                palette.Add(ParseHex((string?)c.Attribute("color")));

        var obj = resources.Elements(core + "object").FirstOrDefault()
            ?? throw new InvalidDataException("3MF: no object.");
        var mesh = obj.Element(core + "mesh")
            ?? throw new InvalidDataException("3MF: object has no mesh.");

        var positions = new List<(double, double, double)>();
        foreach (var v in mesh.Element(core + "vertices")!.Elements(core + "vertex"))
            positions.Add((
                double.Parse((string)v.Attribute("x")!, CultureInfo.InvariantCulture),
                double.Parse((string)v.Attribute("y")!, CultureInfo.InvariantCulture),
                double.Parse((string)v.Attribute("z")!, CultureInfo.InvariantCulture)));

        var triangles = new List<(int, int, int)>();
        var perVertexColor = new (byte R, byte G, byte B)[positions.Count];
        bool anyColor = false;
        foreach (var t in mesh.Element(core + "triangles")!.Elements(core + "triangle"))
        {
            int a = int.Parse((string)t.Attribute("v1")!, CultureInfo.InvariantCulture);
            int b = int.Parse((string)t.Attribute("v2")!, CultureInfo.InvariantCulture);
            int c = int.Parse((string)t.Attribute("v3")!, CultureInfo.InvariantCulture);
            triangles.Add((a, b, c));
            if (palette.Count > 0)
            {
                AssignColor(perVertexColor, palette, a, (string?)t.Attribute("p1"), ref anyColor);
                AssignColor(perVertexColor, palette, b, (string?)t.Attribute("p2"), ref anyColor);
                AssignColor(perVertexColor, palette, c, (string?)t.Attribute("p3"), ref anyColor);
            }
        }

        var colors = new List<(byte, byte, byte)>(positions.Count);
        for (int i = 0; i < positions.Count; i++) colors.Add(perVertexColor[i]);
        return (positions, colors, triangles, unit);
    }

    private static void AssignColor(
        (byte R, byte G, byte B)[] dst, List<(byte R, byte G, byte B)> palette,
        int vertex, string? pAttr, ref bool anyColor)
    {
        if (pAttr == null) return;
        if (!int.TryParse(pAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pi)) return;
        if (pi < 0 || pi >= palette.Count) return;
        dst[vertex] = palette[pi];
        anyColor = true;
    }

    private static (byte, byte, byte) ParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length < 7) return (0, 0, 0);
        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte b = Convert.ToByte(hex.Substring(5, 2), 16);
        return (r, g, b);
    }
}
