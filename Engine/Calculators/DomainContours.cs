// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DomainContours.cs (#1598) — contour lines over the Domain (phase-portrait) views
// of the wave fractals: Julibrot Pair PathInterferenceDomain, Slit Cascade Domain
// and Talbot Carpet Domain (#1592).
//
// Lines of constant phase every 1/n of a turn, and lines where the amplitude |A|
// crosses each power of 2: the contour lines of Wegert's "enhanced" phase
// portraits (E. Wegert, Visual Complex Functions, Birkhäuser 2012 —
// Docs/Resources-Bibliography.md#wegert-visual-complex-functions). They make the
// picture readable without hue: at a vortex of charge ℓ, n·|ℓ| phase lines meet,
// whatever colours the viewer can tell apart.
//
// How the lines are found is FF's choice, not the book's: each pixel's phase band
// ⌊n·frac(φ)⌋ and modulus octave ⌊log₂|A|⌋ are compared with its right and lower
// neighbours, and a pixel whose band differs is on a line. That gives one-pixel
// lines at any zoom, with no extra amplitude evaluation, and a line exactly where
// the sampled value crosses a level. A line pixel is the wheel colour with its
// lightness scaled by LineDim (hue untouched). The pass only overwrites line
// pixels, so Off (never calling it) leaves the image byte-identical.

using System;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.Models;

namespace FracturingFog;

/// <summary>Contour lines over a Domain view (#1598); see the file header.</summary>
public static class DomainContours
{
    /// <summary>Lightness factor of a line pixel (the wheel colour at k·LineDim).</summary>
    public const double LineDim = 0.55;

    /// <summary>Phase lines per turn are clamped to 1..MaxCount.</summary>
    public const int MaxCount = 64;

    public static int ClampCount(int n) => Math.Clamp(n, 1, MaxCount);

    /// <summary>The phase band of <paramref name="turns"/>: ⌊n·frac(turns)⌋ in 0..n−1.</summary>
    public static int PhaseBand(double turns, int n)
    {
        double f = turns - Math.Floor(turns);
        int b = (int)Math.Floor(f * n);
        return b >= n ? 0 : b < 0 ? 0 : b;   // frac rounding to 1 wraps to band 0 (phase 1 ≡ 0)
    }

    /// <summary>The modulus octave of log₂|A|: ⌊log₂|A|⌋.</summary>
    public static int ModulusBand(double log2Mod) => (int)Math.Floor(log2Mod);

    /// <summary>Whether a mode draws phase / modulus lines.</summary>
    public static bool DrawsPhase(DomainContourMode m) => m is DomainContourMode.Phase or DomainContourMode.Both;
    public static bool DrawsModulus(DomainContourMode m) => m is DomainContourMode.Modulus or DomainContourMode.Both;

    /// <summary>Darken the line pixels of <paramref name="color"/> (row-major
    /// <paramref name="width"/> × <paramref name="height"/>). Per pixel:
    /// <paramref name="turns"/> = phase in turns (NaN = not a domain pixel: never on a
    /// line, never compared), <paramref name="k"/> = the brightness it was painted
    /// with, <paramref name="log2Mod"/> = log₂ of the amplitude (NaN = no modulus
    /// line here, e.g. below the dynamic range). Reads only the side buffers, so the
    /// rows are independent and the result deterministic.</summary>
    public static void Apply(uint[] color, float[] turns, float[] k, float[] log2Mod, int width, int height,
        DomainContourMode mode, int count, CancellationToken ct = default)
    {
        bool phase = DrawsPhase(mode), modulus = DrawsModulus(mode);
        if (!phase && !modulus) return;
        int n = ClampCount(count);
        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                float t = turns[i];
                if (float.IsNaN(t)) continue;
                bool line = false;
                if (phase)
                {
                    int b = PhaseBand(t, n);
                    line = (x + 1 < width && !float.IsNaN(turns[i + 1]) && PhaseBand(turns[i + 1], n) != b)
                        || (y + 1 < height && !float.IsNaN(turns[i + width]) && PhaseBand(turns[i + width], n) != b);
                }
                if (!line && modulus && !float.IsNaN(log2Mod[i]))
                {
                    int b = ModulusBand(log2Mod[i]);
                    line = (x + 1 < width && !float.IsNaN(turns[i + 1]) && !float.IsNaN(log2Mod[i + 1]) && ModulusBand(log2Mod[i + 1]) != b)
                        || (y + 1 < height && !float.IsNaN(turns[i + width]) && !float.IsNaN(log2Mod[i + width]) && ModulusBand(log2Mod[i + width]) != b);
                }
                if (line) color[i] = PhaseWheelBlueAmberMap.ColorAt(t, k[i] * LineDim);
            }
        });
    }

    /// <summary>Side buffers sized for <paramref name="n"/> pixels, reused when they fit.</summary>
    internal static void Ensure(ref float[] turns, ref float[] k, ref float[] log2Mod, int n)
    {
        if (turns.Length != n) { turns = new float[n]; k = new float[n]; log2Mod = new float[n]; }
    }
}
