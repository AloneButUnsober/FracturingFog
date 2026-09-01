// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FroxelGpuProbe.cs — GPU froxel compute gate (--froxelgpu, roadmap S6 #408).
//
// Proves the D3D froxel compute kernel (FroxelGpuKernel / CSFroxelIntegrate +
// CSFroxelComposite) is correct by diffing a GPU dispatch against the pure-CPU
// froxel pass (FroxelCameraVolume.Apply) over identical inputs — the SAME
// FroxelGrid + FroxelMedium (via FroxelGpuUniforms) drive both. Runs on a WARP
// (software) D3D11 device so it needs no GPU and validates headless / in CI.
//
// The CPU pass is double, the shader is float, so exact bit-equality is not
// expected: the gate tolerates a small mean per-channel diff (the same threshold
// as the relief gate). Both composited images are written as PPMs for eyeballing.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.Versioning;

using Vortice.Direct3D;
using Vortice.Direct3D11;

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Rendering;

/// <summary>Headless gate for the #408 D3D froxel compute pass. See header.</summary>
[SupportedOSPlatform("windows")]
public static class FroxelGpuProbe
{
    // A fog-free beauty + a per-pixel world-depth field spanning the grid's
    // near..far so the composite exercises the full slice range (foreground fog
    // -> far-clamped full column), plus a sky strip (huge depth) hitting the
    // beyond-far path.
    private static (uint[] beauty, float[] depth) Scene(int w, int h, double near, double far)
    {
        var beauty = new uint[w * h];
        var depth = new float[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            // A mid-tone gradient beauty so transmittance attenuation is visible.
            byte r = (byte)(40 + 180 * x / (double)(w - 1));
            byte g = (byte)(60 + 150 * y / (double)(h - 1));
            byte b = (byte)(200 - 120 * x / (double)(w - 1));
            beauty[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

            if (y < h / 8)
                depth[i] = 1e30f;   // sky strip -> beyond-far (full integrated column)
            else
            {
                // Ramp near*1.01 .. far*1.2 across x so the rightmost columns exceed
                // far (clamped) and the interior samples interpolate between slices.
                double t = (x + 0.5) / w;
                depth[i] = (float)(near * 1.01 + t * (far * 1.2 - near * 1.01));
            }
        }
        return (beauty, depth);
    }

    // The lit fog medium used by both gates. `density` + `noise` vary per frame in
    // the temporal gate (an animated medium); the three lights are fixed so every
    // populate path (directional / point / spot) is exercised.
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

    /// <summary>CLI entry (`--froxelgputemporal`). Two-frame temporal-reprojection
    /// parity: the GPU kernel's device-side history vs the CPU FroxelHistory blend,
    /// with a CHANGED medium between the frames (grid identity fixed) so the blend is
    /// actually exercised. Also asserts the temporal result differs from the
    /// single-frame composite (history influenced it). WARP device.</summary>
    public static int RunTemporalGate()
    {
        const int w = 200, h = 150;
        const double fb = 0.7;
        var sb = new StringBuilder();
        sb.AppendLine("Froxel-GPU temporal gate (#408) — GPU device history vs CPU FroxelHistory (2 frames)");

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

        // Animated medium: frame A then a denser / noisier frame B. The camera (grid)
        // is unchanged, so the history is reused for frame B on both paths.
        var fxA = SceneFx(0.35, 0.15);
        var fxB = SceneFx(0.75, 0.35);

        var grid = FroxelCameraVolume.BuildGrid(in cam);
        var (beauty, depth) = Scene(w, h, grid.Near, grid.Far);

        // CPU oracle — one persistent history, two frames.
        var hist = new FroxelHistory();
        _ = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fxA, hist, temporal: true, feedback: fb);
        var cpu2 = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fxB, hist, temporal: true, feedback: fb);
        // Single-frame B (no temporal) — to prove the blend actually shifted the result.
        var cpuSingleB = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fxB);

        // GPU — one kernel (persistent device history), two frames.
        var uA = FroxelGpuUniforms.Build(in cam, in fxA);
        var uB = FroxelGpuUniforms.Build(in cam, in fxB);
        ID3D11Device? dev = null;
        ID3D11DeviceContext? ctx = null;
        FroxelGpuKernel? kernel = null;
        var gpu2 = new uint[w * h];
        try
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None,
                null!, out dev, out _, out ctx);
            if (hr.Failure || dev == null || ctx == null)
            {
                sb.AppendLine($"  SKIP: no WARP D3D11 device (0x{hr.Code:X8})");
                sb.AppendLine("RESULT: PASS");
                FinishNamed(sb, "froxelgputemporal.out");
                return 0;
            }
            kernel = new FroxelGpuKernel(dev, ctx, new object());
            var gpu1 = new uint[w * h];
            kernel.Composite(in uA, beauty, depth, w, h, gpu1, fb);   // seeds history
            kernel.Composite(in uB, beauty, depth, w, h, gpu2, fb);   // blends against frame A
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  ERROR: GPU dispatch threw — {ex.Message}");
            sb.AppendLine("RESULT: FAIL");
            FinishNamed(sb, "froxelgputemporal.out");
            return 1;
        }
        finally
        {
            kernel?.Dispose();
            ctx?.Dispose();
            dev?.Dispose();
        }

        // GPU-vs-CPU parity on the temporal 2nd frame.
        long sumAbs = 0; int maxAbs = 0; long bad = 0; int nz = 0;
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
        }
        double meanCh = sumAbs / (3.0 * w * h);
        double badFrac = (double)bad / (w * h);

        // Temporal must actually shift the result: cpu2 (blended with frame A) should
        // differ from the single-frame frame-B composite on a meaningful fraction.
        long tempChanged = 0;
        for (int i = 0; i < w * h; i++)
            if (cpu2[i] != cpuSingleB[i]) tempChanged++;
        double tempChangedFrac = (double)tempChanged / (w * h);

        WritePpm(Path.Combine(AppContext.BaseDirectory, "froxel-temporal-cpu.ppm"), cpu2, w, h);
        WritePpm(Path.Combine(AppContext.BaseDirectory, "froxel-temporal-gpu.ppm"), gpu2, w, h);

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  size                 {w}x{h}   feedback {fb:0.00}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  grid near/far        {grid.Near:0.000} / {grid.Far:0.000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  temporal shifted frac {tempChangedFrac:0.0000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  mean channel diff    {meanCh:0.000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  max channel diff     {maxAbs}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  bad pixels >16       {badFrac:0.0000}  ({bad} px)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  alpha mismatches     {nz}"));

        bool ok = tempChangedFrac > 0.10 && meanCh < 2.0 && badFrac < 0.03 && nz == 0;
        sb.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        FinishNamed(sb, "froxelgputemporal.out");
        return ok ? 0 : 1;
    }

    /// <summary>CLI entry (`--froxelgpu`).</summary>
    public static int RunGate()
    {
        const int w = 200, h = 150;
        var sb = new StringBuilder();
        sb.AppendLine("Froxel-GPU gate (#408) — D3D CSFroxelIntegrate/Composite vs CPU FroxelCameraVolume");

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
        // (directional / point / spot) so every populate path is exercised.
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.6;
        fx.VolumeAnisotropy = 0.3;
        fx.VolumeNoiseAmount = 0.2;
        fx.VolumeNoiseScale = 0.5;
        fx.VolumeNoiseOctaves = 3;
        // Light 1 — directional key (default Theta/Phi), warm.
        fx.Light1.Intensity = 1.0;
        fx.Light1.Color = 0xFFFFE8C0u;
        // Light 2 — point above the slab, cool.
        fx.Light2.Type = LightType.Point;
        fx.Light2.Intensity = 0.8;
        fx.Light2.Color = 0xFFC0D8FFu;
        fx.Light2.PosX = 0.4; fx.Light2.PosY = 1.1; fx.Light2.PosZ = 0.3;
        fx.Light2.Range = 4.0;
        // Light 3 — spot angled at the slab.
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

        // GPU dispatch (WARP).
        var u = FroxelGpuUniforms.Build(in cam, in fx);
        ID3D11Device? dev = null;
        ID3D11DeviceContext? ctx = null;
        FroxelGpuKernel? kernel = null;
        var gpu = new uint[w * h];
        try
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None,
                null!, out dev, out _, out ctx);
            if (hr.Failure || dev == null || ctx == null)
            {
                sb.AppendLine($"  SKIP: no WARP D3D11 device (0x{hr.Code:X8})");
                sb.AppendLine("RESULT: PASS");
                Finish(sb);
                return 0;   // no WARP -> skip (as the relief gate does)
            }
            kernel = new FroxelGpuKernel(dev, ctx, new object());
            kernel.Composite(in u, beauty, depth, w, h, gpu);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  ERROR: GPU dispatch threw — {ex.Message}");
            sb.AppendLine("RESULT: FAIL");
            Finish(sb);
            return 1;
        }
        finally
        {
            kernel?.Dispose();
            ctx?.Dispose();
            dev?.Dispose();
        }

        // Diff (RGB; alpha must match exactly). Also count how many pixels the fog
        // actually changed vs the input beauty, so a no-op (density lost) fails.
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

        WritePpm(Path.Combine(AppContext.BaseDirectory, "froxel-gpu-cpu.ppm"), cpu, w, h);
        WritePpm(Path.Combine(AppContext.BaseDirectory, "froxel-gpu-gpu.ppm"), gpu, w, h);

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  size               {w}x{h}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  grid near/far      {grid.Near:0.000} / {grid.Far:0.000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  fog changed frac   {changedFrac:0.0000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  mean channel diff  {meanCh:0.000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  max channel diff   {maxAbs}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  bad pixels >16     {badFrac:0.0000}  ({bad} px)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  alpha mismatches   {nz}"));

        // Fog must actually do something (>10% of pixels changed), and the GPU must
        // track the CPU oracle: small mean diff, few outliers, matching alpha.
        bool ok = changedFrac > 0.10 && meanCh < 2.0 && badFrac < 0.03 && nz == 0;
        sb.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        Finish(sb);
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
        byte[] hdr = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"P6\n{w} {h}\n255\n"));
        fs.Write(hdr, 0, hdr.Length);
        fs.Write(rgb, 0, rgb.Length);
    }

    private static void Finish(StringBuilder sb) => FinishNamed(sb, "froxelgpu.out");

    private static void FinishNamed(StringBuilder sb, string outFile)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, outFile), sb.ToString()); }
        catch { }
        Console.Write(sb.ToString());
    }
}
