// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ViewTransform.cs
//
// Roadmap slice S2 (3D-Rendering-Roadmap.md, parent #389): render in linear
// light, apply a filmic VIEW TRANSFORM at output. This module is the view-
// transform operator library — the output-stage tonemap that turns a linear
// signal into a display-referred image, the way Blender's Filmic / AgX,
// Nuke's and Resolve's view transforms do.
//
// This first slice operates on the existing 8-bit display buffer: a pixel is
// decoded sRGB -> linear, exposed, run through the chosen tonemap operator, and
// re-encoded sRGB. It is therefore a LOOK operator today (highlight roll-off,
// filmic contrast) rather than true HDR recovery — the render buffer is already
// clipped to [0,1]. When full-float / linear rendering lands (the S2 core), the
// SAME operators apply to the HDR float buffer with real headroom, and to the
// EXR intermediate (S7). The operator math does not change; only its input does.
//
// Contract (the parity-twin discipline for a color transform):
//   * None is the identity — byte-for-byte the untouched buffer. The current
//     look is preserved until the user opts into a transform, exactly as the
//     roadmap requires ("gate behind a selector, default preserving output").
//   * Every operator is a pure, deterministic per-pixel function, so it is
//     twinnable and asserts in tests.
//
// Operators return LINEAR display-referred RGB in [0,1]; Apply does the single
// sRGB output encode. Alpha is passed through untouched (never tonemapped).

using System;

namespace FracturingFog.Imaging;

// The ViewTransform enum lives in Abstractions (Abstractions/Imaging/
// ViewTransform.cs) so FractalViewState / batch options can carry it. This file
// holds the pure operator math.

/// <summary>Pure per-pixel view-transform operators + the BGRA apply pass.</summary>
public static class ViewTransformOps
{
    /// <summary>Apply <paramref name="transform"/> to a BGRA <c>uint[]</c> buffer
    /// in place. <paramref name="exposureEv"/> is a stops multiplier applied in
    /// linear light before the operator (0 = unchanged). <see cref="ViewTransform.None"/>
    /// returns immediately, leaving the buffer byte-identical.</summary>
    public static void Apply(uint[] pixels, int count, ViewTransform transform, float exposureEv = 0f)
    {
        if (transform == ViewTransform.None) return;
        if (pixels == null) throw new ArgumentNullException(nameof(pixels));
        count = Math.Min(count, pixels.Length);
        float expMul = MathF.Pow(2f, exposureEv);
        for (int i = 0; i < count; i++)
            pixels[i] = ApplyToBgra(pixels[i], transform, expMul);
    }

    /// <summary>Transform a single straight-alpha BGRA pixel. Exposed for tests
    /// and for callers that composite pixel-by-pixel.</summary>
    public static uint ApplyToBgra(uint bgra, ViewTransform transform, float expMul)
    {
        if (transform == ViewTransform.None) return bgra;

        uint a = (bgra >> 24) & 0xFF;
        float r = SrgbToLinear(((bgra >> 16) & 0xFF) / 255f) * expMul;
        float g = SrgbToLinear(((bgra >> 8) & 0xFF) / 255f) * expMul;
        float b = SrgbToLinear((bgra & 0xFF) / 255f) * expMul;

        Tonemap(transform, ref r, ref g, ref b);

        byte R = Encode(r), G = Encode(g), B = Encode(b);
        return (a << 24) | ((uint)R << 16) | ((uint)G << 8) | B;
    }

    /// <summary>Run the chosen operator on a single LINEAR-light RGB triple in
    /// place (linear in → linear display-referred [0,1] out). This is the shared
    /// tonemap core — <see cref="ApplyToBgra"/> and the float-intermediate
    /// <see cref="LinearFloatImage"/> path both route through it, so an 8-bit
    /// buffer and a linear-float buffer carrying the same value produce the same
    /// look, byte-for-byte, once encoded. <see cref="ViewTransform.None"/> is a
    /// no-op. Exposure is the caller's job (multiply before calling).</summary>
    public static void Tonemap(ViewTransform transform, ref float r, ref float g, ref float b)
    {
        switch (transform)
        {
            case ViewTransform.Reinhard: Reinhard(ref r, ref g, ref b); break;
            case ViewTransform.AcesFilmic: Aces(ref r, ref g, ref b); break;
            case ViewTransform.AgX: Agx(ref r, ref g, ref b); break;
            case ViewTransform.Filmic: Filmic(ref r, ref g, ref b); break;
        }
    }

