// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using FracturingFog.Models;

namespace FracturingFog.Interefaces
{
    public interface IRenderable
    {
        public float CenterX { get; set; }

        public float CenterY { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public float Scale { get; set; }

        public float RealMin { get; set; }

        public float RealMax { get; set; }

        public float ImagMin { get; set; }

        public float ImagMax { get; set; }

        public int Iterations { get; set; }

        public bool UseDoublePrecision { get; set; }

        public IColorMap? ColorMap { get; set; }

        public RenderSettings RenderSettings { get; set; }

        int[] OutputBuffer { get; }

        int[] ColorBuffer { get; }

        void Render(CancellationToken token);

        public QualityLevel QualityLevel { get; set; }

        void ResetView(int Width = 800, int Height = 600);

        public bool SaveImage(string path);
    }
}
