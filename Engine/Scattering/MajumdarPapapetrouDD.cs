// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MajumdarPapapetrouDD.cs (#1927, black hole shadows S5 part 2)
//
// Double-double (DD) twin of the black hole trace, for views past what double precision
// can resolve. Calibration (#1927 part 1) showed the fate edges need a step tolerance of
// about 0.003 x the pixel pitch, and below about 1e-13 double cannot step any tighter:
// past roughly x10^10 the double references themselves disagree. This trace keeps the
// state (position and momentum) and every field evaluation in DD (Hida, Li & Bailey's
// arithmetic, FFMath.DD, about 31 digits), so tolerances down to ~1e-28 are meaningful.
//
// Same method as the double trace: ScatteringIntegrator's Dormand-Prince 5(4) stages and
// controller (step size and error ratios stay double: they only steer the steps), the
// Majumdar-Papapetrou Hamiltonian flow of MpNullSystem, its local step cap and exit tests,
// and MpCamera's launch. Channels off: fate, steps, delay and final momentum, which is what
// the Fate / Delay / Steps / Deflection colourings and the sky read. The DD square root is
// one Newton step x + (a - x^2) / (2x) from the double square root (FFMath.DD has none; this
// is FF's own, not taken from the QD library). DD costs far more per ray than double, so the calculator runs it
// only on pixels at fate boundaries (BlackHoleShadowCalculator's edge fix-up).

using System;

using FracturingFog.FFMath;

namespace FracturingFog.Scattering;

/// <summary>Result of one DD trace.</summary>
public readonly struct MpDdOutcome
{
    public ScatterFate Fate { get; init; }
    public int Body { get; init; }
    public int Steps { get; init; }
    public double Delay { get; init; }
    public double Px { get; init; }
    public double Py { get; init; }
    public double Pz { get; init; }
}

public static class MajumdarPapapetrouDD
{
    private struct V6 { public DD X, Y, Z, Px, Py, Pz; }

    /// <summary>DD square root: one Newton step from the double estimate.</summary>
    public static DD Sqrt(DD a)
    {
        if (!(a.Hi > 0)) return 0.0;
        double x = Math.Sqrt(a.Hi);
        DD xd = x;
        return xd + (a - xd * x) / (2.0 * x);
    }

    private static DD Potential(MpHole[] h, int n, DD x, DD y, DD z, out DD gx, out DD gy, out DD gz)
    {
        DD u = 1.0; gx = 0.0; gy = 0.0; gz = 0.0;
        for (int i = 0; i < n; i++)
        {
            double m = Math.Max(0.0, h[i].M);
            if (m == 0.0) continue;
            DD dx = x - h[i].X, dy = y - h[i].Y, dz = z - h[i].Z;
            DD r2 = dx * dx + dy * dy + dz * dz;
            DD r = Sqrt(r2);
            DD ir = 1.0 / r;
            u = u + ir * m;
            DD k = -(ir * ir * ir * m);
            gx = gx + k * dx; gy = gy + k * dy; gz = gz + k * dz;
        }
        return u;
    }

    private static V6 Deriv(MpHole[] h, int n, in V6 s)
    {
        DD u = Potential(h, n, s.X, s.Y, s.Z, out var gx, out var gy, out var gz);
        DD iu2 = 1.0 / (u * u);
        DD p2 = s.Px * s.Px + s.Py * s.Py + s.Pz * s.Pz;
        DD f = u + p2 * iu2 / u;
        V6 d;
        d.X = s.Px * iu2; d.Y = s.Py * iu2; d.Z = s.Pz * iu2;
        d.Px = gx * f; d.Py = gy * f; d.Pz = gz * f;
        return d;
    }

    private static double Dist2(MpHole[] h, int i, in V6 s)
    {
        double dx = (double)(s.X - h[i].X), dy = (double)(s.Y - h[i].Y), dz = (double)(s.Z - h[i].Z);
        return dx * dx + dy * dy + dz * dz;
    }

    private static V6 Stage(in V6 s, double h, in V6 a, double ca, in V6 b, double cb, in V6 c, double cc,
                            in V6 d, double cd, in V6 e, double ce)
    {
        V6 r;
        r.X = s.X + (a.X * ca + b.X * cb + c.X * cc + d.X * cd + e.X * ce) * h;
        r.Y = s.Y + (a.Y * ca + b.Y * cb + c.Y * cc + d.Y * cd + e.Y * ce) * h;
        r.Z = s.Z + (a.Z * ca + b.Z * cb + c.Z * cc + d.Z * cd + e.Z * ce) * h;
        r.Px = s.Px + (a.Px * ca + b.Px * cb + c.Px * cc + d.Px * cd + e.Px * ce) * h;
        r.Py = s.Py + (a.Py * ca + b.Py * cb + c.Py * cc + d.Py * cd + e.Py * ce) * h;
        r.Pz = s.Pz + (a.Pz * ca + b.Pz * cb + c.Pz * cc + d.Pz * cd + e.Pz * ce) * h;
        return r;
    }

