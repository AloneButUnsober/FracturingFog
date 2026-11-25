// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// OdeTraceBackends.cs (#2049, physics epic #2048)
//
// GPU backends for the ODE families on the shared scattering integrator (#1923): one HLSL
// template (Rendering.D3D/OdeKernelSource, the Dormand-Prince 5(4) core term for term) plus a
// family's equations (OdeFamily.Hlsl), compiled by FXC for D3D11 or by DXC for Vulkan. The
// backends live in Rendering.D3D / Rendering.Vulkan (the Engine does not reference them): the
// host and the batch set the factories (the #1972 / #2024 pattern), each kernel owns its
// device / context, and a backend that cannot start is remembered as unavailable.
//
// The contract is deliberately narrow: the host computes each traced item's launch state
// exactly as the CPU does and labels the outcome from the final state, so the kernel only
// integrates. Items are traced in chunks (ChunkItems) so the buffers stay small at any frame
// size; inside a chunk the kernel dispatches TDR-safe bands steered toward ~100 ms.
//
// Choice (FF_GPU_ODE_BACKEND, or OdeTraceBackends.Selection for tests):
//   auto    D3D11, else Vulkan;  d3d11 / vulkan  that backend only.
// Whether a family uses the GPU at all is its own setting (e.g. FF_GPU_HENON_HEILES) on top
// of the Lighting & FX "GPU render" switch.

using System;
using System.Threading;

namespace FracturingFog.Calculators.Gpu;

/// <summary>#2049 — one family's equations for the GPU ODE template.</summary>
public sealed record OdeFamily(string Key, int Dimension, int Channels, string Hlsl);

/// <summary>#2049 — one batch of trajectories to trace.</summary>
public sealed class OdeJob
{
    public required OdeFamily Family { get; init; }
    /// <summary>Family constants, read by the HLSL as FP(0..).</summary>
    public required double[] FamilyParams { get; init; }
    public double RelTol { get; init; } = 1e-9;
    public double AbsTol { get; init; } = 1e-12;
    public double InitialStep { get; init; } = 0.1;
    public int MaxSteps { get; init; } = 20000;
    /// <summary>Items to trace.</summary>
    public required int Count { get; init; }
    /// <summary>Fills the launch states of items [start, start + count) into dst (count x Dimension).</summary>
    public required Action<int, int, double[]> Launches { get; init; }
    /// <summary>Single precision (fast option) instead of double.</summary>
    public bool Single { get; init; }
}

/// <summary>#2049 — the traced outcomes, item by item.</summary>
public sealed class OdeResult
{
    /// <summary>0 running (never), 1 captured, 2 escaped, 3 undecided — as <see cref="FracturingFog.Scattering.ScatterFate"/>.</summary>
    public int[] Fate { get; }
    public int[] Body { get; }
    public int[] Steps { get; }
    /// <summary>Integration parameter travelled.</summary>
    public double[] T { get; }
    /// <summary>Final states, Dimension per item.</summary>
    public double[] Final { get; }
    /// <summary>Channels, Channels per item.</summary>
    public double[] Channels { get; }

    public OdeResult(int count, int dim, int channels)
    {
        Fate = new int[count]; Body = new int[count]; Steps = new int[count]; T = new double[count];
        Final = new double[count * dim]; Channels = new double[count * Math.Max(1, channels)];
    }
}

/// <summary>#2049 — a GPU backend for the ODE template.</summary>
public interface IOdeTraceKernel : IDisposable
{
    /// <summary>"D3D11" or "Vulkan".</summary>
    string Backend { get; }
    string DeviceName { get; }
    string LastError { get; }
    /// <summary>The device runs the double kernel.</summary>
    bool SupportsDouble { get; }
    /// <summary>Trace a job; null on failure or cancellation (the caller traces on the CPU).</summary>
    OdeResult? Trace(OdeJob job, CancellationToken ct = default);
}

public static class OdeTraceBackends
{
    public enum Choice { Auto, D3D11, Vulkan }

    /// <summary>Items per chunk (launch / outcome buffers are sized to this).</summary>
    public const int ChunkItems = 1 << 18;

    public static Func<IOdeTraceKernel?>? D3D11Factory { get; set; }
    public static Func<IOdeTraceKernel?>? VulkanFactory { get; set; }

