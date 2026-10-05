// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Bivariate.cs (#1122, epic #1114 S8)
//
// Bivariate colour modes (Docs/Technical/DualOrbit-Coloring-RnD.md §3.C): map the
// two orbits' escape counts JOINTLY instead of blending two 1D gradients
// (PerOrbitLayers). Channel A = n_z, channel B = n_c — both already in the #981
// orbit cache, so every mode and knob here is colour-only (recolour in place).
//
//   Bivariate2D     u = n_z/(n_z + k), v = n_c/(n_c + k) → Palette2D[u, v]
//   JointEqualised  u, v = empirical CDF ranks of n_z, n_c (copula transform:
//                   each marginal uniform) → Palette2D — the whole palette used
//   PerceptualSplit u → OkLab L, v → OkLab b (blue↔yellow), a = 0 — no red↔green
//   PhaseModulated  the active theme at n_z + κ·n_c (FM-synthesis analogy; κ
//                   animatable)
//
// A bounded orbit's channel saturates (u or v = 1; n = maxIter for
// PhaseModulated), so the region where only the c-orbit escapes (M \ M_c) is
// coloured; pixels where both orbits are bounded (M_c) get the interior colour,
// and both-escape-by-step-1 the #615 surround.
//
// Built-in 2D palettes: BlueAmberSquare — bilinear in OkLab between near-black
// (0,0), blue (1,0), amber (0,1) and near-white (1,1), colour-blind safe;
// ThemeByLightness — hue from the active theme at u, brightness from v.

