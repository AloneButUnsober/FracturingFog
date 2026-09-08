// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// KleinianGpuCalculator.cs
//
// P7b — ILGPU-backed GPU raymarcher for the Kleinian limit set (fixed
// tetrahedral 4-sphere preset, per the CPU KleinianCalculator). Sphere
// centres are packed as 12 scalar fields rather than an array — ILGPU
// kernels don't take managed arrays as struct fields, and the preset is
// hard-coded at 4 spheres anyway.
//
// Distance estimator: for each iter, find the sphere whose interior most
// contains p (largest negative signed distance); if none, escape. Otherwise
// invert through that sphere and accumulate the scalar inversion scale.
// DE = (nearest signed sphere distance) / accumulated scale. Mirrors
// KleinianCalculator.KleinianDE.

using System;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Per-fractal kernel parameters for the Kleinian limit set.
/// Fixed 4-sphere preset — centres packed scalar-by-scalar so the struct
/// stays blittable for ILGPU. <see cref="Radius"/> is the common tangent
/// radius; sqrt-2 scaled at the CPU side.</summary>
public struct KleinianGpuParams
{
    public double C0X, C0Y, C0Z;
    public double C1X, C1Y, C1Z;
    public double C2X, C2Y, C2Z;
    public double C3X, C3Y, C3Z;
    public double Radius;
    public int DEIter;
    public double SceneRadius;
}

