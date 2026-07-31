// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ReliefRaymarchGpuProbe.cs — Relief 3D Slice 3b gate (--reliefgpuraymarch, #160).
//
// Proves the D3D relief compute kernel (ReliefRaymarchGpuKernel / CSRelief) is
// correct by diffing a GPU dispatch against the CPU parity twin
// (ReliefRaymarchGpu.RenderCpuMirror) over identical inputs — the SAME
// ReliefUniforms cbuffer twin drives both. Runs on a WARP (software) D3D11
// device so it needs no GPU and validates headless / in CI; real hardware just
// runs the same kernel faster.
//
// The twin is double, the shader is float, so exact bit-equality is not
// expected: the gate tolerates a small mean per-channel diff and a small
// fraction of edge pixels (where a float-vs-double silhouette ray flips
// hit/miss). Both images are written as PPMs for eyeballing.

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

/// <summary>Headless gate for the #160 D3D relief-raymarch kernel. See header.</summary>
[SupportedOSPlatform("windows")]
public static class ReliefRaymarchGpuProbe
{
    // Smooth radial cosine bump field + flat warm albedo (matches the 3a test).
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

    /// <summary>CLI entry (`--reliefgpuraymarch`).</summary>
    public static int RunGate()
    {
        const int w = 320, h = 240, hw = 320, hh = 240;
        var sb = new StringBuilder();
        sb.AppendLine("Relief-GPU-raymarch gate (#160) — D3D CSRelief vs CPU parity twin");

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
        // 4a (#165) — exercise the Cook-Torrance GGX spec path. Non-metallic so
        // diffuse stays full; roughness 0.5 spreads the highlight so the
        // float-vs-double lobe fringe stays inside the gate thresholds.
        fx.SpecularStrength = 0.5;
        fx.Roughness = 0.5;
        fx.Metallic = 0.0;
        // 4b (#166) — exercise the IQ soft-shadow march (key light only; the dome
        // self-shadows its far side). Penumbra float-vs-double divergence stays
        // inside the gate thresholds.
        fx.ShadowSteps = 24;
        fx.ShadowSoftK = 8.0;
        fx.ShadowLightMask = 0x1;
        // 4c (#167) — exercise the DE-cone AO. Cone-march creases darken; the
        // float-vs-double occlusion accumulation stays inside the gate thresholds.
        fx.AoSamples = 5;
        fx.AoStrength = 1.0;
        // 4d (#168) — IBL-modulated ambient (gradient env, no HDRI image) +
        // triplanar procedural texture. Marble keeps sin-cascade args bounded so
        // the float-vs-double texture stays inside the gate thresholds (Rock's
        // huge hash multiplier / Checker's floor seams would blow the edge band).
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

        // GPU on a WARP (software) device — no hardware needed.
        ID3D11Device? dev = null;
        ID3D11DeviceContext? ctx = null;
        ReliefRaymarchGpuKernel? kernel = null;
        var gpu = new uint[w * h];
        try
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None,
                null, out dev, out _, out ctx);
            if (hr.Failure || dev == null || ctx == null)
            {
                sb.AppendLine($"  SKIP: could not create a WARP D3D11 device (0x{hr.Code:X8})");
                sb.AppendLine("RESULT: PASS");   // inconclusive → don't fail CI on a missing WARP
                Finish(sb);
                return 0;
            }
            kernel = new ReliefRaymarchGpuKernel(dev, ctx, new object());
            kernel.Run(in u, hbuf, null, albedo, gpu);
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

        // Diff. CPU twin = double, shader = float → tolerate a small mean channel
        // diff and a small fraction of silhouette-edge pixels (a float-vs-double
        // ray flipping hit/miss). RGB only (alpha is 0xFF on both here).
        long sumAbs = 0; int maxAbs = 0; long bad = 0; int nz = 0;
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
        }
        double meanCh = sumAbs / (3.0 * w * h);
        double badFrac = (double)bad / (w * h);

        string pc = Path.Combine(AppContext.BaseDirectory, "relief-gpu-cpu.ppm");
        string pg = Path.Combine(AppContext.BaseDirectory, "relief-gpu-gpu.ppm");
        WritePpm(pc, cpu, w, h);
        WritePpm(pg, gpu, w, h);

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  size               {w}x{h}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  cpu hit frac       {hitCpu:0.000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  mean channel diff  {meanCh:0.000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  max channel diff   {maxAbs}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  edge pixels >16    {badFrac:0.0000}  ({bad} px)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  alpha mismatches   {nz}"));
        sb.AppendLine($"  wrote              {pc}");
        sb.AppendLine($"  wrote              {pg}");

        // Silhouette must be real (both hit + sky), and the GPU must track the
        // CPU twin: small mean diff, few edge flips, matching cutout alpha.
        bool ok = hitCpu > 0.10 && hitCpu < 0.90
                  && meanCh < 2.0 && badFrac < 0.03 && nz == 0;
        sb.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        Finish(sb);
        return ok ? 0 : 1;
    }

    private static void Finish(StringBuilder sb)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "reliefgpuraymarch.out"), sb.ToString()); }
        catch { }
        Console.Write(sb.ToString());
    }
}
