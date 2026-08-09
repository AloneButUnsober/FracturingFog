// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Audio;

namespace FracturingFog.Imaging
{
    /// <summary>
    /// #261 / Audio-Reactive Phase 2 — maps an <see cref="AudioModulationFrame"/>
    /// onto <see cref="AsciiFxSettings"/> scalars so the terminal / ASCII view
    /// pulses with the music. Pure function of (settings, frame): no effect code
    /// changes, only the settings feed. Enabling the "Beat" quick-toggle routes
    /// the live pump through <see cref="Apply"/> each frame.
    /// <para>
    /// It turns on a curated signature set (breathe + bloom + tempo-synced hue /
    /// ramp scroll, plus a glitch stab on each onset) and drives their scalars
    /// from the audio, and additionally surges effects the user already enabled
    /// (e.g. Matrix rain) without switching them on unasked. An inactive frame
    /// (<see cref="AudioModulationFrame.IsActive"/> = false) is a no-op — the
    /// base settings pass through untouched.
    /// </para>
    /// </summary>
    public static class AudioReactiveAsciiFx
    {
        public static void Apply(AsciiFxSettings fx, in AudioModulationFrame f)
        {
            if (fx == null || !f.IsActive) return;

            // Bass → glyph-density breathe depth (the field pumps on the kick).
            fx.Breathe = true;
            fx.BreatheGammaAmp = 0.15 + 0.60 * f.Bass;

            // RMS → bloom strength (louder = brighter glow bleed).
            fx.Bloom = true;
            fx.BloomStrength = 0.25 + 1.25 * f.Rms;

            // Onset → one-frame glitch stab.
            if (f.Transient)
            {
                fx.Glitch = true;
                fx.GlitchIntensity = 0.5;
            }

            // Tempo → synced hue cycle + ramp shimmer, locked to BPM.
            if (f.Bpm > 0)
            {
                fx.HueCycle = true;
                fx.HueCycleDegPerSec = f.Bpm * 1.5;
                fx.RampScroll = true;
                fx.RampScrollSpeed = f.Bpm / 30.0;
            }

            // Beat envelope surges Matrix rain — but only if the user turned it
            // on; never enable a heavy structural effect unasked.
            if (fx.MatrixRain)
                fx.MatrixRainSpeed = 8.0 + 22.0 * f.BeatPulse;
        }
    }
}
