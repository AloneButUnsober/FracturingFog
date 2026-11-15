// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1479 — the Vulkan relief raymarch (ReliefRaymarchVulkanKernel) runs in tiles, as
// the D3D11 kernel does (#1044): ReliefGpuTiling sizes them from the frame's cost
// model and the learned GPU speed, in whole groups of the tuned shape (#1377), and
// each tile is its own command buffer and submit, so the watchdog sees short packets.
// Before, the whole frame was one dispatch: seconds with volumetric fog on a slow GPU
// (Windows TDR, a Linux GPU hang timeout). Checked, on whatever Vulkan device the host
// has (skipped without one):
//   • the frame in tiles (row bands, column tiles, odd sizes) gives the same colour
//     and AOV bytes as the frame in one tile. Another view runs before each tiled
//     frame, so a tile that wrote the wrong pixels (or none) can't pass on its leftovers;
//   • LastBandCount is the tile count, the GPU timestamps count one packet per tile,
//     and the tiles queue back to back: one queue wait per frame;
//   • a fog-heavy frame starts in several tiles (the pessimistic first speed), and the
//     learned speed then moves the tile count;
//   • a VK_ERROR_DEVICE_LOST failure is what Run turns into GpuDeviceLostException
//     (which the relief render catches and falls back to the CPU trace on); other
//     failures are not.

