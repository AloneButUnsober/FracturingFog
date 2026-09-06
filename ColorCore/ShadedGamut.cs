// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ShadedGamut.cs
//
// Roadmap slice S10.7 (PaletteBuilder-Design.md, #392) — PREVIEW UNDER 3D LIGHTING.
// A ramp that sings flat can muddy under shading: shadow crushes the low end, and
// specular blows the high end. This core shows the palette's SHADED GAMUT — each
// swatch swept from full-shadow → lit → specular — the way it will actually read
// once 3D lighting hits it.
//
// The discipline the design demands (§S10.7, ties roadmap S2): AUTHOR IN LINEAR,
// PREVIEW THROUGH THE TONEMAP. So a swatch is decoded sRGB → linear, shaded IN
// LINEAR LIGHT (diffuse attenuation for the shadow half, additive white specular
// for the highlight half), exposed, and then run through the SAME view-transform
// operator the render uses at output (ViewTransformOps, S2) before the single sRGB
// encode. A tonemap rolls the highlights off; ViewTransform.None hard-clips them —
// which is exactly what the artist needs to see.
//
// Two per-swatch warnings fall straight out of the sweep:
//   * CRUSH  — under deep shadow the swatch collapses toward black (its shaded
//              low end is within a JND of black): detail will vanish in shade.
//   * BLOW   — under specular the swatch clips toward white and loses its hue
//              (its shaded high end is within a JND of white): the colour washes
//              out in highlights.
//
// Pure + deterministic → asserted in tests. Reuses ViewTransformOps (S2) for the
// tonemap + transfer functions and PerceptualRamp (S10.1) for the OkLab ΔE.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging;

/// <summary>How a swatch fares across the lighting sweep (roadmap S10.7, #392).</summary>
public readonly record struct ShadedSwatch(
    (byte r, byte g, byte b) Albedo,
    (byte r, byte g, byte b)[] Sweep,
    bool CrushesInShadow,
    bool BlowsInSpecular);

/// <summary>Previews a palette's shaded gamut under 3D lighting (roadmap S10.7, #392):
/// shade each swatch full-shadow → lit → specular IN LINEAR LIGHT, then through the
/// render's view transform (S2). Author in linear, preview through the tonemap.</summary>
public static class ShadedGamut
{
    // Where the diffuse sweep ends and the additive specular sweep begins. The first
    // 70% of the sweep ramps ambient → full diffuse; the last 30% piles white
    // specular on top of the fully-lit albedo.
    private const float SpecularStart = 0.7f;

    // A swatch counts as crushed / blown when its shaded extreme lands within this
    // OkLab ΔE of pure black / pure white — a small perceptual neighbourhood of the
    // clip point (a deep-shadow swatch at 3% light already sits ~0.07 from black).
    private const float ExtremeDeltaE = 0.10f;

    /// <summary>Shade one sRGB albedo across the lighting sweep and preview it through
    /// <paramref name="transform"/>. Returns <paramref name="steps"/> sRGB colours from
    /// full-shadow (index 0) through fully-lit to specular (last index).</summary>
    /// <param name="steps">Sweep resolution (clamped to ≥ 2).</param>
    /// <param name="transform">The output view transform to preview through (S2);
    /// <see cref="ViewTransform.None"/> hard-clips highlights instead of rolling off.</param>
    /// <param name="exposureEv">Exposure stops applied in linear light before the tonemap.</param>
    /// <param name="ambient">Shadow-end diffuse floor in [0,1] (deep-shade light level).</param>
    /// <param name="specularStrength">Peak additive white specular energy in linear light.</param>
    public static (byte r, byte g, byte b)[] Sweep(
        byte r, byte g, byte b, int steps,
        ViewTransform transform = ViewTransform.None,
        float exposureEv = 0f,
        float ambient = 0.03f,
        float specularStrength = 0.6f)
    {
        if (steps < 2) steps = 2;
        ambient = Math.Clamp(ambient, 0f, 1f);
        specularStrength = MathF.Max(0f, specularStrength);
        float expMul = MathF.Pow(2f, exposureEv);

        float al = ViewTransformOps.SrgbToLinear(r / 255f);
        float ag = ViewTransformOps.SrgbToLinear(g / 255f);
        float ab = ViewTransformOps.SrgbToLinear(b / 255f);

        var outp = new (byte, byte, byte)[steps];
        for (int i = 0; i < steps; i++)
        {
            float t = (float)i / (steps - 1);
            Shade(al, ag, ab, t, ambient, specularStrength,
                out float lr, out float lg, out float lb);

            lr *= expMul; lg *= expMul; lb *= expMul;
            ViewTransformOps.Tonemap(transform, ref lr, ref lg, ref lb);
            outp[i] = (ViewTransformOps.Encode(lr), ViewTransformOps.Encode(lg), ViewTransformOps.Encode(lb));
        }
        return outp;
    }