using System;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    internal static bool IsBivariateMode(DualOrbitColorMode m)
        => m is DualOrbitColorMode.Bivariate2D or DualOrbitColorMode.JointEqualised
             or DualOrbitColorMode.PerceptualSplit or DualOrbitColorMode.PhaseModulated;

    /// <summary>#1122 — channel coordinates (u, v ∈ [0, 1]) of the last bivariate
    /// colour pass; NaN where both orbits are bounded. Empty in other modes.</summary>
    public float[] BivariateU { get; private set; } = Array.Empty<float>();
    public float[] BivariateV { get; private set; } = Array.Empty<float>();

    // OkLab corners of the BlueAmberSquare palette (blue = Okabe–Ito #0072B2,
    // amber = #E69F00; near-black / near-white neutrals).
    private static readonly (float L, float A, float B)[] SquareCorners = BuildCorners();

    private static (float, float, float)[] BuildCorners()
    {
        GradientColorSpaces.RgbToOkLab(0x00, 0x72, 0xB2, out float bl, out float ba, out float bb);
        GradientColorSpaces.RgbToOkLab(0xE6, 0x9F, 0x00, out float al, out float aa, out float ab);
        return new[] { (0.20f, 0f, 0f), (bl, ba, bb), (al, aa, ab), (0.96f, 0f, 0f) };   // 00, 10, 01, 11
    }

    /// <summary>The BlueAmberSquare 2D palette at (u, v): bilinear in OkLab.</summary>
    internal static uint BlueAmberSquare(double u, double v)
    {
        u = Math.Clamp(u, 0, 1); v = Math.Clamp(v, 0, 1);
        var (c00, c10, c01, c11) = (SquareCorners[0], SquareCorners[1], SquareCorners[2], SquareCorners[3]);
        float Mix(float a00, float a10, float a01, float a11)
            => (float)((1 - u) * (1 - v) * a00 + u * (1 - v) * a10 + (1 - u) * v * a01 + u * v * a11);
        GradientColorSpaces.OkLabToRgb(
            Mix(c00.L, c10.L, c01.L, c11.L), Mix(c00.A, c10.A, c01.A, c11.A), Mix(c00.B, c10.B, c01.B, c11.B),
            out float r, out float g, out float b);
        return Pack(r, g, b);
    }

    /// <summary>PerceptualSplit at (u, v): L = 0.22 + 0.72·u, b = 0.30·(v − ½), a = 0.</summary>
    internal static uint PerceptualSplitColor(double u, double v)
    {
        GradientColorSpaces.OkLabToRgb((float)(0.22 + 0.72 * Math.Clamp(u, 0, 1)), 0f,
            (float)(0.30 * (Math.Clamp(v, 0, 1) - 0.5)), out float r, out float g, out float b);
        return Pack(r, g, b);
    }

    private static uint Pack(float r, float g, float b)
        => 0xFF000000u | ((uint)(r + 0.5f) << 16) | ((uint)(g + 0.5f) << 8) | (uint)(b + 0.5f);

    private static uint ThemeByLightness(IColorMap cm, double u, double v, int maxIter)
    {
        uint c = unchecked((uint)cm.Map((float)(Math.Clamp(u, 0, 1) * maxIter), 0f, maxIter));
        double k = 0.35 + 0.65 * Math.Clamp(v, 0, 1);
        uint Ch(int sh) => (uint)Math.Clamp(((c >> sh) & 0xFF) * k + 0.5, 0, 255);
        return (c & 0xFF000000u) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    private void ColorizeBivariate(DualOrbitColorMode mode, int maxIter, uint interiorColor, uint? oobColor)
    {
        int n = Width * Height;
        if (BivariateU.Length != n) { BivariateU = new float[n]; BivariateV = new float[n]; }
        float[] U = BivariateU, V = BivariateV, smZ = _smZ, smC = _smC; byte[] flags = _flags;
        var fp = FractalParameters;
        double k = Math.Max(1e-6, fp.DualOrbitBivariateScale);
        double kappa = fp.DualOrbitPhaseK;
        var palette = fp.DualOrbitPalette2D;
        var cm = ColorMap;

        // Channel coordinates. Copula (JointEqualised): the empirical CDF of each
        // channel's escaped values (mid-rank for ties); bounded → 1.
        float[]? sortedZ = null, sortedC = null;
        if (mode == DualOrbitColorMode.JointEqualised)
        {
            sortedZ = Escaped(smZ, flags, FlagZEsc);
            sortedC = Escaped(smC, flags, FlagCEsc);
        }
        Parallel.For(0, n, i =>
        {
            byte f = flags[i];
            bool zEsc = (f & FlagZEsc) != 0, cEsc = (f & FlagCEsc) != 0;
            if (!zEsc && !cEsc) { U[i] = V[i] = float.NaN; return; }
            U[i] = (float)Coord(zEsc, smZ[i], sortedZ);
            V[i] = (float)Coord(cEsc, smC[i], sortedC);
        });

        Parallel.For(0, n, i =>
        {
            byte f = flags[i];
            bool zEsc = (f & FlagZEsc) != 0, cEsc = (f & FlagCEsc) != 0;
            if (!zEsc && !cEsc) { ColorBuffer[i] = interiorColor; return; }
            if (oobColor is uint oob && (f & FlagZFirst) != 0 && (f & FlagCFirst) != 0) { ColorBuffer[i] = oob; return; }
            double u = U[i], v = V[i];
            ColorBuffer[i] = mode switch
            {
                DualOrbitColorMode.PerceptualSplit => PerceptualSplitColor(u, v),
                DualOrbitColorMode.PhaseModulated => unchecked((uint)cm.Map(
                    (float)((zEsc ? smZ[i] : maxIter) + kappa * (cEsc ? smC[i] : maxIter)), 0f, maxIter)),
                _ => palette == DualOrbitPalette2D.ThemeByLightness
                    ? ThemeByLightness(cm, u, v, maxIter) : BlueAmberSquare(u, v),
            };
        });

        double Coord(bool escaped, float sm, float[]? sorted)
        {
            if (!escaped) return 1.0;
            if (sorted == null) return sm / (sm + k);
            int lo = LowerBound(sorted, sm), hi = UpperBound(sorted, sm);
            return sorted.Length <= 1 ? 0.5 : 0.5 * (lo + hi) / sorted.Length;
        }
    }

    private static float[] Escaped(float[] sm, byte[] flags, byte flag)
    {
        int count = 0;
        for (int i = 0; i < sm.Length; i++) if ((flags[i] & flag) != 0) count++;
        var a = new float[count];
        for (int i = 0, j = 0; i < sm.Length; i++) if ((flags[i] & flag) != 0) a[j++] = sm[i];
        Array.Sort(a);
        return a;
    }

    private static int LowerBound(float[] a, float x)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] < x) lo = m + 1; else hi = m; }
        return lo;
    }

    private static int UpperBound(float[] a, float x)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] <= x) lo = m + 1; else hi = m; }
        return lo;
    }
}
