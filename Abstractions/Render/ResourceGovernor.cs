// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/ResourceGovernor.cs
//
// Scene Engine Roadmap — Phase S1: the resource governor (the 90% cap).
//
// The user's hard sub-goal: never let Fracturing Fog take more than ~90% of
// total CPU or memory, so a heavy render never starves the host / UI thread.
// "Never reach 90%" means we must start easing BEFORE the ceiling, not act at
// it — so the governor has a soft target (start shedding quality) below a hard
// ceiling (breach flag the OS backstop reads).
//
// Two mechanisms, per the roadmap:
//   1. Managed adaptive governor (this file) — a cross-platform control loop
//      that watches CPU% + memory and produces a quality scale [floor..1] the
//      hardware-tier layer (S2) maps onto concrete knobs, plus a "shed caches"
//      signal fired at a soft memory watermark before quality is touched. This
//      is the PRIMARY mechanism and it is pure + deterministic + unit-tested.
//   2. OS hard backstop (IResourceCapBackstop) — a Windows Job Object memory /
//      CPU-rate cap as a last resort. Windows-only, injected. The default here
//      is a no-op; the real Win32 implementation lives in the host project
//      (it P/Invokes and can kill the process, so it is not shipped as an
//      unverifiable default).
//
// Design rule: offline renders (RenderModePolicy.ParticipatesInGovernor ==
// false) are NOT quality-throttled — they want full fidelity. But the memory
// shed signal is unconditional: dropping a cache never changes output, and the
// point is to not crash the host.
//
// Ships behind current behaviour — no periodic driver wires this yet; S2 owns
// the sample→evaluate→apply-knobs loop. Mirrors the S0 / Animation-Phase-0
// "infrastructure lands first, consumer follows" pattern.

using System;
using System.Diagnostics;

namespace FracturingFog.Render
{
    /// <summary>One snapshot of resource pressure. <see cref="CpuPercent"/> is
    /// 0..100 across all cores (100 = every core saturated by this process);
    /// <see cref="MemoryFraction"/> is 0..1 of the memory available to the
    /// process.</summary>
    public readonly record struct ResourceSample(double CpuPercent, double MemoryFraction);

    /// <summary>Produces <see cref="ResourceSample"/>s. The real impl reads
    /// process counters; tests feed a fake.</summary>
    public interface IResourceSampler
    {
        ResourceSample Sample();
    }

    /// <summary>Immutable governor tuning. Defaults keep the process under the
    /// 90% ceiling by acting at an 85% / 0.80 soft target and only relaxing
    /// once pressure falls into the recover band — the gap between the soft
    /// target and the recover-below threshold is the anti-oscillation
    /// hysteresis band.</summary>
    public sealed record ResourceGovernorConfig
    {
        /// <summary>Start reducing quality when CPU meets/exceeds this.</summary>
        public double CpuSoftTargetPercent { get; init; } = 85.0;

        /// <summary>Hard ceiling — sets <see cref="GovernorDecision.HardCapBreached"/>
        /// for the OS backstop. The soft target should keep us off this.</summary>
        public double CpuCeilingPercent { get; init; } = 90.0;

        /// <summary>Only relax (step quality back up) once CPU is below this.
        /// Must be &lt; <see cref="CpuSoftTargetPercent"/> to form a hysteresis
        /// band.</summary>
        public double CpuRecoverBelowPercent { get; init; } = 75.0;

        /// <summary>Shed caches (and start reducing quality) at this memory
        /// fraction — below the hard ceiling.</summary>
        public double MemorySoftWatermark { get; init; } = 0.80;

        /// <summary>Hard memory ceiling — sets <see cref="GovernorDecision.HardCapBreached"/>.</summary>
        public double MemoryCeilingFraction { get; init; } = 0.90;

        /// <summary>Only relax once memory is below this.</summary>
        public double MemoryRecoverBelowFraction { get; init; } = 0.70;

        /// <summary>Lowest quality scale the governor will drop to.</summary>
        public double QualityFloor { get; init; } = 0.25;

        /// <summary>Quality scale removed per tick under pressure.</summary>
        public double StepDown { get; init; } = 0.10;

        /// <summary>Quality scale restored per tick during recovery.</summary>
        public double StepUp { get; init; } = 0.05;

        /// <summary>Consecutive calm ticks required before quality steps back
        /// up — makes recovery slower than throttling so the render doesn't
        /// flip-flop.</summary>
        public int RecoverHoldTicks { get; init; } = 6;
    }

    /// <summary>The governor's per-tick output. <see cref="QualityScale"/> is
    /// 1.0 (full) down to the configured floor; S2 maps it onto resolution
    /// scale / effect stack / animated-param ceiling.</summary>
    public readonly record struct GovernorDecision(
        double QualityScale,
        bool ThrottleActive,
        bool ShedCaches,
        bool HardCapBreached);

    /// <summary>
    /// Pure, deterministic adaptive control loop. Feed it a
    /// <see cref="ResourceSample"/> per tick via <see cref="Evaluate"/>; it
    /// ratchets an internal quality scale down under pressure and back up
    /// during sustained calm. No timers, no threads, no I/O — the periodic
    /// driver + knob application live in S2.
    /// </summary>
    public sealed class ResourceGovernor
    {
        private readonly ResourceGovernorConfig _cfg;
        private double _quality = 1.0;
        private int _calmTicks;

