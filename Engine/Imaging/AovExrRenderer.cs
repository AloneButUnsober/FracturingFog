// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/AovExrRenderer.cs
//
// Roadmap slice S1 integration follow-up (3D-Rendering-Roadmap.md, parent #389):
// the render ORCHESTRATION that turns a single PosterRequest into a multi-layer
// AOV OpenEXR. The pieces already existed — AovView diagnostic modes (#317) that
// make ShadingPipeline.Shade return a chosen pass, and AovExrExporter (S1) that
// packs beauty + AOV buffers into named EXR layers. What was missing is the loop
// that RENDERS the scene once per AOV (toggling LightingFxData.DebugAov), collects
// the buffers and hands them to the packer. This is that loop.
//
// CPU-only: DebugAov is honoured by the CPU shade path (relief raymarch + the 3D
// fractal calculators); a flat 2D render simply yields beauty-equal AOV planes.
// Each pass is a full, deterministic re-render (no RNG), so the .exr is identical
// live and under --batch.
//
// S1/S7 deep tail (#389): on the oblique relief-raymarch path the GEOMETRY passes
// are now float-native — the relief march fills a ReliefAovBuffers (world-space
// unit normal + world-units depth) from the primary hit in the SAME pass as the
// beauty, so normal.* / Z carry full precision and their two 8-bit re-renders are
// skipped. The lighting-component passes (diffuse / specular / AO / shadow /
// stepcount) stay 8-bit-sourced until the shade pipeline emits float components.

using System;
using System.Collections.Generic;
using System.Threading;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Imaging;

/// <summary>Renders a scene once per <see cref="AovView"/> and writes the beauty +
/// AOV passes as a single multi-layer OpenEXR (roadmap S1, #389).</summary>
public static class AovExrRenderer
{
    /// <summary>The AOV passes captured alongside beauty by default — the standard
    /// compositor set (geometry + lighting components + a cost diagnostic).</summary>
    public static readonly IReadOnlyList<AovView> DefaultViews = new[]
    {
        AovView.Normals, AovView.Depth, AovView.AmbientOcclusion,
        AovView.Diffuse, AovView.Specular, AovView.Shadow, AovView.StepCount,
    };

    /// <summary>Render beauty + each AOV in <paramref name="views"/> (default
    /// <see cref="DefaultViews"/>) and write them to <paramref name="path"/> as a
    /// multi-layer EXR. Returns the pixel dimensions written. The request's
    /// <c>DebugAov</c> is toggled per pass and restored afterwards.</summary>
    public static (int width, int height) RenderToFile(
        PosterRequest req, string path, CancellationToken token, IReadOnlyList<AovView>? views = null)
    {
        if (req == null) throw new ArgumentNullException(nameof(req));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("AOV EXR: output path required.", nameof(path));
        views ??= DefaultViews;

        // LightingFxData is a struct exposed as a property, so DebugAov must be
        // set by reassigning the whole struct back onto FractalParameters (a
        // by-value member write would be silently discarded).
        var fp = req.FractalParameters;
        var savedAov = fp.Lighting.DebugAov;
        try
        {
            // S1/S7 (#389) — the oblique relief raymarch fills float normal/depth
            // AOVs from the PRIMARY hit in the SAME pass as the beauty. Capture them
            // once here so the geometry passes are full-precision (world-space unit
            // normal + world-units depth) instead of the 8-bit AovView quantisation —
            // and so we skip re-rendering the Normals/Depth passes entirely. Only the
            // relief-raymarch path emits the capture; other renders keep the 8-bit
            // passes.
            bool floatGeo = fp.Relief2DEnabled && fp.Relief2DRaymarch;

            // Beauty pass first — also fixes the canonical width/height every AOV
            // pass must match.
            SetAov(fp, AovView.Beauty);
            var geo = floatGeo
                ? new FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.ReliefAovBuffers(req.Width, req.Height)
                : null;
            uint[] beauty = PosterRenderer.RenderToPixels(req, token, out int w, out int h, geo);

            var aovs = new Dictionary<AovView, uint[]>(views.Count);
            foreach (var v in views)
            {
                if (v == AovView.Beauty) continue;
                // Float geometry replaces these — no need to re-render them 8-bit.
                if (floatGeo && (v == AovView.Normals || v == AovView.Depth)) continue;
                token.ThrowIfCancellationRequested();
                SetAov(fp, v);
                aovs[v] = PosterRenderer.RenderToPixels(req, token, out _, out _);
            }

            AovExrExporter.Write(path, w, h, beauty, aovs, geo?.NormalXyz, geo?.Depth);
            return (w, h);
        }
        finally
        {
            SetAov(fp, savedAov);
        }
    }

    private static void SetAov(FracturingFog.Models.FractalParameters fp, AovView view)
    {
        var l = fp.Lighting;
        l.DebugAov = view;
        fp.Lighting = l;
    }
}
