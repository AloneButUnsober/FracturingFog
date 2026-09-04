// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/SvgfHistory.cs
//
// Roadmap slice S4 (3D-Rendering-Roadmap.md, parent #389 / #402): the persistent
// state that unites SVGF's two halves across frames. A sequence renderer owns ONE
// of these for the whole render and hands it to ReliefDenoisePass.ApplySvgf each
// frame; the pass reprojects the previous denoised colour along the motion AOV,
// blends, variance-guides the À-Trous filter, and stores the result back here as
// the next frame's history. It also carries the previous frame's camera so the
// caller can thread it as the render's previousCamera (which fills the motion AOV)
// and the previous normal / depth planes for disocclusion rejection.
//
// A fresh (or Reset) history is "invalid" → the first frame has nothing to
// reproject and falls back to the plain single-frame denoise, which seeds it.

namespace FracturingFog.Imaging;

/// <summary>Persistent SVGF denoise history across the frames of a sequence render
/// (roadmap S4, #402). Owned by the caller; updated by
/// <see cref="ReliefDenoisePass.ApplySvgf"/>.</summary>
public sealed class SvgfHistory
{
    /// <summary>Previous frame's denoised colour (BGRA, w*h) — the reprojection source.</summary>
    public uint[]? Color { get; set; }

    /// <summary>Previous frame's world-space normal AOV (w*h*3) for disocclusion.</summary>
    public float[]? Normal { get; set; }

    /// <summary>Previous frame's world-units depth AOV (w*h) for disocclusion.</summary>
    public float[]? Depth { get; set; }

    /// <summary>Previous frame's camera — the caller threads it as the render's
    /// <c>previousCamera</c> so this frame's motion AOV fills.</summary>
    public FracturingFog.Rendering.Lighting.ReliefMotionVector.CameraView? PrevCamera { get; set; }

    /// <summary>Grid the stored buffers were captured at; a size change invalidates
    /// reprojection (the history is re-seeded).</summary>
    public int W { get; set; }
    public int H { get; set; }

    /// <summary>False until a frame has been stored — the first frame has no history
    /// to reproject and falls back to the plain spatial denoise.</summary>
    public bool Valid { get; set; }

    /// <summary>Drop the accumulated history (e.g. a shot cut / camera discontinuity):
    /// the next frame re-seeds from scratch.</summary>
    public void Reset()
    {
        Valid = false;
        Color = null;
        Normal = null;
        Depth = null;
        PrevCamera = null;
    }
}
