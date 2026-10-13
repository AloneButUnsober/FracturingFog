// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/MisiurewiczLoop.cs
//
// Interesting-location finder S3b (#1235): the self-similar loop at a
// Misiurewicz point M(k, p). Near M the parameter plane repeats itself under
// c − M ↦ λ·(c − M), with λ = (f^p)′ on the repelling cycle (Tan Lei). So a
// zoom by |λ| combined with a camera turn of −arg λ shows the same picture
// again, and a video doing exactly that per loop wraps seamlessly. The escape
// count grows by p per cycle; a cyclic palette is turned back by that much so
// the colours wrap too.
//
// Loop time t runs 0..1 over Cycles cycles (×|λ|^Cycles zoom in total); a
// video samples t = f / frames so the last frame stops one step short of the
// first one and playback wraps.

using System;
using System.Globalization;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>A Misiurewicz loop: multiplier λ (magnitude and angle), the cycle
/// period p (escape-count shift per cycle; 0 = no palette compensation), and
/// the number of cycles per loop.</summary>
public sealed record MisiurewiczLoop(double Magnitude, double AngleDegrees, int Period, int Cycles)
{
    /// <summary>A loop zooms at least this much: cycles are added while |λ|^n
    /// stays below it (slow spirals with |λ| near 1).</summary>
    public const double MinLoopZoom = 2.0;
    public const int MaxCycles = 256;

    /// <summary>Cycles per loop for a multiplier magnitude (≥ 1, so the loop
    /// zooms at least <see cref="MinLoopZoom"/>×).</summary>
    public static int AutoCycles(double magnitude)
        => magnitude > 1 ? (int)SMath.Clamp(SMath.Ceiling(SMath.Log(MinLoopZoom) / SMath.Log(magnitude) - 1e-9), 1, MaxCycles) : 1;

    /// <summary>A loop for λ with <paramref name="cycles"/> (0 = auto).</summary>
    public static MisiurewiczLoop For(double magnitude, double angleDegrees, int period, int cycles = 0)
    {
        if (!(magnitude > 1) || !double.IsFinite(magnitude))
            throw new ArgumentOutOfRangeException(nameof(magnitude), magnitude, "A Misiurewicz cycle repels: |λ| > 1.");
        double a = SMath.IEEERemainder(angleDegrees, 360);   // the shortest turn, (−180, 180]
        return new MisiurewiczLoop(magnitude, a, SMath.Max(0, period),
                                   cycles > 0 ? SMath.Min(cycles, MaxCycles) : AutoCycles(magnitude));
    }

    /// <summary>Total zoom factor over one loop: |λ|^Cycles.</summary>
    public double LoopZoom => SMath.Pow(Magnitude, Cycles);

    /// <summary>Zoom at loop time t, from <paramref name="startZoom"/>.</summary>
    public double ZoomAt(double startZoom, double t) => startZoom * SMath.Pow(Magnitude, Cycles * t);

    /// <summary>Camera turn at loop time t, radians counter-clockwise in the
    /// complex plane: −t · Cycles · arg λ.</summary>
    public double CameraTurnAt(double t) => -t * Cycles * AngleDegrees * SMath.PI / 180;

    /// <summary>Escape-count growth at loop time t: t · Cycles · p iterations.</summary>
    public double IterationShiftAt(double t) => t * Cycles * Period;

    /// <summary>Parse <c>MAG,DEG</c> (|λ| and arg λ in degrees).</summary>
    public static bool TryParseMultiplier(string? s, out double magnitude, out double angleDegrees)
    {
        magnitude = angleDegrees = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split(',');
        return parts.Length == 2
            && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out magnitude)
            && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out angleDegrees)
            && magnitude > 1 && double.IsFinite(magnitude) && double.IsFinite(angleDegrees);
    }

    public static string FormatMultiplier(double magnitude, double angleDegrees)
        => magnitude.ToString("R", CultureInfo.InvariantCulture) + "," + angleDegrees.ToString("R", CultureInfo.InvariantCulture);
}
