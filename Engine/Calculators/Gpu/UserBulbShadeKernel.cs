// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserBulbShadeKernel.cs
//
// #1173-A / GPU parity plan G2.4 — the User Bulb GPU kernel with the family
// kernels' shading: three lights (directional / point / spot / area), soft
// shadows, AO, GGX specular, SSS, triplanar, IBL / HDRI, caustics, reflections,
// volumetric fog, the colour-map albedo LUT, AOV views and the depth / normal /
// HDR G-buffers for the post stack. Before this, both User Bulb GPU paths shaded
// with one Lambert light over a 0.15 ambient and a sine rainbow.
//
// The body is the shared per-fractal kernel (as MandelboxGpuCalculator), adapted
// to the CPU UserBulb trace: cull-sphere clip, march to one unit past the sphere
// exit, clip plane, forward-difference normals, the StepDepth / Normal colour
// drivers. Only the DE differs between the two User Bulb GPU paths:
//
//   * This file compiles as-is with UserDE = the analytic triplex power-N DE — the
//     legacy path (UserBulbGpuCalculator).
//   * The sandbox path (UserBulbSandboxGpuCompiler) reads THIS FILE's text (it is
//     also an embedded resource), swaps the region between the USERDE markers for
//     the user's compiled Step + DE, renames the class and compiles it with
//     Roslyn. So the sandbox kernel can never drift from the legacy one.
//
// Keep the USERDE markers and the UserDE signature intact.

using System;

using ILGPU;
using ILGPU.Runtime;

using FracturingFog.Models;

namespace FracturingFog.Calculators.Gpu;

public static class UserBulbShadeKernel
{
    //@@USERDE-BEGIN
    /// <summary>The distance estimator. Built in: the analytic triplex power-N DE
    /// (the legacy GPU path's TriplexPowerDE). The sandbox path replaces this region.</summary>
    // Not inlined: the shading body calls the DE from ~16 sites (march, normals, shadows,
    // AO, reflections, volumetrics). Inlining a user DE into each — with software fp64
    // trig on CUDA — made the GT 710's JIT take ~48 s for z^8 + c; as a call it takes ~5 s.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static double UserDE(double cx, double cy, double cz, in GpuRenderParams q, ArrayView<double> __p)
    {
        double power = q.Power;
        double zx = 0, zy = 0, zz = 0;
        double dr = 1.0;
        double r = 0.0;
        for (int i = 0; i < q.DEIter; i++)
        {
            r = Math.Sqrt(zx * zx + zy * zy + zz * zz);
            if (r > q.Bailout) break;
            dr = power * Math.Pow(r, power - 1.0) * dr + 1.0;
            double theta = Math.Atan2(zy, zx) * power;
            double phi = Math.Asin(zz / Math.Max(r, 1e-12)) * power;
            double rn = Math.Pow(r, power);
            double cosp = Math.Cos(phi);
            zx = rn * cosp * Math.Cos(theta) + cx;
            zy = rn * cosp * Math.Sin(theta) + cy;
            zz = rn * Math.Sin(phi) + cz;
        }
        if (r < 1e-12 || dr < 1e-12) return 0.5 * r / Math.Max(dr, 1e-10);
        return 0.5 * Math.Log(Math.Max(r, 1.0)) * r / dr;
    }
    //@@USERDE-END

    /// <summary>UserBulbCalculator.SkyColor twin: the bg gradient for rays that miss
    /// the cull sphere (byte truncation as on the CPU).</summary>
    private static uint BulbBackground(double rdy, in GpuRenderParams q)
    {
        double t = rdy * 0.5 + 0.5;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        uint bot = q.BgBottom, top = q.BgTop;
        uint rb = (uint)(byte)((1 - t) * ((bot >> 16) & 0xFF) + t * ((top >> 16) & 0xFF));
        uint gb = (uint)(byte)((1 - t) * ((bot >> 8) & 0xFF) + t * ((top >> 8) & 0xFF));
        uint bb = (uint)(byte)((1 - t) * (bot & 0xFF) + t * (top & 0xFF));
        return 0xFF000000u | (rb << 16) | (gb << 8) | bb;
    }

    /// <summary>The kernel: one pixel per thread (tiled, #1170), family-kernel shading
    /// around <see cref="UserDE"/>. <paramref name="q"/> carries the DE inputs and the
    /// UserBulb-specific march settings; <paramref name="__p"/> the user parameters
    /// (+ t) the sandbox Step reads.</summary>
    public static void Kernel(
        Index1D tid, ArrayView<uint> output, GpuRaymarchParams r, GpuShadingParams sp, GpuRenderParams q, ArrayView<double> __p, ArrayView<uint> palette, ArrayView<float> depth, ArrayView<float> normals, ArrayView<float> hdr, ArrayView<uint> albedo, ArrayView<uint> hdri)
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
        // The CPU's cone-march tile hint for this pixel (march starts at 0.9 × it).
        double tHint = q.HintTileSize > 0
            ? __p[q.HintOffset + (y / q.HintTileSize) * q.HintTilesX + (x / q.HintTileSize)]
            : double.PositiveInfinity;

