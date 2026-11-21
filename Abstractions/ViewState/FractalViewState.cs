// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/ViewState/FractalViewState.cs
//
// Pure POCO holding everything the renderer needs to draw one frame of any
// supported fractal type. Migrated out of MainForm.cs as step A of the
// Phase 2.3 cut plan (see PHASE2_AVALONIA_MIGRATION.md). No UI deps, no
// renderer deps — so the input controller (step B) and the renderer host
// (step C) can each consume / mutate this object without dragging WinForms
// or Vortice references into Abstractions.
//
// Quad-precision center
//   The complex-plane centre is stored as four doubles (Hi + 3 low limbs)
//   so the same field set covers plain double, double-double (DD), and
//   quad-double (QD) arithmetic. The active tier comes from the
//   QualityPreset / Zoom pair, not from this struct.
//
// 3D camera state
//   Lives on the existing FractalParameters object (already in Abstractions).
//   This view-state references that object so 3D camera tweaks survive a
//   round-trip through any input/render pipeline.

using System;
using FracturingFog;

namespace FracturingFog.ViewState
{
    /// <summary>
    /// Everything needed to render a single frame of any fractal type.
    /// Mutable; pass by reference and update in place.
    /// </summary>
    public sealed class FractalViewState
    {
        // ── Complex-plane centre, quad-precision limbs ────────────────────────

        /// <summary>Hi limb of CX (always populated).</summary>
        public double CenterX { get; set; } = -0.5;

        /// <summary>Lo₁ limb of CX (used when zoom &gt; HP threshold).</summary>
        public double CenterXLo { get; set; }

        /// <summary>Lo₂ limb of CX (used when zoom &gt; QD threshold).</summary>
        public double CenterX2 { get; set; }

        /// <summary>Lo₃ limb of CX (used when zoom &gt; QD threshold).</summary>
        public double CenterX3 { get; set; }

        /// <summary>Lo₄..Lo₇ limbs of CX (used when zoom &gt; OD threshold).
        /// Wave 2.11 — octuple-double centre supports zoom past 1e50.</summary>
        public double CenterX4 { get; set; }
        public double CenterX5 { get; set; }
        public double CenterX6 { get; set; }
        public double CenterX7 { get; set; }

        /// <summary>Hi limb of CY.</summary>
        public double CenterY { get; set; }

        public double CenterYLo { get; set; }
        public double CenterY2 { get; set; }
        public double CenterY3 { get; set; }
        public double CenterY4 { get; set; }
        public double CenterY5 { get; set; }
        public double CenterY6 { get; set; }
        public double CenterY7 { get; set; }

        // ── Zoom + quality ────────────────────────────────────────────────────

        /// <summary>Scalar zoom factor. 0.13 = default fully-zoomed-out view.</summary>
        public double Zoom { get; set; } = DefaultZoom;

        // ── #1836 — log10 zoom (past double's range) ──────────────────────────
        // `Zoom` stays the render contract (byte-identical up to 1e100). Past double's
        // range (~1.8e308) it saturates at double.MaxValue and the log carries the zoom.
        // Same stamp rule as the exact centre (#1832): the log is the authority only while
        // `Zoom` still holds the value SetZoomLog10 wrote, so any plain `Zoom` write wins.
        private double _zoomLog10 = double.NaN;
        private double _zoomLog10Stamp;

        /// <summary>#1836 — log10 of the zoom at any depth: the log SetZoomLog10 set while
        /// <see cref="Zoom"/> is unchanged since, else log10(<see cref="Zoom"/>).</summary>
        public double ZoomLog10
            => !double.IsNaN(_zoomLog10) && _zoomLog10Stamp.Equals(Zoom) ? _zoomLog10 : Math.Log10(Zoom);

        /// <summary>#1836 — set the zoom from its log10, at any depth (<see cref="Zoom"/>
        /// becomes 10^log, saturated at double.MaxValue).</summary>
        public void SetZoomLog10(double log10)
        {
            if (double.IsNaN(log10)) throw new ArgumentException("log10 zoom is NaN", nameof(log10));
            Zoom = ZoomLog.FromLog10(log10);
            _zoomLog10 = log10;
            _zoomLog10Stamp = Zoom;
        }

