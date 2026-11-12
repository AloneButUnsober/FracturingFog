// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/MathOverlayFrames.cs
//
// #1746 — the pinned math overlay on every frame of a --batch video. The 2D overlay
// (--math-overlay, #1554) is drawn with each frame's own view (centre, zoom). The 3D overlay
// (--math-overlay-3d, #1735) is drawn with each frame's own camera and depth, so an orbit
// video shows the pinned point's orbit from the moving viewpoint. The draw calls are the
// poster's (PosterRenderer.ApplyMathOverlay / ApplyMathOverlay3D), so a frame equals the
// still of its view.
//
// The video loop calls BeforeCalculate / AfterCalculate around the frame's first Calculate
// (the unjittered sample, before AA pans the camera), then Draw on the graded frame, as the
// poster draws last on its display buffer.

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.ViewState;

namespace FracturingFog.Imaging
{
    /// <summary>#1746 — the pinned 2D or 3D math overlay drawn per video frame.</summary>
    public sealed class MathOverlayFrames
    {
        public MathOverlayFrames(MathOverlaySpec? spec2D, MathOverlay3DSpec? spec3D)
        {
            Spec2D = spec2D;
            Spec3D = spec3D;
            Capture3D = spec3D != null ? new PosterRenderer.Overlay3DCapture() : null;
        }

        public MathOverlaySpec? Spec2D { get; }
        public MathOverlay3DSpec? Spec3D { get; }
        public bool Active => Spec2D != null || Spec3D != null;

        /// <summary>The camera and depth of the last frame's first sample (3D only).</summary>
        public PosterRenderer.Overlay3DCapture? Capture3D { get; }

        /// <summary>Frames the overlay was drawn on.</summary>
        public int FramesDrawn { get; private set; }
        /// <summary>Frames drawn with no depth (thin-lens DoF), nothing dimmed.</summary>
        public int FramesWithoutDepth { get; private set; }
        /// <summary>Why a frame was not drawn on (the last reason), or null.</summary>
        public string? SkipReason { get; private set; }

        /// <summary>Before the frame's first Calculate: a 3D overlay asks for depth.</summary>
        public void BeforeCalculate(IFractalCalculator? alt)
        {
            if (Spec3D != null) PosterRenderer.RequestOverlayDepth(alt);
        }

        /// <summary>After the frame's first Calculate: keep its camera and depth.</summary>
        public void AfterCalculate(IFractalCalculator? alt)
        {
            if (Capture3D != null) PosterRenderer.CaptureOverlay3D(alt, Capture3D);
        }

        /// <summary>Draw on the finished <paramref name="w"/>×<paramref name="h"/> frame whose view
        /// is (<paramref name="cx"/>, <paramref name="cy"/>, <paramref name="zoom"/>).</summary>
        public void Draw(uint[] frame, int w, int h, FractalType type, FractalParameters? fp,
                         double cx, double cy, double zoom)
        {
            if (Spec2D is { } s2)
            {
                PosterRenderer.ApplyMathOverlay(new PosterRequest
                {
                    FractalType = type, Width = w, Height = h, CenterX = cx, CenterY = cy, Zoom = zoom,
                    FractalParameters = fp, MathOverlay = s2,
                }, frame, w, h);
                FramesDrawn++;
            }
            else if (Spec3D is { } s3)
            {
                var report = new MathOverlay3DReport();
                PosterRenderer.ApplyMathOverlay3D(new PosterRequest
                {
                    FractalType = type, Width = w, Height = h, FractalParameters = fp,
                    MathOverlay3D = s3, MathOverlay3DReport = report,
                }, Capture3D, frame, w, h);
                if (report.Drawn) { FramesDrawn++; if (report.NoDepth) FramesWithoutDepth++; }
                else SkipReason = report.Reason;
            }
        }

        /// <summary>A frame that can't carry the overlay (a stereo pair): record why.</summary>
        public void Skip(string reason)
        {
            if (Active) SkipReason = reason;
        }
    }
}
