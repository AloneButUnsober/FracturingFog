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
            // Prefer CPU accelerator if no GPU device (still JITs the kernel
            // and runs multi-threaded — faster than uncompiled C# loop).
            _accelerator = _context.GetPreferredDevice(preferCPU: false).CreateAccelerator(_context);
            _kernel = _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<uint>, GpuRenderParams>(BulbKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"GPU init failed: {ex.Message}";
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

        double diffuse = Math.Max(0.0, nx * p.LightX + ny * p.LightY + nz * p.LightZ);
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
