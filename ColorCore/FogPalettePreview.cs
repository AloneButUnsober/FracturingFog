// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/FogPalettePreview.cs
//
// Roadmap slice S10.8 (PaletteBuilder-Design.md, #392) — FOG / VOLUMETRIC PALETTE
// PREVIEW. The render already colours the volumetric fog by remapping the in-scatter
// through the active theme's gradient, keyed by OPTICAL DEPTH (#180/#185: thicker fog
// samples deeper into the ramp — ShadingPipeline.VolumetricInScatterSegment). This
// core previews a ramp the same way — as god-rays / haze — and offers a fog-optimised
// sub-ramp.
//
// The model mirrors the render's fog compositing (in LINEAR light, the physical place
// to blend light):
//
//   optical depth  τ  = s · maxDepth        (s ∈ [0,1] sweeps thin → thick)
//   transmittance  T  = exp(-τ)             (Beer–Lambert)
//   in-scatter        = ramp sampled at (1 − T)   (the render's optical-depth key)
//   composited     out = bg · T + inscatter · (1 − T)   (haze over the backdrop)
//
// So a thin sample is almost pure backdrop and a thick sample is almost pure fog
// colour — exactly the god-ray/haze read. Two things fall out:
//   * FOG-OPTIMISED SUB-RAMP — fog in-scatter ADDS light, so near-black ramp
//     entries contribute nothing and dark fog just mutes the scene; the good fog
//     sub-ramp is the ramp's brighter reach, luminance-ascending so thicker fog
//     reads as MORE. FogOptimizedSubRamp culls the dark tail and re-emits that.
//   * WASH-OUT WARNING — if the composited sweep barely moves (the ramp sits too
//     close to the backdrop, or is too flat), the fog produces no visible gradient.
//
// Pure + deterministic → asserted in tests. Reuses PerceptualRamp (OkLab sampling +
// ΔE) and the same sRGB↔linear transfer the render composites in.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging;

/// <summary>Previews a palette as volumetric fog / god-rays (roadmap S10.8, #392):
/// composite the ramp over a backdrop across optical depth, and derive a fog-optimised
/// sub-ramp. Mirrors the render's #180/#185 optical-depth fog remap.</summary>
public static class FogPalettePreview
{
    /// <summary>Default peak optical depth of the sweep (τ = 4 → T ≈ 0.018 at the
    /// thick end, essentially full fog).</summary>
    public const float DefaultMaxOpticalDepth = 4f;

    // Below this OkLab lightness a fog colour reads as "no light added" — culled from
    // the fog-optimised sub-ramp.
    private const float FogDarkFloor = 0.15f;

    // The composited sweep must span at least this OkLab ΔE end-to-end or the fog
    // produces no visible gradient (wash-out).
    private const float WashOutDeltaE = 0.08f;

    /// <summary>Sample the fog in-scatter colour at an optical-depth fraction
    /// <paramref name="opticalDepthFraction"/> = (1 − T) ∈ [0,1] — the render's fog key.
    /// Thin fog (≈0) samples the ramp start, thick fog (≈1) samples the ramp end,
    /// interpolated perceptually in OkLab.</summary>
    public static (byte r, byte g, byte b) FogInscatter(
        IReadOnlyList<(byte r, byte g, byte b)> fogStops, float opticalDepthFraction)
    {
        var anchors = ToStops(fogStops);
        return PerceptualRamp.SampleOkLab(anchors, opticalDepthFraction);
    }

    /// <summary>Composite the fog ramp over a backdrop across the optical-depth sweep
    /// (thin → thick). Each step: T = exp(-τ); out = bg·T + inscatter·(1−T), blended in
    /// LINEAR light (the render's fog arithmetic). Returns <paramref name="steps"/> sRGB
    /// colours — the god-ray/haze strip.</summary>
    public static (byte r, byte g, byte b)[] FogSweep(
        IReadOnlyList<(byte r, byte g, byte b)> fogStops, int steps,
        byte bgR, byte bgG, byte bgB,
        float density = 1f, float maxOpticalDepth = DefaultMaxOpticalDepth)
    {
        if (steps < 2) steps = 2;
        density = MathF.Max(0f, density);
        maxOpticalDepth = MathF.Max(0f, maxOpticalDepth);
        var anchors = ToStops(fogStops);

        float bgLr = PerceptualRamp.SrgbToLinear(bgR / 255f);
        float bgLg = PerceptualRamp.SrgbToLinear(bgG / 255f);
        float bgLb = PerceptualRamp.SrgbToLinear(bgB / 255f);

        var outp = new (byte, byte, byte)[steps];
        for (int i = 0; i < steps; i++)
        {
            float s = (float)i / (steps - 1);
            float tau = s * maxOpticalDepth * density;
            float T = MathF.Exp(-tau);
            float frac = 1f - T;

            var (ir, ig, ib) = PerceptualRamp.SampleOkLab(anchors, frac);
            float inLr = PerceptualRamp.SrgbToLinear(ir / 255f);
            float inLg = PerceptualRamp.SrgbToLinear(ig / 255f);
            float inLb = PerceptualRamp.SrgbToLinear(ib / 255f);

            float or = bgLr * T + inLr * frac;
            float og = bgLg * T + inLg * frac;
            float ob = bgLb * T + inLb * frac;
            outp[i] = (Enc(or), Enc(og), Enc(ob));
        }
        return outp;

        static byte Enc(float lin) =>
            (byte)Math.Clamp(PerceptualRamp.LinearToSrgb(Math.Clamp(lin, 0f, 1f)) * 255f + 0.5f, 0f, 255f);
    }

