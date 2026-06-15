// MandelboxGpuCalculator.cs
//
// P7a — ILGPU-backed GPU raymarcher for the Mandelbox. Box-fold + sphere-fold
// + scale iteration, dr tracked as scalar. DE = |z| / |dr|. Same shading and
// lifecycle contract as MandelbulbGpuCalculator — see that file for details.

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
    private Action<Index1D, ArrayView<uint>, GpuRaymarchParams, MandelboxGpuParams>? _kernel;
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
                Index1D, ArrayView<uint>, GpuRaymarchParams, MandelboxGpuParams>(BoxKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Mandelbox GPU kernel load failed: {ex.Message}";
            _initFailed = true;
            return false;
        }
    }

    public bool Render(uint[] outBuffer, GpuRaymarchParams r, MandelboxGpuParams p)
    {
        if (!TryInit() || _kernel == null) return false;
        if (!GpuAcceleratorHost.TryAcquire(out var acc)) return false;
        try
        {
            int total = r.Width * r.Height;
            using var dev = acc.Allocate1D<uint>(total);
            _kernel(total, dev.View, r, p);
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
        Index1D idx, ArrayView<uint> output, GpuRaymarchParams r, MandelboxGpuParams p)
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
            double d = MandelboxDE(px, py, pz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
            if (d < r.Eps) { hit = true; hitStep = step; break; }
            if (tT > p.SceneRadius) break;
            px += rdx * d; py += rdy * d; pz += rdz * d;
            tT += d;
        }

        if (!hit) { output[idx] = r.InSetColor; return; }

        double h = r.Eps * 2;
        double n0 = MandelboxDE(px + h, py, pz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter)
                  - MandelboxDE(px - h, py, pz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
        double n1 = MandelboxDE(px, py + h, pz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter)
                  - MandelboxDE(px, py - h, pz, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
        double n2 = MandelboxDE(px, py, pz + h, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter)
                  - MandelboxDE(px, py, pz - h, p.Scale, p.FixedR2, p.MinR2, p.Bailout2, p.DEIter);
        double nl = 1.0 / Math.Sqrt(n0 * n0 + n1 * n1 + n2 * n2 + 1e-20);
        double nx = n0 * nl, ny = n1 * nl, nz = n2 * nl;

        double shade = GpuKernelUtils.LambertShade(nx, ny, nz, r.LightX, r.LightY, r.LightZ, 0.15);
        output[idx] = GpuKernelUtils.CheapPalette(hitStep, r.MaxSteps, tT, shade);
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
