// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BlackHoleShadowGpuKernelSingle.cs (#1926) — GENERATED from the double kernel in
// BlackHoleShadowGpuCalculator.cs (double -> float, Math -> MathF, literals suffixed f) by
// the S4 generator; keep the two in step. Single precision is the opt-in fast path for GPUs
// with slow fp64 (nearly every consumer card: GT 710 at 1/24, many at 1/32-1/64): on the
// GT 710 the double kernel ran 55x slower than the 12-thread CPU. The integrator's
// tolerance is floored at <see cref="BlackHoleShadowGpuCalculator.SingleMinRelTol"/>,
// since float cannot resolve tighter steps, so boundary pixels can move against the
// double CPU trace (measured and reported by the parity tests).

using System;

using FracturingFog.Scattering;
using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Single-precision twin of <see cref="BlackHoleGpuParams"/>.</summary>
public struct BlackHoleGpuParamsF
{
    public int Width, Height;
    /// <summary>Kept in double: the view centre needs more digits than float has once zoomed.</summary>
    public double CenterX, CenterY, Pitch;
    public int HoleCount;
    public float X0, Y0, Z0, M0, C0;
    public float X1, Y1, Z1, M1, C1;
    public float X2, Y2, Z2, M2, C2;
    public float X3, Y3, Z3, M3, C3;
    public float Escape2;
    public float CamX, CamY, CamZ, Fx, Fy, Fz, Rx, Ry, Rz, Ux, Uy, Uz, Distance;
    public int Pinhole;
    public float RelTol, AbsTol, InitialStep;
    public int MaxSteps;
}

public static class BlackHoleShadowGpuKernelSingle
{
    /// <summary>Narrow the double parameters (tolerance floored for float).</summary>
    public static BlackHoleGpuParamsF From(BlackHoleGpuParams p)
    {
        var f = new BlackHoleGpuParamsF
        {
            Width = p.Width,
            Height = p.Height,
            CenterX = p.CenterX,
            CenterY = p.CenterY,
            Pitch = p.Pitch,
            HoleCount = p.HoleCount,
            X0 = (float)p.X0,
            Y0 = (float)p.Y0,
            Z0 = (float)p.Z0,
            M0 = (float)p.M0,
            C0 = (float)p.C0,
            X1 = (float)p.X1,
            Y1 = (float)p.Y1,
            Z1 = (float)p.Z1,
            M1 = (float)p.M1,
            C1 = (float)p.C1,
            X2 = (float)p.X2,
            Y2 = (float)p.Y2,
            Z2 = (float)p.Z2,
            M2 = (float)p.M2,
            C2 = (float)p.C2,
            X3 = (float)p.X3,
            Y3 = (float)p.Y3,
            Z3 = (float)p.Z3,
            M3 = (float)p.M3,
            C3 = (float)p.C3,
            Escape2 = (float)p.Escape2,
            CamX = (float)p.CamX,
            CamY = (float)p.CamY,
            CamZ = (float)p.CamZ,
            Fx = (float)p.Fx,
            Fy = (float)p.Fy,
            Fz = (float)p.Fz,
            Rx = (float)p.Rx,
            Ry = (float)p.Ry,
            Rz = (float)p.Rz,
            Ux = (float)p.Ux,
            Uy = (float)p.Uy,
            Uz = (float)p.Uz,
            Distance = (float)p.Distance,
            Pinhole = p.Pinhole,
            RelTol = (float)p.RelTol,
            AbsTol = (float)p.AbsTol,
            InitialStep = (float)p.InitialStep,
            MaxSteps = p.MaxSteps,
        };
        f.RelTol = MathF.Max(f.RelTol, BlackHoleShadowGpuCalculator.SingleMinRelTol);
        f.AbsTol = f.RelTol * 1e-3f;
        return f;
    }

    private struct S6F { public float X, Y, Z, Px, Py, Pz; }

