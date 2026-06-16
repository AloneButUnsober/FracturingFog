// QMandelGpuCalculator.cs
//
// P7b — ILGPU-backed GPU raymarcher for the Quaternion Mandelbrot set.
// Iteration q := q² + c with q starting at the origin and c varying per
// pixel — pixel (x, y, z) ↦ c = (x, y, z, sliceW). Chain-rule derivative
// dq/dc starts at 0; per iter dq := 2·q·dq + 1 (identity quaternion).
// Hubbard–Douady DE = 0.5·|q|·ln|q|/|dq|.
//
// Component packing matches the CPU calculator: (X, Y, Z, W), X plays the
// "real" slot for q·dq. SliceZ field reserved for future alternate slice
// planes — the standard 3D-slice convention routes raymarched z directly
// into c.Z (matches QuatMandelbrotCalculator's `_ = sliceZ;`).

using System;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Per-fractal kernel parameters for the Quaternion Mandelbrot.
/// SliceW is the fixed 4th coord that turns 4D ℍ-set membership into 3D.
/// SliceZ reserved — see file comment.</summary>
public struct QMandelGpuParams
{
    public double SliceZ;
    public double SliceW;
    public double Bailout2;
    public int DEIter;
    public double SceneRadius;
}

public sealed class QMandelGpuCalculator : IDisposable
{
    private Action<Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, QMandelGpuParams>? _kernel;
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
                Index1D, ArrayView<uint>, GpuRaymarchParams, GpuShadingParams, QMandelGpuParams>(QMandelKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"QMandel GPU kernel load failed: {ex.Message}";
            _initFailed = true;
            return false;
        }
    }

    public bool Render(uint[] outBuffer, GpuRaymarchParams r, GpuShadingParams sp, QMandelGpuParams p)
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
            LastError = $"QMandel GPU render failed: {ex.Message}";
            return false;
        }
    }

    private static void QMandelKernel(
        Index1D idx, ArrayView<uint> output, GpuRaymarchParams r, GpuShadingParams sp, QMandelGpuParams p)
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
            double d = QMandelDE(px, py, pz, p.SliceW, p.Bailout2, p.DEIter);
            if (d < r.Eps) { hit = true; hitStep = step; break; }
            if (tT > p.SceneRadius) break;
            px += rdx * d; py += rdy * d; pz += rdz * d;
            tT += d;
        }

        if (!hit) { output[idx] = r.InSetColor; return; }

        double h = r.Eps * 2;
        double n0 = QMandelDE(px + h, py, pz, p.SliceW, p.Bailout2, p.DEIter)
                  - QMandelDE(px - h, py, pz, p.SliceW, p.Bailout2, p.DEIter);
        double n1 = QMandelDE(px, py + h, pz, p.SliceW, p.Bailout2, p.DEIter)
                  - QMandelDE(px, py - h, pz, p.SliceW, p.Bailout2, p.DEIter);
        double n2 = QMandelDE(px, py, pz + h, p.SliceW, p.Bailout2, p.DEIter)
                  - QMandelDE(px, py, pz - h, p.SliceW, p.Bailout2, p.DEIter);
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
                sh1 = SoftShadow(ox, oy, oz, sp.L1X, sp.L1Y, sp.L1Z, r.Eps, sp.ShadowTMax, sp.ShadowSoftK, sp.ShadowSteps, p);
            if ((sp.ShadowLightMask & 0x2) != 0 && sp.L2I > 0)
                sh2 = SoftShadow(ox, oy, oz, sp.L2X, sp.L2Y, sp.L2Z, r.Eps, sp.ShadowTMax, sp.ShadowSoftK, sp.ShadowSteps, p);
            if ((sp.ShadowLightMask & 0x4) != 0 && sp.L3I > 0)
                sh3 = SoftShadow(ox, oy, oz, sp.L3X, sp.L3Y, sp.L3Z, r.Eps, sp.ShadowTMax, sp.ShadowSoftK, sp.ShadowSteps, p);
        }

        double ao = 1.0;
        if (sp.AoSamples > 0)
        {
            double occl = 0.0, w = 0.0;
            for (int k = 1; k <= sp.AoSamples; k++)
            {
                double d = r.Eps * (double)(1L << k);
                double sd = QMandelDE(px + nx * d, py + ny * d, pz + nz * d, p.SliceW, p.Bailout2, p.DEIter);
                occl += Math.Max(0.0, d - sd) / d;
                w += 1.0;
            }
            ao = Math.Clamp(1.0 - sp.AoStrength * (occl / Math.Max(w, 1.0)), 0.0, 1.0);
        }

        var (aR, aG, aB) = GpuKernelUtils.CheapAlbedo(hitStep, r.MaxSteps, tT);
        var (br, bg, bb) = GpuKernelUtils.ComposeSurfacePbr(
            in sp, nx, ny, nz, rdx, rdy, rdz, px, py, pz, sh1, sh2, sh3, ao, aR, aG, aB);

        // P7c.3 — one-bounce reflection (QMandel DE).
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
                double hR = QMandelDE(prx, pry, prz, p.SliceW, p.Bailout2, p.DEIter);
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

        // P7c.2 — single-scattering volumetric in-scatter (QMandel DE).
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
                        r.Eps, sp.ShadowTMax, sp.ShadowSoftK, sp.ShadowSteps, p);
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
        QMandelGpuParams p)
    {
        double res = 1.0, t = tMin;
        for (int s = 0; s < maxSteps; s++)
        {
            double px = ox + ldx * t;
            double py = oy + ldy * t;
            double pz = oz + ldz * t;
            double h = QMandelDE(px, py, pz, p.SliceW, p.Bailout2, p.DEIter);
            if (h < 1e-4) return 0.0;
            if (k > 0) res = Math.Min(res, k * h / t);
            t += h;
            if (t >= tMax) break;
        }
        return Math.Clamp(res, 0.0, 1.0);
    }

    /// <summary>Hubbard–Douady DE for the quaternion Mandelbrot squaring map.
    /// q starts at origin; dq/dc starts at zero; per iter dq := 2·q·dq + 1.
    /// Components packed (X, Y, Z, W) — X plays the "real" slot. Mirrors the
    /// CPU QuatMandelbrotCalculator.QuatMandelDE (with sliceZ omitted since
    /// the CPU path already discards it via `_ = sliceZ;`).</summary>
    private static double QMandelDE(
        double sx, double sy, double sz, double sliceW,
        double bailout2, int iter)
    {
        double cx = sx, cy = sy, cz = sz, cw = sliceW;
        double qx = 0.0, qy = 0.0, qz = 0.0, qw = 0.0;
        double dx = 0.0, dy = 0.0, dz = 0.0, dw = 0.0;

        for (int i = 0; i < iter; i++)
        {
            // dq := 2·q·dq + 1.
            double ndx = qx * dx - qy * dy - qz * dz - qw * dw;
            double ndy = qx * dy + qy * dx + qz * dw - qw * dz;
            double ndz = qx * dz - qy * dw + qz * dx + qw * dy;
            double ndw = qx * dw + qy * dz - qz * dy + qw * dx;
            dx = 2.0 * ndx + 1.0;
            dy = 2.0 * ndy;
            dz = 2.0 * ndz;
            dw = 2.0 * ndw;

            // q := q² + c.
            double nqx = qx * qx - qy * qy - qz * qz - qw * qw;
            double nqy = 2.0 * qx * qy;
            double nqz = 2.0 * qx * qz;
            double nqw = 2.0 * qx * qw;
            qx = nqx + cx;
            qy = nqy + cy;
            qz = nqz + cz;
            qw = nqw + cw;

            double r2 = qx * qx + qy * qy + qz * qz + qw * qw;
            if (r2 > bailout2) break;
        }

        double q2 = qx * qx + qy * qy + qz * qz + qw * qw;
        double d2 = dx * dx + dy * dy + dz * dz + dw * dw;
        if (d2 < 1e-30) return 0.0;
        if (q2 < 1.0) return 0.0;
        double qMag = Math.Sqrt(q2);
        double dMag = Math.Sqrt(d2);
        return 0.5 * qMag * Math.Log(qMag) / dMag;
    }

    public void Dispose() => _kernel = null;
}
