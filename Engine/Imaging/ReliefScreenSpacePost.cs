// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ReliefScreenSpacePost.cs
//
// Roadmap slice S12 (3D-Rendering-Roadmap.md, #652) — the Relief 3D stage-2 post
// chain. Relief renders through HeightfieldRaymarch2D (not a calculator), so it
// never ran the whole-buffer post passes the true-3D calculators do
// (ScreenSpacePost.ApplySsao / ApplyToneMapBloom / ApplyEdgeInk / lens); the
// Volumetric Lighting & FX dialog exposed them but they were silent no-ops on
// Relief. Relief already captures the same inputs those passes want — the pre-clamp
// HDR beauty (ReliefAovBuffers.HdrBeauty, #396) and the float normal + depth
// G-buffer (ReliefAovBuffers.NormalXyz / Depth, #398/#416) — so this is the single
// choke point that runs them on the relief buffer, shared by the live path
// (FractalRenderHost) and the offscreen poster (PosterRenderer) so screen and
// export match.
//
//   * S12.1 (#663): Tone Map + Exposure + Bloom (ApplyToneMapBloom over HDR beauty).
//   * S12.2: Lens post (chromatic aberration / distortion / vignette / anamorphic).
//   * S12.3: Edge ink (ApplyEdgeInk over the normal + depth G-buffer).
//   * S12.4: SSAO (ApplySsao over the depth G-buffer).
//
// The global S2 ViewTransform still runs AFTER this on the (now display-referred)
// 8-bit buffer, exactly as it stacks on a 3D calculator's buffer.

using System;

using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Imaging;

/// <summary>Runs the Relief 3D stage-2 whole-buffer post chain (tone map / bloom /
/// lens / SSAO / edge ink) over the relief buffer + its captured AOVs — the relief
/// analogue of what the 3D calculators run inline (roadmap S12, #652).</summary>
public static class ReliefScreenSpacePost
{
    // The relief raymarch writes this far value as the sky / ray-miss depth
    // sentinel (HeightfieldRaymarch2D). The stage-2 passes detect sky as +Infinity,
    // so it is remapped before they see it.
    private const float ReliefSkyDepth = 1e6f;

    /// <summary>Apply the relief stage-2 post chain in place on <paramref name="dst"/>
    /// (the graded 8-bit relief buffer). Returns <c>true</c> when ANY pass ran (tone
    /// map / bloom / lens / SSAO / edge) — the caller must then route the global S2
    /// view transform through the 8-bit <paramref name="dst"/> (not the HDR-beauty
    /// path, which would overwrite these display-space passes). <c>false</c> = nothing
    /// applied, the caller keeps the HDR view-transform path. Every pass self-gates on
    /// its own FX knob.</summary>
    public static bool ApplyStage2(
        uint[] dst,
        HeightfieldRaymarch2D.ReliefAovBuffers? aov,
        int w, int h,
        in LightingFxData fx)
    {
        if (dst == null) return false;
        int n = w * h;
        if (n <= 0 || dst.Length < n) return false;

        bool wantTonemapBloom = fx.ToneMap != ToneMapOperator.None || fx.BloomStrength > 0.0;
        bool wantLens = WantsLens(in fx);
        float[]? hdr = aov?.HdrBeauty;
        bool hdrOk = hdr != null && hdr.Length == (long)n * 3;

        // Tone map + bloom (+ lens, which ApplyToneMapBloom folds in) over the HDR
        // beauty — S12.1. When the HDR beauty is unavailable (e.g. froxel active) but
        // a lens is set, still run the byte-buffer lens pass so it isn't lost — S12.2.
        bool any = false;
        if (wantTonemapBloom && hdrOk)
        {
            ScreenSpacePost.ApplyToneMapBloom(dst, hdr!, w, h, in fx);
            any = true;
        }
        else if (wantLens)
        {
            ScreenSpacePost.ApplyLensPost(dst, w, h, in fx);
            any = true;
        }

        // SSAO (S12.4) + edge ink (S12.3) key on the relief float normal + depth
        // G-buffer. Both detect sky as +Infinity; the relief depth uses a large finite
        // sentinel, so remap it once and share the copy. Run AFTER the tone map so they
        // modify the display-referred pixels, not the pre-tonemap beauty.
        if (aov?.Depth != null && aov.NormalXyz != null
            && aov.Depth.Length >= n && aov.NormalXyz.Length >= 3L * n
            && (fx.SsaoSamples > 0 || fx.EdgeStrength > 0.0))
        {
            float[] depth = DepthForPost(aov.Depth, n);
            if (fx.SsaoSamples > 0) { ScreenSpacePost.ApplySsao(dst, depth, aov.NormalXyz, w, h, in fx); any = true; }
            if (fx.EdgeStrength > 0.0) { ScreenSpacePost.ApplyEdgeInk(dst, depth, aov.NormalXyz, w, h, in fx); any = true; }
        }

        return any;
    }

    /// <summary>The lens predicate mirroring ScreenSpacePost's own (chromatic
    /// aberration / distortion / vignette / tangential / anamorphic).</summary>
    public static bool WantsLens(in LightingFxData fx)
        => fx.ChromaticAberration > 0 || fx.LensDistortion != 0
        || fx.Vignette > 0 || fx.LensTangentialX != 0 || fx.LensTangentialY != 0
        || (fx.AnamorphicSqueeze != 0 && fx.AnamorphicSqueeze != 1.0);

    /// <summary>True when any stage-2 pass this helper runs is active for
    /// <paramref name="fx"/> — the arming predicate the render sites use to decide
    /// whether to capture the AOVs (HDR beauty for tone map/bloom; normal+depth for
    /// SSAO/edge). Lens alone needs no capture.</summary>
    public static bool WantsHdr(in LightingFxData fx)
        => fx.ToneMap != ToneMapOperator.None || fx.BloomStrength > 0.0;

    /// <summary>True when SSAO or edge ink is active — the render must capture the
    /// relief normal + depth G-buffer (which the GPU relief kernel can also emit, so
    /// this does not force the CPU trace on its own).</summary>
    public static bool WantsGeom(in LightingFxData fx)
        => fx.SsaoSamples > 0 || fx.EdgeStrength > 0.0;

    private static float[] DepthForPost(float[] reliefDepth, int n)
    {
        var d = new float[n];
        for (int i = 0; i < n; i++)
        {
            float z = reliefDepth[i];
            d[i] = z >= ReliefSkyDepth ? float.PositiveInfinity : z;
        }
        return d;
    }
}
