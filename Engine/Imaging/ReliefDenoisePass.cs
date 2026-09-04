// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ReliefDenoisePass.cs
//
// Roadmap slice S4 (3D-Rendering-Roadmap.md, parent #389) — INTEGRATION: wire the
// pure guided À-Trous denoiser (AtrousDenoiser) into the relief-raymarch render
// path, keyed on the float normal + depth AOVs the raymarch now emits (#416,
// HeightfieldRaymarch2D.ReliefAovBuffers). AO / soft shadow / reflections are
// Monte Carlo — noisy — and today FF pays that noise down with supersamples; the
// denoiser smooths within a surface but stops at geometric edges, so detail
// survives while noise averages out, for fewer samples at equal quality.
//
// This is a thin, deterministic glue layer that every relief render site (poster,
// live final frame, cached recolour) calls the same way:
//
//     var aov = ReliefDenoisePass.MakeCapture(p, w, h);   // null when off
//     HeightfieldRaymarch2D.Render(..., dst, out _, kernel, aov);
//     ReliefDenoisePass.Apply(dst, aov, w, h, p);         // no-op when aov null
//
// Iterations 0 (the default) ⇒ MakeCapture returns null ⇒ Render keeps its GPU
// fast path and Apply is a no-op ⇒ the beauty is byte-for-byte unchanged. A
// non-zero iteration count allocates the AOV capture target, which the raymarch's
// GPU gate treats as "force the CPU trace" (the GPU kernel emits no AOVs yet), so
// the guides are always the render's own float data — the CPU-parity discipline
// the roadmap requires.

using System;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Imaging;

/// <summary>Glue that runs the S4 À-Trous denoiser over a relief-raymarch beauty
/// using the render's own float normal + depth AOVs (roadmap S4, #389).</summary>
public static class ReliefDenoisePass
{
    /// <summary>True when the guided denoise should run: the oblique raymarch is
    /// active (the only path that emits normal/depth guides) and at least one pass
    /// is requested. Off ⇒ the whole feature is a byte-identical no-op.</summary>
    public static bool Enabled(FractalParameters? p)
        => p != null && p.Relief2DEnabled && p.Relief2DRaymarch && p.Relief2DDenoiseIterations > 0;

    /// <summary>True when the SVGF temporal path is armed (denoise on + the temporal
    /// toggle). It composes the spatial À-Trous with cross-frame accumulation +
    /// variance guiding, and needs a sequence render carrying an <see cref="SvgfHistory"/>
    /// and a previous-frame camera. Off ⇒ the plain single-frame denoise.</summary>
    public static bool EnabledTemporal(FractalParameters? p)
        => Enabled(p) && p!.Relief2DDenoiseTemporal;

    /// <summary>Allocate an AOV capture target sized to the OUTPUT grid iff the
    /// denoise is enabled; null otherwise. Pass the result straight into
    /// <see cref="HeightfieldRaymarch2D.Render(uint[],float[],int,int,int,int,FractalParameters,uint[],out double,IReliefRaymarchKernel,HeightfieldRaymarch2D.ReliefAovBuffers)"/>:
    /// null keeps the raymarch's GPU fast path, non-null forces the CPU trace and
    /// fills the guides.</summary>
    public static HeightfieldRaymarch2D.ReliefAovBuffers? MakeCapture(FractalParameters? p, int w, int h)
        => MakeCapture(p, w, h, captureHdr: false);

    /// <summary>As <see cref="MakeCapture(FractalParameters,int,int)"/>, but also
    /// requests the S2 (#396) HDR-beauty plane when <paramref name="captureHdr"/> is
    /// set — so the view transform can tonemap the true-linear intermediate. The
    /// capture is allocated whenever the denoise OR the HDR plane is wanted; either
    /// forces the CPU trace. Null only when neither is needed (byte-identical).</summary>
    public static HeightfieldRaymarch2D.ReliefAovBuffers? MakeCapture(FractalParameters? p, int w, int h, bool captureHdr)
        => MakeCapture(p, w, h, captureHdr, captureGeom: false);

    /// <summary>As the <paramref name="captureHdr"/> overload, but
    /// <paramref name="captureGeom"/> also forces a (normal + depth) capture target
    /// even with the denoise and HDR off — the S12 relief stage-2 SSAO / edge-ink
    /// passes (#652) key on that G-buffer. Normal + depth are always allocated on any
    /// capture and the GPU relief kernel emits them, so a geom-only capture does NOT
    /// force the CPU trace (unlike an HDR / motion / component capture). Null only when
    /// nothing at all is wanted (byte-identical).</summary>
    public static HeightfieldRaymarch2D.ReliefAovBuffers? MakeCapture(
        FractalParameters? p, int w, int h, bool captureHdr, bool captureGeom)
    {
        if (w <= 0 || h <= 0) return null;
        bool denoise = Enabled(p);
        if (!denoise && !captureHdr && !captureGeom) return null;
        // SVGF temporal also needs the motion-vector AOV (to reproject the history);
        // the plain denoise needs only normal + depth. Motion capture forces the CPU
        // trace either way, so a denoise already runs on the CPU.
        return new HeightfieldRaymarch2D.ReliefAovBuffers(w, h, captureComponents: false,
            captureMotion: denoise && EnabledTemporal(p), captureHdr: captureHdr);
    }

