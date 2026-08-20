// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/GltfMeshWriter.cs
//
// Roadmap slice S9.4 (3D-Rendering-Roadmap.md §S9, #391) — carry the MATERIAL.
// STL is dumb triangles, PLY carries vertex colour, but neither lands the mesh in
// Blender / a web viewer / three.js already dressed with a shaded material. glTF
// 2.0 is the format built for that: a PBR metallic-roughness material travels with
// the geometry, so the export opens shaded, not grey clay. This is a self-contained
// glTF 2.0 writer (no dependency) emitting either:
//   • .glb  — the single-file BINARY container (12-byte header + JSON chunk + BIN
//     chunk); the drop-in format (one file, no sidecar), preferred.
//   • .gltf — JSON with the buffer inlined as a base64 data: URI (still one file).
//
// A single interleaved-free binary buffer holds POSITION (float VEC3), NORMAL
// (float VEC3), optional COLOR_0 (normalized UNSIGNED_BYTE VEC4), then the index
// triples (UNSIGNED_INT SCALAR), each section already 4-byte aligned. One mesh, one
// primitive (mode 4 = TRIANGLES), one pbrMetallicRoughness material. When per-vertex
// colour is supplied the material base colour is left white so the vertex colours
// multiply through (glTF COLOR_0 semantics); otherwise the material's flat base
// colour dresses the whole solid.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FracturingFog.Export;

/// <summary>Self-contained glTF 2.0 (.glb / .gltf) writer with a PBR
/// metallic-roughness material and optional per-vertex COLOR_0 (roadmap S9.4,
/// #391).</summary>
public static class GltfMeshWriter
{
    /// <summary>PBR metallic-roughness material. BaseColor is the flat albedo used
    /// when there is no per-vertex colour; with vertex colour present keep it white
    /// (1,1,1,1) so COLOR_0 multiplies through unchanged. Metallic 0 + Roughness
    /// ~0.8 reads as a matte print-like surface.</summary>
    public readonly record struct PbrMaterial(
        float BaseR, float BaseG, float BaseB, float BaseA, float Metallic, float Roughness)
    {
        public static PbrMaterial MatteWhite => new(1f, 1f, 1f, 1f, 0f, 0.8f);
        public static PbrMaterial Matte(float r, float g, float b) => new(r, g, b, 1f, 0f, 0.8f);
    }

    /// <summary>Write positions + normals + optional per-vertex ARGB colour +
    /// triangles to <paramref name="path"/>. Dispatches on extension: `.gltf` →
    /// JSON with a base64 buffer; anything else (`.glb`) → the binary container.
    /// <paramref name="colorsArgb"/> may be null (no COLOR_0); its alpha is ignored
    /// (exported opaque so a solid does not come out see-through).</summary>
    public static void Write(
        string path,
        IReadOnlyList<(double X, double Y, double Z)> positions,
        IReadOnlyList<(float X, float Y, float Z)>? normals,
        IReadOnlyList<uint>? colorsArgb,
        IReadOnlyList<(int A, int B, int C)> triangles,
        PbrMaterial material)
    {
        bool binary = !path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);
        int v = positions.Count;
        bool hasColor = colorsArgb != null && colorsArgb.Count == v;
        bool hasNormal = normals != null && normals.Count == v;

        // ── Binary buffer: POSITION | NORMAL | COLOR_0 | INDICES ────────────────
        // Each section length is a multiple of 4 (12·v, 12·v, 4·v, 4·3·t), so the
        // running offsets are all 4-aligned with no interior padding.
        int posLen = 12 * v;
        int normLen = hasNormal ? 12 * v : 0;
        int colLen = hasColor ? 4 * v : 0;
        int idxCount = 3 * triangles.Count;
        int idxLen = 4 * idxCount;

        int posOff = 0;
        int normOff = posOff + posLen;
        int colOff = normOff + normLen;
        int idxOff = colOff + colLen;
        int bufLen = idxOff + idxLen;

