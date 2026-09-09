// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserBulbGpuCalculator.cs
//
// ILGPU-backed GPU raymarcher for User Bulb 3D. Drives the same per-pixel
// camera/raymarch loop as UserBulbCalculator but runs across CUDA/OpenCL/CPU
// JIT compute accelerators via ILGPU.
//
// Current kernel support: square triplex (z*z + c) and power-N triplex
// (Vec3.Pow(z, N) + c) — the cases UserBulbAnalyticDE detects. Arbitrary
// user source is NOT compiled to GPU IL here; the calling code falls back
// to CPU UserBulbCalculator for unsupported sources.
//
// Lifecycle: UserBulbCalculator owns one instance, lazily creates the
// Accelerator on first GPU render, disposes on calculator dispose.

using System;

using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;

namespace FracturingFog.Calculators;

public struct GpuRenderParams
{
    public int Width, Height;
    public double CamX, CamY, CamZ;
    public double TargetX, TargetY, TargetZ;
    public double FwdX, FwdY, FwdZ;
    public double RightX, RightY, RightZ;
    public double UpX, UpY, UpZ;
    public double FovScale, Aspect;
    public double LightX, LightY, LightZ;
    public int DEIter, MaxSteps;
    public double Eps, Bailout, CullRadiusSq;
    public double Power;          // 2 = square triplex; else generic power-N
    public double QuatSliceW;     // Quat axis-mode slice plane (z.W when projecting 4D→3D)
    public uint InSetColor;

    // Wave 4.6 — Julia + numerical-Jacobian fields (Sandbox quat-mode GPU
    // dispatch). Default zero keeps legacy single-step + chain analytic-power
    // paths bit-identical (UseAnalyticDE=1 from the caller routes through
    // the analytic branch; legacy callers that don't set the field get the
    // analytic branch via default-zero check on the new shape — see
    // BuildKernelSource).
    public int JuliaMode;                       // 0 = escape-time, 1 = Julia (c constant from JuliaC*)
    public double JuliaCW, JuliaCX, JuliaCY, JuliaCZ;
    public double JacH;                         // Jacobian forward-diff step (numerical DE only)
    public int UseAnalyticDE;                   // 1 = power-DE; 0 = 5-trajectory numerical Jacobian

    // S8 (#484/#488) — primary-light positional resolve. The UserBulb GPU shade
    // is single-light cheap Lambert; when Light1 is a point/spot light this
    // carries its world position + range + spot cone cosines so the kernel can
    // resolve a surface-relative direction + attenuation (twin of LightSampler).
    // L1Type 0 = Directional → LightX/Y/Z is used unchanged, atten 1 →
    // byte-identical with the pre-S8 GPU path. When non-zero, LightX/Y/Z carries
    // the light's shine direction (the spot cone axis).
    public int L1Type;                          // 0 Directional, 1 Point, 2 Spot
    public double L1PX, L1PY, L1PZ;             // light world position (point/spot)
    public double L1Range;                      // Karis range window; ≤0 = pure 1/d²
    public double L1InnerCos, L1OuterCos;       // precomputed spot cone half-angle cosines
}

public sealed class UserBulbGpuCalculator : IDisposable
{
    private Context? _context;
    private Accelerator? _accelerator;
    private Action<Index1D, ArrayView<uint>, GpuRenderParams>? _kernel;
    private bool _initFailed;
    public string LastError { get; private set; } = string.Empty;

