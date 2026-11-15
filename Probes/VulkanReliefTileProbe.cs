// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1479: --vkrelieftileprobe. The Vulkan relief raymarch in tiles (ReliefGpuTiling,
// #1044) on a fog-heavy 1920x1080 frame, the kind that ran for seconds as one dispatch
// on a GeForce GT 710 (watchdog). Five frames with the learned tile size: per frame the
// tile count, the wall time, the GPU packets and the slowest packet (#1329 timestamps).
// The last one is checked byte for byte against the same frame in 64-row bands (never
// as one dispatch: that is the frame that stalls). A lost device is a FAIL.
// Exit 0 = pass or SKIP; 1 = a frame differs or the device was lost.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.Rendering.Vulkan;

namespace FracturingFog
{
    internal static class VulkanReliefTileProbe
    {
        private const int W = 1920, H = 1080, N = W * H;

        private sealed class Capture : IReliefRaymarchKernel
        {
            public ReliefUniforms U;
            public float[] Hbuf = [];
            public byte[]? Keep;
            public uint[] Albedo = [];
            public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst,
                float[]? aovNormalXyz = null, float[]? aovDepth = null)
            { U = u; Hbuf = hbuf; Keep = keep; Albedo = albedo; }
            public void Dispose() { }
        }

        public static int Run()
        {
            using var k = ReliefRaymarchVulkanKernel.TryCreateWithOwnContext();
            if (k == null) { Console.WriteLine("vkrelieftileprobe SKIP: no Vulkan device."); return 0; }
            Console.WriteLine($"vkrelieftileprobe: {k.Description}, {W}x{H}, group {k.GroupShape.X}x{k.GroupShape.Y}");

            var hbuf = new float[N]; var albedo = new uint[N];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double u = (x + 0.5) / W - 0.5, v = (y + 0.5) / H - 0.5;
                    double r = Math.Sqrt(u * u + v * v) / 0.35;
                    hbuf[y * W + x] = (float)((r >= 1 ? 0 : 0.5 * (1 + Math.Cos(Math.PI * r))) + 0.04 * Math.Sin(x * 0.05) * Math.Cos(y * 0.04));
                    albedo[y * W + x] = 0xFF000000u | ((uint)(120 + 100 * u) << 16) | ((uint)(110 + 90 * v) << 8) | 0x70u;
                }
            var fx = LightingFxData.CreateDefault();
            var l = fx.Light1; l.Phi = 1.2; l.Theta = 1.0; l.Intensity = 1.4; fx.Light1 = l;
            fx.ShadowSteps = 48; fx.ShadowLightMask = 0x1; fx.ShadowSoftK = 8.0;
            fx.AoSamples = 5; fx.AoStrength = 0.8;
            fx.FogDensity = 0.6; fx.VolumeSteps = 64;   // volumetric fog with per-step shadow marches
            var p = new FractalParameters
            {
                Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DGpuRaymarch = true,
                Relief2DHeightScale = 1.4, Relief2DCameraAzimuthDeg = 25, Relief2DCameraElevationDeg = 35,
                Relief2DCameraFovDeg = 55, Relief2DGroundPlane = true, Relief2DAutoShade = false, Lighting = fx,
            };
            var cap = new Capture();
            HeightfieldRaymarch2D.Render(albedo, hbuf, W, H, W, H, p, new uint[N], out _, cap);
            Console.WriteLine($"  cost model: {ReliefGpuTiling.CostPerPixel(in cap.U):F0} units per pixel");

            var auto = new uint[N]; var banded = new uint[N];
            try
            {
                for (int f = 0; f < 5; f++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    k.Run(in cap.U, cap.Hbuf, cap.Keep, cap.Albedo, auto);
                    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    var segs = new List<double>();
                    var g = k.LastGpuStageTimes;
                    Console.WriteLine($"  frame {f}: {k.LastBandCount,4} tiles, wall {ms,8:F1} ms, " +
                                      $"{(g is { } gt ? $"GPU {gt.DispatchMs,8:F1} ms in {gt.Packets} packets (mean {gt.DispatchMs / Math.Max(1, gt.Packets):F1} ms)" : "no timestamps")}");
                }
                ReliefRaymarchVulkanKernel.ForceTileSize = (64, 1 << 16);
                try { k.Run(in cap.U, cap.Hbuf, cap.Keep, cap.Albedo, banded); }
                finally { ReliefRaymarchVulkanKernel.ForceTileSize = default; }
            }
            catch (GpuDeviceLostException ex)
            {
                Console.WriteLine($"vkrelieftileprobe FAIL: device lost ({ex.Message})");
                return 1;
            }
            int diff = -1;
            for (int i = 0; i < N; i++) if (auto[i] != banded[i]) { diff = i; break; }
            Console.WriteLine($"  learned tiles == 64-row bands ({k.LastBandCount} tiles): {(diff < 0 ? "yes" : $"NO, pixel ({diff % W}, {diff / W})")}");
            Console.WriteLine(diff < 0 ? "vkrelieftileprobe OK" : "vkrelieftileprobe FAIL");
            return diff < 0 ? 0 : 1;
        }
    }
}
