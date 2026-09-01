// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/MotionBlurAccumulator.cs
//
// S3 (#389/#400/#568) — shared accumulation motion blur. The offline scene
// renderer already averages N sub-frames per output frame at sub-shutter times
// (SceneRenderPlan); this is the reusable weighted-frame averager that model,
// factored out so the batch video-zoom loop (and future consumers) express the
// SAME shutter accumulation instead of re-rolling it.
//
// A shutter that is closed (a single sample, weight 1) resolves to exactly the
// input frame (opaque), so the default single-frame path is byte-identical.
//
// Pure + deterministic: given the same weighted frames it always produces the
// same average, so it is --batch-stable and twinnable.

using System;

namespace FracturingFog.Rendering;

/// <summary>Weighted average of BGRA <see cref="uint"/> frames for accumulation
/// motion blur (roadmap S3, #568). Reused across an output frame's sub-shutter
/// samples: <see cref="Reset"/>, <see cref="Add"/> each sub-frame with its weight,
/// then <see cref="Resolve"/> into the destination. The alpha channel is forced
/// opaque on resolve (motion blur composits colour, not coverage).</summary>
public sealed class MotionBlurAccumulator
{
    private readonly double[] _accum;   // per-pixel R,G,B running weighted sum
    private readonly int _pixels;
    private double _weightSum;

    public MotionBlurAccumulator(int pixelCount)
    {
        if (pixelCount <= 0) throw new ArgumentOutOfRangeException(nameof(pixelCount));
        _pixels = pixelCount;
        _accum = new double[pixelCount * 3];
    }

    /// <summary>Number of sample frames folded in since the last <see cref="Reset"/>.</summary>
    public int SampleCount { get; private set; }

    /// <summary>Clear the accumulator for the next output frame.</summary>
    public void Reset()
    {
        Array.Clear(_accum, 0, _accum.Length);
        _weightSum = 0.0;
        SampleCount = 0;
    }

    /// <summary>Fold one sub-frame in with the given non-negative weight. Ignores a
    /// zero / negative weight. Reads the R/G/B bytes of each BGRA pixel; alpha is
    /// dropped (restored opaque on resolve).</summary>
    public void Add(uint[] frame, double weight)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (frame.Length < _pixels) throw new ArgumentException("frame smaller than the accumulator", nameof(frame));
        if (weight <= 0.0 || double.IsNaN(weight)) return;

        for (int i = 0; i < _pixels; i++)
        {
            uint p = frame[i];
            int j = i * 3;
            _accum[j]     += ((p >> 16) & 0xFF) * weight;
            _accum[j + 1] += ((p >> 8) & 0xFF) * weight;
            _accum[j + 2] += (p & 0xFF) * weight;
        }
        _weightSum += weight;
        SampleCount++;
    }

    /// <summary>Write the weighted average into <paramref name="dst"/> as opaque
    /// BGRA. When nothing was added (or the weight sum is zero) the destination is
    /// filled opaque black.</summary>
    public void Resolve(uint[] dst)
    {
        if (dst == null) throw new ArgumentNullException(nameof(dst));
        if (dst.Length < _pixels) throw new ArgumentException("destination smaller than the accumulator", nameof(dst));

        double inv = _weightSum > 0.0 ? 1.0 / _weightSum : 0.0;
        for (int i = 0; i < _pixels; i++)
        {
            int j = i * 3;
            int r = (int)(_accum[j] * inv + 0.5);
            int g = (int)(_accum[j + 1] * inv + 0.5);
            int b = (int)(_accum[j + 2] * inv + 0.5);
            if (r < 0) r = 0; else if (r > 255) r = 255;
            if (g < 0) g = 0; else if (g > 255) g = 255;
            if (b < 0) b = 0; else if (b > 255) b = 255;
            dst[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }
    }

    /// <summary>Sub-shutter sample times + weights for one output frame. The frame's
    /// nominal parameter <paramref name="t"/> ∈ [0,1] is spread over a shutter window
    /// of width <paramref name="shutter"/>·<paramref name="frameStep"/> centred on t
    /// (clamped to [0,1]); <paramref name="samples"/> equal-weight taps span it. A
    /// closed shutter (shutter ≤ 0) or a single sample returns the single tap [t],
    /// so the caller renders exactly one frame (byte-identical).</summary>
    public static (double t, double weight)[] ShutterSamples(
        double t, double frameStep, double shutter, int samples)
    {
        if (shutter <= 0.0 || samples <= 1)
            return new[] { (t, 1.0) };

        double half = 0.5 * Math.Min(1.0, shutter) * frameStep;
        double t0 = t - half, t1 = t + half;
        var result = new (double, double)[samples];
        double w = 1.0 / samples;
        for (int s = 0; s < samples; s++)
        {
            double f = samples == 1 ? 0.5 : (double)s / (samples - 1);
            double ts = t0 + (t1 - t0) * f;
            if (ts < 0.0) ts = 0.0; else if (ts > 1.0) ts = 1.0;
            result[s] = (ts, w);
        }
        return result;
    }
}
