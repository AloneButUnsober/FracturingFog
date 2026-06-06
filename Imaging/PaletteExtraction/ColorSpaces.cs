// Imaging/PaletteExtraction/ColorSpaces.cs
//
// Lightweight color-space conversions used by the palette-from-image
// pipeline. RGB byte triples in [0,255], Lab roughly in L:[0,100] a/b:[-128,127],
// HSL with H in [0,360), S/L in [0,1].
//
// OkLab is Björn Ottosson's perceptually uniform space:
// https://bottosson.github.io/posts/oklab/. Euclidean distance in OkLab
// approximates perceptual difference closely enough that k-means in OkLab
// is a quality upgrade over k-means in CIELab without needing CIEDE2000 in
// the inner loop. L roughly [0,1]; a/b roughly [-0.5, 0.5].

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
            float rf = SrgbToLinear(r / 255f);
            float gf = SrgbToLinear(g / 255f);
            float bf = SrgbToLinear(b / 255f);

            float X = rf * 0.4124564f + gf * 0.3575761f + bf * 0.1804375f;
            float Y = rf * 0.2126729f + gf * 0.7151522f + bf * 0.0721750f;
            float Z = rf * 0.0193339f + gf * 0.1191920f + bf * 0.9503041f;

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

        // ── OkLab (Björn Ottosson) ─────────────────────────────────────────

        public static void RgbToOkLab(byte r, byte g, byte b, out float L, out float A, out float B)
        {
            float rf = SrgbToLinear(r / 255f);
            float gf = SrgbToLinear(g / 255f);
            float bf = SrgbToLinear(b / 255f);
            LinearRgbToOkLab(rf, gf, bf, out L, out A, out B);
        }

        public static void LinearRgbToOkLab(float r, float g, float b, out float L, out float A, out float B)
        {
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

        public static void OkLabToLinearRgb(float L, float A, float B, out float r, out float g, out float b)
        {
            float l_ = L + 0.3963377774f * A + 0.2158037573f * B;
            float m_ = L - 0.1055613458f * A - 0.0638541728f * B;
            float s_ = L - 0.0894841775f * A - 1.2914855480f * B;

            float l = l_ * l_ * l_;
            float m = m_ * m_ * m_;
            float s = s_ * s_ * s_;

            r =  4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
            g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
            b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
        }

        /// <summary>OkLab → sRGB bytes, clipping out-of-gamut values.</summary>
        public static void OkLabToRgb(float L, float A, float B, out byte r, out byte g, out byte b)
        {
            OkLabToLinearRgb(L, A, B, out float lr, out float lg, out float lb);
            r = LinearToSrgbByte(lr);
            g = LinearToSrgbByte(lg);
            b = LinearToSrgbByte(lb);
        }

        // ── sRGB ↔ linear ──────────────────────────────────────────────────

        public static float SrgbToLinear(float c)
            => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        public static float LinearToSrgb(float c)
            => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(MathF.Max(c, 0f), 1f / 2.4f) - 0.055f;

        public static byte LinearToSrgbByte(float c)
        {
            float v = LinearToSrgb(c);
            int i = (int)MathF.Round(MathF.Max(0f, MathF.Min(1f, v)) * 255f);
            return (byte)i;
        }

        private static float LabF(float t)
        {
            const float d = 6f / 29f;
            return t > d * d * d
                ? MathF.Pow(t, 1f / 3f)
                : t / (3f * d * d) + 4f / 29f;
        }

        // ── ΔE metrics ─────────────────────────────────────────────────────

        /// <summary>CIE76 ΔE in Lab. Cheap proxy for perceptual distance.</summary>
        public static float DeltaE76(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            float dL = L1 - L2, da = a1 - a2, db = b1 - b2;
            return MathF.Sqrt(dL * dL + da * da + db * db);
        }

        /// <summary>
        /// CIEDE2000 — full perceptual ΔE with hue/chroma/lightness weighting.
        /// More accurate than ΔE76 at the cost of trig calls; use for dedup
        /// and one-shot comparisons, not inner loops.
        /// </summary>
        public static float DeltaE2000(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            const float kL = 1f, kC = 1f, kH = 1f;

            double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double Cbar = (C1 + C2) * 0.5;

            double Cbar7 = Math.Pow(Cbar, 7);
            double G = 0.5 * (1 - Math.Sqrt(Cbar7 / (Cbar7 + Math.Pow(25, 7))));

            double a1p = (1 + G) * a1;
            double a2p = (1 + G) * a2;
            double C1p = Math.Sqrt(a1p * a1p + b1 * b1);
            double C2p = Math.Sqrt(a2p * a2p + b2 * b2);

            double h1p = AtanDeg(b1, a1p);
            double h2p = AtanDeg(b2, a2p);

            double dLp = L2 - L1;
            double dCp = C2p - C1p;

            double dhp;
            if (C1p * C2p == 0) dhp = 0;
            else
            {
                double diff = h2p - h1p;
                if (diff > 180) diff -= 360;
                else if (diff < -180) diff += 360;
                dhp = diff;
            }
            double dHp = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(DegToRad(dhp) * 0.5);

            double Lpbar = (L1 + L2) * 0.5;
            double Cpbar = (C1p + C2p) * 0.5;

            double hpbar;
            if (C1p * C2p == 0) hpbar = h1p + h2p;
            else
            {
                double sum = h1p + h2p;
                double diff = Math.Abs(h1p - h2p);
                if (diff <= 180) hpbar = sum * 0.5;
                else hpbar = (sum + (sum < 360 ? 360 : -360)) * 0.5;
            }

            double T = 1
                - 0.17 * Math.Cos(DegToRad(hpbar - 30))
                + 0.24 * Math.Cos(DegToRad(2 * hpbar))
                + 0.32 * Math.Cos(DegToRad(3 * hpbar + 6))
                - 0.20 * Math.Cos(DegToRad(4 * hpbar - 63));

            double dTheta = 30 * Math.Exp(-Math.Pow((hpbar - 275) / 25, 2));
            double Cpbar7 = Math.Pow(Cpbar, 7);
            double Rc = 2 * Math.Sqrt(Cpbar7 / (Cpbar7 + Math.Pow(25, 7)));
            double Lpbarm50 = Lpbar - 50;
            double Sl = 1 + (0.015 * Lpbarm50 * Lpbarm50) / Math.Sqrt(20 + Lpbarm50 * Lpbarm50);
            double Sc = 1 + 0.045 * Cpbar;
            double Sh = 1 + 0.015 * Cpbar * T;
            double Rt = -Math.Sin(DegToRad(2 * dTheta)) * Rc;

            double termL = dLp / (kL * Sl);
            double termC = dCp / (kC * Sc);
            double termH = dHp / (kH * Sh);

            double dE = Math.Sqrt(termL * termL + termC * termC + termH * termH
                + Rt * termC * termH);
            return (float)dE;
        }

        private static double AtanDeg(double y, double x)
        {
            if (y == 0 && x == 0) return 0;
            double a = Math.Atan2(y, x) * 180.0 / Math.PI;
            return a < 0 ? a + 360.0 : a;
        }

        private static double DegToRad(double d) => d * Math.PI / 180.0;
    }
}
