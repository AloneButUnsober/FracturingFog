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

        /// <summary>Adaptive contrast (histogram eq) strength in [0, 100]; 0 = off.</summary>
        public int HistogramEq { get; set; }

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
                FractalType.Nova             => ( 1.0,  0.0, 0.8),
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
                FractalType.Halley                => ( 0.0,  0.0, 1.0),
                FractalType.Secant                => ( 0.0,  0.0, 1.0),
                FractalType.Spider                => ( 0.0,  0.0, 1.2),
                FractalType.Mandelbox             => ( 0.0,  0.0, 1.0),
                FractalType.Kifs                  => ( 0.0,  0.0, 1.0),
                FractalType.QuaternionJulia       => ( 0.0,  0.0, 1.0),
                FractalType.QuaternionMandelbrot  => ( 0.0,  0.0, 1.0),
                FractalType.Plasma                => ( 0.0,  0.0, 1.0),
                FractalType.Flame                 => ( 0.0,  0.0, 1.0),
                FractalType.Apollonian            => ( 0.0,  0.0, 2.0),
                FractalType.Kleinian              => ( 0.0,  0.0, 1.0),
                FractalType.BicomplexMandelbrot   => ( 0.0,  0.0, 1.0),
                FractalType.Dla                   => ( 0.0,  0.0, 1.0),
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
            || t == FractalType.BicomplexMandelbrot
            || t == FractalType.UserBulb;
    }
}
