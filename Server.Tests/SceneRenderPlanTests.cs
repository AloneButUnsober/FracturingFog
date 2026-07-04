using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Scene Engine Roadmap Phase S7: the pure offline frame plan (SceneRenderPlan).
/// Covers frame counting, motion-blur sub-frame scheduling (count / weights /
/// shutter window), sub-frame shot mapping, cross-fade transition compositing +
/// blend, cut suppression, the LightSweep/ParamMorph → crossfade fallback, the
/// empty scene, and settings clamping.
/// </summary>
public sealed class SceneRenderPlanTests
{
    private static SceneShot Shot(double dur, SceneTransitionKind kind = SceneTransitionKind.Cut,
                                  double te = 1.0, FractalType type = FractalType.Mandelbrot)
        => new() { FractalType = type, DurationSeconds = dur, Transition = kind, TransitionSeconds = te };

    private static SceneData Scene(params SceneShot[] shots)
        => new() { Shots = new List<SceneShot>(shots) };

    [Fact]
    public void Frame_count_is_ceiling_of_duration_times_fps()
    {
        var plan = SceneRenderPlan.Build(
            Scene(Shot(2), Shot(3)),                       // 5s total
            new SceneRenderSettings { Fps = 30, MotionBlurSubframes = 1 });

        Assert.Equal(150, plan.TotalFrames);
        Assert.Equal(30, plan.Fps);
        Assert.Equal(5.0, plan.Duration, precision: 9);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public void Partial_trailing_frame_is_still_emitted()
    {
        // 1.05s @ 10fps = 10.5 frames → 11 emitted (last shot tail not truncated).
        var plan = SceneRenderPlan.Build(
            Scene(Shot(1.05)),
            new SceneRenderSettings { Fps = 10, MotionBlurSubframes = 1 });

        Assert.Equal(11, plan.TotalFrames);
    }

    [Fact]
    public void Exact_multiple_does_not_add_a_spurious_trailing_frame()
    {
        var plan = SceneRenderPlan.Build(
            Scene(Shot(2)),                                // exactly 60 frames @ 30
            new SceneRenderSettings { Fps = 30, MotionBlurSubframes = 1 });

        Assert.Equal(60, plan.TotalFrames);
    }

    [Fact]
    public void Subframe_count_matches_setting_and_weights_sum_to_one()
    {
        var plan = SceneRenderPlan.Build(
            Scene(Shot(1)),
            new SceneRenderSettings { Fps = 24, MotionBlurSubframes = 8 });

        foreach (var frame in plan.Frames)
        {
            Assert.Equal(8, frame.SubFrames.Length);
            Assert.Equal(1.0, frame.SubFrames.Sum(s => s.Weight), precision: 9);
            // Ascending sub-sample times.
            for (int k = 1; k < frame.SubFrames.Length; k++)
                Assert.True(frame.SubFrames[k].GlobalTime > frame.SubFrames[k - 1].GlobalTime);
        }
    }

    [Fact]
    public void Single_subframe_has_full_weight()
    {
        var plan = SceneRenderPlan.Build(
            Scene(Shot(1)),
            new SceneRenderSettings { Fps = 30, MotionBlurSubframes = 1 });

        var first = plan.Frames[0];
        Assert.Single(first.SubFrames);
        Assert.Equal(1.0, first.SubFrames[0].Weight, precision: 9);
    }

    [Fact]
    public void Subframes_stay_inside_the_open_shutter_window()
    {
        int fps = 20;
        double shutter = 0.5;
        var plan = SceneRenderPlan.Build(
            Scene(Shot(2)),
            new SceneRenderSettings { Fps = fps, MotionBlurSubframes = 4, ShutterFraction = shutter });

        double frameDur = 1.0 / fps;
        foreach (var frame in plan.Frames)
        {
            foreach (var s in frame.SubFrames)
            {
                Assert.True(s.GlobalTime >= frame.FrameStart - 1e-9);
                Assert.True(s.GlobalTime <= frame.FrameStart + frameDur * shutter + 1e-9);
            }
        }
    }

    [Fact]
    public void Subframes_map_to_the_shot_they_land_in()
    {
        // shot0 [0,2), shot1 [2,5) — hard cut so no blend confusion.
        var plan = SceneRenderPlan.Build(
            Scene(Shot(2), Shot(3, SceneTransitionKind.Cut)),
            new SceneRenderSettings { Fps = 10, MotionBlurSubframes = 1 });

        // Frame 5 leading edge = 0.5s → shot 0.
        Assert.Equal(0, plan.Frames[5].SubFrames[0].OriginalIndex);
        // Frame 30 leading edge = 3.0s → shot 1, local ~1.0s.
        Assert.Equal(1, plan.Frames[30].SubFrames[0].OriginalIndex);
        Assert.True(plan.Frames[30].SubFrames[0].LocalTime > 0.9);
    }

    [Fact]
    public void Crossfade_frames_composite_the_outgoing_shot_with_rising_blend()
    {
        // shot0 [0,4), shot1 [4,8) crossfade over 2s → transition window [4,6).
        var plan = SceneRenderPlan.Build(
            Scene(Shot(4), Shot(4, SceneTransitionKind.Crossfade, te: 2.0)),
            new SceneRenderSettings { Fps = 10, MotionBlurSubframes = 1 });

        // Frame at center ~4.05s (frame 40) is early in the window: composites,
        // outgoing = shot 0, small blend.
        var early = plan.Frames[40];
        Assert.True(early.CompositeTransition);
        Assert.Equal(0, early.OutgoingOriginalIndex);
        Assert.Equal(SceneTransitionKind.Crossfade, early.ResolvedTransition);
        Assert.True(early.Blend >= 0 && early.Blend < 0.2);
        Assert.Equal(4.0, early.OutgoingLocalTime, precision: 9); // frozen final frame

        // Frame near the end of the window has a higher blend.
        var late = plan.Frames[59]; // center ~5.95s
        Assert.True(late.CompositeTransition);
        Assert.True(late.Blend > early.Blend);

        // Past the window (frame 65, center ~6.55s) — steady state, no composite.
        var after = plan.Frames[65];
        Assert.False(after.CompositeTransition);
        Assert.Equal(-1, after.OutgoingOriginalIndex);
    }

    [Fact]
    public void Cut_transition_never_composites()
    {
        var plan = SceneRenderPlan.Build(
            Scene(Shot(2), Shot(2, SceneTransitionKind.Cut, te: 1.0)),
            new SceneRenderSettings { Fps = 10, MotionBlurSubframes = 1 });

        Assert.All(plan.Frames, f => Assert.False(f.CompositeTransition));
    }

    [Fact]
    public void LightSweep_and_ParamMorph_fall_back_to_crossfade_composite()
    {
        foreach (var kind in new[] { SceneTransitionKind.LightSweep, SceneTransitionKind.ParamMorph })
        {
            var plan = SceneRenderPlan.Build(
                Scene(Shot(4), Shot(4, kind, te: 2.0)),
                new SceneRenderSettings { Fps = 10, MotionBlurSubframes = 1 });

            var inWindow = plan.Frames[45]; // center ~4.55s, inside [4,6)
            Assert.True(inWindow.CompositeTransition);
            Assert.Equal(SceneTransitionKind.Crossfade, inWindow.ResolvedTransition);
        }
    }

    [Fact]
    public void Empty_scene_yields_an_empty_plan()
    {
        var plan = SceneRenderPlan.Build(
            Scene(Shot(0), Shot(-1)),
            new SceneRenderSettings { Fps = 30 });

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.TotalFrames);
    }

    [Fact]
    public void Settings_are_clamped_to_sane_ranges()
    {
        var plan = SceneRenderPlan.Build(
            Scene(Shot(1)),
            new SceneRenderSettings { Fps = 0, MotionBlurSubframes = -3, ShutterFraction = 5.0 });

        Assert.Equal(1, plan.Fps);                       // fps clamped to >= 1
        Assert.All(plan.Frames, f => Assert.Single(f.SubFrames)); // subframes clamped to >= 1
        // Shutter clamped to 1.0 → the single sub-sample sits at the frame's
        // half-second mark (0.5 into a 1s frame).
        Assert.Equal(0.5, plan.Frames[0].SubFrames[0].GlobalTime, precision: 9);
    }
}
