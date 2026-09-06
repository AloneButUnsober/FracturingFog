// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ReliefLegibility.cs
//
// Roadmap slice S10.9 (PaletteBuilder-Design.md, #392) — RELIEF = LUMINANCE IS FORM.
// The design's §2 thesis, restated as a 3D tool. Relief 3D extrudes a 2D fractal into
// a heightfield and shades it, so on screen APPARENT FORM IS LUMINANCE: a slope that
// climbs should get brighter, a valley should get darker. A ramp whose lightness moves
// MONOTONICALLY along the height axis makes relief read as genuinely raised; a ramp
// whose lightness reverses (bright → dark → bright) makes a rising slope read as
// up-then-down — the eye loses the form, the relief flattens. A ramp with too little
// lightness range gives no shading gradient at all.
//
// This is the SAME luminance-lock that keeps a ramp legible under colour-vision
// deficiency (S10.2 — luminance is the channel that survives when hue collapses). One
// discipline, two payoffs: relief that reads as 3D AND a palette a colourblind viewer
// can still follow.
//
// So the core:
//   * Analyze — does the ramp's lightness climb/fall monotonically with enough range
//               to read as relief? Reports the spread, the reversal count, and the flat
//               / non-monotonic verdicts.
//   * LockLuminance — the repair: keep each stop's HUE and CHROMA (OKLCH C, H) but
//               overwrite lightness with a monotonic ascending spine, so the artist's
//               colour progression survives while relief reads as form again.
//
// Pure + deterministic → asserted in tests. Reuses PerceptualRamp (OkLab / OKLCH).

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging;

/// <summary>How well a ramp's lightness structure reads as 3D relief (roadmap S10.9,
/// #392).</summary>
public readonly record struct ReliefReport(
    bool ReadsAsRelief,
    bool Flat,
    bool NonMonotonic,
    float LuminanceSpread,
    int Reversals);

/// <summary>Measures and repairs a ramp's relief legibility (roadmap S10.9, #392):
/// luminance monotonicity + range are what make extruded relief read as raised 3D —
/// the same luminance-lock that survives colour-vision deficiency (S10.2).</summary>
public static class ReliefLegibility
{
    // A lightness step below this is treated as flat (encode noise / a plateau), so it
    // neither sets nor breaks the monotone direction.
    private const float FlatStepEps = 0.01f;

    // Minimum end-to-end OkLab lightness spread to give relief a readable shading
    // gradient. Below it the surface shades almost uniformly — no apparent form.
    private const float DefaultMinSpread = 0.20f;

    /// <summary>Analyze a ramp's relief legibility. <paramref name="minSpread"/> is the
    /// smallest OkLab lightness range that still reads as form.</summary>
    public static ReliefReport Analyze(
        IReadOnlyList<(byte r, byte g, byte b)> stops, float minSpread = DefaultMinSpread)
    {
        if (stops == null || stops.Count < 2)
            return new ReliefReport(false, true, false, 0f, 0);

        int n = stops.Count;
        var L = new float[n];
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            L[i] = PerceptualRamp.RgbToOkLab(stops[i].r, stops[i].g, stops[i].b).L;
            if (L[i] < lo) lo = L[i];
            if (L[i] > hi) hi = L[i];
        }
        float spread = hi - lo;

        // Count direction reversals, ignoring flat (sub-eps) steps.
        int reversals = 0;
        int dir = 0;   // -1 down, +1 up, 0 undecided
        for (int i = 1; i < n; i++)
        {
            float d = L[i] - L[i - 1];
            if (MathF.Abs(d) < FlatStepEps) continue;
            int sd = d > 0f ? 1 : -1;
            if (dir != 0 && sd != dir) reversals++;
            dir = sd;
        }

        bool flat = spread < minSpread;
        bool nonMono = reversals > 0;
        bool reads = !flat && !nonMono;
        return new ReliefReport(reads, flat, nonMono, spread, reversals);
    }

    /// <summary>Repair a ramp so relief reads as form: keep each stop's HUE and CHROMA
    /// (OKLCH C, H) but replace its lightness with a monotonically-ascending spine, then
    /// resample to <paramref name="count"/> stops (0xFFRRGGBB) in OkLab. The artist's
    /// colour progression is preserved; only the luminance is locked monotonic. The
    /// spine spans the ramp's own lightness endpoints, widened to at least
    /// <paramref name="minSpread"/> (clamped to a safe [0.05, 0.95]) so the result always
    /// has readable shading range.</summary>
    public static uint[] LockLuminance(
        IReadOnlyList<(byte r, byte g, byte b)> stops, int count, float minSpread = DefaultMinSpread)
    {
        if (count < 2) count = 2;
        if (stops == null || stops.Count == 0)
            return PerceptualRamp.Emit(_ => (0, 0, 0), count);
        if (stops.Count == 1)
        {
            // Single colour → a monotonic sweep in its own hue from dark to light.
            var (sr, sg, sb) = stops[0];
            var (sL, sa, sb2) = PerceptualRamp.RgbToOkLab(sr, sg, sb);
            var (_, C1, H1) = PerceptualRamp.OkLabToOklch(sL, sa, sb2);
            var (l0, a0, b0) = PerceptualRamp.OklchToOkLab(0.15f, C1, H1);
            var (l1, a1, b1) = PerceptualRamp.OklchToOkLab(0.90f, C1, H1);
            var (r0, g0, bl0) = PerceptualRamp.OkLabToRgb(l0, a0, b0);
            var (r1, g1, bl1) = PerceptualRamp.OkLabToRgb(l1, a1, b1);
            var anchors1 = new[]
            {
                new PerceptualRamp.Stop(0f, r0, g0, bl0),
                new PerceptualRamp.Stop(1f, r1, g1, bl1),
            };
            return PerceptualRamp.Emit(u => PerceptualRamp.SampleOkLab(anchors1, u), count);
        }

        int n = stops.Count;
        // Per-stop chroma + hue, and the existing lightness endpoints.
        var C = new float[n];
        var H = new float[n];
        float firstL = 0f, lastL = 0f;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var (L, a, b) = PerceptualRamp.RgbToOkLab(stops[i].r, stops[i].g, stops[i].b);
            var (_, c, h) = PerceptualRamp.OkLabToOklch(L, a, b);
            C[i] = c; H[i] = h;
            if (i == 0) firstL = L;
            if (i == n - 1) lastL = L;
            if (L < lo) lo = L;
            if (L > hi) hi = L;
        }

        // Ascending spine by default; if the ramp overall runs dark→light keep that
        // sense, else (light→dark) descend — respect the artist's intended direction.
        bool descend = lastL < firstL;
        float span = MathF.Max(hi - lo, minSpread);
        float mid = 0.5f * (lo + hi);
        float sLo = Math.Clamp(mid - span * 0.5f, 0.05f, 0.95f);
        float sHi = Math.Clamp(mid + span * 0.5f, 0.05f, 0.95f);

        var anchors = new PerceptualRamp.Stop[n];
        for (int i = 0; i < n; i++)
        {
            float u = (float)i / (n - 1);
            float targetL = descend ? sHi + (sLo - sHi) * u : sLo + (sHi - sLo) * u;
            var (lr, la, lb) = PerceptualRamp.OklchToOkLab(targetL, C[i], H[i]);
            var (rr, gg, bb) = PerceptualRamp.OkLabToRgb(lr, la, lb);
            anchors[i] = new PerceptualRamp.Stop(u, rr, gg, bb);
        }
        return PerceptualRamp.Emit(uu => PerceptualRamp.SampleOkLab(anchors, uu), count);
    }
}
