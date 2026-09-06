// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/CvdAnalysis.cs
//
// Roadmap slice S10.2 (PaletteBuilder-Design.md, #392) — the CVD-FIRST suite, the
// differentiator. Almost no creative colour tool is built colour-blind-first; this is.
//   * Live CVD SIMULATION — deutan / protan / tritan / monochromacy (Machado 2009).
//   * CONFUSABILITY linter — flag stop pairs whose ΔE in CVD-SIMULATED space collapses.
//   * LUMINANCE-lock check — a ramp whose lightness is monotonic survives full
//     monochromacy (and reads as 3D relief — the design's "luminance twice" thesis).
//   * CVD-safe CATEGORICAL palette — the Okabe-Ito 8-colour set.
// All deterministic → asserted in tests (the colour analog of the render parity twin).
//
// Machado, Oliveira & Fernandes (2009), "A Physiologically-based Model for Simulation
// of Color Vision Deficiency": 3×3 matrices applied in LINEAR RGB. Severity 0 is the
// identity; this lerps identity→(severity-1.0 matrix) by `severity` (the standard
// simple approximation; the paper tabulates 0.0–1.0 in 0.1 steps that this brackets).

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging;

/// <summary>The colour-vision-deficiency types the suite simulates.</summary>
public enum CvdType { Protan, Deutan, Tritan, Monochromacy }

/// <summary>CVD simulation (Machado 2009) — render a colour as a given deficiency
/// sees it (roadmap S10.2, #392). Works in linear RGB; input / output are sRGB bytes.</summary>
public static class CvdSimulation
{
    // Machado et al. 2009 severity-1.0 matrices (row-major, linear RGB).
    private static readonly float[] Protan =
    { 0.152286f, 1.052583f, -0.204868f, 0.114503f, 0.786281f, 0.099216f, -0.003882f, -0.048116f, 1.051998f };
    private static readonly float[] Deutan =
    { 0.367322f, 0.860646f, -0.227968f, 0.280085f, 0.672501f, 0.047413f, -0.011820f, 0.042940f, 0.968881f };
    private static readonly float[] Tritan =
    { 1.255528f, -0.076749f, -0.178779f, -0.078411f, 0.930809f, 0.147602f, 0.004733f, 0.691367f, 0.303900f };

    /// <summary>Simulate how <paramref name="type"/> (at <paramref name="severity"/> in
    /// [0,1]; 1 = full dichromacy) sees the sRGB colour. Monochromacy collapses to the
    /// luminance grey. Severity 0 is a no-op (identity), so it is byte-stable.</summary>
    public static (byte r, byte g, byte b) Simulate(byte r, byte g, byte b, CvdType type, float severity = 1f)
    {
        severity = Math.Clamp(severity, 0f, 1f);
        float rl = PerceptualRamp.SrgbToLinear(r / 255f);
        float gl = PerceptualRamp.SrgbToLinear(g / 255f);
        float bl = PerceptualRamp.SrgbToLinear(b / 255f);

        float or, og, ob;
        if (type == CvdType.Monochromacy)
        {
            // Rec.709 luminance in linear light → an achromatic grey, lerped by severity.
            float y = 0.2126f * rl + 0.7152f * gl + 0.0722f * bl;
            or = rl + (y - rl) * severity;
            og = gl + (y - gl) * severity;
            ob = bl + (y - bl) * severity;
        }
        else
        {
            float[] m = type switch
            {
                CvdType.Protan => Protan,
                CvdType.Deutan => Deutan,
                _              => Tritan,
            };
            // Full-severity simulated colour, then lerp identity→sim by severity.
            float sr = m[0] * rl + m[1] * gl + m[2] * bl;
            float sg = m[3] * rl + m[4] * gl + m[5] * bl;
            float sb = m[6] * rl + m[7] * gl + m[8] * bl;
            or = rl + (sr - rl) * severity;
            og = gl + (sg - gl) * severity;
            ob = bl + (sb - bl) * severity;
        }
        return (Enc(or), Enc(og), Enc(ob));

        static byte Enc(float lin)
        {
            float v = PerceptualRamp.LinearToSrgb(lin);
            return (byte)(int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
        }
    }
}

/// <summary>Palette linters (roadmap S10.2, #392): confusability under CVD, luminance
/// monotonicity, and the CVD-safe categorical set. Deterministic and side-effect-free
/// — the advisory checks S10.6 later surfaces as gentle #FFCC00 hints.</summary>
public static class PaletteLint
{
    /// <summary>Okabe &amp; Ito's 8-colour qualitative palette — the accepted CVD-safe
    /// set for categorical / banded colouring (black, orange, sky blue, bluish green,
    /// yellow, blue, vermillion, reddish purple).</summary>
    public static readonly (byte r, byte g, byte b)[] OkabeIto =
    {
        (0, 0, 0), (230, 159, 0), (86, 180, 233), (0, 158, 115),
        (240, 228, 66), (0, 114, 178), (213, 94, 0), (204, 121, 167),
    };

    /// <summary>A pair of palette entries that collapse (become confusable) under a
    /// CVD type: their perceptual distance in CVD-SIMULATED space.</summary>
    public readonly record struct Confusable(int I, int J, CvdType Type, float DeltaE);

    /// <summary>Flag every stop pair whose OkLab ΔE, measured AFTER CVD simulation,
    /// falls below <paramref name="threshold"/> for any of <paramref name="types"/>
    /// (default deutan + protan, the common deficiencies). These are the pairs that
    /// "look different to you but the same to a deuteranope — nudge?". Sorted by ΔE
    /// ascending (worst collapse first).</summary>
    public static List<Confusable> Confusables(
        IReadOnlyList<(byte r, byte g, byte b)> stops, float threshold, params CvdType[] types)
    {
        if (types == null || types.Length == 0) types = new[] { CvdType.Deutan, CvdType.Protan };
        var outp = new List<Confusable>();
        if (stops == null) return outp;
        foreach (var type in types)
        {
            var sim = new (byte r, byte g, byte b)[stops.Count];
            for (int i = 0; i < stops.Count; i++)
                sim[i] = CvdSimulation.Simulate(stops[i].r, stops[i].g, stops[i].b, type);
            for (int i = 0; i < stops.Count; i++)
                for (int j = i + 1; j < stops.Count; j++)
                {
                    float de = PerceptualRamp.DeltaEOk(sim[i].r, sim[i].g, sim[i].b, sim[j].r, sim[j].g, sim[j].b);
                    if (de < threshold) outp.Add(new Confusable(i, j, type, de));
                }
        }
        outp.Sort((a, b) => a.DeltaE.CompareTo(b.DeltaE));
        return outp;
    }

    /// <summary>True when the ramp's OkLab lightness is monotonic (non-increasing OR
    /// non-decreasing within <paramref name="tol"/>) — the luminance lock that survives
    /// full monochromacy and reads as 3D relief. Fewer than two stops is trivially true.</summary>
    public static bool IsLuminanceMonotonic(IReadOnlyList<(byte r, byte g, byte b)> stops, float tol = 0.001f)
    {
        if (stops == null || stops.Count < 2) return true;
        bool up = true, down = true;
        var (pl, _, _) = PerceptualRamp.RgbToOkLab(stops[0].r, stops[0].g, stops[0].b);
        for (int i = 1; i < stops.Count; i++)
        {
            var (l, _, _) = PerceptualRamp.RgbToOkLab(stops[i].r, stops[i].g, stops[i].b);
            if (l < pl - tol) up = false;
            if (l > pl + tol) down = false;
            pl = l;
        }
        return up || down;
    }
}
