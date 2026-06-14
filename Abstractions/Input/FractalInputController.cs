// Abstractions/Input/FractalInputController.cs
//
// Concrete implementation of IFractalInputController. Carved out of
// MainForm.cs (OnKeyDown / ProcessCmdKey / OnMouseWheel / OnMouseDown /
// OnMouseMove / OnMouseUp / OnMouseDoubleClick / CenterZoomBy /
// PanByPixels / Adjust3DDistance|Camera|Light* / AdaptQualityForZoom|
// Wheel|NaturalQualityForZoom). Math preserved exactly so behaviour is
// identical to the legacy shell.
//
// Precision tiers
//   Zoom > QDZoomThreshold (1e25)    → QD math (~62 digits)
//   Else AllowHighPrecision + above
//        HPZoomThreshold (1e12)      → DD math (~31 digits)
//   Else                             → plain double
// Pan + double-click + wheel anchors each branch on the right tier so the
// cursor pixel stays under the cursor across the operation.

using System;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.ViewState;

namespace FracturingFog.Input
{
    /// <inheritdoc/>
    public sealed class FractalInputController : IFractalInputController
    {
        // ── Pan state ─────────────────────────────────────────────────────────
        private bool _panning;
        private int _panStartScreenX;
        private int _panStartScreenY;
        private double _panStartCX;
        private double _panStartCY;
        private DD _panStartDDCX;
        private DD _panStartDDCY;
        private QD _panStartQDCX;
        private QD _panStartQDCY;

        // ── 3D right-drag state ───────────────────────────────────────────────
        private bool _rightDragging;
        private int _rightDragStartX;
        private int _rightDragStartY;
        private double _rightDragStartTheta;
        private double _rightDragStartPhi;

        // ── 2D right-drag box-zoom state ──────────────────────────────────────
        private bool _boxSelecting;
        private int _boxStartX;
        private int _boxStartY;
        private int _boxCurX;
        private int _boxCurY;
        private int _boxClientW;
        private int _boxClientH;

        // Drags smaller than this in either dimension are treated as a stray
        // right-click and cancelled (no zoom, no menu suppression elsewhere).
        private const int BoxMinPixels = 8;

        // ── Slideshow guard ───────────────────────────────────────────────────
        /// <summary>Set by the host while a slideshow / video zoom is running
        /// so user input is ignored. The legacy shell checked _slideshowRunning
        /// in every handler; this flag centralises the same behaviour.</summary>
        public bool InputSuppressed { get; set; }

        public FractalInputController(FractalViewState state)
        {
            ViewState = state ?? throw new ArgumentNullException(nameof(state));
        }

        public FractalViewState ViewState { get; }

        public event EventHandler<InputCursorRequest>? CursorRequested;
        public event EventHandler<ViewChangedArgs>? ViewChanged;
        public event EventHandler<InputStatusMessage>? StatusRequested;
        public event EventHandler<SelectionBoxChange?>? SelectionBoxChanged;

        // ── Pointer ───────────────────────────────────────────────────────────

        public void OnPointerDown(PointerInput e)
        {
            if (InputSuppressed) return;

            // Right-click drag in 3D rotates the camera (theta = X, phi = Y).
            if ((e.Buttons & PointerButton.Right) != 0 && ViewState.Is3D)
            {
                _rightDragging = true;
                _rightDragStartX = e.X;
                _rightDragStartY = e.Y;
                _rightDragStartTheta = ViewState.FractalType switch
                {
                    FractalType.UserBulb        => ViewState.FractalParameters.UserBulbCameraTheta,
                    FractalType.Mandelbox       => ViewState.FractalParameters.MandelboxCameraTheta,
                    FractalType.Kifs            => ViewState.FractalParameters.KifsCameraTheta,
                    FractalType.QuaternionJulia => ViewState.FractalParameters.QJuliaCameraTheta,
                    FractalType.QuaternionMandelbrot => ViewState.FractalParameters.QMandelCameraTheta,
                    FractalType.Kleinian        => ViewState.FractalParameters.KleinianCameraTheta,
                    _                           => ViewState.FractalParameters.BulbCameraTheta,
                };
                _rightDragStartPhi = ViewState.FractalType switch
                {
                    FractalType.UserBulb        => ViewState.FractalParameters.UserBulbCameraPhi,
                    FractalType.Mandelbox       => ViewState.FractalParameters.MandelboxCameraPhi,
                    FractalType.Kifs            => ViewState.FractalParameters.KifsCameraPhi,
                    FractalType.QuaternionJulia => ViewState.FractalParameters.QJuliaCameraPhi,
                    FractalType.QuaternionMandelbrot => ViewState.FractalParameters.QMandelCameraPhi,
                    FractalType.Kleinian        => ViewState.FractalParameters.KleinianCameraPhi,
                    _                           => ViewState.FractalParameters.BulbCameraPhi,
                };
                CursorRequested?.Invoke(this, new InputCursorRequest(InputCursor.NoMove2D));
                return;
            }

            // Right-click drag in 2D = rubber-band zoom. Track the rect; the
            // actual zoom is applied on release.
            if ((e.Buttons & PointerButton.Right) != 0 && !ViewState.Is3D)
            {
                _boxSelecting = true;
                _boxStartX = _boxCurX = e.X;
                _boxStartY = _boxCurY = e.Y;
                _boxClientW = Math.Max(1, e.ClientWidth);
                _boxClientH = Math.Max(1, e.ClientHeight);
                CursorRequested?.Invoke(this, new InputCursorRequest(InputCursor.Cross));
                RaiseSelectionBox();
                return;
            }

            if ((e.Buttons & PointerButton.Left) == 0) return;

            _panning = true;
            _panStartScreenX = e.X;
            _panStartScreenY = e.Y;
            _panStartCX = ViewState.CenterX;
            _panStartCY = ViewState.CenterY;
            if (!ViewState.Is3D)
            {
                _panStartDDCX = new DD(ViewState.CenterX, ViewState.CenterXLo);
                _panStartDDCY = new DD(ViewState.CenterY, ViewState.CenterYLo);
                _panStartQDCX = new QD(ViewState.CenterX, ViewState.CenterXLo, ViewState.CenterX2, ViewState.CenterX3);
                _panStartQDCY = new QD(ViewState.CenterY, ViewState.CenterYLo, ViewState.CenterY2, ViewState.CenterY3);
            }
            CursorRequested?.Invoke(this, new InputCursorRequest(InputCursor.SizeAll));
        }

