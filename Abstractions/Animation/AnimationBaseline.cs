// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Reflection;

namespace FracturingFog.Abstractions.Animation;

/// <summary>#962 (#941 S3) — the pre-animation values of the parameters an animation
/// session drives, so an explicit stop can put the fractal back where it was.
/// Animators write straight into the live params (via reflection,
/// <see cref="AnimationDataExtensions.ToAnimators"/>); before this, stopping one only
/// cleared the bus and left the fractal wherever the animation happened to be.
/// <para>A <b>session</b> is bound to one target object. <see cref="Capture(AnimationData?)"/>
/// records each targeted param the first time it is seen, so re-pushing an edited
/// animation mid-session (the editor's Live Preview) extends the baseline without
/// overwriting it with already-animated values. <see cref="Begin"/> starts a fresh
/// session without restoring — used when something authoritative (a region recall)
/// has just set the params, so the old baseline must not come back.</para></summary>
public sealed class AnimationBaseline
{
    private readonly Dictionary<string, (PropertyInfo Prop, object? Value)> _values = new(StringComparer.Ordinal);

    /// <summary>The params object this session records against, or null when idle.</summary>
    public object? Target { get; private set; }

    /// <summary>Names of the params recorded so far this session.</summary>
    public IReadOnlyCollection<string> CapturedParams => _values.Keys;

    /// <summary>Start a new session over <paramref name="target"/>, dropping any previous
    /// baseline WITHOUT restoring it.</summary>
    public void Begin(object target)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        _values.Clear();
    }

    /// <summary>Record the current value of every param <paramref name="data"/>'s tracks
    /// target (enabled or not — a track toggled on later is covered), skipping params
    /// already recorded this session. Tracks that don't resolve on the target are
    /// ignored, exactly as <see cref="AnimationDataExtensions.ToAnimators"/> skips them.</summary>
    public void Capture(AnimationData? data)
    {
        if (data?.Tracks == null) return;
        foreach (var track in data.Tracks)
            Capture(track?.ParamName);
    }

    /// <summary>Record one param by name (no-op if already recorded, unresolvable, or no
    /// session is active).</summary>
    public void Capture(string? paramName)
    {
        if (Target == null || string.IsNullOrWhiteSpace(paramName) || _values.ContainsKey(paramName)) return;
        var prop = Target.GetType().GetProperty(paramName, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || !prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length != 0) return;
        try { _values[paramName] = (prop, prop.GetValue(Target)); }
        catch { /* a throwing getter just isn't restorable */ }
    }

    /// <summary>Write every recorded value back onto the target and end the session.
    /// Returns true when anything was restored.</summary>
    public bool Restore()
    {
        bool any = false;
        if (Target != null)
        {
            foreach (var (prop, value) in _values.Values)
            {
                try { prop.SetValue(Target, value); any = true; }
                catch { /* keep going — restore what can be restored */ }
            }
        }
        Discard();
        return any;
    }

    /// <summary>End the session without restoring.</summary>
    public void Discard()
    {
        _values.Clear();
        Target = null;
    }
}