        /// <summary>#1836 — true when the zoom is past what <see cref="Zoom"/> can hold.</summary>
        public bool ZoomBeyondDouble => ZoomLog.BeyondDouble(ZoomLog10);

        /// <summary>Active quality preset (Draft / Standard / High / Ultra / Extreme).</summary>
        public Models.QualityPreset Quality { get; set; } = Models.QualityPreset.Standard;

        /// <summary>Zoom threshold above which centre math promotes to QD.</summary>
        public const double QDZoomThreshold = 1e25;

        /// <summary>Zoom threshold above which centre math promotes to OD
        /// (8-limb, ~124 digits). Wave 2.11. Engaged just below QD's wall
        /// at 5×10⁵⁸ so a single zoom step doesn't fall into the precision
        /// floor. Verified by `OctupleDoubleTests.RefOrbit_ModerateZoom_*`.
        /// </summary>
        public const double ODZoomThreshold = 1e50;

        public const double DefaultCenterX = -0.5;
        public const double DefaultCenterY = 0.0;
        public const double DefaultZoom = 0.13;

        // ── High-precision centre accessor ────────────────────────────────────
        // The 16 limb properties above are the storage + serialization + render
        // contract; this pair is the typed view of them used by the interactive
        // control path (ViewCamera). Reading gives all 8+8 limbs; writing sets
        // all of them, so the centre is never partially updated (the historical
        // "wrong tier branch left stale low limbs" bug class cannot occur here).

        /// <summary>The view centre as a single octuple-double complex value.</summary>
        public FFMath.DeepComplex GetCenter()
            => FFMath.DeepComplex.FromLimbs(
                CenterX, CenterXLo, CenterX2, CenterX3, CenterX4, CenterX5, CenterX6, CenterX7,
                CenterY, CenterYLo, CenterY2, CenterY3, CenterY4, CenterY5, CenterY6, CenterY7);

        /// <summary>Write the view centre, populating all 16 limbs from the OD
        /// value (limbs above the current tier are the OD's own zero limbs).</summary>
        public void SetCenter(FFMath.DeepComplex c)
        {
            CenterX = c.Re.X0; CenterXLo = c.Re.X1; CenterX2 = c.Re.X2; CenterX3 = c.Re.X3;
            CenterX4 = c.Re.X4; CenterX5 = c.Re.X5; CenterX6 = c.Re.X6; CenterX7 = c.Re.X7;
            CenterY = c.Im.X0; CenterYLo = c.Im.X1; CenterY2 = c.Im.X2; CenterY3 = c.Im.X3;
            CenterY4 = c.Im.X4; CenterY5 = c.Im.X5; CenterY6 = c.Im.X6; CenterY7 = c.Im.X7;
        }

        // ── #1832 — the exact (arbitrary-precision) centre ────────────────────
        // The 16 limbs stay the render contract up to 1e100 (byte-identical there). Past
        // OD's precision the exact centre is the authority. It is valid only while the
        // limbs are still the ones SetCenterExact wrote, so any limb-only writer (pan, zoom,
        // jitter, code that predates #1832) drops it instead of leaving a stale exact
        // centre behind moved limbs.
        private FFMath.ExactCentre? _exact;
        private FFMath.DeepComplex _exactStamp;

        /// <summary>#1832 — set the centre at any precision: the exact pair, and its OD limbs.</summary>
        public void SetCenterExact(FFMath.ExactCentre c)
        {
            var od = c.ToDeep();
            SetCenter(od);
            _exact = c;
            _exactStamp = od;
        }

        /// <summary>#1832 — the exact centre, or null when none was set or the limbs have
        /// moved since (then the limbs are the centre).</summary>
        public FFMath.ExactCentre? CenterExact
            => _exact is { } e && FFMath.ExactCentre.SameLimbs(_exactStamp, GetCenter()) ? e : null;

