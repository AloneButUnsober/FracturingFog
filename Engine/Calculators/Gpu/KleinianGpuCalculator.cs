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
    private Action<Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, KleinianGpuParams>? _kernel;
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
                Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, KleinianGpuParams>(KleinianKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Kleinian GPU kernel load failed: {ex.Message}";
            _initFailed = true;
            return false;
        }
    }

    public bool Render(uint[] outBuffer, GpuRaymarchParams r, GpuShadingParams sp, KleinianGpuParams p)
    {
        if (!TryInit() || _kernel == null) return false;
        if (!GpuAcceleratorHost.TryAcquire(out var acc)) return false;
        try
        {
            int total = r.Width * r.Height;
            using var dev = acc.Allocate1D<uint>(total);
            _kernel(total, dev.View, r, sp, p);
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
        Index1D idx, ArrayView<uint> output, GpuRaymarchParams r, GpuShadingParams sp, KleinianGpuParams p)
    {
        int x = idx % r.Width;
        int y = idx / r.Width;
        if (y >= r.Height) return;

        var (rdx, rdy, rdz) = GpuKernelUtils.BuildPrimaryRay(x, y, in r);
        var (sphereHit, tEn, _) = GpuKernelUtils.SphereClip(rdx, rdy, rdz, in r);
        if (!sphereHit) { output[idx] = r.InSetColor; return; }

        double px = r.CamX + rdx * tEn;
        double py = r.CamY + rdy * tEn;
        double pz = r.CamZ + rdz * tEn;
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

        if (!hit) { output[idx] = r.InSetColor; return; }

        double h = r.Eps * 2;
        double n0 = KleinianDE(px + h, py, pz, in p) - KleinianDE(px - h, py, pz, in p);
        double n1 = KleinianDE(px, py + h, pz, in p) - KleinianDE(px, py - h, pz, in p);
        double n2 = KleinianDE(px, py, pz + h, in p) - KleinianDE(px, py, pz - h, in p);
        double nl = 1.0 / Math.Sqrt(n0 * n0 + n1 * n1 + n2 * n2 + 1e-20);
        double nx = n0 * nl, ny = n1 * nl, nz = n2 * nl;

        double bias = r.Eps * 4.0;
        double ox = px + nx * bias;
        double oy = py + ny * bias;
        double oz = pz + nz * bias;
        double sh1 = 1.0, sh2 = 1.0, sh3 = 1.0;
        if (sp.ShadowSteps > 0)
        {
            if ((sp.ShadowLightMask & 0x1) != 0 && sp.L1I > 0)
                sh1 = SoftShadow(ox, oy, oz, sp.L1X, sp.L1Y, sp.L1Z, r.Eps, sp.ShadowTMax, sp.ShadowSoftK, sp.ShadowSteps, in p);
            if ((sp.ShadowLightMask & 0x2) != 0 && sp.L2I > 0)
                sh2 = SoftShadow(ox, oy, oz, sp.L2X, sp.L2Y, sp.L2Z, r.Eps, sp.ShadowTMax, sp.ShadowSoftK, sp.ShadowSteps, in p);
            if ((sp.ShadowLightMask & 0x4) != 0 && sp.L3I > 0)
                sh3 = SoftShadow(ox, oy, oz, sp.L3X, sp.L3Y, sp.L3Z, r.Eps, sp.ShadowTMax, sp.ShadowSoftK, sp.ShadowSteps, in p);
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
            in sp, nx, ny, nz, rdx, rdy, rdz, px, py, pz, sh1, sh2, sh3, ao, aR, aG, aB);

        // P7c.3 — one-bounce reflection (Kleinian DE — DE takes 'in p').
        if (sp.ReflectStrength > 0)
        {
            var (rrx, rry, rrz) = GpuKernelUtils.Reflect3D(rdx, rdy, rdz, nx, ny, nz);
            double rOx = px + nx * bias;
            double rOy = py + ny * bias;
            double rOz = pz + nz * bias;
            int rSteps = sp.ReflectSteps > 0 ? sp.ReflectSteps : 24;
            double rMax = sp.ReflectMaxDist > 0 ? sp.ReflectMaxDist : 12.0;
            double tR = r.Eps;
            bool hitR = false;
            double hitTR = 0.0;
            for (int s = 0; s < rSteps; s++)
            {
                double prx = rOx + rrx * tR;
                double pry = rOy + rry * tR;
                double prz = rOz + rrz * tR;
                double hR = KleinianDE(prx, pry, prz, in p);
                if (hR < r.Eps * 2.0) { hitR = true; hitTR = tR; break; }
                tR += hR;
                if (tR > rMax) break;
            }
            var (rcR, rcG, rcB) = GpuKernelUtils.ReflectShade(hitR, hitTR, rry, in sp);
            double w = GpuKernelUtils.FresnelMix(nx, ny, nz, rdx, rdy, rdz, sp.Metallic, sp.ReflectStrength);
            br += rcR * w;
            bg += rcG * w;
            bb += rcB * w;
        }

        // P7c.2 — single-scattering volumetric in-scatter (Kleinian DE).
        if (sp.VolumeSteps > 0 && sp.FogDensity > 0 && sp.L1I > 0)
        {
            double camX = px - rdx * tT;
            double camY = py - rdy * tT;
            double camZ = pz - rdz * tT;
            int vs = sp.VolumeSteps;
            if (sp.VolumeStepsFalloff > 0 && tT > 4.0)
                vs = Math.Max(4, (int)(vs / (1.0 + (tT - 4.0) * sp.VolumeStepsFalloff)));
            double stepSize = tT / vs;
            bool shadowOn = sp.ShadowSteps > 0 && (sp.ShadowLightMask & 0x1) != 0;
            double T = 1.0, inR = 0, inG = 0, inB = 0;
            for (int s = 0; s < vs; s++)
            {
                double t = (s + 0.5) * stepSize;
                double sx = camX + rdx * t;
                double sy = camY + rdy * t;
                double sz = camZ + rdz * t;
                double density = sp.FogDensity;
                if (sp.FogHeightFalloff > 0)
                    density *= Math.Exp(-sp.FogHeightFalloff * sy);
                density *= GpuKernelUtils.VolumetricDensityMul(sx, sy, sz, in sp);
                double sh = 1.0;
                if (shadowOn)
                    sh = SoftShadow(sx, sy, sz, sp.L1X, sp.L1Y, sp.L1Z,
                        r.Eps, sp.ShadowTMax, sp.ShadowSoftK, sp.ShadowSteps, in p);
                sh *= GpuKernelUtils.CloudSelfShadow(sx, sy, sz, sp.L1X, sp.L1Y, sp.L1Z, in sp);
                double scatter = density * sh * sp.L1I * stepSize;
                inR += T * scatter * sp.L1R;
                inG += T * scatter * sp.L1G;
                inB += T * scatter * sp.L1B;
                double aT = density * stepSize;
                T *= aT < 1.0 ? GpuKernelUtils.ExpNegSmall(aT) : Math.Exp(-aT);
            }
            br = br * T + inR;
            bg = bg * T + inG;
            bb = bb * T + inB;
        }
        else
        {
            (br, bg, bb) = GpuKernelUtils.ApplyScalarFog(in sp, br, bg, bb, rdy, tT);
        }

        output[idx] = GpuKernelUtils.PackBgra(br, bg, bb);
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
