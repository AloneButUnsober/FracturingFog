// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/QualityPreset.cs
//
// Four rendering quality presets, each defining:
//   • How deep the user can zoom (ZoomMax)
//   • How the iteration count scales with zoom depth
//   • The per-wheel-click zoom step (coarser for exploration, finer for detail)
//   • Whether double-double extended precision is engaged at deep zoom
//
// ┌──────────┬─────────────┬──────────────┬───────────┬────────────────────────────────────────┐
// │ Tier     │ ZoomMax     │ Iter range   │ Wheel     │ Precision                              │
// ├──────────┼─────────────┼──────────────┼───────────┼────────────────────────────────────────┤
// │ Draft    │ 1×10⁵       │  64 –   256  │ ×1.40     │ double (SP) only                       │
// │ Standard │ 1×10¹³      │ 256 –  2048  │ ×1.20     │ SP below 10¹², DD above               │
// │ High     │ 1×10²²      │ 512 – 16384  │ ×1.12     │ SP below 10¹², DD above               │
// │ Ultra    │ 5×10²⁷      │1024 – 65536  │ ×1.08     │ SP below 10¹², DD above               │
// └──────────┴─────────────┴──────────────┴───────────┴────────────────────────────────────────┘
//
// The HP (double-double) threshold of 1e12 is chosen conservatively: a double
// has ~15.9 decimal digits; at zoom 1e12 the pixel size is ~3.5e-15, leaving
// only 1-2 guard digits for rounding.  Switching to DD at that point eliminates
// visible pixel-banding well before the user would notice any artefact.