        /// <summary>#1832 — the centre at <paramref name="precision"/> bits: the exact centre
        /// when valid, otherwise the limbs (exactly).</summary>
        public FFMath.ExactCentre GetCenterExact(int precision)
            => CenterExact is { } e ? e.WithPrecision(precision) : FFMath.ExactCentre.FromDeep(GetCenter(), precision);

        // ── Fractal type + per-engine parameters ──────────────────────────────

        public FractalType FractalType { get; set; } = FractalType.Mandelbrot;

        /// <summary>Per-engine knobs (Julia c, multibrot power, bulb camera, etc.).</summary>
        public Models.FractalParameters FractalParameters { get; set; } = new();

        // ── Iteration lock ────────────────────────────────────────────────────

        /// <summary>When true, the iteration count is held at <see cref="LockedIterations"/>
        /// regardless of pan / zoom.</summary>
        public bool IterLocked { get; set; }

        /// <summary>Iter count to hold when <see cref="IterLocked"/> is true.</summary>
        public int LockedIterations { get; set; }

        /// <summary>Region-supplied iter override. > 0 = use this value in
        /// place of <see cref="Models.QualityPreset.ComputeIterations"/> when
        /// no lock + no explicit per-call arg overrides. Set by
        /// <c>HostColorThemeService.ApplyRegion</c> from
        /// <c>FractalRegion.Iterations</c>; cleared on any zoom/pan input so
        /// the saved value only governs the first render after a region jump.
        /// Mirrors legacy <c>MainForm.ApplyRegion</c> which wrote
        /// <c>region.Iterations</c> directly into <c>_calculator.MaxIterations</c>
        /// when not iter-locked.</summary>
        public int PreferredIterations { get; set; }

        // ── Post-process ──────────────────────────────────────────────────────

        /// <summary>Brightness offset in [-100, 100]; 0 = neutral.</summary>
        public int Brightness { get; set; }

        /// <summary>Contrast adjustment in [-100, 100]; 0 = neutral.</summary>
        public int Contrast { get; set; }

        /// <summary>Live image gamma in [-100, 100]; 0 = neutral. Maps to an
        /// exponent of 2^(slider/100) (out = in^(1/gamma)), so +100 brightens
        /// (gamma 2), -100 darkens (gamma 0.5). Applied in the post-FX upload
        /// pass; compounds on top of any per-theme <c>PaletteGamma</c>.</summary>
        public int Gamma { get; set; }

        /// <summary>Adaptive contrast (histogram eq) strength in [0, 100]; 0 = off.</summary>
        public int HistogramEq { get; set; }

        /// <summary>S2 (#389) — output-stage view transform / tonemap. Applied as
        /// the final display encode after brightness/contrast/gamma. Default
        /// <see cref="FracturingFog.Imaging.ViewTransform.None"/> = identity, so
        /// the current look is byte-identical until the user opts in.</summary>
        public FracturingFog.Imaging.ViewTransform ViewTransform { get; set; }
            = FracturingFog.Imaging.ViewTransform.None;

        /// <summary>S2 (#389) — exposure in stops applied in linear light before
        /// the view transform. 0 = neutral. Only consulted when
        /// <see cref="ViewTransform"/> is not None.</summary>
        public float ViewExposureEv { get; set; }

        /// <summary>F11: ordered-dither deband of the palette float→byte quantise
        /// (CPU F11a + GPU F11b). Off = the plain truncate/round. Applied during
        /// colorize, so toggling needs a fresh render, not a post-FX repaint.</summary>
        public bool BandDither { get; set; }

        /// <summary>Ordered-dither amplitude in [0, 100]; 100 = full ±0.5-LSB
        /// spread. Only consulted when <see cref="BandDither"/> is on.</summary>
        public int BandDitherStrength { get; set; } = 100;