public sealed class KleinianGpuCalculator : IDisposable
{
    private Action<Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, KleinianGpuParams, ArrayView<uint>>? _kernel;
    private bool _initFailed;
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
                Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, KleinianGpuParams, ArrayView<uint>>(KleinianKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Kleinian GPU kernel load failed: {ex.Message}";
            _initFailed = true;
            return false;
        }
    }

    public bool Render(uint[] outBuffer, GpuRaymarchParams r, GpuShadingParams sp, KleinianGpuParams p, uint[]? palette = null)
    {
        if (!TryInit() || _kernel == null) return false;
        if (!GpuAcceleratorHost.TryAcquire(out var acc)) return false;
        try
        {
            int total = r.Width * r.Height;
            using var dev = acc.Allocate1D<uint>(total);
            // Slice D GPU parity — upload the theme palette LUT (or a length-1
            // dummy when off) so the kernel arity stays fixed; the kernel gates
            // on VolumePaletteStrength + LUT length.
            uint[] lut = palette is { Length: >= 2 } ? palette : GpuKernelUtils.PaletteOff;
            using var devLut = acc.Allocate1D<uint>(lut.Length);
            devLut.CopyFromCPU(lut);
            _kernel(total, dev.View, r, sp, p, devLut.View);
            acc.Synchronize();
            dev.CopyToCPU(outBuffer);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Kleinian GPU render failed: {ex.Message}";
            return false;
        }
    }

    private static void KleinianKernel(
        Index1D idx, ArrayView<uint> output, GpuRaymarchParams r, GpuShadingParams sp, KleinianGpuParams p, ArrayView<uint> palette)
    {
        int x = idx % r.Width;
        int y = idx / r.Width;
        if (y >= r.Height) return;

        var (cdx, cdy, cdz) = GpuKernelUtils.BuildPrimaryRay(x, y, in r);

        // S3 (#567) — thin-lens DOF. Aperture 0 / one sample -> the single centre
        // ray (byte-identical). Otherwise average DofSamples taps whose origin is
        // jittered across the aperture disc, re-aimed through the focal point.
        int dofN = (r.DofSamples > 1 && r.DofAperture > 0.0) ? r.DofSamples : 1;
        if (dofN <= 1) { output[idx] = ShadeRay(r.CamX, r.CamY, r.CamZ, cdx, cdy, cdz, in r, in sp, in p, palette); return; }
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
            uint c = ShadeRay(lox, loy, loz, ddx * il, ddy * il, ddz * il, in r, in sp, in p, palette);
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
        in GpuRaymarchParams r, in GpuShadingParams sp, in KleinianGpuParams p, ArrayView<uint> palette)
    {
        var (sphereHit, tEn, _) = GpuKernelUtils.SphereClipFrom(rox, roy, roz, rdx, rdy, rdz, in r);
        if (!sphereHit) return GpuKernelUtils.MissColor(rdy, in r, in sp);

        double px = rox + rdx * tEn;
        double py = roy + rdy * tEn;
        double pz = roz + rdz * tEn;
        double tT = tEn;
        bool hit = false;
        int hitStep = 0;

        for (int step = 0; step < r.MaxSteps; step++)
        {
            double d = KleinianDE(px, py, pz, in p);
            if (d < r.Eps) { hit = true; hitStep = step; break; }
            if (tT > p.SceneRadius) break;
            px += rdx * d; py += rdy * d; pz += rdz * d;
            tT += d;
        }

        if (!hit) return GpuKernelUtils.MissColor(rdy, in r, in sp);

        double h = r.Eps * 2;
        double n0 = KleinianDE(px + h, py, pz, in p) - KleinianDE(px - h, py, pz, in p);
        double n1 = KleinianDE(px, py + h, pz, in p) - KleinianDE(px, py - h, pz, in p);
        double n2 = KleinianDE(px, py, pz + h, in p) - KleinianDE(px, py, pz - h, in p);
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
                sh1 = SoftShadow(ox, oy, oz, spL.L1X, spL.L1Y, spL.L1Z, r.Eps, spL.ShadowTMax, spL.ShadowK1, spL.ShadowSteps, in p);
            if ((spL.ShadowLightMask & 0x2) != 0 && spL.L2I > 0)
                sh2 = SoftShadow(ox, oy, oz, spL.L2X, spL.L2Y, spL.L2Z, r.Eps, spL.ShadowTMax, spL.ShadowK2, spL.ShadowSteps, in p);
            if ((spL.ShadowLightMask & 0x4) != 0 && spL.L3I > 0)
                sh3 = SoftShadow(ox, oy, oz, spL.L3X, spL.L3Y, spL.L3Z, r.Eps, spL.ShadowTMax, spL.ShadowK3, spL.ShadowSteps, in p);
        }

        double ao = 1.0;
        if (sp.AoSamples > 0)
        {
            double occl = 0.0, w = 0.0;
            for (int k = 1; k <= sp.AoSamples; k++)
            {
                double d = r.Eps * (double)(1L << k);
                double sd = KleinianDE(px + nx * d, py + ny * d, pz + nz * d, in p);
                occl += Math.Max(0.0, d - sd) / d;
                w += 1.0;
            }
            ao = Math.Clamp(1.0 - sp.AoStrength * (occl / Math.Max(w, 1.0)), 0.0, 1.0);
        }

        var (aR, aG, aB) = GpuKernelUtils.CheapAlbedo(hitStep, r.MaxSteps, tT);
        var (br, bg, bb) = GpuKernelUtils.ComposeSurfacePbr(
            in spL, nx, ny, nz, rdx, rdy, rdz, px, py, pz, sh1, sh2, sh3, ao, aR, aG, aB);

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
                    double hR = KleinianDE(hpx, hpy, hpz, in p);
                    if (hR < r.Eps * 2.0) { hitR = true; hitTR = tR; break; }
                    tR += hR;
                    if (tR > rMax) break;
                }
                var (rcR, rcG, rcB) = GpuKernelUtils.ReflectShade(hitR, hitTR, bDirY, in sp);
                accR += rcR * w;
                accG += rcG * w;
                accB += rcB * w;
                if (!hitR) break;
                if (b + 1 >= bounces) break;
                double h2 = r.Eps * 2.0;
                double n0b = KleinianDE(hpx + h2, hpy, hpz, in p) - KleinianDE(hpx - h2, hpy, hpz, in p);
                double n1b = KleinianDE(hpx, hpy + h2, hpz, in p) - KleinianDE(hpx, hpy - h2, hpz, in p);
                double n2b = KleinianDE(hpx, hpy, hpz + h2, in p) - KleinianDE(hpx, hpy, hpz - h2, in p);
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
                        r.Eps, spL.ShadowTMax, spL.ShadowK1, spL.ShadowSteps, in p) : 1.0;
                    var (dR, dG, dB) = GpuKernelUtils.VolumeScatterLight(in spL,
                        sx, sy, sz, spL.L1X, spL.L1Y, spL.L1Z, rdx, rdy, rdz,
                        spL.L1R, spL.L1G, spL.L1B, spL.L1I, sh, T, density, stepSize);
                    inR += dR; inG += dG; inB += dB;
                }
                if (spL.L2I > 0)
                {
                    double sh = sh2On ? SoftShadow(sx, sy, sz, spL.L2X, spL.L2Y, spL.L2Z,
                        r.Eps, spL.ShadowTMax, spL.ShadowK2, spL.ShadowSteps, in p) : 1.0;
                    var (dR, dG, dB) = GpuKernelUtils.VolumeScatterLight(in spL,
                        sx, sy, sz, spL.L2X, spL.L2Y, spL.L2Z, rdx, rdy, rdz,
                        spL.L2R, spL.L2G, spL.L2B, spL.L2I, sh, T, density, stepSize);
                    inR += dR; inG += dG; inB += dB;
                }
                if (spL.L3I > 0)
                {
                    double sh = sh3On ? SoftShadow(sx, sy, sz, spL.L3X, spL.L3Y, spL.L3Z,
                        r.Eps, spL.ShadowTMax, spL.ShadowK3, spL.ShadowSteps, in p) : 1.0;
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

        return GpuKernelUtils.PackBgra(br, bg, bb);
    }

    private static double SoftShadow(
        double ox, double oy, double oz,
        double ldx, double ldy, double ldz,
        double tMin, double tMax, double k, int maxSteps,
        in KleinianGpuParams p)
    {
        double res = 1.0, t = tMin;
        for (int s = 0; s < maxSteps; s++)
        {
            double px = ox + ldx * t;
            double py = oy + ldy * t;
            double pz = oz + ldz * t;
            double h = KleinianDE(px, py, pz, in p);
            if (h < 1e-4) return 0.0;
            if (k > 0) res = Math.Min(res, k * h / t);
            t += h;
            if (t >= tMax) break;
        }
        return Math.Clamp(res, 0.0, 1.0);
    }

    /// <summary>Sphere-inversion DE for the tetrahedral Kleinian group.
    /// Hand-unrolled 4-sphere selection to keep the inner loop branchless
    /// of array indexing — ILGPU happily inlines the chain. Mirrors
    /// KleinianCalculator.KleinianDE.</summary>
    private static double KleinianDE(double px, double py, double pz, in KleinianGpuParams p)
    {
        double r = p.Radius;
        double r2 = r * r;
        double scale = 1.0;

        for (int i = 0; i < p.DEIter; i++)
        {
            int bestK = -1;
            double bestDeep = 0.0;

            double dx, dy, dz, d;

            dx = px - p.C0X; dy = py - p.C0Y; dz = pz - p.C0Z;
            d = Math.Sqrt(dx * dx + dy * dy + dz * dz) - r;
            if (d < bestDeep) { bestDeep = d; bestK = 0; }

            dx = px - p.C1X; dy = py - p.C1Y; dz = pz - p.C1Z;
            d = Math.Sqrt(dx * dx + dy * dy + dz * dz) - r;
            if (d < bestDeep) { bestDeep = d; bestK = 1; }

            dx = px - p.C2X; dy = py - p.C2Y; dz = pz - p.C2Z;
            d = Math.Sqrt(dx * dx + dy * dy + dz * dz) - r;
            if (d < bestDeep) { bestDeep = d; bestK = 2; }

            dx = px - p.C3X; dy = py - p.C3Y; dz = pz - p.C3Z;
            d = Math.Sqrt(dx * dx + dy * dy + dz * dz) - r;
            if (d < bestDeep) { bestDeep = d; bestK = 3; }

            if (bestK < 0) break;

            double cx = bestK == 0 ? p.C0X : bestK == 1 ? p.C1X : bestK == 2 ? p.C2X : p.C3X;
            double cy = bestK == 0 ? p.C0Y : bestK == 1 ? p.C1Y : bestK == 2 ? p.C2Y : p.C3Y;
            double cz = bestK == 0 ? p.C0Z : bestK == 1 ? p.C1Z : bestK == 2 ? p.C2Z : p.C3Z;

            double ex = px - cx;
            double ey = py - cy;
            double ez = pz - cz;
            double e2 = ex * ex + ey * ey + ez * ez;
            if (e2 < 1e-30) break;
            double f = r2 / e2;
            scale *= f;
            px = cx + ex * f;
            py = cy + ey * f;
            pz = cz + ez * f;
        }

        double nearest = double.PositiveInfinity;

        double ax, ay, az, a;
        ax = px - p.C0X; ay = py - p.C0Y; az = pz - p.C0Z;
        a = Math.Abs(Math.Sqrt(ax * ax + ay * ay + az * az) - r);
        if (a < nearest) nearest = a;
        ax = px - p.C1X; ay = py - p.C1Y; az = pz - p.C1Z;
        a = Math.Abs(Math.Sqrt(ax * ax + ay * ay + az * az) - r);
        if (a < nearest) nearest = a;
        ax = px - p.C2X; ay = py - p.C2Y; az = pz - p.C2Z;
        a = Math.Abs(Math.Sqrt(ax * ax + ay * ay + az * az) - r);
        if (a < nearest) nearest = a;
        ax = px - p.C3X; ay = py - p.C3Y; az = pz - p.C3Z;
        a = Math.Abs(Math.Sqrt(ax * ax + ay * ay + az * az) - r);
        if (a < nearest) nearest = a;

        if (scale < 1e-30) return 0.0;
        return nearest / scale;
    }

    public void Dispose() => _kernel = null;
}
