// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/Lighting/RaymarchGroupShapes.cs
//
// #1377 — the representative frames the thread-group shape tuner (#1339,
// GpuGroupShapes) times for the relief raymarch and the two froxel passes. The
// 2D slots run through IGpuKernel with a handful of scalars; these kernels need
// uniforms, a height field and (froxel) a beauty + depth buffer, so the scene is
// built once by running the real relief render (HeightfieldRaymarch2D.Render)
// against a capturing kernel: the uniforms it packs are exactly the ones a live
// frame would send. The capture fills the beauty and the depth with the CPU
// twin (ReliefRaymarchGpu.RenderCpuMirror), so the froxel composite reads a
// real silhouette, not a constant.
//
// The scene: a rough procedural terrain (ridges and bumps, so neighbouring
// rays hit, miss and shadow differently: the divergence a group shape changes)
// at 256×192, one shadowed key light, 5 AO samples, specular, and froxel fog
// (Balanced grid), which the render composites as a separate pass (the relief
// uniforms then carry no per-pixel fog, as on a live froxel frame).

using System;
using FracturingFog.Models;

namespace FracturingFog.Rendering.Lighting;

/// <summary>#1377 — representative relief / froxel frames for the group-shape
/// tuner. See the file header.</summary>
public static class RaymarchGroupShapes
{
    public const int Width = 256, Height = 192;

    /// <summary>One captured relief + froxel frame.</summary>
    public sealed class Scene
    {
        public int W { get; init; }
        public int H { get; init; }
        public ReliefUniforms Relief { get; init; }
        public float[] Field { get; init; } = [];
        public byte[]? Keep { get; init; }
        public uint[] Albedo { get; init; } = [];
        public FroxelGpuUniforms Froxel { get; init; }
        /// <summary>The fog-free beauty the froxel pass composites over (packed ARGB).</summary>
        public uint[] Beauty { get; init; } = [];
        /// <summary>World depth per pixel (the relief depth AOV).</summary>
        public float[] Depth { get; init; } = [];
    }

    private static readonly object s_lock = new();
    private static Scene? s_scene;

    /// <summary>The <see cref="Width"/>×<see cref="Height"/> scene the tuner times,
    /// built on first use.</summary>
    public static Scene Representative
    {
        get { lock (s_lock) return s_scene ??= Capture(Width, Height); }
    }

