// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

        // ── Phase A colour options (F1 / F4 / F5) ────────────────────────────
        // All default to the historical behaviour so an unconfigured theme
        // renders byte-identically. Set once at construction by the data-driven
        // themes; changing InterpolationSpace after the LUT is built requires
        // InvalidateGradientLut() (only the ctor path sets it, so no invalidate
        // is needed in practice).

        private GradientColorSpace _interpSpace = GradientColorSpace.Srgb;

        /// <summary>Colour space the LUT blends stops in (F1).</summary>
        protected GradientColorSpace InterpolationSpace
        {
            get => _interpSpace;
            set { if (_interpSpace != value) { _interpSpace = value; InvalidateGradientLut(); } }
        }

        /// <summary>Additive phase on the cycling parameter (F4).</summary>
        protected float ColorOffset { get; set; } = 0f;

        /// <summary>Frequency multiplier on the cycling parameter (F4).</summary>
        protected float ColorDensity { get; set; } = 1f;

        /// <summary>Boundary behaviour of the cycling parameter (F5).</summary>
        protected ColorWrapMode CycleWrap { get; set; } = ColorWrapMode.Repeat;

        // Export accessors so data-driven themes round-trip the options to JSON.
        public GradientColorSpace ExportInterpolationSpace => _interpSpace;
        public float ExportColorOffset => ColorOffset;
        public float ExportColorDensity => ColorDensity;
        public ColorWrapMode ExportWrapMode => CycleWrap;

        /// <summary>
        /// Maps a raw cycling value to [0,1] honouring density, offset, and wrap
        /// mode (F4 / F5). Shared by <see cref="CyclingGradientColorMap"/> and
        /// the 3D lit bases so every cycling kind picks up the options.
        /// With defaults (offset 0, density 1, Repeat) this reduces to the
        /// historical <c>((smooth*cycleSpeed) mod 1)</c> for non-negative input.
        /// </summary>
        protected float CyclicT(float smooth, float cycleSpeed)
        {
            float raw = smooth * cycleSpeed * ColorDensity + ColorOffset;
            switch (CycleWrap)
            {
                case ColorWrapMode.Clamp:
                    return System.Math.Clamp(raw, 0f, 1f);
                case ColorWrapMode.PingPong:
                    // Triangle wave: fold [0,2) onto [0,1] so there is no seam.
                    float m = ((raw % 2f) + 2f) % 2f;
                    return m > 1f ? 2f - m : m;
                default: // Repeat
                    return ((raw % 1f) + 1f) % 1f;
            }
        }

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

            // Output is in 0..255 float space (the LUT stores these directly).
            switch (_interpSpace)
            {
                case GradientColorSpace.OkLab:
                    GradientColorSpaces.MixOkLab(a.Color, bStop.Color, localT, out r, out g, out b);
                    return;
                case GradientColorSpace.Hsv:
                    GradientColorSpaces.MixHsv(a.Color, bStop.Color, localT, out r, out g, out b);
                    return;
                default: // Srgb — historical byte lerp.
                    r = a.Color.R + (bStop.Color.R - a.Color.R) * localT;
                    g = a.Color.G + (bStop.Color.G - a.Color.G) * localT;
                    b = a.Color.B + (bStop.Color.B - a.Color.B) * localT;
                    return;
            }
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
            // Density / offset / wrap honoured via the shared CyclicT helper.
            // Defaults collapse to the historical ((smooth*speed) mod 1).
            float t = CyclicT(smooth, CycleSpeed);
            return MapNormalized(t, distance);
        }
    }

    // ── Gradient interpolation-space helpers (F1) ────────────────────────────

    /// <summary>
    /// Stop-blending in colour spaces other than sRGB. Kept inside the Engine
    /// assembly (self-contained OkLab math) so <see cref="GradientColorMap"/>
    /// does not take a dependency on the palette-extraction library. Inputs are
    /// <see cref="System.Drawing.Color"/> stop endpoints; outputs are sRGB in
    /// 0..255 float space to match the LUT's storage.
    /// </summary>
    internal static class GradientColorSpaces
    {
        public static void MixOkLab(System.Drawing.Color c1, System.Drawing.Color c2, float u,
                                    out float r, out float g, out float b)
        {
            RgbToOkLab(c1.R, c1.G, c1.B, out float L1, out float a1, out float b1);
            RgbToOkLab(c2.R, c2.G, c2.B, out float L2, out float a2, out float b2);
            float L = L1 + (L2 - L1) * u;
            float A = a1 + (a2 - a1) * u;
            float B = b1 + (b2 - b1) * u;
            OkLabToRgb(L, A, B, out r, out g, out b);
        }

        public static void MixHsv(System.Drawing.Color c1, System.Drawing.Color c2, float u,
                                  out float r, out float g, out float b)
        {
            RgbToHsv(c1.R, c1.G, c1.B, out float h1, out float s1, out float v1);
            RgbToHsv(c2.R, c2.G, c2.B, out float h2, out float s2, out float v2);

            // Shorter-arc hue interpolation.
            float dh = h2 - h1;
            if (dh > 0.5f) dh -= 1f;
            else if (dh < -0.5f) dh += 1f;
            float h = h1 + dh * u;
            h -= MathF.Floor(h);

            float s = s1 + (s2 - s1) * u;
            float v = v1 + (v2 - v1) * u;
            HsvToRgb(h, s, v, out r, out g, out b);
        }

        // ── HSV (h,s,v in [0,1]; RGB in 0..255) ──────────────────────────────

        private static void RgbToHsv(byte r8, byte g8, byte b8, out float h, out float s, out float v)
        {
            float r = r8 / 255f, g = g8 / 255f, b = b8 / 255f;
            float max = MathF.Max(r, MathF.Max(g, b));
            float min = MathF.Min(r, MathF.Min(g, b));
            float d = max - min;
            v = max;
            s = max <= 0f ? 0f : d / max;
            if (d < 1e-6f) { h = 0f; return; }
            if (max == r) h = ((g - b) / d) % 6f;
            else if (max == g) h = (b - r) / d + 2f;
            else h = (r - g) / d + 4f;
            h /= 6f;
            if (h < 0f) h += 1f;
        }

        private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
        {
            h = (h - MathF.Floor(h)) * 6f;
            int i = (int)h;
            float f = h - i;
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            r *= 255f; g *= 255f; b *= 255f;
        }

        // ── OkLab (Björn Ottosson); RGB bytes ⇄ OkLab, sRGB-encoded output ────

        private static void RgbToOkLab(byte r8, byte g8, byte b8, out float L, out float A, out float B)
        {
            float r = SrgbToLinear(r8 / 255f);
            float g = SrgbToLinear(g8 / 255f);
            float b = SrgbToLinear(b8 / 255f);

            float l = 0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b;
            float m = 0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b;
            float s = 0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b;

            float l_ = MathF.Cbrt(l);
            float m_ = MathF.Cbrt(m);
            float s_ = MathF.Cbrt(s);

            L = 0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_;
            A = 1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_;
            B = 0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_;
        }

        private static void OkLabToRgb(float L, float A, float B, out float r255, out float g255, out float b255)
        {
            float l_ = L + 0.3963377774f * A + 0.2158037573f * B;
            float m_ = L - 0.1055613458f * A - 0.0638541728f * B;
            float s_ = L - 0.0894841775f * A - 1.2914855480f * B;

            float l = l_ * l_ * l_;
            float m = m_ * m_ * m_;
            float s = s_ * s_ * s_;

            float r =  4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
            float g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
            float b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;

            r255 = System.Math.Clamp(LinearToSrgb(r), 0f, 1f) * 255f;
            g255 = System.Math.Clamp(LinearToSrgb(g), 0f, 1f) * 255f;
            b255 = System.Math.Clamp(LinearToSrgb(b), 0f, 1f) * 255f;
        }

        private static float SrgbToLinear(float c)
            => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        private static float LinearToSrgb(float c)
            => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(MathF.Max(c, 0f), 1f / 2.4f) - 0.055f;
    }
}
