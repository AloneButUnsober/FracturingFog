using System;
using System.Collections.Generic;

namespace FracturingFog.Abstractions.Animation;

/// <summary>Minimal snapshot of the host machine used to derive a default
/// animated-param ceiling. Kept tiny and value-typed so tests can construct
/// arbitrary fake hardware without touching the real machine.</summary>
/// <param name="LogicalCores">Logical CPU count. From
/// <see cref="Environment.ProcessorCount"/> in production.</param>
/// <param name="DiscreteGpu">True when a discrete GPU is present. Raises the
/// 3D ceiling. Defaults to false (conservative — assume an iGPU) until the
/// shell wires in the real D3D vendor string.</param>
public readonly record struct HardwareProfile(int LogicalCores, bool DiscreteGpu)
{
    /// <summary>Real discrete-GPU signal, installed by the shell at startup
    /// (Windows wires it to the DXGI adapter probe in
    /// <c>WindowsD3D11HardwareInfoProvider.HasDiscreteGpu</c>). Left null on
    /// headless / non-D3D hosts, where <see cref="Detect"/> falls back to the
    /// conservative iGPU assumption. Lives here rather than in the Engine's
    /// D3D init path so <see cref="Detect"/> stays a single call site.</summary>
    public static Func<bool>? DiscreteGpuProbe { get; set; }

    /// <summary>Best-effort live probe. CPU core count is exact; GPU class is
    /// taken from <see cref="DiscreteGpuProbe"/> when the shell has installed
    /// it, else conservatively false (assume an iGPU).</summary>
    public static HardwareProfile Detect()
        => new(System.Math.Max(1, Environment.ProcessorCount),
               DiscreteGpu: DiscreteGpuProbe?.Invoke() ?? false);
}

/// <summary>Pure policy behind Animation Roadmap Phase 6's animated-param
/// ceiling. Two independent concerns, both side-effect-free and unit-tested:
/// (1) deriving a sensible default ceiling from hardware + whether the leg
/// animates any expensive 3D param, and (2) choosing which animators survive
/// when the enabled count exceeds the ceiling.</summary>
public static class AnimatedParamCeilingPolicy
{
    /// <summary>Ceiling used when no 3D-raymarched param is animated.</summary>
    public const int TwoDCeiling = 12;

    /// <summary>Ceiling for a 3D-raymarched leg on an integrated GPU.</summary>
    public const int ThreeDIntegratedGpuCeiling = 4;

    /// <summary>Ceiling for a 3D-raymarched leg on a discrete GPU.</summary>
    public const int ThreeDDiscreteGpuCeiling = 6;

    /// <summary>Derive the default ceiling. 2D-only legs get a generous
    /// ceiling; legs that animate a 3D-raymarched param (any
    /// <see cref="AnimatableParamCost.Moderate"/> track — the enum reserves
    /// Moderate for raymarched params) get a tight one, bumped when a
    /// discrete GPU is present.</summary>
    public static int DefaultCeiling(HardwareProfile hw, bool includesRaymarched3D)
    {
        if (!includesRaymarched3D) return TwoDCeiling;
        return hw.DiscreteGpu ? ThreeDDiscreteGpuCeiling : ThreeDIntegratedGpuCeiling;
    }

    /// <summary>Given the cost of each candidate animator in declaration
    /// order, return a parallel <c>bool[]</c> where <c>true</c> = tick this
    /// animator this frame. When <paramref name="ceiling"/> is &lt;= 0 or the
    /// count is within the ceiling, every entry is <c>true</c>. Otherwise the
    /// most expensive animators are dropped first; ties break by declaration
    /// order (later-declared dropped first), so the survivors are stable and
    /// front-loaded.</summary>
    public static bool[] SelectActive(IReadOnlyList<AnimatableParamCost> costs, int ceiling)
    {
        ArgumentNullException.ThrowIfNull(costs);
        int n = costs.Count;
        var keep = new bool[n];
        for (int i = 0; i < n; i++) keep[i] = true;

        if (ceiling <= 0 || n <= ceiling) return keep;

        // Drop order: highest cost first, then latest declaration index first.
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int c = costs[b].CompareTo(costs[a]); // higher cost sorts earlier
            return c != 0 ? c : b.CompareTo(a);    // later index sorts earlier
        });

        int toDrop = n - ceiling;
        for (int i = 0; i < toDrop; i++) keep[order[i]] = false;
        return keep;
    }
}
