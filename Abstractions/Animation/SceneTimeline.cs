// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Animation/SceneTimeline.cs
//
// Scene Engine Roadmap — Phase S6: the pure, deterministic playback schedule for
// a SceneData. Turns an ordered list of shots into a back-to-back timeline and
// answers "at global time t, which shot is playing, how far into it are we, and
// are we inside an incoming transition?".
//
// Cut model (matches the slideshow crossfade): shots do NOT overlap in play
// time — each occupies [Start, End). A shot's transition is its *opening*
// window: for the first TransitionSeconds of shot i (i > 0, kind != Cut) the
// composite blends the frozen last frame of shot i-1 into the live frame of
// shot i (blend 0→1). Freezing the outgoing frame is what keeps realtime
// playback inside the resource cap — we never run two shots live at once. The
// actual pixel composite is the consumer's job (realtime playback cuts; the
// offline frame-locked path composites — S7); this class only supplies the
// blend factor.
//
// Pure + allocation-light + unit-tested. No UI, no render, no clock.

using System;
using System.Collections.Generic;

namespace FracturingFog.Abstractions.Animation;

/// <summary>One shot's slot on the timeline.</summary>
public readonly struct SceneScheduleEntry
{
    public SceneScheduleEntry(int originalIndex, double startTime, double duration,
                              SceneTransitionKind transition, double transitionSeconds)
    {
        OriginalIndex = originalIndex;
        StartTime = startTime;
        Duration = duration;
        Transition = transition;
        TransitionSeconds = transitionSeconds;
    }

    /// <summary>Index into the source <see cref="SceneData.Shots"/> list (skips
    /// zero/negative-duration shots, so this is not the schedule position).</summary>
    public int OriginalIndex { get; }

    /// <summary>Global start time (seconds) of this shot.</summary>
    public double StartTime { get; }

    /// <summary>Shot length (seconds), always &gt; 0 on the schedule.</summary>
    public double Duration { get; }

    /// <summary>Global end time — <c>StartTime + Duration</c>.</summary>
    public double EndTime => StartTime + Duration;

    /// <summary>How this shot arrives from the previous one.</summary>
    public SceneTransitionKind Transition { get; }

    /// <summary>Effective opening-transition length (seconds): 0 for the first
    /// shot and for <see cref="SceneTransitionKind.Cut"/>, otherwise the shot's
    /// authored <see cref="SceneShot.TransitionSeconds"/> clamped to its own
    /// duration.</summary>
    public double TransitionSeconds { get; }
}

/// <summary>The answer to "where are we at global time t?".</summary>
public readonly struct SceneSample
{
    public SceneSample(int currentEntry, int originalIndex, double localTime,
                       bool inTransition, int outgoingEntry, double blend,
                       SceneTransitionKind transitionKind)
    {
        CurrentEntry = currentEntry;
        OriginalIndex = originalIndex;
        LocalTime = localTime;
        InTransition = inTransition;
        OutgoingEntry = outgoingEntry;
        Blend = blend;
        TransitionKind = transitionKind;
    }

    /// <summary>Schedule position of the shot whose clock is authoritative
    /// (the incoming shot during a transition). -1 for an empty timeline.</summary>
    public int CurrentEntry { get; }

    /// <summary>The current shot's index into the source shots list. -1 empty.</summary>
    public int OriginalIndex { get; }

    /// <summary>Seconds since the current shot started (drives its camera /
    /// param animation clock).</summary>
    public double LocalTime { get; }

    /// <summary>True while inside the current shot's opening transition window.</summary>
    public bool InTransition { get; }

    /// <summary>Schedule position of the outgoing shot being blended out, or -1
    /// when not in a transition.</summary>
    public int OutgoingEntry { get; }

    /// <summary>0 = fully the outgoing shot, 1 = fully the current shot. Always 1
    /// when not in a transition (the current shot is fully shown).</summary>
    public double Blend { get; }

    /// <summary>The active transition kind (only meaningful while
    /// <see cref="InTransition"/>).</summary>
    public SceneTransitionKind TransitionKind { get; }

    /// <summary>The empty-timeline sample.</summary>
    public static SceneSample None => new(-1, -1, 0, false, -1, 1, SceneTransitionKind.Cut);
}

/// <summary>Deterministic playback schedule built from a <see cref="SceneData"/>.</summary>
public sealed class SceneTimeline
{
    private readonly SceneScheduleEntry[] _entries;

    private SceneTimeline(SceneScheduleEntry[] entries, double total)
    {
        _entries = entries;
        TotalDuration = total;
    }

    /// <summary>Shots that actually play (zero/negative-duration shots dropped),
    /// in play order.</summary>
    public IReadOnlyList<SceneScheduleEntry> Entries => _entries;

    /// <summary>Sum of the playable shots' durations (seconds).</summary>
    public double TotalDuration { get; }

    /// <summary>True when no shot has a positive duration.</summary>
    public bool IsEmpty => _entries.Length == 0;

