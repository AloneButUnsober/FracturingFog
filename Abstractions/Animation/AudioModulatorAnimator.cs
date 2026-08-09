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
    /// <see cref="Tick"/> samples the <see cref="IAudioModulationSource"/> and
    /// writes <c>binding.Evaluate(frame)</c> into the target via the supplied
    /// setter. Registered on the same render-gated
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

        public AudioModulatorAnimator(
            string name,
            IAudioModulationSource source,
            AudioModulationBinding binding,
            Action<double> apply,
            AnimatableParamCost cost = AnimatableParamCost.Cheap)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _binding = binding ?? throw new ArgumentNullException(nameof(binding));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            Cost = cost;
        }

        public string Name { get; }
        public bool IsEnabled { get; set; }
        public AnimatableParamCost Cost { get; }

        /// <summary>The binding this animator evaluates — exposed so a matrix UI
        /// can retune gain / curve / range live without rebuilding the animator.</summary>
        public AudioModulationBinding Binding => _binding;

        public void Tick(double dt)
        {
            if (!_source.IsActive) return;
            _apply(_binding.Evaluate(_source.Sample()));
        }

        /// <summary>
        /// Build an audio animator that drives the named scalar property of
        /// <paramref name="target"/> (a <c>FractalParameters</c>) via reflection —
        /// the same by-name binding the procedural track factory uses. Returns
        /// null when the property is missing, read-only, or not a supported scalar
        /// (<c>double</c> / <c>int</c>; <c>Complex</c> and other kinds are out of
        /// P4 scope). <paramref name="cost"/> defaults to Cheap; callers with a
        /// <see cref="AnimatableParamDescriptor"/> should pass its
        /// <see cref="AnimatableParamDescriptor.Cost"/>.
        /// </summary>
        public static AudioModulatorAnimator? TryCreate(
            object target,
            string paramName,
            IAudioModulationSource source,
            AudioModulationBinding binding,
            AnimatableParamCost cost = AnimatableParamCost.Cheap)
        {
            if (target == null || string.IsNullOrWhiteSpace(paramName)
                || source == null || binding == null)
                return null;

            var prop = target.GetType().GetProperty(
                paramName, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanRead || !prop.CanWrite) return null;

            Action<double> apply;
            if (prop.PropertyType == typeof(double))
                apply = v => prop.SetValue(target, v);
            else if (prop.PropertyType == typeof(int))
                apply = v => prop.SetValue(target, (int)System.Math.Round(v));
            else
                return null; // Complex / unsupported — not in P4 scope

            return new AudioModulatorAnimator(paramName, source, binding, apply, cost);
        }
    }
}
