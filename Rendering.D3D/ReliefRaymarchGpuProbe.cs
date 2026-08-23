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

    // 4d-ii (#171) — small procedural equirect HDRI so the gate exercises the
    // HDRI ambient + HDRI sky branches. Azimuthal variation in R/B, polar gradient
    // in G; values kept in [0.05,0.95] so the linear env stays well-behaved. The
    // twin and both kernels sample the SAME flattened buffer, so it is the oracle.
    private static void RegisterProcHdri(string name)
    {
        const int w = 64, hgt = 32;
        var data = new float[w * hgt * 3];
        for (int y = 0; y < hgt; y++)
        {
            double v = (y + 0.5) / hgt;
            for (int x = 0; x < w; x++)
            {
                double uu = (x + 0.5) / w;
                double r = 0.5 + 0.4 * Math.Sin(2.0 * Math.PI * uu);
                double g = 0.9 - 0.6 * v;
                double b = 0.5 + 0.35 * Math.Cos(2.0 * Math.PI * uu);
                int i = (y * w + x) * 3;
                data[i]     = (float)Math.Clamp(r, 0.05, 0.95);
                data[i + 1] = (float)Math.Clamp(g, 0.05, 0.95);
                data[i + 2] = (float)Math.Clamp(b, 0.05, 0.95);
            }
        }
        HdriRegistry.Register(name, new HdriImage(w, hgt, data));
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
            // 4f (#170) — empty-space-skip on, so the gate proves GPU==twin with the
            // coarse max-height grid driving the leap (both build the same grid).
            Relief2DEmptySkip = true,
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
        // 4d (#168) — IBL-modulated ambient + triplanar procedural texture. Marble
        // keeps sin-cascade args bounded so the float-vs-double texture stays inside
        // the gate thresholds (Rock's huge hash multiplier / Checker's floor seams
        // would blow the edge band).
        fx.IblStrength = 0.5;
        fx.TriplanarKind = TriplanarTextureKind.Marble;
        fx.TriplanarStrength = 0.5;
        fx.TriplanarScale = 4.0;
        fx.TriplanarTint = 0xFFFFFFFFu;
        // 4d-ii (#171) — HDRI equirect env: ambient sampled at the surface normal
        // (mip 0) + ray-miss HDRI sky. SkyMode=Hdri + a registered procedural HDRI
        // routes IBL ambient + sky through the t4 SRV instead of the gradient. The
        // twin samples the same flattened buffer, so GPU==twin proves the port.
        const string hdriName = "relief-gate-proc";
        RegisterProcHdri(hdriName);
        fx.SkyMode = SkyMode.Hdri;
        fx.EnvironmentName = hdriName;
        fx.ShowSkyBackdrop = true;
        // 4e (#169) — single-scatter volumetric in-scatter (key light) + ground-
        // hugging fog. VolumeSteps>0 drives the in-scatter walk; the per-step key-
        // light SoftShadow reuses the 4b shadow settings. VolumeStepsFalloff stays 0
        // so the twin and shader march the same fixed step count (no float-vs-double
        // LOD flip).
        fx.FogDensity = 0.5;
        fx.FogHeightFalloff = 0.3;
        fx.VolumeSteps = 16;
        // #388 — exercise the multi-light in-scatter port. Arm Light 2 (fill, its
        // default cool color) and Light 3 (rim, warm) so the fog/god-ray walk sums
        // all three lights, each HG-phased toward its own direction. Extend the
        // shadow mask to 0x3 so a SECOND light also casts volumetric shafts (its
        // per-step SoftShadow now runs); Light 3 stays unshadowed. GPU==twin here
        // proves the three-light relief kernel matches the oracle. Kept to two
        // shadowed lights so the added penumbra float-vs-double divergence stays
        // inside the gate's edge band.
        fx.Light2.Intensity = 0.7;
        fx.Light3.Intensity = 0.5;
        fx.ShadowLightMask = 0x3;
        // 4e-ii (#172) — N-bounce reflections (mirror path) + FBM cloud-noise
        // volumetrics. Mirror reflect (UseGgxSampling OFF) is the deterministic,
        // parity-friendly path — GGX VNDF hash/trig would scatter bounce rays and
        // blow the edge band, so it stays off in the gate. VolumeNoiseAmount is kept
        // small (bounded ±amount density swing) and speed 0 (static): value noise is
        // C1-continuous across cell boundaries so the float-vs-double floor split is
        // benign, but a small amount keeps the multiplier near 1 for safety.
        fx.ReflectionStrength = 0.4;
        fx.ReflectionSteps = 24;
        fx.MaxBounces = 2;
        fx.UseGgxSampling = false;
        fx.VolumeNoiseAmount = 0.15;
        fx.VolumeNoiseScale = 0.3;
        fx.VolumeNoiseSpeed = 0.0;
        fx.VolumeNoiseOctaves = 3;
        fx.VolumeSelfShadow = 0.5;
        fx.VolumeSelfShadowSteps = 4;
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        // Main scene — pinhole, all features. The comprehensive GPU==twin diff.
        p.Relief2DDofApertureRadius = 0.0;
        bool? okFull = RunDiff(sb, "full (pinhole, all features)", w, h, hw, hh, p, fx);
        if (okFull is null) { sb.AppendLine("RESULT: PASS"); Finish(sb); return 0; }  // no WARP → skip

        // S3 (#389) — a small, cheap DOF diff so the thin-lens port stays guarded
        // without the full 16-taps-on-WARP cost of the comprehensive scene. Aperture
        // open ⇒ the kernel averages DofGpuSamples lens taps (concentric-disc +
        // ThinLensRay) and the CPU twin runs the identical loop. Reflections / fog /
        // HDRI / triplanar are dropped (per-trace-heavy) and the grid shrunk; Lambert
        // + shadow + AO stay so the shaded lens average is meaningful. Focus auto-
        // resolves to |camera|.
        var pDof = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            Relief2DGroundPlane = false,
            Relief2DEmptySkip = true,
            Relief2DDofApertureRadius = 0.06,
            Relief2DDofFocusDistance = 0.0,
        };
        var fxDof = LightingFxData.CreateDefault();
        fxDof.BgTopColor = 0xFF335588u;
        fxDof.BgBottomColor = 0xFF0A0C14u;
        fxDof.ShadowSteps = 24;
        fxDof.ShadowSoftK = 8.0;
        fxDof.ShadowLightMask = 0x1;
        fxDof.AoSamples = 5;
        fxDof.AoStrength = 1.0;
        fxDof.ShowSkyBackdrop = true;
        bool? okDof = RunDiff(sb, "dof (aperture 0.06, cheap scene)", 160, 120, 160, 120, pDof, fxDof);

        // S4 (#402) — AOV emit diff: a cheap pinhole scene rendered with the guide
        // buffers on, checking the GPU-emitted normal/depth track the twin's (the
        // denoiser's inputs). Reuses the cheap DOF scene's fx (Lambert + shadow + AO,
        // no reflections/fog) but at aperture 0.
        var pAov = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            Relief2DGroundPlane = false,
            Relief2DEmptySkip = true,
        };
        bool? okAov = RunDiff(sb, "aov (pinhole normal+depth emit)", 160, 120, 160, 120, pAov, fxDof, emitAov: true);

        // S5 (#389/#406) — glass diff: a transmissive material makes ShadeFlat refract
        // the view ray + Fresnel-mix the reflected/transmitted env. Cheap (env sampling,
        // no extra march), so the GPU==twin refraction port is validated at low cost.
        var pGlass = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            Relief2DGroundPlane = false,
            Relief2DEmptySkip = true,
        };
        var fxGlass = fxDof;
        fxGlass.Transmission = 0.6;
        fxGlass.Ior = 1.5;
        fxGlass.AbsorptionColor = 0xFFCCE6FFu;   // faint blue glass tint
        fxGlass.AbsorptionDistance = 1.0;
        bool? okGlass = RunDiff(sb, "glass (transmission 0.6)", 160, 120, 160, 120, pGlass, fxGlass);

        bool ok = (okFull ?? true) && (okDof ?? true) && (okAov ?? true) && (okGlass ?? true);
        sb.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        Finish(sb);
        return ok ? 0 : 1;
    }

    // One GPU (WARP) vs CPU-twin render + diff at the given size/scene. Returns true
    // on pass, false on fail, null when no WARP device is available (→ skip). Appends
    // the metrics to <paramref name="sb"/> under <paramref name="label"/>.
    private static bool? RunDiff(StringBuilder sb, string label, int w, int h, int hw, int hh,
        FractalParameters p, LightingFxData fx, bool emitAov = false)
    {
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);
        var u = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fx);

        // S4 (#402) — when emitAov, also diff the GPU-emitted normal/depth guides
        // against the twin's.
        float[]? cpuN = emitAov ? new float[w * h * 3] : null;
        float[]? cpuD = emitAov ? new float[w * h] : null;
        float[]? gpuN = emitAov ? new float[w * h * 3] : null;
        float[]? gpuD = emitAov ? new float[w * h] : null;

        var cpu = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in u, hbuf, null, albedo, cpu, out double hitCpu, out _, cpuN, cpuD);

        ID3D11Device? dev = null;
        ID3D11DeviceContext? ctx = null;
        ReliefRaymarchGpuKernel? kernel = null;
        var gpu = new uint[w * h];
        try
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None,
                null!, out dev, out _, out ctx);
            if (hr.Failure || dev == null || ctx == null)
            {
                sb.AppendLine($"  [{label}] SKIP: no WARP D3D11 device (0x{hr.Code:X8})");
                return null;
            }
            kernel = new ReliefRaymarchGpuKernel(dev, ctx, new object());
            kernel.Run(in u, hbuf, null, albedo, gpu, gpuN, gpuD);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  [{label}] ERROR: GPU dispatch threw — {ex.Message}");
            return false;
        }
        finally
        {
            kernel?.Dispose();
            ctx?.Dispose();
            dev?.Dispose();
        }

        // CPU twin = double, shader = float → tolerate a small mean channel diff and
        // a small fraction of silhouette-edge pixels (a float-vs-double ray flipping
        // hit/miss). RGB only (alpha is 0xFF on both here).
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

        string safe = label.Replace(' ', '-').Replace('(', '_').Replace(')', '_').Replace(',', '_');
        string pc = Path.Combine(AppContext.BaseDirectory, $"relief-gpu-cpu-{safe}.ppm");
        string pg = Path.Combine(AppContext.BaseDirectory, $"relief-gpu-gpu-{safe}.ppm");
        WritePpm(pc, cpu, w, h);
        WritePpm(pg, gpu, w, h);

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  [{label}]"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    size               {w}x{h}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    cpu hit frac       {hitCpu:0.000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    mean channel diff  {meanCh:0.000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    max channel diff   {maxAbs}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    edge pixels >16    {badFrac:0.0000}  ({bad} px)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    alpha mismatches   {nz}"));

        // Silhouette must be real (both hit + sky), and the GPU must track the twin:
        // small mean diff, few edge flips, matching cutout alpha.
        bool ok = hitCpu > 0.10 && hitCpu < 0.90 && meanCh < 2.0 && badFrac < 0.03 && nz == 0;

        // S4 (#402) — AOV guide diff: GPU-emitted normal/depth vs the twin's. The
        // guides drive the À-Trous denoiser, so they must track closely. Normal via
        // 1 − |cosine|, depth via relative error on finite (non-sky) hits.
        if (emitAov)
        {
            double nErrSum = 0; double nErrMax = 0; long nCount = 0;
            double dRelSum = 0; double dRelMax = 0; long dCount = 0;
            for (int i = 0; i < w * h; i++)
            {
                double ax = cpuN![i * 3], ay = cpuN[i * 3 + 1], az = cpuN[i * 3 + 2];
                double bx = gpuN![i * 3], by = gpuN[i * 3 + 1], bz = gpuN[i * 3 + 2];
                double al = Math.Sqrt(ax * ax + ay * ay + az * az);
                double bl = Math.Sqrt(bx * bx + by * by + bz * bz);
                if (al > 1e-3 && bl > 1e-3)   // both are real (non-sky) normals
                {
                    double cos = Math.Abs((ax * bx + ay * by + az * bz) / (al * bl));
                    double e = 1.0 - Math.Min(1.0, cos);
                    nErrSum += e; if (e > nErrMax) nErrMax = e; nCount++;
                }
                float cd = cpuD![i], gd = gpuD![i];
                if (cd > 0f && cd < 1e6f && gd > 0f && gd < 1e6f)
                {
                    double rel = Math.Abs(cd - gd) / Math.Max(1e-4, cd);
                    dRelSum += rel; if (rel > dRelMax) dRelMax = rel; dCount++;
                }
            }
            double nErrMean = nCount > 0 ? nErrSum / nCount : 0;
            double dRelMean = dCount > 0 ? dRelSum / dCount : 0;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    aov normal 1-|cos| mean {nErrMean:0.00000} max {nErrMax:0.0000} ({nCount} px)"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    aov depth rel-err  mean {dRelMean:0.00000} max {dRelMax:0.0000} ({dCount} px)"));
            bool aovOk = nCount > 0 && dCount > 0 && nErrMean < 0.02 && dRelMean < 0.02;
            sb.AppendLine($"    aov {(aovOk ? "pass" : "FAIL")}");
            ok = ok && aovOk;
        }

        sb.AppendLine($"    {(ok ? "pass" : "FAIL")}");
        return ok;
    }

    private static void Finish(StringBuilder sb)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "reliefgpuraymarch.out"), sb.ToString()); }
        catch { }
        Console.Write(sb.ToString());
    }
}