        // S3 (#567) — thin-lens DOF. Aperture 0 / one sample -> the single centre
        // ray (byte-identical). Otherwise average DofSamples taps whose origin is
        // jittered across the aperture disc, re-aimed through the focal point.
        int dofN = (r.DofSamples > 1 && r.DofAperture > 0.0) ? r.DofSamples : 1;
        if (dofN <= 1) { output[idx] = ShadeRay(r.CamX, r.CamY, r.CamZ, cdx, cdy, cdz, in r, in sp, in q, __p, palette, depth, dIdx, normals, hdr, gIdx, albedo, hdri, tHint); return; }
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
            uint c = ShadeRay(lox, loy, loz, ddx * il, ddy * il, ddz * il, in r, in sp, in q, __p, palette, depth, -1, normals, hdr, -1, albedo, hdri, tHint);
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
        in GpuRaymarchParams r, in GpuShadingParams sp, in GpuRenderParams q, ArrayView<double> __p, ArrayView<uint> palette,
        ArrayView<float> depth, int depthIdx, ArrayView<float> normals, ArrayView<float> hdr, int gIdx, ArrayView<uint> albedo, ArrayView<uint> hdri,
        double tHint)
    {
        // The CPU UserBulb trace: a ray that misses the cull sphere gets UserBulb's own
        // bg gradient (UserBulbCalculator.SkyColor — not MissColor; a CPU quirk this
        // twin keeps for parity, #1201); the march stops one unit past the sphere exit.
        var (sphereHit, tEn, tEx) = GpuKernelUtils.SphereClipFrom(rox, roy, roz, rdx, rdy, rdz, in r);
        if (!sphereHit) return BulbBackground(rdy, in q);

        // Start where the CPU starts: the sphere entry, or 0.9 × the tile's cone hint.
        double tStart = tEn;
        if (tHint < double.PositiveInfinity) tStart = Math.Max(tStart, tHint * 0.9);
        double px = rox + rdx * tStart;
        double py = roy + rdy * tStart;
        double pz = roz + rdz * tStart;
        double tT = tStart;
        bool hit = false;
        int hitStep = 0;
        double hitDist = 0.0;

        for (int step = 0; step < r.MaxSteps; step++)
        {
            double d = UserDE(px, py, pz, in q, __p);
            if (d < r.Eps)
            {
                // Clip plane: a surface point on the plane's positive side is skipped.
                if (q.ClipEnabled != 0 && (px * q.ClipNX + py * q.ClipNY + pz * q.ClipNZ - q.ClipD) > 0)
                {
                    double skip = Math.Max(r.Eps * 2, 0.01);
                    px += rdx * skip; py += rdy * skip; pz += rdz * skip;
                    tT += skip;
                    continue;
                }
                hit = true; hitStep = step; hitDist = d; break;
            }
            if (tT > tEx + 1.0) break;
            px += rdx * d; py += rdy * d; pz += rdz * d;
            tT += d;
        }

        if (!hit) return GpuKernelUtils.MissColor(hdri, rdx, rdy, rdz, in r, in sp);
        if (depthIdx >= 0) depth[depthIdx] = (float)tT;   // #1070 — ray distance to the hit

        // Forward-difference normal reusing the hit distance (the CPU UserBulb normal).
        double h = r.Eps * 2;
        double invH = 1.0 / h;
        double n0 = (UserDE(px + h, py, pz, in q, __p) - hitDist) * invH;
        double n1 = (UserDE(px, py + h, pz, in q, __p) - hitDist) * invH;
        double n2 = (UserDE(px, py, pz + h, in q, __p) - hitDist) * invH;
        double nl = Math.Sqrt(n0 * n0 + n1 * n1 + n2 * n2);
        double nx = 0, ny = 0, nz = 0;
        if (nl >= 1e-10) { nx = n0 / nl; ny = n1 / nl; nz = n2 / nl; }

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
                sh1 = SoftShadow(ox, oy, oz, spL.L1X, spL.L1Y, spL.L1Z, r.Eps, spL.ShadowTMax, spL.ShadowK1, spL.ShadowSteps, in q, __p);
            if ((spL.ShadowLightMask & 0x2) != 0 && spL.L2I > 0)
                sh2 = SoftShadow(ox, oy, oz, spL.L2X, spL.L2Y, spL.L2Z, r.Eps, spL.ShadowTMax, spL.ShadowK2, spL.ShadowSteps, in q, __p);
            if ((spL.ShadowLightMask & 0x4) != 0 && spL.L3I > 0)
                sh3 = SoftShadow(ox, oy, oz, spL.L3X, spL.L3Y, spL.L3Z, r.Eps, spL.ShadowTMax, spL.ShadowK3, spL.ShadowSteps, in q, __p);
        }

        double ao = 1.0;
        if (sp.AoSamples > 0)
        {
            double occl = 0.0, w = 0.0;
            for (int k = 1; k <= sp.AoSamples; k++)
            {
                double d = r.Eps * (double)(1L << k);
                double sd = UserDE(px + nx * d, py + ny * d, pz + nz * d, in q, __p);
                occl += Math.Max(0.0, d - sd) / d;
                w += 1.0;
            }
            ao = GpuKernelUtils.Clamp(1.0 - sp.AoStrength * (occl / Math.Max(w, 1.0)), 0.0, 1.0);
        }

        // Colour driver: StepDepth = the LUT's step / depth axis (256 / maxSteps, t·4);
        // Normal = smooth from n.x with (n.y, n.z) as the map's normal inputs.
        var (aR, aG, aB) = q.ColorDriverNormal != 0
            ? GpuKernelUtils.SurfaceAlbedoAt(albedo, in spL, (float)((nx + 1.0) * 128.0), ny, nz, hitStep, r.MaxSteps, tT)
            : GpuKernelUtils.SurfaceAlbedo(albedo, in spL, hitStep, r.MaxSteps, tT, nx, ny);
        // #323 — AOV view: return the diagnostic encoding instead of the beauty shade.
        if (spL.DebugAov != 0)
            return GpuKernelUtils.EncodeSurfaceAov(in spL, nx, ny, nz, rdx, rdy, rdz, px, py, pz,
                sh1, sh2, sh3, ao, aR, aG, aB, tT, hitStep);
        var (br, bg, bb) = GpuKernelUtils.ComposeSurfacePbr(
            hdri, in spL, nx, ny, nz, rdx, rdy, rdz, px, py, pz, sh1, sh2, sh3, ao, aR, aG, aB);

        // P7c.3/16b — N-bounce reflection (user DE).
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
                    double hR = UserDE(hpx, hpy, hpz, in q, __p);
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
                double n0b = UserDE(hpx + h2, hpy, hpz, in q, __p)
                           - UserDE(hpx - h2, hpy, hpz, in q, __p);
                double n1b = UserDE(hpx, hpy + h2, hpz, in q, __p)
                           - UserDE(hpx, hpy - h2, hpz, in q, __p);
                double n2b = UserDE(hpx, hpy, hpz + h2, in q, __p)
                           - UserDE(hpx, hpy, hpz - h2, in q, __p);
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

        // P7c.2 — single-scattering volumetric in-scatter (user DE).
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
                        r.Eps, spL.ShadowTMax, spL.ShadowK1, spL.ShadowSteps, in q, __p) : 1.0;
                    var (dR, dG, dB) = GpuKernelUtils.VolumeScatterLight(in spL,
                        sx, sy, sz, spL.L1X, spL.L1Y, spL.L1Z, rdx, rdy, rdz,
                        spL.L1R, spL.L1G, spL.L1B, spL.L1I, sh, T, density, stepSize);
                    inR += dR; inG += dG; inB += dB;
                }
                if (spL.L2I > 0)
                {
                    double sh = sh2On ? SoftShadow(sx, sy, sz, spL.L2X, spL.L2Y, spL.L2Z,
                        r.Eps, spL.ShadowTMax, spL.ShadowK2, spL.ShadowSteps, in q, __p) : 1.0;
                    var (dR, dG, dB) = GpuKernelUtils.VolumeScatterLight(in spL,
                        sx, sy, sz, spL.L2X, spL.L2Y, spL.L2Z, rdx, rdy, rdz,
                        spL.L2R, spL.L2G, spL.L2B, spL.L2I, sh, T, density, stepSize);
                    inR += dR; inG += dG; inB += dB;
                }
                if (spL.L3I > 0)
                {
                    double sh = sh3On ? SoftShadow(sx, sy, sz, spL.L3X, spL.L3Y, spL.L3Z,
                        r.Eps, spL.ShadowTMax, spL.ShadowK3, spL.ShadowSteps, in q, __p) : 1.0;
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
        in GpuRenderParams q, ArrayView<double> __p)
    {
        double res = 1.0, t = tMin;
        for (int s = 0; s < maxSteps; s++)
        {
            double px = ox + ldx * t;
            double py = oy + ldy * t;
            double pz = oz + ldz * t;
            double h = UserDE(px, py, pz, in q, __p);
            if (h < 1e-4) return 0.0;
            if (k > 0) res = Math.Min(res, k * h / t);
            t += h;
            if (t >= tMax) break;
        }
        return GpuKernelUtils.Clamp(res, 0.0, 1.0);
    }

    /// <summary>Mandelbox DE — box-fold (reflect across ±1), sphere-fold
    /// (scale by fixedR²/r² in band, by fixedR²/minR² inside minR), then
    /// z = scale·z + c, dr = |scale|·dr + 1. DE = |z| / |dr|.</summary>
}
