using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class MonoBandMap : IColorMap
    {
        public static string Name => "Monochrome Bands";

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            float v = 0.5f + 0.5f * MathF.Sin(smooth * 0.1f);
            byte b = (byte)(v * 255);
            return unchecked((int)0xFF000000 | (b << 16) | (b << 8) | b);
        }
    }

}
