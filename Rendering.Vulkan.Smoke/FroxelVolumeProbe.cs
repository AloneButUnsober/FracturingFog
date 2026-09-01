// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FroxelVolumeProbe.cs — Vulkan froxel compute gate (--vulkanfroxel, S6 #408).
//
// The Vulkan sibling of the D3D --froxelgpu gate. Proves the cross-platform
// froxel compute kernel (FroxelVolumeVulkanKernel / CSFroxelIntegrate +
// CSFroxelComposite, DXC → SPIR-V) is correct by diffing a Vulkan dispatch
// against the pure-CPU froxel pass (FroxelCameraVolume.Apply) over identical
// inputs — the SAME FroxelGrid + FroxelMedium (via FroxelGpuUniforms) drive both.
// The scene + medium + tolerances mirror the D3D gate so both backends diff the
// same CPU oracle. Runs on Mesa lavapipe (software Vulkan) in CI; float32 only.
//
// The CPU pass is double, the shader is float, so exact bit-equality is not
// expected: the gate tolerates a small mean per-channel diff (same threshold as
// the relief + D3D froxel gates). Both composited images are written as PPMs.

using System;
using System.Globalization;
using System.IO;

using FracturingFog.Models;               // FractalParameters
using FracturingFog.Rendering.Lighting;   // FroxelCameraVolume, FroxelGpuUniforms, LightingFxData
using FracturingFog.Rendering.Vulkan;     // FroxelVolumeVulkanKernel, VulkanContext

namespace FracturingFog.Rendering.Vulkan.Smoke;

/// <summary>Headless gate for the #408 Vulkan froxel compute pass. See header.</summary>
internal static class FroxelVolumeProbe
{
    // A fog-free beauty + a per-pixel world-depth field spanning near..far (plus a
    // sky strip at huge depth) — identical to the D3D --froxelgpu gate's Scene.
    private static (uint[] beauty, float[] depth) Scene(int w, int h, double near, double far)
    {
        var beauty = new uint[w * h];
        var depth = new float[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            byte r = (byte)(40 + 180 * x / (double)(w - 1));
            byte g = (byte)(60 + 150 * y / (double)(h - 1));
            byte b = (byte)(200 - 120 * x / (double)(w - 1));
            beauty[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

            if (y < h / 8)
                depth[i] = 1e30f;   // sky strip -> beyond-far (full integrated column)
            else
            {
                double t = (x + 0.5) / w;
                depth[i] = (float)(near * 1.01 + t * (far * 1.2 - near * 1.01));
            }
        }
        return (beauty, depth);
    }

    // The lit fog medium; `density` + `noise` vary per frame in the temporal gate.
    private static LightingFxData SceneFx(double density, double noise)
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = density;
        fx.VolumeAnisotropy = 0.3;
        fx.VolumeNoiseAmount = noise;
        fx.VolumeNoiseScale = 0.5;
        fx.VolumeNoiseOctaves = 3;
        fx.Light1.Intensity = 1.0;
        fx.Light1.Color = 0xFFFFE8C0u;
        fx.Light2.Type = LightType.Point;
        fx.Light2.Intensity = 0.8;
        fx.Light2.Color = 0xFFC0D8FFu;
        fx.Light2.PosX = 0.4; fx.Light2.PosY = 1.1; fx.Light2.PosZ = 0.3;
        fx.Light2.Range = 4.0;
        fx.Light3.Type = LightType.Spot;
        fx.Light3.Intensity = 0.6;
        fx.Light3.Color = 0xFFE0FFE0u;
        fx.Light3.PosX = -0.4; fx.Light3.PosY = 1.0; fx.Light3.PosZ = -0.3;
        fx.Light3.Range = 4.0;
        fx.Light3.SpotInnerDeg = 30.0;
        fx.Light3.SpotOuterDeg = 60.0;
        return fx;
    }

    /// <summary>CLI entry (`--vulkanfroxeltemporal`). The Vulkan sibling of the D3D
    /// --froxelgputemporal gate: two frames with a CHANGED medium (grid identity fixed)
    /// so the temporal blend is exercised, diffing the Vulkan kernel's device-side
    /// history against the CPU FroxelHistory, and asserting the blend actually shifts
    /// the result vs the single-frame composite.</summary>
    public static int RunTemporal(VulkanContext ctx)
    {
        const int w = 200, h = 150;
        const double fb = 0.7;

        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DFroxelVolumetrics = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
        };
        double aspect = (double)w / h;
        var cam = HeightfieldRaymarch2D.BuildObliqueCamera(w, h, aspect, sy: 0.35, maxH: 1.0, p);

