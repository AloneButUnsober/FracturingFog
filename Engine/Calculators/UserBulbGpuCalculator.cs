// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserBulbGpuCalculator.cs
//
// ILGPU-backed GPU raymarcher for User Bulb 3D — the legacy path: the analytic
// triplex power-N DE built into UserBulbShadeKernel (no Roslyn). The sandbox path
// (UserBulbSandboxGpuCompiler) compiles the same kernel with the user's own DE.
//
// #1173-A / GPU parity plan G2.4 — the kernel is UserBulbShadeKernel: the family
// kernels' full shading (lights, shadows, AO, PBR, reflections, volumetrics,
// HDRI, colour-map albedo, AOV, G-buffers) instead of the old one-light Lambert
// + sine rainbow. Device: the shared GPU (GpuAcceleratorHost), else the ILGPU
// CPU accelerator (UserBulbGpuDevice) — still faster than User Bulb's CPU loop.

using System;

using System.Threading;

using ILGPU;
using ILGPU.Runtime;

using FracturingFog.Calculators.Gpu;

namespace FracturingFog.Calculators;

public struct GpuRenderParams
{
    public int Width, Height;
    public double CamX, CamY, CamZ;
    public double TargetX, TargetY, TargetZ;
    public double FwdX, FwdY, FwdZ;
    public double RightX, RightY, RightZ;
    public double UpX, UpY, UpZ;
    public double FovScale, Aspect;
    public double LightX, LightY, LightZ;
    public int DEIter, MaxSteps;
    public double Eps, Bailout, CullRadiusSq;
    public double Power;          // 2 = square triplex; else generic power-N
    public double QuatSliceW;     // Quat axis-mode slice plane (z.W when projecting 4D→3D)
    public uint InSetColor;

    // Wave 4.6 — Julia + numerical-Jacobian fields (Sandbox quat-mode GPU
    // dispatch). Default zero keeps legacy single-step + chain analytic-power
    // paths bit-identical (UseAnalyticDE=1 from the caller routes through
    // the analytic branch; legacy callers that don't set the field get the
    // analytic branch via default-zero check on the new shape — see
    // BuildKernelSource).
    public int JuliaMode;                       // 0 = escape-time, 1 = Julia (c constant from JuliaC*)
    public double JuliaCW, JuliaCX, JuliaCY, JuliaCZ;
    public double JacH;                         // Jacobian forward-diff step (numerical DE only)
    public int UseAnalyticDE;                   // 1 = power-DE; 0 = 5-trajectory numerical Jacobian

    // S8 (#484/#488) — primary-light positional resolve. The UserBulb GPU shade
    // is single-light cheap Lambert; when Light1 is a point/spot light this
    // carries its world position + range + spot cone cosines so the kernel can
    // resolve a surface-relative direction + attenuation (twin of LightSampler).
    // L1Type 0 = Directional → LightX/Y/Z is used unchanged, atten 1 →
    // byte-identical with the pre-S8 GPU path. When non-zero, LightX/Y/Z carries
    // the light's shine direction (the spot cone axis).
    public int L1Type;                          // 0 Directional, 1 Point, 2 Spot
    public double L1PX, L1PY, L1PZ;             // light world position (point/spot)
    public double L1Range;                      // Karis range window; ≤0 = pure 1/d²
    public double L1InnerCos, L1OuterCos;       // precomputed spot cone half-angle cosines

    // #1173-A — UserBulbShadeKernel inputs the CPU UserBulb trace uses.
    public int ClipEnabled;                     // clip plane: skip surface points with n·p − d > 0
    public double ClipNX, ClipNY, ClipNZ, ClipD;
    public int ColorDriverNormal;               // 1 = BulbColorDriver.Normal, 0 = StepDepth
    public uint BgTop, BgBottom;                // UserBulb bg gradient for cull-sphere misses
    // The CPU cone-march prepass's per-tile start distances (UserBulbCalculator), packed
    // into the user-parameter buffer at HintOffset: HintTilesX × rows of HintTileSize²
    // pixels; +Inf = the tile's centre ray found nothing. HintTileSize 0 = no hints.
    public int HintOffset, HintTilesX, HintTileSize;
}

