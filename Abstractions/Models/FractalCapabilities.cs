// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/FractalCapabilities.cs
//
// Motion-class classifier for the video slideshow (multi-type roadmap #91).
// Answers "what does an unattended zoom leg *mean* for this fractal family?"
// so the slideshow can route each region to the right leg motion model
// instead of the historical Mandelbrot-only gate.
//
//   • Zoomable2D  — 2D escape-time / self-similar. Plane point-zoom reveals
//                   deeper detail (Mandelbrot, Julia, Newton, Apollonian, …).
//                   These get real zoom legs in P1 (#91).
//   • Raymarch3D  — distance-estimated raymarch. "Zoom" is a camera fly, not a
//                   plane zoom (Mandelbulb, Mandelbox, KIFS, Quaternion, …).
//                   Camera-fly legs land in P3 (#93).
//   • NonSpatial  — pan/zoom is a no-op or addresses the wrong axis (Plasma,
//                   Flame, DLA, Logistic, Buddhabrot family, IFS/L-System/
//                   attractors). Static-hold / param-sweep legs land in P4 (#94).
//
// IsUserCode flags the three families that execute user-authored code
// (UserEquation, Sandbox, UserBulb). The slideshow pool excludes them even
// when their geometry would otherwise qualify — they round-trip source by
// name/embed and are the RCE-sensitive set gated by Server/Guard/
// FractalTypeAllowlist on any networked path.
//
// NB: the name is FractalMotionCapabilities (not FractalCapabilities) — the
// latter is already the [Flags] per-pixel-data bitmask in Enums.cs.

namespace FracturingFog.Models
{
    /// <summary>
    /// What an unattended video-slideshow zoom leg means for a fractal family.
    /// See <see cref="FractalMotionCapabilities.MotionClass"/>.
    /// </summary>
    public enum FractalMotionClass
    {
        /// <summary>2D escape-time / self-similar — plane point-zoom reveals
        /// deeper detail. Real zoom legs (P1, #91).</summary>
        Zoomable2D,

        /// <summary>Raymarched 3D — "zoom" is a camera fly, not a plane zoom.
        /// Camera-fly legs (P3, #93).</summary>
        Raymarch3D,

        /// <summary>Pan/zoom is a no-op or wrong-axis — static-hold / param-sweep
        /// legs only (P4, #94).</summary>
        NonSpatial,
    }

