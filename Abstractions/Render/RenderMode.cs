// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/RenderMode.cs
//
// Scene Engine Roadmap — Phase S0: the two-mode render split.
//
// Fracturing Fog serves two conflicting goals from one renderer: silky
// realtime preview on modest hardware, and maximum-fidelity offline output
// on capable hardware. A single render path cannot do both, so we name the
// split explicitly and let every later phase read one source of truth.
//
//   • Realtime preview  — governed, adaptive, holds a frame-time budget by
//     shedding quality (resolution, param count, effect stack). This is what
//     runs while a user authors / previews a Scene. Participates in the
//     ResourceGovernor (S1) so the 90% CPU ceiling is honoured.
//   • Offline render    — frame-locked. Every frame renders to completion,
//     decoupled from wall-clock; slower-than-realtime is expected. Pins the
//     deterministic CPU (double) path by default so exported MP4s are
//     reproducible (the GPU raymarch is float and not bit-identical — see
//     Performance-Roadmap P7 / Lighting-FX-Roadmap). Does NOT participate in
//     the CPU adaptive-quality throttle (it wants full fidelity), though the
//     memory backstop is unconditional and lives outside this policy.
//
// This phase ships the contract only — nothing consumes it yet, exactly like
// the Animation-Roadmap Phase 0 bus and the Performance-Roadmap P7
// infrastructure phase. S1 (governor), S2 (tiers), and S7 (offline scene
// render) are the consumers.

using System;

namespace FracturingFog.Render
{
    /// <summary>Which of the two render modes is active. See
    /// <see cref="RenderModePolicy"/> for the per-mode behaviour.</summary>
    public enum RenderMode
    {
        /// <summary>Governed, adaptive, frame-budgeted interactive preview.</summary>
        RealtimePreview,

        /// <summary>Frame-locked, full-fidelity, deterministic offline render
        /// (Scene export, video record).</summary>
        OfflineRender,
    }

    /// <summary>
    /// Immutable policy describing how a render should behave under a given
    /// <see cref="RenderMode"/>. Later phases read this — the governor (S1)
    /// checks <see cref="ParticipatesInGovernor"/> + <see cref="FrameTimeBudgetMs"/>;
    /// the calculator GPU-dispatch decision routes through
    /// <see cref="ResolveUseGpuRender"/>; the Scene export loop (S7) enters an
    /// offline scope so it never early-outs on a frame-time budget.
    /// </summary>
    public sealed record RenderModePolicy
    {
        /// <summary>The mode this policy describes.</summary>
        public RenderMode Mode { get; init; }

        /// <summary>Target wall-clock budget per frame in milliseconds, or
        /// <c>null</c> for a frame-locked render (no early-out, render every
        /// frame to completion). Realtime carries a budget the governor steers
        /// toward; offline is always <c>null</c>.</summary>
        public double? FrameTimeBudgetMs { get; init; }

        /// <summary>True to force the deterministic CPU (double-precision)
        /// calculator path and bypass the float GPU raymarch, so output is
        /// bit-reproducible across runs and machines. Offline pins this by
        /// default; the fast-GPU offline opt-in clears it.</summary>
        public bool PinDeterministicCpuPath { get; init; }

        /// <summary>True when this render should be throttled by the
        /// ResourceGovernor's adaptive quality feedback (S1). Realtime opts in;
        /// offline opts out (it wants full fidelity — the memory hard-cap is a
        /// separate, unconditional backstop).</summary>
        public bool ParticipatesInGovernor { get; init; }

        /// <summary>Convenience: a frame-locked render has no time budget.</summary>
        public bool IsFrameLocked => FrameTimeBudgetMs is null;

        /// <summary>Resolve whether the GPU raymarch may run given a caller's
        /// request and this policy. When the deterministic CPU path is pinned
        /// the GPU is refused regardless of the request; otherwise the request
        /// stands. This is the single decision point S7's offline loop and the
        /// calculator dispatch share so "deterministic export" never silently
        /// falls onto the float GPU path.</summary>
        public bool ResolveUseGpuRender(bool requested)
            => requested && !PinDeterministicCpuPath;

        /// <summary>Governed interactive preview. Default when no scope is
        /// active. 33.3 ms ≈ 30 fps target; S1's governor owns the live value,
        /// this is the pre-governor default.</summary>
        public static readonly RenderModePolicy Realtime = new()
        {
            Mode                    = RenderMode.RealtimePreview,
            FrameTimeBudgetMs       = 1000.0 / 30.0,
            PinDeterministicCpuPath = false,
            ParticipatesInGovernor  = true,
        };

        /// <summary>Frame-locked, deterministic offline render (the default
        /// export path). CPU path pinned for reproducibility.</summary>
        public static readonly RenderModePolicy Offline = new()
        {
            Mode                    = RenderMode.OfflineRender,
            FrameTimeBudgetMs       = null,
            PinDeterministicCpuPath = true,
            ParticipatesInGovernor  = false,
        };

        /// <summary>Frame-locked offline render that permits the float GPU
        /// raymarch — faster export on capable hardware at the cost of
        /// bit-reproducibility. The "fast GPU export (non-deterministic)"
        /// opt-in from the S0 roadmap.</summary>
        public static readonly RenderModePolicy OfflineFastGpu = new()
        {
            Mode                    = RenderMode.OfflineRender,
            FrameTimeBudgetMs       = null,
            PinDeterministicCpuPath = false,
            ParticipatesInGovernor  = false,
        };
    }

    /// <summary>
    /// Ambient, per-thread current render mode. Realtime render runs on the UI
    /// / render thread; offline export runs on its own background thread — so
    /// the current policy is <c>[ThreadStatic]</c> and each thread defaults to
    /// <see cref="RenderModePolicy.Realtime"/> until it enters a scope.
    ///
    /// Usage:
    /// <code>
    /// using (RenderModeScope.Enter(RenderModePolicy.Offline))
    /// {
    ///     // every frame rendered on this thread inside the block is offline
    /// }
    /// </code>
    /// Scopes nest — <see cref="Enter"/> restores the previous policy on
    /// dispose, so a nested scope cannot leak its policy to the outer one.
    ///
    /// The scope is <b>thread-affine</b>, not async-flow-captured: it does not
    /// follow <c>await</c> continuations across threads (deliberately — it is a
    /// <c>[ThreadStatic]</c>, not an <c>AsyncLocal</c>). Wrap a synchronous
    /// per-frame render loop on a single thread; do not <c>await</c> across the
    /// <c>using</c> block and expect <see cref="Current"/> to survive. The
    /// offline render loop (S7) runs synchronously on its own background thread,
    /// which is exactly this pattern.
    /// </summary>
    public static class RenderModeScope
    {
        [ThreadStatic] private static RenderModePolicy? _current;

        /// <summary>The policy in effect on the calling thread. Defaults to
        /// <see cref="RenderModePolicy.Realtime"/> when no scope is active.</summary>
        public static RenderModePolicy Current => _current ?? RenderModePolicy.Realtime;

        /// <summary>Enter a render-mode scope on the calling thread. Dispose
        /// the returned handle (via <c>using</c>) to restore the previous
        /// policy.</summary>
        public static IDisposable Enter(RenderModePolicy policy)
        {
            if (policy is null) throw new ArgumentNullException(nameof(policy));
            var restore = new Restorer(_current);
            _current = policy;
            return restore;
        }

        private sealed class Restorer : IDisposable
        {
            private readonly RenderModePolicy? _previous;
            private bool _disposed;

            public Restorer(RenderModePolicy? previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _current = _previous;
            }
        }
    }
}
