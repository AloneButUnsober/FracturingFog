// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/PlyMeshReader.cs
//
// Roadmap slice S9.3 (3D-Rendering-Roadmap.md §S9, #391) — read a binary
// little-endian PLY back into positions + per-vertex colour + index triples, so
// the vertex-colour export (HeightfieldMeshExporter.WritePly) can be validated
// end-to-end: the geometry through MeshValidator (still watertight / manifold /
// outward) and the colours as the theme actually baked in. Parses the header
// property list generically (float/double/int/uint/short/ushort/uchar/char) so it
// reads a standard PLY, then streams the binary body.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FracturingFog.Export;

/// <summary>Minimal binary-little-endian PLY reader — positions, per-vertex RGB
/// (0 when absent), and index triples out — for validating a vertex-colour mesh
/// export (roadmap S9.3, #391). Triangulates a polygon face into a fan.</summary>
public static class PlyMeshReader
{
    private readonly record struct Prop(string Name, int Size, bool IsList, int ListCountSize, int ListItemSize);

    public static (List<(double X, double Y, double Z)> positions,
                   List<(byte R, byte G, byte B)> colors,
                   List<(int A, int B, int C)> triangles)
        ReadBinary(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);

        // ── Header (ASCII lines up to and including "end_header\n") ──────────────
        var headerText = new StringBuilder();
        int b;
        while ((b = fs.ReadByte()) >= 0)
        {
            headerText.Append((char)b);
            if (b == (int)'\n' && headerText.ToString().EndsWith("end_header\n", StringComparison.Ordinal))
                break;
        }
        var lines = headerText.ToString().Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "ply")
            throw new InvalidDataException("PLY: missing magic.");

        int vertexCount = 0, faceCount = 0;
        var vertexProps = new List<Prop>();
        Prop faceProp = default;
        string current = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var tok = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (tok[0])
            {
                case "format":
                    if (tok.Length < 2 || tok[1] != "binary_little_endian")
                        throw new NotSupportedException($"PLY: only binary_little_endian supported (got {(tok.Length > 1 ? tok[1] : "?")}).");
                    break;
                case "element":
                    current = tok[1];
                    if (current == "vertex") vertexCount = int.Parse(tok[2]);
                    else if (current == "face") faceCount = int.Parse(tok[2]);
                    break;
                case "property":
                    if (tok[1] == "list")
                    {
                        if (current == "face")
                            faceProp = new Prop(tok[4], 0, true, TypeSize(tok[2]), TypeSize(tok[3]));
                    }
                    else if (current == "vertex")
                    {
                        vertexProps.Add(new Prop(tok[2], TypeSize(tok[1]), false, 0, 0));
                    }
                    break;
            }
        }

        // Locate x/y/z + red/green/blue offsets in the vertex record.
        int stride = 0, ox = -1, oy = -1, oz = -1, or_ = -1, og = -1, ob = -1;
        var offsets = new Dictionary<string, int>();
        foreach (var p in vertexProps) { offsets[p.Name] = stride; stride += p.Size; }
        offsets.TryGetValue("x", out ox); offsets.TryGetValue("y", out oy); offsets.TryGetValue("z", out oz);
        bool hasColor = offsets.TryGetValue("red", out or_) & offsets.TryGetValue("green", out og) & offsets.TryGetValue("blue", out ob);
        if (ox < 0 || oy < 0 || oz < 0) throw new InvalidDataException("PLY: vertex is missing x/y/z.");

        using var br = new BinaryReader(fs);
        var positions = new List<(double, double, double)>(vertexCount);
        var colors = new List<(byte, byte, byte)>(vertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            byte[] rec = br.ReadBytes(stride);
            if (rec.Length < stride) throw new EndOfStreamException("PLY: truncated vertex data.");
            positions.Add((BitConverter.ToSingle(rec, ox), BitConverter.ToSingle(rec, oy), BitConverter.ToSingle(rec, oz)));
            colors.Add(hasColor ? (rec[or_], rec[og], rec[ob]) : ((byte)0, (byte)0, (byte)0));
        }

        var tris = new List<(int, int, int)>(faceCount);
        Span<int> scratch = stackalloc int[32];   // hoisted out of the loop
        for (int i = 0; i < faceCount; i++)
        {
            int count = ReadIndex(br, faceProp.ListCountSize);
            Span<int> idx = count <= scratch.Length ? scratch.Slice(0, count) : new int[count];
            for (int j = 0; j < count; j++) idx[j] = ReadIndex(br, faceProp.ListItemSize);
            for (int j = 1; j + 1 < count; j++) tris.Add((idx[0], idx[j], idx[j + 1]));   // fan-triangulate
        }
        return (positions, colors, tris);
    }

    private static int TypeSize(string t) => t switch
    {
        "char" or "int8" or "uchar" or "uint8" => 1,
        "short" or "int16" or "ushort" or "uint16" => 2,
        "int" or "int32" or "uint" or "uint32" or "float" or "float32" => 4,
        "double" or "float64" => 8,
        _ => throw new NotSupportedException($"PLY: unsupported property type '{t}'.")
    };

    private static int ReadIndex(BinaryReader br, int size) => size switch
    {
        1 => br.ReadByte(),
        2 => br.ReadUInt16(),
        4 => br.ReadInt32(),
        _ => throw new NotSupportedException($"PLY: unsupported index size {size}.")
    };
}
