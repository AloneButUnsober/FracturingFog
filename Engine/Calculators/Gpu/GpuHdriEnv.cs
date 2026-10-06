// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// GpuHdriEnv.cs — #1173-B / GPU parity plan G2.3: the HDRI environment for the
// 3D ILGPU kernels. Before this the kernels always used the gradient sky, so an
// HDRI scene rendered with the wrong backdrop, ambient and reflections on the GPU
// while the CPU sampled the image (a silent parity gap, not a gate).
//
// The buffer is the relief kernel's (#171): ReliefHdriBuffer.Flatten — a uint[]
// with an integer mip header and the RGB floats as bit patterns — so the 3D
// kernels, the relief kernel and their CPU twins all read one layout. The kernel
// sampler is GpuKernelUtils.SampleHdri, a line-for-line twin of
// ReliefHdriBuffer.Sample (itself the twin of HdriImage.Sample).

using System.Runtime.CompilerServices;

using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Resolves the frame's HDRI the way the CPU shade does and hands the
/// kernels its flattened buffer.</summary>
public static class GpuHdriEnv
{
    // Flattening walks every mip; an HdriImage is immutable, so do it once per image.
    private static readonly ConditionalWeakTable<HdriImage, uint[]> s_flat = new();

    /// <summary>The flattened HDRI for <paramref name="fx"/>, or null for the gradient
    /// sky. Sets <see cref="GpuShadingParams.HdriOn"/> to match. Same resolution rule
    /// as <c>ShadingPipeline.SkyColorHdri</c>: SkyMode Hdri and a name that resolves.</summary>
    public static uint[]? Resolve(in LightingFxData fx, ref GpuShadingParams sp)
    {
        sp.HdriOn = 0;
        if (fx.SkyMode != SkyMode.Hdri || !ShadingPipeline.TryResolveHdri(fx.EnvironmentName, out var img) || img is null)
            return null;
        sp.HdriOn = 1;
        return s_flat.GetValue(img, ReliefHdriBuffer.Flatten);
    }
}
