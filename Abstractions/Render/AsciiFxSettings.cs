// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/AsciiFxSettings.cs
//
// Settings for the ASCII-native FX chain (#229) — post effects applied to the
// character grid AFTER downsample/ramp, before paint or file emit. Pure CPU, no
// fractal recompute. Animated effects read TimeSeconds (wall clock for the live
// view, or SceneTime when baked into an animation export).
//
// Lives in Abstractions (not Engine) so the UI shell — which does not reference
// Engine — can build the full effect set and hand it to IFractalRenderHost. The
// namespace stays FracturingFog.Imaging (namespaces span assemblies) so the
// Engine-side AsciiFxChain / AsciiFxState consume it unchanged. The effect
// implementation and cross-frame state remain Engine types.

namespace FracturingFog.Imaging
{
    /// <summary>Knobs for the ASCII FX chain (<c>AsciiFxChain</c>). All effects
    /// off by default (identity pass).</summary>
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

        /// <summary>Darken toward the frame edges (radial falloff). Static.</summary>
        public bool Vignette { get; set; }
        /// <summary>Vignette depth, [0,1] — how dark the corners get.</summary>
        public double VignetteStrength { get; set; } = 0.7;

        /// <summary>CRT full: a bundled retro-monitor look — barrel warp + green
        /// phosphor bias + scanlines + vignette in one toggle. Static.</summary>
        public bool CrtFull { get; set; }
        /// <summary>Barrel (bulge) amount for <see cref="CrtFull"/>.</summary>
        public double CrtBarrel { get; set; } = 0.12;

        // ── Spatial (read neighbours / displace) ──────────────────────────

        /// <summary>Chromatic aberration: offset the red / blue channels
        /// horizontally by <see cref="ChromaticShift"/> cells for an RGB fringe.
        /// Static.</summary>
        public bool ChromaticAberration { get; set; }
        /// <summary>Channel offset in cells for <see cref="ChromaticAberration"/>.</summary>
        public int ChromaticShift { get; set; } = 1;

        /// <summary>Ripple: displace each row horizontally by a travelling sine
        /// wave. Animated.</summary>
        public bool Wave { get; set; }
        /// <summary>Peak horizontal displacement in cells.</summary>
        public double WaveAmplitude { get; set; } = 2.0;
        /// <summary>Wavelength in rows.</summary>
        public double WaveLength { get; set; } = 8.0;
        /// <summary>Wave travel speed (radians per second).</summary>
        public double WaveSpeed { get; set; } = 2.0;

        /// <summary>Drift: pan the whole grid over time, wrapping at the edges,
        /// independent of the fractal. Animated.</summary>
        public bool Drift { get; set; }
        /// <summary>Horizontal / vertical drift in cells per second.</summary>
        public double DriftDxPerSec { get; set; } = 3.0;
        public double DriftDyPerSec { get; set; } = 0.0;

        /// <summary>Twist: rotate the grid about its centre, the angle strongest at
        /// the centre and fading to the edge — a swirl. Static.</summary>
        public bool Twist { get; set; }
        /// <summary>Peak twist angle at the centre, in radians.</summary>
        public double TwistStrength { get; set; } = 1.5;

        /// <summary>Glitch: tear random rows sideways in bursts. Stateless (hashed
        /// from row + frame), so reproducible. Animated.</summary>
        public bool Glitch { get; set; }
        /// <summary>Fraction of rows torn each burst, [0,1].</summary>
        public double GlitchIntensity { get; set; } = 0.3;
        /// <summary>Glitch re-roll rate (bursts per second).</summary>
        public double GlitchHz { get; set; } = 8.0;

        /// <summary>Bloom: bright cells bleed a soft glow onto their neighbours.
        /// Static.</summary>
        public bool Bloom { get; set; }
        /// <summary>Glow add strength, [0,~2].</summary>
        public double BloomStrength { get; set; } = 0.6;
        /// <summary>Only neighbours brighter than this luma glow, [0,1].</summary>
        public double BloomThreshold { get; set; } = 0.55;

        /// <summary>Edge / contour: Sobel the brightness and draw oriented line
        /// glyphs on the boundaries, dimming the interior. Static.</summary>
        public bool Edge { get; set; }
        /// <summary>Gradient magnitude to count as an edge, [0,1] of a full
        /// black↔white step between adjacent cells. Low values reveal fine
        /// contours (needed at zoom, where per-cell contrast is gentle).</summary>
        public double EdgeThreshold { get; set; } = 0.12;

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

        /// <summary>Film grain: randomly nudge each glyph ±1 along the ramp,
        /// re-rolled over time — twinkle / noise. Stateless (hashed from cell +
        /// frame), so reproducible. Animated.</summary>
        public bool Grain { get; set; }
        /// <summary>Fraction of cells that jitter each frame, [0,1].</summary>
        public double GrainAmount { get; set; } = 0.4;
        /// <summary>Grain re-roll rate (frames per second).</summary>
        public double GrainHz { get; set; } = 20.0;

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

