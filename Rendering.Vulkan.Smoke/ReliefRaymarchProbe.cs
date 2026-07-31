// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ReliefRaymarchProbe.cs — Relief 3D Slice 3c gate (--vulkanrelief, #161).
//
// The Vulkan sibling of the D3D --reliefgpuraymarch gate (#160). Proves the
// cross-platform relief compute kernel (ReliefRaymarchVulkanKernel / CSRelief,
// DXC → SPIR-V) is correct by diffing a Vulkan dispatch against the CPU parity
// twin ReliefRaymarchGpu.RenderCpuMirror over identical inputs — the SAME
// ReliefUniforms cbuffer twin drives both. Runs on Mesa lavapipe (software
// Vulkan) in CI so it needs no GPU; real hardware just runs the same kernel
// faster. Uses only float32 (no shaderFloat64 dependency).
//
// The twin is double, the shader is float, so exact bit-equality is not
// expected: the gate tolerates a small mean per-channel diff and a small
// fraction of silhouette-edge pixels (a float-vs-double ray flipping hit/miss).
// Both images are written as PPMs for eyeballing.

using System;
using System.Globalization;
using System.IO;
using System.Text;

using FracturingFog.Models;               // FractalParameters
using FracturingFog.Rendering.Lighting;   // ReliefUniforms, ReliefRaymarchGpu, LightingFxData
using FracturingFog.Rendering.Vulkan;     // ReliefRaymarchVulkanKernel, VulkanContext

namespace FracturingFog.Rendering.Vulkan.Smoke;

/// <summary>Headless gate for the #161 Vulkan relief-raymarch kernel. See header.</summary>
internal static class ReliefRaymarchProbe
{
    // Smooth radial cosine bump field + flat warm albedo — identical to the
    // 3b D3D gate so both backends diff against the same CPU oracle.
    private static (float[] hbuf, uint[] albedo, float maxH) BumpField(int hw, int hh, int aw, int ah)
    {
        var hbuf = new float[hw * hh];
        float maxH = 0f;
        for (int y = 0; y < hh; y++)
        for (int x = 0; x < hw; x++)
        {
            double u = (x + 0.5) / hw - 0.5, v = (y + 0.5) / hh - 0.5;
            double r = Math.Sqrt(u * u + v * v) / 0.5;
            float hv = r >= 1.0 ? 0f : (float)(0.5 * (1.0 + Math.Cos(Math.PI * r)));
            hbuf[y * hw + x] = hv;
            if (hv > maxH) maxH = hv;
        }
        var albedo = new uint[aw * ah];
        for (int i = 0; i < aw * ah; i++) albedo[i] = 0xFFB06030u;
        return (hbuf, albedo, maxH);
    }