        public void OnPointerMove(PointerInput e)
        {
            if (InputSuppressed) return;

            if (_boxSelecting)
            {
                _boxCurX = e.X;
                _boxCurY = e.Y;
                _boxClientW = Math.Max(1, e.ClientWidth);
                _boxClientH = Math.Max(1, e.ClientHeight);
                RaiseSelectionBox();
                return;
            }

            if (_rightDragging && ViewState.Is3D)
            {
                double w = Math.Max(1, e.ClientWidth);
                double h = Math.Max(1, e.ClientHeight);
                double dTheta = (e.X - _rightDragStartX) / w * Math.PI;
                double dPhi = (e.Y - _rightDragStartY) / h * Math.PI;
                // Invert vertical drag for all 3D fractals — drag down looks
                // up. Matches Docs/User/Keyboard-Shortcuts.md "natural tilt-up
                // feel" applied across Mandelbulb / Mandelbox / UserBulb.
                dPhi = -dPhi;

                const double phiMin = 0.01;
                const double phiMax = Math.PI - 0.01;
                double newTheta = NormalizeAngle(_rightDragStartTheta + dTheta);
                double newPhi = Math.Clamp(_rightDragStartPhi + dPhi, phiMin, phiMax);

                switch (ViewState.FractalType)
                {
                    case FractalType.UserBulb:
                        ViewState.FractalParameters.UserBulbCameraTheta = newTheta;
                        ViewState.FractalParameters.UserBulbCameraPhi = newPhi;
                        break;
                    case FractalType.Mandelbox:
                        ViewState.FractalParameters.MandelboxCameraTheta = newTheta;
                        ViewState.FractalParameters.MandelboxCameraPhi = newPhi;
                        break;
                    case FractalType.Kifs:
                        ViewState.FractalParameters.KifsCameraTheta = newTheta;
                        ViewState.FractalParameters.KifsCameraPhi = newPhi;
                        break;
                    case FractalType.QuaternionJulia:
                        ViewState.FractalParameters.QJuliaCameraTheta = newTheta;
                        ViewState.FractalParameters.QJuliaCameraPhi = newPhi;
                        break;
                    case FractalType.QuaternionMandelbrot:
                        ViewState.FractalParameters.QMandelCameraTheta = newTheta;
                        ViewState.FractalParameters.QMandelCameraPhi = newPhi;
                        break;
                    case FractalType.Kleinian:
                        ViewState.FractalParameters.KleinianCameraTheta = newTheta;
                        ViewState.FractalParameters.KleinianCameraPhi = newPhi;
                        break;
                    default:
                        ViewState.FractalParameters.BulbCameraTheta = newTheta;
                        ViewState.FractalParameters.BulbCameraPhi = newPhi;
                        break;
                }
                RaiseViewChanged(RenderHint.Full);
                return;
            }

            if (!_panning) return;

            if (ViewState.Is3D)
            {
                double s3 = CurrentScale3D(e.ClientWidth, e.ClientHeight);
                ViewState.CenterX = _panStartCX - (e.X - _panStartScreenX) * s3;
                ViewState.CenterY = _panStartCY - (e.Y - _panStartScreenY) * s3;
                ClearLowLimbs();
                RaiseViewChanged(RenderHint.Fast);
                return;
            }

            double scale = CurrentScale(e.ClientWidth, e.ClientHeight);
            if (ViewState.RequiresQD)
            {
                double dx = -(e.X - _panStartScreenX) * scale;
                double dy = -(e.Y - _panStartScreenY) * scale;
                var newCX = _panStartQDCX + dx;
                var newCY = _panStartQDCY + dy;
                StoreQD(newCX, newCY);
            }
            else if (ViewState.RequiresDD)
            {
                double dx = -(e.X - _panStartScreenX) * scale;
                double dy = -(e.Y - _panStartScreenY) * scale;
                var newCX = _panStartDDCX + dx;
                var newCY = _panStartDDCY + dy;
                StoreDD(newCX, newCY);
            }
            else
            {
                ViewState.CenterX = _panStartCX - (e.X - _panStartScreenX) * scale;
                ViewState.CenterY = _panStartCY - (e.Y - _panStartScreenY) * scale;
                ClearLowLimbs();
            }
            RaiseViewChanged(RenderHint.Fast);
        }

