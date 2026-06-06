// Services/WcagContrast.cs
//
// WCAG 2.1 relative luminance + contrast-ratio helpers. Produces the same
// numeric value the W3C uses to grade text/background pairs (AA, AAA).
//   1:1   = identical
//   3:1   = AA Large text minimum
//   4.5:1 = AA Normal text minimum
//   7:1   = AAA Normal text minimum
//
// Caller can feed any two swatches to RatioBetween and tag a row with its
// AA / AAA badge via PassLevel.

using System;
using FracturingFog.Imaging.PaletteExtraction;

namespace PaletteBuilder.Services
{
    public enum WcagPass
    {
        Fail,           // < 3:1
        AALarge,        // ≥ 3:1
        AANormal,       // ≥ 4.5:1
        AAA,            // ≥ 7:1
    }

    public static class WcagContrast
    {
        public static double RelativeLuminance(byte r, byte g, byte b)
        {
            double R = ColorSpaces.SrgbToLinear(r / 255f);
            double G = ColorSpaces.SrgbToLinear(g / 255f);
            double B = ColorSpaces.SrgbToLinear(b / 255f);
            return 0.2126 * R + 0.7152 * G + 0.0722 * B;
        }

        public static double RatioBetween(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
        {
            double L1 = RelativeLuminance(r1, g1, b1);
            double L2 = RelativeLuminance(r2, g2, b2);
            if (L1 < L2) (L1, L2) = (L2, L1);
            return (L1 + 0.05) / (L2 + 0.05);
        }

        public static WcagPass GradeRatio(double ratio)
        {
            if (ratio >= 7.0) return WcagPass.AAA;
            if (ratio >= 4.5) return WcagPass.AANormal;
            if (ratio >= 3.0) return WcagPass.AALarge;
            return WcagPass.Fail;
        }

        public static string FormatBadge(WcagPass p) => p switch
        {
            WcagPass.AAA => "AAA",
            WcagPass.AANormal => "AA",
            WcagPass.AALarge => "AA·L",
            _ => "—",
        };
    }
}
