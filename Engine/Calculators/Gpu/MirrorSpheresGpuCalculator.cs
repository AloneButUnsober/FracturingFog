// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MirrorSpheresGpuCalculator.cs (#1443, epic #1439)
//
// ILGPU trace for the Wada mirror spheres (CUDA / OpenCL, the path every 3D family
// uses). Kernel-side twin of MirrorSpheresScene.Trace: per pixel, the camera ray
// reflects off the four tangent balls (closed-form ray-sphere hits) until it leaves
// the inner chamber, which fixes the pixel's opening. In photo mode it keeps
// tracing past the exit until nothing is ahead (the "free" continuation the env
// openings and outer reflections need). That continuation is exactly the CPU's
// separate stopAtChamberExit:false trace, because the chamber test never changes
// the ray state.
//
// The kernel only traces. It writes each pixel's outcome (opening, bounces, kept
// light, path length, exit direction), and MirrorSpheresCalculator's colour pass
// turns those into colours exactly as on the CPU. So billiard themes, ColorGen
// programs and the photo palette stay CPU-side and need no GPU twin.
//
// The trace runs in double, as the CPU does (the basins are chaotic: fp32 would move
// boundary pixels). The readback is small: one packed int (gates, entered flag,
// bounces) plus three floats per pixel, and four more floats in photo mode. The
// colour pass consumes these in float anyway (Shade, the theme's pathLength, the
// float depth buffer, the sky lookup).