    /// <summary>The scene's render parameters (probes and tests render it at other sizes).</summary>
    public static FractalParameters Params()
    {
        var fx = LightingFxData.CreateDefault();
        var l = fx.Light1; l.Phi = 1.2; l.Theta = 0.9; l.Intensity = 1.4; fx.Light1 = l;
        fx.ShadowSteps = 32; fx.ShadowLightMask = 0x1; fx.ShadowSoftK = 8.0;
        fx.AoSamples = 5; fx.AoStrength = 0.8;
        fx.SpecularStrength = 1.0; fx.Roughness = 0.35;
        fx.FogDensity = 0.6; fx.VolumeSteps = 16;
        return new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DGpuRaymarch = true,
            Relief2DHeightScale = 1.4, Relief2DCameraAzimuthDeg = 25, Relief2DCameraElevationDeg = 35,
            Relief2DCameraFovDeg = 55, Relief2DGroundPlane = true, Relief2DAutoShade = false,
            Relief2DFroxelVolumetrics = true, Relief2DFroxelQuality = FroxelQuality.Balanced,
            Lighting = fx,
        };
    }

    /// <summary>The terrain: ridges plus two bumps, in [0, 1], and an albedo ramp.</summary>
    public static (float[] Height, uint[] Albedo) Terrain(int w, int h)
    {
        var hb = new float[w * h];
        var al = new uint[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double u = (x + 0.5) / w - 0.5, v = (y + 0.5) / h - 0.5;
            double ridges = 0.18 * Math.Sin(23 * u + 4 * v) * Math.Cos(17 * v - 3 * u) + 0.08 * Math.Sin(61 * u * v + 9 * u);
            double r1 = Math.Sqrt((u + 0.15) * (u + 0.15) + v * v) / 0.22;
            double r2 = Math.Sqrt((u - 0.2) * (u - 0.2) + (v - 0.1) * (v - 0.1)) / 0.3;
            double a = r1 >= 1 ? 0 : 0.45 * (1 + Math.Cos(Math.PI * r1));
            double b = r2 >= 1 ? 0 : 0.2 * (1 + Math.Cos(Math.PI * r2));
            hb[y * w + x] = (float)Math.Clamp(0.3 + ridges + Math.Max(a, b) * 0.6, 0, 1);
            al[y * w + x] = 0xFF000000u | ((uint)(80 + 120 * (u + 0.5)) << 16) | ((uint)(90 + 100 * (v + 0.5)) << 8) | 0x60u;
        }
        return (hb, al);
    }

    /// <summary>Render the scene at <paramref name="w"/>×<paramref name="h"/>
    /// through the real relief render and capture what it hands the kernels
    /// (<paramref name="p"/>: other render parameters, default <see cref="Params"/>).
    /// The beauty and depth come from <paramref name="fill"/> when given (a GPU
    /// kernel, for frames too big for the CPU twin), else the CPU twin.</summary>
    public static Scene Capture(int w, int h, FractalParameters? p = null, IReliefRaymarchKernel? fill = null)
    {
        var (hb, al) = Terrain(w, h);
        var cap = new Capturer { Fill = fill };
        HeightfieldRaymarch2D.Render(al, hb, w, h, w, h, p ?? Params(), new uint[w * h], out _, cap, froxelKernel: cap);
        if (cap.Field == null || cap.Beauty == null || cap.Depth == null)
            throw new InvalidOperationException("the relief render did not take the GPU relief + froxel branch");
        return new Scene
        {
            W = w, H = h, Relief = cap.Relief, Field = cap.Field, Keep = cap.Keep, Albedo = cap.Albedo!,
            Froxel = cap.Froxel, Beauty = cap.Beauty, Depth = cap.Depth,
        };
    }

    /// <summary>Run the relief frame on <paramref name="kernel"/> (dispatching its
    /// current trial shape) and return its GPU dispatch time, or the host's when
    /// the device has no timestamps.</summary>
    public static double TimeRelief(IReliefRaymarchKernel kernel, Scene? scene = null, uint[]? dst = null)
    {
        var s = scene ?? Representative;
        kernel.Run(s.Relief, s.Field, s.Keep, s.Albedo, dst ?? new uint[s.W * s.H]);
        return kernel.LastGpuStageTimes is { DispatchMs: > 0 } t ? t.DispatchMs : kernel.LastDispatchMs;
    }

    /// <summary>Run the froxel frame on <paramref name="kernel"/> and return the GPU
    /// time of <paramref name="slot"/>'s pass, or the host time of both passes when
    /// the device has no timestamps (the other pass is the same in every trial, so
    /// the difference is still the trial shape's).</summary>
    public static double TimeFroxel(IFroxelVolumeKernel kernel, string slot, Scene? scene = null, uint[]? dst = null)
    {
        var s = scene ?? Representative;
        kernel.Composite(s.Froxel, s.Beauty, s.Depth, s.W, s.H, dst ?? new uint[s.W * s.H]);
        if (kernel.LastPassTimes is { } p)
        {
            double ms = slot == GpuGroupShapes.SlotFroxelIntegrate ? p.IntegrateMs : p.CompositeMs;
            if (ms > 0) return ms;
        }
        return kernel.LastDispatchMs;
    }

    // Records the relief uniforms and buffers, fills the beauty + depth with the
    // CPU twin, and records the froxel uniforms and the buffers it composites.
    private sealed class Capturer : IReliefRaymarchKernel, IFroxelVolumeKernel
    {
        public IReliefRaymarchKernel? Fill;
        public ReliefUniforms Relief;
        public float[]? Field;
        public byte[]? Keep;
        public uint[]? Albedo;
        public FroxelGpuUniforms Froxel;
        public uint[]? Beauty;
        public float[]? Depth;

        public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst,
            float[]? aovNormalXyz = null, float[]? aovDepth = null)
        {
            Relief = u;
            Field = (float[])hbuf.Clone();
            Keep = (byte[]?)keep?.Clone();
            Albedo = (uint[])albedo.Clone();
            if (Fill != null) Fill.Run(in u, hbuf, keep, albedo, dst, aovNormalXyz, aovDepth);
            else ReliefRaymarchGpu.RenderCpuMirror(in u, hbuf, keep, albedo, dst, out _, out _, aovNormalXyz, aovDepth);
        }

        public void Composite(in FroxelGpuUniforms u, uint[] beauty, float[] worldDepth, int w, int h, uint[] dst)
        {
            Froxel = u;
            Beauty = (uint[])beauty.Clone();
            Depth = (float[])worldDepth.Clone();
        }

        public void Dispose() { }
    }
}
