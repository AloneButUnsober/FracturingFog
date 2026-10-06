// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// KifsFoldGpuCalculator.cs
//
// #1173-D / GPU parity plan G3.2 — ILGPU raymarcher for the three KIFS folds that
// had no kernel: Octahedron, Dodecahedron and MandelboxRot (Wave 5.9.f1). Before
// this they always rendered on the CPU. The shading body is the Menger kernel's
// (MengerGpuCalculator); only the DE differs. One kernel branches on the fold
// (uniform across the launch) so Menger and Sierpinski keep their own lean kernels.
//
// The DEs are line-for-line ports of KifsCalculator.OctaDE / DodecaDE /
// MandelboxRotDE AS SHIPPED. Those folds are approximations of their namesakes
// (see the 5.9.f1 notes on the CPU methods); this is a parity port, not a fix —
// the CPU is the reference. The per-iteration rotation coefficients come from the
// host (KifsFoldGpuParams.Rot*), computed with the same Math.Cos / Math.Sin calls
// as the CPU, so they match bit-for-bit and the kernel runs no trig per DE call.

using System;
using System.Threading;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Fold selector for <see cref="KifsFoldGpuCalculator"/>.</summary>
public enum KifsGpuFold
{
    Octahedron = 0,
    Dodecahedron = 1,
    MandelboxRot = 2,
}

/// <summary>Per-fractal kernel parameters for the Octahedron / Dodecahedron /
/// MandelboxRot KIFS folds. <see cref="RotA"/> / <see cref="RotB"/> /
/// <see cref="RotC"/> are the fold's rotation coefficients — use <see cref="For"/>.</summary>
public struct KifsFoldGpuParams
{
    public int Fold;
    public double Scale;
    public double OffsetX, OffsetY, OffsetZ;
    public int DEIter;
    public double SceneRadius;
    public double RotA, RotB, RotC;

    /// <summary>Parameters for <paramref name="fold"/> with its rotation coefficients,
    /// computed exactly as the CPU DE computes them: Octahedron cos / sin(30°) about
    /// Y; Dodecahedron the Rodrigues k1 / k2 / k3 for 36° about (1, 1, 1)/√3;
    /// MandelboxRot cos / sin(π/48) about Y.</summary>
    public static KifsFoldGpuParams For(KifsGpuFold fold, double scale,
        double ox, double oy, double oz, int iter, double sceneRadius)
    {
        var p = new KifsFoldGpuParams
        {
            Fold = (int)fold, Scale = scale, OffsetX = ox, OffsetY = oy, OffsetZ = oz,
            DEIter = iter, SceneRadius = sceneRadius,
        };
        switch (fold)
        {
            case KifsGpuFold.Octahedron:
            {
                const double rot = Math.PI / 6.0;
                p.RotA = Math.Cos(rot); p.RotB = Math.Sin(rot);
                break;
            }
            case KifsGpuFold.Dodecahedron:
            {
                const double ang = Math.PI / 5.0;
                double cosA = Math.Cos(ang), sinA = Math.Sin(ang);
                const double inv3 = 1.0 / 3.0;
                const double invSqrt3 = 0.5773502691896258;
                p.RotA = cosA + (1.0 - cosA) * inv3;
                p.RotB = (1.0 - cosA) * inv3 - sinA * invSqrt3;
                p.RotC = (1.0 - cosA) * inv3 + sinA * invSqrt3;
                break;
            }
            default:
            {
                const double rot = Math.PI / 48.0;
                p.RotA = Math.Cos(rot); p.RotB = Math.Sin(rot);
                break;
            }
        }
        return p;
    }
}

public sealed class KifsFoldGpuCalculator : IDisposable
{
    private Action<Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, KifsFoldGpuParams, ArrayView<uint>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<uint>, ArrayView<uint>>? _kernel;
    private bool _initFailed;
    // #1169 — set once a device proved too slow for this kernel; per family and
    // device, process-wide, so new calculator instances don't re-probe it.
    private static (Accelerator Device, string Message)? s_tooSlow;
    public string LastError { get; private set; } = string.Empty;

