// GpuKernelUtils.cs
//
// P7 infra — kernel-callable static helpers shared across per-fractal ILGPU
// kernels under Engine/Calculators/Gpu/. Pulled from UserBulbGpuCalculator's
// kernel inner loops so the second, third, ... per-fractal kernel can reuse
// the same ray construction + sphere clip + cheap-palette code paths.
//
// Constraints: every method here must be ILGPU-kernel-callable. That means:
//   * No managed references (no string, no class instances, no delegate).
//   * No Math.Pow with non-const exponent on the GPU path (CUDA backend
//     refuses to JIT). Use Math.Sqrt + manual multiplies, or the dedicated
//     pow approximations.
//   * No 'out' parameters (ILGPU's IR-level inlining loses track). Use
//     return tuples or pack into the value tuple positions.
//   * No exception throw.
//
// Pattern: methods take primitives or the shared GpuRaymarchParams struct.
// They never touch ArrayView<T> — that stays in the per-fractal kernel so
// each kernel controls its own output layout (uint color, optional depth /
// normal G-buffer once 12b lands).

using System;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Kernel-side helpers shared by every per-fractal GPU calculator.
/// All methods are ILGPU-kernel-compatible — see file comment for the rules
/// they obey.</summary>
internal static class GpuKernelUtils
{
    /// <summary>Construct the primary ray direction for pixel <c>(x, y)</c>
    /// from the camera basis baked into <paramref name="p"/>. Returns the
    /// unit direction as (rdx, rdy, rdz). Honors <see
    /// cref="GpuRaymarchParams.PanU"/> / <see cref="GpuRaymarchParams.PanV"/>.
    /// Mirrors the CPU calculator's per-pixel ray construction so GPU and
    /// CPU paths produce visually matched rays.</summary>
    public static (double rdx, double rdy, double rdz) BuildPrimaryRay(
        int x, int y, in GpuRaymarchParams p)
    {
        double u = (2.0 * (x + 0.5) / p.Width - 1.0) * p.FovScale * p.Aspect + p.PanU;
        double v = (1.0 - 2.0 * (y + 0.5) / p.Height) * p.FovScale + p.PanV;
        double rdx = p.RightX * u + p.UpX * v + p.FwdX;
        double rdy = p.RightY * u + p.UpY * v + p.FwdY;
        double rdz = p.RightZ * u + p.UpZ * v + p.FwdZ;
        double rl = 1.0 / Math.Sqrt(rdx * rdx + rdy * rdy + rdz * rdz);
        return (rdx * rl, rdy * rl, rdz * rl);
    }

    /// <summary>Sphere-clip the primary ray against the cull radius in
    /// <paramref name="p"/>. Returns <c>hit = false</c> when the ray misses
    /// the bounding sphere (caller writes <see cref="GpuRaymarchParams.InSetColor"/>);
    /// otherwise returns the entry / exit ray-t values so the per-fractal
    /// sphere-trace loop starts at <c>tEn</c> and bails past <c>tEx</c>.
    /// When <see cref="GpuRaymarchParams.CullRadiusSq"/> is zero the clip is
    /// disabled — returns <c>(true, 0, double.MaxValue)</c>.</summary>
    public static (bool hit, double tEn, double tEx) SphereClip(
        double rdx, double rdy, double rdz, in GpuRaymarchParams p)
    {
        if (p.CullRadiusSq <= 0.0) return (true, 0.0, double.MaxValue);
        double ocx = p.CamX - p.TargetX;
        double ocy = p.CamY - p.TargetY;
        double ocz = p.CamZ - p.TargetZ;
        double bS = ocx * rdx + ocy * rdy + ocz * rdz;
        double cS = ocx * ocx + ocy * ocy + ocz * ocz - p.CullRadiusSq;
        double disc = bS * bS - cS;
        if (disc < 0) return (false, 0.0, 0.0);
        double sq = Math.Sqrt(disc);
        double tEx = -bS + sq;
        if (tEx < 0) return (false, 0.0, 0.0);
        double tEn = Math.Max(0.0, -bS - sq);
        return (true, tEn, tEx);
    }

    /// <summary>Hash a hit into a deterministic ARGB color using the
    /// cheap-palette pattern from <c>UserBulbGpuCalculator</c>: shade by
    /// step-depth + total ray length, hue from a phase-shifted sine
    /// cascade. Acceptable until the full <c>ShadingPipeline</c> ports to
    /// GPU (deferred sub-phase). Lambert diffuse already factored into
    /// <paramref name="shade"/> by the caller (gives caller control over
    /// ambient floor / hemisphere modulation).</summary>
    public static uint CheapPalette(int hitStep, int maxSteps, double tTotal, double shade)
    {
        double t = hitStep / (double)maxSteps + tTotal * 0.05;
        t -= Math.Floor(t);
        uint r = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(t * 6.283)));
        uint g = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(t * 6.283 + 2.094)));
        uint b = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(t * 6.283 + 4.188)));
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    /// <summary>Lambert diffuse with ambient floor for the cheap-shading
    /// path. <paramref name="ambient"/> is the floor (0.15 matches the CPU
    /// pipeline's default ambient term); diffuse is scaled into the
    /// remaining 1 - ambient range.</summary>
    public static double LambertShade(
        double nx, double ny, double nz,
        double lx, double ly, double lz,
        double ambient)
    {
        double diffuse = Math.Max(0.0, nx * lx + ny * ly + nz * lz);
        return ambient + diffuse * (1.0 - ambient);
    }
}
