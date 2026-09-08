// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MandelboxGpuCalculator.cs
//
// P7a — ILGPU-backed GPU raymarcher for the Mandelbox. Box-fold + sphere-fold
// + scale iteration, dr tracked as scalar. DE = |z| / |dr|. P7c.1 lifts the
// full 3-light Lambert + shadow + AO + fog shade onto the GPU — see
// MandelbulbGpuCalculator for the design notes; same pattern here.

using System;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Per-fractal kernel parameters for the Mandelbox. Pre-squared
/// radii to skip sqrt inside the inner loop.</summary>
public struct MandelboxGpuParams
{
    public double Scale;
    public double FixedR2;
    public double MinR2;
    public double Bailout2;
    public int DEIter;
    /// <summary>Scene-escape ray length — bigger than Mandelbulb's because
    /// the Mandelbox sits at radius (2·|scale|+2), camera floor lifts past
    /// that, and the marcher must still reach the far side.</summary>
    public double SceneRadius;
}

public sealed class MandelboxGpuCalculator : IDisposable
{
    private Action<Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, MandelboxGpuParams, ArrayView<uint>>? _kernel;
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
                Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, MandelboxGpuParams, ArrayView<uint>>(BoxKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Mandelbox GPU kernel load failed: {ex.Message}";
            _initFailed = true;
            return false;
        }
    }

    public bool Render(uint[] outBuffer, GpuRaymarchParams r, GpuShadingParams sp, MandelboxGpuParams p, uint[]? palette = null)
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
            LastError = $"Mandelbox GPU render failed: {ex.Message}";
            return false;
        }
    }

    private static void BoxKernel(
        Index1D idx, ArrayView<uint> output, GpuRaymarchParams r, GpuShadingParams sp, MandelboxGpuParams p, ArrayView<uint> palette)
    {
        int x = idx % r.Width;
        int y = idx / r.Width;
        if (y >= r.Height) return;

        var (rdx, rdy, rdz) = GpuKernelUtils.BuildPrimaryRay(x, y, in r);
        var (sphereHit, tEn, _) = GpuKernelUtils.SphereClip(rdx, rdy, rdz, in r);
        if (!sphereHit) { output[idx] = GpuKernelUtils.MissColor(rdy, in r, in sp); return; }

        double px = r.CamX + rdx * tEn;
        double py = r.CamY + rdy * tEn;
        double pz = r.CamZ + rdz * tEn;
        double tT = tEn;
        bool hit = false;
        int hitStep = 0;

        for (int step = 0; step < r.MaxSteps; step++)
        {
            double d = MandelboxDE(px, py, pz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
            if (d < r.Eps) { hit = true; hitStep = step; break; }
            if (tT > p.SceneRadius) break;
            px += rdx * d; py += rdy * d; pz += rdz * d;
            tT += d;
        }

        if (!hit) { output[idx] = GpuKernelUtils.MissColor(rdy, in r, in sp); return; }

        double h = r.Eps * 2;
        double n0 = MandelboxDE(px + h, py, pz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter)
                  - MandelboxDE(px - h, py, pz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
        double n1 = MandelboxDE(px, py + h, pz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter)
                  - MandelboxDE(px, py - h, pz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
        double n2 = MandelboxDE(px, py, pz + h, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter)
                  - MandelboxDE(px, py, pz - h, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
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
        double ox = px + nx * bias;
        double oy = py + ny * bias;
        double oz = pz + nz * bias;
        double sh1 = 1.0, sh2 = 1.0, sh3 = 1.0;
        if (spL.ShadowSteps > 0)
        {
            if ((spL.ShadowLightMask & 0x1) != 0 && spL.L1I > 0)
                sh1 = SoftShadow(ox, oy, oz, spL.L1X, spL.L1Y, spL.L1Z, r.Eps, spL.ShadowTMax, spL.ShadowK1, spL.ShadowSteps, p);
            if ((spL.ShadowLightMask & 0x2) != 0 && spL.L2I > 0)
                sh2 = SoftShadow(ox, oy, oz, spL.L2X, spL.L2Y, spL.L2Z, r.Eps, spL.ShadowTMax, spL.ShadowK2, spL.ShadowSteps, p);
            if ((spL.ShadowLightMask & 0x4) != 0 && spL.L3I > 0)
                sh3 = SoftShadow(ox, oy, oz, spL.L3X, spL.L3Y, spL.L3Z, r.Eps, spL.ShadowTMax, spL.ShadowK3, spL.ShadowSteps, p);
        }

        double ao = 1.0;
        if (sp.AoSamples > 0)
        {
            double occl = 0.0, w = 0.0;
            for (int k = 1; k <= sp.AoSamples; k++)
            {
                double d = r.Eps * (double)(1L << k);
                double sd = MandelboxDE(px + nx * d, py + ny * d, pz + nz * d, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
                occl += Math.Max(0.0, d - sd) / d;
                w += 1.0;
            }
            ao = Math.Clamp(1.0 - sp.AoStrength * (occl / Math.Max(w, 1.0)), 0.0, 1.0);
        }

        var (aR, aG, aB) = GpuKernelUtils.CheapAlbedo(hitStep, r.MaxSteps, tT);
        var (br, bg, bb) = GpuKernelUtils.ComposeSurfacePbr(
            in spL, nx, ny, nz, rdx, rdy, rdz, px, py, pz, sh1, sh2, sh3, ao, aR, aG, aB);

        // P7c.3/16b — N-bounce reflection (Mandelbox DE).
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
                    double hR = MandelboxDE(hpx, hpy, hpz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
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
                double n0b = MandelboxDE(hpx + h2, hpy, hpz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter)
                           - MandelboxDE(hpx - h2, hpy, hpz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
                double n1b = MandelboxDE(hpx, hpy + h2, hpz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter)
                           - MandelboxDE(hpx, hpy - h2, hpz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
                double n2b = MandelboxDE(hpx, hpy, hpz + h2, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter)
                           - MandelboxDE(hpx, hpy, hpz - h2, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
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

        // P7c.2 — single-scattering volumetric in-scatter (Mandelbox DE).
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

        output[idx] = GpuKernelUtils.PackBgra(br, bg, bb);
    }

    private static double SoftShadow(
        double ox, double oy, double oz,
        double ldx, double ldy, double ldz,
        double tMin, double tMax, double k, int maxSteps,
        MandelboxGpuParams p)
    {
        double res = 1.0, t = tMin;
        for (int s = 0; s < maxSteps; s++)
        {
            double px = ox + ldx * t;
            double py = oy + ldy * t;
            double pz = oz + ldz * t;
            double h = MandelboxDE(px, py, pz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
            if (h < 1e-4) return 0.0;
            if (k > 0) res = Math.Min(res, k * h / t);
            t += h;
            if (t >= tMax) break;
        }
        return Math.Clamp(res, 0.0, 1.0);
    }

    /// <summary>Mandelbox DE — box-fold (reflect across ±1), sphere-fold
    /// (scale by fixedR²/r² in band, by fixedR²/minR² inside minR), then
    /// z = scale·z + c, dr = |scale|·dr + 1. DE = |z| / |dr|.</summary>
    private static double MandelboxDE(double cx, double cy, double cz,
        double scale, double fixedR2, double minR2, double bailout2, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double dr = 1.0;
        for (int i = 0; i < iter; i++)
        {
            if (zx > 1.0) zx = 2.0 - zx; else if (zx < -1.0) zx = -2.0 - zx;
            if (zy > 1.0) zy = 2.0 - zy; else if (zy < -1.0) zy = -2.0 - zy;
            if (zz > 1.0) zz = 2.0 - zz; else if (zz < -1.0) zz = -2.0 - zz;

            double r2 = zx * zx + zy * zy + zz * zz;
            if (r2 < minR2)
            {
                double f = fixedR2 / minR2;
                zx *= f; zy *= f; zz *= f;
                dr *= f;
            }
            else if (r2 < fixedR2)
            {
                double f = fixedR2 / r2;
                zx *= f; zy *= f; zz *= f;
                dr *= f;
            }

            zx = scale * zx + cx;
            zy = scale * zy + cy;
            zz = scale * zz + cz;
            dr = dr * Math.Abs(scale) + 1.0;

            if (zx * zx + zy * zy + zz * zz > bailout2) break;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        return rFinal / Math.Max(Math.Abs(dr), 1e-10);
    }

    public void Dispose() => _kernel = null;
}
