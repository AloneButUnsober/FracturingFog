// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/AsciiFxSettings.cs
//
// Settings for the ASCII-native FX chain (#229) — post effects applied to the
// character grid AFTER downsample/ramp, before paint or file emit. Pure CPU, no
// fractal recompute. Animated effects read TimeSeconds (wall clock for the live
// view, or SceneTime when baked into an animation export).

namespace FracturingFog.Imaging
{
    /// <summary>Knobs for <see cref="AsciiFxChain"/>. All effects off by default
    /// (identity pass).</summary>
    public sealed class AsciiFxSettings
    {
        /// <summary>Animation clock in seconds. Drives the time-varying effects
        /// (<see cref="HueCycle"/>, <see cref="Breathe"/>). Static effects
        /// (<see cref="Crt"/>) ignore it.</summary>
        public double TimeSeconds { get; set; }

        /// <summary>Rotate every cell's colour hue over time — palette cycling.</summary>
        public bool HueCycle { get; set; }
        /// <summary>Hue rotation rate, degrees per second.</summary>
        public double HueCycleDegPerSec { get; set; } = 40.0;

        /// <summary>CRT scanline: dim alternate rows (static).</summary>
        public bool Crt { get; set; }
        /// <summary>Brightness multiplier applied to dimmed scanline rows, [0,1].</summary>
        public double CrtScanlineDim { get; set; } = 0.55;

        /// <summary>"Breathe" the glyph density: animate a gamma on the ramp index
        /// so the whole field pulses lighter/darker over time (independent of the
        /// fractal). Operates on the chosen glyph via the ramp, so no re-sample.</summary>
        public bool Breathe { get; set; }
        /// <summary>Breathe gamma midpoint, and half-amplitude of the sine swing.</summary>
        public double BreatheGammaMid { get; set; } = 1.0;
        public double BreatheGammaAmp { get; set; } = 0.55;
        /// <summary>Breathe cycles per second.</summary>
        public double BreatheHz { get; set; } = 0.35;

        // ── Glyph-space ───────────────────────────────────────────────────

        /// <summary>Re-map every glyph onto a different character set of the same
        /// tonal ordering (blocks / dots / pure-ASCII / custom), preserving each
        /// cell's density. Static.</summary>
        public bool CharsetSwap { get; set; }
        /// <summary>Replacement ramp for <see cref="CharsetSwap"/> (dark→light).
        /// A cell's position along the source ramp is carried to the same
        /// position along this one.</summary>
        public string SwapRamp { get; set; } = "░▒▓█"; // ░▒▓█

        /// <summary>True when any effect is enabled (the host can skip the pass).</summary>
        public bool AnyEnabled => HueCycle || Crt || Breathe || CharsetSwap;

        /// <summary>True when an enabled effect varies with time (the live view
        /// must repaint on a timer for these, not only on buffer changes).</summary>
        public bool AnyAnimated => HueCycle || Breathe;
    }
}
