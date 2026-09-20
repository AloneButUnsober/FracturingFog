// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.cs (#863 / #864, epic #850)
//
// Dual-orbit escape-geometry field — an FF-original construction (session notes
// 2026-09-17; Docs/Technical/Theoretical-Fractal-RnD.md §3.6). Per parameter-
// space sample s = (sx, sy), run TWO orbits under one shared complex-square map
// u_{n+1} = u_n² + s, differing only in initial condition:
//   • z-orbit: u0 = 0            (the critical orbit — its escape set is the
//                                 Mandelbrot set of the s-plane)
//   • c-orbit: u0 = c            (a fixed, independent seed — DECOUPLED from s)
// Capture each orbit's escape location E and smooth escape count n, then render a
// derived escape-space scalar (DualOrbitField: separation |E_c−E_z|, midpoint
// residual |M−s|, dual-orbit angle, or Δn) to the SmoothBuffer — so every 2D
// theme, ColorGen theme and Relief-3D height path colours it unchanged (the
// PrecisionFieldCalculator #628 precedent: dual TIER there, dual INIT here).
//
// The c-seed MUST be decoupled from s. Setting c = s makes the c-orbit the
// z-orbit advanced one step (c_n = z_{n+1}), so E_c = E_z, D ≡ 0, Δn ≡ −1 and the
// dual fields collapse to a plain Mandelbrot exterior — kept only as the labelled
// "Mandelbrot control" (DualOrbitCEqualsS). See §3.6 "Seed-decoupling degeneracy".

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class DualOrbitEscapeCalculator : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // The derived escape-space scalar (scaled to [0, maxIter]) doubles as the
    // Relief-3D height field.
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; } = -0.5;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 256;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    // Large bailout for a smooth continuous escape count / escape location.
    private const double EscapeR = 128.0;
    private const double EscapeR2 = EscapeR * EscapeR;
    private static readonly double LogEscapeR = Math.Log(EscapeR);

    public DualOrbitEscapeCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
    }

    // One orbit's escape outcome.
    private readonly struct Orbit
    {
        public readonly bool Escaped;
        public readonly double Ex, Ey;    // escape location (u at escape)
        public readonly double SmoothN;   // continuous escape count
        public Orbit(bool escaped, double ex, double ey, double smoothN)
        { Escaped = escaped; Ex = ex; Ey = ey; SmoothN = smoothN; }
    }

    // Iterate u_{n+1} = u² + s from (u0x, u0y) to the escape radius.
    private static Orbit Run(double u0x, double u0y, double sx, double sy, int maxIter)
    {
        double zx = u0x, zy = u0y;
        for (int n = 0; n < maxIter; n++)
        {
            double x2 = zx * zx, y2 = zy * zy;
            double r2 = x2 + y2;
            if (r2 > EscapeR2)
            {
                // Continuous (fractional) escape count.
                double logZn = Math.Log(r2) * 0.5;
                double nu = Math.Log(logZn / LogEscapeR) / Math.Log(2.0);
                return new Orbit(true, zx, zy, n - nu);
            }
            double nzx = x2 - y2 + sx;
            zy = 2.0 * zx * zy + sy;
            zx = nzx;
        }
        return new Orbit(false, zx, zy, maxIter);
    }

    // One quaternion orbit's escape outcome (Hamilton square, real slot = qx).
    private readonly struct QOrbit
    {
        public readonly bool Escaped;
        public readonly double Ex, Ey, Ez, Ew;   // escape location (4D)
        public readonly double SmoothN;
        public QOrbit(bool escaped, double ex, double ey, double ez, double ew, double smoothN)
        { Escaped = escaped; Ex = ex; Ey = ey; Ez = ez; Ew = ew; SmoothN = smoothN; }
    }

    // Iterate q_{n+1} = q² + C, q² = (qx²−qy²−qz²−qw², 2qx·qy, 2qx·qz, 2qx·qw).
    private static QOrbit RunQuat(double q0x, double q0y, double q0z, double q0w,
        double cx, double cy, double cz, double cw, int maxIter)
    {
        double qx = q0x, qy = q0y, qz = q0z, qw = q0w;
        for (int n = 0; n < maxIter; n++)
        {
            double r2 = qx * qx + qy * qy + qz * qz + qw * qw;
            if (r2 > EscapeR2)
            {
                double logZn = Math.Log(r2) * 0.5;
                double nu = Math.Log(logZn / LogEscapeR) / Math.Log(2.0);
                return new QOrbit(true, qx, qy, qz, qw, n - nu);
            }
            double nqx = qx * qx - qy * qy - qz * qz - qw * qw;
            double nqy = 2.0 * qx * qy;
            double nqz = 2.0 * qx * qz;
            double nqw = 2.0 * qx * qw;
            qx = nqx + cx; qy = nqy + cy; qz = nqz + cz; qw = nqw + cw;
        }
        return new QOrbit(false, qx, qy, qz, qw, maxIter);
    }

    public void Calculate(CancellationToken ct = default)
    {
        int maxIter = Math.Max(16, MaxIterations);
        ColorMap.MaxIterations = maxIter;

        var map = FractalParameters.DualOrbitMap;
        var field = FractalParameters.DualOrbitField;
        bool cEqualsS = FractalParameters.DualOrbitCEqualsS;
        double cSeedX = FractalParameters.DualOrbitCSeedX;
        double cSeedY = FractalParameters.DualOrbitCSeedY;
        double cSeedZ = FractalParameters.DualOrbitCSeedZ;
        double sZ = FractalParameters.DualOrbitSZ;

        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        int width = Width, height = Height;
        double centerX = CenterX, centerY = CenterY;
        bool quat = map == DualOrbitMap.Quaternion;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowBase = y * width;
            double sy = centerY + (y - height * 0.5) * pixelPitch;
            for (int x = 0; x < width; x++)
            {
                double sx = centerX + (x - width * 0.5) * pixelPitch;

                double scalar;
                if (quat)
                {
                    // C = (0, s_x, s_y, s_z) pure-imaginary. z-orbit seed 0; c-orbit
                    // seed the decoupled pure-imaginary (cx, cy, cz) — a different
                    // plane, so the pair diverges in 4D (non-degenerate).
                    QOrbit oz = RunQuat(0, 0, 0, 0, 0, sx, sy, sZ, maxIter);
                    QOrbit oc = cEqualsS
                        ? RunQuat(0, sx, sy, sZ, 0, sx, sy, sZ, maxIter)
                        : RunQuat(0, cSeedX, cSeedY, cSeedZ, 0, sx, sy, sZ, maxIter);
                    scalar = ScalarQ(field, oz, oc, sx, sy, sZ, maxIter);
                }
                else
                {
                    Orbit oz = Run(0.0, 0.0, sx, sy, maxIter);
                    Orbit oc = cEqualsS
                        ? Run(sx, sy, sx, sy, maxIter)
                        : Run(cSeedX, cSeedY, sx, sy, maxIter);
                    scalar = Scalar(field, oz, oc, sx, sy, maxIter);
                }

                int idx = rowBase + x;
                float smooth = (float)scalar;
                SmoothBuffer[idx] = smooth;
                ColorBuffer[idx] = unchecked((uint)ColorMap.Map(smooth, 0f, maxIter));
            }
        });
    }

    // 4D escape-geometry scalar (quaternion mode). s lives at the pure-imaginary
    // point (0, sx, sy, sz); distances / angles use the Euclidean 4D metric.
    private static double ScalarQ(DualOrbitField field, in QOrbit oz, in QOrbit oc,
        double sx, double sy, double sz, int maxIter)
    {
        if (!oz.Escaped || !oc.Escaped) return 0.0;
        // s as a 4D point (real part 0).
        double s0 = 0.0, s1 = sx, s2 = sy, s3 = sz;
        switch (field)
        {
            case DualOrbitField.EscapeSeparation:
            {
                double dx = oc.Ex - oz.Ex, dy = oc.Ey - oz.Ey, dz = oc.Ez - oz.Ez, dw = oc.Ew - oz.Ew;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
                return Math.Min(d / (2.0 * EscapeR), 1.0) * maxIter;
            }
            case DualOrbitField.MidpointResidual:
            {
                double mx = 0.5 * (oz.Ex + oc.Ex), my = 0.5 * (oz.Ey + oc.Ey);
                double mz = 0.5 * (oz.Ez + oc.Ez), mw = 0.5 * (oz.Ew + oc.Ew);
                double rx = mx - s0, ry = my - s1, rz = mz - s2, rw = mw - s3;
                double r = Math.Sqrt(rx * rx + ry * ry + rz * rz + rw * rw);
                return Math.Min(r / (2.0 * EscapeR), 1.0) * maxIter;
            }
            case DualOrbitField.DualOrbitAngle:
            {
                double azx = oz.Ex - s0, azy = oz.Ey - s1, azz = oz.Ez - s2, azw = oz.Ew - s3;
                double acx = oc.Ex - s0, acy = oc.Ey - s1, acz = oc.Ez - s2, acw = oc.Ew - s3;
                double dot = azx * acx + azy * acy + azz * acz + azw * acw;
                double mag = Math.Sqrt((azx * azx + azy * azy + azz * azz + azw * azw)
                                     * (acx * acx + acy * acy + acz * acz + acw * acw));
                if (mag < 1e-18) return 0.0;
                double ang = Math.Acos(Math.Clamp(dot / mag, -1.0, 1.0));
                return (ang / Math.PI) * maxIter;
            }
            case DualOrbitField.DeltaN:
            {
                double dn = oc.SmoothN - oz.SmoothN;
                double t = 0.5 + 0.5 * Math.Clamp(dn / maxIter, -1.0, 1.0);
                return t * maxIter;
            }
            default:
                return 0.0;
        }
    }

    // Derived escape-space scalar, normalised to [0, maxIter] for the palette /
    // height field. Interior (either orbit never escaped) reads 0 — dark, flat.
    private static double Scalar(DualOrbitField field, in Orbit oz, in Orbit oc,
        double sx, double sy, int maxIter)
    {
        if (!oz.Escaped || !oc.Escaped) return 0.0;

        switch (field)
        {
            case DualOrbitField.EscapeSeparation:
            {
                double dx = oc.Ex - oz.Ex, dy = oc.Ey - oz.Ey;
                double d = Math.Sqrt(dx * dx + dy * dy);
                return Math.Min(d / (2.0 * EscapeR), 1.0) * maxIter;
            }
            case DualOrbitField.MidpointResidual:
            {
                double mx = 0.5 * (oz.Ex + oc.Ex), my = 0.5 * (oz.Ey + oc.Ey);
                double rx = mx - sx, ry = my - sy;
                double r = Math.Sqrt(rx * rx + ry * ry);
                return Math.Min(r / (2.0 * EscapeR), 1.0) * maxIter;
            }
            case DualOrbitField.DualOrbitAngle:
            {
                // Angle between (E_z − s) and (E_c − s), in [0, π].
                double azx = oz.Ex - sx, azy = oz.Ey - sy;
                double acx = oc.Ex - sx, acy = oc.Ey - sy;
                double dot = azx * acx + azy * acy;
                double mag = Math.Sqrt((azx * azx + azy * azy) * (acx * acx + acy * acy));
                if (mag < 1e-18) return 0.0;
                double ang = Math.Acos(Math.Clamp(dot / mag, -1.0, 1.0));   // [0, π]
                return (ang / Math.PI) * maxIter;
            }
            case DualOrbitField.DeltaN:
            {
                // n_c − n_z centred at maxIter/2 so 0 difference is mid-palette.
                double dn = oc.SmoothN - oz.SmoothN;
                double t = 0.5 + 0.5 * Math.Clamp(dn / maxIter, -1.0, 1.0);
                return t * maxIter;
            }
            default:
                return 0.0;
        }
    }
}