    private static void AddHole(float dx, float dy, float dz, float m,
                                ref float u, ref float gx, ref float gy, ref float gz)
    {
        if (m == 0.0f) return;
        float r2 = dx * dx + dy * dy + dz * dz;
        float r = MathF.Sqrt(r2);
        float ir = 1.0f / r;
        u += m * ir;
        float k = -m * ir * ir * ir;
        gx += k * dx; gy += k * dy; gz += k * dz;
    }

    private static float Potential(in BlackHoleGpuParamsF p, float x, float y, float z,
                                    out float gx, out float gy, out float gz)
    {
        float u = 1.0f; gx = 0.0f; gy = 0.0f; gz = 0.0f;
        AddHole(x - p.X0, y - p.Y0, z - p.Z0, p.M0, ref u, ref gx, ref gy, ref gz);
        if (p.HoleCount > 1) AddHole(x - p.X1, y - p.Y1, z - p.Z1, p.M1, ref u, ref gx, ref gy, ref gz);
        if (p.HoleCount > 2) AddHole(x - p.X2, y - p.Y2, z - p.Z2, p.M2, ref u, ref gx, ref gy, ref gz);
        if (p.HoleCount > 3) AddHole(x - p.X3, y - p.Y3, z - p.Z3, p.M3, ref u, ref gx, ref gy, ref gz);
        return u;
    }

    private static S6F Deriv(in BlackHoleGpuParamsF p, S6F s)
    {
        float u = Potential(p, s.X, s.Y, s.Z, out float gx, out float gy, out float gz);
        float iu2 = 1.0f / (u * u);
        float p2 = s.Px * s.Px + s.Py * s.Py + s.Pz * s.Pz;
        float f = u + p2 * iu2 / u;
        S6F d;
        d.X = s.Px * iu2; d.Y = s.Py * iu2; d.Z = s.Pz * iu2;
        d.Px = gx * f; d.Py = gy * f; d.Pz = gz * f;
        return d;
    }

    private static float Dist2(in BlackHoleGpuParamsF p, int i, float x, float y, float z)
    {
        float dx, dy, dz;
        if (i == 0) { dx = x - p.X0; dy = y - p.Y0; dz = z - p.Z0; }
        else if (i == 1) { dx = x - p.X1; dy = y - p.Y1; dz = z - p.Z1; }
        else if (i == 2) { dx = x - p.X2; dy = y - p.Y2; dz = z - p.Z2; }
        else { dx = x - p.X3; dy = y - p.Y3; dz = z - p.Z3; }
        return dx * dx + dy * dy + dz * dz;
    }

    private static float Cap2(in BlackHoleGpuParamsF p, int i)
        => i == 0 ? p.C0 : i == 1 ? p.C1 : i == 2 ? p.C2 : p.C3;

    private static float MaxStep(in BlackHoleGpuParamsF p, S6F s)
    {
        float near = float.PositiveInfinity;
        for (int i = 0; i < p.HoleCount; i++) near = MathF.Min(near, Dist2(p, i, s.X, s.Y, s.Z));
        float r = MathF.Sqrt(near);
        return MathF.Max(1e-9f, MathF.Min(0.25f * r + 1e-6f, 4.0f + 0.05f * r));
    }

    // 0 running, 1 captured (body), 2 escaped.
    private static int Classify(in BlackHoleGpuParamsF p, S6F s, out int body)
    {
        for (int i = 0; i < p.HoleCount; i++)
        {
            if (Dist2(p, i, s.X, s.Y, s.Z) < Cap2(p, i)) { body = i; return 1; }
        }
        body = -1;
        float r2 = s.X * s.X + s.Y * s.Y + s.Z * s.Z;
        if (r2 > p.Escape2 && s.X * s.Px + s.Y * s.Py + s.Z * s.Pz > 0.0f) return 2;
        return 0;
    }