    public bool TryInit()
    {
        if (_kernel != null) return true;
        if (_initFailed) return false;
        try
        {
            _context = Context.Create(b => b.Default());
            // Pick a Float64-capable device: the kernel is all-double, so an
            // fp64-less OpenCL iGPU throws at JIT (#749). Prefer a real GPU with
            // fp64, else the CPU accelerator (always fp64 — still JITs the kernel
            // and runs multi-threaded, faster than UserBulb's uncompiled C# loop,
            // so CPU is an accepted fallback here unlike the 3D-fractal families).
            var dev = Gpu.GpuAcceleratorHost.SelectFloat64Device(_context, allowCpu: true)
                      ?? throw new NotSupportedException("no Float64-capable ILGPU device");
            _accelerator = dev.CreateAccelerator(_context);
            _kernel = _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<uint>, GpuRenderParams>(BulbKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"GPU init failed: {ex.GetBaseException().Message}";
            _initFailed = true;
            return false;
        }
    }

    public bool Render(uint[] outBuffer, GpuRenderParams p)
    {
        if (!TryInit() || _accelerator == null || _kernel == null) return false;
        try
        {
            int total = p.Width * p.Height;
            using var dev = _accelerator.Allocate1D<uint>(total);
            _kernel(total, dev.View, p);
            _accelerator.Synchronize();
            dev.CopyToCPU(outBuffer);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"GPU render failed: {ex.Message}";
            return false;
        }
    }

    // ── Kernel ──────────────────────────────────────────────────────────────
    private static void BulbKernel(Index1D idx, ArrayView<uint> output, GpuRenderParams p)
    {
        int x = idx % p.Width;
        int y = idx / p.Width;
        if (y >= p.Height) return;

        double u = (2.0 * (x + 0.5) / p.Width - 1.0) * p.FovScale * p.Aspect;
        double v = (1.0 - 2.0 * (y + 0.5) / p.Height) * p.FovScale;
        double rdx = p.RightX * u + p.UpX * v + p.FwdX;
        double rdy = p.RightY * u + p.UpY * v + p.FwdY;
        double rdz = p.RightZ * u + p.UpZ * v + p.FwdZ;
        double rl = 1.0 / Math.Sqrt(rdx * rdx + rdy * rdy + rdz * rdz);
        rdx *= rl; rdy *= rl; rdz *= rl;

        // Sphere clip
        double ocx = p.CamX - p.TargetX;
        double ocy = p.CamY - p.TargetY;
        double ocz = p.CamZ - p.TargetZ;
        double bS = ocx * rdx + ocy * rdy + ocz * rdz;
        double cS = ocx * ocx + ocy * ocy + ocz * ocz - p.CullRadiusSq;
        double disc = bS * bS - cS;
        if (disc < 0) { output[idx] = p.InSetColor; return; }
        double sq = Math.Sqrt(disc);
        double tEx = -bS + sq;
        if (tEx < 0) { output[idx] = p.InSetColor; return; }
        double tEn = Math.Max(0.0, -bS - sq);

        double px = p.CamX + rdx * tEn;
        double py = p.CamY + rdy * tEn;
        double pz = p.CamZ + rdz * tEn;
        double tT = tEn;
        bool hit = false;
        int hitStep = 0;
        double hitDist = 0.0;

        for (int step = 0; step < p.MaxSteps; step++)
        {
            double d = TriplexPowerDE(px, py, pz, p.DEIter, p.Bailout, p.Power);
            if (d < p.Eps) { hit = true; hitStep = step; hitDist = d; break; }
            if (tT > tEx + 1.0) break;
            px += rdx * d; py += rdy * d; pz += rdz * d;
            tT += d;
        }

        if (!hit) { output[idx] = p.InSetColor; return; }

        // Forward-diff normals.
        double h = p.Eps * 2;
        double invH = 1.0 / h;
        double n0 = (TriplexPowerDE(px + h, py, pz, p.DEIter, p.Bailout, p.Power) - hitDist) * invH;
        double n1 = (TriplexPowerDE(px, py + h, pz, p.DEIter, p.Bailout, p.Power) - hitDist) * invH;
        double n2 = (TriplexPowerDE(px, py, pz + h, p.DEIter, p.Bailout, p.Power) - hitDist) * invH;
        double nl = 1.0 / Math.Sqrt(n0 * n0 + n1 * n1 + n2 * n2 + 1e-20);
        double nx = n0 * nl, ny = n1 * nl, nz = n2 * nl;

        // S8 (#484/#488) — resolve a point/spot Light1 at the surface point
        // (inline twin of LightSampler.Sample; inlined rather than calling the
        // internal GpuKernelUtils so the identical code also compiles in the
        // sandbox-emitted kernel). L1Type 0 → the baked directional dir, atten 1
        // → byte-identical.
        double llx = p.LightX, lly = p.LightY, llz = p.LightZ, latten = 1.0;
        if (p.L1Type != 0)
        {
            double ldx = p.L1PX - px, ldy = p.L1PY - py, ldz = p.L1PZ - pz;
            double ld2 = ldx * ldx + ldy * ldy + ldz * ldz;
            double ld = Math.Sqrt(ld2);
            double linv = ld > 1e-12 ? 1.0 / ld : 0.0;
            llx = ldx * linv; lly = ldy * linv; llz = ldz * linv;
            latten = 1.0 / Math.Max(ld2, 1e-6);
            if (p.L1Range > 0.0)
            {
                double lt = ld / p.L1Range;
                double lt4 = lt * lt * lt * lt;
                double lwin = lt4 < 1.0 ? 1.0 - lt4 : 0.0;
                latten *= lwin * lwin;
            }
            if (p.L1Type == 2)
            {
                double lcos = llx * p.LightX + lly * p.LightY + llz * p.LightZ;
                double ldenom = p.L1InnerCos - p.L1OuterCos;
                double lcone;
                if (ldenom <= 1e-9) lcone = lcos >= p.L1InnerCos ? 1.0 : 0.0;
                else { double ltc = (lcos - p.L1OuterCos) / ldenom; if (ltc < 0.0) ltc = 0.0; else if (ltc > 1.0) ltc = 1.0; lcone = ltc * ltc * (3.0 - 2.0 * ltc); }
                latten *= lcone;
            }
        }
        double diffuse = Math.Max(0.0, nx * llx + ny * lly + nz * llz) * latten;
        double ambient = 0.15;
        double shade = ambient + diffuse * (1.0 - ambient);

        // Cheap palette: shade hue by step-depth + normal — no IColorMap
        // delegation on GPU. Acceptable trade until color drivers (3.7) port.
        double t = hitStep / (double)p.MaxSteps + tT * 0.05;
        t -= Math.Floor(t);
        uint r = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(t * 6.283)));
        uint g = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(t * 6.283 + 2.094)));
        uint b = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(t * 6.283 + 4.188)));
        output[idx] = 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    /// <summary>Hubbard-Douady DE for triplex power-N Mandelbulb. Branches on
    /// Power==2 to use the fast square form (no Pow call).</summary>
    private static double TriplexPowerDE(double cx, double cy, double cz, int iter, double bailout, double power)
    {
        double zx = 0, zy = 0, zz = 0;
        double dr = 1.0;
        double r = 0.0;
        for (int i = 0; i < iter; i++)
        {
            r = Math.Sqrt(zx * zx + zy * zy + zz * zz);
            if (r > bailout) break;
            dr = power * Math.Pow(r, power - 1.0) * dr + 1.0;

            // Triplex pow: r=|z|, theta=atan2(zy, zx), phi=asin(zz/r)
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

    public void Dispose()
    {
        _accelerator?.Dispose();
        _context?.Dispose();
        _accelerator = null;
        _context = null;
        _kernel = null;
    }
}
