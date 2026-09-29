// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/ReliefHeightField.cs
//
// Roadmap slice S11 (3D-Rendering-Roadmap.md, #592) — build the relief height field
// from a chosen per-pixel scalar source. Relief 3D has always extruded the smooth
// iteration count (IHeightFieldSource.SmoothBuffer). An orbit-trap min-distance is
// ALSO a per-pixel scalar field FF already computes when an orbit-trap theme runs
// (MandelbrotCalculator.TrapBuffer, filled from OrbitAccumulator.TrapMin) — the same
// shape of data — so relief can raymarch IT instead of / blended with smooth for
// literal 3D orbit-trap topography (Ring -> concentric ridges, Hexagon -> a hex
// lattice). Pure S1-thesis: reuse a field FF already computes as a height AOV, no new
// geometry machinery. Colour still comes from the theme.
//
// The relief raymarch applies the Relief2DHeightCurve (Log / Sqrt / Linear) to the
// RAW field downstream, so the trap field is normalised into the SAME raw range as
// smooth ([0, max(smooth)]) — inverted, so a small trap distance (the orbit passed
// close to the trap shape) reads as a HIGH ridge — and the existing height-scale +
// camera framing stay in the same ballpark whichever source is chosen.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Selects / blends the per-pixel scalar field that drives the 2D relief
/// height (roadmap S11, #592): smooth iteration count, orbit-trap min-distance, or a
/// blend of the two.</summary>
public static class ReliefHeightField
{
    /// <summary>Build the effective relief height field for <paramref name="source"/>.
    /// <paramref name="smooth"/> is the raw smooth-count field (always present).
    /// <paramref name="trap"/> is the orbit-trap min-distance field (0 = no trap / in
    /// set), or null / empty when no orbit-trap theme is active. Returns
    /// <paramref name="smooth"/> UNCHANGED (same reference) for
    /// <see cref="ReliefHeightSource.Smooth"/>, or when the trap field is unavailable /
    /// all-zero — so the default path is byte-identical and non-Mandelbrot callers
    /// (no trap) transparently fall back. Otherwise returns a NEW array; the caller
    /// copies it into its height buffer.</summary>
    public static float[] Build(float[] smooth, float[]? trap, int n,
        ReliefHeightSource source, double blend)
        => Build(smooth, trap, n, source, blend, null, 0, 0, 0.0, 0.02);

    /// <summary>#1029 — as above, plus the Distance source. <paramref name="distance"/>
    /// is the exterior distance estimate (complex-plane units, 0 in the set) over a
    /// <paramref name="w"/>×<paramref name="h"/> grid whose pixels are
    /// <paramref name="pixelScale"/> wide, so <c>distance / (pixelScale·min(w,h))</c>
    /// is the distance in view units. Height = <see cref="DistanceHeight"/>·exp(−d /
    /// <paramref name="falloff"/>): the set is a raised plateau, filaments are ridges.
    /// Falls back to <paramref name="smooth"/> when no usable distance field is
    /// supplied (a calculator without one, or a deep zoom where the float estimate
    /// underflowed to 0 for most escaped pixels).</summary>
    public static float[] Build(float[] smooth, float[]? trap, int n,
        ReliefHeightSource source, double blend,
        float[]? distance, int w, int h, double pixelScale, double falloff)
    {
        if (smooth == null) throw new ArgumentNullException(nameof(smooth));
        if (n <= 0 || n > smooth.Length) n = smooth.Length;
        if (source == ReliefHeightSource.Smooth) return smooth;
        if (source == ReliefHeightSource.Distance)
            return BuildDistance(smooth, n, distance, w, h, pixelScale, falloff) ?? smooth;
        if (trap == null || trap.Length < n) return smooth;

        // Normalise trap into smooth's raw range, inverted. First the finite trap
        // extent over "active" (trap > 0) pixels, and smooth's max as the target scale.
        float tMin = float.MaxValue, tMax = 0f, sMax = 0f;
        bool anyTrap = false;
        for (int i = 0; i < n; i++)
        {
            float t = trap[i];
            if (t > 0f && !float.IsNaN(t) && !float.IsInfinity(t))
            {
                if (t < tMin) tMin = t;
                if (t > tMax) tMax = t;
                anyTrap = true;
            }
            float s = smooth[i];
            if (s > sMax) sMax = s;
        }
        if (!anyTrap) return smooth;              // no orbit-trap theme ran → fall back
        if (sMax <= 0f) sMax = 1f;                 // all in-set smooth → nominal scale
        float span = tMax - tMin;
        float invSpan = span > 0f ? 1f / span : 0f;

        var outp = new float[n];
        bool blendMode = source == ReliefHeightSource.Blend;
        float wTrap = blendMode ? (float)Math.Clamp(blend, 0.0, 1.0) : 1f;
        float wSmooth = 1f - wTrap;
        for (int i = 0; i < n; i++)
        {
            float t = trap[i];
            // In-set / no-trap pixels read flat (0), matching smooth's in-set 0.
            float th;
            if (t <= 0f || float.IsNaN(t) || float.IsInfinity(t))
                th = 0f;
            else
            {
                float norm = (t - tMin) * invSpan;     // 0 = nearest trap, 1 = farthest
                th = sMax * (1f - norm);               // invert: near-trap -> high ridge
            }
            outp[i] = blendMode ? smooth[i] * wSmooth + th * wTrap : th;
        }
        return outp;
    }

    /// <summary>#1029 — the raw height the Distance source gives the set itself (the
    /// plateau). A fixed nominal value, independent of iteration counts, so the
    /// height tone curve behaves the same at every zoom depth.</summary>
    public const float DistanceHeight = 10f;

    private static float[]? BuildDistance(float[] smooth, int n, float[]? distance,
        int w, int h, double pixelScale, double falloff)
    {
        if (distance == null || distance.Length < n || w <= 0 || h <= 0 || (long)w * h < n) return null;
        double span = pixelScale * Math.Min(w, h);
        if (!(span > 0.0) || double.IsInfinity(span)) return null;
        double f = Math.Clamp(falloff, 0.002, 0.2);

        // Usable only if the escaped pixels carry an estimate (a deep zoom can
        // underflow the float estimate to 0).
        int escaped = 0, missing = 0;
        for (int i = 0; i < n; i++)
        {
            if (smooth[i] > 0f)
            {
                escaped++;
                float d = distance[i];
                if (!(d > 0f) || float.IsInfinity(d)) missing++;
            }
        }
        if (escaped == 0 || missing * 2 > escaped) return null;

        var outp = new float[n];
        double inv = 1.0 / (span * f);
        for (int i = 0; i < n; i++)
        {
            float d = distance[i];
            if (smooth[i] <= 0f) { outp[i] = DistanceHeight; continue; }        // in the set: plateau
            if (!(d > 0f) || float.IsInfinity(d)) { outp[i] = 0f; continue; }   // no estimate: ground
            outp[i] = (float)(DistanceHeight * Math.Exp(-d * inv));
        }
        return outp;
    }
}