    private static double Ratio(double h, DD k1, DD k3, DD k4, DD k5, DD k6, DD k7, DD y0, DD y1, double rtol, double atol)
    {
        DD e = (k1 * ScatteringIntegrator.E1 + k3 * ScatteringIntegrator.E3 + k4 * ScatteringIntegrator.E4
              + k5 * ScatteringIntegrator.E5 + k6 * ScatteringIntegrator.E6 + k7 * ScatteringIntegrator.E7) * h;
        double sc = atol + rtol * Math.Max(Math.Abs(y0.Hi), Math.Abs(y1.Hi));
        return Math.Abs(e.Hi) / sc;
    }

    /// <summary>Trace the ray for image-plane point (<paramref name="u"/>, <paramref name="v"/>) in DD
    /// at relative tolerance <paramref name="relTol"/>, with the scene's holes, camera, capture and
    /// escape rules.</summary>
    public static MpDdOutcome Trace(BlackHoleShadowCalculator.Scene scene, DD u, DD v, double relTol, int maxSteps)
    {
        var holes = scene.Holes;
        int n = Math.Clamp(holes.Length, 1, MpNullSystem.MaxHoles);
        double cf = Math.Max(1e-6, scene.CaptureFraction);
        var cam = scene.Camera;

        // MpCamera.Origin / Direction / Launch, in DD.
        DD ox, oy, oz, dx, dy, dz;
        if (cam.Pinhole)
        {
            ox = cam.Cx; oy = cam.Cy; oz = cam.Cz;
            dx = u * cam.Rx + v * cam.Ux + cam.Distance * cam.Fx;
            dy = u * cam.Ry + v * cam.Uy + cam.Distance * cam.Fy;
            dz = u * cam.Rz + v * cam.Uz + cam.Distance * cam.Fz;
            DD l = Sqrt(dx * dx + dy * dy + dz * dz);
            dx = dx / l; dy = dy / l; dz = dz / l;
        }
        else
        {
            ox = u * cam.Rx + v * cam.Ux + cam.Cx; oy = u * cam.Ry + v * cam.Uy + cam.Cy; oz = u * cam.Rz + v * cam.Uz + cam.Cz;
            dx = cam.Fx; dy = cam.Fy; dz = cam.Fz;
        }
        DD uu = Potential(holes, n, ox, oy, oz, out _, out _, out _);
        DD pm = uu * uu;
        V6 s;
        s.X = ox; s.Y = oy; s.Z = oz; s.Px = pm * dx; s.Py = pm * dy; s.Pz = pm * dz;

        const double A21 = 1.0 / 5.0;
        const double A31 = 3.0 / 40.0, A32 = 9.0 / 40.0;
        const double A41 = 44.0 / 45.0, A42 = -56.0 / 15.0, A43 = 32.0 / 9.0;
        const double A51 = 19372.0 / 6561.0, A52 = -25360.0 / 2187.0, A53 = 64448.0 / 6561.0, A54 = -212.0 / 729.0;
        const double A61 = 9017.0 / 3168.0, A62 = -355.0 / 33.0, A63 = 46732.0 / 5247.0, A64 = 49.0 / 176.0, A65 = -5103.0 / 18656.0;
        const double B1 = 35.0 / 384.0, B3 = 500.0 / 1113.0, B4 = 125.0 / 192.0, B5 = -2187.0 / 6784.0, B6 = 11.0 / 84.0;

        double rtol = relTol, atol = relTol * 1e-3;
        double escape2 = scene.EscapeRadius * scene.EscapeRadius;
        double MaxStep(in V6 st)
        {
            double near = double.PositiveInfinity;
            for (int i = 0; i < n; i++) near = Math.Min(near, Dist2(holes, i, st));
            double r = Math.Sqrt(near);
            return Math.Max(1e-30, Math.Min(0.25 * r + 1e-6, 4.0 + 0.05 * r));
        }

        double h = Math.Min(scene.Settings.InitialStep > 0 ? scene.Settings.InitialStep : 0.1, MaxStep(s));
        double lambda = 0.0, delay = 0.0;
        int steps = 0, attempts = 0, attemptCap = maxSteps * 8;
        var fate = ScatterFate.Undecided; int body = -1;
        V6 zero = default;
        V6 k1 = Deriv(holes, n, s);
        while (steps < maxSteps && attempts < attemptCap)
        {
            attempts++;
            double hMax = MaxStep(s);
            if (h > hMax) h = hMax;
            if (!(h > 0) || !double.IsFinite(h)) break;

            V6 k2 = Deriv(holes, n, Stage(s, h, k1, A21, zero, 0, zero, 0, zero, 0, zero, 0));
            V6 k3 = Deriv(holes, n, Stage(s, h, k1, A31, k2, A32, zero, 0, zero, 0, zero, 0));
            V6 k4 = Deriv(holes, n, Stage(s, h, k1, A41, k2, A42, k3, A43, zero, 0, zero, 0));
            V6 k5 = Deriv(holes, n, Stage(s, h, k1, A51, k2, A52, k3, A53, k4, A54, zero, 0));
            V6 k6 = Deriv(holes, n, Stage(s, h, k1, A61, k2, A62, k3, A63, k4, A64, k5, A65));
            V6 yn = Stage(s, h, k1, B1, k3, B3, k4, B4, k5, B5, k6, B6);
            V6 k7 = Deriv(holes, n, yn);

            double e0 = Ratio(h, k1.X, k3.X, k4.X, k5.X, k6.X, k7.X, s.X, yn.X, rtol, atol);
            double e1 = Ratio(h, k1.Y, k3.Y, k4.Y, k5.Y, k6.Y, k7.Y, s.Y, yn.Y, rtol, atol);
            double e2 = Ratio(h, k1.Z, k3.Z, k4.Z, k5.Z, k6.Z, k7.Z, s.Z, yn.Z, rtol, atol);
            double e3 = Ratio(h, k1.Px, k3.Px, k4.Px, k5.Px, k6.Px, k7.Px, s.Px, yn.Px, rtol, atol);
            double e4 = Ratio(h, k1.Py, k3.Py, k4.Py, k5.Py, k6.Py, k7.Py, s.Py, yn.Py, rtol, atol);
            double e5 = Ratio(h, k1.Pz, k3.Pz, k4.Pz, k5.Pz, k6.Pz, k7.Pz, s.Pz, yn.Pz, rtol, atol);
            bool finite = double.IsFinite(e0) && double.IsFinite(e1) && double.IsFinite(e2)
                       && double.IsFinite(e3) && double.IsFinite(e4) && double.IsFinite(e5);
            double err = Math.Max(Math.Max(Math.Max(e0, e1), Math.Max(e2, e3)), Math.Max(e4, e5));

            if (finite && err <= 1.0)
            {
                lambda += h;
                steps++;
                s = yn;
                k1 = k7;
                DD uo = Potential(holes, n, s.X, s.Y, s.Z, out _, out _, out _);
                delay += ((double)(uo * uo) - 1.0) * h;
                // MpNullSystem.Classify.
                bool done = false;
                for (int i = 0; i < n; i++)
                {
                    double c = cf * Math.Max(0.0, holes[i].M);
                    if (Dist2(holes, i, s) < c * c) { fate = ScatterFate.Captured; body = i; done = true; break; }
                }
                if (!done)
                {
                    double x = s.X.Hi, y = s.Y.Hi, z = s.Z.Hi;
                    if (x * x + y * y + z * z > escape2 && x * s.Px.Hi + y * s.Py.Hi + z * s.Pz.Hi > 0.0)
                    { fate = ScatterFate.Escaped; done = true; }
                }
                if (done) break;
                h *= err == 0.0 ? 5.0 : Math.Clamp(0.9 * Math.Pow(err, -0.2), 0.2, 5.0);
            }
            else
            {
                h *= finite ? Math.Clamp(0.9 * Math.Pow(err, -0.2), 0.2, 1.0) : 0.2;
                if (h < 1e-30 * Math.Max(1.0, lambda)) break;
            }
        }
        return new MpDdOutcome
        {
            Fate = fate, Body = body, Steps = steps, Delay = delay,
            Px = (double)s.Px, Py = (double)s.Py, Pz = (double)s.Pz,
        };
    }

    /// <summary>|p| / U² at the end of a DD trace's state, for the null-condition check (tests).</summary>
    public static double NullRatio(BlackHoleShadowCalculator.Scene scene, DD x, DD y, DD z, DD px, DD py, DD pz)
    {
        var h = scene.Holes;
        DD u = Potential(h, Math.Clamp(h.Length, 1, MpNullSystem.MaxHoles), x, y, z, out _, out _, out _);
        return (double)(Sqrt(px * px + py * py + pz * pz) / (u * u));
    }
}
