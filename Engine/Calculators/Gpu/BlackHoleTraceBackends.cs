// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BlackHoleTraceBackends.cs (#1972, black hole shadows epic #1922)
//
// The D3D11 and Vulkan twins of the ILGPU black hole trace (#1926). Both compile one HLSL
// source (Rendering.D3D/BlackHoleKernelSource) and return the same per-pixel outcomes as
// BlackHoleShadowGpuCalculator (packed fate + steps, delay, final momentum), so the calculator's
// colour pass needs nothing new. The backends live in Rendering.D3D / Rendering.Vulkan (the
// Engine does not reference them): the host and the batch set the factories, each kernel owns
// its own device / context, and a backend that cannot start (no device, no fp64) is remembered
// as unavailable for the session.
//
// Choice (FF_GPU_BLACK_HOLE_BACKEND, or BlackHoleTraceBackends.Selection for tests):
//   auto    ILGPU (CUDA / OpenCL) when an accelerator is there, else D3D11, else Vulkan;
//   ilgpu / d3d11 / vulkan  that backend only (CPU when it is unavailable).
// FF_GPU_BLACK_HOLE still chooses GPU / CPU / Auto (measured) on top of this.

using System;
using System.Threading;

namespace FracturingFog.Calculators.Gpu;

/// <summary>#1972 — a GPU backend for the black hole trace.</summary>
public interface IBlackHoleTraceKernel : IDisposable
{
    /// <summary>"D3D11" or "Vulkan".</summary>
    string Backend { get; }
    /// <summary>The device the kernel runs on (status bar / benchmark).</summary>
    string DeviceName { get; }
    string LastError { get; }
    /// <summary>Trace a frame; null on failure or cancellation (the caller traces on the CPU).</summary>
    BlackHoleShadowGpuCalculator.Outcomes? Trace(in BlackHoleGpuParams p, bool single, CancellationToken ct = default);
}

/// <summary>#1972 — the D3D11 / Vulkan trace backends and the backend choice.</summary>
public static class BlackHoleTraceBackends
{
    public enum Choice { Auto, Ilgpu, D3D11, Vulkan }

    /// <summary>Set by the host / batch (Windows): a self-owned D3D11 kernel, or null.</summary>
    public static Func<IBlackHoleTraceKernel?>? D3D11Factory { get; set; }
    /// <summary>Set by the host / batch: a self-owned Vulkan kernel, or null.</summary>
    public static Func<IBlackHoleTraceKernel?>? VulkanFactory { get; set; }

    private static readonly object s_gate = new();
    private static IBlackHoleTraceKernel? s_d3d11, s_vulkan;
    private static bool s_d3d11Failed, s_vulkanFailed;

    /// <summary>The backend choice (env FF_GPU_BLACK_HOLE_BACKEND); a set value overrides for the
    /// calling thread only (tests).</summary>
    public static Choice Selection
    {
        get => t_selection ?? s_env;
        set => t_selection = value;
    }
    [ThreadStatic] private static Choice? t_selection;
    private static readonly Choice s_env = Parse(Environment.GetEnvironmentVariable("FF_GPU_BLACK_HOLE_BACKEND"));

    public static Choice Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "ilgpu" or "cuda" or "opencl" => Choice.Ilgpu,
        "d3d11" or "d3d" or "dx11" => Choice.D3D11,
        "vulkan" or "vk" => Choice.Vulkan,
        _ => Choice.Auto,
    };

    /// <summary>The D3D11 kernel (created once; null when unavailable).</summary>
    public static IBlackHoleTraceKernel? D3D11 => Get(ref s_d3d11, ref s_d3d11Failed, D3D11Factory);
    /// <summary>The Vulkan kernel (created once; null when unavailable).</summary>
    public static IBlackHoleTraceKernel? Vulkan => Get(ref s_vulkan, ref s_vulkanFailed, VulkanFactory);

    private static IBlackHoleTraceKernel? Get(ref IBlackHoleTraceKernel? slot, ref bool failed, Func<IBlackHoleTraceKernel?>? factory)
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

    /// <summary>The non-ILGPU kernel to use for <paramref name="choice"/> when ILGPU is not used
    /// (<paramref name="ilgpuAvailable"/> false, or the choice names D3D11 / Vulkan), else null.</summary>
    public static IBlackHoleTraceKernel? KernelFor(Choice choice, bool ilgpuAvailable) => choice switch
    {
        Choice.D3D11 => D3D11,
        Choice.Vulkan => Vulkan,
        Choice.Ilgpu => null,
        _ => ilgpuAvailable ? null : (D3D11 ?? Vulkan),
    };

    /// <summary>Kernel parameters as the t0 buffer's doubles (layout: BlackHoleKernelSource).</summary>
    public static double[] Pack(in BlackHoleGpuParams p)
    {
        var a = new double[40];
        a[0] = p.CenterX; a[1] = p.CenterY; a[2] = p.Pitch;
        void Hole(int i, double x, double y, double z, double m, double c)
        { int o = 3 + 5 * i; a[o] = x; a[o + 1] = y; a[o + 2] = z; a[o + 3] = m; a[o + 4] = c; }
        Hole(0, p.X0, p.Y0, p.Z0, p.M0, p.C0);
        Hole(1, p.X1, p.Y1, p.Z1, p.M1, p.C1);
        Hole(2, p.X2, p.Y2, p.Z2, p.M2, p.C2);
        Hole(3, p.X3, p.Y3, p.Z3, p.M3, p.C3);
        a[23] = p.Escape2;
        a[24] = p.CamX; a[25] = p.CamY; a[26] = p.CamZ;
        a[27] = p.Fx; a[28] = p.Fy; a[29] = p.Fz;
        a[30] = p.Rx; a[31] = p.Ry; a[32] = p.Rz;
        a[33] = p.Ux; a[34] = p.Uy; a[35] = p.Uz;
        a[36] = p.Distance; a[37] = p.RelTol; a[38] = p.AbsTol; a[39] = p.InitialStep;
        return a;
    }

    /// <summary>The float parameters of the single-precision kernel (tolerance floored as the ILGPU
    /// float kernel's).</summary>
    public static float[] PackSingle(in BlackHoleGpuParams p)
    {
        var d = Pack(p);
        var f = new float[d.Length];
        for (int i = 0; i < d.Length; i++) f[i] = (float)d[i];
        f[37] = MathF.Max(f[37], BlackHoleShadowGpuCalculator.SingleMinRelTol);
        return f;
    }

    /// <summary>Reset the cached kernels (tests).</summary>
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
