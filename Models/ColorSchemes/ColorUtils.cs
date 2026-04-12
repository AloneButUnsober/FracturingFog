using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public struct ColorStop
    {
        public float Position; // 0..1
        public System.Drawing.Color Color;

        public ColorStop(float pos, System.Drawing.Color color)
        {
            Position = pos;
            Color = color;
        }
    }

    public static class ColorUtils
    {
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

        private static System.Drawing.Color FromFloat(float r, float g, float b)
        {
            return System.Drawing.Color.FromArgb(
                255,
                (byte)(r * 255),
                (byte)(g * 255),
                (byte)(b * 255));
        }
    }

    public abstract class GradientColorMap : IColorMap
    {
        protected readonly List<ColorStop> Stops = new();

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            float t = smooth / maxIterations;
            t = Math.Clamp(t, 0f, 1f);

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

}
