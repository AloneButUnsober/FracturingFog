// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Text.Json.Serialization;

namespace FracturingFog.Audio
{
    /// <summary>How a binding's shaped drive lands on its target (#1431).</summary>
    public enum AudioBindingMode
    {
        /// <summary>The drive picks the value: <c>lerp(OutMin, OutMax, drive)</c>.
        /// The knob follows the music and falls back when it drops.</summary>
        Position,
        /// <summary>The drive picks a speed: <c>lerp(OutMin, OutMax, drive)</c>
        /// units per second, integrated onto the target. The knob only ever moves
        /// at the signed speed the music sets — it never jumps back when the
        /// signal drops, so e.g. <c>AcidWarpFlow</c> flows pattern to pattern.</summary>
        Rate,
    }

    /// <summary>Shaping curve applied to a normalized 0..1 signal before gain /
    /// bias. All variants are monotonic on [0,1] and map [0,1]-&gt;[0,1].</summary>
    public enum AudioResponseCurve
    {
        /// <summary>x — pass-through.</summary>
        Linear,
        /// <summary>x^2 — expand: suppress quiet, emphasize peaks.</summary>
        Exp,
        /// <summary>sqrt(x) — compress: lift quiet detail toward the ceiling.</summary>
        Log,
        /// <summary>x^2(3-2x) — ease-in-out, no hard corners.</summary>
        Smoothstep,
    }

    /// <summary>
    /// One row of the audio modulation matrix: take an
    /// <see cref="AudioSignalKind"/>, shape it, and land it in a target
    /// parameter's <see cref="OutMin"/>..<see cref="OutMax"/> range. Pure data —
    /// savable in regions / scenes / presets. <see cref="Evaluate"/> is a total
    /// function of the frame; callers gate on <see cref="AudioModulationFrame.IsActive"/>
    /// to decide whether to write the result at all (an inactive analyzer must
    /// leave the base parameter untouched).
    /// </summary>
    public sealed class AudioModulationBinding
    {
        /// <summary>Which derived signal drives this binding.</summary>
        public AudioSignalKind Source { get; set; } = AudioSignalKind.Rms;

        /// <summary>Scale applied to the shaped signal (before clamp).</summary>
        public double Gain { get; set; } = 1.0;

        /// <summary>Constant added to the shaped signal (before clamp). Lets a
        /// binding idle above zero so a param never fully collapses.</summary>
        public double Bias { get; set; } = 0.0;

        /// <summary>Response shaping applied to the raw 0..1 signal.</summary>
        public AudioResponseCurve Curve { get; set; } = AudioResponseCurve.Linear;

        /// <summary>Flip the shaped signal (1 - x) — e.g. duck a param on the beat.</summary>
        public bool Invert { get; set; }

        /// <summary>Target value when the shaped/gained signal is 0.</summary>
        public double OutMin { get; set; } = 0.0;

        /// <summary>Target value when the shaped/gained signal is 1.</summary>
        public double OutMax { get; set; } = 1.0;

        /// <summary>#1431 — Position (default) or Rate. In Rate mode
        /// <see cref="OutMin"/>/<see cref="OutMax"/> are speeds in target units per
        /// second. Stateful modes are honoured by <c>AudioModulatorAnimator</c>;
        /// <see cref="Evaluate"/> is always the stateless Position mapping.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public AudioBindingMode Mode { get; set; } = AudioBindingMode.Position;

        /// <summary>#1431 — Rate mode only: wrap the integrated value around the
        /// target's range instead of clamping at its ends (for periodic targets
        /// such as <c>AcidWarpFlow</c> or an angle).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Wrap { get; set; }

        /// <summary>#1431 — time constant (seconds) for the drive to rise toward a
        /// higher value. 0 = instant (no smoothing).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double AttackSeconds { get; set; }

        /// <summary>#1431 — time constant (seconds) for the drive to fall toward a
        /// lower value. 0 = instant (no smoothing).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double ReleaseSeconds { get; set; }

        /// <summary>
        /// Map the frame's <see cref="Source"/> signal to a target value:
        /// shape -&gt; invert -&gt; bias+gain -&gt; clamp01 -&gt; lerp into
        /// [<see cref="OutMin"/>, <see cref="OutMax"/>]. Deterministic; does not
        /// consult <see cref="AudioModulationFrame.IsActive"/> (caller's job).
        /// </summary>
        public double Evaluate(in AudioModulationFrame frame) => Map(Drive(frame));

        /// <summary>The frame's shaped drive in [0, 1]: shape -&gt; invert -&gt;
        /// bias+gain -&gt; clamp01 (before smoothing and before the range lerp).</summary>
        public double Drive(in AudioModulationFrame frame)
        {
            double s = ApplyCurve(Math.Clamp(frame.Signal(Source), 0f, 1f), Curve);
            if (Invert) s = 1.0 - s;
            return Math.Clamp(Bias + Gain * s, 0.0, 1.0);
        }

        /// <summary>Lerp a drive in [0, 1] into [<see cref="OutMin"/>, <see cref="OutMax"/>]
        /// — the value (Position) or the speed per second (Rate).</summary>
        public double Map(double drive) => OutMin + (OutMax - OutMin) * drive;

        private static double ApplyCurve(double x, AudioResponseCurve curve) => curve switch
        {
            AudioResponseCurve.Exp => x * x,
            AudioResponseCurve.Log => Math.Sqrt(x),
            AudioResponseCurve.Smoothstep => x * x * (3.0 - 2.0 * x),
            _ => x,
        };
    }
}
