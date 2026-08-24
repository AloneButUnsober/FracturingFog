// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/AovExrExporter.cs
//
// Roadmap slice S1 (3D-Rendering-Roadmap.md, parent #389): promote the per-pixel
// data the raymarch already resolves — normal, depth, AO, the lighting
// components — to FIRST-CLASS render passes, and emit them as a single
// multi-layer OpenEXR (built on the S7 OpenExrWriter, which already takes
// arbitrary named channels). Then relight / grade / denoise in a compositor
// without re-rendering. This is the actual superpower of Blender's compositor,
// and FF has the source data today (the #317 AovView diagnostic modes) — the gap
// is that only one AOV is viewable at a time and it is discarded, not that it is
// uncomputed.
//
// This first slice packs the existing 8-bit AovView buffers into named EXR
// layers: beauty as the bare R/G/B/A layer, and each captured AOV as a dotted
// sub-layer (normal.R/.G/.B, Z, AO.V, diffuse.*, specular.*, shadow.V) following
// the standard multi-layer EXR / Cryptomatte-style naming that Blender, Nuke and
// Fusion read. The channel VALUES are 8-bit-sourced until the shade pipeline
// emits float AOVs (the deeper S1 slice); the layout and reader contract do not
// change when that lands — only the sample precision improves.

using System;
using System.Collections.Generic;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Imaging;

/// <summary>Packs a beauty buffer + captured <see cref="AovView"/> buffers into a
/// single multi-layer OpenEXR. Pure data plumbing over <see cref="OpenExrWriter"/>.</summary>
public static class AovExrExporter
{
    /// <summary>Build the EXR channel set for a multi-layer AOV image without
    /// writing it — exposed so the channel naming / decode is unit-testable
    /// independently of the file I/O. Beauty is always present (bare RGBA, RGB
    /// linearized like <see cref="OpenExrWriter.WriteBgra8"/>); each AOV in
    /// <paramref name="aovs"/> adds its sub-layer(s). AOV data is decoded, never
    /// gamma'd (normals, depth, occlusion and shadow are data, not color).</summary>
    public static IReadOnlyList<ExrChannel> BuildChannels(
        int width, int height, uint[] beauty, IReadOnlyDictionary<AovView, uint[]> aovs,
        float[]? floatNormalXyz = null, float[]? floatDepth = null,
        ShadingPipeline.ShadeComponents[]? components = null)
    {
        if (beauty == null) throw new ArgumentNullException(nameof(beauty));
        long n = (long)width * height;
        if (beauty.Length < n) throw new ArgumentException("AOV EXR: beauty buffer smaller than width*height.");

        var channels = new List<ExrChannel>(8);

        // Beauty — the default (bare) layer, linear half RGBA.
        var br = new float[n]; var bg = new float[n]; var bb = new float[n]; var ba = new float[n];
        for (int i = 0; i < n; i++)
        {
            uint p = beauty[i];
            br[i] = ViewTransformOps.SrgbToLinear(((p >> 16) & 0xFF) / 255f);
            bg[i] = ViewTransformOps.SrgbToLinear(((p >> 8) & 0xFF) / 255f);
            bb[i] = ViewTransformOps.SrgbToLinear((p & 0xFF) / 255f);
            ba[i] = ((p >> 24) & 0xFF) / 255f;
        }
        channels.Add(new ExrChannel("R", br));
        channels.Add(new ExrChannel("G", bg));
        channels.Add(new ExrChannel("B", bb));
        channels.Add(new ExrChannel("A", ba));

        // S1/S7 (#389) — float-native geometry planes captured in the beauty pass.
        // When supplied they carry the render's own full-precision data (world-space
        // unit normal + world-units depth), so they REPLACE the 8-bit Normals/Depth
        // passes below (which are quantised n·0.5+0.5 / normalized-grey). The lighting
        // component AOVs (diffuse/specular/AO/shadow/stepcount) stay 8-bit for now.
        bool haveFloatNormal = floatNormalXyz != null && floatNormalXyz.Length >= n * 3;
        bool haveFloatDepth = floatDepth != null && floatDepth.Length >= n;
        if (haveFloatNormal)
        {
            var nx = new float[n]; var ny = new float[n]; var nz = new float[n];
            for (int i = 0; i < n; i++)
            {
                nx[i] = floatNormalXyz![i * 3];
                ny[i] = floatNormalXyz[i * 3 + 1];
                nz[i] = floatNormalXyz[i * 3 + 2];
            }
            channels.Add(new ExrChannel("normal.R", nx));
            channels.Add(new ExrChannel("normal.G", ny));
            channels.Add(new ExrChannel("normal.B", nz));
        }
        if (haveFloatDepth)
        {
            var z = new float[n];
            Array.Copy(floatDepth!, z, n);
            channels.Add(new ExrChannel("Z", z));
        }

        // S1/S7 (#389) — float lighting-component planes captured in the beauty pass.
        // When supplied they REPLACE the 8-bit Diffuse/Specular/AO/Shadow passes with
        // the render's own raw values (diffuse/specular byte-scale/255; AO/shadow 0..1).
        bool haveComponents = components != null && components.Length >= n;
        if (haveComponents)
        {
            var dr = new float[n]; var dg = new float[n]; var db = new float[n];
            var sr = new float[n]; var sg = new float[n]; var sb = new float[n];
            var ao = new float[n]; var sh = new float[n];
            for (int i = 0; i < n; i++)
            {
                var c = components![i];
                dr[i] = c.DiffR; dg[i] = c.DiffG; db[i] = c.DiffB;
                sr[i] = c.SpecR; sg[i] = c.SpecG; sb[i] = c.SpecB;
                ao[i] = c.Ao; sh[i] = c.Shadow;
            }
            channels.Add(new ExrChannel("diffuse.R", dr));
            channels.Add(new ExrChannel("diffuse.G", dg));
            channels.Add(new ExrChannel("diffuse.B", db));
            channels.Add(new ExrChannel("specular.R", sr));
            channels.Add(new ExrChannel("specular.G", sg));
            channels.Add(new ExrChannel("specular.B", sb));
            channels.Add(new ExrChannel("AO.V", ao));
            channels.Add(new ExrChannel("shadow.V", sh));
        }

        if (aovs != null)
        {
            foreach (var kv in aovs)
            {
                if (kv.Key == AovView.Beauty) continue;   // beauty already emitted
                if (haveFloatNormal && kv.Key == AovView.Normals) continue;  // float plane wins
                if (haveFloatDepth && kv.Key == AovView.Depth) continue;     // float plane wins
                if (haveComponents && (kv.Key == AovView.Diffuse || kv.Key == AovView.Specular
                    || kv.Key == AovView.AmbientOcclusion || kv.Key == AovView.Shadow)) continue; // float wins
                var buf = kv.Value;
                if (buf == null || buf.Length < n) continue;
                AddAovChannels(channels, kv.Key, buf, (int)n);
            }
        }

        return channels;
    }

