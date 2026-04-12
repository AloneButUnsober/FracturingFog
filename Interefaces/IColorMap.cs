using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Interefaces
{
    public interface IColorMap
    {
        public static string Name { get; } = "No Color Map";

        public int MaxIterations { get; set; }

        int Map(float smooth, float distance, int iterations);
    }
}