        public void OnPointerUp(PointerInput e)
        {
            if ((e.Buttons & PointerButton.Right) != 0 && _boxSelecting)
            {
                _boxSelecting = false;
                _boxCurX = e.X;
                _boxCurY = e.Y;
                int rx = Math.Min(_boxStartX, _boxCurX);
                int ry = Math.Min(_boxStartY, _boxCurY);
                int rw = Math.Abs(_boxCurX - _boxStartX);
                int rh = Math.Abs(_boxCurY - _boxStartY);
                SelectionBoxChanged?.Invoke(this, null);
                CursorRequested?.Invoke(this, new InputCursorRequest(InputCursor.Cross));
                if (rw >= BoxMinPixels && rh >= BoxMinPixels)
                    ApplyBoxZoom(rx, ry, rw, rh, _boxClientW, _boxClientH);
                return;
            }
            if ((e.Buttons & PointerButton.Right) != 0 && _rightDragging)
            {
                _rightDragging = false;
                CursorRequested?.Invoke(this, new InputCursorRequest(InputCursor.Cross));
                return;
            }
            if ((e.Buttons & PointerButton.Left) == 0) return;
            _panning = false;
            CursorRequested?.Invoke(this, new InputCursorRequest(InputCursor.Cross));
        }

        private void RaiseSelectionBox()
        {
            int rx = Math.Min(_boxStartX, _boxCurX);
            int ry = Math.Min(_boxStartY, _boxCurY);
            int rw = Math.Abs(_boxCurX - _boxStartX);
            int rh = Math.Abs(_boxCurY - _boxStartY);
            SelectionBoxChanged?.Invoke(this,
                new SelectionBoxChange(rx, ry, rw, rh, _boxClientW, _boxClientH));
        }

        // Apply a box-zoom: recentre on the rect midpoint and scale zoom so
        // the selected rect fills the view. Uses the same precision-tier
        // anchor pattern as OnWheel — pixel-anchor in current scale, mutate
        // zoom, then re-anchor in the new scale.
        private void ApplyBoxZoom(int rx, int ry, int rw, int rh, int w, int h)
        {
            double midPxX = rx + rw * 0.5;
            double midPxY = ry + rh * 0.5;
            double ox = midPxX - w * 0.5;
            double oy = midPxY - h * 0.5;

            double scale = CurrentScale(w, h);

            // Fit: shrink the smaller of width/height ratios so the whole rect
            // remains visible after zoom (no clipping).
            double factor = Math.Min((double)w / rw, (double)h / rh);

            double targetZoom = Math.Clamp(
                ViewState.Zoom * factor,
                QualityPreset.Draft.ZoomMin,
                QualityPreset.Extreme.ZoomMax);
            if (AdaptQualityForWheel(ViewState.Zoom, targetZoom))
                StatusRequested?.Invoke(this, new InputStatusMessage(
                    $"Quality → {ViewState.Quality.Name} (zoom {targetZoom:G3}).",
                    InputStatusKind.Info));

            // Box zoom uses center-anchor (box midpoint becomes screen
            // center), not cursor-anchor: world coord at box midpoint =
            // currentCenter + (ox, oy) * scale, and newCenter = that anchor.
            // Y axis on screen grows downward but the fractal plane's
            // CenterY is the world Y at screen-center pixel and pan code
            // does CenterY -= dyPixels*scale, so a positive screen-y
            // offset corresponds to a negative world-Y delta from the
            // current centre. Mirror that sign here.
            if (ViewState.RequiresQD)
            {
                var qdCX = new QD(ViewState.CenterX, ViewState.CenterXLo, ViewState.CenterX2, ViewState.CenterX3);
                var qdCY = new QD(ViewState.CenterY, ViewState.CenterYLo, ViewState.CenterY2, ViewState.CenterY3);
                var anchorX = qdCX + ox * scale;
                var anchorY = qdCY + oy * scale;
                ViewState.Zoom = Math.Clamp(ViewState.Zoom * factor, ViewState.Quality.ZoomMin, ViewState.Quality.ZoomMax);
                StoreQD(anchorX, anchorY);
            }
            else if (ViewState.RequiresDD)
            {
                var ddCX = new DD(ViewState.CenterX, ViewState.CenterXLo);
                var ddCY = new DD(ViewState.CenterY, ViewState.CenterYLo);
                var anchorX = ddCX + ox * scale;
                var anchorY = ddCY + oy * scale;
                ViewState.Zoom = Math.Clamp(ViewState.Zoom * factor, ViewState.Quality.ZoomMin, ViewState.Quality.ZoomMax);
                StoreDD(anchorX, anchorY);
            }
            else
            {
                double anchorX = ViewState.CenterX + ox * scale;
                double anchorY = ViewState.CenterY + oy * scale;
                ViewState.Zoom = Math.Clamp(ViewState.Zoom * factor, ViewState.Quality.ZoomMin, ViewState.Quality.ZoomMax);
                ViewState.CenterX = anchorX;
                ViewState.CenterY = anchorY;
                ClearLowLimbs();
            }
            RaiseViewChanged(RenderHint.Full);
        }