    private static S6F Stage(S6F s, float h, S6F a, float ca, S6F b, float cb, S6F c, float cc, S6F d, float cd, S6F e, float ce)
    {
        S6F r;
        r.X = s.X + h * (ca * a.X + cb * b.X + cc * c.X + cd * d.X + ce * e.X);
        r.Y = s.Y + h * (ca * a.Y + cb * b.Y + cc * c.Y + cd * d.Y + ce * e.Y);
        r.Z = s.Z + h * (ca * a.Z + cb * b.Z + cc * c.Z + cd * d.Z + ce * e.Z);
        r.Px = s.Px + h * (ca * a.Px + cb * b.Px + cc * c.Px + cd * d.Px + ce * e.Px);
        r.Py = s.Py + h * (ca * a.Py + cb * b.Py + cc * c.Py + cd * d.Py + ce * e.Py);
        r.Pz = s.Pz + h * (ca * a.Pz + cb * b.Pz + cc * c.Pz + cd * d.Pz + ce * e.Pz);
        return r;
    }

    // One component's error ratio (ScatteringIntegrator's loop body).
    private static float Ratio(float h, float k1, float k3, float k4, float k5, float k6, float k7,
                                float y0, float y1, float rtol, float atol)
    {
        float e = h * ((float)ScatteringIntegrator.E1 * k1 + (float)ScatteringIntegrator.E3 * k3 + (float)ScatteringIntegrator.E4 * k4
                      + (float)ScatteringIntegrator.E5 * k5 + (float)ScatteringIntegrator.E6 * k6 + (float)ScatteringIntegrator.E7 * k7);
        float sc = atol + rtol * MathF.Max(MathF.Abs(y0), MathF.Abs(y1));
        return MathF.Abs(e) / sc;
    }

    private static bool Finite(float v) => v - v == 0.0f;