        /// <summary>F10.5: live per-stop alpha preview. The on-screen path is
        /// opaque (present ignores the alpha channel; the post-FX pass forces
        /// 0xFF), so authored translucent stops are otherwise invisible while
        /// editing a theme. When on, the host composites the render over a
        /// checkerboard using the authored coverage byte so A&lt;255 reads as
        /// see-through. Display-only — saved frames + exports keep straight
        /// alpha. Toggling needs only a post-FX repaint, not a fresh render.</summary>
        public bool AlphaPreview { get; set; }

        // ── Audio view-breathe overlay (P5 #264) ──────────────────────────────
        // Transient render-time overlay, NOT part of the input/navigation state
        // and NOT serialized. ViewCamera + region save read the base Zoom /
        // GetCenter(); the render host multiplies these on top per frame and
        // restores them immediately after configuring the calculators, so a
        // beat-driven zoom wobble or camera shake never drifts the persistent
        // centre. Identity = { 1, 0, 0 }. Written by ViewBreatheAnimator; applied
        // only at shallow zoom (deep-zoom limb math / perturbation reference make
        // a per-frame view wobble pointless and risky there).

        /// <summary>Multiplicative zoom applied on top of <see cref="Zoom"/> for
        /// one frame (1 = no pulse).</summary>
        public double BreatheZoomFactor { get; set; } = 1.0;

        /// <summary>Screen-centre X offset for one frame, as a fraction of the
        /// plane extent at the current zoom (0 = centred).</summary>
        public double BreatheOffsetXFrac { get; set; }

        /// <summary>Screen-centre Y offset for one frame, as a fraction of the
        /// plane extent at the current zoom (0 = centred).</summary>
        public double BreatheOffsetYFrac { get; set; }

        /// <summary>True when any breathe overlay is off its identity value.</summary>
        public bool HasBreathe =>
            BreatheZoomFactor != 1.0 || BreatheOffsetXFrac != 0.0 || BreatheOffsetYFrac != 0.0;

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Reset to the default centre (-0.5, 0) at the default zoom.
        /// Clears all low-limb fields and unlocks iterations. Leaves
        /// <see cref="FractalType"/>, <see cref="Quality"/>, and post-FX alone.
        /// </summary>
        public void ResetView()
        {
            CenterX = DefaultCenterX; CenterXLo = 0; CenterX2 = 0; CenterX3 = 0;
            CenterX4 = 0; CenterX5 = 0; CenterX6 = 0; CenterX7 = 0;
            CenterY = DefaultCenterY; CenterYLo = 0; CenterY2 = 0; CenterY3 = 0;
            CenterY4 = 0; CenterY5 = 0; CenterY6 = 0; CenterY7 = 0;
            Zoom = DefaultZoom;
            IterLocked = false;
            LockedIterations = 0;
            PreferredIterations = 0;
        }

