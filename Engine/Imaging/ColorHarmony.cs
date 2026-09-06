// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ColorHarmony.cs
//
// Roadmap slice S10.4 (PaletteBuilder-Design.md, #392) — harmony + generation in
// PERCEPTUAL space. Three deterministic generators, all downstream of the S10.1
// perceptual core (PerceptualRamp):
//
//   * ColorHarmony — Adobe-Color harmony rules (complementary / triadic / analogous /
//     split-complementary / tetradic) computed by rotating HUE in OKLCH (keeping
//     lightness + chroma), not in HSL — so companions stay perceptually balanced.
//   * CosinePalette — Inigo Quilez's cosine palette a+b·cos(2π(c·t+d)) per channel,
//     the compact GPU-friendly idiom the ColorGen DSL already uses.
//   * BezierRamp — a chroma.js-style Bézier through control colours IN OkLab
//     (De Casteljau), with optional lightness correction that re-times the curve so
//     lightness rises evenly end-to-end (the design's luminance-structured discipline).
//
// Pure + deterministic → asserted in tests.

using System;

namespace FracturingFog.Imaging;

/// <summary>Adobe-Color-style harmony sets computed in OKLCH (roadmap S10.4, #392):
/// rotate hue, keep lightness + chroma, so the companions stay perceptually balanced.</summary>
public static class ColorHarmony
{
    public enum Scheme { Complementary, Triadic, Analogous, SplitComplementary, Tetradic }

    /// <summary>Rotate a colour's hue by <paramref name="degrees"/> in OKLCH (lightness +
    /// chroma preserved). 0 / 360 is a no-op (± the gamut round-trip).</summary>
    public static (byte r, byte g, byte b) RotateHue(byte r, byte g, byte b, float degrees)
    {
        var (L, a, bb) = PerceptualRamp.RgbToOkLab(r, g, b);
        var (_, C, H) = PerceptualRamp.OkLabToOklch(L, a, bb);
        float h = H + degrees;
        h -= MathF.Floor(h / 360f) * 360f;                 // wrap to [0,360)
        var (L2, a2, b2) = PerceptualRamp.OklchToOkLab(L, C, h);
        return PerceptualRamp.OkLabToRgb(L2, a2, b2);
    }

    /// <summary>The harmony set for <paramref name="scheme"/>, with the base colour first.
    /// <paramref name="analogousDeg"/> sets the analogous / split spread (default 30°).</summary>
    public static (byte r, byte g, byte b)[] Harmony(
        byte r, byte g, byte b, Scheme scheme, float analogousDeg = 30f)
    {
        (byte, byte, byte) Rot(float d) => RotateHue(r, g, b, d);
        return scheme switch
        {
            Scheme.Complementary      => new[] { (r, g, b), Rot(180f) },
            Scheme.Triadic            => new[] { (r, g, b), Rot(120f), Rot(240f) },
            Scheme.Analogous          => new[] { (r, g, b), Rot(analogousDeg), Rot(-analogousDeg) },
            Scheme.SplitComplementary => new[] { (r, g, b), Rot(180f - analogousDeg), Rot(180f + analogousDeg) },
            Scheme.Tetradic           => new[] { (r, g, b), Rot(90f), Rot(180f), Rot(270f) },
            _                         => new[] { (r, g, b) },
        };
    }
}

/// <summary>Inigo Quilez cosine palette: color(t) = a + b·cos(2π(c·t + d)) per channel
/// (roadmap S10.4, #392). The compact, GPU-friendly idiom the ColorGen DSL uses;
/// coefficients are float triples (r,g,b).</summary>
public static class CosinePalette
{
    /// <summary>IQ's default rainbow coefficients (a=.5, b=.5, c=1, d=0/.33/.67).</summary>
    public static ((float r, float g, float b) a, (float r, float g, float b) b,
                   (float r, float g, float b) c, (float r, float g, float b) d) Rainbow =>
        ((0.5f, 0.5f, 0.5f), (0.5f, 0.5f, 0.5f), (1f, 1f, 1f), (0f, 0.33f, 0.67f));

