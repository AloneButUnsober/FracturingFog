// Models/ColorSchemes/ColorUtils.cs
// Shared color utilities and abstract base classes used by all gradient themes.
//
// Hierarchy:
//   IColorMap
//     └─ GradientColorMap          (linear t = smooth/maxIter → gradient stops)
//           └─ CyclingGradientColorMap   (cyclic t = (smooth*speed) % 1)
using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    // ── Value types ───────────────────────────────────────────────────────────

    /// <summary>A position+colour pair defining one stop in a gradient.</summary>
    public struct ColorStop
    {
        /// <summary>Normalised position in [0, 1].</summary>
        public float Position;
        public System.Drawing.Color Color;

        public ColorStop(float pos, System.Drawing.Color color)
        {
            Position = pos;
            Color = color;
        }
    }

    // ── Utility ───────────────────────────────────────────────────────────────
    public static class ColorUtils
    {
        /// <summary>
        /// Converts HSV (h∈[0,1), s∈[0,1], v∈[0,1]) to a System.Drawing.Color.
        /// </summary>
        public static System.Drawing.Color Hsv(float h, float s, float v)
        {
            h = (h % 1f + 1f) % 1f;
            int i = (int)(h * 6f);
            float f = h * 6f - i;
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);

            return i switch
            {
                0 => FromFloat(v, t, p),
                1 => FromFloat(q, v, p),
                2 => FromFloat(p, v, t),
                3 => FromFloat(p, q, v),
                4 => FromFloat(t, p, v),
                _ => FromFloat(v, p, q),
            };
        }

        /// <summary>Packs float RGB [0,1] into a System.Drawing.Color (A=255).</summary>
        public static System.Drawing.Color FromFloat(float r, float g, float b)
            => System.Drawing.Color.FromArgb(
                255,
                   (byte)(System.Math.Clamp(r, 0f, 1f) * 255),
                   (byte)(System.Math.Clamp(g, 0f, 1f) * 255),
                   (byte)(System.Math.Clamp(b, 0f, 1f) * 255));

        /// <summary>Packs byte R,G,B into the ARGB int format expected by IColorMap.Map().</summary>
        public static int PackArgb(byte r, byte g, byte b)
            => unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);

        /// <summary>Packs float R,G,B [0,1] into the ARGB int format.</summary>
        public static int PackArgbF(float r, float g, float b)
            => PackArgb(
                   (byte)(System.Math.Clamp(r, 0f, 1f) * 255f),
                   (byte)(System.Math.Clamp(g, 0f, 1f) * 255f),
                   (byte)(System.Math.Clamp(b, 0f, 1f) * 255f));
    }

    // ── Abstract base: linear gradient ───────────────────────────────────────

    /// <summary>
    /// Colour map that linearly interpolates between a list of <see cref="ColorStop"/>
    /// objects.  The mapping parameter <c>t = smooth / maxIterations</c> is clamped
    /// to [0, 1] so the gradient stretches across the full iteration range.
    /// </summary>
    public abstract class GradientColorMap : IColorMap
    {
        protected readonly List<ColorStop> Stops = new();

        public int MaxIterations { get; set; } = 1000;

        /// <inheritdoc/>
        public virtual int Map(float smooth, float distance, int maxIterations)
        {
            float t = (maxIterations > 0) ? smooth / maxIterations : 0f;
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
        }

        /// <summary>
        /// Evaluates the gradient at normalised position <paramref name="t"/> ∈ [0,1].
        /// Subclasses can call this directly with a custom <c>t</c>.
        /// </summary>
        protected int MapNormalized(float t, float distance)
        {
            if (Stops.Count == 0)
                return unchecked((int)0xFF000000);
            t = System.Math.Clamp(t, 0f, 1f);

            // Find two stops
            ColorStop a = Stops[0];
            ColorStop b = Stops[^1];

            for (int i = 0; i < Stops.Count - 1; i++)
            {
                if (t >= Stops[i].Position && t <= Stops[i + 1].Position)
                {
                    a = Stops[i];
                    b = Stops[i + 1];
                    break;
                }
            }

            float range = b.Position - a.Position;
            float localT = (range <= 0f) ? 0f : (t - a.Position) / range;

            byte r = (byte)(a.Color.R + (b.Color.R - a.Color.R) * localT);
            byte g = (byte)(a.Color.G + (b.Color.G - a.Color.G) * localT);
            byte bC = (byte)(a.Color.B + (b.Color.B - a.Color.B) * localT);

            return unchecked((int)0xFF000000 | (r << 16) | (g << 8) | bC);
        }
    }

    // ── Abstract base: cycling gradient ──────────────────────────────────────

    /// <summary>
    /// A <see cref="GradientColorMap"/> variant whose parameter cycles through
    /// the gradient multiple times as iteration count increases, preventing the
    /// whole image going dark at deep zoom levels.
    /// </summary>
    /// <remarks>
    /// Override <see cref="CycleSpeed"/> to control how fast the gradient repeats.
    /// A value of <c>0.02f</c> gives one full cycle every ~50 iteration-units of
    /// smooth value (same rhythm as the default HSV palette).
    /// </remarks>
    public abstract class CyclingGradientColorMap : GradientColorMap
    {
        /// <summary>
        /// Controls repetition speed.  Higher = more rapid colour cycling.
        /// Default 0.02 gives roughly the same cycle rate as the HSV palette.
        /// </summary>
        protected virtual float CycleSpeed { get; } = 0.02f;

        /// <inheritdoc/>
        public override int Map(float smooth, float distance, int maxIterations)
        {
            // Wrap into [0,1) using fmod, always positive.
            float t = ((smooth * CycleSpeed) % 1.0f + 1.0f) % 1.0f;
            return MapNormalized(t, distance);
        }
    }
}