using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Identifies one of the four rendering quality tiers.
    /// </summary>
    public enum QualityTier
    {
        /// <summary>Fast preview. Shallow zoom, low iteration cap.</summary>
        Draft = 0,
        /// <summary>Balanced quality and speed. Full double-precision zoom depth.</summary>
        Standard = 1,
        /// <summary>Deep zoom with extended precision. Slower at depth.</summary>
        High = 2,
        /// <summary>Deep zoom with double-double precision (~5×10²⁷).</summary>
        Ultra = 3,
        /// <summary>Octuple-double precision — zoom up to ~1×10¹⁰⁰. Slow at extreme depth.</summary>
        Extreme = 4,
    }

    /// <summary>
    /// Immutable descriptor for one quality tier.
    /// </summary>
    public sealed class QualityPreset
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>
        /// Quality Tier enum value corresponding to this preset.  Used for serialization and lookup.
        /// </summary>
        public QualityTier Tier { get; init; }

        /// <summary>
        /// Name of this preset, shown in the UI and used for serialization.  Should be unique across presets.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Descriptive text for this preset, shown in the UI.  Should be concise but informative about the tradeoffs.
        /// </summary>
        public string Description { get; init; } = string.Empty;

        // ── Zoom control ──────────────────────────────────────────────────────

        /// <summary>Minimum allowed zoom. Default 1e-6 lets the user pull the
        /// view well outside the standard Mandelbrot box (span ≈ 3.5 / Zoom),
        /// which is necessary for fractal types whose interesting structure
        /// extends far beyond the [-2, 2] range — User Equation Sandbox,
        /// User Bulb 3D, strange attractors, etc. The classical 0.13 wall
        /// was tuned for the Mandelbrot set only.</summary>
        public double ZoomMin { get; init; } = 1e-6;

        /// <summary>Maximum allowed zoom for this tier.</summary>
        public double ZoomMax { get; init; }

        /// <summary>
        /// Multiplicative zoom change per mouse-wheel detent.
        /// Coarser (1.40) for fast navigation; finer (1.08) for precision at extreme depth.
        /// </summary>
        public double WheelZoomFactor { get; init; } = 1.20;

        // ── Iteration control ─────────────────────────────────────────────────

        /// <summary>Iteration count at zoom = 1 (no zoom).</summary>
        public int IterBase { get; init; }

        /// <summary>Hard cap on iterations regardless of zoom.</summary>
        public int IterMax { get; init; }

        /// <summary>Additional iterations per decade of zoom (log₁₀ scale).</summary>
        public int IterPerDecade { get; init; }

        // ── Precision control ─────────────────────────────────────────────────

        /// <summary>
        /// When true, the calculator switches to double-double arithmetic when
        /// zoom exceeds <see cref="HPZoomThreshold"/>.
        /// </summary>
        public bool AllowHighPrecision { get; init; }

        /// <summary>
        /// Zoom level at which double-double arithmetic is engaged.
        /// Ignored when <see cref="AllowHighPrecision"/> is false.
        /// </summary>
        public double HPZoomThreshold { get; init; } = double.MaxValue;

        // ── Computed helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Returns the recommended iteration count for the given zoom level.
        /// Formula: <c>IterBase + floor(log₁₀(zoom) × IterPerDecade)</c>,
        /// clamped to [IterBase, IterMax].
        /// </summary>
        public int ComputeIterations(double zoom)
        {
            if (zoom <= 1.0) return IterBase;
            int raw = IterBase + (int)(System.Math.Log10(zoom) * IterPerDecade);
            return System.Math.Clamp(raw, IterBase, IterMax);
        }

        /// <summary>Returns true when the given zoom level requires double-double arithmetic.</summary>
        public bool NeedsHighPrecision(double zoom)
            => AllowHighPrecision && zoom > HPZoomThreshold;

        // ── Anti-aliasing (Wave 2.6 / D-5.19) ────────────────────────────────
        //
        // Sub-pixel super-sampling — N² samples averaged into one pixel.
        // 1  = off (default; bit-identical to pre-AA output).
        // 4  = 2×2 jitter grid (High preset).
        // 16 = 4×4 jitter grid (Ultra / Extreme presets).
        //
        // FractalRenderHost reads this value off the active calc's
        // Quality preset and runs N-1 extra Calculate() passes with
        // sub-pixel-shifted centre coords; the per-pixel uint colours
        // are decomposed to RGBA, averaged, and repacked. Cost is
        // roughly linear in AaSamples on the CPU paths; GPU paths
        // currently ignore this and render single-sample.
        public int AaSamples { get; init; } = 1;

        // ── Temporal accumulation (Wave 2.7 / D-5.21) ────────────────────────
        //
        // When the camera is still, the render host re-runs Calculate() with
        // sub-pixel Halton jitter and blends each new sample into a running
        // average. Successive frames smooth out the noise that AaSamples alone
        // can't kill (palette dithering, deep-zoom precision rounding,
        // single-sample edge aliasing). Each TAA sample presents as soon as
        // it's blended, so the user sees the image refine over ~1-2 seconds
        // of stillness; any new Trigger cancels the loop and resets the
        // accumulator.
        //
        // 1   = off (default; bit-identical to pre-TAA output).
        // >1  = cap on total samples accumulated (including MSAA seed).
        //       Capped to keep CPU paths from looping forever; once reached,
        //       the host stops queueing continuation frames.
        public int TaaMaxSamples { get; init; } = 1;

        /// <summary>Short label for the status bar: "SP" (single precision) or "DD" (double-double).</summary>
        public string GetPrecisionLabel(double zoom)
            => NeedsHighPrecision(zoom) ? "DD" : "SP";

        // ── Built-in preset instances ─────────────────────────────────────────

        /// <summary>
        /// Fast preview mode.  Low iteration cap, shallow zoom, coarse wheel step.
        /// Ideal for rapid exploration before committing to a more detailed render.
        /// </summary>
        public static readonly QualityPreset Draft = new()
        {
            Tier = QualityTier.Draft,
            Name = "Draft",
            Description = "Fast preview — shallow zoom (max 10⁵), low iteration cap (256).",
            ZoomMin = 1e-6,
            ZoomMax = 1e5,
            WheelZoomFactor = 1.40,     // large steps: 40% per detent
            IterBase = 64,
            IterMax = 256,
            IterPerDecade = 20,        // +20 iters per decade of zoom
            AllowHighPrecision = false,
        };

        /// <summary>
        /// Balanced quality.  Uses the full depth available to double-precision
        /// arithmetic (~10¹³) with a comfortable iteration range.
        /// </summary>
        public static readonly QualityPreset Standard = new()
        {
            Tier = QualityTier.Standard,
            Name = "Standard",
            Description = "Balanced quality — zoom to 10¹³ (DD above 10¹²), up to 2048 iterations.",
            ZoomMin = 1e-6,
            ZoomMax = 1e13,
            WheelZoomFactor = 1.20,     // 20% per detent
            IterBase = 256,
            IterMax = 2048,
            IterPerDecade = 128,       // +128 iters per decade
            AllowHighPrecision = true,
            HPZoomThreshold = 1e12,
        };

        /// <summary>
        /// Deep zoom with double-double extended precision beyond zoom 10¹².
        /// Allows exploration to 10²² — well into territory invisible to
        /// standard double arithmetic. Notably slower at depth.
        /// </summary>
        public static readonly QualityPreset High = new()
        {
            Tier = QualityTier.High,
            Name = "High",
            Description = "Extended precision (double-double) — zoom to 10²², up to 16384 iterations. 2×2 anti-aliasing + 8-sample TAA. Slower at depth.",
            ZoomMin = 1e-6,
            ZoomMax = 1e22,
            WheelZoomFactor = 1.12,     // 12% per detent — finer control at depth
            IterBase = 512,
            IterMax = 16384,
            IterPerDecade = 256,       // +256 iters per decade
            AllowHighPrecision = true,
            HPZoomThreshold = 1e12,      // engage DD when double starts to degrade
            AaSamples = 4,             // Wave 2.6 — 2×2 MSAA
            TaaMaxSamples = 8,         // Wave 2.7 — extend out to 8 jittered frames
        };

        /// <summary>
        /// Maximum zoom depth supported by double-double arithmetic (~5×10²⁷).
        /// Extreme detail at the deepest levels comes at significant compute cost.
        /// </summary>
        public static readonly QualityPreset Ultra = new()
        {
            Tier = QualityTier.Ultra,
            Name = "Ultra",
            Description = "Maximum detail — double-double zoom to 5×10²⁷, up to 65536 iterations. 4×4 anti-aliasing + 16-sample TAA. Slow at extreme depth.",
            ZoomMin = 1e-6,
            ZoomMax = 5e27,
            WheelZoomFactor = 1.08,     // 8% per detent — very fine control
            IterBase = 1024,
            IterMax = 65536,
            IterPerDecade = 512,       // +512 iters per decade
            AllowHighPrecision = true,
            HPZoomThreshold = 1e12,
            AaSamples = 16,            // Wave 2.6 — 4×4 MSAA
            TaaMaxSamples = 16,        // Wave 2.7
        };

        /// <summary>
        /// Octuple-double precision — zoom up to ~1×10¹⁰⁰. The reference orbit
        /// uses QD math (~62 digits) above 1e25 and OD (~124 digits) above 1e50;
        /// pixel deltas remain double-precision.
        /// Very slow at extreme depth due to QD orbit cost (~5–10× DD).
        /// </summary>
        public static readonly QualityPreset Extreme = new()
        {
            Tier = QualityTier.Extreme,
            Name = "Extreme",
            Description = "Octuple-double precision — zoom to 1×10¹⁰⁰, up to 131072 iterations. 4×4 anti-aliasing + 32-sample TAA. Slow.",
            ZoomMin = 1e-6,
            // Above 1e50 both the reference orbit and per-pixel coordinates use OD
            // (octuple-double, ~124 digits — MandelbrotCalculator.ODZoomThreshold).
            // --qdfloorsweep measures OD coordinate separation at 128/128 through
            // 1e120 (once OD.FromCenterOffset was fixed to place the offset via the
            // full-cascade OD+double add; the old OD+OD add lost it past ~1e64).
            // Cap set at 1e100 — 20 decades under the measured-clean coordinate
            // floor, leaving margin for OD reference-orbit accuracy at depth (the
            // soft limit; beyond deep filaments, chaotic sensitivity dominates
            // "correctness" as it already does on the QD path). Iter budget fits:
            // 2048 + 100*1024 = 104448 < 131072 cap.
            ZoomMax = 1e100,
            WheelZoomFactor = 1.06,     // very fine
            IterBase = 2048,
            IterMax = 131072,
            IterPerDecade = 1024,
            AllowHighPrecision = true,
            HPZoomThreshold = 1e12,
            AaSamples = 16,            // Wave 2.6 — 4×4 MSAA
            TaaMaxSamples = 32,        // Wave 2.7
        };
        // ── Lookup helpers ────────────────────────────────────────────────────

        /// <summary>All presets in tier order.</summary>
        public static readonly QualityPreset[] All = { Draft, Standard, High, Ultra, Extreme };

        /// <summary>Returns the preset for the given tier.</summary>
        public static QualityPreset Get(QualityTier tier) => tier switch
        {
            QualityTier.Draft => Draft,
            QualityTier.Standard => Standard,
            QualityTier.High => High,
            QualityTier.Ultra => Ultra,
            QualityTier.Extreme => Extreme,
            _ => Standard,
        };

        public static QualityPreset FromName(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                foreach (var preset in All)
                {
                    if (string.Equals(preset.Name, value, StringComparison.OrdinalIgnoreCase))
                        return preset;
                }
            }
            return Standard; // default if not found or empty
        }
    }
}
