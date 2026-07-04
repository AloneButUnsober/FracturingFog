// Abstractions/Animation/SceneRenderPlan.cs
//
// Scene Engine Roadmap — Phase S7: the pure, deterministic *frame plan* for an
// offline (frame-locked) scene render. Where SceneTimeline answers "at global
// time t, which shot is playing?", SceneRenderPlan turns a whole scene into the
// exact list of output frames an encoder must emit at a fixed fps, and — for
// each output frame — the set of sub-frame sample times an accumulation motion
// blur must render and average.
//
// Two things S6 deferred to the offline path land here as pure data:
//
//   1. Accumulation motion blur. Realtime playback renders one live frame per
//      tick; offline can afford to render N sub-frames per output frame at
//      sub-tick times across an open-shutter window and average them, which is
//      the only place camera / param motion blur is affordable (S6 note:
//      "only viable in offline mode"). This class schedules those sub-frame
//      times + weights; the renderer does the rendering + averaging.
//
//   2. Frame-composited transitions. Realtime cuts between shots because
//      compositing two live frames breaches the CPU/mem cap; offline renders
//      sub-frames anyway, so a crossfade is just "blend the frozen last frame
//      of the outgoing shot into the accumulated incoming frame by the
//      timeline's blend factor". This class flags which output frames composite
//      and supplies the outgoing shot + blend; the renderer does the pixels.
//      (SceneTimeline already computes the blend — "no re-work, just a
//      consumer", per the S6 roadmap note.)
//
// Pure + allocation-light + unit-tested. No UI, no render, no clock, no I/O.

using System;
using System.Collections.Generic;

namespace FracturingFog.Abstractions.Animation;

/// <summary>Knobs for an offline scene render. Frame rate plus the accumulation
/// motion-blur controls. Clamped to sane ranges by
/// <see cref="SceneRenderPlan.Build"/> — construct freely and let Build
/// normalise.</summary>
public sealed class SceneRenderSettings
{
    /// <summary>Output frame rate. Clamped to &gt;= 1.</summary>
    public int Fps { get; set; } = 30;

    /// <summary>Number of sub-frames rendered + averaged per output frame. 1 =
    /// no motion blur (a single sample). Higher = smoother camera / param
    /// motion blur at N× the render cost. Clamped to &gt;= 1.</summary>
    public int MotionBlurSubframes { get; set; } = 1;

    /// <summary>Fraction of the output-frame interval the shutter is open, i.e.
    /// how far across the frame the sub-frames are spread. 0.5 ≈ a 180° shutter
    /// (the film default); 1.0 spreads samples across the whole frame interval.
    /// Clamped to (0, 1].</summary>
    public double ShutterFraction { get; set; } = 0.5;
}

/// <summary>One motion-blur sub-sample of an output frame: a global time to
/// render at, its averaging weight, and the shot that time lands in.</summary>
public readonly struct SceneSubFrame
{
    public SceneSubFrame(double globalTime, double weight, int originalIndex, double localTime)
    {
        GlobalTime = globalTime;
        Weight = weight;
        OriginalIndex = originalIndex;
        LocalTime = localTime;
    }

    /// <summary>Global scene time (seconds) this sub-frame renders at.</summary>
    public double GlobalTime { get; }

    /// <summary>Averaging weight — the sub-frames of one output frame sum to 1
    /// (uniform box filter: <c>1 / MotionBlurSubframes</c>).</summary>
    public double Weight { get; }

    /// <summary>Source-shot index (into <see cref="SceneData.Shots"/>) this
    /// sub-frame lands in — its camera / param clock reads <see cref="LocalTime"/>.
    /// -1 only for an empty timeline.</summary>
    public int OriginalIndex { get; }

    /// <summary>Seconds since that shot started — drives the shot's camera +
    /// param animation for this sub-frame.</summary>
    public double LocalTime { get; }
}

/// <summary>One output frame: the sub-frames to render + average, plus the
/// optional cross-fade composite that brings in the incoming shot.</summary>
public readonly struct SceneRenderFrame
{
    public SceneRenderFrame(int index, double frameStart, double frameCenterTime,
                            SceneSubFrame[] subFrames, int primaryOriginalIndex,
                            bool compositeTransition, int outgoingOriginalIndex,
                            double outgoingLocalTime, double blend,
                            SceneTransitionKind resolvedTransition)
    {
        Index = index;
        FrameStart = frameStart;
        FrameCenterTime = frameCenterTime;
        SubFrames = subFrames;
        PrimaryOriginalIndex = primaryOriginalIndex;
        CompositeTransition = compositeTransition;
        OutgoingOriginalIndex = outgoingOriginalIndex;
        OutgoingLocalTime = outgoingLocalTime;
        Blend = blend;
        ResolvedTransition = resolvedTransition;
    }

    /// <summary>Zero-based output-frame number.</summary>
    public int Index { get; }

    /// <summary>Global time of the frame's leading edge (seconds).</summary>
    public double FrameStart { get; }

    /// <summary>Global time at the frame's midpoint — the sample used to resolve
    /// the transition composite for this frame.</summary>
    public double FrameCenterTime { get; }

    /// <summary>The motion-blur sub-frames to render and average (length =
    /// <see cref="SceneRenderSettings.MotionBlurSubframes"/>).</summary>
    public SceneSubFrame[] SubFrames { get; }

    /// <summary>Source-shot index of the incoming (authoritative) shot at the
    /// frame midpoint. -1 for an empty timeline.</summary>
    public int PrimaryOriginalIndex { get; }

    /// <summary>True when this frame sits inside a resolvable opening-transition
    /// window: the renderer blends the frozen last frame of
    /// <see cref="OutgoingOriginalIndex"/> into the accumulated incoming frame.
    /// False for hard cuts and steady-state frames.</summary>
    public bool CompositeTransition { get; }

    /// <summary>Source-shot index of the outgoing shot being blended out, or -1
    /// when <see cref="CompositeTransition"/> is false.</summary>
    public int OutgoingOriginalIndex { get; }

    /// <summary>The outgoing shot's frozen local time — its full duration, i.e.
    /// its final frame. (Realtime freezes the outgoing frame to stay in the
    /// resource cap; offline mirrors that framing.)</summary>
    public double OutgoingLocalTime { get; }

    /// <summary>Cross-fade weight: 0 = fully the outgoing (frozen) frame, 1 =
    /// fully the incoming accumulated frame. Only meaningful when
    /// <see cref="CompositeTransition"/> is true.</summary>
    public double Blend { get; }

    /// <summary>The transition kind actually rendered (post
    /// <see cref="SceneTransitions.ResolveVisual"/>): Crossfade for every
    /// composited frame today. <see cref="SceneTransitionKind.Cut"/> when the
    /// frame does not composite.</summary>
    public SceneTransitionKind ResolvedTransition { get; }
}