        public void OnPointerDoubleClick(PointerInput e)
        {
            if (InputSuppressed) return;
            if ((e.Buttons & PointerButton.Left) == 0) return;

            _panning = false;
            CursorRequested?.Invoke(this, new InputCursorRequest(InputCursor.Cross));

            if (ViewState.Is3D)
            {
                double s3 = CurrentScale3D(e.ClientWidth, e.ClientHeight);
                double ox = e.X - e.ClientWidth * 0.5;
                double oy = e.Y - e.ClientHeight * 0.5;
                ViewState.CenterX += ox * s3;
                ViewState.CenterY += oy * s3;
                ClearLowLimbs();
                RaiseViewChanged(RenderHint.Full);
                return;
            }

            double scale = CurrentScale(e.ClientWidth, e.ClientHeight);
            double dx = e.X - e.ClientWidth * 0.5;
            double dy = e.Y - e.ClientHeight * 0.5;
            if (ViewState.RequiresQD)
            {
                var qdCX = new QD(ViewState.CenterX, ViewState.CenterXLo, ViewState.CenterX2, ViewState.CenterX3) + dx * scale;
                var qdCY = new QD(ViewState.CenterY, ViewState.CenterYLo, ViewState.CenterY2, ViewState.CenterY3) + dy * scale;
                StoreQD(qdCX, qdCY);
            }
            else if (ViewState.RequiresDD)
            {
                var newCX = new DD(ViewState.CenterX, ViewState.CenterXLo) + dx * scale;
                var newCY = new DD(ViewState.CenterY, ViewState.CenterYLo) + dy * scale;
                StoreDD(newCX, newCY);
            }
            else
            {
                ViewState.CenterX += dx * scale;
                ViewState.CenterY += dy * scale;
                ClearLowLimbs();
            }
            RaiseViewChanged(RenderHint.Full);
        }

        public void OnWheel(WheelInput e)
        {
            if (InputSuppressed) return;

            double wf = ViewState.Quality.WheelZoomFactor;
            double factor = e.Delta > 0 ? wf : 1.0 / wf;

            if (ViewState.Is3D)
            {
                ViewState.Zoom = Math.Clamp(ViewState.Zoom * factor,
                    ViewState.Quality.ZoomMin, ViewState.Quality.ZoomMax);
                RaiseViewChanged(RenderHint.Full);
                return;
            }

            double scale = CurrentScale(e.ClientWidth, e.ClientHeight);
            double ox = e.X - e.ClientWidth * 0.5;
            double oy = e.Y - e.ClientHeight * 0.5;

            double targetZoom = Math.Clamp(
                ViewState.Zoom * factor,
                QualityPreset.Draft.ZoomMin,
                QualityPreset.Extreme.ZoomMax);
            if (AdaptQualityForWheel(ViewState.Zoom, targetZoom))
                StatusRequested?.Invoke(this, new InputStatusMessage(
                    $"Quality → {ViewState.Quality.Name} (zoom {targetZoom:G3}).",
                    InputStatusKind.Info));

            if (ViewState.RequiresQD)
            {
                var qdCX = new QD(ViewState.CenterX, ViewState.CenterXLo, ViewState.CenterX2, ViewState.CenterX3);
                var qdCY = new QD(ViewState.CenterY, ViewState.CenterYLo, ViewState.CenterY2, ViewState.CenterY3);
                var anchorX = qdCX + ox * scale;
                var anchorY = qdCY + oy * scale;
                ViewState.Zoom = Math.Clamp(ViewState.Zoom * factor, ViewState.Quality.ZoomMin, ViewState.Quality.ZoomMax);
                double ns = CurrentScale(e.ClientWidth, e.ClientHeight);
                var newCX = anchorX + (-ox * ns);
                var newCY = anchorY + (-oy * ns);
                StoreQD(newCX, newCY);
            }
            else if (ViewState.RequiresDD)
            {
                var ddCX = new DD(ViewState.CenterX, ViewState.CenterXLo);
                var ddCY = new DD(ViewState.CenterY, ViewState.CenterYLo);
                var anchorX = ddCX + ox * scale;
                var anchorY = ddCY + oy * scale;
                ViewState.Zoom = Math.Clamp(ViewState.Zoom * factor, ViewState.Quality.ZoomMin, ViewState.Quality.ZoomMax);
                double ns = CurrentScale(e.ClientWidth, e.ClientHeight);
                var newCX = anchorX - ox * ns;
                var newCY = anchorY - oy * ns;
                StoreDD(newCX, newCY);
            }
            else
            {
                double compX = ViewState.CenterX + ox * scale;
                double compY = ViewState.CenterY + oy * scale;
                ViewState.Zoom = Math.Clamp(ViewState.Zoom * factor, ViewState.Quality.ZoomMin, ViewState.Quality.ZoomMax);
                double ns = CurrentScale(e.ClientWidth, e.ClientHeight);
                ViewState.CenterX = compX - ox * ns;
                ViewState.CenterY = compY - oy * ns;
                ClearLowLimbs();
            }
            RaiseViewChanged(RenderHint.Full);
        }