    /// <summary>
    /// Pure, side-effect-free capability lookups for a <see cref="FractalType"/>.
    /// </summary>
    public static class FractalMotionCapabilities
    {
        /// <summary>
        /// Classifies how a video-slideshow leg should move for this family.
        /// Purely geometric — does not consider the user-code exclusion
        /// (see <see cref="IsUserCode"/>).
        /// </summary>
        public static FractalMotionClass MotionClass(FractalType type) => type switch
        {
            // ── 2D escape-time / self-similar — real plane zoom ──────────────
            FractalType.Mandelbrot => FractalMotionClass.Zoomable2D,
            FractalType.Julia => FractalMotionClass.Zoomable2D,
            FractalType.BurningShip => FractalMotionClass.Zoomable2D,
            FractalType.Tricorn => FractalMotionClass.Zoomable2D,
            FractalType.Multibrot => FractalMotionClass.Zoomable2D,
            FractalType.Phoenix => FractalMotionClass.Zoomable2D,
            FractalType.Newton => FractalMotionClass.Zoomable2D,
            FractalType.Nova => FractalMotionClass.Zoomable2D,
            FractalType.Magnet1 => FractalMotionClass.Zoomable2D,
            FractalType.Magnet2 => FractalMotionClass.Zoomable2D,
            FractalType.Glynn => FractalMotionClass.Zoomable2D,
            FractalType.Halley => FractalMotionClass.Zoomable2D,
            FractalType.Secant => FractalMotionClass.Zoomable2D,
            FractalType.Spider => FractalMotionClass.Zoomable2D,
            FractalType.TearDrop => FractalMotionClass.Zoomable2D,
            FractalType.Apollonian => FractalMotionClass.Zoomable2D,
            FractalType.ChaoticBilliard => FractalMotionClass.Zoomable2D,
            FractalType.PrecisionField => FractalMotionClass.Zoomable2D,
            // Indra's Pearls (#892) — 2D Möbius-group limit set; deeper words
            // auto-reveal on zoom (Apollonian contract).
            FractalType.IndrasPearls => FractalMotionClass.Zoomable2D,
            // Markus–Lyapunov: a genuine self-similar 2D parameter plane —
            // point-zoom into (a, b) reveals structure at all scales (unlike the
            // 1D-stretched Logistic bifurcation, which stays NonSpatial).
            FractalType.Lyapunov => FractalMotionClass.Zoomable2D,
            FractalType.TranscendentalJulia => FractalMotionClass.Zoomable2D,
            FractalType.GeneratedMandelbrotZ2 => FractalMotionClass.Zoomable2D,
            FractalType.GeneratedMandelbrotZ3 => FractalMotionClass.Zoomable2D,
            FractalType.GeneratedMandelbrotZ4 => FractalMotionClass.Zoomable2D,
            FractalType.GeneratedMandelbrotZ5 => FractalMotionClass.Zoomable2D,
            FractalType.GeneratedTricorn => FractalMotionClass.Zoomable2D,
            FractalType.GeneratedBurningShip => FractalMotionClass.Zoomable2D,
            // User-code 2D escape-time — geometry is zoomable, but excluded from
            // the slideshow pool by IsUserCode.
            FractalType.UserEquation => FractalMotionClass.Zoomable2D,
            FractalType.Sandbox => FractalMotionClass.Zoomable2D,

            // ── Raymarched 3D — camera fly, not plane zoom ───────────────────
            FractalType.Mandelbulb => FractalMotionClass.Raymarch3D,
            FractalType.Mandelbox => FractalMotionClass.Raymarch3D,
            FractalType.Kifs => FractalMotionClass.Raymarch3D,
            FractalType.QuaternionJulia => FractalMotionClass.Raymarch3D,
            FractalType.QuaternionMandelbrot => FractalMotionClass.Raymarch3D,
            FractalType.Kleinian => FractalMotionClass.Raymarch3D,
            FractalType.BicomplexMandelbrot => FractalMotionClass.Raymarch3D,
            FractalType.Coquaternion => FractalMotionClass.Raymarch3D,
            FractalType.UserBulb => FractalMotionClass.Raymarch3D,

            // ── Non-spatial — zoom is a no-op or addresses the wrong axis ────
            FractalType.BuddhaBrot => FractalMotionClass.NonSpatial,
            FractalType.Nebulabrot => FractalMotionClass.NonSpatial,
            FractalType.AntiBuddhabrot => FractalMotionClass.NonSpatial,
            FractalType.AntiNebulabrot => FractalMotionClass.NonSpatial,
            FractalType.IFS => FractalMotionClass.NonSpatial,
            FractalType.LSystem => FractalMotionClass.NonSpatial,
            FractalType.StrangeAttractor => FractalMotionClass.NonSpatial,
            FractalType.Logistic => FractalMotionClass.NonSpatial,
            FractalType.Plasma => FractalMotionClass.NonSpatial,
            FractalType.AcidWarp => FractalMotionClass.NonSpatial,
            FractalType.Flame => FractalMotionClass.NonSpatial,
            FractalType.Dla => FractalMotionClass.NonSpatial,
            // Random tiling: the placement IS the image; pan/zoom can't reuse the
            // packing state, so zoom is a no-op (matches DLA). P1 ships zoom-off.
            FractalType.RandomTile => FractalMotionClass.NonSpatial,

            // Unknown/future types default to NonSpatial so a new family never
            // silently lands a broken zoom leg in the slideshow.
            _ => FractalMotionClass.NonSpatial,
        };