using System;
using System.Linq;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.Rendering.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1479VulkanReliefTilingTests : IDisposable
{
    private const int W = 200, H = 136, N = W * H;

    public void Dispose() => ReliefRaymarchVulkanKernel.ForceTileSize = default;

    // Records what HeightfieldRaymarch2D hands the kernel (the real uniforms + inputs).
    private sealed class Capture : IReliefRaymarchKernel
    {
        public ReliefUniforms U;
        public float[] Hbuf = [];
        public byte[]? Keep;
        public uint[] Albedo = [];
        public bool Called;
        public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst,
            float[]? aovNormalXyz = null, float[]? aovDepth = null)
        {
            U = u; Hbuf = (float[])hbuf.Clone(); Keep = (byte[]?)keep?.Clone(); Albedo = (uint[])albedo.Clone();
            Called = true;
        }
        public void Dispose() { }
    }

    private static (float[] height, uint[] albedo) Field()
    {
        var hbuf = new float[N];
        var albedo = new uint[N];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double u = (x + 0.5) / W - 0.5, v = (y + 0.5) / H - 0.5;
            double r1 = Math.Sqrt((u + 0.15) * (u + 0.15) + v * v) / 0.22;
            double r2 = Math.Sqrt((u - 0.2) * (u - 0.2) + (v - 0.1) * (v - 0.1)) / 0.3;
            float a = r1 >= 1 ? 0f : (float)(0.5 * (1 + Math.Cos(Math.PI * r1)));
            float b = r2 >= 1 ? 0f : (float)(0.25 * (1 + Math.Cos(Math.PI * r2)));
            hbuf[y * W + x] = Math.Max(a, b) + 0.05f * (float)Math.Sin(x * 0.3) * (float)Math.Cos(y * 0.25);
            albedo[y * W + x] = 0xFF000000u | ((uint)(80 + 120 * u + 60) << 16) | ((uint)(90 + 100 * v + 50) << 8) | 0x60u;
        }
        return (hbuf, albedo);
    }

    private static Capture Scene(double azimuth, bool fog, int volumeSteps = 16)
    {
        var fx = LightingFxData.CreateDefault();
        var l = fx.Light1; l.Phi = 1.2; l.Theta = 1.0; l.Intensity = 1.4; fx.Light1 = l;
        fx.ShadowSteps = 32; fx.ShadowLightMask = 0x1; fx.ShadowSoftK = 8.0;
        fx.AoSamples = 5; fx.AoStrength = 0.8;
        fx.SpecularStrength = 1.0; fx.Roughness = 0.35;
        if (fog) { fx.FogDensity = 0.6; fx.VolumeSteps = volumeSteps; }
        var p = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DGpuRaymarch = true,
            Relief2DHeightScale = 1.4, Relief2DCameraAzimuthDeg = azimuth, Relief2DCameraElevationDeg = 35,
            Relief2DCameraFovDeg = 55, Relief2DGroundPlane = true, Relief2DAutoShade = false,
            Lighting = fx,
        };
        var (hb, al) = Field();
        var cap = new Capture();
        HeightfieldRaymarch2D.Render(al, hb, W, H, W, H, p, new uint[N], out _, cap);
        Assert.True(cap.Called, "precondition: the relief render took the GPU branch");
        return cap;
    }

    private sealed class Frame
    {
        public readonly uint[] Col = new uint[N];
        public readonly float[] Nrm = new float[N * 3], Depth = new float[N];
        public int Tiles, Waits, Packets;
    }

    private static Frame Run(ReliefRaymarchVulkanKernel k, Capture c, (int Rows, int Cols) tile)
    {
        var f = new Frame();
        ReliefRaymarchVulkanKernel.ForceTileSize = tile;
        try { k.Run(in c.U, c.Hbuf, c.Keep, c.Albedo, f.Col, f.Nrm, f.Depth); }
        finally { ReliefRaymarchVulkanKernel.ForceTileSize = default; }
        f.Tiles = k.LastBandCount;
        f.Waits = k.LastFrameQueueWaits;
        f.Packets = k.LastGpuStageTimes?.Packets ?? -1;
        return f;
    }

    private static string? FirstDifference(Frame a, Frame b)
    {
        for (int i = 0; i < N; i++)
        {
            if (a.Col[i] != b.Col[i]) return $"colour at ({i % W}, {i / W})";
            if (BitConverter.SingleToInt32Bits(a.Depth[i]) != BitConverter.SingleToInt32Bits(b.Depth[i])) return $"depth at ({i % W}, {i / W})";
            for (int c = 0; c < 3; c++)
                if (BitConverter.SingleToInt32Bits(a.Nrm[i * 3 + c]) != BitConverter.SingleToInt32Bits(b.Nrm[i * 3 + c]))
                    return $"normal at ({i % W}, {i / W})";
        }
        return null;
    }

    private static ReliefRaymarchVulkanKernel Device()
    {
        var k = ReliefRaymarchVulkanKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        return k;
    }

    private static int RoundUp(int v, int m) => (v + m - 1) / m * m;

    [Fact]
    public void Tiled_Equals_One_Tile_And_Counts_Its_Packets()
    {
        using var k = Device();
        var scene = Scene(25, fog: true);
        var other = Scene(140, fog: true);
        var shape = k.GroupShape;
        var whole = Run(k, scene, (1 << 16, 1 << 16));
        Assert.Equal(1, whole.Tiles);
        Assert.True(whole.Col.Distinct().Count() > 100, "precondition: a flat frame");

        foreach (var tile in new[] { (8, 1 << 16), (32, 64), (24, 40), (H, 72), (8, 8) })
        {
            var dirty = Run(k, other, (1 << 16, 1 << 16));
            int differ = Enumerable.Range(0, N).Count(i => dirty.Col[i] != whole.Col[i]);
            Assert.True(differ > N / 2, $"precondition: the dirty view differs on only {differ} of {N} pixels");

            var tiled = Run(k, scene, tile);
            int rows = Math.Min(RoundUp(tile.Item1, shape.Y), RoundUp(1 << 16, shape.Y));
            int cols = RoundUp(tile.Item2, shape.X);
            int expected = ((H + rows - 1) / rows) * ((W + cols - 1) / cols);
            Assert.Equal(expected, tiled.Tiles);
            Assert.True(tiled.Tiles > 1, $"{tile}: precondition: several tiles");
            Assert.Null(FirstDifference(whole, tiled));
            Assert.Equal(1, tiled.Waits);   // the tiles queue back to back
            if (tiled.Packets >= 0) Assert.Equal(tiled.Tiles, tiled.Packets);   // one GPU packet per tile
        }
    }

    [Fact]
    public void A_Fog_Heavy_Frame_Starts_Tiled_And_The_Learned_Speed_Moves_The_Tiles()
    {
        using var k = Device();
        // 200 x 136 with 256 fog steps, one shadowed light: about 1e4 cost units per
        // pixel, so the first frame (pessimistic 6 ns per unit) predicts ~1.6 s.
        var heavy = Scene(25, fog: true, volumeSteps: 256);
        double cost = ReliefGpuTiling.CostPerPixel(in heavy.U);
        var (r0, c0) = ReliefGpuTiling.TileSize(W, H, cost, ReliefGpuTiling.InitialNsPerUnit, k.GroupShape);
        int expected0 = ((H + r0 - 1) / r0) * ((W + c0 - 1) / c0);
        Assert.True(expected0 > 1, $"precondition: {expected0} tile(s) at the starting speed");
        var first = Run(k, heavy, default);
        Assert.Equal(expected0, first.Tiles);

        // The speed is learned from the measured frame: the next frames' tile counts follow it.
        var counts = Enumerable.Range(0, 3).Select(_ => Run(k, heavy, default).Tiles).ToArray();
        Assert.True(counts.Any(c => c != first.Tiles), $"the tile count never moved from {first.Tiles}: {string.Join(", ", counts)}");
        Assert.Null(FirstDifference(first, Run(k, heavy, (1 << 16, 1 << 16))));
    }

    [Fact]
    public void Only_A_Lost_Device_Becomes_The_Device_Lost_Exception()
    {
        Assert.True(ReliefRaymarchVulkanKernel.IsDeviceLost(
            new ReliefRaymarchVulkanKernel.VulkanResultException(Result.ErrorDeviceLost, "vkQueueWaitIdle failed")));
        Assert.False(ReliefRaymarchVulkanKernel.IsDeviceLost(
            new ReliefRaymarchVulkanKernel.VulkanResultException(Result.ErrorOutOfDeviceMemory, "vkAllocateMemory failed")));
        Assert.False(ReliefRaymarchVulkanKernel.IsDeviceLost(new InvalidOperationException("ErrorDeviceLost")));
        // Still an InvalidOperationException, as every Vulkan failure was before.
        Assert.IsAssignableFrom<InvalidOperationException>(
            new ReliefRaymarchVulkanKernel.VulkanResultException(Result.ErrorDeviceLost, "x"));
    }
}