        // ── Keyboard ──────────────────────────────────────────────────────────

        public bool OnKeyDown(KeyInput e)
        {
            // Diagnostic toggles fire even while a slideshow is running so the
            // user can A/B compare the SA/BLA paths in flight. The host (which
            // owns the calculator) listens via StatusRequested for the toggle
            // notice — the actual flag flip lives there.
            // (FractalInputController has no calculator handle; the host wraps
            //  the controller's events and applies the toggle.)

            // Suppression: pan/zoom keys ignored during a slideshow.
            if (InputSuppressed) return false;

            bool isPanKey = e.Key is InputKey.A or InputKey.D or InputKey.Q or InputKey.E;
            if ((e.Modifiers & InputModifiers.Control) != 0) return false;
            if ((e.Modifiers & InputModifiers.Alt) != 0) return false;
            if ((e.Modifiers & InputModifiers.Shift) != 0 && !isPanKey) return false;

            const double panPreciseFactor = 0.25;

            switch (e.Key)
            {
                case InputKey.R:
                    // Reset is a host concern (also resets brightness etc.) —
                    // the host can subscribe via ViewChanged after detecting
                    // the R key, but the input layer doesn't itself reset.
                    return false;
                case InputKey.Escape:
                    if (_boxSelecting)
                    {
                        _boxSelecting = false;
                        SelectionBoxChanged?.Invoke(this, null);
                        CursorRequested?.Invoke(this, new InputCursorRequest(InputCursor.Cross));
                        return true;
                    }
                    return false;
            }

            if (!ViewState.Is3D)
            {
                const double zoomFactor = 1.25;
                const double panFrac = 0.125;
                double pan2D = (e.Modifiers & InputModifiers.Shift) != 0 ? panFrac * panPreciseFactor : panFrac;
                switch (e.Key)
                {
                    case InputKey.W: CenterZoomBy(zoomFactor, e.ClientWidth, e.ClientHeight); return true;
                    case InputKey.S: CenterZoomBy(1.0 / zoomFactor, e.ClientWidth, e.ClientHeight); return true;
                    case InputKey.A: PanByPixels((int)(e.ClientWidth * pan2D), 0, e.ClientWidth, e.ClientHeight); return true;
                    case InputKey.D: PanByPixels(-(int)(e.ClientWidth * pan2D), 0, e.ClientWidth, e.ClientHeight); return true;
                    case InputKey.Q: PanByPixels(0, (int)(e.ClientHeight * pan2D), e.ClientWidth, e.ClientHeight); return true;
                    case InputKey.E: PanByPixels(0, -(int)(e.ClientHeight * pan2D), e.ClientWidth, e.ClientHeight); return true;
                }
                return false;
            }

            const double distStep = 0.25;
            const double rotStep = Math.PI / 36.0;
            const double pan3DFrac = 0.125;
            double pan3D = (e.Modifiers & InputModifiers.Shift) != 0 ? pan3DFrac * panPreciseFactor : pan3DFrac;
            switch (e.Key)
            {
                case InputKey.W: Adjust3DDistance(-distStep); return true;
                case InputKey.S: Adjust3DDistance(distStep); return true;
                case InputKey.A: PanByPixels((int)(e.ClientWidth * pan3D), 0, e.ClientWidth, e.ClientHeight); return true;
                case InputKey.D: PanByPixels(-(int)(e.ClientWidth * pan3D), 0, e.ClientWidth, e.ClientHeight); return true;
                case InputKey.Q: PanByPixels(0, (int)(e.ClientHeight * pan3D), e.ClientWidth, e.ClientHeight); return true;
                case InputKey.E: PanByPixels(0, -(int)(e.ClientHeight * pan3D), e.ClientWidth, e.ClientHeight); return true;

                case InputKey.Up:    Adjust3DCameraPhi(rotStep); return true;
                case InputKey.Down:  Adjust3DCameraPhi(-rotStep); return true;
                case InputKey.Left:  Adjust3DCameraTheta(-rotStep); return true;
                case InputKey.Right: Adjust3DCameraTheta(rotStep); return true;

                case InputKey.PageUp:   Adjust3DLightTheta(-rotStep); return true;
                case InputKey.PageDown: Adjust3DLightTheta(rotStep); return true;
                case InputKey.Home:     Adjust3DLightPhi(-rotStep); return true;
                case InputKey.End:      Adjust3DLightPhi(rotStep); return true;
            }
            return false;
        }

