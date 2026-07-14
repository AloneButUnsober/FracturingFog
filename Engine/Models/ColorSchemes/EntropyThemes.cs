// Models/ColorSchemes/EntropyThemes.cs
//
// Color themes driven by local Shannon entropy of the smooth-iteration field.
//
// Shannon entropy (https://en.wikipedia.org/wiki/Entropy_(information_theory)):
//   H(X) = -Σ p(x) log₂ p(x)
//
// For each pixel we sample a (2R+1)×(2R+1) window of smooth-iteration values,
// rescale the window to [0, BinCount-1] using its own min/max, count per-bin
// occupancy, and compute H normalised by log₂(BinCount) → output ∈ [0,1].
//
// Regions where smooth varies smoothly (interior, far exterior) yield H ≈ 0;
// chaotic boundary filaments saturate H near 1.  Each theme below paints this
// entropy field a different way:
//
//   • EntropyHeatmap     — Map() is a placeholder; PostProcess overwrites the
//                          pixel with a cool→hot gradient evaluated at H.
//                          Pure entropy view; classical information-theoretic
//                          colouring of the fractal.
//
//   • EntropyContrastMap — Base twilight gradient driven by iteration count.
//                          PostProcess modulates saturation and brightness by
//                          H — chaotic regions vivid, smooth regions muted.
//
//   • EntropyEdgeMap     — Dark deep-teal base from iteration count.
//                          PostProcess additively blends gold-white into
//                          pixels where H > threshold, producing a filament
//                          glow that traces high-information regions.

using FracturingFog.Interefaces;
using System;
using System.Drawing;
using System.Threading.Tasks;

namespace FracturingFog.Models
{
    // =========================================================================
    // Shared local-entropy kernel
    // =========================================================================

    internal static class LocalEntropy
    {
        public const int WindowRadius = 3;        // 7×7 window
        public const int BinCount     = 16;
        public static readonly float InvLogBin = 1f / MathF.Log2(BinCount);

        /// <summary>
        /// Shannon entropy of smooth values inside a (2R+1)×(2R+1) window around
        /// (cx, cy), per-window adaptive binning into <see cref="BinCount"/>
        /// bins, normalised by log₂(BinCount) so the return is in [0,1].
        /// Returns 0 for flat windows (range below epsilon).
        /// </summary>
        public static float ComputeAt(float[] smooth, int width, int height, int cx, int cy)
        {
            Span<int> bins = stackalloc int[BinCount];
            bins.Clear();

            int x0 = Math.Max(0, cx - WindowRadius);
            int x1 = Math.Min(width  - 1, cx + WindowRadius);
            int y0 = Math.Max(0, cy - WindowRadius);
            int y1 = Math.Min(height - 1, cy + WindowRadius);

            float min = float.MaxValue, max = float.MinValue;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * width;
                for (int x = x0; x <= x1; x++)
                {
                    float s = smooth[row + x];
                    if (s < min) min = s;
                    if (s > max) max = s;
                }
            }

            float range = max - min;
            if (range < 1e-6f) return 0f;
            float scale = (BinCount - 1) / range;

            int total = 0;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * width;
                for (int x = x0; x <= x1; x++)
                {
                    float s = smooth[row + x];
                    int b = (int)((s - min) * scale);
                    if (b < 0) b = 0;
                    else if (b >= BinCount) b = BinCount - 1;
                    bins[b]++;
                    total++;
                }
            }

