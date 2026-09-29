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

    /// <summary>Implemented by calculators that fill a per-pixel orbit-trap
    /// min-distance field when an orbit-trap theme runs — the alternative relief
    /// height source (roadmap S11, #592 / #726). <c>TrapBuffer</c> holds
    /// <c>OrbitAccumulator.TrapMin</c> per pixel (0 = no trap sampled / in set).
    /// <see cref="FracturingFog.Rendering.Lighting.ReliefHeightField.Build"/> reads it
    /// generically, so any trap source (Mandelbrot, User Equation, …) drives orbit-trap
    /// relief without a Mandelbrot-only gate. Same length / layout as <c>ColorBuffer</c>.</summary>
    public interface ITrapFieldSource
    {
        /// <summary>Per-pixel orbit-trap min-distance; 0 for in-set / no-trap pixels.</summary>
        float[] TrapBuffer { get; }
    }

    /// <summary>#1029 — implemented by calculators that fill a per-pixel exterior
    /// distance estimate (complex-plane units; 0 for in-set pixels) — the Distance
    /// relief height source. <see cref="DistancePixelScale"/> is the complex-plane
    /// width of one pixel for the last calculation, so the estimate can be put in
    /// view units.</summary>
    public interface IDistanceFieldSource
    {
        float[] DistanceBuffer { get; }
        double DistancePixelScale { get; }
    }

    /// <summary>Implemented by the 3D raymarch calculators, which can render one
    /// eye of a true (two-render) stereo pair (#107 / #1008). The camera origin is
    /// shifted by <see cref="StereoEyeOffset"/> world units along the camera's
    /// right basis. The offset lives on the calculator, not in the shared
    /// <c>FractalParameters</c>, so a UI edit made during the two eye renders is
    /// never overwritten and the transient offset can never reach a saved preset.</summary>
    public interface IStereoEyeCamera
    {
        /// <summary>Per-eye camera offset along the right basis (world units).
        /// <c>StereoRender.RenderTrueStereo</c> sets <c>-IPD/2</c> / <c>+IPD/2</c>
        /// around the two passes and resets it to 0. Default 0 = mono.</summary>
        double StereoEyeOffset { get; set; }
    }

    /// <summary>Implemented by the 3D raymarch calculators (#1009): the per-pixel
    /// ray distance of the last <c>Calculate</c>, for passes that run after the
    /// frame (the depth-parallax stereo warp, later autostereograms, #1014).
    /// Published only when <c>ScreenSpacePost.WantsDepthOutput</c> asks for it.</summary>
    public interface IDepthAovSource
    {
        /// <summary>Per-pixel ray distance at <c>ColorBuffer</c> dims
        /// (<c>ScreenSpacePost.DepthMiss</c> = +Infinity for sky), or null when
        /// the last frame did not capture a usable depth (not requested, GPU /
        /// thin-lens / cached frame, or cancelled). Reset at the start of every
        /// <c>Calculate</c>, so it never describes an older frame.</summary>
        float[]? DepthBuffer { get; }
    }
}