    /// <summary>Sample the cosine palette at <paramref name="t"/> → sRGB (channels clamped
    /// to [0,1] before encoding). Output is the DIRECT sRGB byte value (the idiom authors
    /// in display space), matching the ColorGen cosine maps.</summary>
    public static (byte r, byte g, byte b) Sample(
        (float r, float g, float b) a, (float r, float g, float b) b,
        (float r, float g, float b) c, (float r, float g, float b) d, float t)
    {
        return (Ch(a.r, b.r, c.r, d.r), Ch(a.g, b.g, c.g, d.g), Ch(a.b, b.b, c.b, d.b));
        byte Ch(float av, float bv, float cv, float dv)
        {
            float v = av + bv * MathF.Cos(2f * MathF.PI * (cv * t + dv));
            return (byte)(int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
        }
    }

    /// <summary>Emit <paramref name="count"/> evenly-spaced sRGB stops (0xFFRRGGBB).</summary>
    public static uint[] Emit(
        (float r, float g, float b) a, (float r, float g, float b) b,
        (float r, float g, float b) c, (float r, float g, float b) d, int count)
    {
        if (count < 1) count = 1;
        var outp = new uint[count];
        for (int i = 0; i < count; i++)
        {
            float t = count == 1 ? 0f : (float)i / (count - 1);
            var (r, g, bl) = Sample(a, b, c, d, t);
            outp[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | bl;
        }
        return outp;
    }
}

/// <summary>A Bézier ramp through control colours in OkLab (roadmap S10.4, #392) —
/// chroma.js's high-quality ramp idiom. Optional lightness correction re-times the curve
/// so OkLab lightness rises evenly end-to-end.</summary>
public static class BezierRamp
{
    /// <summary>Sample the Bézier defined by <paramref name="controls"/> (≥1) at
    /// <paramref name="t"/>∈[0,1], interpolating IN OkLab via De Casteljau → sRGB. When
    /// <paramref name="lightnessCorrect"/> is set, <paramref name="t"/> is re-mapped so
    /// the returned lightness matches a linear sweep between the endpoints' lightness
    /// (evens out / monotonises L). Endpoints are preserved.</summary>
    public static (byte r, byte g, byte b) Sample((byte r, byte g, byte b)[] controls, float t, bool lightnessCorrect = false)
    {
        if (controls == null || controls.Length == 0) throw new ArgumentException("controls empty", nameof(controls));
        t = Math.Clamp(t, 0f, 1f);
        if (!lightnessCorrect) return EvalRgb(controls, t);

        // Lightness correction: pick the curve parameter whose L is closest to the
        // linear target L0→L1 for this output position (robust to a non-monotonic curve).
        var (L0, _, _) = PerceptualRamp.RgbToOkLab(controls[0].r, controls[0].g, controls[0].b);
        var last = controls[^1];
        var (L1, _, _) = PerceptualRamp.RgbToOkLab(last.r, last.g, last.b);
        float target = L0 + (L1 - L0) * t;
        const int N = 256;
        float bestT = 0f, bestErr = float.MaxValue;
        for (int i = 0; i <= N; i++)
        {
            float ct = (float)i / N;
            var (Lc, _, _) = EvalOkLab(controls, ct);
            float err = MathF.Abs(Lc - target);
            if (err < bestErr) { bestErr = err; bestT = ct; }
        }
        return EvalRgb(controls, bestT);
    }

    /// <summary>Emit <paramref name="count"/> evenly-spaced sRGB stops (0xFFRRGGBB).</summary>
    public static uint[] Emit((byte r, byte g, byte b)[] controls, int count, bool lightnessCorrect = false)
    {
        if (count < 1) count = 1;
        var outp = new uint[count];
        for (int i = 0; i < count; i++)
        {
            float t = count == 1 ? 0f : (float)i / (count - 1);
            var (r, g, b) = Sample(controls, t, lightnessCorrect);
            outp[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        return outp;
    }

    private static (float L, float a, float b) EvalOkLab((byte r, byte g, byte b)[] controls, float t)
    {
        // De Casteljau in OkLab.
        int n = controls.Length;
        var L = new float[n]; var A = new float[n]; var B = new float[n];
        for (int i = 0; i < n; i++)
            (L[i], A[i], B[i]) = PerceptualRamp.RgbToOkLab(controls[i].r, controls[i].g, controls[i].b);
        for (int k = 1; k < n; k++)
            for (int i = 0; i < n - k; i++)
            {
                L[i] = L[i] + (L[i + 1] - L[i]) * t;
                A[i] = A[i] + (A[i + 1] - A[i]) * t;
                B[i] = B[i] + (B[i + 1] - B[i]) * t;
            }
        return (L[0], A[0], B[0]);
    }

    private static (byte r, byte g, byte b) EvalRgb((byte r, byte g, byte b)[] controls, float t)
    {
        var (L, a, b) = EvalOkLab(controls, t);
        return PerceptualRamp.OkLabToRgb(L, a, b);
    }
}
