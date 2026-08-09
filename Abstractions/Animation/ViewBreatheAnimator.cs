// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using FracturingFog.Audio;
using FracturingFog.ViewState;

namespace FracturingFog.Abstractions.Animation
{
    /// <summary>
    /// #264 / Audio-Reactive Phase 5 — view-level "breathing". Drives a transient
    /// zoom-pulse and optional camera shake on a <see cref="FractalViewState"/>
    /// from live audio, writing the render-time overlay fields
    /// (<see cref="FractalViewState.BreatheZoomFactor"/> /
    /// <see cref="FractalViewState.BreatheOffsetXFrac"/> /
    /// <see cref="FractalViewState.BreatheOffsetYFrac"/>) the render host applies
    /// and restores per frame. Because it never touches the base
    /// <see cref="FractalViewState.Zoom"/> / centre, the input path (ViewCamera)
    /// and region save keep the exact user navigation — the wobble is purely a
    /// look, not a drift.
    /// <para>
    /// An <see cref="IParameterAnimator"/> so it rides the same render-gated
    /// <c>ParameterAnimationBus</c> as every other track (Cheap — the overlay is a
    /// couple of writes; the render itself is the cost, gated by the bus). When the
    /// source is inactive, or the view is zoomed past <see cref="MaxZoom"/>, the
    /// overlay is reset to identity so the frame renders at the base view — a
    /// beat-driven zoom wobble at 1e40 is meaningless against the perturbation
    /// reference and would only add limb churn.
    /// </para>
    /// </summary>
    public sealed class ViewBreatheAnimator : IParameterAnimator
    {
        private readonly IAudioModulationSource _source;
        private readonly FractalViewState _view;

        // Zoom pulse reuses the tested AudioModulationBinding shaping/curve path;
        // its output range is pinned to [1, 1+depth] so Evaluate yields a factor.
        private readonly AudioModulationBinding _zoom = new()
        {
            Source = AudioSignalKind.Bass,
            Curve = AudioResponseCurve.Smoothstep,
            OutMin = 1.0,
            OutMax = 1.06,
        };

        private double _zoomDepth = 0.06;
        private double _shakeAmount = 0.01;
        private double _shakePhase;

        public ViewBreatheAnimator(IAudioModulationSource source, FractalViewState view)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _view = view ?? throw new ArgumentNullException(nameof(view));
        }

        public string Name => "ViewBreathe";
        public bool IsEnabled { get; set; }
        public AnimatableParamCost Cost => AnimatableParamCost.Cheap;

        // ── Zoom pulse ────────────────────────────────────────────────────────
        public bool ZoomPulseEnabled { get; set; } = true;
        public AudioSignalKind ZoomSignal { get => _zoom.Source; set => _zoom.Source = value; }
        public AudioResponseCurve ZoomCurve { get => _zoom.Curve; set => _zoom.Curve = value; }

        /// <summary>Peak extra zoom as a fraction of the base (0.06 = ±6% breathe).
        /// Clamped to [0, 0.5]; keeps the output binding's range in sync.</summary>
        public double ZoomDepth
        {
            get => _zoomDepth;
            set { _zoomDepth = Clamp(value, 0.0, 0.5); _zoom.OutMin = 1.0; _zoom.OutMax = 1.0 + _zoomDepth; }
        }

        // ── Camera shake ──────────────────────────────────────────────────────
        public bool ShakeEnabled { get; set; }
        public AudioSignalKind ShakeSignal { get; set; } = AudioSignalKind.High;

        /// <summary>Peak shake displacement as a fraction of the view extent.
        /// Clamped to [0, 0.2].</summary>
        public double ShakeAmount
        {
            get => _shakeAmount;
            set => _shakeAmount = Clamp(value, 0.0, 0.2);
        }

        /// <summary>Shake oscillation rate (Hz). The jitter direction is a
        /// dt-integrated phase — deterministic given the tick cadence, so an
        /// offline export replays identically.</summary>
        public double ShakeHz { get; set; } = 24.0;

        /// <summary>Zoom above which breathing is suppressed (identity overlay).</summary>
        public double MaxZoom { get; set; } = 1e6;

        public void Tick(double dt)
        {
            if (!_source.IsActive || _view.Zoom > MaxZoom) { ResetOverlay(); return; }

            var f = _source.Sample();

            _view.BreatheZoomFactor = ZoomPulseEnabled ? _zoom.Evaluate(f) : 1.0;

            if (ShakeEnabled)
            {
                double s = Clamp(f.Signal(ShakeSignal), 0.0, 1.0);
                _shakePhase += dt * ShakeHz * (System.Math.PI * 2.0);
                double mag = _shakeAmount * s;
                _view.BreatheOffsetXFrac = mag * System.Math.Sin(_shakePhase);
                _view.BreatheOffsetYFrac = mag * System.Math.Cos(_shakePhase * 1.3);
            }
            else
            {
                _view.BreatheOffsetXFrac = 0.0;
                _view.BreatheOffsetYFrac = 0.0;
            }
        }

        /// <summary>Snap the overlay back to identity. Called on inactive source /
        /// deep zoom; the owner should also call it (and trigger one render) when
        /// disabling the animator so the view returns to the base immediately.</summary>
        public void ResetOverlay()
        {
            _view.BreatheZoomFactor = 1.0;
            _view.BreatheOffsetXFrac = 0.0;
            _view.BreatheOffsetYFrac = 0.0;
        }

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : (v > hi ? hi : v);
    }
}
