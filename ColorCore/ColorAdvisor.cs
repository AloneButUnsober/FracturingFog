// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ColorAdvisor.cs
//
// Roadmap slice S10.6 (PaletteBuilder-Design.md, #392) — the COLOR ADVISOR, the
// artist-*assistant* framing. The parity-twin discipline applied to colour: automated
// checks that GUIDE, not tools that just sit there. It composes the S10.1–S10.4 cores
// into a single review that surfaces:
//
//   * CVD-COLLAPSE   — stop pairs that become confusable under colour-vision deficiency
//                      (PaletteLint.Confusables, S10.2).
//   * SHADOW-CRUSH   — the low end of the ramp loses separation under 3D shading
//                      ("the low third crushes to black") — the 3D-form cost of a dark,
//                      low-lightness tail.
//   * HISTOGRAM-WASTE— the ramp spends range on iterations THIS view never hits
//                      (PaletteHistogram.WastedFraction, S10.3).
//   * CYCLE-SEAM     — a cycling ramp whose ends don't meet in perceptual space
//                      (PaletteHistogram.IsSeamlessCycle, S10.3).
//
// Findings are gentle, dismissible advisories (the UI paints them #FFCC00, the
// colourblind-safe advisory colour FF already uses — never red). A linter for colour.
// Pure + deterministic → asserted in tests.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging;

/// <summary>What a colour advisory is about (roadmap S10.6, #392).</summary>
public enum ColorAdviceKind { CvdCollapse, ShadowCrush, HistogramWaste, CycleSeam }

/// <summary>How strong the advisory is. Both render as the same #FFCC00 advisory colour
/// (never red — the colourblind-safe convention); severity only orders / emphasises.</summary>
public enum AdviceSeverity { Info, Warn }

/// <summary>One advisory finding.</summary>
public readonly record struct ColorAdvice(ColorAdviceKind Kind, AdviceSeverity Severity, string Message);

/// <summary>Composes the S10.1–S10.4 colour cores into a guiding review (roadmap S10.6,
/// #392) — the "linter for colour".</summary>
public static class ColorAdvisor
{
    /// <summary>Review a palette and return advisories (empty = nothing to flag). Each
    /// check reuses the deterministic core it belongs to.</summary>
    /// <param name="stops">The ramp's colours, in order.</param>
    /// <param name="viewHistogram">Optional per-view histogram of the palette parameter
    /// (from <see cref="PaletteHistogram.Build"/>); enables the histogram-waste check.</param>
    /// <param name="cycling">True when the palette cycles — enables the cycle-seam check.</param>
    /// <param name="cvdThreshold">OkLab ΔE below which a CVD-simulated pair counts as
    /// collapsed (default ≈ a JND).</param>
    /// <param name="wasteThreshold">Wasted-range fraction that trips the histogram-waste
    /// advisory (default 0.35 = a third of the ramp unused).</param>
    /// <param name="shadowFactor">Linear-light attenuation modelling deep shade for the
    /// shadow-crush check (default 0.25).</param>
    public static List<ColorAdvice> Review(
        IReadOnlyList<(byte r, byte g, byte b)> stops,
        int[]? viewHistogram = null,
        bool cycling = false,
        float cvdThreshold = 0.02f,
        double wasteThreshold = 0.35,
        float shadowFactor = 0.25f)
    {
        var outp = new List<ColorAdvice>();
        if (stops == null || stops.Count == 0) return outp;

        // CVD-collapse (deutan + protan + tritan).
        var collapses = PaletteLint.Confusables(stops, cvdThreshold,
            CvdType.Deutan, CvdType.Protan, CvdType.Tritan);
        if (collapses.Count > 0)
        {
            var worst = collapses[0];
            var sev = collapses.Count >= 2 ? AdviceSeverity.Warn : AdviceSeverity.Info;
            // Stop numbers are 1-based in user-facing text (the Confusable indices are
            // 0-based) — artists count stops from 1.
            outp.Add(new ColorAdvice(ColorAdviceKind.CvdCollapse, sev,
                $"{collapses.Count} stop pair(s) collapse under colour-vision deficiency " +
                $"(worst: stops {worst.I + 1}&{worst.J + 1} under {worst.Type}). Nudge hue or lightness."));
        }

        // Shadow-crush.
        if (ShadowCrushes(stops, shadowFactor, out float spread))
            outp.Add(new ColorAdvice(ColorAdviceKind.ShadowCrush, AdviceSeverity.Warn,
                $"The low end of the ramp loses separation under 3D shading " +
                $"(shaded spread {spread:0.###}). Lift the dark tail's lightness."));

        // Histogram-waste.
        if (viewHistogram != null)
        {
            double wasted = PaletteHistogram.WastedFraction(viewHistogram);
            if (wasted >= wasteThreshold)
                outp.Add(new ColorAdvice(ColorAdviceKind.HistogramWaste,
                    wasted >= 0.6 ? AdviceSeverity.Warn : AdviceSeverity.Info,
                    $"This view never hits {wasted * 100:0}% of the palette range. Redistribute the stops."));
        }

        // Cycle-seam.
        if (cycling && stops.Count >= 2)
        {
            var a = stops[0];
            var b = stops[^1];
            float seam = PaletteHistogram.CycleSeamDeltaE(a.r, a.g, a.b, b.r, b.g, b.b);
            if (seam > 0.02f)
                outp.Add(new ColorAdvice(ColorAdviceKind.CycleSeam,
                    seam > 0.1f ? AdviceSeverity.Warn : AdviceSeverity.Info,
                    $"The cycling ramp's ends don't meet (seam ΔE {seam:0.###}). Match end to start."));
        }

        return outp;
    }

    /// <summary>True when the ramp's low third loses perceptual separation under deep
    /// shade: attenuate the low-third colours in linear light by <paramref name="shadowFactor"/>
    /// and measure their max pairwise OkLab ΔE (<paramref name="shadedSpread"/>). A small
    /// spread = the dark tail crushes together toward black under 3D shading. Needs at
    /// least two low-third stops.</summary>
    public static bool ShadowCrushes(
        IReadOnlyList<(byte r, byte g, byte b)> stops, float shadowFactor, out float shadedSpread)
    {
        shadedSpread = float.MaxValue;
        if (stops == null) return false;
        int n = stops.Count;
        int lowN = Math.Max(0, (n + 2) / 3);   // ceil(n/3)
        if (lowN < 2) return false;

        var shaded = new (byte r, byte g, byte b)[lowN];
        for (int i = 0; i < lowN; i++)
        {
            var (r, g, b) = stops[i];
            shaded[i] = (Att(r), Att(g), Att(b));
        }
        float maxDe = 0f;
        for (int i = 0; i < lowN; i++)
            for (int j = i + 1; j < lowN; j++)
            {
                float de = PerceptualRamp.DeltaEOk(shaded[i].r, shaded[i].g, shaded[i].b,
                    shaded[j].r, shaded[j].g, shaded[j].b);
                if (de > maxDe) maxDe = de;
            }
        shadedSpread = maxDe;
        return maxDe < 0.04f;

        byte Att(byte c)
        {
            float lin = PerceptualRamp.SrgbToLinear(c / 255f) * Math.Clamp(shadowFactor, 0f, 1f);
            return (byte)(int)MathF.Round(Math.Clamp(PerceptualRamp.LinearToSrgb(lin), 0f, 1f) * 255f);
        }
    }
}
