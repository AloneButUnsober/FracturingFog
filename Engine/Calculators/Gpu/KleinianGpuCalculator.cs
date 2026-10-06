// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// KleinianGpuCalculator.cs
//
// P7b — ILGPU-backed GPU raymarcher for the Kleinian limit set.
//
// #880 / #1173-E (GPU parity G3.4) — any inversion group, not just the uniform
// tetrahedral 4-sphere preset: the generators arrive as a buffer of (cx, cy, cz, r)
// quadruples (KleinianGpuParams.GenCount of them; presets up to the 24-sphere
// necklace, or any custom list), each with its own radius. The #877 rotation fold,
// #878 word-length / last-generator colouring and #881 under-relaxed stepping run
// in the kernel too. The generator count is uniform across a launch, so the
// variable-length descent doesn't diverge between threads.
//
// Distance estimator: for each iter, find the sphere whose interior most
// contains p (largest negative signed distance); if none, escape. Otherwise
// invert through that sphere and accumulate the scalar inversion scale.
// DE = (nearest signed sphere distance) / accumulated scale. Mirrors
// KleinianCalculator.KleinianDE.

using System;
using System.Threading;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Per-fractal kernel parameters for the Kleinian limit set. The
/// generators themselves are the kernel's generator buffer
/// (<see cref="KleinianGpuCalculator.PackGenerators"/>); this carries their count
/// and the group-wide settings.</summary>
public struct KleinianGpuParams
{
    /// <summary>Number of (cx, cy, cz, r) generators in the generator buffer.</summary>
    public int GenCount;
    public int DEIter;
    public double SceneRadius;

    /// <summary>#877 — rotation fold after each inversion (KleinianRotation):
    /// RotHas 0 = none, else Rodrigues about the unit axis by the angle whose
    /// sine / cosine are RotSin / RotCos.</summary>
    public int RotHas;
    public double RotAx, RotAy, RotAz, RotSin, RotCos;

    /// <summary>#878 — KleinianColorSource: 0 Smooth (step / depth blend), else the
    /// descent word sampled just inside the surface (1 WordLength, 2 LastGenerator;
    /// the enum values).</summary>
    public int ColorSource;

    /// <summary>#881 — primary-march step factor (1 = plain sphere tracing).</summary>
    public double DeFactor;
}

