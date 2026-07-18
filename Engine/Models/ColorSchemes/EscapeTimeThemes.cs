// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/EscapeTimeThemes.cs
//
// Raw escape-time / level-set colourings.  These themes deliberately AVOID the
// smooth iteration count and quantise to integer iteration boundaries, giving
// the classic banded "dwell ring" look that smooth themes hide.
//
// Three sample themes:
//   • RawIterationBandsMap  — N discrete rainbow bands keyed to floor(smooth)
//   • BinaryDwellRingsMap   — black/white alternating bands (1 iter wide)
//   • LevelSetStaircaseMap  — 16-step monochrome staircase across iteration range
//
// All three implement IColorMap directly (no GradientColorMap) because the
// effect is integer-quantised rather than gradient-interpolated.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Discrete rainbow bands, one colour per integer iteration count
    /// (modulo <see cref="BandCount"/>).  The fractional part of smooth is
    /// discarded, so iteration ring boundaries are razor sharp.
    /// </summary>
    public sealed class RawIterationBandsMap : IColorMap
    {
        public static string Name => "Escape Time - Rainbow Bands";
        public static string Category => "Escape Time / Level Sets";
        public static string Description =>
            "Discrete rainbow bands, one hue per integer iteration count modulo 12. " +
            "No smoothing — boundaries are razor-sharp dwell rings.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        private const int BandCount = 12;

        public int Map(float smooth, float distance, int maxIterations)
        {
            int iter = (int)smooth;
            int bin = ((iter % BandCount) + BandCount) % BandCount;
            float h = bin / (float)BandCount;
            var c = ColorUtils.Hsv(h, 0.85f, 1f);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }

    /// <summary>
    /// Two-tone black-and-white bands, each one integer iteration wide.  Reveals
    /// the iteration field as a strict topographic map.
    /// </summary>
    public sealed class BinaryDwellRingsMap : IColorMap
    {
        public static string Name => "Escape Time - Binary Dwell Rings";
        public static string Category => "Escape Time / Level Sets";
        public static string Description =>
            "Alternating black/white bands one iteration wide.  Topographic map " +
            "of the escape-time field with no smoothing.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            int iter = (int)smooth;
            bool light = (iter & 1) == 0;
            return light ? unchecked((int)0xFFEEEEEEu) : unchecked((int)0xFF111111u);
        }
    }

    /// <summary>
    /// 16-step monochrome staircase across the full iteration range.
    /// Quantises smooth iteration count into 16 fixed grey bins so contours
    /// remain visible from the cardioid to the deep boundary.
    /// </summary>
    public sealed class LevelSetStaircaseMap : IColorMap
    {
        public static string Name => "Escape Time - 16-Step Staircase";
        public static string Category => "Escape Time / Level Sets";
        public static string Description =>
            "16-step monochrome staircase across the full iteration range. " +
            "Sharp contours from cardioid to deep boundary.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.Perceptual | ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        private const int StepCount = 16;

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (maxIterations <= 0) return unchecked((int)0xFF000000);
            float t = smooth / maxIterations;
            if (t < 0f) t = 0f; else if (t > 0.9999f) t = 0.9999f;
            int step = (int)(t * StepCount);
            float g = step / (float)(StepCount - 1);
            byte v = (byte)(g * 255f);
            return ColorUtils.PackArgb(v, v, v);
        }
    }
}