        /// <summary>
        /// Snap the centre + zoom to the canonical default view for the given
        /// fractal type. Mirrors the per-type switch in legacy
        /// <c>MainForm.OnFractalTypeChanged</c> so that picking Burning Ship /
        /// Tricorn / etc. from the toolbar lands on the recognisable
        /// silhouette of the set rather than the inherited Mandelbrot view —
        /// which at the Mandelbrot default would put the entire image
        /// inside the new set and burn every pixel through MAX_ITER,
        /// looking like a lockup.
        ///
        /// Also clears all low-precision limbs and unlocks iterations. Leaves
        /// post-FX (Brightness / Contrast / HistogramEq) alone.
        /// </summary>
        public void SnapToFractalDefault(FractalType t)
        {
            (CenterX, CenterY, Zoom) = t switch
            {
                FractalType.Mandelbrot       => (-0.5,  0.0, 1.0),
                FractalType.Julia            => ( 0.0,  0.0, 1.0),
                FractalType.BurningShip      => (-0.5, -0.5, 1.0),
                FractalType.Tricorn          => ( 0.0,  0.0, 1.0),
                FractalType.Multibrot        => ( 0.0,  0.0, 1.0),
                FractalType.Phoenix          => ( 0.0,  0.0, 1.5),
                FractalType.Newton           => ( 0.0,  0.0, 1.0),
                FractalType.Nova             => (-0.35, 0.0, 1.6),   // #1294 — parameter plane
                FractalType.BuddhaBrot       => (-0.5,  0.0, 1.0),
                FractalType.IFS              => ( 0.0,  0.0, 1.0),
                FractalType.LSystem          => ( 0.0,  0.0, 1.0),
                FractalType.StrangeAttractor => ( 0.0,  0.0, 1.0),
                FractalType.UserEquation     => ( 0.0,  0.0, 1.0),
                FractalType.Mandelbulb       => ( 0.0,  0.0, 1.0),
                FractalType.Sandbox          => ( 0.0,  0.0, 1.0),
                FractalType.UserBulb         => ( 0.0,  0.0, 1.0),
                FractalType.TearDrop         => ( 0.0,  0.0, 0.16),
                FractalType.GeneratedMandelbrotZ2 => (-0.5, 0.0, 1.0),
                FractalType.GeneratedMandelbrotZ3 => ( 0.0, 0.0, 1.0),
                FractalType.GeneratedMandelbrotZ4 => ( 0.0, 0.0, 1.0),
                FractalType.GeneratedMandelbrotZ5 => ( 0.0, 0.0, 1.0),
                FractalType.GeneratedTricorn      => ( 0.0, 0.0, 1.0),
                FractalType.GeneratedBurningShip  => (-0.5,-0.5, 1.0),
                FractalType.Magnet1               => ( 1.5,  0.0, 0.6),
                FractalType.Magnet2               => ( 1.5,  0.0, 0.5),
                FractalType.Glynn                 => (-0.2,  0.0, 0.7),
                FractalType.Logistic              => ( 3.5,  0.5, 2.0),
                FractalType.Lyapunov              => ( 2.9,  3.0, 1.7),
                FractalType.TranscendentalJulia   => ( 0.0,  0.0, 0.5),
                FractalType.Halley                => ( 0.0,  0.0, 1.0),
                FractalType.Secant                => ( 0.0,  0.0, 1.0),
                FractalType.Spider                => ( 0.0,  0.0, 1.2),
                FractalType.Mandelbox             => ( 0.0,  0.0, 1.0),
                FractalType.Kifs                  => ( 0.0,  0.0, 1.0),
                FractalType.QuaternionJulia       => ( 0.0,  0.0, 1.0),
                FractalType.QuaternionMandelbrot  => ( 0.0,  0.0, 1.0),
                FractalType.Plasma                => ( 0.0,  0.0, 1.0),
                FractalType.AcidWarp              => ( 0.0,  0.0, 1.0),
                FractalType.Flame                 => ( 0.0,  0.0, 1.0),
                FractalType.Apollonian            => ( 0.0,  0.0, 1.75),
                FractalType.ChaoticBilliard       => ( 0.0,  0.0, 0.875),
                FractalType.BilliardRays          => ( 0.0,  0.0, 0.525),   // the play circle (r 3) fits
                FractalType.BlackHoleShadow       => ( 0.0,  0.0, 0.14),  // #1923 the default binary + eyebrows fit (#1927 camera pitch)
                FractalType.HenonHeilesBasins     => ( 0.0, -0.25, 0.85), // #1942 the well and its three saddles (plane y = -physical y)
                FractalType.BlackHoleRingOrbits   => (16.0,  0.0, 0.12),  // #1947 r from ~1.5 to ~30 M, u^r ±0.14 at scale 60
                FractalType.MixmasterBasins       => ( 1.5, -0.5, 3.5),   // #1946 u from 1 to 2, v from ~0.2 to ~0.8 (plane y = −v)
                FractalType.SpinningBinaryBasins  => ( 0.0,  0.0, 0.557), // #1945 θ1 from −π to π (radians)
                FractalType.MagnetisedBlackHole   => ( 3.2, -1.5, 0.7),   // #1944 Kick view: ρ0 to ~5.7, ℰ to ~2.9 (plane y = -ℰ)
                FractalType.ThreeBodyBasins       => ( 0.0,  0.0, 1.0),   // #1943 both primaries and L1-L5
                FractalType.SlitCascade           => ( 0.0,  0.0, 0.21875),  // #1569 — u ∈ ±8: the first fringes and the level-1 envelope
                FractalType.TalbotCarpet          => ( 0.0,  0.5, 1.6625),   // #1570 — one Talbot distance tall, about two periods wide (16:9)
                FractalType.MathPlane             => ( 0.0,  0.0, 0.4375),   // x in [-4, 4]
                FractalType.PrecisionField        => (-0.5,  0.0, 0.875),
                FractalType.JulibrotPair       => (-0.5,  0.0, 0.875),
                FractalType.Kleinian              => ( 0.0,  0.0, 1.0),
                FractalType.MirrorSpheres         => ( 0.0,  0.0, 1.0),
                FractalType.MathPlane3D           => ( 0.0,  0.0, 1.0),   // #1713
                FractalType.BicomplexMandelbrot   => ( 0.0,  0.0, 1.0),
                FractalType.Coquaternion          => ( 0.0,  0.0, 1.0),
                FractalType.Julibrot       => ( 0.0,  0.0, 1.0),
                FractalType.Dla                   => ( 0.0,  0.0, 1.0),
                // Indra's Pearls (#892) — Maskit μ = 2i limit set above the real
                // axis; centre (0, 1), wide zoom (matches MiniMapDefaults).
                FractalType.RandomTile            => (-0.5,  0.0, 0.875),  // #1974 (was the fallback zoom 1)
                FractalType.IndrasPearls          => ( 0.0,  1.0, 0.525),
                _                            => (-0.5,  0.0, 1.0),
            };
            CenterXLo = CenterX2 = CenterX3 = 0;
            CenterX4 = CenterX5 = CenterX6 = CenterX7 = 0;
            CenterYLo = CenterY2 = CenterY3 = 0;
            CenterY4 = CenterY5 = CenterY6 = CenterY7 = 0;
            IterLocked = false;
            LockedIterations = 0;
            PreferredIterations = 0;
        }

