// Abstractions/ViewState/ViewCamera.cs
//
// The single screen<->world authority for 2D fractal navigation.
//
// Every interactive view change (pan, double-click focus, wheel zoom, box
// zoom, key pan) goes through one of the methods here. Each is tier-agnostic:
// the centre is always carried at octuple-double precision via DeepComplex, so
// there is no per-tier branch and no double->HP promotion to freeze an anchor
// error. Adding a new precision tier or a new zoom backend touches DeepComplex
// only; these methods and their call sites never change. That is the
// future-proofing — the six input handlers that were historically rewritten
// per precision tier now delegate here.
//
// The camera also owns the ONE screen<->world scale formula. Input and render
// must agree on pixels-per-plane-unit or clicks land in the wrong place; both
// derive it from PlaneExtent / (max(px) * zoom) so there is a single source of
// truth. Callers pass the viewport in the SAME pixel space the frame is
// rendered in.

using System;

namespace FracturingFog.ViewState
{
    /// <summary>Stateless-ish helper (holds only a reference to the mutated
    /// <see cref="FractalViewState"/>) that performs all 2D pan/zoom/focus math
    /// in one place, at full precision. Quality adaptation and event raising
    /// stay in the input controller; this type only moves the centre and
    /// zoom.</summary>
    public sealed class ViewCamera
    {
        /// <summary>Width of the complex plane mapped across the viewport at
        /// zoom 1. Must match the renderer's own scale constant (the
        /// MandelbrotCalculator uses 3.5) so a screen pixel maps to the same
        /// world distance in input and render.</summary>
        public const double PlaneExtent = 3.5;

        private readonly FractalViewState _vs;

        public ViewCamera(FractalViewState vs)
            => _vs = vs ?? throw new ArgumentNullException(nameof(vs));

        /// <summary>World units per pixel for a viewport whose largest dimension
        /// is <paramref name="viewW"/> × <paramref name="viewH"/> pixels.</summary>
        public double Scale(int viewW, int viewH)
            => PlaneExtent / (Math.Max(1, Math.Max(viewW, viewH)) * _vs.Zoom);

        /// <summary>The world coordinate currently under screen pixel
        /// (<paramref name="px"/>, <paramref name="py"/>).</summary>
        public FFMath.DeepComplex WorldFromScreen(double px, double py, int viewW, int viewH)
        {
            double s = Scale(viewW, viewH);
            double ox = px - viewW * 0.5;
            double oy = py - viewH * 0.5;
            return _vs.GetCenter().Translate(ox * s, oy * s);
        }

        /// <summary>Pan by a pixel delta (drag / key pan). Positive
        /// <paramref name="dxPx"/> moves the view content right, i.e. the centre
        /// moves left — callers pass the sign they want.</summary>
        public void TranslateByPixels(double dxPx, double dyPx, int viewW, int viewH)
        {
            double s = Scale(viewW, viewH);
            _vs.SetCenter(_vs.GetCenter().Translate(dxPx * s, dyPx * s));
        }

        /// <summary>Double-click focus / box-zoom recentre: make the world point
        /// under (<paramref name="px"/>, <paramref name="py"/>) the new screen
        /// centre. Does not change zoom.</summary>
        public void SetCenterToScreenPoint(double px, double py, int viewW, int viewH)
            => _vs.SetCenter(WorldFromScreen(px, py, viewW, viewH));

        /// <summary>Zoom by <paramref name="factor"/> while keeping the world
        /// point under (<paramref name="px"/>, <paramref name="py"/>) fixed on
        /// screen (wheel zoom about the cursor). Zoom is clamped to
        /// [<paramref name="zoomMin"/>, <paramref name="zoomMax"/>].</summary>
        public void ZoomAboutScreen(double px, double py, double factor,
                                    int viewW, int viewH, double zoomMin, double zoomMax)
        {
            double ox = px - viewW * 0.5;
            double oy = py - viewH * 0.5;
            var anchor = WorldFromScreen(px, py, viewW, viewH);   // world under cursor, old zoom
            _vs.Zoom = Math.Clamp(_vs.Zoom * factor, zoomMin, zoomMax);
            double sNew = Scale(viewW, viewH);
            // Put the anchor back under the cursor: centre = anchor - offset*newScale.
            _vs.SetCenter(anchor.Translate(-ox * sNew, -oy * sNew));
        }

        /// <summary>Box zoom: recentre on the box midpoint and multiply zoom by
        /// <paramref name="factor"/> (clamped). The midpoint becomes the screen
        /// centre.</summary>
        public void BoxZoomToPoint(double midPx, double midPy, double factor,
                                   int viewW, int viewH, double zoomMin, double zoomMax)
        {
            var anchor = WorldFromScreen(midPx, midPy, viewW, viewH);
            _vs.Zoom = Math.Clamp(_vs.Zoom * factor, zoomMin, zoomMax);
            _vs.SetCenter(anchor);
        }
    }
}
