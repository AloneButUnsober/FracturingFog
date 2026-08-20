// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/GltfMeshReader.cs
//
// Roadmap slice S9.4 (3D-Rendering-Roadmap.md §S9, #391) — read a glTF 2.0 file
// (.glb binary container or .gltf with an embedded base64 / data-URI buffer) back
// into positions + optional per-vertex COLOR_0 + index triples, so the glTF export
// (GltfMeshWriter) can be validated end-to-end: geometry through MeshValidator
// (still watertight / manifold / outward) and the colours as the theme baked in.
// Reads the single-mesh / single-primitive layout the writer emits; honours each
// accessor's bufferView byteOffset and the index componentType (ubyte/ushort/uint).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FracturingFog.Export;

/// <summary>Minimal glTF 2.0 reader — POSITION, optional COLOR_0 (as 0-255 RGB),
/// and index triples out — for validating a glTF export (roadmap S9.4, #391).</summary>
public static class GltfMeshReader
{
    public static (List<(double X, double Y, double Z)> positions,
                   List<(byte R, byte G, byte B)> colors,
                   List<(int A, int B, int C)> triangles)
        Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string json;
        byte[] bin;

        if (bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 0) == 0x46546C67u)
        {
            // GLB: header(12) then chunks [len|type|data].
            (json, bin) = ParseGlb(bytes);
        }
        else
        {
            json = Encoding.UTF8.GetString(bytes);
            bin = Array.Empty<byte>();
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Resolve the buffer: GLB → the BIN chunk; .gltf → the base64 data URI.
        var buffers = root.GetProperty("buffers");
        byte[] buf = bin;
        if (buf.Length == 0 && buffers.GetArrayLength() > 0)
        {
            var b0 = buffers[0];
            if (b0.TryGetProperty("uri", out var uri))
                buf = DecodeDataUri(uri.GetString()!);
        }

        var bufferViews = root.GetProperty("bufferViews");
        var accessors = root.GetProperty("accessors");

        var prim = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var attrs = prim.GetProperty("attributes");

        int posAcc = attrs.GetProperty("POSITION").GetInt32();
        int idxAcc = prim.GetProperty("indices").GetInt32();
        int colAcc = attrs.TryGetProperty("COLOR_0", out var ca) ? ca.GetInt32() : -1;

        var positions = ReadVec3(accessors, bufferViews, buf, posAcc);

        var colors = new List<(byte, byte, byte)>();
        if (colAcc >= 0) colors = ReadColor(accessors, bufferViews, buf, colAcc);
        else for (int i = 0; i < positions.Count; i++) colors.Add(((byte)0, (byte)0, (byte)0));

        var flat = ReadIndices(accessors, bufferViews, buf, idxAcc);
        var tris = new List<(int, int, int)>(flat.Count / 3);
        for (int i = 0; i + 2 < flat.Count; i += 3) tris.Add((flat[i], flat[i + 1], flat[i + 2]));

        return (positions, colors, tris);
    }

    private static (string json, byte[] bin) ParseGlb(byte[] b)
    {
        string json = "";
        byte[] bin = Array.Empty<byte>();
        int off = 12;
        while (off + 8 <= b.Length)
        {
            uint len = BitConverter.ToUInt32(b, off);
            uint type = BitConverter.ToUInt32(b, off + 4);
            int dataOff = off + 8;
            if (dataOff + (int)len > b.Length) break;
            if (type == 0x4E4F534Au)          // JSON
                json = Encoding.UTF8.GetString(b, dataOff, (int)len).TrimEnd(' ', '\0');
            else if (type == 0x004E4942u)     // BIN
            {
                bin = new byte[len];
                Array.Copy(b, dataOff, bin, 0, (int)len);
            }
            off = dataOff + (int)len;
        }
        if (json.Length == 0) throw new InvalidDataException("glTF: GLB has no JSON chunk.");
        return (json, bin);
    }

    private static byte[] DecodeDataUri(string uri)
    {
        int comma = uri.IndexOf(',');
        if (comma < 0 || !uri.StartsWith("data:", StringComparison.Ordinal))
            throw new NotSupportedException("glTF: only embedded base64 data-URI buffers are supported.");
        return Convert.FromBase64String(uri[(comma + 1)..]);
    }

    private static (int bufferView, int byteOffset, int componentType, int count) Accessor(
        JsonElement accessors, int i)
    {
        var a = accessors[i];
        int bvIdx = a.GetProperty("bufferView").GetInt32();
        int accOff = a.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
        int comp = a.GetProperty("componentType").GetInt32();
        int count = a.GetProperty("count").GetInt32();
        return (bvIdx, accOff, comp, count);
    }

    private static (int byteOffset, int byteLength) BufferView(JsonElement bufferViews, int i)
    {
        var bv = bufferViews[i];
        int off = bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
        int len = bv.GetProperty("byteLength").GetInt32();
        return (off, len);
    }

    private static List<(double, double, double)> ReadVec3(
        JsonElement accessors, JsonElement bufferViews, byte[] buf, int accIdx)
    {
        var (bvIdx, accOff, comp, count) = Accessor(accessors, accIdx);
        var (bvOff, _) = BufferView(bufferViews, bvIdx);
        int baseOff = bvOff + accOff;
        var outp = new List<(double, double, double)>(count);
        for (int i = 0; i < count; i++)
        {
            int o = baseOff + i * 12;
            outp.Add((BitConverter.ToSingle(buf, o),
                      BitConverter.ToSingle(buf, o + 4),
                      BitConverter.ToSingle(buf, o + 8)));
        }
        return outp;
    }

    private static List<(byte, byte, byte)> ReadColor(
        JsonElement accessors, JsonElement bufferViews, byte[] buf, int accIdx)
    {
        var (bvIdx, accOff, comp, count) = Accessor(accessors, accIdx);
        var (bvOff, _) = BufferView(bufferViews, bvIdx);
        int baseOff = bvOff + accOff;
        var outc = new List<(byte, byte, byte)>(count);
        // The writer emits normalized UNSIGNED_BYTE VEC4; also tolerate float VEC3/4.
        for (int i = 0; i < count; i++)
        {
            if (comp == 5121) // UNSIGNED_BYTE
            {
                int o = baseOff + i * 4;
                outc.Add((buf[o], buf[o + 1], buf[o + 2]));
            }
            else // FLOAT (assume VEC4 stride 16 for our own writer; VEC3 not emitted)
            {
                int o = baseOff + i * 16;
                outc.Add(((byte)(BitConverter.ToSingle(buf, o) * 255f),
                          (byte)(BitConverter.ToSingle(buf, o + 4) * 255f),
                          (byte)(BitConverter.ToSingle(buf, o + 8) * 255f)));
            }
        }
        return outc;
    }

    private static List<int> ReadIndices(
        JsonElement accessors, JsonElement bufferViews, byte[] buf, int accIdx)
    {
        var (bvIdx, accOff, comp, count) = Accessor(accessors, accIdx);
        var (bvOff, _) = BufferView(bufferViews, bvIdx);
        int baseOff = bvOff + accOff;
        var outi = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            outi.Add(comp switch
            {
                5121 => buf[baseOff + i],                              // UNSIGNED_BYTE
                5123 => BitConverter.ToUInt16(buf, baseOff + i * 2),   // UNSIGNED_SHORT
                5125 => (int)BitConverter.ToUInt32(buf, baseOff + i * 4), // UNSIGNED_INT
                _ => throw new NotSupportedException($"glTF: index componentType {comp} unsupported.")
            });
        }
        return outi;
    }
}