        public ResourceGovernor(ResourceGovernorConfig? config = null)
            => _cfg = config ?? new ResourceGovernorConfig();

        /// <summary>Current quality scale [floor..1].</summary>
        public double QualityScale => _quality;

        /// <summary>
        /// Advance the control loop one tick.
        /// </summary>
        /// <param name="sample">Latest resource pressure.</param>
        /// <param name="participatesInGovernor">
        /// From <see cref="RenderModePolicy.ParticipatesInGovernor"/> — realtime
        /// true (quality is throttled), offline false (quality frozen at its
        /// current value; only the unconditional cache-shed signal applies).
        /// </param>
        public GovernorDecision Evaluate(in ResourceSample sample, bool participatesInGovernor)
        {
            bool shed = sample.MemoryFraction >= _cfg.MemorySoftWatermark;
            bool hardBreach = sample.CpuPercent >= _cfg.CpuCeilingPercent
                           || sample.MemoryFraction >= _cfg.MemoryCeilingFraction;

            if (participatesInGovernor)
            {
                bool pressure = sample.CpuPercent >= _cfg.CpuSoftTargetPercent
                             || sample.MemoryFraction >= _cfg.MemorySoftWatermark;
                bool calm = sample.CpuPercent < _cfg.CpuRecoverBelowPercent
                         && sample.MemoryFraction < _cfg.MemoryRecoverBelowFraction;

                if (pressure)
                {
                    _quality = Math.Max(_cfg.QualityFloor, _quality - _cfg.StepDown);
                    _calmTicks = 0;
                }
                else if (calm)
                {
                    if (++_calmTicks >= _cfg.RecoverHoldTicks)
                    {
                        _quality = Math.Min(1.0, _quality + _cfg.StepUp);
                        _calmTicks = 0;
                    }
                }
                else
                {
                    // In the hysteresis band: hold quality, and reset the
                    // recover counter so a brief calm dip doesn't accumulate
                    // toward a premature step-up.
                    _calmTicks = 0;
                }
            }
            // Not participating: _quality is frozen — offline wants full
            // fidelity and resumes its prior throttle when realtime returns.

            return new GovernorDecision(
                QualityScale: _quality,
                ThrottleActive: _quality < 1.0,
                ShedCaches: shed,
                HardCapBreached: hardBreach);
        }

        /// <summary>Restore full quality and clear the recover counter.</summary>
        public void Reset()
        {
            _quality = 1.0;
            _calmTicks = 0;
        }
    }

    /// <summary>
    /// Cross-platform process resource sampler. CPU% is the process's CPU time
    /// delta over the wall-clock delta, normalised by logical core count so
    /// 100 = every core saturated. Memory fraction is the working set over the
    /// memory available to the process (<see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/>,
    /// which respects container / cgroup limits). First call establishes the
    /// baseline and reports 0% CPU.
    /// </summary>
    public sealed class ProcessResourceSampler : IResourceSampler
    {
        private readonly Process _proc = Process.GetCurrentProcess();
        private readonly int _cores = Math.Max(1, Environment.ProcessorCount);
        private readonly object _lock = new();
        private TimeSpan _lastCpu;
        private long _lastTicks;
        private bool _primed;

        public ResourceSample Sample()
        {
            lock (_lock)
            {
                _proc.Refresh();

                TimeSpan cpu = _proc.TotalProcessorTime;
                long now = Stopwatch.GetTimestamp();

                double cpuPercent = 0.0;
                if (_primed)
                {
                    double wallSeconds = (now - _lastTicks) / (double)Stopwatch.Frequency;
                    if (wallSeconds > 0)
                    {
                        double cpuSeconds = (cpu - _lastCpu).TotalSeconds;
                        cpuPercent = Math.Clamp(cpuSeconds / (wallSeconds * _cores) * 100.0, 0.0, 100.0);
                    }
                }

                _lastCpu = cpu;
                _lastTicks = now;
                _primed = true;

                long avail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                double memFraction = avail > 0
                    ? Math.Clamp(_proc.WorkingSet64 / (double)avail, 0.0, 1.0)
                    : 0.0;

                return new ResourceSample(cpuPercent, memFraction);
            }
        }
    }

    /// <summary>
    /// Optional OS-level hard cap installed as a last-resort backstop under the
    /// managed governor. The real implementation (Windows Job Object memory +
    /// CPU-rate limit) is platform-specific and lives in the host project; this
    /// contract keeps Abstractions cross-platform. The default
    /// <see cref="NoOpResourceCapBackstop"/> does nothing.
    /// </summary>
    public interface IResourceCapBackstop : IDisposable
    {
        /// <summary>Best-effort install of an OS hard cap. Safe to call once at
        /// startup. Silently no-ops on platforms without support.</summary>
        void Install(long memoryLimitBytes, double cpuHardCapFraction);
    }

    /// <summary>Default backstop — no OS enforcement. The managed governor is
    /// the primary mechanism; this placeholder keeps wiring simple on
    /// platforms (or builds) without a Job Object implementation.</summary>
    public sealed class NoOpResourceCapBackstop : IResourceCapBackstop
    {
        public void Install(long memoryLimitBytes, double cpuHardCapFraction) { }
        public void Dispose() { }
    }
}