    /// <summary>Shade one swatch and classify it: the sweep plus the crush / blow
    /// warnings (design S10.7).</summary>
    public static ShadedSwatch Analyze(
        byte r, byte g, byte b, int steps,
        ViewTransform transform = ViewTransform.None,
        float exposureEv = 0f,
        float ambient = 0.03f,
        float specularStrength = 0.6f)
    {
        var sweep = Sweep(r, g, b, steps, transform, exposureEv, ambient, specularStrength);
        var lo = sweep[0];
        var hi = sweep[^1];
        bool crush = PerceptualRamp.DeltaEOk(lo.r, lo.g, lo.b, 0, 0, 0) < ExtremeDeltaE;
        bool blow = PerceptualRamp.DeltaEOk(hi.r, hi.g, hi.b, 255, 255, 255) < ExtremeDeltaE;
        return new ShadedSwatch((r, g, b), sweep, crush, blow);
    }

    /// <summary>Shade a whole ramp: one <see cref="ShadedSwatch"/> per stop, in order.
    /// The shaded-gamut grid the PaletteBuilder preview draws (stops down, lighting
    /// across).</summary>
    public static List<ShadedSwatch> AnalyzeRamp(
        IReadOnlyList<(byte r, byte g, byte b)> stops, int steps,
        ViewTransform transform = ViewTransform.None,
        float exposureEv = 0f,
        float ambient = 0.03f,
        float specularStrength = 0.6f)
    {
        var outp = new List<ShadedSwatch>();
        if (stops == null) return outp;
        foreach (var (r, g, b) in stops)
            outp.Add(Analyze(r, g, b, steps, transform, exposureEv, ambient, specularStrength));
        return outp;
    }

    // The lighting model, in LINEAR light. t∈[0,1]:
    //   t ∈ [0, SpecularStart]   — diffuse only: albedo × (ambient → 1).
    //   t ∈ (SpecularStart, 1]   — fully-lit albedo + additive white specular (0 → peak).
    // White specular adds equally to all channels (a neutral highlight), so a saturated
    // albedo desaturates toward white as the highlight climbs — the physical wash-out
    // the BLOW check catches.
    private static void Shade(
        float al, float ag, float ab, float t, float ambient, float specularStrength,
        out float r, out float g, out float b)
    {
        float diffuse, spec;
        if (t <= SpecularStart)
        {
            float u = SpecularStart > 0f ? t / SpecularStart : 1f;   // 0 → 1 across the diffuse half
            diffuse = ambient + (1f - ambient) * u;
            spec = 0f;
        }
        else
        {
            diffuse = 1f;
            float u = (t - SpecularStart) / (1f - SpecularStart);     // 0 → 1 across the specular half
            spec = specularStrength * u;
        }
        r = al * diffuse + spec;
        g = ag * diffuse + spec;
        b = ab * diffuse + spec;
    }
}