    /// <summary>Write a multi-layer AOV EXR to <paramref name="path"/>. When
    /// <paramref name="floatNormalXyz"/> / <paramref name="floatDepth"/> are supplied
    /// the geometry passes are emitted at full float precision and the matching 8-bit
    /// Normals/Depth passes are dropped (roadmap S1/S7, #389).</summary>
    public static void Write(string path, int width, int height,
        uint[] beauty, IReadOnlyDictionary<AovView, uint[]> aovs,
        float[]? floatNormalXyz = null, float[]? floatDepth = null,
        ShadingPipeline.ShadeComponents[]? components = null,
        ExrCompression compression = ExrCompression.None)
    {
        var channels = BuildChannels(width, height, beauty, aovs, floatNormalXyz, floatDepth, components);
        OpenExrWriter.WriteFile(path, width, height, channels, compression);
    }

    /// <summary>Build the channel set for a FLOAT-NATIVE AOV EXR (roadmap S1, #389):
    /// beauty (linear RGBA from the 8-bit buffer) + a raw <c>normal.R/G/B</c> plane
    /// (world-space unit normal in [-1,1], length w·h·3, interleaved x,y,z) + a raw
    /// <c>Z</c> plane (ray distance to the hit in WORLD UNITS, length w·h). Unlike
    /// <see cref="BuildChannels"/> these are not 8-bit-quantised — the values come
    /// straight from the render's float capture, so Z carries true depth and the
    /// normals are full-precision. Null planes are skipped.</summary>
    public static IReadOnlyList<ExrChannel> BuildFloatChannels(
        int width, int height, uint[] beauty, float[]? normalXyz, float[]? depth)
    {
        if (beauty == null) throw new ArgumentNullException(nameof(beauty));
        long n = (long)width * height;
        if (beauty.Length < n) throw new ArgumentException("Float AOV EXR: beauty buffer smaller than width*height.");

        var channels = new List<ExrChannel>(8);
        var br = new float[n]; var bg = new float[n]; var bb = new float[n]; var ba = new float[n];
        for (int i = 0; i < n; i++)
        {
            uint p = beauty[i];
            br[i] = ViewTransformOps.SrgbToLinear(((p >> 16) & 0xFF) / 255f);
            bg[i] = ViewTransformOps.SrgbToLinear(((p >> 8) & 0xFF) / 255f);
            bb[i] = ViewTransformOps.SrgbToLinear((p & 0xFF) / 255f);
            ba[i] = ((p >> 24) & 0xFF) / 255f;
        }
        channels.Add(new ExrChannel("R", br));
        channels.Add(new ExrChannel("G", bg));
        channels.Add(new ExrChannel("B", bb));
        channels.Add(new ExrChannel("A", ba));

        if (normalXyz != null && normalXyz.Length >= n * 3)
        {
            var nx = new float[n]; var ny = new float[n]; var nz = new float[n];
            for (int i = 0; i < n; i++)
            {
                nx[i] = normalXyz[i * 3];
                ny[i] = normalXyz[i * 3 + 1];
                nz[i] = normalXyz[i * 3 + 2];
            }
            channels.Add(new ExrChannel("normal.R", nx));
            channels.Add(new ExrChannel("normal.G", ny));
            channels.Add(new ExrChannel("normal.B", nz));
        }
        if (depth != null && depth.Length >= n)
        {
            var z = new float[n];
            Array.Copy(depth, z, n);
            channels.Add(new ExrChannel("Z", z));
        }
        return channels;
    }

