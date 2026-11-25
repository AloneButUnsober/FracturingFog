// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// OdeGpuRoute.cs (#2049, physics epic #2048)
//
// Where an ODE family's frame runs when GPU render is on, shared by every family on the GPU ODE
// template. The GPU and CPU traces are the same arithmetic, so the path is a speed choice and
// the answer depends on the hardware (the Buddhabrot #1220 / mirror spheres #1443 / black hole
// #1926 pattern): Auto measures on the first GPU frame (per kernel, family and precision), keeps
// the faster path, and re-measures when the pixel count moves by more than RemeasureFactor.
// A family's env variable (e.g. FF_GPU_HENON_HEILES) forces either path.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace FracturingFog.Calculators.Gpu;

public static class OdeGpuRoute
{
    public enum Mode { Auto, Gpu, Cpu }

    /// <summary>Re-measure when the pixel count differs from the measured frame by more than this factor.</summary>
    public const double RemeasureFactor = 4.0;
    /// <summary>Auto times the CPU on every this-many-th row of the first GPU frame.</summary>
    public const int MeasureRowStride = 8;

    public static Mode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "on" or "yes" or "gpu" => Mode.Gpu,
        "0" or "false" or "off" or "no" or "cpu" => Mode.Cpu,
        _ => Mode.Auto,
    };

    /// <summary>One measured comparison: trace cost per pixel on each path.</summary>
    public sealed record Measurement(double CpuNsPerPixel, double GpuNsPerPixel, int Pixels)
    {
        public bool GpuFaster => GpuNsPerPixel < CpuNsPerPixel;
        public bool Covers(int pixels) => pixels <= Pixels * RemeasureFactor && Pixels <= pixels * RemeasureFactor;
        public string Summary => $"measured CPU {CpuNsPerPixel:0.#} vs GPU {GpuNsPerPixel:0.#} ns/pixel";
    }

    private static readonly ConditionalWeakTable<object, Dictionary<(string, bool), Measurement>> s_decisions = new();

    /// <summary>The kept measurement for this kernel / family / precision covering <paramref name="pixels"/>, or null.</summary>
    public static Measurement? Known(object kernel, string family, bool single, int pixels)
    {
        if (!s_decisions.TryGetValue(kernel, out var d)) return null;
        lock (d) return d.TryGetValue((family, single), out var m) && m.Covers(pixels) ? m : null;
    }

    public static void Record(object kernel, string family, bool single, Measurement m)
    {
        var d = s_decisions.GetOrCreateValue(kernel);
        lock (d) d[(family, single)] = m;
    }
}
