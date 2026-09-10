// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MandelbulbGpuCalculator.cs
//
// P7a — ILGPU-backed GPU raymarcher for the Mandelbulb (triplex power-N).
// Borrows the shared accelerator from GpuAcceleratorHost, the camera/ray
// struct from GpuRaymarchParams, and the ray construction / sphere clip /
// shading helpers from GpuKernelUtils.
//
// P7c.1 — shading now lifts the CPU pipeline's 3-light Lambert + soft
// shadow (per light, gated by ShadowLightMask) + DE-cone AO + scalar exp
// fog with sky-gradient tint. Albedo is still the cheap step-hash palette
// (per-pixel color-map / driver GPU port = separate phase). PBR specular,
// SSS, triplanar, IBL, caustics, reflection, volumetric in-scatter all
// silently drop on the GPU branch and ship in later P7c sub-phases.
//
// Lifecycle: one MandelbulbGpuCalculator per CPU MandelbulbCalculator.
// Kernel is loaded lazily on first Render. Disposal is a no-op for the
// accelerator (owned by GpuAcceleratorHost); only the kernel delegate is
// released so a fresh load can re-JIT after a fold-switch / driver event.
//
// Falls back to CPU via the calling code on TryInit==false or any kernel
// throw at Render time.

using System;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Per-fractal kernel parameters for the Mandelbulb. Pass-by-value
/// alongside the shared <see cref="GpuRaymarchParams"/>. Triplex power-N
/// formula — Power=2 is the fast square branch on CPU; on GPU we use the
/// general Pow(r, Power) path for both since the Power==2 branch saves only
/// a single Pow call and complicates the JIT'd kernel.</summary>
public struct MandelbulbGpuParams
{
    /// <summary>Triplex exponent (8.0 is the canonical Mandelbulb).</summary>
    public double Power;
    /// <summary>DE iteration cap. Same value used for the on-hit normal
    /// gradient samples so the surface and the lit shade are consistent.</summary>
    public int DEIter;
    /// <summary>Bailout magnitude on |z| inside the DE iter. Match CPU 2.0.</summary>
    public double Bailout;
    /// <summary>Scene-escape ray length — once tTotal exceeds this the
    /// march bails to InSetColor. Mirrors the CPU calculator's tTotal>12
    /// guard (caller passes the scaled-for-camera-distance value).</summary>
    public double SceneRadius;
}