            if (total <= 0) return 0f;
            float invTotal = 1f / total;
            float H = 0f;
            for (int i = 0; i < BinCount; i++)
            {
                int c = bins[i];
                if (c == 0) continue;
                float p = c * invTotal;
                H -= p * MathF.Log2(p);
            }
            return H * InvLogBin;
        }

        public static bool IsInterior(float smooth) => smooth <= 0f;

        public static void UnpackARGB(uint c, out float r, out float g, out float b)
        {
            r = ((c >> 16) & 0xFF) / 255f;
            g = ((c >>  8) & 0xFF) / 255f;
            b = ( c        & 0xFF) / 255f;
        }

        public static uint PackARGB(float r, float g, float b)
        {
            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
        }
    }

    // =========================================================================
    // EntropyHeatmap — pure entropy display
    // =========================================================================

    /// <summary>
    /// Direct visualisation of local Shannon entropy.  PostProcess computes H
    /// per exterior pixel and paints a cool→hot gradient at that value.
    /// </summary>
    public sealed class EntropyHeatmap : GradientColorMap, IPostProcessColorMap
    {
        public static string Name => "Entropy - Heatmap";
        public static string Category => "Information Theory";
        public static string Description =>
            "Local Shannon entropy of the smooth-iteration field, displayed as " +
            "a cool→hot gradient.  Smooth regions read dark/cool; chaotic " +
            "boundary filaments saturate hot.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.UsesPostProcess | ColorMapFeatures.Perceptual;

        public new ColorPaletteType Type => ColorPaletteType.Scientific;

        public EntropyHeatmap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(  8,   4,  30)));
            Stops.Add(new ColorStop(0.15f, Color.FromArgb( 40,  20, 100)));
            Stops.Add(new ColorStop(0.35f, Color.FromArgb( 30, 110, 180)));
            Stops.Add(new ColorStop(0.55f, Color.FromArgb( 40, 200, 170)));
            Stops.Add(new ColorStop(0.72f, Color.FromArgb(220, 220,  60)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(250, 140,  40)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 250, 230)));
        }

        public void PostProcess(uint[] colorBuf, float[] smooth, float[] nx, float[] ny,
                                int width, int height, int iterations)
        {
            Parallel.For(0, height, y =>
            {
                int rowBase = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowBase + x;
                    float s = smooth[idx];
                    if (LocalEntropy.IsInterior(s))
                    {
                        colorBuf[idx] = 0xFF000000u;
                        continue;
                    }

                    float H = LocalEntropy.ComputeAt(smooth, width, height, x, y);
                    int argb = MapNormalized(H, 0f);
                    colorBuf[idx] = unchecked((uint)argb);
                }
            });
        }
    }

    // =========================================================================
    // EntropyContrastMap — entropy modulates iteration gradient
    // =========================================================================

    /// <summary>
    /// Base twilight gradient driven by iteration count; PostProcess scales
    /// saturation and brightness by local entropy.  Smooth regions desaturate
    /// toward muted blue-gray; chaotic regions retain full vibrancy.
    /// </summary>
    public sealed class EntropyContrastMap : GradientColorMap, IPostProcessColorMap
    {
        public static string Name => "Entropy - Contrast";
        public static string Category => "Information Theory";
        public static string Description =>
            "Twilight gradient by iteration count, modulated by local Shannon " +
            "entropy.  Low-entropy regions desaturate and dim; high-entropy " +
            "filaments stay vivid.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.UsesPostProcess;

        public new ColorPaletteType Type => ColorPaletteType.Scientific;

        private const float MinSaturation = 0.20f;   // sat scale at H = 0
        private const float MinBrightness = 0.45f;   // value scale at H = 0

        public EntropyContrastMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 18,  10,  45)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb( 70,  35, 120)));
            Stops.Add(new ColorStop(0.40f, Color.FromArgb(150,  60, 150)));
            Stops.Add(new ColorStop(0.60f, Color.FromArgb(230, 130, 130)));
            Stops.Add(new ColorStop(0.80f, Color.FromArgb(245, 210, 130)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 250, 220)));
        }

        public void PostProcess(uint[] colorBuf, float[] smooth, float[] nx, float[] ny,
                                int width, int height, int iterations)
        {
            Parallel.For(0, height, y =>
            {
                int rowBase = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowBase + x;
                    float s = smooth[idx];
                    if (LocalEntropy.IsInterior(s)) continue;

                    float H = LocalEntropy.ComputeAt(smooth, width, height, x, y);

                    LocalEntropy.UnpackARGB(colorBuf[idx], out float r, out float g, out float b);

                    // Pull color toward its luminance (desaturate) by (1 - H_sat_weight).
                    float satScale = MinSaturation + (1f - MinSaturation) * H;
                    float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    r = lum + (r - lum) * satScale;
                    g = lum + (g - lum) * satScale;
                    b = lum + (b - lum) * satScale;

                    float vScale = MinBrightness + (1f - MinBrightness) * H;
                    r *= vScale; g *= vScale; b *= vScale;

                    colorBuf[idx] = LocalEntropy.PackARGB(r, g, b);
                }
            });
        }
    }

    // =========================================================================
    // EntropyEdgeMap — gold filament glow on chaotic regions
    // =========================================================================

    /// <summary>
    /// Dark deep-teal base from iteration count.  PostProcess additively
    /// blends a gold-white tint into pixels where local entropy exceeds a
    /// threshold, producing a filament glow tracing high-information regions.
    /// </summary>
    public sealed class EntropyEdgeMap : GradientColorMap, IPostProcessColorMap
    {
        public static string Name => "Entropy - Filament Glow";
        public static string Category => "Information Theory";
        public static string Description =>
            "Dark deep-teal base modulated by additive gold glow where local " +
            "Shannon entropy is high.  Highlights chaotic boundary filaments " +
            "against a quiet exterior.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.UsesPostProcess | ColorMapFeatures.HighContrast;

        public new ColorPaletteType Type => ColorPaletteType.Scientific;

        private const float GlowThreshold = 0.35f;   // H below this → no glow
        private const float GlowStrength  = 1.25f;   // peak additive amplitude
        private static readonly float TintR = 1.00f;
        private static readonly float TintG = 0.82f;
        private static readonly float TintB = 0.40f;

        public EntropyEdgeMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(  4,   8,  16)));
            Stops.Add(new ColorStop(0.30f, Color.FromArgb( 10,  30,  50)));
            Stops.Add(new ColorStop(0.65f, Color.FromArgb( 18,  60,  80)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 30,  90, 100)));
        }

        public void PostProcess(uint[] colorBuf, float[] smooth, float[] nx, float[] ny,
                                int width, int height, int iterations)
        {
            float invSpan = 1f / (1f - GlowThreshold);

            Parallel.For(0, height, y =>
            {
                int rowBase = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowBase + x;
                    float s = smooth[idx];
                    if (LocalEntropy.IsInterior(s)) continue;

                    float H = LocalEntropy.ComputeAt(smooth, width, height, x, y);
                    if (H <= GlowThreshold) continue;

                    float t = (H - GlowThreshold) * invSpan;
                    float k = t * t * GlowStrength;   // quadratic ramp

                    LocalEntropy.UnpackARGB(colorBuf[idx], out float r, out float g, out float b);
                    r += TintR * k;
                    g += TintG * k;
                    b += TintB * k;
                    colorBuf[idx] = LocalEntropy.PackARGB(r, g, b);
                }
            });
        }
    }
}
