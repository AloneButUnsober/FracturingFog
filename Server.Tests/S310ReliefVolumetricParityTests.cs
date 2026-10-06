// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #310 (GPU parity plan G0.7) — volumetric in-scatter parity between the CPU
// relief trace (HeightfieldRaymarch2D → ShadingPipeline) and the GPU relief
// kernel, via its byte-locked CPU twin (ReliefRaymarchGpu.RenderCpuMirror).
//
// The twin is plugged in as the IReliefRaymarchKernel, so HeightfieldRaymarch2D
// .Render does the whole height prepass + uniforms build once and hands the twin
// exactly what it would hand the real D3D / Vulkan kernel. The CPU trace is the
// same Render with no kernel. Scenes are shaft-forming: a low key light behind /
// beside an occluding bump, dense forward-scattering fog, ShadowSteps > 0.
//
// Compared on the volumetric terms themselves, not only the beauty:
//   in-scatter = (fog on) − (fog off),  shafts = (shadowed fog) − (unshadowed fog)
// per pixel; they must correlate and agree in mean. This found a real bug: the
// #455 footprint-edge dissolve blended the fogged terrain into an UNFOGGED floor
// on the GPU (the CPU's Shade fogs it), a dark band along the plate edge — the
// twin's in-scatter was 17% low (corr 0.88). Fixed in the HLSL + twin.

using System;
using System.Collections.Generic;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S310ReliefVolumetricParityTests
{
    private const int W = 240, H = 180;

    /// <summary>The GPU relief kernel's CPU twin behind the kernel seam.</summary>
    private sealed class TwinKernel : IReliefRaymarchKernel
    {
        public int Calls;
        public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst,
            float[]? aovNormalXyz = null, float[]? aovDepth = null)
        {
            Calls++;
            ReliefRaymarchGpu.RenderCpuMirror(in u, hbuf, keep, albedo, dst, out _, out _, aovNormalXyz, aovDepth);
        }
        public void Dispose() { }
    }

    // A tall bump in front of a lower plateau, so a low light casts shafts.
    private static (float[] height, uint[] albedo) Field()
    {
        var hbuf = new float[W * H];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double u = (x + 0.5) / W - 0.5, v = (y + 0.5) / H - 0.5;
            double r1 = Math.Sqrt((u + 0.15) * (u + 0.15) + v * v) / 0.22;
            double r2 = Math.Sqrt((u - 0.2) * (u - 0.2) + (v - 0.1) * (v - 0.1)) / 0.3;
            float a = r1 >= 1 ? 0f : (float)(0.5 * (1 + Math.Cos(Math.PI * r1)));
            float b = r2 >= 1 ? 0f : (float)(0.25 * (1 + Math.Cos(Math.PI * r2)));
            hbuf[y * W + x] = Math.Max(a, b);
        }
        var albedo = new uint[W * H];
        Array.Fill(albedo, 0xFFB06030u);
        return (hbuf, albedo);
    }

    private static FractalParameters Params() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DGpuRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 35,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = true,
        Relief2DAutoShade = false,   // identical explicit lighting on both paths
    };

    private static uint[] Render(LightingFxData fx, bool twin)
    {
        var (hb, al) = Field();
        var p = Params();
        p.Lighting = fx;
        var dst = new uint[W * H];
        var kernel = twin ? new TwinKernel() : null;
        HeightfieldRaymarch2D.Render(al, hb, W, H, W, H, p, dst, out _, kernel);
        if (twin) Assert.True(kernel!.Calls > 0, "the twin kernel was not used (the frame took the CPU trace)");
        return dst;
    }

    private static double MeanDrift(uint[] a, uint[] b)
    {
        long s = 0;
        for (int i = 0; i < a.Length; i++)
            for (int k = 0; k < 24; k += 8)
                s += Math.Abs((int)((a[i] >> k) & 0xFF) - (int)((b[i] >> k) & 0xFF));
        return s / (a.Length * 3.0);
    }

    private static double[] Delta(uint[] a, uint[] b)
    {
        var d = new double[a.Length];
        for (int i = 0; i < a.Length; i++)
            for (int k = 0; k < 24; k += 8)
                d[i] += (int)((a[i] >> k) & 0xFF) - (int)((b[i] >> k) & 0xFF);
        return d;
    }

    private static (double corr, double meanA, double meanB) Compare(double[] a, double[] b)
    {
        double n = a.Length, sa = 0, sb = 0;
        for (int i = 0; i < a.Length; i++) { sa += a[i]; sb += b[i]; }
        double ma = sa / n, mb = sb / n, cov = 0, va = 0, vb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            cov += (a[i] - ma) * (b[i] - mb);
            va += (a[i] - ma) * (a[i] - ma);
            vb += (b[i] - mb) * (b[i] - mb);
        }
        return (cov / Math.Sqrt(va * vb + 1e-30), ma, mb);
    }

    public static IEnumerable<object[]> Scenes => new[]
    {
        new object[] { "side-lit", 1.48, 1.0, 2.0 },
        new object[] { "back-lit", 1.45, 3.5, 1.5 },
    };

    [Theory]
    [MemberData(nameof(Scenes))]
    public void GpuRelief_Twin_Matches_Cpu_InScatter_And_Shafts(string scene, double phi, double theta, double density)
    {
        var baseFx = LightingFxData.CreateDefault();
        var l = baseFx.Light1;
        l.Phi = phi; l.Theta = theta; l.Intensity = 1.5;
        baseFx.Light1 = l;
        baseFx.ShadowSteps = 0;
        var fog = baseFx;
        fog.FogDensity = density; fog.VolumeSteps = 64; fog.VolumeAnisotropy = 0.7;
        var shafts = fog;
        shafts.ShadowSteps = 24; shafts.ShadowLightMask = 0x1; shafts.ShadowSoftK = 8.0;

        uint[] cBase = Render(baseFx, false), tBase = Render(baseFx, true);
        uint[] cFog = Render(fog, false), tFog = Render(fog, true);
        uint[] cShaft = Render(shafts, false), tShaft = Render(shafts, true);

        Assert.True(MeanDrift(cFog, tFog) < 1.5, $"{scene}: fog beauty drift {MeanDrift(cFog, tFog):F2}");
        Assert.True(MeanDrift(cShaft, tShaft) < 1.5, $"{scene}: shaft beauty drift {MeanDrift(cShaft, tShaft):F2}");

        var (ci, mic, mit) = Compare(Delta(cFog, cBase), Delta(tFog, tBase));
        Assert.True(mic > 10, $"{scene}: fog adds too little in-scatter to compare ({mic:F1})");
        Assert.True(ci > 0.98, $"{scene}: in-scatter correlation {ci:F3}");
        Assert.True(Math.Abs(mit - mic) <= 0.05 * Math.Abs(mic), $"{scene}: in-scatter mean CPU {mic:F2} vs GPU twin {mit:F2}");

        var (cs, msc, mst) = Compare(Delta(cShaft, cFog), Delta(tShaft, tFog));
        Assert.True(msc < -10, $"{scene}: shadowing barely carves the medium ({msc:F1}) — no shafts to compare");
        Assert.True(cs > 0.97, $"{scene}: shaft correlation {cs:F3}");
        Assert.True(Math.Abs(mst - msc) <= 0.05 * Math.Abs(msc), $"{scene}: shaft mean CPU {msc:F2} vs GPU twin {mst:F2}");
    }
}