public sealed class MandelbulbGpuCalculator : IDisposable
{
    private Action<Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, MandelbulbGpuParams, ArrayView<uint>>? _kernel;
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
                Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, MandelbulbGpuParams, ArrayView<uint>>(BulbKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Mandelbulb GPU kernel load failed: {ex.GetBaseException().Message}";
            _initFailed = true;
            return false;
        }
    }

    /// <summary>Render one frame into <paramref name="outBuffer"/>. Returns
    /// false on init or kernel failure — caller falls back to CPU. The
    /// output buffer length must equal <c>r.Width * r.Height</c>.</summary>
    public bool Render(uint[] outBuffer, GpuRaymarchParams r, GpuShadingParams sp, MandelbulbGpuParams p, uint[]? palette = null)
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
            LastError = $"Mandelbulb GPU render failed: {ex.Message}";
            return false;
        }
    }

    // ── Kernel ──────────────────────────────────────────────────────────────
    private static void BulbKernel(
        Index1D idx, ArrayView<uint> output, GpuRaymarchParams r, GpuShadingParams sp, MandelbulbGpuParams p, ArrayView<uint> palette)
    {
        int x = idx % r.Width;
        int y = idx / r.Width;
        if (y >= r.Height) return;

        var (cdx, cdy, cdz) = GpuKernelUtils.BuildPrimaryRay(x, y, in r);

        // S3 (#567) — thin-lens DOF. Aperture 0 / one sample → the single centre
        // ray (byte-identical). Otherwise average DofSamples taps whose origin is
        // jittered across the aperture disc (HashPair-seeded Shirley disc) and
        // re-aimed through the focal point. Twin of CameraDof.ThinLensRay /
        // ThinLensDof; matches the relief GPU DOF lens loop (jitter every tap).
        int dofN = (r.DofSamples > 1 && r.DofAperture > 0.0) ? r.DofSamples : 1;
        if (dofN <= 1)
        {
            output[idx] = ShadeBulbRay(r.CamX, r.CamY, r.CamZ, cdx, cdy, cdz, in r, in sp, in p, palette);
            return;
        }

        double fpx = r.CamX + cdx * r.DofFocus;
        double fpy = r.CamY + cdy * r.DofFocus;
        double fpz = r.CamZ + cdz * r.DofFocus;
        double aR = 0, aG = 0, aB = 0, aA = 0;
        for (int k = 0; k < dofN; k++)
        {
            var (u1, u2) = GpuKernelUtils.HashPair(x, y, k, 9);
            var (dkx, dky) = GpuKernelUtils.ConcentricSampleDisk(u1, u2);
            double lx = dkx * r.DofAperture, ly = dky * r.DofAperture;
            double ox = r.CamX + r.RightX * lx + r.UpX * ly;
            double oy = r.CamY + r.RightY * lx + r.UpY * ly;
            double oz = r.CamZ + r.RightZ * lx + r.UpZ * ly;
            double ddx = fpx - ox, ddy = fpy - oy, ddz = fpz - oz;
            double il = 1.0 / Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
            uint c = ShadeBulbRay(ox, oy, oz, ddx * il, ddy * il, ddz * il, in r, in sp, in p, palette);
            aR += (c >> 16) & 0xFF; aG += (c >> 8) & 0xFF; aB += c & 0xFF; aA += (c >> 24) & 0xFF;
        }
        double inv = 1.0 / dofN;
        output[idx] = ((uint)(aA * inv + 0.5) << 24)
                    | ((uint)(aR * inv + 0.5) << 16)
                    | ((uint)(aG * inv + 0.5) << 8)
                    | (uint)(aB * inv + 0.5);
    }

    // S3 (#567) — trace + shade one primary ray from (ox,oy,oz) along (rdx,rdy,rdz).
    // Extracted from BulbKernel so the DOF lens loop calls it once per aperture tap;
    // the pinhole path passes the camera position + centre ray → byte-identical.
    private static uint ShadeBulbRay(
        double rox, double roy, double roz, double rdx, double rdy, double rdz,
        in GpuRaymarchParams r, in GpuShadingParams sp, in MandelbulbGpuParams p, ArrayView<uint> palette)
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
            double d = MandelbulbDE(px, py, pz, p.Power, p.DEIter, p.Bailout);
            if (d < r.Eps) { hit = true; hitStep = step; break; }
            if (tT > p.SceneRadius) break;
            px += rdx * d; py += rdy * d; pz += rdz * d;
            tT += d;
        }

        if (!hit) return GpuKernelUtils.MissColor(rdy, in r, in sp);

        // Central-difference normals matching the CPU path.
        double h = r.Eps * 2;
        double n0 = MandelbulbDE(px + h, py, pz, p.Power, p.DEIter, p.Bailout)
                  - MandelbulbDE(px - h, py, pz, p.Power, p.DEIter, p.Bailout);
        double n1 = MandelbulbDE(px, py + h, pz, p.Power, p.DEIter, p.Bailout)
                  - MandelbulbDE(px, py - h, pz, p.Power, p.DEIter, p.Bailout);
        double n2 = MandelbulbDE(px, py, pz + h, p.Power, p.DEIter, p.Bailout)
                  - MandelbulbDE(px, py, pz - h, p.Power, p.DEIter, p.Bailout);
        double nl = 1.0 / Math.Sqrt(n0 * n0 + n1 * n1 + n2 * n2 + 1e-20);
        double nx = n0 * nl, ny = n1 * nl, nz = n2 * nl;

        // S8 (#484/#485) — resolve point/spot lights at this surface point.
        // ResolveLight returns the surface→light unit direction + attenuation
        // (twin of ShadingPipeline.ResolveLight/LightSampler); fold the atten
        // into the effective intensity and overwrite the baked direction, so
        // the rest of the shade (shadow march, ComposeSurfacePbr, volumetric)
        // uses the positional light. Directional lights (Type 0) skip this →
        // spL == sp → byte-identical with the pre-S8 GPU path.
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

        // Per-light soft shadow. Bias the origin off the surface by eps·4 so
        // the first DE sample doesn't trigger on the hit itself. Mirrors
        // ShadingPipeline.Shade's shadow block.
        double bias = r.Eps * 4.0;
        double ox = px + nx * bias;
        double oy = py + ny * bias;
        double oz = pz + nz * bias;
        double sh1 = 1.0, sh2 = 1.0, sh3 = 1.0;
        if (spL.ShadowSteps > 0)
        {
            if ((spL.ShadowLightMask & 0x1) != 0 && spL.L1I > 0)
                sh1 = SoftShadow(ox, oy, oz, spL.L1X, spL.L1Y, spL.L1Z,
                    r.Eps, spL.ShadowTMax, spL.ShadowK1, spL.ShadowSteps, p);
            if ((spL.ShadowLightMask & 0x2) != 0 && spL.L2I > 0)
                sh2 = SoftShadow(ox, oy, oz, spL.L2X, spL.L2Y, spL.L2Z,
                    r.Eps, spL.ShadowTMax, spL.ShadowK2, spL.ShadowSteps, p);
            if ((spL.ShadowLightMask & 0x4) != 0 && spL.L3I > 0)
                sh3 = SoftShadow(ox, oy, oz, spL.L3X, spL.L3Y, spL.L3Z,
                    r.Eps, spL.ShadowTMax, spL.ShadowK3, spL.ShadowSteps, p);
        }

        // DE-cone AO. Mirrors ShadingPipeline.Shade's AO block.
        double ao = 1.0;
        if (sp.AoSamples > 0)
        {
            double occl = 0.0, w = 0.0;
            for (int k = 1; k <= sp.AoSamples; k++)
            {
                double d = r.Eps * (double)(1L << k);
                double sd = MandelbulbDE(px + nx * d, py + ny * d, pz + nz * d, p.Power, p.DEIter, p.Bailout);
                occl += Math.Max(0.0, d - sd) / d;
                w += 1.0;
            }
            ao = GpuKernelUtils.Clamp(1.0 - sp.AoStrength * (occl / Math.Max(w, 1.0)), 0.0, 1.0);
        }

        var (aR, aG, aB) = GpuKernelUtils.CheapAlbedo(hitStep, r.MaxSteps, tT);
        var (br, bg, bb) = GpuKernelUtils.ComposeSurfacePbr(
            in spL, nx, ny, nz, rdx, rdy, rdz, px, py, pz, sh1, sh2, sh3, ao, aR, aG, aB);

        // P7c.3 — reflection probe. Reflect view ray about the surface
        // normal, sphere-trace this fractal's DE to either a hit (sky tint
        // attenuated by depth) or the sky. Mix by Schlick Fresnel ramped
        // toward Metallic. ReflectStrength==0 → skip (bit-identical legacy).
        //
        // Phase 16b — N-bounce loop driven by ReflectBounces (default 1 =
        // legacy single bounce). Each bounce: sphere-trace, on miss
        // accumulate sky-tint × chain weight + stop; on hit accumulate
        // env-proxy × chain weight, recompute normal via central differences
        // (six DE evals), reflect bounce dir, chain weight *= F for next.
        if (sp.ReflectStrength > 0)
        {
            int rSteps = sp.ReflectSteps > 0 ? sp.ReflectSteps : 24;
            double rMax = sp.ReflectMaxDist > 0 ? sp.ReflectMaxDist : 12.0;
            int bounces = sp.ReflectBounces > 0 ? sp.ReflectBounces : 1;
            if (bounces > 6) bounces = 6;

            // Current bounce state. Start at the primary hit.
            double brx0, bry0, brz0;
            (brx0, bry0, brz0) = GpuKernelUtils.Reflect3D(rdx, rdy, rdz, nx, ny, nz);
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
                    double hR = MandelbulbDE(hpx, hpy, hpz, p.Power, p.DEIter, p.Bailout);
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

                // Recompute normal at bounce hit, reflect for next bounce.
                double h2 = r.Eps * 2.0;
                double n0b = MandelbulbDE(hpx + h2, hpy, hpz, p.Power, p.DEIter, p.Bailout)
                           - MandelbulbDE(hpx - h2, hpy, hpz, p.Power, p.DEIter, p.Bailout);
                double n1b = MandelbulbDE(hpx, hpy + h2, hpz, p.Power, p.DEIter, p.Bailout)
                           - MandelbulbDE(hpx, hpy - h2, hpz, p.Power, p.DEIter, p.Bailout);
                double n2b = MandelbulbDE(hpx, hpy, hpz + h2, p.Power, p.DEIter, p.Bailout)
                           - MandelbulbDE(hpx, hpy, hpz - h2, p.Power, p.DEIter, p.Bailout);
                double nlen = 1.0 / Math.Sqrt(n0b * n0b + n1b * n1b + n2b * n2b + 1e-20);
                double nbx2 = n0b * nlen, nby2 = n1b * nlen, nbz2 = n2b * nlen;
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

        // P7c.2 — single-scattering Beer–Lambert volumetric in-scatter. Active
        // when VolumeSteps>0, FogDensity>0, key light emits. Per-step SoftShadow
        // (calls this fractal's DE) gates god-rays. CloudSelfShadow + FBM
        // density modulator inherit the same field set as the CPU pipe.
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

        return GpuKernelUtils.PackBgra(br, bg, bb);
    }

    /// <summary>Per-fractal soft-shadow march. Mirrors
    /// <c>ShadingPipeline.SoftShadow&lt;TDe&gt;</c> with the Mandelbulb DE
    /// inlined — ILGPU kernels can't take struct-generic DE arguments at the
    /// LoadAutoGroupedStreamKernel level, so each fractal carries its own.</summary>
    private static double SoftShadow(
        double ox, double oy, double oz,
        double ldx, double ldy, double ldz,
        double tMin, double tMax, double k, int maxSteps,
        MandelbulbGpuParams p)
    {
        double res = 1.0, t = tMin;
        for (int s = 0; s < maxSteps; s++)
        {
            double px = ox + ldx * t;
            double py = oy + ldy * t;
            double pz = oz + ldz * t;
            double h = MandelbulbDE(px, py, pz, p.Power, p.DEIter, p.Bailout);
            if (h < 1e-4) return 0.0;
            if (k > 0) res = Math.Min(res, k * h / t);
            t += h;
            if (t >= tMax) break;
        }
        return GpuKernelUtils.Clamp(res, 0.0, 1.0);
    }

    /// <summary>Triplex Mandelbulb distance estimator. Mirrors the CPU
    /// MandelbulbCalculator.MandelbulbDE but no escape-iter out param (ILGPU
    /// inlining drops out-params). Spherical-coords power-N formula.</summary>
    private static double MandelbulbDE(double cx, double cy, double cz, double power, int iter, double bailout)
    {
        double zx = cx, zy = cy, zz = cz;
        double dr = 1.0;
        double r = 0.0;
        for (int i = 0; i < iter; i++)
        {
            r = Math.Sqrt(zx * zx + zy * zy + zz * zz);
            if (r > bailout) break;

            double theta = Math.Acos(zz / Math.Max(r, 1e-12));
            double phi = Math.Atan2(zy, zx);
            double rPow = Math.Pow(r, power);
            dr = Math.Pow(r, power - 1.0) * power * dr + 1.0;

            double newTheta = theta * power;
            double newPhi = phi * power;
            double sinT = Math.Sin(newTheta);
            zx = rPow * sinT * Math.Cos(newPhi) + cx;
            zy = rPow * sinT * Math.Sin(newPhi) + cy;
            zz = rPow * Math.Cos(newTheta) + cz;
        }
        return 0.5 * Math.Log(Math.Max(r, 1e-10)) * r / Math.Max(dr, 1e-10);
    }

    public void Dispose()
    {
        _kernel = null;
    }
}