        var fxA = SceneFx(0.35, 0.15);
        var fxB = SceneFx(0.75, 0.35);   // denser / noisier — the "animated" 2nd frame
        var grid = FroxelCameraVolume.BuildGrid(in cam);
        var (beauty, depth) = Scene(w, h, grid.Near, grid.Far);

        // CPU oracle — one persistent history, two frames.
        var hist = new FroxelHistory();
        _ = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fxA, hist, temporal: true, feedback: fb);
        var cpu2 = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fxB, hist, temporal: true, feedback: fb);
        var cpuSingleB = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fxB);

        // Vulkan — one kernel (persistent device history), two frames.
        var uA = FroxelGpuUniforms.Build(in cam, in fxA);
        var uB = FroxelGpuUniforms.Build(in cam, in fxB);
        var gpu2 = new uint[w * h];
        try
        {
            using var kernel = new FroxelVolumeVulkanKernel(ctx);
            var gpu1 = new uint[w * h];
            kernel.Composite(in uA, beauty, depth, w, h, gpu1, fb);   // seeds history
            kernel.Composite(in uB, beauty, depth, w, h, gpu2, fb);   // blends against frame A
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vulkanfroxeltemporal FAIL: GPU dispatch threw — {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        long sumAbs = 0; int maxAbs = 0; long bad = 0; int nz = 0; long tempChanged = 0;
        for (int i = 0; i < w * h; i++)
        {
            uint a = cpu2[i], b = gpu2[i];
            int dr = Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
            int dg = Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF));
            int db = Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));
            int m = Math.Max(dr, Math.Max(dg, db));
            sumAbs += dr + dg + db;
            if (m > maxAbs) maxAbs = m;
            if (m > 16) bad++;
            if (((a >> 24) & 0xFF) != ((b >> 24) & 0xFF)) nz++;
            if (cpu2[i] != cpuSingleB[i]) tempChanged++;
        }
        double meanCh = sumAbs / (3.0 * w * h);
        double badFrac = (double)bad / (w * h);
        double tempChangedFrac = (double)tempChanged / (w * h);

        WritePpm(Path.Combine(AppContext.BaseDirectory, "froxel-vk-temporal-cpu.ppm"), cpu2, w, h);
        WritePpm(Path.Combine(AppContext.BaseDirectory, "froxel-vk-temporal-gpu.ppm"), gpu2, w, h);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"vulkanfroxeltemporal {w}x{h} feedback={fb:0.00} dev={ctx.PickedType}:{ctx.PickedName}:"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  grid near/far         {grid.Near:0.000} / {grid.Far:0.000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  temporal shifted frac {tempChangedFrac:0.0000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  mean channel diff     {meanCh:0.000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  max channel diff      {maxAbs}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  bad pixels >16        {badFrac:0.0000}  ({bad} px)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  alpha mismatches      {nz}"));

        bool ok = tempChangedFrac > 0.10 && meanCh < 2.0 && badFrac < 0.03 && nz == 0;
        Console.WriteLine(ok
            ? $"vulkanfroxeltemporal OK: {ctx.PickedType} {ctx.PickedName}"
            : "vulkanfroxeltemporal FAIL: outside band (temporal shifted>10%, mean<2.0, edge<3%, alpha exact).");
        return ok ? 0 : 1;
    }

    /// <summary>CLI entry (`--vulkanfroxel`).</summary>
    public static int Run(VulkanContext ctx)
    {
        const int w = 200, h = 150;

        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DFroxelVolumetrics = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
        };
        double aspect = (double)w / h;
        var cam = HeightfieldRaymarch2D.BuildObliqueCamera(w, h, aspect, sy: 0.35, maxH: 1.0, p);

        // Fog medium: density + anisotropy + FBM heterogeneity + all three lights
        // (directional / point / spot) so every populate path is exercised — same as
        // the D3D gate.
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.6;
        fx.VolumeAnisotropy = 0.3;
        fx.VolumeNoiseAmount = 0.2;
        fx.VolumeNoiseScale = 0.5;
        fx.VolumeNoiseOctaves = 3;
        fx.Light1.Intensity = 1.0;
        fx.Light1.Color = 0xFFFFE8C0u;
        fx.Light2.Type = LightType.Point;
        fx.Light2.Intensity = 0.8;
        fx.Light2.Color = 0xFFC0D8FFu;
        fx.Light2.PosX = 0.4; fx.Light2.PosY = 1.1; fx.Light2.PosZ = 0.3;
        fx.Light2.Range = 4.0;
        fx.Light3.Type = LightType.Spot;
        fx.Light3.Intensity = 0.6;
        fx.Light3.Color = 0xFFE0FFE0u;
        fx.Light3.PosX = -0.4; fx.Light3.PosY = 1.0; fx.Light3.PosZ = -0.3;
        fx.Light3.Range = 4.0;
        fx.Light3.SpotInnerDeg = 30.0;
        fx.Light3.SpotOuterDeg = 60.0;

        var grid = FroxelCameraVolume.BuildGrid(in cam);
        var (beauty, depth) = Scene(w, h, grid.Near, grid.Far);

        // CPU oracle.
        var cpu = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fx);

        // GPU dispatch on the picked Vulkan device.
        var u = FroxelGpuUniforms.Build(in cam, in fx);
        var gpu = new uint[w * h];
        try
        {
            using var kernel = new FroxelVolumeVulkanKernel(ctx);
            kernel.Composite(in u, beauty, depth, w, h, gpu);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vulkanfroxel FAIL: GPU dispatch threw — {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        // Diff (RGB; alpha must match exactly). Count fog-changed pixels so a no-op
        // (density lost) fails.
        long sumAbs = 0; int maxAbs = 0; long bad = 0; int nz = 0; long changed = 0;
        for (int i = 0; i < w * h; i++)
        {
            uint a = cpu[i], b = gpu[i];
            int dr = Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
            int dg = Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF));
            int db = Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));
            int m = Math.Max(dr, Math.Max(dg, db));
            sumAbs += dr + dg + db;
            if (m > maxAbs) maxAbs = m;
            if (m > 16) bad++;
            if (((a >> 24) & 0xFF) != ((b >> 24) & 0xFF)) nz++;
            if (cpu[i] != beauty[i]) changed++;
        }
        double meanCh = sumAbs / (3.0 * w * h);
        double badFrac = (double)bad / (w * h);
        double changedFrac = (double)changed / (w * h);

        WritePpm(Path.Combine(AppContext.BaseDirectory, "froxel-vk-cpu.ppm"), cpu, w, h);
        WritePpm(Path.Combine(AppContext.BaseDirectory, "froxel-vk-gpu.ppm"), gpu, w, h);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"vulkanfroxel {w}x{h} kernel=CSFroxelIntegrate/Composite(DXC→SPIR-V) dev={ctx.PickedType}:{ctx.PickedName}:"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  grid near/far      {grid.Near:0.000} / {grid.Far:0.000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  fog changed frac   {changedFrac:0.0000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  mean channel diff  {meanCh:0.000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  max channel diff   {maxAbs}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  bad pixels >16     {badFrac:0.0000}  ({bad} px)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  alpha mismatches   {nz}"));

        // Fog must actually do something (>10% of pixels changed), and the GPU must
        // track the CPU oracle: small mean diff, few outliers, matching alpha.
        bool ok = changedFrac > 0.10 && meanCh < 2.0 && badFrac < 0.03 && nz == 0;
        Console.WriteLine(ok
            ? $"vulkanfroxel OK: {ctx.PickedType} {ctx.PickedName}"
            : "vulkanfroxel FAIL: outside band (fog changed>10%, mean<2.0, edge<3%, alpha exact).");
        return ok ? 0 : 1;
    }

    private static void WritePpm(string path, uint[] argb, int w, int h)
    {
        byte[] rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            uint c = argb[i];
            rgb[i * 3] = (byte)((c >> 16) & 0xFF);
            rgb[i * 3 + 1] = (byte)((c >> 8) & 0xFF);
            rgb[i * 3 + 2] = (byte)(c & 0xFF);
        }
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        byte[] hdr = System.Text.Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"P6\n{w} {h}\n255\n"));
        fs.Write(hdr, 0, hdr.Length);
        fs.Write(rgb, 0, rgb.Length);
    }
}