    /// <summary>Denoise <paramref name="beauty"/> in place using the captured
    /// normal + depth guides. No-op when <paramref name="aov"/> is null or the
    /// denoise is disabled — so a caller can always call this unconditionally.</summary>
    public static void Apply(uint[] beauty, HeightfieldRaymarch2D.ReliefAovBuffers? aov,
        int w, int h, FractalParameters? p)
    {
        if (aov == null || beauty == null || !Enabled(p)) return;
        long n = (long)w * h;
        if (beauty.Length < n) return;

        var denoised = AtrousDenoiser.Denoise(beauty, w, h, BuildParams(p!), aov.NormalXyz, aov.Depth);
        Array.Copy(denoised, beauty, Math.Min(beauty.Length, denoised.Length));
    }

    /// <summary>The united SVGF pass (roadmap S4, #402): temporal accumulation +
    /// variance-guided À-Trous, keyed on the render's motion / normal / depth AOVs and
    /// a persistent <paramref name="history"/>. Reprojects the previous denoised frame
    /// along the motion AOV and blends it in (rejecting disocclusion), estimates a
    /// per-pixel variance from the accumulated frame, runs the variance-guided À-Trous,
    /// writes the result into <paramref name="beauty"/>, and stores it (+ this frame's
    /// normal/depth/camera) back into <paramref name="history"/> for the next frame.
    /// <para>The FIRST frame (an invalid or size-mismatched history) has nothing to
    /// reproject and falls back to the plain single-frame variance-guided denoise, which
    /// seeds the history. When the temporal toggle is off this defers to
    /// <see cref="Apply"/> (a caller can always call this). Denoise off ⇒ no-op.</para></summary>
    public static void ApplySvgf(uint[] beauty, HeightfieldRaymarch2D.ReliefAovBuffers? aov,
        int w, int h, FractalParameters? p, SvgfHistory history)
    {
        if (aov == null || beauty == null || history == null || !Enabled(p)) return;
        long n = (long)w * h;
        if (beauty.Length < n) return;
        if (!p!.Relief2DDenoiseTemporal) { Apply(beauty, aov, w, h, p); return; }

        var atrous = BuildParams(p);
        double feedback = Math.Clamp(p.Relief2DDenoiseTemporalFeedback, 0.0, 0.98);
        double varScale = Math.Max(0.0, p.Relief2DDenoiseVarianceScale);

        // 1. Temporal accumulation — reproject the previous denoised frame along the
        //    motion AOV and blend, rejecting disocclusion on the normal/depth guides.
        //    A fresh / resized history has nothing to reproject → start from the beauty.
        bool reuse = history.Valid && history.Color != null
                     && history.W == w && history.H == h && history.Color.Length >= n;
        uint[] acc = reuse
            ? SvgfTemporal.Accumulate(beauty, history.Color, aov.Motion, w, h, feedback,
                aov.NormalXyz, history.Normal, aov.Depth, history.Depth)
            : (uint[])beauty.Clone();

        // 2. Variance. Accumulate the luminance moments (E[l], E[l²]) of the noisy
        //    beauty across frames with the SAME reprojection + disocclusion the colour
        //    uses, and derive the TEMPORAL variance (E[l²] − E[l]²). Blend from the
        //    SPATIAL estimate toward the temporal one by the per-pixel history length,
        //    so a fresh / disoccluded pixel (few samples) still filters wide while a
        //    converged one trusts its settled temporal variance.
        var (m1, m2, len) = SvgfMoments.Accumulate(beauty,
            reuse ? history.Moment1 : null, reuse ? history.Moment2 : null,
            reuse ? history.Length : null, aov.Motion, w, h, feedback,
            aov.NormalXyz, history.Normal, aov.Depth, history.Depth);
        var spatial = SvgfVariance.EstimateSpatial(acc, w, h, 1);
        var temporal = SvgfVariance.FromMoments(m1, m2, w, h);
        var variance = new float[n];
        System.Threading.Tasks.Parallel.For(0, (int)n, i =>
        {
            double t = Math.Min((int)len[i], 4) / 4.0;   // 4 samples → fully temporal
            variance[i] = (float)(spatial[i] * (1.0 - t) + temporal[i] * t);
        });

        // 3. Variance-guided À-Trous, guided by the geometry AOVs.
        var denoised = AtrousDenoiser.Denoise(acc, w, h, atrous, aov.NormalXyz, aov.Depth, variance, varScale);
        Array.Copy(denoised, beauty, Math.Min(beauty.Length, denoised.Length));

        // 4. Store this frame as the next frame's history.
        history.Color = (uint[])denoised.Clone();
        history.Normal = aov.NormalXyz != null ? (float[])aov.NormalXyz.Clone() : null;
        history.Depth = aov.Depth != null ? (float[])aov.Depth.Clone() : null;
        history.Moment1 = m1; history.Moment2 = m2; history.Length = len;
        history.PrevCamera = aov.CurrentCamera;
        history.W = w; history.H = h; history.Valid = true;
    }

    /// <summary>Map the relief denoise knobs onto the pure operator's parameters.</summary>
    public static AtrousParams BuildParams(FractalParameters p) => new()
    {
        Iterations = Math.Max(0, p.Relief2DDenoiseIterations),
        ColorSigma = p.Relief2DDenoiseColorSigma,
        NormalSigma = p.Relief2DDenoiseNormalSigma,
        DepthSigma = p.Relief2DDenoiseDepthSigma,
    };
}
