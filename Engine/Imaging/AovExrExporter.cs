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
        int width, int height, uint[] beauty, IReadOnlyDictionary<AovView, uint[]> aovs)
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

        if (aovs != null)
        {
            foreach (var kv in aovs)
            {
                if (kv.Key == AovView.Beauty) continue;   // beauty already emitted
                var buf = kv.Value;
                if (buf == null || buf.Length < n) continue;
                AddAovChannels(channels, kv.Key, buf, (int)n);
            }
        }

        return channels;
    }

    /// <summary>Write a multi-layer AOV EXR to <paramref name="path"/>.</summary>
    public static void Write(string path, int width, int height,
        uint[] beauty, IReadOnlyDictionary<AovView, uint[]> aovs)
    {
        var channels = BuildChannels(width, height, beauty, aovs);
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
