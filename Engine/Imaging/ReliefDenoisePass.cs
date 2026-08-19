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

    /// <summary>Allocate an AOV capture target sized to the OUTPUT grid iff the
    /// denoise is enabled; null otherwise. Pass the result straight into
    /// <see cref="HeightfieldRaymarch2D.Render(uint[],float[],int,int,int,int,FractalParameters,uint[],out double,IReliefRaymarchKernel,HeightfieldRaymarch2D.ReliefAovBuffers)"/>:
    /// null keeps the raymarch's GPU fast path, non-null forces the CPU trace and
    /// fills the guides.</summary>
    public static HeightfieldRaymarch2D.ReliefAovBuffers? MakeCapture(FractalParameters? p, int w, int h)
        => Enabled(p) && w > 0 && h > 0 ? new HeightfieldRaymarch2D.ReliefAovBuffers(w, h) : null;

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

    /// <summary>Map the relief denoise knobs onto the pure operator's parameters.</summary>
    public static AtrousParams BuildParams(FractalParameters p) => new()
    {
        Iterations = Math.Max(0, p.Relief2DDenoiseIterations),
        ColorSigma = p.Relief2DDenoiseColorSigma,
        NormalSigma = p.Relief2DDenoiseNormalSigma,
        DepthSigma = p.Relief2DDenoiseDepthSigma,
    };
}
