// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BlackHoleShadowGpuCalculator.cs (#1926, black hole shadows epic #1922)
//
// ILGPU trace for BlackHoleShadow (CUDA / OpenCL, the path the analytic 3D families and
// the Wada mirror spheres use; a D3D11 / Vulkan twin is a later follow-up). Kernel-side
// twin of ScatteringIntegrator.Trace over MpNullSystem with channels off, term for term:
// the same Dormand-Prince 5(4) stages and controller, the same Majumdar-Papapetrou field,
// camera launch, local step cap and exit tests. Per pixel it writes the fate (hole,
// escaped, undecided), the step count, the coordinate-time delay and the final momentum;
// BlackHoleShadowCalculator's colour pass turns those into colours exactly as on the CPU,
// so themes, skies and the Fate / Delay / Steps / Deflection channels need no GPU twin.
// Winding and closest approach need per-step bookkeeping and stay on the CPU.
//
// The trace runs in double, like the CPU: the fates are chaotic near the eyebrows, and
// the readback keeps the final momentum in double so the sky lookup matches the CPU too.

using System;
using System.Threading;

using FracturingFog.Scattering;
using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Kernel parameters for the black hole trace (#1926).</summary>
public struct BlackHoleGpuParams
{
    public int Width, Height;
    public double CenterX, CenterY, Pitch;
    public int HoleCount;
    public double X0, Y0, Z0, M0, C0;
    public double X1, Y1, Z1, M1, C1;
    public double X2, Y2, Z2, M2, C2;
    public double X3, Y3, Z3, M3, C3;
    public double Escape2;
    public double CamX, CamY, CamZ, Fx, Fy, Fz, Rx, Ry, Rz, Ux, Uy, Uz, Distance;
    public int Pinhole;
    public double RelTol, AbsTol, InitialStep;
    public int MaxSteps;
}

public sealed class BlackHoleShadowGpuCalculator
{
    /// <summary>Doubles per pixel in the readback: delay, final px, py, pz.</summary>
    public const int DataStride = 4;

    /// <summary>Single precision cannot resolve tighter steps: its relative tolerance is floored here.</summary>
    public const float SingleMinRelTol = 2e-5f;

    private Action<Index1D, GpuRaymarchParams, BlackHoleGpuParams, ArrayView<int>, ArrayView<double>>? _kernel;
    private Action<Index1D, GpuRaymarchParams, BlackHoleGpuParamsF, ArrayView<int>, ArrayView<float>>? _kernelF;
    private bool _initFailed;
    private static (Accelerator Device, string Message)? s_tooSlow;
    public string LastError { get; private set; } = string.Empty;

    /// <summary>The per-pixel outcomes of one GPU frame.</summary>
    public sealed class Outcomes
    {
        public readonly int[] Packed;
        public readonly double[] Data;
        public Outcomes(int[] packed, double[] data) { Packed = packed; Data = data; }
        /// <summary>Hole index, the escaped gate (hole count), or -1 undecided.</summary>
        public int Gate(int i) => (Packed[i] & 7) - 1;
        public int Steps(int i) => (int)((uint)Packed[i] >> 3);
        public double Delay(int i) => Data[i * DataStride];
        public double Px(int i) => Data[i * DataStride + 1];
        public double Py(int i) => Data[i * DataStride + 2];
        public double Pz(int i) => Data[i * DataStride + 3];
    }

    /// <summary>Kernel parameters for a frame of <paramref name="scene"/> at the given view.</summary>
    public static BlackHoleGpuParams ParamsFor(BlackHoleShadowCalculator.Scene scene, int width, int height,
                                               double centerX, double centerY, double zoom)
    {
        var h = scene.Holes;
        double cf = Math.Max(1e-6, scene.CaptureFraction);
        double Cap(int i) { double c = cf * Math.Max(0.0, h[i].M); return c * c; }
        var cam = scene.Camera;
        var p = new BlackHoleGpuParams
        {
            Width = width, Height = height, CenterX = centerX, CenterY = centerY,
            Pitch = BlackHoleShadowCalculator.PixelPitch(width, height, zoom),   // #1927: the camera's scale
            HoleCount = Math.Clamp(h.Length, 1, MpNullSystem.MaxHoles),
            Escape2 = scene.EscapeRadius * scene.EscapeRadius,
            CamX = cam.Cx, CamY = cam.Cy, CamZ = cam.Cz, Fx = cam.Fx, Fy = cam.Fy, Fz = cam.Fz,
            Rx = cam.Rx, Ry = cam.Ry, Rz = cam.Rz, Ux = cam.Ux, Uy = cam.Uy, Uz = cam.Uz,
            Distance = cam.Distance, Pinhole = cam.Pinhole ? 1 : 0,
            RelTol = scene.Settings.RelTol > 0 ? scene.Settings.RelTol : 1e-9,
            AbsTol = scene.Settings.AbsTol > 0 ? scene.Settings.AbsTol : 1e-12,
            InitialStep = scene.Settings.InitialStep > 0 ? scene.Settings.InitialStep : 0.1,
            MaxSteps = Math.Max(1, scene.Settings.MaxSteps),
        };
        if (h.Length > 0) { p.X0 = h[0].X; p.Y0 = h[0].Y; p.Z0 = h[0].Z; p.M0 = Math.Max(0.0, h[0].M); p.C0 = Cap(0); }
        if (h.Length > 1) { p.X1 = h[1].X; p.Y1 = h[1].Y; p.Z1 = h[1].Z; p.M1 = Math.Max(0.0, h[1].M); p.C1 = Cap(1); }
        if (h.Length > 2) { p.X2 = h[2].X; p.Y2 = h[2].Y; p.Z2 = h[2].Z; p.M2 = Math.Max(0.0, h[2].M); p.C2 = Cap(2); }
        if (h.Length > 3) { p.X3 = h[3].X; p.Y3 = h[3].Y; p.Z3 = h[3].Z; p.M3 = Math.Max(0.0, h[3].M); p.C3 = Cap(3); }
        return p;
    }

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
            _kernel = acc.LoadAutoGroupedStreamKernel<Index1D, GpuRaymarchParams, BlackHoleGpuParams, ArrayView<int>, ArrayView<double>>(TraceKernel);
            _kernelF = acc.LoadAutoGroupedStreamKernel<Index1D, GpuRaymarchParams, BlackHoleGpuParamsF, ArrayView<int>, ArrayView<float>>(
                BlackHoleShadowGpuKernelSingle.TraceKernelF);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"BlackHoleShadow GPU kernel load failed: {ex.GetBaseException().Message}";
            _initFailed = true;
            return false;
        }
    }

    /// <summary>Trace a frame on the GPU. Null when the GPU is unavailable, too slow, cancelled or
    /// faulted (see <see cref="LastError"/>); the caller then traces on the CPU.</summary>
    public Outcomes? Render(BlackHoleGpuParams p, CancellationToken ct = default, bool single = false)
    {
        if (!TryInit() || _kernel == null || _kernelF == null) return null;
        if (!GpuAcceleratorHost.TryAcquire(out var acc)) return null;
        if (s_tooSlow is { } slow && ReferenceEquals(slow.Device, acc)) { LastError = slow.Message; return null; }
        try
        {
            int total = p.Width * p.Height;
            using var devPacked = GpuMemoryStats.Allocate1D<int>(acc, total);
            var r = new GpuRaymarchParams { Width = p.Width, Height = p.Height };
            if (single)
            {
                using var devF = GpuMemoryStats.Allocate1D<float>(acc, (long)DataStride * total);
                var kf = _kernelF;
                var pf = BlackHoleShadowGpuKernelSingle.From(p);
                var runF = GpuTiledDispatch.Run(acc, r, (n, rt) => kf(n, rt, pf, devPacked.View, devF.View), ct);
                if (runF == GpuDispatchResult.TooSlow) { LastError = GpuTiledDispatch.TooSlowMessage("BlackHoleShadow"); s_tooSlow = (acc, LastError); return null; }
                if (runF != GpuDispatchResult.Completed) return null;
                var packedF = new int[total];
                var dataF = new float[DataStride * total];
                devPacked.CopyToCPU(packedF);
                devF.CopyToCPU(dataF);
                var widened = new double[dataF.Length];
                for (int i = 0; i < dataF.Length; i++) widened[i] = dataF[i];
                return new Outcomes(packedF, widened);
            }
            using var devData = GpuMemoryStats.Allocate1D<double>(acc, (long)DataStride * total);
            var kernel = _kernel;
            var run = GpuTiledDispatch.Run(acc, r, (n, rt) => kernel(n, rt, p, devPacked.View, devData.View), ct);
            if (run == GpuDispatchResult.TooSlow)
            {
                LastError = GpuTiledDispatch.TooSlowMessage("BlackHoleShadow");
                s_tooSlow = (acc, LastError);
                return null;
            }
            if (run != GpuDispatchResult.Completed) return null;
            var packed = new int[total];
            var data = new double[DataStride * total];
            devPacked.CopyToCPU(packed);
            devData.CopyToCPU(data);
            return new Outcomes(packed, data);
        }
        catch (Exception ex)
        {
            LastError = $"BlackHoleShadow GPU render failed: {ex.Message}";
            GpuAcceleratorHost.ReportRenderFault(acc, ex);
            return null;
        }
    }

    // ── Kernel ───────────────────────────────────────────────────────────────
    // Everything below mirrors MpNullSystem / ScatteringIntegrator / MpCamera term for term.

    private struct S6 { public double X, Y, Z, Px, Py, Pz; }

    private static void AddHole(double dx, double dy, double dz, double m,
                                ref double u, ref double gx, ref double gy, ref double gz)
    {
        if (m == 0.0) return;
        double r2 = dx * dx + dy * dy + dz * dz;
        double r = Math.Sqrt(r2);
        double ir = 1.0 / r;
        u += m * ir;
        double k = -m * ir * ir * ir;
        gx += k * dx; gy += k * dy; gz += k * dz;
    }

    private static double Potential(in BlackHoleGpuParams p, double x, double y, double z,
                                    out double gx, out double gy, out double gz)
    {
        double u = 1.0; gx = 0.0; gy = 0.0; gz = 0.0;
        AddHole(x - p.X0, y - p.Y0, z - p.Z0, p.M0, ref u, ref gx, ref gy, ref gz);
        if (p.HoleCount > 1) AddHole(x - p.X1, y - p.Y1, z - p.Z1, p.M1, ref u, ref gx, ref gy, ref gz);
        if (p.HoleCount > 2) AddHole(x - p.X2, y - p.Y2, z - p.Z2, p.M2, ref u, ref gx, ref gy, ref gz);
        if (p.HoleCount > 3) AddHole(x - p.X3, y - p.Y3, z - p.Z3, p.M3, ref u, ref gx, ref gy, ref gz);
        return u;
    }

    private static S6 Deriv(in BlackHoleGpuParams p, S6 s)
    {
        double u = Potential(p, s.X, s.Y, s.Z, out double gx, out double gy, out double gz);
        double iu2 = 1.0 / (u * u);
        double p2 = s.Px * s.Px + s.Py * s.Py + s.Pz * s.Pz;
        double f = u + p2 * iu2 / u;
        S6 d;
        d.X = s.Px * iu2; d.Y = s.Py * iu2; d.Z = s.Pz * iu2;
        d.Px = gx * f; d.Py = gy * f; d.Pz = gz * f;
        return d;
    }

    private static double Dist2(in BlackHoleGpuParams p, int i, double x, double y, double z)
    {
        double dx, dy, dz;
        if (i == 0) { dx = x - p.X0; dy = y - p.Y0; dz = z - p.Z0; }
        else if (i == 1) { dx = x - p.X1; dy = y - p.Y1; dz = z - p.Z1; }
        else if (i == 2) { dx = x - p.X2; dy = y - p.Y2; dz = z - p.Z2; }
        else { dx = x - p.X3; dy = y - p.Y3; dz = z - p.Z3; }
        return dx * dx + dy * dy + dz * dz;
    }

    private static double Cap2(in BlackHoleGpuParams p, int i)
        => i == 0 ? p.C0 : i == 1 ? p.C1 : i == 2 ? p.C2 : p.C3;

    private static double MaxStep(in BlackHoleGpuParams p, S6 s)
    {
        double near = double.PositiveInfinity;
        for (int i = 0; i < p.HoleCount; i++) near = Math.Min(near, Dist2(p, i, s.X, s.Y, s.Z));
        double r = Math.Sqrt(near);
        return Math.Max(1e-9, Math.Min(0.25 * r + 1e-6, 4.0 + 0.05 * r));
    }

    // 0 running, 1 captured (body), 2 escaped.
    private static int Classify(in BlackHoleGpuParams p, S6 s, out int body)
    {
        for (int i = 0; i < p.HoleCount; i++)
        {
            if (Dist2(p, i, s.X, s.Y, s.Z) < Cap2(p, i)) { body = i; return 1; }
        }
        body = -1;
        double r2 = s.X * s.X + s.Y * s.Y + s.Z * s.Z;
        if (r2 > p.Escape2 && s.X * s.Px + s.Y * s.Py + s.Z * s.Pz > 0.0) return 2;
        return 0;
    }

    private static S6 Stage(S6 s, double h, S6 a, double ca, S6 b, double cb, S6 c, double cc, S6 d, double cd, S6 e, double ce)
    {
        S6 r;
        r.X = s.X + h * (ca * a.X + cb * b.X + cc * c.X + cd * d.X + ce * e.X);
        r.Y = s.Y + h * (ca * a.Y + cb * b.Y + cc * c.Y + cd * d.Y + ce * e.Y);
        r.Z = s.Z + h * (ca * a.Z + cb * b.Z + cc * c.Z + cd * d.Z + ce * e.Z);
        r.Px = s.Px + h * (ca * a.Px + cb * b.Px + cc * c.Px + cd * d.Px + ce * e.Px);
        r.Py = s.Py + h * (ca * a.Py + cb * b.Py + cc * c.Py + cd * d.Py + ce * e.Py);
        r.Pz = s.Pz + h * (ca * a.Pz + cb * b.Pz + cc * c.Pz + cd * d.Pz + ce * e.Pz);
        return r;
    }

    // One component's error ratio (ScatteringIntegrator's loop body).
    private static double Ratio(double h, double k1, double k3, double k4, double k5, double k6, double k7,
                                double y0, double y1, double rtol, double atol)
    {
        double e = h * (ScatteringIntegrator.E1 * k1 + ScatteringIntegrator.E3 * k3 + ScatteringIntegrator.E4 * k4
                      + ScatteringIntegrator.E5 * k5 + ScatteringIntegrator.E6 * k6 + ScatteringIntegrator.E7 * k7);
        double sc = atol + rtol * Math.Max(Math.Abs(y0), Math.Abs(y1));
        return Math.Abs(e) / sc;
    }

    private static bool Finite(double v) => v - v == 0.0;

    private static void TraceKernel(Index1D tid, GpuRaymarchParams r, BlackHoleGpuParams p,
                                    ArrayView<int> packed, ArrayView<double> data)
    {
        int idx = GpuKernelUtils.TilePixel(tid, in r);
        int px = idx % r.Width;
        int py = idx / r.Width;
        if (py >= r.Height) return;

        // BlackHoleShadowCalculator.PlaneAt(x + 0.5, y + 0.5, ...): v is the negated plane y.
        double u = p.CenterX + (px + 0.5 - p.Width * 0.5) * p.Pitch;
        double v = -(p.CenterY + (py + 0.5 - p.Height * 0.5) * p.Pitch);

        // MpCamera.Origin / Direction / Launch.
        double ox, oy, oz, dx, dy, dz;
        if (p.Pinhole != 0)
        {
            ox = p.CamX; oy = p.CamY; oz = p.CamZ;
            dx = p.Distance * p.Fx + u * p.Rx + v * p.Ux;
            dy = p.Distance * p.Fy + u * p.Ry + v * p.Uy;
            dz = p.Distance * p.Fz + u * p.Rz + v * p.Uz;
            double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            dx /= l; dy /= l; dz /= l;
        }
        else
        {
            ox = p.CamX + u * p.Rx + v * p.Ux; oy = p.CamY + u * p.Ry + v * p.Uy; oz = p.CamZ + u * p.Rz + v * p.Uz;
            dx = p.Fx; dy = p.Fy; dz = p.Fz;
        }
        double uu = Potential(p, ox, oy, oz, out _, out _, out _);
        double pm = uu * uu;
        S6 s;
        s.X = ox; s.Y = oy; s.Z = oz; s.Px = pm * dx; s.Py = pm * dy; s.Pz = pm * dz;

        // ScatteringIntegrator.Trace.
        const double A21 = 1.0 / 5.0;
        const double A31 = 3.0 / 40.0, A32 = 9.0 / 40.0;
        const double A41 = 44.0 / 45.0, A42 = -56.0 / 15.0, A43 = 32.0 / 9.0;
        const double A51 = 19372.0 / 6561.0, A52 = -25360.0 / 2187.0, A53 = 64448.0 / 6561.0, A54 = -212.0 / 729.0;
        const double A61 = 9017.0 / 3168.0, A62 = -355.0 / 33.0, A63 = 46732.0 / 5247.0, A64 = 49.0 / 176.0, A65 = -5103.0 / 18656.0;
        const double B1 = 35.0 / 384.0, B3 = 500.0 / 1113.0, B4 = 125.0 / 192.0, B5 = -2187.0 / 6784.0, B6 = 11.0 / 84.0;

        int maxSteps = p.MaxSteps;
        double h = Math.Min(p.InitialStep, MaxStep(p, s));
        double lambda = 0.0, delay = 0.0;
        int steps = 0, attempts = 0, attemptCap = maxSteps * 8;
        int fate = 3, body = -1;   // 3 = undecided
        S6 k1 = Deriv(p, s);
        S6 zero = default;
        while (steps < maxSteps && attempts < attemptCap)
        {
            attempts++;
            double hMax = MaxStep(p, s);
            if (h > hMax) h = hMax;
            if (!(h > 0) || !Finite(h)) break;

            S6 k2 = Deriv(p, Stage(s, h, k1, A21, zero, 0, zero, 0, zero, 0, zero, 0));
            S6 k3 = Deriv(p, Stage(s, h, k1, A31, k2, A32, zero, 0, zero, 0, zero, 0));
            S6 k4 = Deriv(p, Stage(s, h, k1, A41, k2, A42, k3, A43, zero, 0, zero, 0));
            S6 k5 = Deriv(p, Stage(s, h, k1, A51, k2, A52, k3, A53, k4, A54, zero, 0));
            S6 k6 = Deriv(p, Stage(s, h, k1, A61, k2, A62, k3, A63, k4, A64, k5, A65));
            S6 yn = Stage(s, h, k1, B1, k3, B3, k4, B4, k5, B5, k6, B6);
            S6 k7 = Deriv(p, yn);

            double rt = p.RelTol, at = p.AbsTol;
            double e0 = Ratio(h, k1.X, k3.X, k4.X, k5.X, k6.X, k7.X, s.X, yn.X, rt, at);
            double e1 = Ratio(h, k1.Y, k3.Y, k4.Y, k5.Y, k6.Y, k7.Y, s.Y, yn.Y, rt, at);
            double e2 = Ratio(h, k1.Z, k3.Z, k4.Z, k5.Z, k6.Z, k7.Z, s.Z, yn.Z, rt, at);
            double e3 = Ratio(h, k1.Px, k3.Px, k4.Px, k5.Px, k6.Px, k7.Px, s.Px, yn.Px, rt, at);
            double e4 = Ratio(h, k1.Py, k3.Py, k4.Py, k5.Py, k6.Py, k7.Py, s.Py, yn.Py, rt, at);
            double e5 = Ratio(h, k1.Pz, k3.Pz, k4.Pz, k5.Pz, k6.Pz, k7.Pz, s.Pz, yn.Pz, rt, at);
            bool finite = Finite(e0) && Finite(e1) && Finite(e2) && Finite(e3) && Finite(e4) && Finite(e5);
            double err = Math.Max(Math.Max(Math.Max(e0, e1), Math.Max(e2, e3)), Math.Max(e4, e5));

            if (finite && err <= 1.0)
            {
                lambda += h;
                steps++;
                s = yn;
                k1 = k7;
                double uo = Potential(p, s.X, s.Y, s.Z, out _, out _, out _);
                delay += (uo * uo - 1.0) * h;
                int c = Classify(p, s, out int b);
                if (c != 0) { fate = c; body = b; break; }
                double fac = err == 0.0 ? 5.0 : Math.Min(5.0, Math.Max(0.2, 0.9 * Math.Pow(err, -0.2)));
                h *= fac;
            }
            else
            {
                double fac = finite ? Math.Min(1.0, Math.Max(0.2, 0.9 * Math.Pow(err, -0.2))) : 0.2;
                h *= fac;
                if (h < 1e-14 * Math.Max(1.0, lambda)) break;
            }
        }

        int gate = fate == 1 ? body : fate == 2 ? p.HoleCount : -1;
        packed[idx] = (gate + 1) | (steps << 3);
        int o = idx * DataStride;
        data[o] = delay;
        data[o + 1] = s.Px;
        data[o + 2] = s.Py;
        data[o + 3] = s.Pz;
    }
}