    /// <summary>Apply <paramref name="transform"/> to an interleaved LINEAR-light
    /// float RGB buffer (<c>rgb[3i+0..2]</c>, unbounded ≥0 — the S2 core
    /// intermediate with real highlight headroom) in place, leaving linear
    /// display-referred RGB in [0,1] ready for <see cref="LinearToSrgb"/> encode.
    /// <paramref name="exposureEv"/> is stops applied in linear light before the
    /// operator. <see cref="ViewTransform.None"/> returns immediately (byte-
    /// identical to no call), matching the 8-bit <see cref="Apply"/> gate — so the
    /// default look is preserved until the user opts into a transform. Unlike the
    /// 8-bit <see cref="ApplyToBgra"/>, the input is NOT clamped to [0,1]: values
    /// above 1.0 are rolled off by the tonemap instead of hard-clipped, which is
    /// the whole point of the linear intermediate.</summary>
    public static void ApplyLinear(float[] rgb, int pixelCount, ViewTransform transform, float exposureEv = 0f)
    {
        if (transform == ViewTransform.None) return;
        if (rgb == null) throw new ArgumentNullException(nameof(rgb));
        pixelCount = Math.Min(pixelCount, rgb.Length / 3);
        float expMul = MathF.Pow(2f, exposureEv);
        for (int i = 0; i < pixelCount; i++)
        {
            int j = i * 3;
            float r = rgb[j] * expMul, g = rgb[j + 1] * expMul, b = rgb[j + 2] * expMul;
            Tonemap(transform, ref r, ref g, ref b);
            rgb[j] = r; rgb[j + 1] = g; rgb[j + 2] = b;
        }
    }

    // ── operators (linear in → linear display-referred [0,1] out) ─────────

    private static void Reinhard(ref float r, ref float g, ref float b)
    {
        r = r / (1f + r);
        g = g / (1f + g);
        b = b / (1f + b);
    }

    // Narkowicz 2015 ACES filmic approximation. Returns linear; encode after.
    private static void Aces(ref float r, ref float g, ref float b)
    {
        r = AcesChannel(r); g = AcesChannel(g); b = AcesChannel(b);
    }

    private static float AcesChannel(float x)
    {
        const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        return Saturate((x * (a * x + b)) / (x * (c * x + d) + e));
    }

    // Hable / Uncharted 2 filmic curve, normalized to a white point.
    private static void Filmic(ref float r, ref float g, ref float b)
    {
        const float white = 11.2f;
        float inv = 1f / Hable(white);
        r = Saturate(Hable(r) * inv);
        g = Saturate(Hable(g) * inv);
        b = Saturate(Hable(b) * inv);
    }

    private static float Hable(float x)
    {
        const float A = 0.15f, B = 0.50f, C = 0.10f, D = 0.20f, E = 0.02f, F = 0.30f;
        return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
    }

    // Minimal AgX (Troy Sobotka's transform, Benjamin Wrensch's fit): inset
    // matrix → log2 encode → sigmoid contrast → outset matrix. Returns linear;
    // encode after. Gentle, hue-stable highlight desaturation.
    private static void Agx(ref float r, ref float g, ref float b)
    {
        // Inset (sRGB → AgX working space).
        float x = 0.842479062253094f * r + 0.0423282422610123f * g + 0.0423756549057051f * b;
        float y = 0.0784335999999992f * r + 0.878468636469772f * g + 0.0784336f * b;
        float z = 0.0792237451477643f * r + 0.0791661274605434f * g + 0.879142973793104f * b;

        const float minEv = -12.47393f, maxEv = 4.026069f;
        x = (Math.Clamp(MathF.Log2(MathF.Max(x, 1e-10f)), minEv, maxEv) - minEv) / (maxEv - minEv);
        y = (Math.Clamp(MathF.Log2(MathF.Max(y, 1e-10f)), minEv, maxEv) - minEv) / (maxEv - minEv);
        z = (Math.Clamp(MathF.Log2(MathF.Max(z, 1e-10f)), minEv, maxEv) - minEv) / (maxEv - minEv);

        x = AgxContrast(x); y = AgxContrast(y); z = AgxContrast(z);

        // Outset (AgX working space → sRGB).
        r = 1.19687900512017f * x - 0.0528968517574562f * y - 0.0529716355144438f * z;
        g = -0.0980208811401368f * x + 1.15190312990417f * y - 0.0980434501171241f * z;
        b = -0.0990297440797205f * x - 0.0989611768448433f * y + 1.15107367264116f * z;
        r = Saturate(r); g = Saturate(g); b = Saturate(b);
    }

    // 6th-order polynomial sigmoid — Wrensch's agxDefaultContrastApprox.
    private static float AgxContrast(float x)
    {
        float x2 = x * x;
        float x4 = x2 * x2;
        return 15.5f * x4 * x2 - 40.14f * x4 * x + 31.96f * x4 - 6.868f * x2 * x
             + 0.4298f * x2 + 0.1191f * x - 0.00232f;
    }

    // ── color transfer ────────────────────────────────────────────────────

    /// <summary>sRGB → linear (IEC 61966-2-1).</summary>
    public static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>linear → sRGB (IEC 61966-2-1).</summary>
    public static float LinearToSrgb(float c) =>
        c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    /// <summary>Encode one LINEAR display-referred channel to an 8-bit sRGB byte
    /// (saturate → sRGB OETF → round). The single encode used by every view-
    /// transform output path, so the 8-bit and float intermediates land on the
    /// same byte.</summary>
    public static byte Encode(float linear) =>
        (byte)Math.Clamp(LinearToSrgb(Saturate(linear)) * 255f + 0.5f, 0f, 255f);

    private static float Saturate(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
}