/// <summary>#1173-A — the device the User Bulb GPU kernels run on: the shared GPU
/// (GpuAcceleratorHost) when there is one, else a process-wide ILGPU CPU accelerator.
/// User Bulb keeps that CPU fallback (unlike the families, #749): its CPU loop runs
/// interpreted / delegate-based DEs, so the JIT'd kernel on the CPU accelerator is
/// still the faster path.</summary>
public static class UserBulbGpuDevice
{
    private static readonly object s_lock = new();
    private static Context? s_cpuContext;
    private static Accelerator? s_cpu;

    public static bool TryAcquire(out Accelerator acc, out string error)
    {
        error = string.Empty;
        if (GpuAcceleratorHost.TryAcquire(out acc)) return true;
        lock (s_lock)
        {
            try
            {
                s_cpuContext ??= GpuAcceleratorHost.CreateContext();
                s_cpu ??= System.Linq.Enumerable.First(s_cpuContext.Devices, d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(s_cpuContext);
                acc = s_cpu;
                return true;
            }
            catch (Exception ex)
            {
                error = $"GPU init failed: {ex.GetBaseException().Message}";
                acc = null!;
                return false;
            }
        }
    }

    public static string Label(Accelerator acc) => $"{acc.AcceleratorType} {acc.Name}";
}

public sealed class UserBulbGpuCalculator : IDisposable
{
    private UserBulbKernel? _kernel;
    private Accelerator? _kernelAcc;
    private bool _initFailed;
    // #1169 — a device proved too slow for this kernel (process-wide, per device).
    private static (Accelerator Device, string Message)? s_tooSlow;
    public string LastError { get; private set; } = string.Empty;

    /// <summary>#1173-M — the device this path renders on, or null before init.</summary>
    public string? DeviceLabel => _kernelAcc is { } a ? UserBulbGpuDevice.Label(a) : null;

    private bool TryInit(out Accelerator acc)
    {
        acc = null!;
        if (_initFailed) return false;
        if (!UserBulbGpuDevice.TryAcquire(out acc, out var err)) { LastError = err; _initFailed = true; return false; }
        if (_kernel != null && ReferenceEquals(_kernelAcc, acc)) return true;
        try
        {
            _kernel = UserBulbGpuDispatch.Load(acc, UserBulbShadeKernel.Kernel);
            _kernelAcc = acc;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"User Bulb GPU kernel load failed: {ex.GetBaseException().Message}";
            _initFailed = true;
            return false;
        }
    }

    /// <summary>Render one frame with the built-in analytic DE. False = fall back to the CPU.</summary>
    public bool Render(uint[] outBuffer, double[]? userParams, GpuRaymarchParams r, GpuShadingParams sp, GpuRenderParams q,
        uint[]? palette, float[]? depthOut, float[]? normalOut, float[]? hdrOut,
        uint[]? albedoLut, uint[]? hdri, CancellationToken ct = default)
    {
        if (!TryInit(out var acc)) return false;
        if (s_tooSlow is { } slow && ReferenceEquals(slow.Device, acc)) { LastError = slow.Message; return false; }
        try
        {
            var run = UserBulbGpuDispatch.Run(acc, _kernel!, outBuffer, r, sp, q, userParams,
                palette, depthOut, normalOut, hdrOut, albedoLut, hdri, ct);
            if (run == GpuDispatchResult.TooSlow)
            {
                LastError = GpuTiledDispatch.TooSlowMessage("User Bulb");
                s_tooSlow = (acc, LastError);
                return false;
            }
            return run == GpuDispatchResult.Completed;
        }
        catch (Exception ex)
        {
            LastError = $"User Bulb GPU render failed: {ex.Message}";
            GpuAcceleratorHost.ReportRenderFault(acc, ex);
            return false;
        }
    }

    public void Dispose() { _kernel = null; _kernelAcc = null; }
}