/// <summary>The full frame schedule for an offline scene render, built from a
/// <see cref="SceneData"/> and <see cref="SceneRenderSettings"/>.</summary>
public sealed class SceneRenderPlan
{
    private readonly SceneRenderFrame[] _frames;

    private SceneRenderPlan(SceneRenderFrame[] frames, int fps, double duration)
    {
        _frames = frames;
        Fps = fps;
        Duration = duration;
    }

    /// <summary>The output frames in emit order.</summary>
    public IReadOnlyList<SceneRenderFrame> Frames => _frames;

    /// <summary>Total output frames (== <c>Frames.Count</c>).</summary>
    public int TotalFrames => _frames.Length;

    /// <summary>Normalised output frame rate.</summary>
    public int Fps { get; }

    /// <summary>Total scene duration in seconds (the timeline's total).</summary>
    public double Duration { get; }

    /// <summary>True when the scene has no playable frames.</summary>
    public bool IsEmpty => _frames.Length == 0;

    /// <summary>Build the frame plan. Clamps <paramref name="settings"/> to sane
    /// ranges (fps &gt;= 1, subframes &gt;= 1, shutter in (0,1]). An empty /
    /// zero-duration scene yields an empty plan.</summary>
    public static SceneRenderPlan Build(SceneData scene, SceneRenderSettings settings)
    {
        settings ??= new SceneRenderSettings();

        int fps = settings.Fps < 1 ? 1 : settings.Fps;
        int sub = settings.MotionBlurSubframes < 1 ? 1 : settings.MotionBlurSubframes;
        double shutter = settings.ShutterFraction;
        if (double.IsNaN(shutter) || shutter <= 0) shutter = 1e-6;
        if (shutter > 1.0) shutter = 1.0;

        var timeline = SceneTimeline.Build(scene);
        if (timeline.IsEmpty)
            return new SceneRenderPlan(Array.Empty<SceneRenderFrame>(), fps, 0);

        double total = timeline.TotalDuration;
        double frameDur = 1.0 / fps;
        double shutterDur = frameDur * shutter;

        // ceil(total * fps): the final partial frame still gets emitted so the
        // last shot's tail is not truncated. Guard the float edge so an exact
        // multiple doesn't add a spurious trailing frame.
        int frameCount = (int)global::System.Math.Ceiling(total * fps - 1e-9);
        if (frameCount < 1) frameCount = 1;

        var frames = new SceneRenderFrame[frameCount];
        double weight = 1.0 / sub;

        for (int f = 0; f < frameCount; f++)
        {
            double frameStart = f * frameDur;

            var subs = new SceneSubFrame[sub];
            for (int k = 0; k < sub; k++)
            {
                // Sub-sample centres spread evenly across the open-shutter
                // window at the frame's leading edge.
                double subTime = frameStart + (k + 0.5) / sub * shutterDur;
                double clamped = subTime < 0 ? 0 : (subTime > total ? total : subTime);
                var s = timeline.Sample(clamped);
                subs[k] = new SceneSubFrame(subTime, weight, s.OriginalIndex, s.LocalTime);
            }

            // Transition resolved at the frame midpoint (a stable, shutter-
            // independent choice — the blend factor is a per-frame decision,
            // not a per-sub-sample one).
            double centerTime = frameStart + frameDur * 0.5;
            double centerClamped = centerTime > total ? total : centerTime;
            var center = timeline.Sample(centerClamped);

            bool composite = false;
            int outgoingOriginal = -1;
            double outgoingLocal = 0;
            double blend = 1.0;
            var resolved = SceneTransitionKind.Cut;

            if (center.InTransition && center.OutgoingEntry >= 0
                && center.OutgoingEntry < timeline.Entries.Count)
            {
                var visual = SceneTransitions.ResolveVisual(center.TransitionKind);
                if (visual != SceneTransitionKind.Cut)
                {
                    var outgoing = timeline.Entries[center.OutgoingEntry];
                    composite = true;
                    outgoingOriginal = outgoing.OriginalIndex;
                    outgoingLocal = outgoing.Duration; // frozen final frame
                    blend = center.Blend;
                    resolved = visual;
                }
            }

            frames[f] = new SceneRenderFrame(
                f, frameStart, centerTime, subs, center.OriginalIndex,
                composite, outgoingOriginal, outgoingLocal, blend, resolved);
        }

        return new SceneRenderPlan(frames, fps, total);
    }
}
