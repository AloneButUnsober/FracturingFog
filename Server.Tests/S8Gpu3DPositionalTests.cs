// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S8 GPU 3D lights slice 1 (#484/#485) — foundation. The 8 per-fractal ILGPU
// kernels used to resolve only a DIRECTIONAL light; a point/spot light forced
// the CPU shade (#483). #485 adds the CPU→GPU bridge (GpuShadingParams carries
// per-light Type / world position / range / spot-cone cosines) and the
// kernel-side resolve (GpuKernelUtils.ResolveLight, the twin of
// LightSampler.Sample), then lifts the Mandelbulb force-CPU gate.
//
// The kernel pixel output is validated on-device (CLI probe + user smoke) like
// the rest of the GPU shade path; what is unit-testable is the bridge: a stock
// LightingFxData must build directional (Type 0) params so the kernel resolve
// is a no-op → byte-identical, and a point/spot light must reach the struct
// with its position + precomputed cone cosines intact.

using System;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8Gpu3DPositionalTests
{
    // A stock scene is all-directional → every LnType is 0, so the kernel's
    // ResolveLight branch is skipped and spL == sp → byte-identical with the
    // pre-S8 GPU path. This is the premise the whole slice rests on.
    [Fact]
    public void Default_Build_Is_All_Directional()
    {
        var gp = GpuShadingParams.Build(LightingFxData.CreateDefault());
        Assert.Equal(0, gp.L1Type);
        Assert.Equal(0, gp.L2Type);
        Assert.Equal(0, gp.L3Type);
    }

    // A point light reaches the kernel struct with its type + world position +
    // range, so ResolveLight can compute the surface-relative direction + the
    // inverse-square / range window per pixel.
    [Fact]
    public void Build_Carries_Point_Light_Type_Position_Range()
    {
        var fx = LightingFxData.CreateDefault();
        var l = fx.Light1;
        l.Type = LightType.Point;
        l.PosX = 1.5; l.PosY = -2.0; l.PosZ = 3.25;
        l.Range = 8.0;
        fx.Light1 = l;

        var gp = GpuShadingParams.Build(in fx);
        Assert.Equal(1, gp.L1Type);
        Assert.Equal(1.5, gp.L1PX);
        Assert.Equal(-2.0, gp.L1PY);
        Assert.Equal(3.25, gp.L1PZ);
        Assert.Equal(8.0, gp.L1Range);
    }

    // A spot light carries its type + the cosines of the inner/outer half-angle,
    // precomputed CPU-side (matching ShadingPipeline.ResolveLight) so the kernel
    // never calls Cos per pixel. cos is monotone-decreasing in the angle, so the
    // (smaller) inner angle has the larger cosine.
    [Fact]
    public void Build_Carries_Spot_Cone_Cosines()
    {
        var fx = LightingFxData.CreateDefault();
        var l = fx.Light2;
        l.Type = LightType.Spot;
        l.SpotInnerDeg = 15.0;
        l.SpotOuterDeg = 25.0;
        fx.Light2 = l;

        var gp = GpuShadingParams.Build(in fx);
        Assert.Equal(2, gp.L2Type);
        Assert.Equal(Math.Cos(15.0 * Math.PI / 180.0), gp.L2InnerCos, 12);
        Assert.Equal(Math.Cos(25.0 * Math.PI / 180.0), gp.L2OuterCos, 12);
        Assert.True(gp.L2InnerCos > gp.L2OuterCos, "cos(inner) must exceed cos(outer)");
    }

    // The kernel-side resolve is a transcription of LightSampler.Sample. Lock the
    // contract it mirrors here (LightSampler itself is what GpuKernelUtils.ResolveLight
    // reproduces line-for-line): directional passes the incoming direction through
    // unchanged with attenuation 1 → the byte-identical guarantee.
    [Fact]
    public void LightSampler_Directional_Is_PassThrough_Atten1()
    {
        var s = LightSampler.Sample(LightType.Directional,
            0.0, 1.0, 0.0, 5.0, 5.0, 5.0, 0.0, 0.0, 0.0, 1.0, 2.0, 3.0);
        Assert.Equal(0.0, s.lx);
        Assert.Equal(1.0, s.ly);
        Assert.Equal(0.0, s.lz);
        Assert.Equal(1.0, s.atten);
    }
}