    private static ReliefUniforms BuildUniforms(int w, int h, int hw, int hh,
        float[] hbuf, float maxH, FractalParameters p, LightingFxData fx)
    {
        double aspect = (double)w / h;
        double sy = 0.35 * Math.Max(0.0, p.Relief2DHeightScale) / maxH;
        float gx = 0f, gz = 0f;
        for (int y = 0; y < hh; y++)
        for (int x = 0; x < hw; x++)
        {
            if (x > 0) gx = Math.Max(gx, Math.Abs(hbuf[y * hw + x] - hbuf[y * hw + x - 1]));
            if (y > 0) gz = Math.Max(gz, Math.Abs(hbuf[y * hw + x] - hbuf[(y - 1) * hw + x]));
        }
        double worldDx = aspect / hw, worldDz = 1.0 / hh;
        double maxSlope = Math.Max(gx * sy / worldDx, gz * sy / worldDz);
        double invLip = 1.0 / Math.Sqrt(1.0 + maxSlope * maxSlope);
        return ReliefUniforms.Build(w, h, hw, hh, sy, aspect, invLip, maxH, p, in fx);
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

    /// <summary>CLI entry (`--vulkanrelief`).</summary>
    public static int Run(VulkanContext ctx)
    {
        const int w = 320, h = 240, hw = 320, hh = 240;

        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            Relief2DGroundPlane = false,
        };
        var fx = LightingFxData.CreateDefault();
        fx.BgTopColor = 0xFF335588u;
        fx.BgBottomColor = 0xFF0A0C14u;
        // 4a (#165) — exercise the Cook-Torrance GGX spec path (matches the D3D
        // gate). Non-metallic, roughness 0.5 to keep the DXC-SPIR-V lobe fringe
        // inside the parity thresholds vs the CPU twin.
        fx.SpecularStrength = 0.5;
        fx.Roughness = 0.5;
        fx.Metallic = 0.0;
        // 4b (#166) — exercise the IQ soft-shadow march (key light only), matches
        // the D3D gate. DXC-SPIR-V penumbra stays inside the parity thresholds.
        fx.ShadowSteps = 24;
        fx.ShadowSoftK = 8.0;
        fx.ShadowLightMask = 0x1;
        // 4c (#167) — exercise the DE-cone AO (matches the D3D gate). DXC-SPIR-V
        // occlusion accumulation stays inside the parity thresholds.
        fx.AoSamples = 5;
        fx.AoStrength = 1.0;
        // 4d (#168) — IBL-modulated ambient (gradient env) + triplanar (matches
        // the D3D gate). Marble keeps the DXC-SPIR-V texture inside the thresholds.
        fx.IblStrength = 0.5;
        fx.TriplanarKind = TriplanarTextureKind.Marble;
        fx.TriplanarStrength = 0.5;
        fx.TriplanarScale = 4.0;
        fx.TriplanarTint = 0xFFFFFFFFu;

        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);
        var u = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fx);

        // CPU oracle.
        var cpu = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in u, hbuf, null, albedo, cpu, out double hitCpu);

        // GPU dispatch on the picked Vulkan device.
        var gpu = new uint[w * h];
        try
        {
            using var kernel = new ReliefRaymarchVulkanKernel(ctx);
            kernel.Run(in u, hbuf, null, albedo, gpu);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vulkanrelief FAIL: GPU dispatch threw — {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        // Diff. CPU twin = double, shader = float → tolerate a small mean channel
        // diff and a small fraction of silhouette-edge pixels. RGB only (alpha is
        // 0xFF on both here).
        long sumAbs = 0; int maxAbs = 0; long bad = 0; int alphaMismatch = 0;
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
            if (((a >> 24) & 0xFF) != ((b >> 24) & 0xFF)) alphaMismatch++;
        }
        double meanCh = sumAbs / (3.0 * w * h);
        double badFrac = (double)bad / (w * h);

        string pc = Path.Combine(AppContext.BaseDirectory, "relief-vk-cpu.ppm");
        string pg = Path.Combine(AppContext.BaseDirectory, "relief-vk-gpu.ppm");
        WritePpm(pc, cpu, w, h);
        WritePpm(pg, gpu, w, h);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"vulkanrelief {w}x{h} kernel=CSRelief(DXC→SPIR-V) dev={ctx.PickedType}:{ctx.PickedName}:"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  cpu hit frac       {hitCpu:0.000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  mean channel diff  {meanCh:0.000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  max channel diff   {maxAbs}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  edge pixels >16    {badFrac:0.0000}  ({bad} px)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  alpha mismatches   {alphaMismatch}"));
        Console.WriteLine($"  wrote {pc}");
        Console.WriteLine($"  wrote {pg}");

        // Silhouette must be real (both hit + sky), and the GPU must track the
        // CPU twin: small mean diff, few edge flips, matching cutout alpha.
        bool ok = hitCpu > 0.10 && hitCpu < 0.90
                  && meanCh < 2.0 && badFrac < 0.03 && alphaMismatch == 0;
        Console.WriteLine(ok
            ? $"vulkanrelief OK: {ctx.PickedType} {ctx.PickedName}"
            : "vulkanrelief FAIL: outside band (real hit silhouette, mean<2.0, edge<3%, alpha exact).");
        return ok ? 0 : 1;
    }
}
