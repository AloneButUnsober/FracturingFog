// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/Lighting/StagePerf.cs
//
// Static hook used by ScreenSpacePost.Apply* to publish per-pass elapsed-ms
// samples into whatever sink the host wires up (PerfStats today, possibly
// an ETW source or a CSV later). Decoupled via a delegate so the post-pass
// module stays free of FractalRenderHost references — keeps the
// Engine.Rendering.Lighting / Engine.Rendering dependency direction clean.
//
// Usage at each Apply* method body:
//
//     using var __ = StagePerf.Begin(PostStage.Ssao);
//     // body
//
// Cost: Stopwatch.GetTimestamp() bracket + one delegate invocation. ~50 ns
// when StageTimingPublisher is non-null, ~5 ns when null (StagePerf.Begin
// short-circuits and Dispose is a no-op). Safe to leave wired in Release.

using System;
using System.Diagnostics;

namespace FracturingFog.Rendering.Lighting;

/// <summary>
/// Post-pass stages the in-app perf HUD breaks out separately. Volume is
/// emitted from inside the raymarch hot loop (ShadingPipeline) rather than
/// a top-level Apply call — same hook so the HUD aggregates uniformly.
/// </summary>
public enum PostStage
{
    Ssao,
    Bloom,
    Dof,
    Edge,
    Lens,
    Volume,
}

/// <summary>
/// Static publisher hook. Host wires this once during construction; null
/// means "no sink" and Begin/Dispose costs collapse to a single null check.
/// </summary>
public static class StagePerf
{
    public static Action<PostStage, double>? Publisher;

    /// <summary>
    /// Start a timing scope. Result must be disposed at the end of the
    /// pass — the recommended use is `using var __ = StagePerf.Begin(...)`
    /// so the JIT emits a clean try/finally around the body.
    /// </summary>
    public static StageScope Begin(PostStage stage)
        => Publisher is null ? default : new StageScope(stage, Stopwatch.GetTimestamp());
}

/// <summary>
/// Stack-allocated timing scope. Records elapsed ms via
/// <see cref="StagePerf.Publisher"/> on dispose.
/// </summary>
public ref struct StageScope
{
    private readonly PostStage _stage;
    private readonly long _startTicks;
    private bool _active;

    internal StageScope(PostStage stage, long startTicks)
    {
        _stage = stage;
        _startTicks = startTicks;
        _active = true;
    }

    public void Dispose()
    {
        if (!_active) return;
        _active = false;
        var pub = StagePerf.Publisher;
        if (pub is null) return;
        double ms = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
        pub(_stage, ms);
    }
}
