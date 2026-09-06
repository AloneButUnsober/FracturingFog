// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PerceptualRamp.cs
//
// Roadmap slice S10.1 (PaletteBuilder-Design.md, #392) — the PERCEPTUAL CORE.
// Author, interpolate and measure colour in OkLab / OKLCH (Björn Ottosson's
// perceptually-uniform space) rather than sRGB, and emit perceptually-even,
// luminance-structured ramps to the render. Ships the viridis / cividis family
// (cividis is CVD-optimised) and a uniform, CVD-safe (luminance-monotonic) ramp
// generator.
//
// Why here (Engine, not the PaletteBuilder extraction lib): perceptually-even
// ramps FLOW INTO THE RENDER, so the core lives where the render + the headless
// tests can both reach it (the extraction lib is PaletteBuilder-only). The OkLab
// primitives are self-contained (the codebase keeps one copy per assembly by
// design). Unifying insight (design §2): luminance is load-bearing twice — it is
// apparent 3D relief AND the channel that survives colour-blind vision — so one
// discipline (perceptually-uniform, luminance-structured ramps) serves both.

using System;

namespace FracturingFog.Imaging;

/// <summary>Perceptual colour core (roadmap S10.1, #392): OkLab / OKLCH conversions,
/// OkLab ΔE, perceptually-even multi-stop ramp sampling, the viridis / cividis family,
/// and a luminance-monotonic (CVD-safe) ramp generator. All sRGB values are 8-bit;
/// packed colours are 0xAARRGGBB with alpha 0xFF.</summary>
public static class PerceptualRamp
{
    // ── sRGB ↔ linear (float, per channel) ──────────────────────────────────
    public static float SrgbToLinear(float c)
        => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float c)
        => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(MathF.Max(c, 0f), 1f / 2.4f) - 0.055f;

    // ── sRGB bytes ↔ OkLab ───────────────────────────────────────────────────

    /// <summary>sRGB byte triple → OkLab (L≈[0,1], a/b≈[-0.4,0.4]).</summary>
    public static (float L, float a, float b) RgbToOkLab(byte r, byte g, byte b)
    {
        float rl = SrgbToLinear(r / 255f), gl = SrgbToLinear(g / 255f), bl = SrgbToLinear(b / 255f);
        float l = 0.4122214708f * rl + 0.5363325363f * gl + 0.0514459929f * bl;
        float m = 0.2119034982f * rl + 0.6806995451f * gl + 0.1073969566f * bl;
        float s = 0.0883024619f * rl + 0.2817188376f * gl + 0.6299787005f * bl;
        float l_ = MathF.Cbrt(l), m_ = MathF.Cbrt(m), s_ = MathF.Cbrt(s);
        return (
            0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_,
            1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_,
            0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_);
    }

    /// <summary>OkLab → sRGB byte triple, clipping out-of-gamut linear values.</summary>
    public static (byte r, byte g, byte b) OkLabToRgb(float L, float A, float B)
    {
        float l_ = L + 0.3963377774f * A + 0.2158037573f * B;
        float m_ = L - 0.1055613458f * A - 0.0638541728f * B;
        float s_ = L - 0.0894841775f * A - 1.2914855480f * B;
        float l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
        float rl =  4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        float gl = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        float bl = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
        return (Enc(rl), Enc(gl), Enc(bl));
        static byte Enc(float lin)
        {
            float v = LinearToSrgb(lin);
            int i = (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
            return (byte)i;
        }
    }

    // ── OkLab ↔ OKLCH (polar: chroma + hue) ──────────────────────────────────

    /// <summary>OkLab → OKLCH. Hue in degrees [0,360); chroma = hypot(a,b).</summary>
    public static (float L, float C, float H) OkLabToOklch(float L, float a, float b)
    {
        float C = MathF.Sqrt(a * a + b * b);
        float H = MathF.Atan2(b, a) * 180f / MathF.PI;
        if (H < 0f) H += 360f;
        return (L, C, H);
    }

    /// <summary>OKLCH → OkLab. Hue in degrees.</summary>
    public static (float L, float a, float b) OklchToOkLab(float L, float C, float Hdeg)
    {
        float h = Hdeg * MathF.PI / 180f;
        return (L, C * MathF.Cos(h), C * MathF.Sin(h));
    }

    // ── ΔE (OkLab Euclidean — perceptual difference) ─────────────────────────

    /// <summary>Perceptual difference between two sRGB colours: Euclidean distance
    /// in OkLab (a close, cheap proxy — the design's headline metric).</summary>
    public static float DeltaEOk(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var (L1, a1, bb1) = RgbToOkLab(r1, g1, b1);
        var (L2, a2, bb2) = RgbToOkLab(r2, g2, b2);
        float dL = L1 - L2, da = a1 - a2, db = bb1 - bb2;
        return MathF.Sqrt(dL * dL + da * da + db * db);
    }

    // ── perceptually-even multi-stop sampling (interpolate IN OkLab) ─────────

    /// <summary>Anchor stop: position <paramref name="T"/> in [0,1] + an sRGB colour.</summary>
    public readonly record struct Stop(float T, byte R, byte G, byte B);

    /// <summary>Sample a ramp at <paramref name="t"/> (clamped to [0,1]) by interpolating
    /// IN OkLab between the bracketing anchor stops — perceptually even, unlike an sRGB
    /// lerp. <paramref name="stops"/> must be sorted ascending by <c>T</c> and non-empty.</summary>
    public static (byte r, byte g, byte b) SampleOkLab(Stop[] stops, float t)
    {
        if (stops == null || stops.Length == 0) throw new ArgumentException("stops empty", nameof(stops));
        t = Math.Clamp(t, 0f, 1f);
        if (t <= stops[0].T) return (stops[0].R, stops[0].G, stops[0].B);
        int last = stops.Length - 1;
        if (t >= stops[last].T) return (stops[last].R, stops[last].G, stops[last].B);
        int i = 0;
        while (i < last && stops[i + 1].T < t) i++;
        var lo = stops[i];
        var hi = stops[i + 1];
        float span = hi.T - lo.T;
        float u = span > 1e-9f ? (t - lo.T) / span : 0f;
        var (L0, a0, b0) = RgbToOkLab(lo.R, lo.G, lo.B);
        var (L1, a1, b1) = RgbToOkLab(hi.R, hi.G, hi.B);
        return OkLabToRgb(L0 + (L1 - L0) * u, a0 + (a1 - a0) * u, b0 + (b1 - b0) * u);
    }

    /// <summary>Emit <paramref name="count"/> evenly-spaced sRGB stops (0xFFRRGGBB) from a
    /// ramp sampler — the perceptually-even ramp handed to the render.</summary>
    public static uint[] Emit(Func<float, (byte r, byte g, byte b)> ramp, int count)
    {
        if (count < 1) count = 1;
        var outp = new uint[count];
        for (int i = 0; i < count; i++)
        {
            float t = count == 1 ? 0f : (float)i / (count - 1);
            var (r, g, b) = ramp(t);
            outp[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        return outp;
    }

    // ── uniform, CVD-safe (luminance-monotonic) ramp generator ───────────────

    /// <summary>Generate a <paramref name="count"/>-stop ramp between two sRGB endpoints
    /// by lerping IN OkLab, so lightness moves monotonically from end to end — the
    /// luminance-lock that (design §2) both reads as 3D form and survives full
    /// monochromacy (CVD-safe). Endpoints are preserved exactly; returns 0xFFRRGGBB.</summary>
    public static uint[] UniformLuminanceRamp(
        byte r0, byte g0, byte b0, byte r1, byte g1, byte b1, int count)
    {
        if (count < 2) count = 2;
        var (L0, a0, bb0) = RgbToOkLab(r0, g0, b0);
        var (L1, a1, bb1) = RgbToOkLab(r1, g1, b1);
        var outp = new uint[count];
        for (int i = 0; i < count; i++)
        {
            float u = (float)i / (count - 1);
            var (r, g, b) = OkLabToRgb(L0 + (L1 - L0) * u, a0 + (a1 - a0) * u, bb0 + (bb1 - bb0) * u);
            outp[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        return outp;
    }

    // ── built-in perceptual ramps ─────────────────────────────────────────────

    // Viridis — perceptually uniform, colourblind-friendly, greyscale-safe. Anchors
    // sampled from the matplotlib viridis LUT (BSD), the same 10 stops the render's
    // ViridisColorMap uses. Sampled IN OkLab here for a perceptually-even sweep.
    private static readonly Stop[] ViridisStops =
    {
        new(0.00f,  68,   1,  84), new(0.11f,  72,  40, 120), new(0.22f,  62,  74, 137),
        new(0.33f,  49, 104, 142), new(0.44f,  38, 130, 142), new(0.55f,  31, 158, 137),
        new(0.66f,  53, 183, 121), new(0.77f, 110, 206,  88), new(0.88f, 181, 222,  43),
        new(1.00f, 253, 231,  37),
    };

    // Cividis — CVD-optimised (Nuñez/Anderton/Renslow 2018): deutan/protan see a
    // near-identical, luminance-monotonic sweep. Anchors sampled from the matplotlib
    // cividis LUT (CC0): dark blue → desaturated blue-grey → khaki → yellow.
    private static readonly Stop[] CividisStops =
    {
        new(0.00f,   0,  32,  76), new(0.10f,   0,  42, 102), new(0.20f,  47,  62, 101),
        new(0.30f,  74,  78,  98), new(0.40f,  99,  93,  95), new(0.50f, 120, 109,  95),
        new(0.60f, 143, 126,  91), new(0.70f, 167, 144,  84), new(0.80f, 192, 163,  74),
        new(0.90f, 219, 183,  60), new(1.00f, 255, 233,  69),
    };

    /// <summary>Viridis sampled perceptually-evenly in OkLab. <paramref name="t"/> in [0,1].</summary>
    public static (byte r, byte g, byte b) Viridis(float t) => SampleOkLab(ViridisStops, t);

    /// <summary>Cividis (CVD-optimised) sampled perceptually-evenly in OkLab.</summary>
    public static (byte r, byte g, byte b) Cividis(float t) => SampleOkLab(CividisStops, t);
}