        var buffer = new byte[bufLen];
        // POSITION (float VEC3) + min/max for the required accessor bounds.
        float minx = float.PositiveInfinity, miny = float.PositiveInfinity, minz = float.PositiveInfinity;
        float maxx = float.NegativeInfinity, maxy = float.NegativeInfinity, maxz = float.NegativeInfinity;
        for (int i = 0; i < v; i++)
        {
            float px = (float)positions[i].X, py = (float)positions[i].Y, pz = (float)positions[i].Z;
            WriteF(buffer, posOff + i * 12 + 0, px);
            WriteF(buffer, posOff + i * 12 + 4, py);
            WriteF(buffer, posOff + i * 12 + 8, pz);
            if (px < minx) minx = px; if (py < miny) miny = py; if (pz < minz) minz = pz;
            if (px > maxx) maxx = px; if (py > maxy) maxy = py; if (pz > maxz) maxz = pz;
        }
        if (v == 0) { minx = miny = minz = maxx = maxy = maxz = 0f; }
        if (hasNormal)
            for (int i = 0; i < v; i++)
            {
                WriteF(buffer, normOff + i * 12 + 0, normals![i].X);
                WriteF(buffer, normOff + i * 12 + 4, normals![i].Y);
                WriteF(buffer, normOff + i * 12 + 8, normals![i].Z);
            }
        if (hasColor)
            for (int i = 0; i < v; i++)
            {
                uint c = colorsArgb![i];
                buffer[colOff + i * 4 + 0] = (byte)((c >> 16) & 0xFF); // R
                buffer[colOff + i * 4 + 1] = (byte)((c >> 8) & 0xFF);  // G
                buffer[colOff + i * 4 + 2] = (byte)(c & 0xFF);         // B
                buffer[colOff + i * 4 + 3] = 0xFF;                     // A (opaque solid)
            }
        {
            int o = idxOff;
            foreach (var t in triangles)
            {
                WriteU(buffer, o + 0, (uint)t.A);
                WriteU(buffer, o + 4, (uint)t.B);
                WriteU(buffer, o + 8, (uint)t.C);
                o += 12;
            }
        }

        // ── JSON ────────────────────────────────────────────────────────────────
        string json = BuildJson(v, idxCount, hasNormal, hasColor,
            posOff, posLen, normOff, normLen, colOff, colLen, idxOff, idxLen,
            minx, miny, minz, maxx, maxy, maxz, material, bufLen, binary, buffer);