public sealed class KleinianGpuCalculator : IDisposable
{
    private Action<Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, KleinianGpuParams, ArrayView<uint>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<uint>, ArrayView<uint>, ArrayView<double>>? _kernel;
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
                Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, KleinianGpuParams, ArrayView<uint>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<uint>, ArrayView<uint>, ArrayView<double>>(KleinianKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Kleinian GPU kernel load failed: {ex.GetBaseException().Message}";
            _initFailed = true;
            return false;
        }
    }

    public bool Render(uint[] outBuffer, GpuRaymarchParams r, GpuShadingParams sp, KleinianGpuParams p, uint[]? palette = null, float[]? depthOut = null, CancellationToken ct = default, float[]? normalOut = null, float[]? hdrOut = null, uint[]? albedoLut = null, uint[]? hdri = null, double[]? generators = null)
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
            // #880 — the generator list, (cx, cy, cz, r) per generator.
            if (generators is null || generators.Length < 4 * Math.Max(1, p.GenCount))
            {
                LastError = "Kleinian GPU render: generator buffer missing or shorter than GenCount";
                return false;
            }
            using var devGens = GpuMemoryStats.Allocate1D<double>(acc, generators.Length);
            devGens.CopyFromCPU(generators);
            // #1170 — tiled under the GPU watchdog; byte-identical to one launch.
            var kernel = _kernel;
            var run = GpuTiledDispatch.Run(acc, r, (n, rt) => kernel(n, dev.View, rt, sp, p, devLut.View, devDepth.View, devNormal.View, devHdr.View, devAlbedo.View, devHdri.View, devGens.View), ct);
            if (run == GpuDispatchResult.TooSlow)
            {
                // #1169 — this device can't render this kernel in useful time; stay on the CPU.
                LastError = GpuTiledDispatch.TooSlowMessage("Kleinian");
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
            LastError = $"Kleinian GPU render failed: {ex.Message}";
            GpuAcceleratorHost.ReportRenderFault(acc, ex);
            return false;
        }
    }

    private static void KleinianKernel(
        Index1D tid, ArrayView<uint> output, GpuRaymarchParams r, GpuShadingParams sp, KleinianGpuParams p, ArrayView<uint> palette, ArrayView<float> depth, ArrayView<float> normals, ArrayView<float> hdr, ArrayView<uint> albedo, ArrayView<uint> hdri, ArrayView<double> gens)
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
        if (dofN <= 1) { output[idx] = ShadeRay(r.CamX, r.CamY, r.CamZ, cdx, cdy, cdz, in r, in sp, in p, palette, depth, dIdx, normals, hdr, gIdx, albedo, hdri, gens); return; }
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
            uint c = ShadeRay(lox, loy, loz, ddx * il, ddy * il, ddz * il, in r, in sp, in p, palette, depth, -1, normals, hdr, -1, albedo, hdri, gens);
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
        in GpuRaymarchParams r, in GpuShadingParams sp, in KleinianGpuParams p, ArrayView<uint> palette,
        ArrayView<float> depth, int depthIdx, ArrayView<float> normals, ArrayView<float> hdr, int gIdx, ArrayView<uint> albedo, ArrayView<uint> hdri, ArrayView<double> gens)
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
            double d = KleinianDE(px, py, pz, in p, gens);
            if (d < r.Eps) { hit = true; hitStep = step; break; }
            if (tT > p.SceneRadius) break;
            double mstep = d * p.DeFactor;   // #881 — under-relaxed step (1 = plain)
            px += rdx * mstep; py += rdy * mstep; pz += rdz * mstep;
            tT += mstep;
        }

        if (!hit) return GpuKernelUtils.MissColor(hdri, rdx, rdy, rdz, in r, in sp);
        if (depthIdx >= 0) depth[depthIdx] = (float)tT;   // #1070 — ray distance to the hit

        double h = r.Eps * 2;
        double n0 = KleinianDE(px + h, py, pz, in p, gens) - KleinianDE(px - h, py, pz, in p, gens);
        double n1 = KleinianDE(px, py + h, pz, in p, gens) - KleinianDE(px, py - h, pz, in p, gens);
        double n2 = KleinianDE(px, py, pz + h, in p, gens) - KleinianDE(px, py, pz - h, in p, gens);
        double nl = 1.0 / Math.Sqrt(n0 * n0 + n1 * n1 + n2 * n2 + 1e-20);
        double nx = n0 * nl, ny = n1 * nl, nz = n2 * nl;

        // S8 (#484/#487) — resolve point/spot lights at this surface point into a
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
        double ox = px + nx * bias;
        double oy = py + ny * bias;
        double oz = pz + nz * bias;
        double sh1 = 1.0, sh2 = 1.0, sh3 = 1.0;
        if (spL.ShadowSteps > 0)
        {
            if ((spL.ShadowLightMask & 0x1) != 0 && spL.L1I > 0)
                sh1 = SoftShadow(ox, oy, oz, spL.L1X, spL.L1Y, spL.L1Z, r.Eps, spL.ShadowTMax, spL.ShadowK1, spL.ShadowSteps, in p, gens);
            if ((spL.ShadowLightMask & 0x2) != 0 && spL.L2I > 0)
                sh2 = SoftShadow(ox, oy, oz, spL.L2X, spL.L2Y, spL.L2Z, r.Eps, spL.ShadowTMax, spL.ShadowK2, spL.ShadowSteps, in p, gens);
            if ((spL.ShadowLightMask & 0x4) != 0 && spL.L3I > 0)
                sh3 = SoftShadow(ox, oy, oz, spL.L3X, spL.L3Y, spL.L3Z, r.Eps, spL.ShadowTMax, spL.ShadowK3, spL.ShadowSteps, in p, gens);
        }

        double ao = 1.0;
        if (sp.AoSamples > 0)
        {
            double occl = 0.0, w = 0.0;
            for (int k = 1; k <= sp.AoSamples; k++)
            {
                double d = r.Eps * (double)(1L << k);
                double sd = KleinianDE(px + nx * d, py + ny * d, pz + nz * d, in p, gens);
                occl += Math.Max(0.0, d - sd) / d;
                w += 1.0;
            }
            ao = GpuKernelUtils.Clamp(1.0 - sp.AoStrength * (occl / Math.Max(w, 1.0)), 0.0, 1.0);
        }

        // #1172 / G2.2 — colour-map albedo; #878 — word colouring samples the descent
        // just inside the surface, as the CPU KleinianColorScalar does.
        var (aR, aG, aB) = p.ColorSource != 0
            ? GpuKernelUtils.SurfaceAlbedoAt(albedo, in spL,
                KleinianColorScalar(px + rdx * r.Eps * 4, py + rdy * r.Eps * 4, pz + rdz * r.Eps * 4, in p, gens),
                nx, ny, hitStep, r.MaxSteps, tT)
            : GpuKernelUtils.SurfaceAlbedo(albedo, in spL, hitStep, r.MaxSteps, tT, nx, ny);
        // #323 — AOV view: return the diagnostic encoding instead of the beauty shade.
        if (spL.DebugAov != 0)
            return GpuKernelUtils.EncodeSurfaceAov(in spL, nx, ny, nz, rdx, rdy, rdz, px, py, pz,
                sh1, sh2, sh3, ao, aR, aG, aB, tT, hitStep);
        var (br, bg, bb) = GpuKernelUtils.ComposeSurfacePbr(
            hdri, in spL, nx, ny, nz, rdx, rdy, rdz, px, py, pz, sh1, sh2, sh3, ao, aR, aG, aB);

        // P7c.3/16b — N-bounce reflection (Kleinian DE — DE takes 'in p').
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
                    double hR = KleinianDE(hpx, hpy, hpz, in p, gens);
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
                double n0b = KleinianDE(hpx + h2, hpy, hpz, in p, gens) - KleinianDE(hpx - h2, hpy, hpz, in p, gens);
                double n1b = KleinianDE(hpx, hpy + h2, hpz, in p, gens) - KleinianDE(hpx, hpy - h2, hpz, in p, gens);
                double n2b = KleinianDE(hpx, hpy, hpz + h2, in p, gens) - KleinianDE(hpx, hpy, hpz - h2, in p, gens);
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

        // P7c.2 — single-scattering volumetric in-scatter (Kleinian DE).
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
                        r.Eps, spL.ShadowTMax, spL.ShadowK1, spL.ShadowSteps, in p, gens) : 1.0;
                    var (dR, dG, dB) = GpuKernelUtils.VolumeScatterLight(in spL,
                        sx, sy, sz, spL.L1X, spL.L1Y, spL.L1Z, rdx, rdy, rdz,
                        spL.L1R, spL.L1G, spL.L1B, spL.L1I, sh, T, density, stepSize);
                    inR += dR; inG += dG; inB += dB;
                }
                if (spL.L2I > 0)
                {
                    double sh = sh2On ? SoftShadow(sx, sy, sz, spL.L2X, spL.L2Y, spL.L2Z,
                        r.Eps, spL.ShadowTMax, spL.ShadowK2, spL.ShadowSteps, in p, gens) : 1.0;
                    var (dR, dG, dB) = GpuKernelUtils.VolumeScatterLight(in spL,
                        sx, sy, sz, spL.L2X, spL.L2Y, spL.L2Z, rdx, rdy, rdz,
                        spL.L2R, spL.L2G, spL.L2B, spL.L2I, sh, T, density, stepSize);
                    inR += dR; inG += dG; inB += dB;
                }
                if (spL.L3I > 0)
                {
                    double sh = sh3On ? SoftShadow(sx, sy, sz, spL.L3X, spL.L3Y, spL.L3Z,
                        r.Eps, spL.ShadowTMax, spL.ShadowK3, spL.ShadowSteps, in p, gens) : 1.0;
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
        in KleinianGpuParams p, ArrayView<double> gens)
    {
        double res = 1.0, t = tMin;
        for (int s = 0; s < maxSteps; s++)
        {
            double px = ox + ldx * t;
            double py = oy + ldy * t;
            double pz = oz + ldz * t;
            double h = KleinianDE(px, py, pz, in p, gens);
            if (h < 1e-4) return 0.0;
            if (k > 0) res = Math.Min(res, k * h / t);
            t += h;
            if (t >= tMax) break;
        }
        return GpuKernelUtils.Clamp(res, 0.0, 1.0);
    }

    /// <summary>Sphere-inversion DE for the tetrahedral Kleinian group.
    /// Hand-unrolled 4-sphere selection to keep the inner loop branchless
    /// of array indexing — ILGPU happily inlines the chain. Mirrors
    /// KleinianCalculator.KleinianDE.</summary>
    /// <summary>#880 — pack a generator list as the kernel's buffer: (cx, cy, cz, r)
    /// per generator, in order (the order picks ties exactly as the CPU loop does).</summary>
    public static double[] PackGenerators(FracturingFog.Models.KleinianGenerator[] gens)
    {
        var buf = new double[4 * gens.Length];
        for (int k = 0; k < gens.Length; k++)
        {
            buf[4 * k] = gens[k].Cx; buf[4 * k + 1] = gens[k].Cy;
            buf[4 * k + 2] = gens[k].Cz; buf[4 * k + 3] = gens[k].R;
        }
        return buf;
    }

    /// <summary>#877 — twin of <c>KleinianCalculator.RotateInPlace</c> (Rodrigues).</summary>
    private static (double x, double y, double z) Rotate(double px, double py, double pz, in KleinianGpuParams p)
    {
        double kv = p.RotAx * px + p.RotAy * py + p.RotAz * pz;
        double kxx = p.RotAy * pz - p.RotAz * py;
        double kxy = p.RotAz * px - p.RotAx * pz;
        double kxz = p.RotAx * py - p.RotAy * px;
        double om = 1.0 - p.RotCos;
        return (px * p.RotCos + kxx * p.RotSin + p.RotAx * kv * om,
                py * p.RotCos + kxy * p.RotSin + p.RotAy * kv * om,
                pz * p.RotCos + kxz * p.RotSin + p.RotAz * kv * om);
    }

    /// <summary>Twin of <c>KleinianCalculator.KleinianDE</c> over the generator buffer:
    /// invert through the deepest containing sphere until the point escapes every
    /// sphere (rotating after each inversion when the fold is on), tracking the
    /// inversion-scale product; DE = nearest sphere boundary / scale.</summary>
    private static double KleinianDE(double px, double py, double pz, in KleinianGpuParams p, ArrayView<double> gens)
    {
        double scale = 1.0;
        int n = p.GenCount;

        for (int i = 0; i < p.DEIter; i++)
        {
            int bestK = -1;
            double bestDeep = 0.0;
            for (int k = 0; k < n; k++)
            {
                double dx = px - gens[4 * k];
                double dy = py - gens[4 * k + 1];
                double dz = pz - gens[4 * k + 2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz) - gens[4 * k + 3];
                if (d < bestDeep) { bestDeep = d; bestK = k; }
            }
            if (bestK < 0) break;

            double cx = gens[4 * bestK], cy = gens[4 * bestK + 1], cz = gens[4 * bestK + 2], br = gens[4 * bestK + 3];
            double ex = px - cx;
            double ey = py - cy;
            double ez = pz - cz;
            double e2 = ex * ex + ey * ey + ez * ez;
            if (e2 < 1e-30) break;
            double f = (br * br) / e2;
            scale *= f;
            px = cx + ex * f;
            py = cy + ey * f;
            pz = cz + ez * f;
            if (p.RotHas != 0) (px, py, pz) = Rotate(px, py, pz, in p);   // #877
        }

        double nearest = double.PositiveInfinity;
        for (int k = 0; k < n; k++)
        {
            double dx = px - gens[4 * k];
            double dy = py - gens[4 * k + 1];
            double dz = pz - gens[4 * k + 2];
            double a = Math.Abs(Math.Sqrt(dx * dx + dy * dy + dz * dz) - gens[4 * k + 3]);
            if (a < nearest) nearest = a;
        }
        if (scale < 1e-30) return 0.0;
        return nearest / scale;
    }

    /// <summary>#878 — twin of <c>KleinianCalculator.KleinianWord</c> +
    /// <c>KleinianColorScalar</c>: run the descent and return the palette scalar —
    /// LastGenerator (g + 0.5) / n · 256, WordLength depth / iter · 255.</summary>
    private static float KleinianColorScalar(double px, double py, double pz, in KleinianGpuParams p, ArrayView<double> gens)
    {
        int n = p.GenCount;
        int depth = 0, lastGen = -1;
        for (int i = 0; i < p.DEIter; i++)
        {
            int bestK = -1;
            double bestDeep = 0.0;
            for (int k = 0; k < n; k++)
            {
                double dx = px - gens[4 * k];
                double dy = py - gens[4 * k + 1];
                double dz = pz - gens[4 * k + 2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz) - gens[4 * k + 3];
                if (d < bestDeep) { bestDeep = d; bestK = k; }
            }
            if (bestK < 0) break;
            double cx = gens[4 * bestK], cy = gens[4 * bestK + 1], cz = gens[4 * bestK + 2], br = gens[4 * bestK + 3];
            double ex = px - cx, ey = py - cy, ez = pz - cz;
            double e2 = ex * ex + ey * ey + ez * ez;
            if (e2 < 1e-30) break;
            double f = (br * br) / e2;
            px = cx + ex * f; py = cy + ey * f; pz = cz + ez * f;
            if (p.RotHas != 0) (px, py, pz) = Rotate(px, py, pz, in p);
            depth++; lastGen = bestK;
        }
        if (p.ColorSource == 2)   // LastGenerator
        {
            int nn = n < 1 ? 1 : n;
            int g = lastGen < 0 ? 0 : lastGen;
            return (float)((g + 0.5) / nn * 256.0);
        }
        float wl = (float)depth / (p.DEIter < 1 ? 1 : p.DEIter);
        return wl * 255.0f;
    }

    public void Dispose() => _kernel = null;
}
