// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Threading;

using FracturingFog.Models;

namespace FracturingFog.Interefaces
{
    /// <summary>
    /// Minimal surface MainForm uses to drive any non-Mandelbrot fractal
    /// calculator (escape-time, IFS, L-system, attractor, Buddhabrot, Newton).
    /// Mandelbrot continues to use the concrete MandelbrotCalculator directly
    /// because it exposes deep-zoom-specific properties (CenterXLo, X2/X3,
    /// DisableSeriesApproximation, etc.) that don't generalize.
    /// </summary>
    public interface IFractalCalculator
    {
        int Width { get; }
        int Height { get; }
        uint[] ColorBuffer { get; }

        double CenterX { get; set; }
        double CenterY { get; set; }
        double Zoom { get; set; }
        int MaxIterations { get; set; }
        QualityPreset Quality { get; set; }
        IColorMap ColorMap { get; set; }

        bool SupportsZoomPan { get; }

        void Resize(int width, int height);
        void Calculate(CancellationToken ct);
    }

    /// <summary>
    /// Implemented by escape-time 2D calculators that expose a per-pixel smooth
    /// iteration count usable as a height field (#102 heightfield relief). The
    /// render host reads <see cref="SmoothBuffer"/> off the active calculator to
    /// drive <c>HeightfieldRelief2D</c>. Auto-satisfied by any calculator that
    /// already has a public <c>float[] SmoothBuffer</c> (Mandelbrot, the
    /// EscapeTimeCalculator family, and every CalcGen-generated escape-time
    /// calculator).
    /// </summary>
    public interface IHeightFieldSource
    {
        /// <summary>Per-pixel smooth (continuous) iteration count; in-set pixels
        /// read 0. Same length/layout as <c>ColorBuffer</c>.</summary>
        float[] SmoothBuffer { get; }
    }
}
