// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ExrRegrade.cs
//
// Roadmap slice S2 (3D-Rendering-Roadmap.md, #389 / #396) — the EXR READ-BACK
// consumer. FF renders and can write a scene-linear OpenEXR (S7, OpenExrWriter),
// which keeps real highlight headroom (values > 1.0). This reads one back into the
// LinearFloatImage intermediate and applies a view transform + exposure at output,
// so a rendered .exr can be regraded / tonemapped AFTER the fact — the actual
// render-pass compositor superpower (relight / grade in post without re-rendering).
//
// Pure and headless (no device, no UI): read → transform → encode. The tonemap is
// the same ViewTransformOps operator every other S2 producer routes through, so a
// regraded EXR and a live/poster render with the same transform match.

using System;

namespace FracturingFog.Imaging;

/// <summary>Reads a scene-linear OpenEXR and tonemaps it through the S2 view
/// transform (#396). The EXR read-back consumer of <see cref="LinearFloatImage"/>.</summary>
public static class ExrRegrade
{
    /// <summary>Tonemap a scene-linear EXR to an 8-bit BGRA buffer via
    /// <paramref name="transform"/> + <paramref name="exposureEv"/>. Returns the
    /// encoded buffer, or <c>null</c> when the file is not a supported EXR (the
    /// <see cref="LinearFloatImage.FromExr(string)"/> contract). <see cref="ViewTransform.None"/>
    /// straight-encodes the linear image (highlights above 1.0 saturate — the plain
    /// sRGB encode).</summary>
    public static uint[]? ToneMapToBgra(string exrPath, ViewTransform transform, float exposureEv,
        out int width, out int height)
    {
        width = 0; height = 0;
        var img = LinearFloatImage.FromExr(exrPath);
        if (img == null) return null;
        width = img.Width;
        height = img.Height;
        return img.ApplyViewTransform(transform, exposureEv).ToBgra();
    }

    /// <summary>Read a scene-linear EXR, tonemap it, and save the result to
    /// <paramref name="outPath"/> (format inferred from the extension by default —
    /// e.g. <c>.png</c>). Returns <c>false</c> when the input is not a supported EXR.
    /// Saving back to an <c>.exr</c> re-encodes the tonemapped display-referred image,
    /// not the original scene-linear data.</summary>
    public static bool RenderToFile(string exrPath, string outPath,
        ViewTransform transform, float exposureEv,
        ImageFileFormat format = ImageFileFormat.Auto)
    {
        if (string.IsNullOrEmpty(outPath)) throw new ArgumentException("ExrRegrade.RenderToFile: outPath is null or empty.", nameof(outPath));
        var bgra = ToneMapToBgra(exrPath, transform, exposureEv, out int w, out int h);
        if (bgra == null) return false;
        ImageExport.SavePixelsToFile(bgra, w, h, outPath, format, wm: null);
        return true;
    }
}