        /// <summary>
        /// True for families that execute user-authored code (UserEquation,
        /// Sandbox, UserBulb). Excluded from the video-slideshow pool regardless
        /// of motion class; gated by <c>FractalTypeAllowlist</c> on networked
        /// paths (RCE risk).
        /// </summary>
        public static bool IsUserCode(FractalType type) => type switch
        {
            FractalType.UserEquation => true,
            FractalType.Sandbox => true,
            FractalType.UserBulb => true,
            _ => false,
        };

        /// <summary>
        /// True when a region of this type is eligible for a real zoom leg in the
        /// video slideshow today (P1, #91): 2D-zoomable and not user code.
        /// Raymarch3D (P3) and NonSpatial (P4) return false until their leg
        /// motion models land.
        /// </summary>
        public static bool SupportsVideoZoomLeg(FractalType type)
            => MotionClass(type) == FractalMotionClass.Zoomable2D && !IsUserCode(type);

        /// <summary>
        /// True when a region of this type is eligible for a camera-fly leg in
        /// the video slideshow (P3, #93): raymarched-3D and not user code. The
        /// camera dolly rides the same VideoLoop zoom interpolation the 2D legs
        /// use — every 3D calculator derives its camera distance as
        /// <c>CameraDistance / Zoom</c>, so a log-lerp of Zoom from a wide
        /// establishing value to the authored framing flies the camera in.
        /// UserBulb is raymarched-3D but excluded (user code).
        /// </summary>
        public static bool SupportsVideoCameraLeg(FractalType type)
            => MotionClass(type) == FractalMotionClass.Raymarch3D && !IsUserCode(type);

        /// <summary>
        /// True when a region of this type plays a static-hold leg in the video
        /// slideshow (P4, #94): non-spatial and not user code. Plane zoom is a
        /// no-op or wrong-axis for these, so the leg renders the authored frame
        /// once and holds it (cross-fading in/out like a zoom leg) instead of a
        /// broken zoom.
        /// </summary>
        public static bool SupportsVideoHoldLeg(FractalType type)
            => MotionClass(type) == FractalMotionClass.NonSpatial && !IsUserCode(type);

        /// <summary>
        /// True for the non-spatial families whose static-hold leg can instead
        /// run a smooth, cheap mid-leg param sweep (#806): Logistic (pan the
        /// r-window along the plane X = r axis) and AcidWarp (morph the flow
        /// position). These re-render per frame, so only families whose per-frame
        /// calc is cheap and whose sweep is visually smooth qualify — the slow or
        /// non-smooth generators (Flame, DLA, Plasma, Buddhabrot) stay static-hold
        /// / Ken-Burns. Requires <see cref="SupportsVideoHoldLeg"/>.
        /// </summary>
        public static bool SupportsVideoParamSweep(FractalType type) => type switch
        {
            FractalType.Logistic => true,
            FractalType.AcidWarp => true,
            _ => false,
        };

        /// <summary>
        /// True for the Buddhabrot-family hold legs that can render as a
        /// progressive accumulation (#806): re-present the sample buffer as it
        /// builds over the leg (the image "develops") instead of a single render.
        /// Requires <see cref="SupportsVideoHoldLeg"/>. Shares the
        /// "animate hold legs" opt-in with <see cref="SupportsVideoParamSweep"/>.
        /// </summary>
        public static bool SupportsVideoBuddhaAccumulation(FractalType type) => type switch
        {
            FractalType.BuddhaBrot => true,
            FractalType.Nebulabrot => true,
            FractalType.AntiBuddhabrot => true,
            FractalType.AntiNebulabrot => true,
            _ => false,
        };

        /// <summary>True when a region of this type gets any motion leg in the
        /// video slideshow — a 2D zoom leg (P1), a 3D camera-fly leg (P3), or a
        /// non-spatial static-hold leg (P4). Only user-code families (excluded
        /// for RCE safety) return false.</summary>
        public static bool SupportsVideoLeg(FractalType type)
            => SupportsVideoZoomLeg(type)
            || SupportsVideoCameraLeg(type)
            || SupportsVideoHoldLeg(type);
    }
}
