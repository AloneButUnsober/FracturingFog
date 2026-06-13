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
using System.Runtime.Intrinsics;

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

        /// <summary>Read-only view of the gradient stops, used by JSON export.</summary>
        public IReadOnlyList<ColorStop> ExportStops => Stops;

        public ColorPaletteType Type => ColorPaletteType.GradientLinear;

        public int MaxIterations { get; set; } = 1000;

        // ── Precomputed RGB LUT ──────────────────────────────────────────────
        // Lazily built on first MapNormalized call. 256 entries sampled across
        // the gradient. Each entry packs the base RGB (lower 4 lanes: R, G, B, 0)
        // and the delta to the next entry (upper 4 lanes: dR, dG, dB, 0) into a
        // single Vector256<float>, so the per-pixel lerp is one aligned load +
        // one Vector128 FMA.
        //
        // Theme switch ⇒ new IColorMap instance ⇒ fresh LUT (no invalidation
        // path needed by callers). If a subclass mutates Stops after Map() has
        // run, it must call InvalidateGradientLut() to force a rebuild.
        private const int LutSize = 256;
        private Vector256<float>[]? _lut;
        private int _lutStopsCount = -1;

        /// <summary>
        /// Marks the precomputed gradient LUT as stale. Call after mutating
        /// <see cref="Stops"/> in a way that doesn't change Count (e.g. swapping
        /// a stop's colour in place). Adding or removing stops is auto-detected.
        /// </summary>
        protected void InvalidateGradientLut()
        {
            _lut = null;
            _lutStopsCount = -1;
        }

        private Vector256<float>[] EnsureLut()
        {
            var lut = _lut;
            if (lut != null && _lutStopsCount == Stops.Count) return lut;
            return BuildLut();
        }

        private Vector256<float>[] BuildLut()
        {
            int count = Stops.Count;
            var lut = new Vector256<float>[LutSize];

            if (count == 0)
            {
                _lutStopsCount = 0;
                _lut = lut;
                return lut;
            }

            Span<Vector128<float>> samples = stackalloc Vector128<float>[LutSize + 1];
            for (int i = 0; i <= LutSize; i++)
            {
                float t = (i >= LutSize) ? 1f : i / (float)LutSize;
                SampleStops(t, out float r, out float g, out float b);
                samples[i] = Vector128.Create(r, g, b, 0f);
            }
            for (int i = 0; i < LutSize; i++)
            {
                Vector128<float> a = samples[i];
                Vector128<float> delta = samples[i + 1] - a;
                lut[i] = Vector256.Create(a, delta);
            }

            _lutStopsCount = count;
            _lut = lut;
            return lut;
        }

        private void SampleStops(float t, out float r, out float g, out float b)
        {
            ColorStop a = Stops[0];
            ColorStop bStop = Stops[^1];
            int n = Stops.Count - 1;
            for (int i = 0; i < n; i++)
            {
                if (t >= Stops[i].Position && t <= Stops[i + 1].Position)
                {
                    a = Stops[i];
                    bStop = Stops[i + 1];
                    break;
                }
            }
            float range = bStop.Position - a.Position;
            float localT = (range <= 0f) ? 0f : (t - a.Position) / range;
            r = a.Color.R + (bStop.Color.R - a.Color.R) * localT;
            g = a.Color.G + (bStop.Color.G - a.Color.G) * localT;
            b = a.Color.B + (bStop.Color.B - a.Color.B) * localT;
        }

        /// <inheritdoc/>
        public virtual int Map(float smooth, float distance, int maxIterations)
        {
            float t = (maxIterations > 0) ? smooth / maxIterations : 0f;
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
        }

        // Virtual 9-arg overload exists at the class level so derived themes that
        // need finalZ / dz-dc can override it directly. Without this, a plain
        // public 9-arg method in a derived class is NOT in the interface map
        // (the map is fixed at GradientColorMap, where IColorMap is declared),
        // so calls through an IColorMap reference fall to the interface default
        // implementation and miss the override.
        //
        // Default delegates to the 5-arg IColorMap.Map(s,d,i,nx,ny) — NOT the
        // 3-arg overload — so 3D subclasses (GradientPhong3DBase, PbrGradient3DBase)
        // that provide an explicit IColorMap.Map(s,d,i,nx,ny) still receive the
        // normal data they need for lighting.
        public virtual int Map(float smooth, float distance, int iterations,
                               float nx, float ny,
                               float finalZr, float finalZi,
                               float dzdcR, float dzdcI)
            => ((IColorMap)this).Map(smooth, distance, iterations, nx, ny);

        /// <summary>
        /// Evaluates the gradient at normalised position <paramref name="t"/> ∈ [0,1].
        /// Subclasses can call this directly with a custom <c>t</c>.
        /// </summary>
        protected int MapNormalized(float t, float distance)
        {
            if (Stops.Count == 0)
                return unchecked((int)0xFF000000);

            var lut = EnsureLut();

            t = System.Math.Clamp(t, 0f, 1f);
            float scaled = t * LutSize;
            int idx = (int)scaled;
            if (idx >= LutSize) idx = LutSize - 1;
            float frac = scaled - idx;

            Vector256<float> packed = lut[idx];
            Vector128<float> baseRgb = packed.GetLower();
            Vector128<float> delta = packed.GetUpper();
            Vector128<float> rgb = baseRgb + delta * Vector128.Create(frac);

            int r = (int)rgb.GetElement(0);
            int g = (int)rgb.GetElement(1);
            int bC = (int)rgb.GetElement(2);
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

        /// <summary>Effective cycle speed of this instance, for JSON export.</summary>
        public float ExportCycleSpeed => CycleSpeed;

        public new ColorPaletteType Type => ColorPaletteType.GradientCyclic;

        /// <inheritdoc/>
        public override int Map(float smooth, float distance, int maxIterations)
        {
            // Wrap into [0,1) using fmod, always positive.
            float t = ((smooth * CycleSpeed) % 1.0f + 1.0f) % 1.0f;
            return MapNormalized(t, distance);
        }
    }
}