    private static readonly object s_gate = new();
    private static IOdeTraceKernel? s_d3d11, s_vulkan;
    private static bool s_d3d11Failed, s_vulkanFailed;

    /// <summary>The backend choice (env FF_GPU_ODE_BACKEND); a set value overrides for the calling thread only (tests).</summary>
    public static Choice Selection
    {
        get => t_selection ?? s_env;
        set => t_selection = value;
    }
    [ThreadStatic] private static Choice? t_selection;
    private static readonly Choice s_env = Parse(Environment.GetEnvironmentVariable("FF_GPU_ODE_BACKEND"));

    public static Choice Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "d3d11" or "d3d" or "dx11" => Choice.D3D11,
        "vulkan" or "vk" => Choice.Vulkan,
        _ => Choice.Auto,
    };

    public static IOdeTraceKernel? D3D11 => Get(ref s_d3d11, ref s_d3d11Failed, D3D11Factory);
    public static IOdeTraceKernel? Vulkan => Get(ref s_vulkan, ref s_vulkanFailed, VulkanFactory);

    private static IOdeTraceKernel? Get(ref IOdeTraceKernel? slot, ref bool failed, Func<IOdeTraceKernel?>? factory)
    {
        lock (s_gate)
        {
            if (slot != null || failed || factory == null) return slot;
            try { slot = factory(); }
            catch (Exception) { slot = null; }
            if (slot == null) failed = true;
            return slot;
        }
    }

    /// <summary>The kernel for <paramref name="choice"/> (null: none available). With
    /// <paramref name="needDouble"/>, Auto skips a backend without fp64.</summary>
    public static IOdeTraceKernel? KernelFor(Choice choice, bool needDouble)
    {
        switch (choice)
        {
            case Choice.D3D11: return D3D11;
            case Choice.Vulkan: return Vulkan;
        }
        var d = D3D11;
        if (d != null && (!needDouble || d.SupportsDouble)) return d;
        var v = Vulkan;
        if (v != null && (!needDouble || v.SupportsDouble)) return v;
        return d ?? v;
    }

    /// <summary>Decode one chunk's outcome words (written by the kernel) into <paramref name="r"/>.</summary>
    public static void Decode(ReadOnlySpan<uint> words, int first, int count, OdeFamily fam, bool single, OdeResult r)
    {
        int dim = fam.Dimension, nch = Math.Max(1, fam.Channels);
        int w = single ? 1 : 2;
        int stride = 2 + (1 + dim + nch) * w;
        static double Val(ReadOnlySpan<uint> wd, int o, bool sgl) => sgl
            ? BitConverter.Int32BitsToSingle(unchecked((int)wd[o]))
            : BitConverter.Int64BitsToDouble(unchecked((long)((ulong)wd[o] | ((ulong)wd[o + 1] << 32))));
        for (int i = 0; i < count; i++)
        {
            int o = i * stride, item = first + i;
            uint head = words[o];
            r.Fate[item] = (int)(head & 0xF);
            r.Body[item] = (int)(head >> 4) - 1;
            r.Steps[item] = (int)words[o + 1];
            r.T[item] = Val(words, o + 2, single);
            for (int k = 0; k < dim; k++) r.Final[item * dim + k] = Val(words, o + 2 + w * (1 + k), single);
            for (int k = 0; k < fam.Channels; k++) r.Channels[item * nch + k] = Val(words, o + 2 + w * (1 + dim + k), single);
        }
    }

    /// <summary>The t1 parameter buffer: rtol, atol, initial step, then the family's.</summary>
    public static double[] Params(OdeJob job)
    {
        var p = new double[3 + Math.Max(1, job.FamilyParams.Length)];
        p[0] = job.RelTol; p[1] = job.AbsTol; p[2] = job.InitialStep;
        Array.Copy(job.FamilyParams, 0, p, 3, job.FamilyParams.Length);
        return p;
    }

    public static void ResetForTests()
    {
        lock (s_gate)
        {
            try { s_d3d11?.Dispose(); } catch { }
            try { s_vulkan?.Dispose(); } catch { }
            s_d3d11 = s_vulkan = null;
            s_d3d11Failed = s_vulkanFailed = false;
        }
    }
}
