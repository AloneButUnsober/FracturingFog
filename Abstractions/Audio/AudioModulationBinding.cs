// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog.Audio
{
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

        /// <summary>
        /// Map the frame's <see cref="Source"/> signal to a target value:
        /// shape -&gt; invert -&gt; bias+gain -&gt; clamp01 -&gt; lerp into
        /// [<see cref="OutMin"/>, <see cref="OutMax"/>]. Deterministic; does not
        /// consult <see cref="AudioModulationFrame.IsActive"/> (caller's job).
        /// </summary>
        public double Evaluate(in AudioModulationFrame frame)
        {
            double s = ApplyCurve(Math.Clamp(frame.Signal(Source), 0f, 1f), Curve);
            if (Invert) s = 1.0 - s;
            double u = Math.Clamp(Bias + Gain * s, 0.0, 1.0);
            return OutMin + (OutMax - OutMin) * u;
        }

        private static double ApplyCurve(double x, AudioResponseCurve curve) => curve switch
        {
            AudioResponseCurve.Exp => x * x,
            AudioResponseCurve.Log => Math.Sqrt(x),
            AudioResponseCurve.Smoothstep => x * x * (3.0 - 2.0 * x),
            _ => x,
        };
    }
}