    /// <summary>True when the fog ramp produces no visible haze gradient over the
    /// backdrop: the composited sweep's max pairwise OkLab ΔE is below a small
    /// threshold (the ramp sits too close to the backdrop, or is too flat / dark to
    /// read as fog). <paramref name="span"/> returns that measured ΔE.</summary>
    public static bool WashesOut(
        IReadOnlyList<(byte r, byte g, byte b)> fogStops,
        byte bgR, byte bgG, byte bgB,
        out float span,
        float density = 1f, float maxOpticalDepth = DefaultMaxOpticalDepth)
    {
        var sweep = FogSweep(fogStops, 16, bgR, bgG, bgB, density, maxOpticalDepth);
        float maxDe = 0f;
        for (int i = 0; i < sweep.Length; i++)
            for (int j = i + 1; j < sweep.Length; j++)
            {
                float de = PerceptualRamp.DeltaEOk(
                    sweep[i].r, sweep[i].g, sweep[i].b, sweep[j].r, sweep[j].g, sweep[j].b);
                if (de > maxDe) maxDe = de;
            }
        span = maxDe;
        return maxDe < WashOutDeltaE;
    }

    /// <summary>Derive a fog-optimised sub-ramp from a source ramp: fog in-scatter adds
    /// light, so the ramp's dark reach (OkLab L &lt; a floor) contributes nothing and is
    /// culled, and the survivors are re-emitted luminance-ASCENDING so thicker fog reads
    /// as MORE. Returns <paramref name="count"/> stops (0xFFRRGGBB) sampled perceptually
    /// in OkLab. If the whole ramp is below the dark floor, its brightest stop anchors a
    /// lifted ramp so the result is never empty.</summary>
    public static uint[] FogOptimizedSubRamp(
        IReadOnlyList<(byte r, byte g, byte b)> stops, int count, float minLightness = FogDarkFloor)
    {
        if (count < 2) count = 2;
        var bright = new List<(byte r, byte g, byte b, float L)>();
        if (stops != null)
            foreach (var (r, g, b) in stops)
            {
                float L = PerceptualRamp.RgbToOkLab(r, g, b).L;
                if (L >= minLightness) bright.Add((r, g, b, L));
            }

        // Nothing bright enough → anchor on the brightest source stop and lift toward
        // white so the fog still reads.
        if (bright.Count < 2)
        {
            (byte r, byte g, byte b) anchor = (128, 128, 128);
            if (stops != null && stops.Count > 0)
            {
                float bestL = -1f;
                foreach (var (r, g, b) in stops)
                {
                    float L = PerceptualRamp.RgbToOkLab(r, g, b).L;
                    if (L > bestL) { bestL = L; anchor = (r, g, b); }
                }
            }
            return PerceptualRamp.UniformLuminanceRamp(
                anchor.r, anchor.g, anchor.b, 255, 255, 255, count);
        }

        // Luminance-ascending survivors → perceptual anchor stops → resample.
        bright.Sort((a, b) => a.L.CompareTo(b.L));
        var anchors = new PerceptualRamp.Stop[bright.Count];
        for (int i = 0; i < bright.Count; i++)
        {
            float t = (float)i / (bright.Count - 1);
            anchors[i] = new PerceptualRamp.Stop(t, bright[i].r, bright[i].g, bright[i].b);
        }
        return PerceptualRamp.Emit(u => PerceptualRamp.SampleOkLab(anchors, u), count);
    }

    // Distribute a stop list evenly across [0,1] as perceptual anchor stops. A single
    // stop is duplicated to a valid two-anchor ramp.
    private static PerceptualRamp.Stop[] ToStops(IReadOnlyList<(byte r, byte g, byte b)> stops)
    {
        if (stops == null || stops.Count == 0)
            return new[] { new PerceptualRamp.Stop(0f, 0, 0, 0), new PerceptualRamp.Stop(1f, 0, 0, 0) };
        if (stops.Count == 1)
        {
            var (r, g, b) = stops[0];
            return new[] { new PerceptualRamp.Stop(0f, r, g, b), new PerceptualRamp.Stop(1f, r, g, b) };
        }
        var anchors = new PerceptualRamp.Stop[stops.Count];
        for (int i = 0; i < stops.Count; i++)
        {
            float t = (float)i / (stops.Count - 1);
            anchors[i] = new PerceptualRamp.Stop(t, stops[i].r, stops[i].g, stops[i].b);
        }
        return anchors;
    }
}