    /// <summary>Write a float-native AOV EXR (beauty + world-space normal + world-
    /// units depth) to <paramref name="path"/> (roadmap S1, #389).</summary>
    public static void WriteFloatAov(string path, int width, int height,
        uint[] beauty, float[]? normalXyz, float[]? depth)
    {
        var channels = BuildFloatChannels(width, height, beauty, normalXyz, depth);
        OpenExrWriter.WriteFile(path, width, height, channels);
    }

    private static void AddAovChannels(List<ExrChannel> channels, AovView view, uint[] buf, int n)
    {
        switch (view)
        {
            case AovView.Normals:
                // Packed as n*0.5+0.5 in the 8-bit AOV → decode back to [-1,1].
                var nx = new float[n]; var ny = new float[n]; var nz = new float[n];
                for (int i = 0; i < n; i++)
                {
                    uint p = buf[i];
                    nx[i] = Decode01(((p >> 16) & 0xFF)) * 2f - 1f;
                    ny[i] = Decode01(((p >> 8) & 0xFF)) * 2f - 1f;
                    nz[i] = Decode01((p & 0xFF)) * 2f - 1f;
                }
                channels.Add(new ExrChannel("normal.R", nx));
                channels.Add(new ExrChannel("normal.G", ny));
                channels.Add(new ExrChannel("normal.B", nz));
                break;

            case AovView.Depth:
                // Grayscale near=dark..far=light → single Z plane (normalized 0..1
                // until float world-distance AOVs land).
                channels.Add(new ExrChannel("Z", ScalarPlane(buf, n)));
                break;

            case AovView.AmbientOcclusion:
                channels.Add(new ExrChannel("AO.V", ScalarPlane(buf, n)));
                break;

            case AovView.Shadow:
                channels.Add(new ExrChannel("shadow.V", ScalarPlane(buf, n)));
                break;

            case AovView.Diffuse:
                AddRgbLayer(channels, "diffuse", buf, n);
                break;

            case AovView.Specular:
                AddRgbLayer(channels, "specular", buf, n);
                break;

            case AovView.StepCount:
                // Cost heat map — a diagnostic scalar, kept as a data plane.
                channels.Add(new ExrChannel("stepcount.V", ScalarPlane(buf, n)));
                break;
        }
    }

    private static void AddRgbLayer(List<ExrChannel> channels, string layer, uint[] buf, int n)
    {
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            uint p = buf[i];
            r[i] = Decode01((p >> 16) & 0xFF);
            g[i] = Decode01((p >> 8) & 0xFF);
            b[i] = Decode01(p & 0xFF);
        }
        channels.Add(new ExrChannel($"{layer}.R", r));
        channels.Add(new ExrChannel($"{layer}.G", g));
        channels.Add(new ExrChannel($"{layer}.B", b));
    }

    // Grayscale scalar plane — read the red channel (all three are equal for the
    // grayscale AOVs).
    private static float[] ScalarPlane(uint[] buf, int n)
    {
        var v = new float[n];
        for (int i = 0; i < n; i++) v[i] = Decode01((buf[i] >> 16) & 0xFF);
        return v;
    }

    private static float Decode01(uint b) => b / 255f;
}
