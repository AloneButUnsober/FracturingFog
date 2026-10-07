// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserBulbGpuDispatch.cs — #1173-A / GPU parity plan G2.4: the host side of the
// User Bulb GPU kernel (UserBulbShadeKernel), shared by the legacy built-in-DE path
// (UserBulbGpuCalculator) and the sandbox-compiled path (UserBulbSandboxGpuCompiler).
// Same buffer contract as the per-family calculators: colour out, optional depth /
// normal / HDR G-buffers (length-1 dummies when off), palette / albedo LUT / HDRI
// tables, tiled under the GPU watchdog (#1170).

using System;
using System.Threading;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Loaded <see cref="UserBulbShadeKernel.Kernel"/> (or a sandbox twin).</summary>
public delegate void UserBulbKernel(
    Index1D tid, ArrayView<uint> output, GpuRaymarchParams r, GpuShadingParams sp, GpuRenderParams q,
    ArrayView<double> userParams, ArrayView<uint> palette, ArrayView<float> depth, ArrayView<float> normals,
    ArrayView<float> hdr, ArrayView<uint> albedo, ArrayView<uint> hdri);

public static class UserBulbGpuDispatch
{
    /// <summary>The shared raymarch block for a User Bulb frame described by
    /// <paramref name="q"/>: same camera, cull sphere (CullRadiusSq around the target),
    /// steps / eps and in-set colour; no pan, no thin-lens (User Bulb has neither).</summary>
    public static GpuRaymarchParams Raymarch(in GpuRenderParams q) => new()
    {
        Width = q.Width, Height = q.Height,
        CamX = q.CamX, CamY = q.CamY, CamZ = q.CamZ,
        TargetX = q.TargetX, TargetY = q.TargetY, TargetZ = q.TargetZ,
        FwdX = q.FwdX, FwdY = q.FwdY, FwdZ = q.FwdZ,
        RightX = q.RightX, RightY = q.RightY, RightZ = q.RightZ,
        UpX = q.UpX, UpY = q.UpY, UpZ = q.UpZ,
        FovScale = q.FovScale, Aspect = q.Aspect,
        LightX = q.LightX, LightY = q.LightY, LightZ = q.LightZ,
        MaxSteps = q.MaxSteps, Eps = q.Eps,
        CullRadiusSq = q.CullRadiusSq,
        InSetColor = q.InSetColor,
        DofSamples = 1,
    };

    /// <summary>Load a kernel method with the <see cref="UserBulbShadeKernel.Kernel"/>
    /// signature onto <paramref name="acc"/>.</summary>
    public static UserBulbKernel Load(Accelerator acc,
        Action<Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, GpuRenderParams, ArrayView<double>,
            ArrayView<uint>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<uint>, ArrayView<uint>> method)
    {
        var k = acc.LoadAutoGroupedStreamKernel(method);
        return (tid, o, r, sp, q, up, pal, d, n, h, alb, env) => k(tid, o, r, sp, q, up, pal, d, n, h, alb, env);
    }

    /// <summary>Upload, launch (tiled) and read back one frame. Throws on a device
    /// error; the caller reports it (GpuAcceleratorHost.ReportRenderFault).</summary>
    public static GpuDispatchResult Run(Accelerator acc, UserBulbKernel kernel, uint[] outBuffer,
        GpuRaymarchParams r, GpuShadingParams sp, GpuRenderParams q, double[]? userParams,
        uint[]? palette, float[]? depthOut, float[]? normalOut, float[]? hdrOut,
        uint[]? albedoLut, uint[]? hdri, CancellationToken ct)
    {
        int total = r.Width * r.Height;
        using var dev = GpuMemoryStats.Allocate1D<uint>(acc, total);
        double[] up = userParams is { Length: > 0 } ? userParams : new double[1];
        using var devP = GpuMemoryStats.Allocate1D<double>(acc, up.Length);
        devP.CopyFromCPU(up);
        uint[] lut = palette is { Length: >= 2 } ? palette : GpuKernelUtils.PaletteOff;
        using var devLut = GpuMemoryStats.Allocate1D<uint>(acc, lut.Length);
        devLut.CopyFromCPU(lut);
        bool wantDepth = depthOut != null && depthOut.Length == total;
        bool wantNormal = normalOut != null && normalOut.Length == 3 * total;
        bool wantHdr = hdrOut != null && hdrOut.Length == 3 * total;
        using var devDepth = GpuMemoryStats.Allocate1D<float>(acc, wantDepth ? total : 1);
        using var devNormal = GpuMemoryStats.Allocate1D<float>(acc, wantNormal ? 3L * total : 1);
        using var devHdr = GpuMemoryStats.Allocate1D<float>(acc, wantHdr ? 3L * total : 1);
        uint[] alb = albedoLut is { Length: >= 2 } ? albedoLut : GpuKernelUtils.PaletteOff;
        using var devAlbedo = GpuMemoryStats.Allocate1D<uint>(acc, alb.Length);
        devAlbedo.CopyFromCPU(alb);
        uint[] env = hdri is { Length: >= 2 } ? hdri : GpuKernelUtils.PaletteOff;
        using var devHdri = GpuMemoryStats.Allocate1D<uint>(acc, env.Length);
        devHdri.CopyFromCPU(env);

        var run = GpuTiledDispatch.Run(acc, r, (n, rt) => kernel(n, dev.View, rt, sp, q, devP.View, devLut.View,
            devDepth.View, devNormal.View, devHdr.View, devAlbedo.View, devHdri.View), ct);
        if (run != GpuDispatchResult.Completed) return run;
        dev.CopyToCPU(outBuffer);
        if (wantDepth) devDepth.CopyToCPU(depthOut!);
        if (wantNormal) devNormal.CopyToCPU(normalOut!);
        if (wantHdr) devHdr.CopyToCPU(hdrOut!);
        return run;
    }
}