        /// <summary>Ordered (Bayer 4×4) dithering of colour to
        /// <see cref="DitherLevels"/> per-channel steps — retro banding-free
        /// gradients from few colours. Static.</summary>
        public bool Dither { get; set; }
        /// <summary>Per-channel levels the dither resolves to (≥2).</summary>
        public int DitherLevels { get; set; } = 3;

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

        /// <summary>Plasma / fire: blend an animated procedural noise field (fire
        /// palette) over the colour. Stateless (a function of x, y, time).
        /// Animated.</summary>
        public bool Plasma { get; set; }
        /// <summary>Blend amount of the plasma over the base colour, [0,1].</summary>
        public double PlasmaStrength { get; set; } = 0.55;
        /// <summary>Spatial frequency of the plasma.</summary>
        public double PlasmaScale { get; set; } = 0.18;
        /// <summary>Plasma animation speed.</summary>
        public double PlasmaSpeed { get; set; } = 1.2;

        // ── Structural (stateful) ─────────────────────────────────────────

        /// <summary>"Matrix" digital rain: falling columns of glyphs, the fractal
        /// showing through as a ghost mask. Animated; needs a persistent
        /// <c>AsciiFxState</c>.</summary>
        public bool MatrixRain { get; set; }
        /// <summary>Base fall speed in rows per second.</summary>
        public double MatrixRainSpeed { get; set; } = 14.0;
        /// <summary>Fraction of columns that carry a drop, [0,1].</summary>
        public double MatrixRainDensity { get; set; } = 0.85;
        /// <summary>How strongly the underlying fractal brightness masks the rain
        /// (0 = uniform rain, 1 = rain only where the fractal is bright).</summary>
        public double MatrixRainMask { get; set; } = 0.6;

        /// <summary>Particles: drifting snow / rain flecks over the art. Animated;
        /// needs a persistent <c>AsciiFxState</c>.</summary>
        public bool Particles { get; set; }
        /// <summary>Number of particles.</summary>
        public int ParticleCount { get; set; } = 60;
        /// <summary>Fall speed in rows per second.</summary>
        public double ParticleSpeed { get; set; } = 6.0;
        /// <summary>Horizontal sway amplitude in cells.</summary>
        public double ParticleSway { get; set; } = 1.5;
        /// <summary>Glyph drawn for each particle.</summary>
        public char ParticleGlyph { get; set; } = '*';

        // ── Transitions (reveal over TransitionSeconds) ───────────────────

        /// <summary>Seconds for a reveal transition to complete.</summary>
        public double TransitionSeconds { get; set; } = 2.0;

        /// <summary>When true (default) reveal transitions loop — wipe in, reset,
        /// wipe in again — so they are visibly animating in the live view. Set
        /// false for a one-shot intro (e.g. baked at the head of a recording),
        /// which stays fully revealed once complete.</summary>
        public bool TransitionLoop { get; set; } = true;

        /// <summary>Typewriter: reveal cells in reading order over the transition,
        /// the rest blank. Animated.</summary>
        public bool Typewriter { get; set; }

        /// <summary>Dissolve: reveal cells in a fixed pseudo-random order over the
        /// transition, the rest blank. Animated.</summary>
        public bool Dissolve { get; set; }

        /// <summary>Frame trails: blend a decayed copy of the previous frame into
        /// this one — a phosphor / motion smear (most visible while navigating).
        /// Animated; needs a persistent <c>AsciiFxState</c>.</summary>
        public bool Trails { get; set; }
        /// <summary>Per-frame trail persistence, [0,1) — higher = longer smear.</summary>
        public double TrailDecay { get; set; } = 0.92;

        /// <summary>True when any effect is enabled (the host can skip the pass).</summary>
        public bool AnyEnabled => HueCycle || Crt || CrtFull || Breathe || CharsetSwap || Monochrome
            || Saturate || Invert || Solarize || Quantize || Dither || Duotone || Plasma
            || RampScroll || Grain || Vignette || ChromaticAberration || Wave || Drift || Twist
            || Glitch || Bloom || Edge || MatrixRain || Particles || Typewriter || Dissolve || Trails;

        /// <summary>True when an enabled effect varies with time (the live view
        /// must repaint on a timer for these, not only on buffer changes).</summary>
        public bool AnyAnimated => HueCycle || Breathe || MatrixRain || RampScroll || Grain || Wave
            || Drift || Glitch || Plasma || Particles || Typewriter || Dissolve || Trails
            || (Saturate && SaturateAmp > 0);

        /// <summary>True when an enabled effect evolves across frames and so needs
        /// a persistent <c>AsciiFxState</c> (not a pure grid+clock function).</summary>
        public bool NeedsState => MatrixRain || Particles || Trails;
    }
}
