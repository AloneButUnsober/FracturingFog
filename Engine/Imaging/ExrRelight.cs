// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ExrRelight.cs
//
// Roadmap slice S1 follow-up (3D-Rendering-Roadmap.md, #389 / #398 / #718): the
// OFFLINE relight round-trip. The live relight (Relief2DRelight / Relief 3D dialog
// + --relight*) recombines the captured lighting passes for the CURRENT render;
// this reads a SAVED multi-layer AOV EXR back (via OpenExrReader.ParseLayers), pulls
// out the albedo + diffuse/specular/AO layers AovExrExporter wrote, and runs the
// same LightCompositor.Composite — so a rendered .exr can be relit AFTER the fact,
// without re-tracing the geometry. The real render-pass-compositor superpower.
//
// Pure and headless (no device, no UI): read → composite → encode. The compositor
// is the identical operator the live path uses, so an offline relight and a live
// relight of the same scene with the same gains match.

using System;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Imaging;

/// <summary>Relight a saved AOV EXR from its captured lighting passes (roadmap S1,
/// #718). Reads <c>albedo.*</c> + <c>diffuse.*</c> + <c>specular.*</c> + <c>AO.V</c>
/// and recombines them through <see cref="LightCompositor"/>.</summary>
public static class ExrRelight
{
    /// <summary>Read <paramref name="exrPath"/>, recombine its captured lighting
    /// components under <paramref name="p"/>, and return the relit straight-alpha
    /// BGRA buffer. Returns null when the file is not a supported EXR OR does not
    /// carry the layers the relight needs (at minimum <c>albedo.R/.G/.B</c> and
    /// <c>diffuse.R/.G/.B</c> — a beauty-only EXR cannot be relit). <c>specular.*</c>
    /// defaults to 0 and <c>AO.V</c> to 1 (no occlusion) when absent.</summary>
    public static uint[]? Composite(string exrPath, LightCompositeParams p,
        out int width, out int height)
    {
        width = 0; height = 0;
        using var fs = System.IO.File.OpenRead(exrPath);
        var img = OpenExrReader.ParseLayers(fs);
        if (img == null) return null;

        float[]? aR = img.Plane("albedo.R"), aG = img.Plane("albedo.G"), aB = img.Plane("albedo.B");
        float[]? dR = img.Plane("diffuse.R"), dG = img.Plane("diffuse.G"), dB = img.Plane("diffuse.B");
        if (aR == null || aG == null || aB == null || dR == null || dG == null || dB == null)
            return null;   // not a relightable AOV EXR (needs albedo + diffuse layers)

        float[]? sR = img.Plane("specular.R"), sG = img.Plane("specular.G"), sB = img.Plane("specular.B");
        float[]? ao = img.Plane("AO.V");

        int w = img.Width, h = img.Height;
        long n = (long)w * h;

        // Rebuild the compositor's inputs: opaque straight-alpha ARGB albedo + the
        // per-pixel float ShadeComponents (diffuse/specular/AO; shadow unused by the
        // operator). Albedo stored without alpha → force opaque.
        var albedo = new uint[n];
        var comps = new ShadingPipeline.ShadeComponents[n];
        for (long i = 0; i < n; i++)
        {
            uint r = (uint)Math.Clamp(aR[i] * 255f + 0.5f, 0, 255);
            uint g = (uint)Math.Clamp(aG[i] * 255f + 0.5f, 0, 255);
            uint b = (uint)Math.Clamp(aB[i] * 255f + 0.5f, 0, 255);
            albedo[i] = 0xFF000000u | (r << 16) | (g << 8) | b;

            comps[i] = new ShadingPipeline.ShadeComponents(
                dR[i], dG[i], dB[i],
                sR != null ? sR[i] : 0f, sG != null ? sG[i] : 0f, sB != null ? sB[i] : 0f,
                ao != null ? ao[i] : 1f,
                0f);
        }

        width = w; height = h;
        return LightCompositor.Composite(albedo, comps, w, h, p);
    }

    /// <summary>Relight a saved AOV EXR and write the result to
    /// <paramref name="outPath"/> (format inferred from the extension by default).
    /// Returns false when the input is not a supported / relightable EXR.</summary>
    public static bool RenderToFile(string exrPath, string outPath, LightCompositeParams p,
        ImageFileFormat format = ImageFileFormat.Auto)
    {
        if (string.IsNullOrEmpty(outPath)) throw new ArgumentException("ExrRelight.RenderToFile: outPath is null or empty.", nameof(outPath));
        var bgra = Composite(exrPath, p, out int w, out int h);
        if (bgra == null) return false;
        ImageExport.SavePixelsToFile(bgra, w, h, outPath, format, wm: null);
        return true;
    }
}
