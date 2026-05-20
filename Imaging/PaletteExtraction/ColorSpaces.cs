// Imaging/PaletteExtraction/ColorSpaces.cs
//
// Lightweight color-space conversions used by the palette-from-image
// pipeline. RGB byte triples in [0,255], Lab roughly in L:[0,100] a/b:[-128,127],
// HSL with H in [0,360), S/L in [0,1].

using System;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public static class ColorSpaces
    {
        public static void RgbToHsl(byte r, byte g, byte b, out float h, out float s, out float l)
        {
            float rf = r / 255f, gf = g / 255f, bf = b / 255f;
            float max = MathF.Max(rf, MathF.Max(gf, bf));
            float min = MathF.Min(rf, MathF.Min(gf, bf));
            l = (max + min) * 0.5f;
            float d = max - min;
            if (d < 1e-6f) { h = 0f; s = 0f; return; }
            s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
            if (max == rf) h = ((gf - bf) / d + (gf < bf ? 6f : 0f)) * 60f;
            else if (max == gf) h = ((bf - rf) / d + 2f) * 60f;
            else h = ((rf - gf) / d + 4f) * 60f;
        }

        public static float Luminance(byte r, byte g, byte b)
            => 0.2126f * r + 0.7152f * g + 0.0722f * b;

        public static void RgbToLab(byte r, byte g, byte b, out float L, out float a, out float bb)
        {
            // sRGB → linear
            float rf = SrgbToLinear(r / 255f);
            float gf = SrgbToLinear(g / 255f);
            float bf = SrgbToLinear(b / 255f);

            // linear RGB → XYZ (D65)
            float X = rf * 0.4124564f + gf * 0.3575761f + bf * 0.1804375f;
            float Y = rf * 0.2126729f + gf * 0.7151522f + bf * 0.0721750f;
            float Z = rf * 0.0193339f + gf * 0.1191920f + bf * 0.9503041f;

            // Normalize by D65 white
            X /= 0.95047f;
            Y /= 1.00000f;
            Z /= 1.08883f;

            float fx = LabF(X);
            float fy = LabF(Y);
            float fz = LabF(Z);

            L = 116f * fy - 16f;
            a = 500f * (fx - fy);
            bb = 200f * (fy - fz);
        }

        private static float SrgbToLinear(float c)
            => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        private static float LabF(float t)
        {
            const float d = 6f / 29f;
            return t > d * d * d
                ? MathF.Pow(t, 1f / 3f)
                : t / (3f * d * d) + 4f / 29f;
        }

        /// <summary>CIE76 ΔE in Lab. Cheap proxy for perceptual distance.</summary>
        public static float DeltaE76(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            float dL = L1 - L2, da = a1 - a2, db = b1 - b2;
            return MathF.Sqrt(dL * dL + da * da + db * db);
        }
    }
}
