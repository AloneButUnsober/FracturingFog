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
    private readonly Action _reconcile;
    private readonly Func<FractalParameters?> _getParams;
    private readonly Func<FractalType> _getType;
    private readonly Func<ParameterAnimationBus?> _getBus;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public AudioModulationManager(
        Func<IAudioModulationSource?> getSource,
        Action reconcile,
        Func<FractalParameters?> getParams,
        Func<FractalType> getType,
        Func<ParameterAnimationBus?> getBus)
    {
        _getSource = getSource ?? throw new ArgumentNullException(nameof(getSource));
        _reconcile = reconcile ?? throw new ArgumentNullException(nameof(reconcile));
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
            Edge = d.Wraps ? AudioRateEdge.Wrap : AudioRateEdge.Clamp,   // #1431 — periodic targets wrap in Rate mode
        };
        _entries[d.ParamName] = new Entry { Binding = binding };
        return binding;
    }

    public bool IsEnabled(string paramName)
        => _entries.TryGetValue(paramName, out var e) && e.Enabled;

    /// <summary>#1431 — true when an enabled binding drives
    /// <paramref name="paramName"/> (e.g. <c>AcidWarpFlow</c>, whose drive only
    /// shows with Acid Warp Morph on).</summary>
    public static bool AnyEnabledFor(IEnumerable<AudioParamBinding>? bindings, string paramName)
        => bindings != null && bindings.Any(b => b != null && b.Enabled
            && string.Equals(b.ParamName, paramName, StringComparison.Ordinal));

    /// <summary>#1432 — names of the params audio is driving right now (enabled
    /// and resolved against the current type), in name order.</summary>
    public IReadOnlyList<string> ActiveParamNames()
        => _entries.Where(kv => kv.Value.Enabled && kv.Value.Animator != null)
                   .Select(kv => kv.Key)
                   .OrderBy(n => n, StringComparer.Ordinal)
                   .ToList();

    /// <summary>#1432 — the bindings audio is driving right now (as
    /// <see cref="ActiveParamNames"/>), cloned, for the Command builder's
    /// <c>--audio-bind</c>.</summary>
    public IReadOnlyList<AudioParamBinding> ActiveBindings()
        => _entries.Where(kv => kv.Value.Enabled && kv.Value.Animator != null)
                   .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                   .Select(kv => new AudioParamBinding { ParamName = kv.Key, Enabled = true, Binding = CloneBinding(kv.Value.Binding) })
                   .ToList();

    /// <summary>#277 — true when at least one binding is enabled. Feeds the
    /// host's audio-capture demand predicate.</summary>
    public bool HasEnabledBindings => _entries.Values.Any(e => e.Enabled);

    /// <summary>Enable / disable audio drive for a param. Enabling reconciles
    /// capture on (before rebind, so the source exists); disabling reconciles
    /// after teardown so capture stops when nothing else needs it.</summary>
    public void SetEnabled(string paramName, bool enabled)
    {
        if (!_entries.TryGetValue(paramName, out var e)) return;
        if (e.Enabled == enabled) return;
        e.Enabled = enabled;
        if (enabled) _reconcile();
        Rebind();
        if (!enabled) _reconcile();
    }

    /// <summary>#268 — snapshot every binding (param name + mapping + enabled) for
    /// persistence on a region. Clones the mappings so later edits don't mutate the
    /// saved copy.</summary>
    public IReadOnlyList<AudioParamBinding> ExportBindings()
    {
        var list = new List<AudioParamBinding>(_entries.Count);
        foreach (var (name, e) in _entries)
        {
            list.Add(new AudioParamBinding
            {
                ParamName = name,
                Enabled = e.Enabled,
                Binding = CloneBinding(e.Binding),
            });
        }
        return list;
    }

    /// <summary>#268 — replace the in-session bindings with a region's saved set,
    /// tearing down any current animator registrations first. Call
    /// <see cref="Rebind"/> afterwards to re-register the enabled ones against the
    /// new region's params. Null / empty clears all drive (region carried none).</summary>
    public void LoadBindings(IEnumerable<AudioParamBinding>? bindings)
    {
        var bus = _getBus();
        foreach (var e in _entries.Values)
            if (e.Animator != null) { bus?.UnregisterPermanent(e.Animator); e.Animator = null; }
        _entries.Clear();

        if (bindings != null)
        {
            foreach (var b in bindings)
            {
                if (string.IsNullOrEmpty(b?.ParamName)) continue;
                _entries[b.ParamName] = new Entry
                {
                    Binding = CloneBinding(b.Binding ?? new AudioModulationBinding()),
                    Enabled = b.Enabled,
                };
            }
        }
        // Reconcile capture: start if the loaded set enables any binding, stop
        // if this region carried none and nothing else wants audio (#277).
        _reconcile();
    }

    /// <summary>#1434 — apply a recipe's bindings to the current fractal type. Each
    /// binding the type can animate is copied INTO the existing binding object (so
    /// open matrix rows stay bound to it) and enabled as the recipe says; the rest
    /// are skipped. <paramref name="replace"/> first turns off every other binding
    /// of this type; otherwise the recipe merges over them. Returns the params it set.</summary>
    public IReadOnlyList<string> ApplyRecipe(IEnumerable<AudioParamBinding>? bindings, bool replace)
    {
        var byName = DescriptorsForCurrentType().ToDictionary(d => d.ParamName, StringComparer.Ordinal);
        if (replace)
            foreach (var (name, e) in _entries)
                if (byName.ContainsKey(name)) e.Enabled = false;
        var applied = new List<string>();
        foreach (var b in bindings ?? Array.Empty<AudioParamBinding>())
        {
            if (b == null || !byName.TryGetValue(b.ParamName ?? string.Empty, out var d)) continue;
            CopyInto(b.Binding ?? new AudioModulationBinding(), GetOrCreateBinding(d));
            _entries[d.ParamName].Enabled = b.Enabled;
            if (!applied.Contains(d.ParamName)) applied.Add(d.ParamName);
        }
        _reconcile();
        Rebind();
        _reconcile();
        return applied;
    }

    /// <summary>#1434 — undo for <see cref="ApplyRecipe"/>: put back a snapshot taken
    /// with <see cref="ExportBindings"/>, in place. A binding created since the
    /// snapshot is turned off.</summary>
    public void RestoreBindings(IReadOnlyList<AudioParamBinding> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var saved = snapshot.Where(b => !string.IsNullOrEmpty(b?.ParamName))
                            .GroupBy(b => b.ParamName, StringComparer.Ordinal)
                            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        foreach (var (name, e) in _entries)
            if (!saved.ContainsKey(name)) e.Enabled = false;
        foreach (var (name, b) in saved)
        {
            if (_entries.TryGetValue(name, out var e)) CopyInto(b.Binding ?? new AudioModulationBinding(), e.Binding);
            else _entries[name] = e = new Entry { Binding = CloneBinding(b.Binding ?? new AudioModulationBinding()) };
            e.Enabled = b.Enabled;
        }
        _reconcile();
        Rebind();
        _reconcile();
    }

    private static void CopyInto(AudioModulationBinding from, AudioModulationBinding to)
    {
        to.Source = from.Source;
        to.Gain = from.Gain;
        to.Bias = from.Bias;
        to.Curve = from.Curve;
        to.Invert = from.Invert;
        to.OutMin = from.OutMin;
        to.OutMax = from.OutMax;
        to.Mode = from.Mode;
        to.Edge = from.Edge;
        to.AttackSeconds = from.AttackSeconds;
        to.ReleaseSeconds = from.ReleaseSeconds;
    }

    private static AudioModulationBinding CloneBinding(AudioModulationBinding b) => new()
    {
        Source = b.Source,
        Gain = b.Gain,
        Bias = b.Bias,
        Curve = b.Curve,
        Invert = b.Invert,
        OutMin = b.OutMin,
        OutMax = b.OutMax,
        Mode = b.Mode,
        Edge = b.Edge,
        AttackSeconds = b.AttackSeconds,
        ReleaseSeconds = b.ReleaseSeconds,
    };

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
                var anim = AudioModulatorAnimator.TryCreate(p, name, src, e.Binding, d.Cost, d.Min, d.Max);
                if (anim != null) { anim.IsEnabled = true; bus.Register(anim); e.Animator = anim; }
            }
        }

        bus.Refresh();
    }
}
