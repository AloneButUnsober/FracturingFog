// MandelbulbGpuCalculator.cs
//
// P7a — ILGPU-backed GPU raymarcher for the Mandelbulb (triplex power-N).
// Borrows the shared accelerator from GpuAcceleratorHost, the camera/ray
// struct from GpuRaymarchParams, and the ray construction / sphere clip /
// shading helpers from GpuKernelUtils.
//
// Shading is the cheap-palette path (hue from step-depth + tTotal, Lambert
// diffuse over normal·light, ambient floor). Full ShadingPipeline lift
// happens in P7c — at that point shadow/AO/reflection/volumetric all live
// on the device and we drop the cheap path entirely.
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
    private Action<Index1D, ArrayView<uint>, GpuRaymarchParams, MandelbulbGpuParams>? _kernel;
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
                Index1D, ArrayView<uint>, GpuRaymarchParams, MandelbulbGpuParams>(BulbKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Mandelbulb GPU kernel load failed: {ex.Message}";
            _initFailed = true;
            return false;
        }
    }

    /// <summary>Render one frame into <paramref name="outBuffer"/>. Returns
    /// false on init or kernel failure — caller falls back to CPU. The
    /// output buffer length must equal <c>r.Width * r.Height</c>.</summary>
    public bool Render(uint[] outBuffer, GpuRaymarchParams r, MandelbulbGpuParams p)
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
            LastError = $"Mandelbulb GPU render failed: {ex.Message}";
            return false;
        }
    }

    // ── Kernel ──────────────────────────────────────────────────────────────
    private static void BulbKernel(
        Index1D idx, ArrayView<uint> output, GpuRaymarchParams r, MandelbulbGpuParams p)
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
            double d = MandelbulbDE(px, py, pz, p.Power, p.DEIter, p.Bailout);
            if (d < r.Eps) { hit = true; hitStep = step; break; }
            if (tT > p.SceneRadius) break;
            px += rdx * d; py += rdy * d; pz += rdz * d;
            tT += d;
        }

        if (!hit) { output[idx] = r.InSetColor; return; }

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

        double shade = GpuKernelUtils.LambertShade(nx, ny, nz, r.LightX, r.LightY, r.LightZ, 0.15);
        output[idx] = GpuKernelUtils.CheapPalette(hitStep, r.MaxSteps, tT, shade);
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
