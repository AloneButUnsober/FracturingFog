// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Audio;
using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.ViewModels.Animation;

/// <summary>
/// #263 / Audio-Reactive Phase 4 — app-scoped owner of the audio→param
/// modulation matrix. Holds one <see cref="AudioModulationBinding"/> per
/// parameter name (in-session; not persisted this pass) and, for each enabled
/// binding, an <see cref="AudioModulatorAnimator"/> registered as a
/// <em>permanent</em> animator on the shared <see cref="AnimationBusHost"/> bus
/// so it survives region jumps (which wipe only the dynamic set) and ticks under
/// the same render-completion gate + ceiling as every other animator.
/// <para>
/// Registration binds an animator to the live <see cref="FractalParameters"/>
/// instance current at build time; call <see cref="Rebind"/> whenever the target
/// object or the fractal type changes (region jump / type switch) so animators
/// re-resolve against the new params and drop params the new type can't animate.
/// </para>
/// </summary>
public sealed class AudioModulationManager
{
    private sealed class Entry
    {
        public required AudioModulationBinding Binding;
        public bool Enabled;
        public AudioModulatorAnimator? Animator;
    }

    private readonly Func<IAudioModulationSource?> _getSource;
    private readonly Action _ensureStarted;
    private readonly Func<FractalParameters?> _getParams;
    private readonly Func<FractalType> _getType;
    private readonly Func<ParameterAnimationBus?> _getBus;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public AudioModulationManager(
        Func<IAudioModulationSource?> getSource,
        Action ensureStarted,
        Func<FractalParameters?> getParams,
        Func<FractalType> getType,
        Func<ParameterAnimationBus?> getBus)
    {
        _getSource = getSource ?? throw new ArgumentNullException(nameof(getSource));
        _ensureStarted = ensureStarted ?? throw new ArgumentNullException(nameof(ensureStarted));
        _getParams = getParams ?? throw new ArgumentNullException(nameof(getParams));
        _getType = getType ?? throw new ArgumentNullException(nameof(getType));
        _getBus = getBus ?? throw new ArgumentNullException(nameof(getBus));
    }

    /// <summary>Animatable scalar descriptors for the current fractal type
    /// (Complex kinds excluded — out of P4 scope).</summary>
    public IReadOnlyList<AnimatableParamDescriptor> DescriptorsForCurrentType()
        => FractalAnimatableParamsMap.For(_getType())
            .Where(d => d.Kind != AnimatableParamKind.Complex)
            .ToList();

    /// <summary>Get the binding for a param, creating one seeded from the
    /// descriptor's range on first access.</summary>
    public AudioModulationBinding GetOrCreateBinding(AnimatableParamDescriptor d)
    {
        if (_entries.TryGetValue(d.ParamName, out var e)) return e.Binding;
        var binding = new AudioModulationBinding
        {
            Source = AudioSignalKind.Rms,
            OutMin = d.Min,
            OutMax = d.Max,
        };
        _entries[d.ParamName] = new Entry { Binding = binding };
        return binding;
    }

    public bool IsEnabled(string paramName)
        => _entries.TryGetValue(paramName, out var e) && e.Enabled;

    /// <summary>Enable / disable audio drive for a param. Enabling ensures audio
    /// capture is running, then rebuilds the bus registration set.</summary>
    public void SetEnabled(string paramName, bool enabled)
    {
        if (!_entries.TryGetValue(paramName, out var e)) return;
        if (e.Enabled == enabled) return;
        e.Enabled = enabled;
        if (enabled) _ensureStarted();
        Rebind();
    }

    /// <summary>Re-resolve every enabled binding against the current params /
    /// type. Call on region jump or fractal-type change.</summary>
    public void Rebind()
    {
        var bus = _getBus();
        if (bus == null) return;

        // Tear down prior registrations.
        foreach (var e in _entries.Values)
        {
            if (e.Animator != null) { bus.UnregisterPermanent(e.Animator); e.Animator = null; }
        }

        var p = _getParams();
        var src = _getSource();
        if (p != null && src != null)
        {
            var byName = DescriptorsForCurrentType().ToDictionary(d => d.ParamName, StringComparer.Ordinal);
            foreach (var (name, e) in _entries)
            {
                if (!e.Enabled) continue;
                if (!byName.TryGetValue(name, out var d)) continue;  // not animatable on this type
                var anim = AudioModulatorAnimator.TryCreate(p, name, src, e.Binding, d.Cost);
                if (anim != null) { anim.IsEnabled = true; bus.Register(anim); e.Animator = anim; }
            }
        }

        bus.Refresh();
    }
}
