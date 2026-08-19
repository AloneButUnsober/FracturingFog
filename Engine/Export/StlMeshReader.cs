// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/StlMeshReader.cs
//
// Roadmap slice S9.1 (3D-Rendering-Roadmap.md §S9, #391) — read a binary STL
// back into a triangle soup so any exported mesh FILE (not just an in-memory
// build) can be fed to MeshValidator. Binary STL is a flat triangle soup: an
// 80-byte header, a uint32 triangle count, then per triangle 12 little-endian
// float32 (face normal + 3 corners) and a uint16 attribute word. Vertices are
// unshared — the validator welds them — so the reader emits 3 fresh positions
// per triangle and the trivial index triples into them.

using System;
using System.Collections.Generic;
using System.IO;

namespace FracturingFog.Export;

/// <summary>Minimal binary-STL reader — triangle soup in, positions + index
/// triples out, for feeding an exported mesh file to <see cref="MeshValidator"/>
/// (roadmap S9.1, #391).</summary>
public static class StlMeshReader
{
    public static (List<(double X, double Y, double Z)> positions, List<(int A, int B, int C)> triangles)
        ReadBinary(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);
        return ReadBinary(br);
    }

    public static (List<(double X, double Y, double Z)> positions, List<(int A, int B, int C)> triangles)
        ReadBinary(BinaryReader br)
    {
        if (br == null) throw new ArgumentNullException(nameof(br));
        br.ReadBytes(80);                       // header
        uint count = br.ReadUInt32();

        var positions = new List<(double, double, double)>((int)Math.Min(count, int.MaxValue / 3) * 3);
        var tris = new List<(int, int, int)>((int)Math.Min(count, int.MaxValue));
        for (uint t = 0; t < count; t++)
        {
            br.ReadSingle(); br.ReadSingle(); br.ReadSingle();   // face normal (ignored)
            int baseIdx = positions.Count;
            for (int v = 0; v < 3; v++)
            {
                float x = br.ReadSingle(), y = br.ReadSingle(), z = br.ReadSingle();
                positions.Add((x, y, z));
            }
            br.ReadUInt16();                    // attribute byte count
            tris.Add((baseIdx, baseIdx + 1, baseIdx + 2));
        }
        return (positions, tris);
    }
}
