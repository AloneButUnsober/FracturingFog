// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/RenderThrottle.cs
//
// #189 feature 5 (performance safety) — a process-global cap on how many CPU
// workers the heavy render loops (MandelbrotCalculator, HeightfieldRaymarch2D)
// are allowed to use. Default -1 = unlimited (System.Threading default), so
// unless a caller opts in the render is byte-for-byte and perf-for-perf as
// before. The shell's poster / wallpaper handlers set it to Cpu90() around a
// heavy offscreen render so a print-resolution poster can't peg every core and
// starve the UI thread; batch / server / cluster renders leave it unlimited so
// dedicated headless work still uses the whole machine.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.Rendering;

/// <summary>Process-global degree-of-parallelism cap for the heavy render
/// loops. -1 = unlimited (default). See file header.</summary>
public static class RenderThrottle
{
    private static int s_maxDop = -1;

    /// <summary>Maximum parallel workers the render loops may use. -1 (default)
    /// = unlimited. Read at each <c>Parallel.For</c> dispatch via
    /// <see cref="Options"/>, so a change takes effect on the next band.</summary>
    public static int MaxDegreeOfParallelism
    {
        get => Volatile.Read(ref s_maxDop);
        set => Volatile.Write(ref s_maxDop, value == 0 ? -1 : value);
    }

    /// <summary>A <see cref="ParallelOptions"/> honouring the current cap and the
    /// supplied token. Allocation-cheap; callers that dispatch many bands should
    /// still cache one per operation and refresh its token, matching the existing
    /// calculators' reuse pattern.</summary>
    public static ParallelOptions Options(CancellationToken ct = default)
        => new() { CancellationToken = ct, MaxDegreeOfParallelism = MaxDegreeOfParallelism };

    /// <summary>Worker count that leaves at least ~10% of the logical CPUs free
    /// (so a render tops out near 90% CPU, per #189). Always ≥ 1.</summary>
    public static int Cpu90()
        => Math.Max(1, (int)Math.Floor(Environment.ProcessorCount * 0.9));

    /// <summary>Run <paramref name="action"/> with the cap set to
    /// <paramref name="maxDop"/>, restoring the previous value afterwards. Nested
    /// scopes restore the outer cap. Not re-entrant across threads — intended for
    /// a single heavy offscreen render at a time.</summary>
    public static void With(int maxDop, Action action)
    {
        int prev = MaxDegreeOfParallelism;
        MaxDegreeOfParallelism = maxDop;
        try { action(); }
        finally { MaxDegreeOfParallelism = prev; }
    }
}