using System;
using System.Threading;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Kernel parameters for the mirror-spheres trace (#1443).</summary>
public struct MirrorSpheresGpuParams
{
    public double Radius;
    public int MaxBounces;
    /// <summary>Normal-incidence reflectance F0.</summary>
    public double Reflectance;
    /// <summary>1 = weight each bounce by Schlick's Fresnel term, 0 = by F0.</summary>
    public int Fresnel;
    /// <summary>1 = also trace the free continuation (photo mode).</summary>
    public int Free;
}

public sealed class MirrorSpheresGpuCalculator
{
    // Per-pixel outcome layout. Packed int: bits 0-2 exit gate + 1, bit 3 entered,
    // bits 4-6 free gate + 1, bits 7-19 exit bounces (MaxBounces <= 4096).
    public const int ExitStride = 3;     // floats: first hit, exit path, exit light
    public const int FreeStride = 4;     // floats: free light, free direction xyz

    private Action<Index1D, GpuRaymarchParams, MirrorSpheresGpuParams, ArrayView<int>, ArrayView<float>, ArrayView<float>>? _kernel;
    private bool _initFailed;
    private static (Accelerator Device, string Message)? s_tooSlow;
    public string LastError { get; private set; } = string.Empty;

    /// <summary>The per-pixel outcomes of one GPU frame.</summary>
    public sealed class Outcomes
    {
        public readonly int[] Packed;
        public readonly float[] ExitData;
        public readonly float[]? FreeData;
        public Outcomes(int[] packed, float[] exit, float[]? free) { Packed = packed; ExitData = exit; FreeData = free; }

        /// <summary>The chamber-exit outcome (what <c>Trace</c> returns by default).</summary>
        public MirrorSpheresOutcome Exit(int i)
        {
            int w = Packed[i], d = i * ExitStride;
            return new MirrorSpheresOutcome
            {
                Gate = (w & 7) - 1, EnteredChamber = (w & 8) != 0, Bounces = (w >> 7) & 0x1FFF,
                FirstHit = ExitData[d], PathLength = ExitData[d + 1], Throughput = ExitData[d + 2],
            };
        }

        /// <summary>The free continuation's gate, kept light and final direction (what the
        /// photo colour reads from <c>Trace(..., stopAtChamberExit: false)</c>); only filled
        /// when the frame was traced with <see cref="MirrorSpheresGpuParams.Free"/>.</summary>
        public MirrorSpheresOutcome Free(int i)
        {
            int w = Packed[i], d = i * FreeStride;
            var f = FreeData!;
            return new MirrorSpheresOutcome
            {
                Gate = ((w >> 4) & 7) - 1,
                Throughput = f[d], EndDx = f[d + 1], EndDy = f[d + 2], EndDz = f[d + 3],
            };
        }
    }

    public static MirrorSpheresGpuParams ParamsFor(MirrorSpheresScene scene, bool free) => new()
    {
        Radius = scene.Radius,
        MaxBounces = scene.MaxBounces,
        Reflectance = scene.Reflectance,
        Fresnel = scene.Fresnel ? 1 : 0,
        Free = free ? 1 : 0,
    };

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
                Index1D, GpuRaymarchParams, MirrorSpheresGpuParams, ArrayView<int>, ArrayView<float>, ArrayView<float>>(TraceKernel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"MirrorSpheres GPU kernel load failed: {ex.GetBaseException().Message}";
            _initFailed = true;
            return false;
        }
    }

    /// <summary>Trace a frame on the GPU. Null when the GPU is unavailable, too slow,
    /// cancelled or faulted (see <see cref="LastError"/>); the caller then traces on the CPU.</summary>
    public Outcomes? Render(GpuRaymarchParams r, MirrorSpheresGpuParams p, CancellationToken ct = default)
    {
        if (!TryInit() || _kernel == null) return null;
        if (!GpuAcceleratorHost.TryAcquire(out var acc)) return null;
        if (s_tooSlow is { } slow && ReferenceEquals(slow.Device, acc)) { LastError = slow.Message; return null; }
        try
        {
            int total = r.Width * r.Height;
            bool free = p.Free != 0;
            using var devPacked = GpuMemoryStats.Allocate1D<int>(acc, total);
            using var devExit = GpuMemoryStats.Allocate1D<float>(acc, (long)ExitStride * total);
            // Free data only in photo mode; a length-1 dummy keeps the kernel arity fixed.
            using var devFree = GpuMemoryStats.Allocate1D<float>(acc, free ? (long)FreeStride * total : 1);
            var kernel = _kernel;
            var run = GpuTiledDispatch.Run(acc, r, (n, rt) => kernel(n, rt, p, devPacked.View, devExit.View, devFree.View), ct);
            if (run == GpuDispatchResult.TooSlow)
            {
                LastError = GpuTiledDispatch.TooSlowMessage("MirrorSpheres");
                s_tooSlow = (acc, LastError);
                return null;
            }
            if (run != GpuDispatchResult.Completed) return null;
            var packed = new int[total];
            var exit = new float[ExitStride * total];
            devPacked.CopyToCPU(packed);
            devExit.CopyToCPU(exit);
            float[]? freeData = null;
            if (free)
            {
                freeData = new float[FreeStride * total];
                devFree.CopyToCPU(freeData);
            }
            return new Outcomes(packed, exit, freeData);
        }
        catch (Exception ex)
        {
            LastError = $"MirrorSpheres GPU render failed: {ex.Message}";
            GpuAcceleratorHost.ReportRenderFault(acc, ex);
            return null;
        }
    }

    // Even-parity cube corners: k0 (1,1,1), k1 (1,-1,-1), k2 (-1,1,-1), k3 (-1,-1,1).
    private static double Cx(int k) => k < 2 ? 1.0 : -1.0;
    private static double Cy(int k) => (k == 0 || k == 2) ? 1.0 : -1.0;
    private static double Cz(int k) => (k == 0 || k == 3) ? 1.0 : -1.0;

    private static int Opening(double dx, double dy, double dz)
    {
        int best = 0;
        double bestDot = double.NegativeInfinity;
        for (int k = 0; k < 4; k++)
        {
            double d = -(dx * Cx(k) + dy * Cy(k) + dz * Cz(k));
            if (d > bestDot) { bestDot = d; best = k; }
        }
        return best;
    }

    private static void TraceKernel(Index1D tid, GpuRaymarchParams r, MirrorSpheresGpuParams p,
                                    ArrayView<int> packed, ArrayView<float> exitData, ArrayView<float> freeData)
    {
        int idx = GpuKernelUtils.TilePixel(tid, in r);
        int x = idx % r.Width;
        int y = idx / r.Width;
        if (y >= r.Height) return;

        // MirrorSpheresCamera.Ray, term for term.
        double u = (2.0 * (x + 0.5) / r.Width - 1.0) * r.FovScale * r.Aspect + r.PanU;
        double v = (1.0 - 2.0 * (y + 0.5) / r.Height) * r.FovScale + r.PanV;
        double dx = r.RightX * u + r.UpX * v + r.FwdX;
        double dy = r.RightY * u + r.UpY * v + r.FwdY;
        double dz = r.RightZ * u + r.UpZ * v + r.FwdZ;
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        dx /= len; dy /= len; dz /= len;

        // MirrorSpheresScene.Trace, term for term.
        double r2 = p.Radius * p.Radius;
        const double eps = 1e-9;
        double faceOffset = 1.0 / Math.Sqrt(3.0);
        double invSqrt3 = 1.0 / Math.Sqrt(3.0);
        double px = r.CamX, py = r.CamY, pz = r.CamZ;
        double first = double.PositiveInfinity, path = 0.0, light = 1.0;
        int bounces = 0, last = -1;

        bool exited = false;
        int exitGate = -1, exitBounces = 0, entered = 0;
        double exitPath = 0.0, exitLight = 1.0;
        int freeGate = -1;

        while (bounces < p.MaxBounces)
        {
            double tHit = double.PositiveInfinity;
            int hit = -1;
            for (int k = 0; k < 4; k++)
            {
                if (k == last) continue;
                double qx = px - Cx(k), qy = py - Cy(k), qz = pz - Cz(k);
                double b = qx * dx + qy * dy + qz * dz;
                double c = qx * qx + qy * qy + qz * qz - r2;
                double disc = b * b - c;
                if (disc < 0.0) continue;
                double t = -b - Math.Sqrt(disc);
                if (t > eps && t < tHit) { tHit = t; hit = k; }
            }

            if (!exited)
            {
                // ChamberInterval: the part of this segment inside the tetrahedron.
                double tIn = double.NegativeInfinity, tOut = double.PositiveInfinity;
                int exitFace = -1;
                for (int k = 0; k < 4; k++)
                {
                    double nx = -Cx(k) * invSqrt3, ny = -Cy(k) * invSqrt3, nz = -Cz(k) * invSqrt3;
                    double num = faceOffset - (nx * px + ny * py + nz * pz);
                    double den = nx * dx + ny * dy + nz * dz;
                    if (den > 0.0)
                    {
                        double t = num / den;
                        if (t < tOut) { tOut = t; exitFace = k; }
                    }
                    else if (den < 0.0)
                    {
                        double t = num / den;
                        if (t > tIn) tIn = t;
                    }
                    else if (num < 0.0)
                    {
                        tIn = double.PositiveInfinity;
                    }
                }
                if (tIn < tOut && tOut > 0.0 && tIn < tHit && tOut <= tHit)
                {
                    exited = true;
                    exitGate = exitFace; exitBounces = bounces; entered = 1;
                    exitPath = path + tOut; exitLight = light;
                    if (p.Free == 0) break;
                }
            }

            if (hit < 0)
            {
                freeGate = Opening(dx, dy, dz);
                if (!exited)
                {
                    exited = true;
                    exitGate = freeGate; exitBounces = bounces; entered = 0;
                    exitPath = path; exitLight = light;
                }
                break;
            }

            px += tHit * dx; py += tHit * dy; pz += tHit * dz;
            path += tHit;
            if (bounces == 0) first = tHit;
            double hx = (px - Cx(hit)) / p.Radius, hy = (py - Cy(hit)) / p.Radius, hz = (pz - Cz(hit)) / p.Radius;
            double dot = dx * hx + dy * hy + dz * hz;
            if (p.Fresnel != 0)
            {
                double cs = -dot;
                if (cs < 0.0) cs = 0.0; else if (cs > 1.0) cs = 1.0;
                double m = 1.0 - cs;
                double m2 = m * m;
                light *= p.Reflectance + (1.0 - p.Reflectance) * m2 * m2 * m;
            }
            else
            {
                light *= p.Reflectance;
            }
            dx -= 2.0 * dot * hx; dy -= 2.0 * dot * hy; dz -= 2.0 * dot * hz;
            last = hit;
            bounces++;
        }

        if (!exited)
        {
            // Trapped at the cap without leaving the chamber.
            exitGate = -1; exitBounces = bounces; entered = 1; exitPath = path; exitLight = light;
        }
        // freeGate stays -1 when the bounce cap ended the free trace (trapped).

        packed[idx] = (exitGate + 1) | (entered << 3) | ((freeGate + 1) << 4) | (exitBounces << 7);
        int e = idx * ExitStride;
        exitData[e] = (float)first;
        exitData[e + 1] = (float)exitPath;
        exitData[e + 2] = (float)exitLight;
        if (p.Free != 0)
        {
            int f = idx * FreeStride;
            freeData[f] = (float)light;
            freeData[f + 1] = (float)dx;
            freeData[f + 2] = (float)dy;
            freeData[f + 3] = (float)dz;
        }
    }
}