        // ── Math helpers (preserved from MainForm) ───────────────────────────

        private void CenterZoomBy(double factor, int w, int h)
        {
            double targetZoom = Math.Clamp(
                ViewState.Zoom * factor,
                QualityPreset.Draft.ZoomMin,
                QualityPreset.Extreme.ZoomMax);
            if (AdaptQualityForWheel(ViewState.Zoom, targetZoom))
                StatusRequested?.Invoke(this, new InputStatusMessage(
                    $"Quality → {ViewState.Quality.Name} (zoom {targetZoom:G3}).",
                    InputStatusKind.Info));

            ViewState.Zoom = Math.Clamp(ViewState.Zoom * factor,
                ViewState.Quality.ZoomMin, ViewState.Quality.ZoomMax);
            RaiseViewChanged(RenderHint.Full);
        }

        private void PanByPixels(int dx, int dy, int w, int h)
        {
            double scale = CurrentScale(w, h);
            if (ViewState.RequiresQD)
            {
                var qdCX = new QD(ViewState.CenterX, ViewState.CenterXLo, ViewState.CenterX2, ViewState.CenterX3) + dx * scale;
                var qdCY = new QD(ViewState.CenterY, ViewState.CenterYLo, ViewState.CenterY2, ViewState.CenterY3) + dy * scale;
                StoreQD(qdCX, qdCY);
            }
            else if (ViewState.RequiresDD)
            {
                var newCX = new DD(ViewState.CenterX, ViewState.CenterXLo) + dx * scale;
                var newCY = new DD(ViewState.CenterY, ViewState.CenterYLo) + dy * scale;
                StoreDD(newCX, newCY);
            }
            else
            {
                ViewState.CenterX += dx * scale;
                ViewState.CenterY += dy * scale;
                ClearLowLimbs();
            }
            RaiseViewChanged(RenderHint.Full);
        }

        private void Adjust3DDistance(double delta)
        {
            // Upper bound 500 (was 50): user-defined bulb equations sometimes
            // produce sets that extend well beyond the unit sphere, and the
            // standard 50-unit cap clipped the camera before the whole figure
            // came into view. Lower bound stays at 0.1 to avoid degenerate
            // ray directions when the camera sits on the surface.
            if (ViewState.FractalType == FractalType.UserBulb)
                ViewState.FractalParameters.UserBulbCameraDistance = Math.Clamp(
                    ViewState.FractalParameters.UserBulbCameraDistance + delta, 0.1, 500.0);
            else if (ViewState.FractalType == FractalType.Mandelbulb)
                ViewState.FractalParameters.BulbCameraDistance = Math.Clamp(
                    ViewState.FractalParameters.BulbCameraDistance + delta, 0.1, 500.0);
            else if (ViewState.FractalType == FractalType.Mandelbox)
                ViewState.FractalParameters.MandelboxCameraDistance = Math.Clamp(
                    ViewState.FractalParameters.MandelboxCameraDistance + delta, 0.1, 500.0);
            else if (ViewState.FractalType == FractalType.Kifs)
                ViewState.FractalParameters.KifsCameraDistance = Math.Clamp(
                    ViewState.FractalParameters.KifsCameraDistance + delta, 0.1, 500.0);
            else if (ViewState.FractalType == FractalType.QuaternionJulia)
                ViewState.FractalParameters.QJuliaCameraDistance = Math.Clamp(
                    ViewState.FractalParameters.QJuliaCameraDistance + delta, 0.1, 500.0);
            else if (ViewState.FractalType == FractalType.QuaternionMandelbrot)
                ViewState.FractalParameters.QMandelCameraDistance = Math.Clamp(
                    ViewState.FractalParameters.QMandelCameraDistance + delta, 0.1, 500.0);
            else if (ViewState.FractalType == FractalType.Kleinian)
                ViewState.FractalParameters.KleinianCameraDistance = Math.Clamp(
                    ViewState.FractalParameters.KleinianCameraDistance + delta, 0.1, 500.0);
            else return;
            RaiseViewChanged(RenderHint.Full);
        }