    /// <summary>Build the schedule. Shots with a non-positive duration are
    /// skipped (they can't occupy time). The first playable shot always starts
    /// at 0 with no opening transition.</summary>
    public static SceneTimeline Build(SceneData scene)
    {
        if (scene?.Shots == null || scene.Shots.Count == 0)
            return new SceneTimeline(Array.Empty<SceneScheduleEntry>(), 0);

        var list = new List<SceneScheduleEntry>(scene.Shots.Count);
        double cursor = 0;
        bool first = true;
        for (int i = 0; i < scene.Shots.Count; i++)
        {
            var shot = scene.Shots[i];
            double dur = shot.DurationSeconds;
            if (dur <= 0) continue; // skip non-playing shots

            double te;
            if (first || shot.Transition == SceneTransitionKind.Cut)
                te = 0; // first shot and hard cuts have no blend window
            else
                te = global::System.Math.Max(0, global::System.Math.Min(shot.TransitionSeconds, dur));

            list.Add(new SceneScheduleEntry(i, cursor, dur, shot.Transition, te));
            cursor += dur;
            first = false;
        }

        return new SceneTimeline(list.ToArray(), cursor);
    }

    /// <summary>Sample the timeline at global time <paramref name="t"/> (seconds).
    /// Negative t clamps to the start; t at or past the end clamps to the final
    /// shot's last instant. Callers that loop should pass <c>t % TotalDuration</c>.</summary>
    public SceneSample Sample(double t)
    {
        if (_entries.Length == 0) return SceneSample.None;
        if (t < 0) t = 0;

        // Find the shot whose [Start, End) contains t. Linear scan — scene shot
        // counts are tiny (single digits).
        int idx = _entries.Length - 1;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (t < _entries[i].EndTime) { idx = i; break; }
        }

        ref readonly var e = ref _entries[idx];
        double local = t - e.StartTime;
        if (local < 0) local = 0;
        if (local > e.Duration) local = e.Duration;

        // Inside the opening transition window? (never for the first entry — its
        // TransitionSeconds is 0).
        if (idx > 0 && e.TransitionSeconds > 0 && local < e.TransitionSeconds)
        {
            double blend = local / e.TransitionSeconds; // 0 → 1
            return new SceneSample(idx, e.OriginalIndex, local,
                inTransition: true, outgoingEntry: idx - 1, blend: blend,
                transitionKind: e.Transition);
        }

        return new SceneSample(idx, e.OriginalIndex, local,
            inTransition: false, outgoingEntry: -1, blend: 1.0,
            transitionKind: e.Transition);
    }
}

/// <summary>Maps an authored transition kind to the concrete blend the current
/// build can render, and supplies the pure per-pixel weight the bespoke
/// visuals need. As of S8 every authored kind is honoured directly:
/// <list type="bullet">
/// <item>Cut — no composite (realtime + offline both hard-cut).</item>
/// <item>Crossfade — uniform alpha blend across the frame.</item>
/// <item>LightSweep — a directional (left→right) wipe; see
///   <see cref="LightSweepWeight"/>.</item>
/// <item>ParamMorph — the offline renderer interpolates the shots' fractal
///   params (<see cref="SceneParamMorph"/>) instead of compositing two frames;
///   it falls back to Crossfade at render time when the two shots are different
///   fractal types (nothing to morph).</item>
/// </list>
/// Only the ParamMorph type-mismatch fallback is decided at render time (it
/// needs the resolved shot types); everything else is honoured as authored.</summary>
public static class SceneTransitions
{
    /// <summary>Default soft-edge band width for the <see cref="LightSweepWeight"/>
    /// wipe, as a fraction of frame width. A wider feather = a softer sweep.</summary>
    public const double DefaultLightSweepFeather = 0.35;

    public static SceneTransitionKind ResolveVisual(SceneTransitionKind authored) => authored switch
    {
        SceneTransitionKind.Cut => SceneTransitionKind.Cut,
        SceneTransitionKind.Crossfade => SceneTransitionKind.Crossfade,
        SceneTransitionKind.LightSweep => SceneTransitionKind.LightSweep,
        SceneTransitionKind.ParamMorph => SceneTransitionKind.ParamMorph,
        _ => SceneTransitionKind.Crossfade,
    };

    /// <summary>Incoming-shot weight for a left→right light-sweep wipe at
    /// horizontal position <paramref name="u"/> (0 = left edge, 1 = right edge)
    /// and transition progress <paramref name="blend"/> (0 = fully outgoing,
    /// 1 = fully incoming). A soft edge of width <paramref name="feather"/>
    /// sweeps across the frame as blend rises, so at blend 0 every column is 0
    /// and at blend 1 every column is 1. Pure + monotonic in both args.</summary>
    public static double LightSweepWeight(double u, double blend, double feather = DefaultLightSweepFeather)
    {
        if (feather <= 0) feather = 1e-6;
        // Edge advances from off-frame-left to off-frame-right as blend: 0→1.
        double w = (blend * (1.0 + feather) - u) / feather;
        return w < 0 ? 0 : (w > 1 ? 1 : w);
    }
}
