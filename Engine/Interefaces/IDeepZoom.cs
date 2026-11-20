// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

namespace FracturingFog.Interefaces
{
    /// <summary>
    /// #1921 (epic #1253, D2b) — a non-Mandelbrot calculator that takes the zoom as a log10
    /// past double (<see cref="IFractalCalculator.Zoom"/> saturates at ~1e308), as
    /// MandelbrotCalculator.SetZoomLog10 does. The render host, the video engine and the
    /// poster / batch / explore path hand the view's log zoom over through this when it is
    /// beyond double.
    /// </summary>
    public interface IDeepZoom
    {
        /// <summary>The zoom's log10: the one last set by <see cref="SetZoomLog10"/> while Zoom
        /// is unchanged since, else log10(Zoom).</summary>
        double ZoomLog10 { get; }

        /// <summary>Set the zoom from its log10 (Zoom saturates past double).</summary>
        void SetZoomLog10(double log10);
    }
}
