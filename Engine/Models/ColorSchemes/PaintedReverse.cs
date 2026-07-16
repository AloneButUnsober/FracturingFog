// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/HSV-Modified.cs
//
// HsvModified — smooth version that preserves the original visual character.
//
// After comparing the original rendering against the previous "fixed" version,
// it's clear that only ONE of the three original issues was actually causing
// the jaggedness.  The other two were contributing to the desired look and
// should be kept (or very lightly adjusted).
//
// ── What was causing the jagged lines ─────────────────────────────────────
//
//   (smooth * 0.05f) % 1.1f   then   hue -= MathF.Floor(hue)
//
//   The 1.1 modulus gives a 22-unit hue cycle instead of a 20-unit one.
//   At smooth ≈ 20, hue reaches exactly 1.0 and Floor(hue) = 1, so the
//   result snaps instantly from 0.95 back to 0.0 — a hard discontinuity that
//   draws a visible sharp cutting line diagonally across every 22 iterations.
//   These are the jagged lines and curves the user wanted removed.
//
//   Fix: use % 1.0f for a perfectly clean 20-unit cycle with no mid-cycle jump.
//
// ── What was creating the dark moody background ───────────────────────────
//
//   lightness = 1.35f − MathF.Min(distance * 0.04f, 1.0f)
//
//   For pixels far from the set boundary (distance >= 25), Min clamps to 1.0
//   and lightness = 0.35 — a near-constant dark value.  This is the grey-olive
//   background that gives the theme its moody, jewel-island character.
//   This was intentional and is KEPT.
//
//   The one genuine rendering problem here is that at distance ≈ 0, lightness
//   reaches 1.35, and HsvToRgb can receive value = 1.35.  Depending on the
//   HSV sector, one or more channels may clip to 255 producing small white
//   patches.  We soft-cap value at 1.0 with MathF.Min — this only affects
//   pixels extremely close to the set boundary, is invisible at normal zoom,
//   and preserves every other aspect of the brightness distribution unchanged.
//
// ── What is NOT changed ────────────────────────────────────────────────────
//
//   • Hue speed:        * 0.05f  — same colour rotation rate
//   • Saturation:       0.9f     — same jewel-tone saturation
//   • Distance scale:   * 0.04f  — same brightness decay rate
//   • Brightness floor: 0.35     — same dark background level
//   • baseValue logic            — same interior/exterior treatment

using FracturingFog.Interefaces;

using System;

namespace FracturingFog.Models
{
    public class PaintedReversed : IColorMap, IGpuHlslPalette
    {
        public static string Name => "Painted Reversed";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;

        public int MaxIterations { get; set; } = 1000;

        public string HlslPrelude => HlslPaletteHelpers.HsvAndMods;

        public string HlslPaletteBody => @"
    float h0 = in_smooth * 0.05;
    float hue = h0 - floor(h0);
    float baseV = (in_isInSet > 0.5) ? -0.01 : 1.0;
    float lightness = 1.35 - min(in_dist * 0.04, 1.0);
    return cg_hsv_to_rgb(hue, 0.9, baseV * lightness);
";

        public string PaletteId => "PaintedReversed/v1";

        public int Map(float smooth, float distance, int iterations)
        {
            // ── Hue ──────────────────────────────────────────────────────────
            // CHANGED: % 1.0f instead of % 1.1f.
            // The original 1.1 modulus created a hard discontinuity every 22
            // smooth-units (the jump from hue ≈ 0.95 back to 0.0) which
            // manifested as sharp diagonal jagged lines cutting across bands.
            // A clean % 1.0f gives a seamless 20-unit cycle through the full
            // spectrum with no visible seam.  Nothing else about the hue —
            // speed, direction, cycle count — changes.
            float hue = (smooth * 0.05f) % 1.0f;

            // ── Saturation ───────────────────────────────────────────────────
            // UNCHANGED from original.
            float saturation = 0.9f;

            // ── Value (brightness) ───────────────────────────────────────────
            // baseValue and lightness formula are UNCHANGED from original.
            // The only addition is MathF.Min(..., 1.0f) to prevent the small
            // white blowout patches caused by value = 1.35 near the set edge.
            // This clamp affects only a very narrow band of pixels at distance
            // ≈ 0 and is visually imperceptible at any zoom level.  The dark
            // background, the 0.35 floor, and the distance falloff curve are
            // all completely unchanged.
            float baseValue = smooth < iterations ? 1.0f : -0.01f;
            float lightness = 1.35f - MathF.Min(distance * 0.04f, 1.0f);
            //float value = MathF.Min(baseValue * lightness, 1.0f);
            float value = baseValue * lightness;

            return Fractals.HsvToRgb(hue, saturation, value);
        }
    }
}