        private void Adjust3DCameraTheta(double delta)
        {
            if (ViewState.FractalType == FractalType.UserBulb)
                ViewState.FractalParameters.UserBulbCameraTheta = NormalizeAngle(ViewState.FractalParameters.UserBulbCameraTheta + delta);
            else if (ViewState.FractalType == FractalType.Mandelbulb)
                ViewState.FractalParameters.BulbCameraTheta = NormalizeAngle(ViewState.FractalParameters.BulbCameraTheta + delta);
            else if (ViewState.FractalType == FractalType.Mandelbox)
                ViewState.FractalParameters.MandelboxCameraTheta = NormalizeAngle(ViewState.FractalParameters.MandelboxCameraTheta + delta);
            else if (ViewState.FractalType == FractalType.Kifs)
                ViewState.FractalParameters.KifsCameraTheta = NormalizeAngle(ViewState.FractalParameters.KifsCameraTheta + delta);
            else if (ViewState.FractalType == FractalType.QuaternionJulia)
                ViewState.FractalParameters.QJuliaCameraTheta = NormalizeAngle(ViewState.FractalParameters.QJuliaCameraTheta + delta);
            else if (ViewState.FractalType == FractalType.QuaternionMandelbrot)
                ViewState.FractalParameters.QMandelCameraTheta = NormalizeAngle(ViewState.FractalParameters.QMandelCameraTheta + delta);
            else if (ViewState.FractalType == FractalType.Kleinian)
                ViewState.FractalParameters.KleinianCameraTheta = NormalizeAngle(ViewState.FractalParameters.KleinianCameraTheta + delta);
            else return;
            RaiseViewChanged(RenderHint.Full);
        }

        private void Adjust3DCameraPhi(double delta)
        {
            const double phiMin = 0.01;
            const double phiMax = Math.PI - 0.01;
            if (ViewState.FractalType == FractalType.UserBulb)
                ViewState.FractalParameters.UserBulbCameraPhi = Math.Clamp(
                    ViewState.FractalParameters.UserBulbCameraPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.Mandelbulb)
                ViewState.FractalParameters.BulbCameraPhi = Math.Clamp(
                    ViewState.FractalParameters.BulbCameraPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.Mandelbox)
                ViewState.FractalParameters.MandelboxCameraPhi = Math.Clamp(
                    ViewState.FractalParameters.MandelboxCameraPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.Kifs)
                ViewState.FractalParameters.KifsCameraPhi = Math.Clamp(
                    ViewState.FractalParameters.KifsCameraPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.QuaternionJulia)
                ViewState.FractalParameters.QJuliaCameraPhi = Math.Clamp(
                    ViewState.FractalParameters.QJuliaCameraPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.QuaternionMandelbrot)
                ViewState.FractalParameters.QMandelCameraPhi = Math.Clamp(
                    ViewState.FractalParameters.QMandelCameraPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.Kleinian)
                ViewState.FractalParameters.KleinianCameraPhi = Math.Clamp(
                    ViewState.FractalParameters.KleinianCameraPhi + delta, phiMin, phiMax);
            else return;
            RaiseViewChanged(RenderHint.Full);
        }

        private void Adjust3DLightTheta(double delta)
        {
            if (ViewState.FractalType == FractalType.UserBulb)
                ViewState.FractalParameters.UserBulbLightTheta = NormalizeAngle(ViewState.FractalParameters.UserBulbLightTheta + delta);
            else if (ViewState.FractalType == FractalType.Mandelbulb)
                ViewState.FractalParameters.BulbLightTheta = NormalizeAngle(ViewState.FractalParameters.BulbLightTheta + delta);
            else if (ViewState.FractalType == FractalType.Mandelbox)
                ViewState.FractalParameters.MandelboxLightTheta = NormalizeAngle(ViewState.FractalParameters.MandelboxLightTheta + delta);
            else if (ViewState.FractalType == FractalType.Kifs)
                ViewState.FractalParameters.KifsLightTheta = NormalizeAngle(ViewState.FractalParameters.KifsLightTheta + delta);
            else if (ViewState.FractalType == FractalType.QuaternionJulia)
                ViewState.FractalParameters.QJuliaLightTheta = NormalizeAngle(ViewState.FractalParameters.QJuliaLightTheta + delta);
            else if (ViewState.FractalType == FractalType.QuaternionMandelbrot)
                ViewState.FractalParameters.QMandelLightTheta = NormalizeAngle(ViewState.FractalParameters.QMandelLightTheta + delta);
            else if (ViewState.FractalType == FractalType.Kleinian)
                ViewState.FractalParameters.KleinianLightTheta = NormalizeAngle(ViewState.FractalParameters.KleinianLightTheta + delta);
            else return;
            RaiseViewChanged(RenderHint.Full);
        }

