// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Reflection;
using FracturingFog.Audio;

namespace FracturingFog.Abstractions.Animation
{
    /// <summary>
    /// #263 / Audio-Reactive Phase 4 — an <see cref="IParameterAnimator"/> that
    /// drives a scalar fractal parameter from a live audio signal. Each
    /// <see cref="Tick"/> samples the <see cref="IAudioModulationSource"/>,
    /// slews the binding's drive (attack / release, #1431) and either writes the
    /// mapped value (Position) or integrates the mapped speed onto the target
    /// (Rate, #1431) via the supplied setter. Registered on the same render-gated
    /// <see cref="ParameterAnimationBus"/> as every other animator, so it inherits
    /// the render-completion gate and the animated-param <c>Ceiling</c> (its
    /// <see cref="Cost"/> lets the policy shed an expensive audio track first).
    /// <para>
    /// When the source is inactive the tick is a no-op — the base parameter is
    /// left exactly as the user set it, so toggling audio off (or losing the
    /// signal) never strands a param at a modulated value mid-write. (A one-shot
    /// restore of the pre-modulation value is the owner's concern, not the
    /// animator's.)
    /// </para>
    /// </summary>
    public sealed class AudioModulatorAnimator : IParameterAnimator
    {
        private readonly IAudioModulationSource _source;
        private readonly AudioModulationBinding _binding;
        private readonly Action<double> _apply;
        private readonly Func<double>? _read;
        private readonly double _rangeMin;
        private readonly double _rangeMax;

        // #1431 — smoothing state (the slewed drive) and the Rate-mode
        // accumulator. _lastWritten is what the last tick applied (after the
        // setter's own rounding, read back), so an outside edit — the user's
        // slider, a region recall — re-seeds the integration from the new value.
        private bool _primed;
        private double _drive;
        private bool _seeded;
        private double _acc;
        private double _lastWritten;

        /// <param name="read">Reads the target's current value. Rate mode
        /// integrates from it (and re-seeds when something else changes it);
        /// without it Rate integrates from 0.</param>
        /// <param name="rangeMin">Lower end of the target's range — Rate mode
        /// clamps (or, with <see cref="AudioModulationBinding.Wrap"/>, wraps) the
        /// integrated value into [<paramref name="rangeMin"/>, <paramref name="rangeMax"/>].
        /// Unbounded when either end is not finite.</param>
        public AudioModulatorAnimator(
            string name,
            IAudioModulationSource source,
            AudioModulationBinding binding,
            Action<double> apply,
            AnimatableParamCost cost = AnimatableParamCost.Cheap,
            Func<double>? read = null,
            double rangeMin = double.NegativeInfinity,
            double rangeMax = double.PositiveInfinity)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _binding = binding ?? throw new ArgumentNullException(nameof(binding));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _read = read;
            _rangeMin = rangeMin;
            _rangeMax = rangeMax;
            Cost = cost;
        }

        public string Name { get; }
        public bool IsEnabled { get; set; }
        public AnimatableParamCost Cost { get; }

        /// <summary>The binding this animator evaluates — exposed so a matrix UI
        /// can retune gain / curve / range / mode live without rebuilding the
        /// animator.</summary>
        public AudioModulationBinding Binding => _binding;

        public void Tick(double dt)
        {
            if (!_source.IsActive) return;
            double drive = Smooth(_binding.Drive(_source.Sample()), dt);
            if (_binding.Mode == AudioBindingMode.Rate)
                Integrate(_binding.Map(drive), dt);
            else
            {
                _seeded = false; // a later switch to Rate starts from the live value
                _apply(_binding.Map(drive));
            }
        }

        /// <summary>One-pole slew toward <paramref name="target"/>: the attack
        /// time-constant while rising, release while falling; 0 = instant. The
        /// first sample primes the state, so enabling never fades in from 0.</summary>
        private double Smooth(double target, double dt)
        {
            if (!_primed) { _primed = true; return _drive = target; }
            double tau = target > _drive ? _binding.AttackSeconds : _binding.ReleaseSeconds;
            if (tau <= 0 || dt <= 0) return _drive = target;
            return _drive += (target - _drive) * (1.0 - System.Math.Exp(-dt / tau));
        }

        private void Integrate(double speed, double dt)
        {
            double current = _read?.Invoke() ?? (_seeded ? _lastWritten : 0.0);
            if (!_seeded || current != _lastWritten) { _acc = current; _seeded = true; }
            _acc = Fit(_acc + speed * System.Math.Max(0.0, dt));
            _apply(_acc);
            _lastWritten = _read?.Invoke() ?? _acc;
        }

        private double Fit(double v)
        {
            if (!double.IsFinite(_rangeMin) || !double.IsFinite(_rangeMax) || _rangeMax <= _rangeMin)
                return v;
            if (!_binding.Wrap) return System.Math.Clamp(v, _rangeMin, _rangeMax);
            double span = _rangeMax - _rangeMin;
            return _rangeMin + (v - _rangeMin - System.Math.Floor((v - _rangeMin) / span) * span);
        }

        /// <summary>
        /// Build an audio animator that drives the named scalar property of
        /// <paramref name="target"/> (a <c>FractalParameters</c>) via reflection —
        /// the same by-name binding the procedural track factory uses. Returns
        /// null when the property is missing, read-only, or not a supported scalar
        /// (<c>double</c> / <c>int</c>; <c>Complex</c> and other kinds are out of
        /// P4 scope). <paramref name="cost"/> defaults to Cheap; callers with a
        /// <see cref="AnimatableParamDescriptor"/> should pass its
        /// <see cref="AnimatableParamDescriptor.Cost"/>, and its
        /// <c>Min</c>/<c>Max</c> as the range Rate mode clamps or wraps into (#1431).
        /// </summary>
        public static AudioModulatorAnimator? TryCreate(
            object target,
            string paramName,
            IAudioModulationSource source,
            AudioModulationBinding binding,
            AnimatableParamCost cost = AnimatableParamCost.Cheap,
            double rangeMin = double.NegativeInfinity,
            double rangeMax = double.PositiveInfinity)
        {
            if (target == null || string.IsNullOrWhiteSpace(paramName)
                || source == null || binding == null)
                return null;

            var prop = target.GetType().GetProperty(
                paramName, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanRead || !prop.CanWrite) return null;

            Action<double> apply;
            Func<double> read;
            if (prop.PropertyType == typeof(double))
            {
                apply = v => prop.SetValue(target, v);
                read = () => (double)prop.GetValue(target)!;
            }
            else if (prop.PropertyType == typeof(int))
            {
                apply = v => prop.SetValue(target, (int)System.Math.Round(v));
                read = () => (int)prop.GetValue(target)!;
            }
            else
                return null; // Complex / unsupported — not in P4 scope

            return new AudioModulatorAnimator(paramName, source, binding, apply, cost,
                                              read, rangeMin, rangeMax);
        }
    }
}
