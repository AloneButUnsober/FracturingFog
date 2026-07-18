// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Scene Engine Roadmap Phase S6: the deterministic playback schedule
/// (SceneTimeline). Covers back-to-back sequencing, zero-duration skipping,
/// first-shot / cut transition suppression, mid-shot vs in-transition sampling,
/// and the visual transition fallback.
/// </summary>
public sealed class SceneTimelineTests
{
    private static SceneShot Shot(double dur, SceneTransitionKind kind = SceneTransitionKind.Cut,
                                  double te = 1.0, FractalType type = FractalType.Mandelbrot)
        => new() { FractalType = type, DurationSeconds = dur, Transition = kind, TransitionSeconds = te };

    [Fact]
    public void Build_sequences_shots_back_to_back_and_totals_duration()
    {
        var scene = new SceneData
        {
            Shots = new List<SceneShot> { Shot(5), Shot(3), Shot(2) },
        };

        var tl = SceneTimeline.Build(scene);

        Assert.Equal(3, tl.Entries.Count);
        Assert.Equal(10.0, tl.TotalDuration, precision: 9);
        Assert.Equal(0.0, tl.Entries[0].StartTime, precision: 9);
        Assert.Equal(5.0, tl.Entries[1].StartTime, precision: 9);
        Assert.Equal(8.0, tl.Entries[2].StartTime, precision: 9);
        Assert.Equal(10.0, tl.Entries[2].EndTime, precision: 9);
    }

    [Fact]
    public void Build_skips_non_positive_durations_but_keeps_original_index()
    {
        var scene = new SceneData
        {
            Shots = new List<SceneShot> { Shot(4), Shot(0), Shot(-1), Shot(6) },
        };

        var tl = SceneTimeline.Build(scene);

        Assert.Equal(2, tl.Entries.Count);
        Assert.Equal(0, tl.Entries[0].OriginalIndex);
        Assert.Equal(3, tl.Entries[1].OriginalIndex); // the 4th shot, not the 2nd
        Assert.Equal(10.0, tl.TotalDuration, precision: 9);
    }

    [Fact]
    public void First_shot_and_cut_have_no_transition_window()
    {
        var scene = new SceneData
        {
            Shots = new List<SceneShot>
            {
                Shot(5, SceneTransitionKind.Crossfade, te: 2.0), // first → suppressed
                Shot(5, SceneTransitionKind.Cut, te: 2.0),       // cut → suppressed
                Shot(5, SceneTransitionKind.Crossfade, te: 2.0), // real window
            },
        };

        var tl = SceneTimeline.Build(scene);

        Assert.Equal(0.0, tl.Entries[0].TransitionSeconds, precision: 9);
        Assert.Equal(0.0, tl.Entries[1].TransitionSeconds, precision: 9);
        Assert.Equal(2.0, tl.Entries[2].TransitionSeconds, precision: 9);
    }

    [Fact]
    public void Transition_seconds_clamp_to_shot_duration()
    {
        var scene = new SceneData
        {
            Shots = new List<SceneShot>
            {
                Shot(5),
                Shot(1.5, SceneTransitionKind.Crossfade, te: 10.0), // longer than the shot
            },
        };

        var tl = SceneTimeline.Build(scene);
        Assert.Equal(1.5, tl.Entries[1].TransitionSeconds, precision: 9);
    }

    [Fact]
    public void Sample_midshot_reports_index_local_time_and_no_transition()
    {
        var scene = new SceneData { Shots = new List<SceneShot> { Shot(5), Shot(5) } };
        var tl = SceneTimeline.Build(scene);

        var s = tl.Sample(6.5); // 1.5s into shot 2
        Assert.Equal(1, s.CurrentEntry);
        Assert.Equal(1, s.OriginalIndex);
        Assert.Equal(1.5, s.LocalTime, precision: 9);
        Assert.False(s.InTransition);
        Assert.Equal(1.0, s.Blend, precision: 9);
    }

    [Fact]
    public void Sample_inside_transition_window_blends_outgoing_into_incoming()
    {
        var scene = new SceneData
        {
            Shots = new List<SceneShot>
            {
                Shot(5),
                Shot(5, SceneTransitionKind.Crossfade, te: 2.0),
            },
        };
        var tl = SceneTimeline.Build(scene);

        // 0.5s into shot 2's 2s crossfade → blend 0.25, outgoing = shot 1.
        var s = tl.Sample(5.5);
        Assert.True(s.InTransition);
        Assert.Equal(1, s.CurrentEntry);
        Assert.Equal(0, s.OutgoingEntry);
        Assert.Equal(0.25, s.Blend, precision: 9);
        Assert.Equal(SceneTransitionKind.Crossfade, s.TransitionKind);

        // Past the window (3s in) → fully the incoming shot.
        var after = tl.Sample(8.0);
        Assert.False(after.InTransition);
        Assert.Equal(1.0, after.Blend, precision: 9);
    }

    [Fact]
    public void Sample_clamps_out_of_range_times()
    {
        var scene = new SceneData { Shots = new List<SceneShot> { Shot(4), Shot(4) } };
        var tl = SceneTimeline.Build(scene);

        Assert.Equal(0, tl.Sample(-3).CurrentEntry);           // before start
        Assert.Equal(1, tl.Sample(99).CurrentEntry);           // past end → last shot
    }

    [Fact]
    public void Empty_scene_builds_empty_timeline()
    {
        var tl = SceneTimeline.Build(new SceneData());
        Assert.True(tl.IsEmpty);
        Assert.Equal(0.0, tl.TotalDuration, precision: 9);
        Assert.Equal(-1, tl.Sample(1.0).CurrentEntry);
    }

    [Fact]
    public void ResolveVisual_honours_every_authored_kind()
    {
        // S8 — all four are rendered as authored (ParamMorph's same-type guard
        // is a render-time decision, not a ResolveVisual fallback).
        Assert.Equal(SceneTransitionKind.Cut, SceneTransitions.ResolveVisual(SceneTransitionKind.Cut));
        Assert.Equal(SceneTransitionKind.Crossfade, SceneTransitions.ResolveVisual(SceneTransitionKind.Crossfade));
        Assert.Equal(SceneTransitionKind.LightSweep, SceneTransitions.ResolveVisual(SceneTransitionKind.LightSweep));
        Assert.Equal(SceneTransitionKind.ParamMorph, SceneTransitions.ResolveVisual(SceneTransitionKind.ParamMorph));
    }
}