        if (binary) WriteGlb(path, json, buffer);
        else File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static string BuildJson(
        int v, int idxCount, bool hasNormal, bool hasColor,
        int posOff, int posLen, int normOff, int normLen, int colOff, int colLen,
        int idxOff, int idxLen,
        float minx, float miny, float minz, float maxx, float maxy, float maxz,
        PbrMaterial mat, int bufLen, bool binary, byte[] buffer)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(2048);
        sb.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"FracturingFog\"},");
        sb.Append("\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}],");

        // Accessor indexing: 0 POSITION, 1 NORMAL(opt), 2 COLOR_0(opt), last = indices.
        int acc = 0, posAcc = acc++;
        int normAcc = hasNormal ? acc++ : -1;
        int colAcc = hasColor ? acc++ : -1;
        int idxAcc = acc++;

        sb.Append("\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":").Append(posAcc);
        if (hasNormal) sb.Append(",\"NORMAL\":").Append(normAcc);
        if (hasColor) sb.Append(",\"COLOR_0\":").Append(colAcc);
        sb.Append("},\"indices\":").Append(idxAcc).Append(",\"material\":0,\"mode\":4}]}],");

        // Material — metallic-roughness. doubleSided so back faces are lit too
        // (prints/relief seen from behind don't go black).
        sb.Append("\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorFactor\":[")
          .Append(F(mat.BaseR)).Append(',').Append(F(mat.BaseG)).Append(',')
          .Append(F(mat.BaseB)).Append(',').Append(F(mat.BaseA))
          .Append("],\"metallicFactor\":").Append(F(mat.Metallic))
          .Append(",\"roughnessFactor\":").Append(F(mat.Roughness))
          .Append("},\"doubleSided\":true,\"name\":\"FracturingFog PBR\"}],");

        // bufferViews: 0 POSITION,1 NORMAL(opt),2 COLOR_0(opt),3 indices.
        int bv = 0;
        int posBv = bv++, normBv = hasNormal ? bv++ : -1, colBv = hasColor ? bv++ : -1, idxBv = bv++;
        sb.Append("\"bufferViews\":[");
        sb.Append("{\"buffer\":0,\"byteOffset\":").Append(posOff).Append(",\"byteLength\":").Append(posLen).Append(",\"target\":34962}");
        if (hasNormal) sb.Append(",{\"buffer\":0,\"byteOffset\":").Append(normOff).Append(",\"byteLength\":").Append(normLen).Append(",\"target\":34962}");
        if (hasColor) sb.Append(",{\"buffer\":0,\"byteOffset\":").Append(colOff).Append(",\"byteLength\":").Append(colLen).Append(",\"target\":34962}");
        sb.Append(",{\"buffer\":0,\"byteOffset\":").Append(idxOff).Append(",\"byteLength\":").Append(idxLen).Append(",\"target\":34963}");
        sb.Append("],");

        // accessors
        sb.Append("\"accessors\":[");
        // POSITION (with required min/max)
        sb.Append("{\"bufferView\":").Append(posBv)
          .Append(",\"componentType\":5126,\"count\":").Append(v)
          .Append(",\"type\":\"VEC3\",\"min\":[").Append(F(minx)).Append(',').Append(F(miny)).Append(',').Append(F(minz))
          .Append("],\"max\":[").Append(F(maxx)).Append(',').Append(F(maxy)).Append(',').Append(F(maxz)).Append("]}");
        if (hasNormal)
            sb.Append(",{\"bufferView\":").Append(normBv)
              .Append(",\"componentType\":5126,\"count\":").Append(v).Append(",\"type\":\"VEC3\"}");
        if (hasColor)
            sb.Append(",{\"bufferView\":").Append(colBv)
              .Append(",\"componentType\":5121,\"normalized\":true,\"count\":").Append(v).Append(",\"type\":\"VEC4\"}");
        sb.Append(",{\"bufferView\":").Append(idxBv)
          .Append(",\"componentType\":5125,\"count\":").Append(idxCount).Append(",\"type\":\"SCALAR\"}");
        sb.Append("],");

        // buffer — GLB references the BIN chunk (no uri); .gltf inlines base64.
        sb.Append("\"buffers\":[{\"byteLength\":").Append(bufLen);
        if (!binary)
            sb.Append(",\"uri\":\"data:application/octet-stream;base64,")
              .Append(Convert.ToBase64String(buffer)).Append('"');
        sb.Append("}]}");
        return sb.ToString();

        string F(float x) => x.ToString("R", ci);
    }

    // GLB container: header (12) + JSON chunk + BIN chunk. Chunks are 4-aligned —
    // JSON padded with spaces (0x20), BIN with zeros (0x00).
    private static void WriteGlb(string path, string json, byte[] bin)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - (jsonBytes.Length & 3)) & 3;
        int binPad = (4 - (bin.Length & 3)) & 3;
        int jsonChunkLen = jsonBytes.Length + jsonPad;
        int binChunkLen = bin.Length + binPad;
        int total = 12 + 8 + jsonChunkLen + 8 + binChunkLen;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(0x46546C67u);            // "glTF"
        bw.Write(2u);                     // version
        bw.Write((uint)total);            // total length
        // JSON chunk
        bw.Write((uint)jsonChunkLen);
        bw.Write(0x4E4F534Au);            // "JSON"
        bw.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) bw.Write((byte)0x20);
        // BIN chunk
        bw.Write((uint)binChunkLen);
        bw.Write(0x004E4942u);            // "BIN\0"
        bw.Write(bin);
        for (int i = 0; i < binPad; i++) bw.Write((byte)0x00);
    }

    private static void WriteF(byte[] b, int off, float f)
    {
        uint u = BitConverter.SingleToUInt32Bits(f);
        b[off + 0] = (byte)u; b[off + 1] = (byte)(u >> 8);
        b[off + 2] = (byte)(u >> 16); b[off + 3] = (byte)(u >> 24);
    }

    private static void WriteU(byte[] b, int off, uint u)
    {
        b[off + 0] = (byte)u; b[off + 1] = (byte)(u >> 8);
        b[off + 2] = (byte)(u >> 16); b[off + 3] = (byte)(u >> 24);
    }
}
