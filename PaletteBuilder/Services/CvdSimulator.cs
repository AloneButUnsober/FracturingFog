// Services/CvdSimulator.cs
//
// Colour-vision-deficiency simulation via the Machado et al. 2009
// transformation matrices (severity 1.0 — full dichromacy). Operates in
// linear-sRGB to avoid the sRGB gamma double-applying through the matrix.
//
// Three flavours covered: protanopia (no L cones), deuteranopia (no M),
// tritanopia (no S). Each maps an sRGB triple to the closest perceptible
// equivalent for a viewer with that condition.

using System;
using FracturingFog.Imaging.PaletteExtraction;

namespace PaletteBuilder.Services
{
    public enum CvdKind
    {
        None,
        Protanopia,
        Deuteranopia,
        Tritanopia,
    }

    public static class CvdSimulator
    {
        // Machado et al. 2009 severity = 1.0 matrices, row-major 3x3 in linear sRGB.
        private static readonly float[] M_Pro =
        {
            0.152286f, 1.052583f, -0.204868f,
            0.114503f, 0.786281f,  0.099216f,
           -0.003882f, -0.048116f, 1.051998f,
        };

        private static readonly float[] M_Deu =
        {
            0.367322f, 0.860646f, -0.227968f,
            0.280085f, 0.672501f,  0.047413f,
           -0.011820f, 0.042940f,  0.968881f,
        };

        private static readonly float[] M_Tri =
        {
            1.255528f, -0.076749f, -0.178779f,
           -0.078411f,  0.930809f,  0.147602f,
            0.004733f,  0.691367f,  0.303900f,
        };

        public static (byte R, byte G, byte B) Simulate(byte r, byte g, byte b, CvdKind kind)
        {
            if (kind == CvdKind.None) return (r, g, b);
            float[] m = kind switch
            {
                CvdKind.Protanopia => M_Pro,
                CvdKind.Deuteranopia => M_Deu,
                _ => M_Tri,
            };
            float lr = ColorSpaces.SrgbToLinear(r / 255f);
            float lg = ColorSpaces.SrgbToLinear(g / 255f);
            float lb = ColorSpaces.SrgbToLinear(b / 255f);

            float or = m[0] * lr + m[1] * lg + m[2] * lb;
            float og = m[3] * lr + m[4] * lg + m[5] * lb;
            float ob = m[6] * lr + m[7] * lg + m[8] * lb;

            return (ColorSpaces.LinearToSrgbByte(or),
                    ColorSpaces.LinearToSrgbByte(og),
                    ColorSpaces.LinearToSrgbByte(ob));
        }
    }
}