        private void Adjust3DLightPhi(double delta)
        {
            const double phiMin = 0.01;
            const double phiMax = Math.PI - 0.01;
            if (ViewState.FractalType == FractalType.UserBulb)
                ViewState.FractalParameters.UserBulbLightPhi = Math.Clamp(
                    ViewState.FractalParameters.UserBulbLightPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.Mandelbulb)
                ViewState.FractalParameters.BulbLightPhi = Math.Clamp(
                    ViewState.FractalParameters.BulbLightPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.Mandelbox)
                ViewState.FractalParameters.MandelboxLightPhi = Math.Clamp(
                    ViewState.FractalParameters.MandelboxLightPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.Kifs)
                ViewState.FractalParameters.KifsLightPhi = Math.Clamp(
                    ViewState.FractalParameters.KifsLightPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.QuaternionJulia)
                ViewState.FractalParameters.QJuliaLightPhi = Math.Clamp(
                    ViewState.FractalParameters.QJuliaLightPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.QuaternionMandelbrot)
                ViewState.FractalParameters.QMandelLightPhi = Math.Clamp(
                    ViewState.FractalParameters.QMandelLightPhi + delta, phiMin, phiMax);
            else if (ViewState.FractalType == FractalType.Kleinian)
                ViewState.FractalParameters.KleinianLightPhi = Math.Clamp(
                    ViewState.FractalParameters.KleinianLightPhi + delta, phiMin, phiMax);
            else return;
            RaiseViewChanged(RenderHint.Full);
        }

        private static double NormalizeAngle(double a)
        {
            const double twoPi = Math.PI * 2.0;
            a %= twoPi;
            if (a < 0) a += twoPi;
            return a;
        }

        private bool AdaptQualityForWheel(double oldZoom, double newZoom)
        {
            if (newZoom > ViewState.Quality.ZoomMax)
                return AdaptQualityForZoom(newZoom);

            QualityPreset natOld = NaturalQualityForZoom(oldZoom);
            QualityPreset natNew = NaturalQualityForZoom(newZoom);
            if (natOld.Tier == natNew.Tier) return false;
            if (ViewState.Quality.Tier != natOld.Tier) return false;
            return AdaptQualityForZoom(newZoom);
        }

        private bool AdaptQualityForZoom(double targetZoom)
        {
            QualityPreset fit = NaturalQualityForZoom(targetZoom);
            if (fit.Tier == ViewState.Quality.Tier) return false;
            ViewState.Quality = fit;
            return true;
        }

        private static QualityPreset NaturalQualityForZoom(double z)
        {
            foreach (var p in QualityPreset.All)
                if (p.ZoomMax >= z) return p;
            return QualityPreset.Extreme;
        }

        // FOV-scale must match MandelbulbCalculator / UserBulbCalculator: tan(π/6).
        private const double Bulb3DFovScale = 0.57735026918962576;

        private double CurrentScale(int width, int height)
        {
            // Matches MainForm.CurrentScale(): complex-plane units per pixel.
            int dim = Math.Max(1, Math.Max(width, height));
            return 3.5 / (dim * ViewState.Zoom);
        }

        private double CurrentScale3D(int width, int height)
        {
            // Matches MainForm.CurrentScale3D(): NDC units per screen pixel.
            int h = Math.Max(1, height);
            return 2.0 * Bulb3DFovScale / h;
        }

        private void ClearLowLimbs()
        {
            ViewState.CenterXLo = 0; ViewState.CenterX2 = 0; ViewState.CenterX3 = 0;
            ViewState.CenterYLo = 0; ViewState.CenterY2 = 0; ViewState.CenterY3 = 0;
        }

        private void StoreDD(DD cx, DD cy)
        {
            ViewState.CenterX = cx.Hi; ViewState.CenterXLo = cx.Lo; ViewState.CenterX2 = 0; ViewState.CenterX3 = 0;
            ViewState.CenterY = cy.Hi; ViewState.CenterYLo = cy.Lo; ViewState.CenterY2 = 0; ViewState.CenterY3 = 0;
        }

        private void StoreQD(QD cx, QD cy)
        {
            ViewState.CenterX = cx.X0; ViewState.CenterXLo = cx.X1; ViewState.CenterX2 = cx.X2; ViewState.CenterX3 = cx.X3;
            ViewState.CenterY = cy.X0; ViewState.CenterYLo = cy.X1; ViewState.CenterY2 = cy.X2; ViewState.CenterY3 = cy.X3;
        }

        private void RaiseViewChanged(RenderHint hint)
            => ViewChanged?.Invoke(this, new ViewChangedArgs(hint));
    }
}
