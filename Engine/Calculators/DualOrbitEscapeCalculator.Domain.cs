// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Domain.cs (#1120, epic #1114 S6)
//
// Böttcher-ratio domain colouring (Docs/Technical/DualOrbit-Coloring-RnD.md
// §3.D). With φ_s the Böttcher coordinate of u → u² + s (φ_s(u) ~ u at ∞,
// φ_s(u² + s) = φ_s(u)²), the two orbits' level-1 points z₁ = s and c₁ = c² + s
// give one complex invariant
//
//     w = log(φ_s(c₁) / φ_s(z₁)) = (G_c − G_z) + 2πi·Δθ
//
// G = log|φ_s| is the Green function (escape potential), θ = arg φ_s / 2π the
// external angle. Both parts are bailout-independent (G from the smooth count,
// θ by backward lifting — the same intrinsic constructions as GreenRatio /
// ExternalAngleDelta, #970). Note the real part is the Green DIFFERENCE, not
// GreenRatio's log-ratio (verified, doc §5).
//
// Domain colouring of w: hue = Δθ (cyclic), contour bands = G_c − G_z
// (sawtooth, DualOrbitContourDensity per unit), optional angular grid. Where the
// critical orbit is bounded (s ∈ M) φ_s(z₁) is undefined; there the colouring
// falls back to w = log φ_s(c₁) = G_c + 2πi·θ_c, so M's interior still shows the
// c-orbit's own Böttcher structure (live wherever the c-orbit escapes).
//
// Branch cuts. φ_s is analytic only on {G_s > G_s(0)}, outside the critical
// point's level curve (G_s(0) = G(z₁)/2). Where the c-orbit's level-1 point sits
// below it, φ_s(c₁) has no canonical value: backward lifting still returns an
// angle, but it jumps across cuts (straight-looking seams in the render — the
// session's smoke render; verified that every seam lies in G(c₁) ≤ G(0)).
// DualOrbitDomainMarkCuts (default on) dims those pixels; off shows the raw lift.
// Inside M (critical orbit bounded) G_s(0) = 0, so the fallback is always valid.
//
// Default palette: a cyclic path in OkLab with a = 0 — lightness and the
// blue↔yellow axis only — so no information rides on red↔green.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Models;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    /// <summary>#1120 — Im w / 2π per pixel from the last BoettcherDomain colour
    /// pass: Δθ = θ_c − θ_z (turns, [0, 1)), or θ_c alone where the critical orbit
    /// is bounded. NaN where the c-orbit is bounded. Empty in other modes.</summary>
    public float[] BoettcherAngleTurns { get; private set; } = Array.Empty<float>();

    /// <summary>#1120 — Re w per pixel: G_c − G_z (level-1 Green functions), or G_c
    /// alone where the critical orbit is bounded. NaN where the c-orbit is bounded.</summary>
    public float[] BoettcherLogModulus { get; private set; } = Array.Empty<float>();

    /// <summary>#1120 — true where w is analytically defined: G(c₁) &gt; G_s(0) =
    /// G(z₁)/2 (always, inside M). False = below the critical level, where the
    /// lifted angle has branch cuts. False also where the c-orbit is bounded.</summary>
    public bool[] BoettcherInDomain { get; private set; } = Array.Empty<bool>();

    /// <summary>Lightness factor applied to pixels outside the Böttcher domain
    /// when DualOrbitDomainMarkCuts is on.</summary>
    internal const double OutsideDomainDim = 0.6;

    // OkLab cyclic map (a = 0): L = 0.64 + 0.18·cos φ, b = 0.13·sin φ.
    internal const float DomainLMid = 0.64f, DomainLAmp = 0.18f, DomainBAmp = 0.13f;

    private void ColorizeDomain(in GeometryKey key, uint interiorColor, uint? oobColor, CancellationToken ct)
    {
        int n = Width * Height;
        if (BoettcherAngleTurns.Length != n) { BoettcherAngleTurns = new float[n]; BoettcherLogModulus = new float[n]; BoettcherInDomain = new bool[n]; }
        float[] angOut = BoettcherAngleTurns, modOut = BoettcherLogModulus; bool[] inDom = BoettcherInDomain;

        var fp = FractalParameters;
        bool markCuts = fp.DualOrbitDomainMarkCuts;
        double density = Math.Max(0.0, fp.DualOrbitContourDensity);
        bool grid = fp.DualOrbitDomainGrid && density > 0;
        bool theme = fp.DualOrbitDomainPalette == DualOrbitDomainPalette.Theme;
        int maxIter = key.MaxIter;
        var cm = ColorMap;
        double twoLogR = 2.0 * Math.Log(key.Bailout);
        bool quat = key.Map == DualOrbitMap.Quaternion;

        int width = Width, height = Height;
        float[] smZ = _smZ, smC = _smC, thZ = _thZ, thC = _thC; byte[] flags = _flags;
        bool haveAngles = thC.Length == n;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x;
                byte f = flags[idx];
                bool zEsc = (f & FlagZEsc) != 0, cEsc = (f & FlagCEsc) != 0, cFirst = (f & FlagCFirst) != 0;
                float tc = haveAngles ? thC[idx] : float.NaN;
                if (quat || !cEsc || float.IsNaN(tc))
                {
                    angOut[idx] = float.NaN; modOut[idx] = float.NaN; inDom[idx] = false;
                    ColorBuffer[idx] = interiorColor;
                    continue;
                }

                // Level-1 Green function from the smooth count: G = 2·ln R·2^(−smooth).
                double gC = twoLogR * Math.Pow(2.0, -smC[idx]);
                double turns, logMod;
                bool valid = true;
                float tz = thZ[idx];
                if (zEsc && !float.IsNaN(tz))
                {
                    double gZ = twoLogR * Math.Pow(2.0, -smZ[idx]);
                    turns = Frac(tc - tz);
                    logMod = gC - gZ;
                    valid = gC > 0.5 * gZ;            // above the critical level G_s(0)
                }
                else { turns = tc; logMod = gC; }     // s ∈ M: φ_s(c₁) alone (G_s(0) = 0)
                angOut[idx] = (float)turns; modOut[idx] = (float)logMod; inDom[idx] = valid;

                if (oobColor is uint oob && cFirst) { ColorBuffer[idx] = oob; continue; }

                // Contour sawtooth on Re w + optional angular grid → lightness factor.
                double k = 1.0;
                if (density > 0) k = 0.75 + 0.25 * Frac(density * logMod);
                if (grid)
                {
                    double g = Frac(turns * density);
                    if (Math.Min(g, 1.0 - g) < 0.03) k *= 0.55;
                }
                if (markCuts && !valid) k *= OutsideDomainDim;
                ColorBuffer[idx] = theme ? ThemeDomain(cm, turns, k, maxIter) : CycleDomain(turns, k);
            }
        });
    }

    /// <summary>The built-in a = 0 cyclic OkLab colour for <paramref name="turns"/>,
    /// lightness scaled by <paramref name="k"/>.</summary>
    internal static uint CycleDomain(double turns, double k)
    {
        double phi = 2.0 * Math.PI * turns;
        float L = (float)((DomainLMid + DomainLAmp * Math.Cos(phi)) * k);
        float B = (float)(DomainBAmp * Math.Sin(phi) * Math.Sqrt(k));   // keep chroma in gamut when dimmed
        GradientColorSpaces.OkLabToRgb(L, 0f, B, out float r, out float g, out float b);
        return 0xFF000000u | ((uint)(r + 0.5f) << 16) | ((uint)(g + 0.5f) << 8) | (uint)(b + 0.5f);
    }

    private static uint ThemeDomain(Interefaces.IColorMap cm, double turns, double k, int maxIter)
    {
        uint c = unchecked((uint)cm.Map((float)(turns * maxIter), 0f, maxIter));
        uint r = (uint)Math.Clamp(((c >> 16) & 0xFF) * k + 0.5, 0, 255);
        uint g = (uint)Math.Clamp(((c >> 8) & 0xFF) * k + 0.5, 0, 255);
        uint b = (uint)Math.Clamp((c & 0xFF) * k + 0.5, 0, 255);
        return (c & 0xFF000000u) | (r << 16) | (g << 8) | b;
    }
}