        /// <summary>True when the active <see cref="Zoom"/> requires OD math
        /// (8-limb, ~124 digits). Wave 2.11.</summary>
        public bool RequiresOD => Zoom > ODZoomThreshold;

        /// <summary>True when the active <see cref="Zoom"/> requires QD math
        /// to keep pan/zoom anchoring stable.</summary>
        public bool RequiresQD => Zoom > QDZoomThreshold && !RequiresOD;

        /// <summary>True when the active <see cref="Zoom"/> requires DD math
        /// (but QD is not yet needed).</summary>
        public bool RequiresDD => !RequiresQD && Quality != null && Quality.NeedsHighPrecision(Zoom);

        /// <summary>True for fractal types that render in 3D camera space
        /// (camera dollies on zoom, right-drag rotates).</summary>
        public bool Is3D => IsThreeD(FractalType);

        /// <summary>Static 3D classifier — single source of truth for which
        /// <see cref="FractalType"/> values render in 3D camera space. Used by
        /// the instance <see cref="Is3D"/> and by UI filters (toolbar Type combo
        /// 2D/3D sort menu) that need the classification without a view state.</summary>
        public static bool IsThreeD(FractalType t) =>
               t == FractalType.Mandelbulb
            || t == FractalType.Mandelbox
            || t == FractalType.Kifs
            || t == FractalType.QuaternionJulia
            || t == FractalType.QuaternionMandelbrot
            || t == FractalType.Kleinian
            || t == FractalType.MirrorSpheres
            || t == FractalType.MathPlane3D        // #1713
            || t == FractalType.BicomplexMandelbrot
            || t == FractalType.Coquaternion
            || t == FractalType.Julibrot
            || t == FractalType.UserBulb;
    }
}
