// MengerGpuCalculator.cs
//
// P7a — ILGPU-backed GPU raymarcher for the Menger-sponge KIFS (Knighty's
// formulation). Mirrors KifsCalculator's MengerDE — octant fold, sort-3,
// scale·z − (scale−1)·offset, corner-mirror on z.
//
// Sierpinski fold is *not* covered in this kernel — KifsCalculator routes
// only KifsFold == Menger to GPU. Sierpinski stays on CPU until P7b adds a
// SierpinskiGpuCalculator (or this kernel branches on a fold-kind field;
// branchy GPU code is generally avoided so two kernels is cleaner).
//
// Scale-power normalisation (Math.Pow(scale, -iter) on the DE return) uses
// the same Pow call as the CPU path — ILGPU lifts it to its math intrinsic.

using System;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Per-fractal kernel parameters for the Menger-fold KIFS.</summary>
public struct MengerGpuParams
{
    public double Scale;
    public double OffsetX, OffsetY, OffsetZ;
    public int DEIter;
    public double SceneRadius;
}

public sealed class MengerGpuCalculator : IDisposable
{
    private Action<Index1D, ArrayView<uint>, GpuRaymarchParams, MengerGpuParams>? _kernel;
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
                Index1D, ArrayView<uint>, GpuRaymarchParams, MengerGpuParams>(MengerKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Menger GPU kernel load failed: {ex.Message}";
            _initFailed = true;
            return false;
        }
    }

    public bool Render(uint[] outBuffer, GpuRaymarchParams r, MengerGpuParams p)
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
            LastError = $"Menger GPU render failed: {ex.Message}";
            return false;
        }
    }

    private static void MengerKernel(
        Index1D idx, ArrayView<uint> output, GpuRaymarchParams r, MengerGpuParams p)
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
            double d = MengerDE(px, py, pz, p.Scale, p.OffsetX, p.OffsetY, p.OffsetZ, p.DEIter);
            if (d < r.Eps) { hit = true; hitStep = step; break; }
            if (tT > p.SceneRadius) break;
            px += rdx * d; py += rdy * d; pz += rdz * d;
            tT += d;
        }

        if (!hit) { output[idx] = r.InSetColor; return; }

        double h = r.Eps * 2;
        double n0 = MengerDE(px + h, py, pz, p.Scale, p.OffsetX, p.OffsetY, p.OffsetZ, p.DEIter)
                  - MengerDE(px - h, py, pz, p.Scale, p.OffsetX, p.OffsetY, p.OffsetZ, p.DEIter);
        double n1 = MengerDE(px, py + h, pz, p.Scale, p.OffsetX, p.OffsetY, p.OffsetZ, p.DEIter)
                  - MengerDE(px, py - h, pz, p.Scale, p.OffsetX, p.OffsetY, p.OffsetZ, p.DEIter);
        double n2 = MengerDE(px, py, pz + h, p.Scale, p.OffsetX, p.OffsetY, p.OffsetZ, p.DEIter)
                  - MengerDE(px, py, pz - h, p.Scale, p.OffsetX, p.OffsetY, p.OffsetZ, p.DEIter);
        double nl = 1.0 / Math.Sqrt(n0 * n0 + n1 * n1 + n2 * n2 + 1e-20);
        double nx = n0 * nl, ny = n1 * nl, nz = n2 * nl;

        double shade = GpuKernelUtils.LambertShade(nx, ny, nz, r.LightX, r.LightY, r.LightZ, 0.15);
        output[idx] = GpuKernelUtils.CheapPalette(hitStep, r.MaxSteps, tT, shade);
    }

    private static double MengerDE(double cx, double cy, double cz,
        double scale, double ox, double oy, double oz, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double k = scale - 1.0;
        double offX = k * ox;
        double offY = k * oy;
        double offZ = k * oz;
        double mirrorThresh = -0.5 * offZ;
        for (int i = 0; i < iter; i++)
        {
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

    public void Dispose() => _kernel = null;
}