    internal static void TraceKernelF(Index1D tid, GpuRaymarchParams r, BlackHoleGpuParamsF p,
                                    ArrayView<int> packed, ArrayView<float> data)
    {
        int idx = GpuKernelUtils.TilePixel(tid, in r);
        int px = idx % r.Width;
        int py = idx / r.Width;
        if (py >= r.Height) return;

        // BlackHoleShadowCalculator.PlaneAt(x + 0.5f, y + 0.5f, ...): v is the negated plane y.
        float u = (float)(p.CenterX + (px + 0.5 - p.Width * 0.5) * p.Pitch);
        float v = (float)-(p.CenterY + (py + 0.5 - p.Height * 0.5) * p.Pitch);

        // MpCamera.Origin / Direction / Launch.
        float ox, oy, oz, dx, dy, dz;
        if (p.Pinhole != 0)
        {
            ox = p.CamX; oy = p.CamY; oz = p.CamZ;
            dx = p.Distance * p.Fx + u * p.Rx + v * p.Ux;
            dy = p.Distance * p.Fy + u * p.Ry + v * p.Uy;
            dz = p.Distance * p.Fz + u * p.Rz + v * p.Uz;
            float l = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            dx /= l; dy /= l; dz /= l;
        }
        else
        {
            ox = p.CamX + u * p.Rx + v * p.Ux; oy = p.CamY + u * p.Ry + v * p.Uy; oz = p.CamZ + u * p.Rz + v * p.Uz;
            dx = p.Fx; dy = p.Fy; dz = p.Fz;
        }
        float uu = Potential(p, ox, oy, oz, out _, out _, out _);
        float pm = uu * uu;
        S6F s;
        s.X = ox; s.Y = oy; s.Z = oz; s.Px = pm * dx; s.Py = pm * dy; s.Pz = pm * dz;

        // ScatteringIntegrator.Trace.
        const float A21 = 1.0f / 5.0f;
        const float A31 = 3.0f / 40.0f, A32 = 9.0f / 40.0f;
        const float A41 = 44.0f / 45.0f, A42 = -56.0f / 15.0f, A43 = 32.0f / 9.0f;
        const float A51 = 19372.0f / 6561.0f, A52 = -25360.0f / 2187.0f, A53 = 64448.0f / 6561.0f, A54 = -212.0f / 729.0f;
        const float A61 = 9017.0f / 3168.0f, A62 = -355.0f / 33.0f, A63 = 46732.0f / 5247.0f, A64 = 49.0f / 176.0f, A65 = -5103.0f / 18656.0f;
        const float B1 = 35.0f / 384.0f, B3 = 500.0f / 1113.0f, B4 = 125.0f / 192.0f, B5 = -2187.0f / 6784.0f, B6 = 11.0f / 84.0f;

        int maxSteps = p.MaxSteps;
        float h = MathF.Min(p.InitialStep, MaxStep(p, s));
        float lambda = 0.0f, delay = 0.0f;
        int steps = 0, attempts = 0, attemptCap = maxSteps * 8;
        int fate = 3, body = -1;   // 3 = undecided
        S6F k1 = Deriv(p, s);
        S6F zero = default;
        while (steps < maxSteps && attempts < attemptCap)
        {
            attempts++;
            float hMax = MaxStep(p, s);
            if (h > hMax) h = hMax;
            if (!(h > 0) || !Finite(h)) break;

            S6F k2 = Deriv(p, Stage(s, h, k1, A21, zero, 0, zero, 0, zero, 0, zero, 0));
            S6F k3 = Deriv(p, Stage(s, h, k1, A31, k2, A32, zero, 0, zero, 0, zero, 0));
            S6F k4 = Deriv(p, Stage(s, h, k1, A41, k2, A42, k3, A43, zero, 0, zero, 0));
            S6F k5 = Deriv(p, Stage(s, h, k1, A51, k2, A52, k3, A53, k4, A54, zero, 0));
            S6F k6 = Deriv(p, Stage(s, h, k1, A61, k2, A62, k3, A63, k4, A64, k5, A65));
            S6F yn = Stage(s, h, k1, B1, k3, B3, k4, B4, k5, B5, k6, B6);
            S6F k7 = Deriv(p, yn);

            float rt = p.RelTol, at = p.AbsTol;
            float e0 = Ratio(h, k1.X, k3.X, k4.X, k5.X, k6.X, k7.X, s.X, yn.X, rt, at);
            float e1 = Ratio(h, k1.Y, k3.Y, k4.Y, k5.Y, k6.Y, k7.Y, s.Y, yn.Y, rt, at);
            float e2 = Ratio(h, k1.Z, k3.Z, k4.Z, k5.Z, k6.Z, k7.Z, s.Z, yn.Z, rt, at);
            float e3 = Ratio(h, k1.Px, k3.Px, k4.Px, k5.Px, k6.Px, k7.Px, s.Px, yn.Px, rt, at);
            float e4 = Ratio(h, k1.Py, k3.Py, k4.Py, k5.Py, k6.Py, k7.Py, s.Py, yn.Py, rt, at);
            float e5 = Ratio(h, k1.Pz, k3.Pz, k4.Pz, k5.Pz, k6.Pz, k7.Pz, s.Pz, yn.Pz, rt, at);
            bool finite = Finite(e0) && Finite(e1) && Finite(e2) && Finite(e3) && Finite(e4) && Finite(e5);
            float err = MathF.Max(MathF.Max(MathF.Max(e0, e1), MathF.Max(e2, e3)), MathF.Max(e4, e5));

            if (finite && err <= 1.0f)
            {
                lambda += h;
                steps++;
                s = yn;
                k1 = k7;
                float uo = Potential(p, s.X, s.Y, s.Z, out _, out _, out _);
                delay += (uo * uo - 1.0f) * h;
                int c = Classify(p, s, out int b);
                if (c != 0) { fate = c; body = b; break; }
                float fac = err == 0.0f ? 5.0f : MathF.Min(5.0f, MathF.Max(0.2f, 0.9f * MathF.Pow(err, -0.2f)));
                h *= fac;
            }
            else
            {
                float fac = finite ? MathF.Min(1.0f, MathF.Max(0.2f, 0.9f * MathF.Pow(err, -0.2f))) : 0.2f;
                h *= fac;
                if (h < 1e-14f * MathF.Max(1.0f, lambda)) break;
            }
        }

        int gate = fate == 1 ? body : fate == 2 ? p.HoleCount : -1;
        packed[idx] = (gate + 1) | (steps << 3);
        int o = idx * BlackHoleShadowGpuCalculator.DataStride;
        data[o] = delay;
        data[o + 1] = s.Px;
        data[o + 2] = s.Py;
        data[o + 3] = s.Pz;
    }
}
