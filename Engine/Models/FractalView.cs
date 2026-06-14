using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace FracturingFog.Models
{
    [StructLayout(LayoutKind.Sequential)]
    public struct FractalConstants
    {
        public float CenterX;
        public float CenterY;
        public float Scale;

        public float RealMin;
        public float RealMax;
        public float ImagMin;
        public float ImagMax;

        public uint Width;
        public uint Height;
        public uint MaxIterations;
        public uint Padding; // keep 16‑byte alignment
    }

    public static class Fractals
    {
        public static FractalConstants BuildConstants(RenderSettings s)
        {
            return new FractalConstants
            {
                CenterX = s.CenterX,
                CenterY = s.CenterY,
                Scale = s.Scale,

                RealMin = s.RealMin,
                RealMax = s.RealMax,
                ImagMin = s.ImagMin,
                ImagMax = s.ImagMax,

                Width = (uint)s.Width,
                Height = (uint)s.Height,
                MaxIterations = (uint)s.Iterations,
                Padding = 0
            };
        }

        public static int HsvToRgb(float h, float s, float v)
        {
            int r, g, b;
            int a = 255; // alpha channel
            int packed = 0;
            if (s == 0)
            {
                r = g = b = (int)(v * 255);
                return (a << 24) | (r << 16) | (g << 8) | b;
            }

            h = h * 6; // sector 0 to 5
            int i = (int)MathF.Floor(h);
            float f = h - i; // fractional part of h
            float p = v * (1 - s);
            float q = v * (1 - s * f);
            float t = v * (1 - s * (1 - f));
            float rF, gF, bF;
            switch (i % 6)
            {
                case 0: rF = v; gF = t; bF = p; break;
                case 1: rF = q; gF = v; bF = p; break;
                case 2: rF = p; gF = v; bF = t; break;
                case 3: rF = p; gF = q; bF = v; break;
                case 4: rF = t; gF = p; bF = v; break;
                case 5: rF = v; gF = p; bF = q; break;
                default: rF = gF = bF = 0; break; // should never happen
            }

            r = (int)(rF * 255);
            g = (int)(gF * 255);
            b = (int)(bF * 255);

            packed = (a << 24) | (r << 16) | (g << 8) | b;
            return packed;
        }

        public static readonly Vector<float> THRESHOLD = new Vector<float>(4f);

        public static readonly Vector<float> FONE = Vector<float>.One;

        public static readonly Vector<float> FZERO = Vector<float>.Zero;

        public static readonly Vector<int> IONE = Vector<int>.One;

        public static readonly Vector<int> IZERO = Vector<int>.Zero;

        public static readonly int BATCHSIZE = Vector<float>.Count;

        public static readonly int TILESIZE = 64;

        public static readonly bool AVX2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported;

        public static readonly bool AVX = System.Runtime.Intrinsics.X86.Avx.IsSupported;

        public static readonly bool SSE2 = System.Runtime.Intrinsics.X86.Sse2.IsSupported;

        public static Dictionary<FractalType, string> FractalNameByNameType => new()
        {
            {FractalType.BuddhaBrot, "BuddhaBrot" },
            {FractalType.Nebulabrot, "Nebulabrot" },
            {FractalType.AntiBuddhabrot, "AntiBuddhabrot" },
            {FractalType.AntiNebulabrot, "AntiNebulabrot" },
            {FractalType.BurningShip, "BurningShip" },
            {FractalType.IFS, "IFS" },
            {FractalType.Julia, "Julia" },
            {FractalType.LSystem, "LSystem" },
            {FractalType.Mandelbrot, "Mandelbrot" },
            {FractalType.Mandelbulb, "Mandelbulb" },
            {FractalType.Multibrot, "Multibrot" },
            {FractalType.Newton, "Newton" },
            {FractalType.Nova, "Nova" },
            {FractalType.Phoenix, "Phoenix" },
            {FractalType.Sandbox, "Sandbox" },
            {FractalType.StrangeAttractor, "StrangeAttractor" },
            {FractalType.Tricorn, "Tricorn" },
            {FractalType.UserEquation, "UserEquation" },
            {FractalType.UserBulb, "UserBulb" },
            {FractalType.TearDrop, "Tear Drop" },
            {FractalType.Magnet1, "Magnet 1" },
            {FractalType.Magnet2, "Magnet 2" },
            {FractalType.Glynn, "Glynn" },
            {FractalType.Logistic, "Logistic" },
            {FractalType.Halley, "Halley" },
            {FractalType.Secant, "Secant" },
            {FractalType.Spider, "Spider" },
            {FractalType.Mandelbox, "Mandelbox" }
        };
    }

    public record FractalView(string Name, float CenterX, float CenterY, float Scale);

    public static class FractalViews
    {
        public static readonly FractalView Classic = new("Classic", -0.75f, 0.0f, 3.5f);

        public static readonly FractalView SeahorseValley = new("Seahorse Valley", -0.7435f, 0.1314f, 0.02f);

        public static readonly FractalView ElephantValley = new("Elephant Valley", -0.743f, 0.11f, 0.03f);
    }
}
