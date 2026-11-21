// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
//
// #1836 (epic #1428, C3) — past double's zoom range the zoom moves in log10
// (FractalViewState.ZoomLog10), and when the view has an exact centre (#1832) or is
// past OD's coordinate floor, the centre moves exactly (BigFloat) instead of through
// the OD limbs, which would drop the exact centre or lose the move. Up to there every
// method is the double / OD path it always was.
//
// DEEP-ZOOM NAVIGATION NOTE: this path is provably EXACT to 1e70 (--inputprobe,
// and the input-math check in --navrepro: 9.5e-15 px at 4.65e64). If a user
// reports deep-zoom "controls off / drift / jitter," it is NOT here — it is the
// reference-dependent RENDER (or the per-point detail floor). Do not re-derive;
// see Docs/Deep-Zoom-Perturbation.md (§4-5) before changing anything in the
// input path.

using System;
using FracturingFog.FFMath;

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

        /// <summary>#1974 — world units per pixel of a <paramref name="width"/> x
        /// <paramref name="height"/> frame at <paramref name="zoom"/>: the camera's scale
        /// (<see cref="Scale"/>) without a view state, for renderers. Every 2D calculator maps
        /// pixels with this, so input and render agree.</summary>
        public static double PixelPitch(double width, double height, double zoom)
            => PlaneExtent / (Math.Max(1.0, Math.Max(width, height)) * Math.Max(1e-300, zoom));

        /// <summary>#1974 — the families that rendered with pitch (4 / width) / zoom before #1974
        /// (input landed at 3.5/4 of the distance from the centre). A zoom saved for them in the
        /// old convention frames the same landscape view at <see cref="LegacyZoomFactor"/> x the
        /// zoom; saved regions convert once on load (FractalRegion.ZoomConvention).</summary>
        public static bool UsedLegacyPitch(FractalType type) => type is
            FractalType.Apollonian or FractalType.BilliardRays or FractalType.ChaoticBilliard
            or FractalType.JulibrotPair or FractalType.IndrasPearls or FractalType.MathPlane
            or FractalType.PrecisionField or FractalType.RandomTile or FractalType.SlitCascade
            or FractalType.TalbotCarpet;

        /// <summary>#1974 — old zoom x this = new zoom with the same landscape framing (3.5 / 4).</summary>
        public const double LegacyZoomFactor = PlaneExtent / 4.0;

        /// <summary>#1836 — log10 zoom past which the centre moves exactly even without an
        /// exact centre: OD's measured clean coordinate floor (~1e120, --qdfloorsweep).</summary>
        public const double ExactNavZoomLog10 = 120;

        private readonly FractalViewState _vs;

        public ViewCamera(FractalViewState vs)
            => _vs = vs ?? throw new ArgumentNullException(nameof(vs));

        /// <summary>World units per pixel for a viewport whose largest dimension
        /// is <paramref name="viewW"/> × <paramref name="viewH"/> pixels.</summary>
        public double Scale(int viewW, int viewH)
            => PlaneExtent / (Math.Max(1, Math.Max(viewW, viewH)) * _vs.Zoom);

        /// <summary>#1836 — log10 of <see cref="Scale"/>, at any zoom.</summary>
        public double ScaleLog10(int viewW, int viewH)
            => Math.Log10(PlaneExtent / Math.Max(1, Math.Max(viewW, viewH))) - _vs.ZoomLog10;

        /// <summary>#1836 — true when centre moves go through the exact centre: the view has
        /// one, or the zoom is past <see cref="ExactNavZoomLog10"/>.</summary>
        public bool ExactNavigation => _vs.CenterExact != null || _vs.ZoomLog10 > ExactNavZoomLog10;

        /// <summary>The world coordinate currently under screen pixel
        /// (<paramref name="px"/>, <paramref name="py"/>).</summary>
        public FFMath.DeepComplex WorldFromScreen(double px, double py, int viewW, int viewH)
        {
            double s = Scale(viewW, viewH);
            double ox = px - viewW * 0.5;
            double oy = py - viewH * 0.5;
            return _vs.GetCenter().Translate(ox * s, oy * s);
        }

        /// <summary>#1836 — the world coordinate under screen pixel (<paramref name="px"/>,
        /// <paramref name="py"/>) exactly, at the view's precision (any zoom).</summary>
        public ExactCentre ExactWorldFromScreen(double px, double py, int viewW, int viewH)
        {
            int prec = NavPrecision();
            return Offset(_vs.GetCenterExact(prec), px - viewW * 0.5, py - viewH * 0.5, viewW, viewH, prec);
        }

        /// <summary>Pan by a pixel delta (drag / key pan). Positive
        /// <paramref name="dxPx"/> moves the view content right, i.e. the centre
        /// moves left — callers pass the sign they want.</summary>
        public void TranslateByPixels(double dxPx, double dyPx, int viewW, int viewH)
        {
            if (ExactNavigation)
            {
                int prec = NavPrecision();
                _vs.SetCenterExact(Offset(_vs.GetCenterExact(prec), dxPx, dyPx, viewW, viewH, prec));
                return;
            }
            double s = Scale(viewW, viewH);
            _vs.SetCenter(_vs.GetCenter().Translate(dxPx * s, dyPx * s));
        }

        /// <summary>Double-click focus / box-zoom recentre: make the world point
        /// under (<paramref name="px"/>, <paramref name="py"/>) the new screen
        /// centre. Does not change zoom.</summary>
        public void SetCenterToScreenPoint(double px, double py, int viewW, int viewH)
        {
            if (ExactNavigation) _vs.SetCenterExact(ExactWorldFromScreen(px, py, viewW, viewH));
            else _vs.SetCenter(WorldFromScreen(px, py, viewW, viewH));
        }

        /// <summary>Zoom by <paramref name="factor"/> while keeping the world
        /// point under (<paramref name="px"/>, <paramref name="py"/>) fixed on
        /// screen (wheel zoom about the cursor). Zoom is clamped to
        /// [<paramref name="zoomMin"/>, <paramref name="zoomMax"/>].</summary>
        public void ZoomAboutScreen(double px, double py, double factor,
                                    int viewW, int viewH, double zoomMin, double zoomMax)
        {
            double ox = px - viewW * 0.5;
            double oy = py - viewH * 0.5;
            if (ExactNavigation)
            {
                var anchorE = ExactWorldFromScreen(px, py, viewW, viewH);   // old zoom
                ApplyZoomFactor(factor, zoomMin, zoomMax);
                _vs.SetCenterExact(Offset(anchorE, -ox, -oy, viewW, viewH, NavPrecision()));
                return;
            }
            var anchor = WorldFromScreen(px, py, viewW, viewH);   // world under cursor, old zoom
            ApplyZoomFactor(factor, zoomMin, zoomMax);
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
            if (ExactNavigation)
            {
                var anchorE = ExactWorldFromScreen(midPx, midPy, viewW, viewH);
                ApplyZoomFactor(factor, zoomMin, zoomMax);
                _vs.SetCenterExact(anchorE);
                return;
            }
            var anchor = WorldFromScreen(midPx, midPy, viewW, viewH);
            ApplyZoomFactor(factor, zoomMin, zoomMax);
            _vs.SetCenter(anchor);
        }

        /// <summary>#1836 — zoom by <paramref name="factor"/> about the screen centre (key
        /// zoom), clamped. The centre (and an exact centre) is unchanged.</summary>
        public void ZoomBy(double factor, double zoomMin, double zoomMax)
            => ApplyZoomFactor(factor, zoomMin, zoomMax);

        // #1836 — multiply the zoom and clamp. While the zoom and the product stay inside
        // double this is the double expression it always was; past double it runs on the log.
        private void ApplyZoomFactor(double factor, double zoomMin, double zoomMax)
        {
            double z = _vs.Zoom * factor;
            if (!_vs.ZoomBeyondDouble && double.IsFinite(z) && z < double.MaxValue)
            {
                _vs.Zoom = Math.Clamp(z, zoomMin, zoomMax);
                return;
            }
            double l = _vs.ZoomLog10 + Math.Log10(factor);
            _vs.SetZoomLog10(Math.Clamp(l, Math.Log10(zoomMin), Math.Log10(zoomMax)));
        }

        // Bits for exact centre moves: the exact centre's own, or what the zoom needs.
        private int NavPrecision()
            => Math.Max(_vs.CenterExact?.Precision ?? 0, BigFloat.PrecisionForZoom(_vs.ZoomLog10));

        // c + (ox, oy) pixels at the current scale, exactly (one rounding per part). The
        // pixel scale comes from the log as m·2^k so it holds past double's range.
        private ExactCentre Offset(ExactCentre c, double ox, double oy, int viewW, int viewH, int prec)
            => c.TranslatePixels(ox, oy, Scale(viewW, viewH), ScaleLog10(viewW, viewH), prec);   // #1838: shared
    }
}