    private bool TryInit()
    {
        if (_kernel != null) return true;
        if (_initFailed) return false;
        if (!GpuAcceleratorHost.TryAcquire(out var acc))
        {
            LastError = GpuAcceleratorHost.LastError;
            _initFailed = true;
            return false;
        }
        try
        {
            _kernel = acc.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, KifsFoldGpuParams, ArrayView<uint>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<uint>, ArrayView<uint>>(KifsFoldKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"KIFS fold GPU kernel load failed: {ex.GetBaseException().Message}";
            _initFailed = true;
            return false;
        }
    }

    public bool Render(uint[] outBuffer, GpuRaymarchParams r, GpuShadingParams sp, KifsFoldGpuParams p, uint[]? palette = null, float[]? depthOut = null, CancellationToken ct = default, float[]? normalOut = null, float[]? hdrOut = null, uint[]? albedoLut = null, uint[]? hdri = null)
    {
        if (!TryInit() || _kernel == null) return false;
        if (!GpuAcceleratorHost.TryAcquire(out var acc)) return false;
        if (s_tooSlow is { } slow && ReferenceEquals(slow.Device, acc)) { LastError = slow.Message; return false; }
        try
        {
            int total = r.Width * r.Height;
            using var dev = GpuMemoryStats.Allocate1D<uint>(acc, total);
            // Slice D GPU parity — upload the theme palette LUT (or a length-1
            // dummy when off) so the kernel arity stays fixed; the kernel gates
            // on VolumePaletteStrength + LUT length.
            uint[] lut = palette is { Length: >= 2 } ? palette : GpuKernelUtils.PaletteOff;
            using var devLut = GpuMemoryStats.Allocate1D<uint>(acc, lut.Length);
            devLut.CopyFromCPU(lut);
            // #1070 — optional per-pixel ray distance (+Inf = miss), the CPU
            // depth G-buffer contract, for the froxel composite. Off → a
            // length-1 dummy so the kernel arity stays fixed.
            bool wantDepth = depthOut != null && depthOut.Length == total;
            using var devDepth = GpuMemoryStats.Allocate1D<float>(acc, wantDepth ? total : 1);
            // #1172 — optional normal + HDR G-buffers (3 floats / pixel); length-1 dummies when off.
            bool wantNormal = normalOut != null && normalOut.Length == 3 * total;
            bool wantHdr = hdrOut != null && hdrOut.Length == 3 * total;
            using var devNormal = GpuMemoryStats.Allocate1D<float>(acc, wantNormal ? 3L * total : 1);
            using var devHdr = GpuMemoryStats.Allocate1D<float>(acc, wantHdr ? 3L * total : 1);
            // #1172 / G2.2 — colour-map albedo LUT (GpuAlbedoLut), or the length-1 "off" dummy.
            uint[] alb = albedoLut is { Length: >= 2 } ? albedoLut : GpuKernelUtils.PaletteOff;
            using var devAlbedo = GpuMemoryStats.Allocate1D<uint>(acc, alb.Length);
            devAlbedo.CopyFromCPU(alb);
            // #1173-B / G2.3 — flattened HDRI environment (GpuHdriEnv), or the length-1 "off" dummy.
            uint[] env = hdri is { Length: >= 2 } ? hdri : GpuKernelUtils.PaletteOff;
            using var devHdri = GpuMemoryStats.Allocate1D<uint>(acc, env.Length);
            devHdri.CopyFromCPU(env);
            // #1170 — tiled under the GPU watchdog; byte-identical to one launch.
            var kernel = _kernel;
            var run = GpuTiledDispatch.Run(acc, r, (n, rt) => kernel(n, dev.View, rt, sp, p, devLut.View, devDepth.View, devNormal.View, devHdr.View, devAlbedo.View, devHdri.View), ct);
            if (run == GpuDispatchResult.TooSlow)
            {
                // #1169 — this device can't render this kernel in useful time; stay on the CPU.
                LastError = GpuTiledDispatch.TooSlowMessage("KIFS fold");
                s_tooSlow = (acc, LastError);
                return false;
            }
            if (run != GpuDispatchResult.Completed) return false;
            dev.CopyToCPU(outBuffer);
            if (wantDepth) devDepth.CopyToCPU(depthOut!);
            if (wantNormal) devNormal.CopyToCPU(normalOut!);
            if (wantHdr) devHdr.CopyToCPU(hdrOut!);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"KIFS fold GPU render failed: {ex.Message}";
            GpuAcceleratorHost.ReportRenderFault(acc, ex);
            return false;
        }
    }

    private static void KifsFoldKernel(
        Index1D tid, ArrayView<uint> output, GpuRaymarchParams r, GpuShadingParams sp, KifsFoldGpuParams p, ArrayView<uint> palette, ArrayView<float> depth, ArrayView<float> normals, ArrayView<float> hdr, ArrayView<uint> albedo, ArrayView<uint> hdri)
    {
        int idx = GpuKernelUtils.TilePixel(tid, in r);   // #1170 — pixel this thread shades
        int x = idx % r.Width;
        int y = idx / r.Width;
        if (y >= r.Height) return;
        // #1070 — depth wanted when the view covers the frame (else the
        // length-1 dummy). Pre-fill the miss value; a pinhole hit overwrites
        // it with the ray distance. Thin-lens taps leave the miss.
        bool wantDepth = depth.Length >= output.Length;
        if (wantDepth) depth[idx] = float.PositiveInfinity;
        int dIdx = wantDepth ? idx : -1;
        // #1172 — optional normal + pre-clamp HDR G-buffers (3 floats / pixel) for the
        // CPU post stack; the CPU Shade contract: normal 0 and HDR NaN on a miss.
        bool wantNormal = normals.Length >= 3 * output.Length;
        bool wantHdr = hdr.Length >= 3 * output.Length;
        if (wantNormal) { normals[3 * idx] = 0f; normals[3 * idx + 1] = 0f; normals[3 * idx + 2] = 0f; }
        if (wantHdr) { hdr[3 * idx] = float.NaN; hdr[3 * idx + 1] = float.NaN; hdr[3 * idx + 2] = float.NaN; }
        int gIdx = wantNormal || wantHdr ? idx : -1;

        var (cdx, cdy, cdz) = GpuKernelUtils.BuildPrimaryRay(x, y, in r);

        // S3 (#567) — thin-lens DOF. Aperture 0 / one sample -> the single centre
        // ray (byte-identical). Otherwise average DofSamples taps whose origin is
        // jittered across the aperture disc, re-aimed through the focal point.
        int dofN = (r.DofSamples > 1 && r.DofAperture > 0.0) ? r.DofSamples : 1;
        if (dofN <= 1) { output[idx] = ShadeRay(r.CamX, r.CamY, r.CamZ, cdx, cdy, cdz, in r, in sp, in p, palette, depth, dIdx, normals, hdr, gIdx, albedo, hdri); return; }
        double fpx = r.CamX + cdx * r.DofFocus, fpy = r.CamY + cdy * r.DofFocus, fpz = r.CamZ + cdz * r.DofFocus;
        double aR = 0, aG = 0, aB = 0, aA = 0;
        for (int k = 0; k < dofN; k++)
        {
            var (u1, u2) = GpuKernelUtils.HashPair(x, y, k, 9);
            var (dkx, dky) = GpuKernelUtils.ConcentricSampleDisk(u1, u2);
            double lx = dkx * r.DofAperture, ly = dky * r.DofAperture;
            double lox = r.CamX + r.RightX * lx + r.UpX * ly;
            double loy = r.CamY + r.RightY * lx + r.UpY * ly;
            double loz = r.CamZ + r.RightZ * lx + r.UpZ * ly;
            double ddx = fpx - lox, ddy = fpy - loy, ddz = fpz - loz;
            double il = 1.0 / Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
            uint c = ShadeRay(lox, loy, loz, ddx * il, ddy * il, ddz * il, in r, in sp, in p, palette, depth, -1, normals, hdr, -1, albedo, hdri);
            aR += (c >> 16) & 0xFF; aG += (c >> 8) & 0xFF; aB += c & 0xFF; aA += (c >> 24) & 0xFF;
        }
        double inv = 1.0 / dofN;
        output[idx] = ((uint)(aA * inv + 0.5) << 24) | ((uint)(aR * inv + 0.5) << 16)
                    | ((uint)(aG * inv + 0.5) << 8) | (uint)(aB * inv + 0.5);
    }

    // S3 (#567) — trace + shade one primary ray from (rox,roy,roz) along (rdx,rdy,rdz).
    // Extracted so the DOF lens loop calls it once per aperture tap; the pinhole path
    // passes the camera position + centre ray -> byte-identical.
    private static uint ShadeRay(
        double rox, double roy, double roz, double rdx, double rdy, double rdz,
        in GpuRaymarchParams r, in GpuShadingParams sp, in KifsFoldGpuParams p, ArrayView<uint> palette,
        ArrayView<float> depth, int depthIdx, ArrayView<float> normals, ArrayView<float> hdr, int gIdx, ArrayView<uint> albedo, ArrayView<uint> hdri)
    {
        var (sphereHit, tEn, _) = GpuKernelUtils.SphereClipFrom(rox, roy, roz, rdx, rdy, rdz, in r);
        if (!sphereHit) return GpuKernelUtils.MissColor(hdri, rdx, rdy, rdz, in r, in sp);

        double px = rox + rdx * tEn;
        double py = roy + rdy * tEn;
        double pz = roz + rdz * tEn;
        double tT = tEn;
        bool hit = false;
        int hitStep = 0;

        for (int step = 0; step < r.MaxSteps; step++)
        {
            double d = FoldDE(px, py, pz, in p);
            if (d < r.Eps) { hit = true; hitStep = step; break; }
            if (tT > p.SceneRadius) break;
            px += rdx * d; py += rdy * d; pz += rdz * d;
            tT += d;
        }

        if (!hit) return GpuKernelUtils.MissColor(hdri, rdx, rdy, rdz, in r, in sp);
        if (depthIdx >= 0) depth[depthIdx] = (float)tT;   // #1070 — ray distance to the hit

        double h = r.Eps * 2;
        double n0 = FoldDE(px + h, py, pz, in p)
                  - FoldDE(px - h, py, pz, in p);
        double n1 = FoldDE(px, py + h, pz, in p)
                  - FoldDE(px, py - h, pz, in p);
        double n2 = FoldDE(px, py, pz + h, in p)
                  - FoldDE(px, py, pz - h, in p);
        double nl = 1.0 / Math.Sqrt(n0 * n0 + n1 * n1 + n2 * n2 + 1e-20);
        double nx = n0 * nl, ny = n1 * nl, nz = n2 * nl;

        // S8 (#484/#486) — resolve point/spot lights at this surface point into a
        // local spL copy (dir overwritten, atten folded into intensity), same
        // pattern as the Mandelbulb kernel (#485). Directional (Type 0) skips the
        // resolve → spL == sp → byte-identical with the pre-S8 GPU path.
        GpuShadingParams spL = sp;
        if (sp.L1Type != 0)
        {
            var r1 = GpuKernelUtils.ResolveLight(sp.L1Type, sp.L1X, sp.L1Y, sp.L1Z,
                sp.L1PX, sp.L1PY, sp.L1PZ, sp.L1Range, sp.L1InnerCos, sp.L1OuterCos, px, py, pz);
            spL.L1X = r1.lx; spL.L1Y = r1.ly; spL.L1Z = r1.lz; spL.L1I = sp.L1I * r1.atten;
        }
        if (sp.L2Type != 0)
        {
            var r2 = GpuKernelUtils.ResolveLight(sp.L2Type, sp.L2X, sp.L2Y, sp.L2Z,
                sp.L2PX, sp.L2PY, sp.L2PZ, sp.L2Range, sp.L2InnerCos, sp.L2OuterCos, px, py, pz);
            spL.L2X = r2.lx; spL.L2Y = r2.ly; spL.L2Z = r2.lz; spL.L2I = sp.L2I * r2.atten;
        }
        if (sp.L3Type != 0)
        {
            var r3 = GpuKernelUtils.ResolveLight(sp.L3Type, sp.L3X, sp.L3Y, sp.L3Z,
                sp.L3PX, sp.L3PY, sp.L3PZ, sp.L3Range, sp.L3InnerCos, sp.L3OuterCos, px, py, pz);
            spL.L3X = r3.lx; spL.L3Y = r3.ly; spL.L3Z = r3.lz; spL.L3I = sp.L3I * r3.atten;
        }

        double bias = r.Eps * 4.0;
        double ox2 = px + nx * bias;
        double oy2 = py + ny * bias;
        double oz2 = pz + nz * bias;
        double sh1 = 1.0, sh2 = 1.0, sh3 = 1.0;
        if (spL.ShadowSteps > 0)
        {
            if ((spL.ShadowLightMask & 0x1) != 0 && spL.L1I > 0)
                sh1 = SoftShadow(ox2, oy2, oz2, spL.L1X, spL.L1Y, spL.L1Z, r.Eps, spL.ShadowTMax, spL.ShadowK1, spL.ShadowSteps, p);
            if ((spL.ShadowLightMask & 0x2) != 0 && spL.L2I > 0)
                sh2 = SoftShadow(ox2, oy2, oz2, spL.L2X, spL.L2Y, spL.L2Z, r.Eps, spL.ShadowTMax, spL.ShadowK2, spL.ShadowSteps, p);
            if ((spL.ShadowLightMask & 0x4) != 0 && spL.L3I > 0)
                sh3 = SoftShadow(ox2, oy2, oz2, spL.L3X, spL.L3Y, spL.L3Z, r.Eps, spL.ShadowTMax, spL.ShadowK3, spL.ShadowSteps, p);
        }

        double ao = 1.0;
        if (sp.AoSamples > 0)
        {
            double occl = 0.0, w = 0.0;
            for (int k = 1; k <= sp.AoSamples; k++)
            {
                double d = r.Eps * (double)(1L << k);
                double sd = FoldDE(px + nx * d, py + ny * d, pz + nz * d, in p);
                occl += Math.Max(0.0, d - sd) / d;
                w += 1.0;
            }
            ao = GpuKernelUtils.Clamp(1.0 - sp.AoStrength * (occl / Math.Max(w, 1.0)), 0.0, 1.0);
        }

        var (aR, aG, aB) = GpuKernelUtils.SurfaceAlbedo(albedo, in spL, hitStep, r.MaxSteps, tT, nx, ny);   // #1172 / G2.2 — colour-map albedo
        // #323 — AOV view: return the diagnostic encoding instead of the beauty shade.
        if (spL.DebugAov != 0)
            return GpuKernelUtils.EncodeSurfaceAov(in spL, nx, ny, nz, rdx, rdy, rdz, px, py, pz,
                sh1, sh2, sh3, ao, aR, aG, aB, tT, hitStep);
        var (br, bg, bb) = GpuKernelUtils.ComposeSurfacePbr(
            hdri, in spL, nx, ny, nz, rdx, rdy, rdz, px, py, pz, sh1, sh2, sh3, ao, aR, aG, aB);

        // P7c.3/16b — N-bounce reflection (fold DE).
        if (sp.ReflectStrength > 0)
        {
            int rSteps = sp.ReflectSteps > 0 ? sp.ReflectSteps : 24;
            double rMax = sp.ReflectMaxDist > 0 ? sp.ReflectMaxDist : 12.0;
            int bounces = sp.ReflectBounces > 0 ? sp.ReflectBounces : 1;
            if (bounces > 6) bounces = 6;
            var (brx0, bry0, brz0) = GpuKernelUtils.Reflect3D(rdx, rdy, rdz, nx, ny, nz);
            double bOx = px + nx * bias;
            double bOy = py + ny * bias;
            double bOz = pz + nz * bias;
            double bnx = nx, bny = ny, bnz = nz;
            double bDirX = brx0, bDirY = bry0, bDirZ = brz0;
            double brdx = rdx, brdy = rdy, brdz = rdz;
            double chainW = sp.ReflectStrength;
            double accR = 0, accG = 0, accB = 0;
            for (int b = 0; b < bounces; b++)
            {
                double w = GpuKernelUtils.FresnelMix(bnx, bny, bnz, brdx, brdy, brdz, sp.Metallic, chainW);
                if (w < 1e-4) break;
                double tR = r.Eps;
                bool hitR = false;
                double hitTR = 0.0;
                double hpx = 0, hpy = 0, hpz = 0;
                for (int s = 0; s < rSteps; s++)
                {
                    hpx = bOx + bDirX * tR;
                    hpy = bOy + bDirY * tR;
                    hpz = bOz + bDirZ * tR;
                    double hR = FoldDE(hpx, hpy, hpz, in p);
                    if (hR < r.Eps * 2.0) { hitR = true; hitTR = tR; break; }
                    tR += hR;
                    if (tR > rMax) break;
                }
                var (rcR, rcG, rcB) = GpuKernelUtils.ReflectShade(hitR, hitTR, bDirX, bDirY, bDirZ, in sp, hdri);
                accR += rcR * w;
                accG += rcG * w;
                accB += rcB * w;
                if (!hitR) break;
                if (b + 1 >= bounces) break;
                double h2 = r.Eps * 2.0;
                double n0b = FoldDE(hpx + h2, hpy, hpz, in p)
                           - FoldDE(hpx - h2, hpy, hpz, in p);
                double n1b = FoldDE(hpx, hpy + h2, hpz, in p)
                           - FoldDE(hpx, hpy - h2, hpz, in p);
                double n2b = FoldDE(hpx, hpy, hpz + h2, in p)
                           - FoldDE(hpx, hpy, hpz - h2, in p);
                double nlb = 1.0 / Math.Sqrt(n0b * n0b + n1b * n1b + n2b * n2b + 1e-20);
                double nbx2 = n0b * nlb, nby2 = n1b * nlb, nbz2 = n2b * nlb;
                brdx = bDirX; brdy = bDirY; brdz = bDirZ;
                var (rrx2, rry2, rrz2) = GpuKernelUtils.Reflect3D(bDirX, bDirY, bDirZ, nbx2, nby2, nbz2);
                bOx = hpx + nbx2 * bias;
                bOy = hpy + nby2 * bias;
                bOz = hpz + nbz2 * bias;
                bnx = nbx2; bny = nby2; bnz = nbz2;
                bDirX = rrx2; bDirY = rry2; bDirZ = rrz2;
                chainW = w;
            }
            br += accR;
            bg += accG;
            bb += accB;
        }

        // P7c.2 — single-scattering volumetric in-scatter (fold DE).
        if (spL.VolumeSteps > 0 && spL.FogDensity > 0
            && (spL.L1I > 0 || spL.L2I > 0 || spL.L3I > 0))
        {
            double camX = px - rdx * tT;
            double camY = py - rdy * tT;
            double camZ = pz - rdz * tT;
            int vs = spL.VolumeSteps;
            if (spL.VolumeStepsFalloff > 0 && tT > 4.0)
                vs = Math.Max(4, (int)(vs / (1.0 + (tT - 4.0) * spL.VolumeStepsFalloff)));
            double stepSize = tT / vs;
            bool ss = spL.ShadowSteps > 0;
            bool sh1On = ss && (spL.ShadowLightMask & 0x1) != 0;
            bool sh2On = ss && (spL.ShadowLightMask & 0x2) != 0;
            bool sh3On = ss && (spL.ShadowLightMask & 0x4) != 0;
            double T = 1.0, inR = 0, inG = 0, inB = 0;
            for (int s = 0; s < vs; s++)
            {
                double t = (s + 0.5) * stepSize;
                double sx = camX + rdx * t;
                double sy = camY + rdy * t;
                double sz = camZ + rdz * t;
                double density = spL.FogDensity;
                if (spL.FogHeightFalloff > 0)
                    density *= Math.Exp(-spL.FogHeightFalloff * sy);
                density *= GpuKernelUtils.VolumetricDensityMul(sx, sy, sz, in spL);
                // Vol-color slice A/B/C GPU parity (#181): every emitting light
                // adds its own colored, phase-weighted single-scatter. Surface
                // soft-shadow marches this fractal's DE inline (ILGPU can't take
                // a struct-generic DE); cloud self-shadow + HG phase + fog-color
                // tint live in GpuKernelUtils, matching the CPU pipe.
                if (spL.L1I > 0)
                {
                    double sh = sh1On ? SoftShadow(sx, sy, sz, spL.L1X, spL.L1Y, spL.L1Z,
                        r.Eps, spL.ShadowTMax, spL.ShadowK1, spL.ShadowSteps, p) : 1.0;
                    var (dR, dG, dB) = GpuKernelUtils.VolumeScatterLight(in spL,
                        sx, sy, sz, spL.L1X, spL.L1Y, spL.L1Z, rdx, rdy, rdz,
                        spL.L1R, spL.L1G, spL.L1B, spL.L1I, sh, T, density, stepSize);
                    inR += dR; inG += dG; inB += dB;
                }
                if (spL.L2I > 0)
                {
                    double sh = sh2On ? SoftShadow(sx, sy, sz, spL.L2X, spL.L2Y, spL.L2Z,
                        r.Eps, spL.ShadowTMax, spL.ShadowK2, spL.ShadowSteps, p) : 1.0;
                    var (dR, dG, dB) = GpuKernelUtils.VolumeScatterLight(in spL,
                        sx, sy, sz, spL.L2X, spL.L2Y, spL.L2Z, rdx, rdy, rdz,
                        spL.L2R, spL.L2G, spL.L2B, spL.L2I, sh, T, density, stepSize);
                    inR += dR; inG += dG; inB += dB;
                }
                if (spL.L3I > 0)
                {
                    double sh = sh3On ? SoftShadow(sx, sy, sz, spL.L3X, spL.L3Y, spL.L3Z,
                        r.Eps, spL.ShadowTMax, spL.ShadowK3, spL.ShadowSteps, p) : 1.0;
                    var (dR, dG, dB) = GpuKernelUtils.VolumeScatterLight(in spL,
                        sx, sy, sz, spL.L3X, spL.L3Y, spL.L3Z, rdx, rdy, rdz,
                        spL.L3R, spL.L3G, spL.L3B, spL.L3I, sh, T, density, stepSize);
                    inR += dR; inG += dG; inB += dB;
                }
                double aT = density * stepSize;
                T *= aT < 1.0 ? GpuKernelUtils.ExpNegSmall(aT) : Math.Exp(-aT);
            }
            // Slice C: medium color / scattering-albedo tint. White fog → ×1 →
            // bit-identical with the pre-parity single-light path.
            double fInR = inR * (spL.FogR / 255.0);
            double fInG = inG * (spL.FogG / 255.0);
            double fInB = inB * (spL.FogB / 255.0);
            // Slice D GPU parity: palette-map the in-scatter through the uploaded
            // theme LUT (no-op when strength 0 / LUT is the length-1 dummy).
            (fInR, fInG, fInB) = GpuKernelUtils.PaletteRemapInScatter(
                in spL, palette, fInR, fInG, fInB, T);
            br = br * T + fInR;
            bg = bg * T + fInG;
            bb = bb * T + fInB;
        }
        else
        {
            (br, bg, bb) = GpuKernelUtils.ApplyScalarFog(in spL, br, bg, bb, rdy, tT);
        }

        // #1172 — G-buffer writes for the post stack (twin of ShadingPipeline.Shade's tail).
        if (gIdx >= 0)
        {
            if (normals.Length > 1) { normals[3 * gIdx] = (float)nx; normals[3 * gIdx + 1] = (float)ny; normals[3 * gIdx + 2] = (float)nz; }
            if (hdr.Length > 1) { hdr[3 * gIdx] = (float)br; hdr[3 * gIdx + 1] = (float)bg; hdr[3 * gIdx + 2] = (float)bb; }
        }

        return GpuKernelUtils.PackBgra(br, bg, bb);
    }

    private static double SoftShadow(
        double ox, double oy, double oz,
        double ldx, double ldy, double ldz,
        double tMin, double tMax, double k, int maxSteps,
        KifsFoldGpuParams p)
    {
        double res = 1.0, t = tMin;
        for (int s = 0; s < maxSteps; s++)
        {
            double px = ox + ldx * t;
            double py = oy + ldy * t;
            double pz = oz + ldz * t;
            double h = FoldDE(px, py, pz, in p);
            if (h < 1e-4) return 0.0;
            if (k > 0) res = Math.Min(res, k * h / t);
            t += h;
            if (t >= tMax) break;
        }
        return GpuKernelUtils.Clamp(res, 0.0, 1.0);
    }

    /// <summary>The fold's DE — dispatches on <see cref="KifsFoldGpuParams.Fold"/>.</summary>
    private static double FoldDE(double cx, double cy, double cz, in KifsFoldGpuParams p)
    {
        if (p.Fold == (int)KifsGpuFold.Dodecahedron)
            return DodecaDE(cx, cy, cz, in p);
        if (p.Fold == (int)KifsGpuFold.MandelboxRot)
            return MandelboxRotDE(cx, cy, cz, in p);
        return OctaDE(cx, cy, cz, in p);
    }

    /// <summary>Twin of <c>KifsCalculator.OctaDE</c>: 30° Y pre-rotation, then the
    /// Menger sort-3 + corner mirror.</summary>
    private static double OctaDE(double cx, double cy, double cz, in KifsFoldGpuParams p)
    {
        double scale = p.Scale;
        int iter = p.DEIter;
        double zx = cx, zy = cy, zz = cz;
        double k = scale - 1.0;
        double offX = k * p.OffsetX;
        double offY = k * p.OffsetY;
        double offZ = k * p.OffsetZ;
        double mirrorThresh = -0.5 * offZ;
        double cosR = p.RotA;
        double sinR = p.RotB;
        for (int i = 0; i < iter; i++)
        {
            double rxr = cosR * zx + sinR * zz;
            double rzr = -sinR * zx + cosR * zz;
            zx = rxr; zz = rzr;

            zx = Math.Abs(zx); zy = Math.Abs(zy); zz = Math.Abs(zz);
            double t;
            if (zx - zy < 0) { t = zx; zx = zy; zy = t; }
            if (zx - zz < 0) { t = zx; zx = zz; zz = t; }
            if (zy - zz < 0) { t = zy; zy = zz; zz = t; }

            zx = scale * zx - offX;
            zy = scale * zy - offY;
            zz = scale * zz;
            if (zz < mirrorThresh) zz += offZ;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        return (rFinal - 2.0) * Math.Pow(scale, -iter);
    }

    /// <summary>Twin of <c>KifsCalculator.DodecaDE</c>: 36° rotation about (1, 1, 1)
    /// per iteration, then the Sierpinski tetrahedron fold.</summary>
    private static double DodecaDE(double cx, double cy, double cz, in KifsFoldGpuParams p)
    {
        double scale = p.Scale;
        int iter = p.DEIter;
        double zx = cx, zy = cy, zz = cz;
        double k = scale - 1.0;
        double offX = k * p.OffsetX;
        double offY = k * p.OffsetY;
        double offZ = k * p.OffsetZ;
        double k1 = p.RotA, k2 = p.RotB, k3 = p.RotC;
        for (int i = 0; i < iter; i++)
        {
            double nx = k1 * zx + k2 * zy + k3 * zz;
            double ny = k3 * zx + k1 * zy + k2 * zz;
            double nz = k2 * zx + k3 * zy + k1 * zz;
            zx = nx; zy = ny; zz = nz;

            double t;
            if (zx + zy < 0) { t = -zy; zy = -zx; zx = t; }
            if (zx + zz < 0) { t = -zz; zz = -zx; zx = t; }
            if (zy + zz < 0) { t = -zz; zz = -zy; zy = t; }

            zx = scale * zx - offX;
            zy = scale * zy - offY;
            zz = scale * zz - offZ;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        return rFinal * Math.Pow(scale, -iter);
    }

    /// <summary>Twin of <c>KifsCalculator.MandelboxRotDE</c>: box fold at ±1, sphere
    /// fold, π/48 Y rotation, scale + offset; the fixed-dr KIFS DE.</summary>
    private static double MandelboxRotDE(double cx, double cy, double cz, in KifsFoldGpuParams p)
    {
        double scale = p.Scale;
        int iter = p.DEIter;
        double zx = cx, zy = cy, zz = cz;
        double k = scale - 1.0;
        double offX = k * p.OffsetX;
        double offY = k * p.OffsetY;
        double offZ = k * p.OffsetZ;
        double cosR = p.RotA;
        double sinR = p.RotB;
        for (int i = 0; i < iter; i++)
        {
            if      (zx >  1.0) zx =  2.0 - zx;
            else if (zx < -1.0) zx = -2.0 - zx;
            if      (zy >  1.0) zy =  2.0 - zy;
            else if (zy < -1.0) zy = -2.0 - zy;
            if      (zz >  1.0) zz =  2.0 - zz;
            else if (zz < -1.0) zz = -2.0 - zz;

            double r2 = zx * zx + zy * zy + zz * zz;
            if (r2 < 0.25)
            {
                zx *= 4.0; zy *= 4.0; zz *= 4.0;
            }
            else if (r2 < 1.0)
            {
                double m = 1.0 / r2;
                zx *= m; zy *= m; zz *= m;
            }

            double nx = cosR * zx + sinR * zz;
            double nz = -sinR * zx + cosR * zz;
            zx = nx; zz = nz;

            zx = scale * zx - offX;
            zy = scale * zy - offY;
            zz = scale * zz - offZ;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        return (rFinal - 2.0) * Math.Pow(scale, -iter);
    }

    public void Dispose() => _kernel = null;
}
