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

        /// <summary>Cyclically scroll each glyph through the ramp over time, so the
        /// whole field shimmers. Animated.</summary>
        public bool RampScroll { get; set; }
        /// <summary>Ramp steps per second for <see cref="RampScroll"/>.</summary>
        public double RampScrollSpeed { get; set; } = 4.0;

        // ── Colour-space ──────────────────────────────────────────────────

        /// <summary>Collapse every cell to a single tint hue, keeping its
        /// brightness (luma) — the classic amber / green-phosphor terminal look.
        /// Static.</summary>
        public bool Monochrome { get; set; }
        /// <summary>Tint colour for <see cref="Monochrome"/> at full brightness.
        /// Default is phosphor green.</summary>
        public byte MonochromeR { get; set; } = 40;
        public byte MonochromeG { get; set; } = 255;
        public byte MonochromeB { get; set; } = 90;

        /// <summary>Scale colour saturation. <see cref="SaturateAmp"/> &gt; 0 makes
        /// it pulse over time (animated); otherwise a static boost/desaturate at
        /// <see cref="SaturateMid"/>. 1 = unchanged, 0 = greyscale, &gt;1 = vivid.</summary>
        public bool Saturate { get; set; }
        public double SaturateMid { get; set; } = 1.0;
        public double SaturateAmp { get; set; } = 0.0;
        public double SaturateHz { get; set; } = 0.3;

        /// <summary>Invert every colour channel (photographic negative). Static.</summary>
        public bool Invert { get; set; }
        /// <summary>Solarize: invert only channels brighter than
        /// <see cref="SolarizeThreshold"/> — the Sabattier tone-reversal look.</summary>
        public bool Solarize { get; set; }
        /// <summary>Solarize crossover, [0,1] of full brightness.</summary>
        public double SolarizeThreshold { get; set; } = 0.5;

        /// <summary>Posterize: snap each channel to <see cref="QuantizeLevels"/>
        /// steps, or to the 16-colour ANSI palette when
        /// <see cref="QuantizeTerminal16"/> is set. Static.</summary>
        public bool Quantize { get; set; }
        /// <summary>Per-channel levels for posterize (≥2).</summary>
        public int QuantizeLevels { get; set; } = 4;
        /// <summary>Snap to the classic 16-colour terminal palette instead of an
        /// even per-channel posterize.</summary>
        public bool QuantizeTerminal16 { get; set; }

        /// <summary>Duotone / gradient wash: remap each cell's brightness (luma)
        /// onto a shadow→highlight colour gradient, discarding the source chroma.
        /// Static.</summary>
        public bool Duotone { get; set; }
        public byte DuotoneLoR { get; set; } = 10;
        public byte DuotoneLoG { get; set; } = 20;
        public byte DuotoneLoB { get; set; } = 60;   // deep blue shadow
        public byte DuotoneHiR { get; set; } = 255;
        public byte DuotoneHiG { get; set; } = 200;
        public byte DuotoneHiB { get; set; } = 80;    // warm highlight

        // ── Structural (stateful) ─────────────────────────────────────────

        /// <summary>"Matrix" digital rain: falling columns of glyphs, the fractal
        /// showing through as a ghost mask. Animated; needs an
        /// <see cref="AsciiFxState"/>.</summary>
        public bool MatrixRain { get; set; }
        /// <summary>Base fall speed in rows per second.</summary>
        public double MatrixRainSpeed { get; set; } = 14.0;
        /// <summary>Fraction of columns that carry a drop, [0,1].</summary>
        public double MatrixRainDensity { get; set; } = 0.85;
        /// <summary>How strongly the underlying fractal brightness masks the rain
        /// (0 = uniform rain, 1 = rain only where the fractal is bright).</summary>
        public double MatrixRainMask { get; set; } = 0.6;

        /// <summary>True when any effect is enabled (the host can skip the pass).</summary>
        public bool AnyEnabled => HueCycle || Crt || Breathe || CharsetSwap || Monochrome
            || Saturate || Invert || Solarize || Quantize || Duotone || RampScroll || MatrixRain;

        /// <summary>True when an enabled effect varies with time (the live view
        /// must repaint on a timer for these, not only on buffer changes).</summary>
        public bool AnyAnimated => HueCycle || Breathe || MatrixRain || RampScroll
            || (Saturate && SaturateAmp > 0);

        /// <summary>True when an enabled effect evolves across frames and so needs
        /// a persistent <see cref="AsciiFxState"/> (not a pure grid+clock function).</summary>
        public bool NeedsState => MatrixRain;
    }
}
