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
// Intrinsic fields (#970): E sits at radius ≥ the bailout while s sits at ≤ 2, so
// the three escape-LOCATION fields (separation / residual / angle) depend on the
// bailout radius. GreenRatio (log2 G_c/G_z = n_z − n_c) and ExternalAngleDelta
// (Böttcher angles by backward lifting) are bailout-independent.
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

    // Bailout radius (FractalParameters.DualOrbitBailout, default 128) — large for
    // a smooth continuous escape count. Snapshotted per Calculate().
    private readonly struct Bailout
    {
        public readonly double R, R2, LogR;
        public Bailout(double r) { R = r; R2 = r * r; LogR = Math.Log(r); }
    }

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

    // Iterate u_{n+1} = u² + s from (u0x, u0y) to the escape radius. When
    // `args` is non-empty, arg(u_n) is recorded for every n up to the escape index
    // (the external-angle lift reads it); it needs length ≥ maxIter + 1.
    private static Orbit Run(double u0x, double u0y, double sx, double sy, int maxIter,
        in Bailout b, Span<double> args, out int escapeIndex)
    {
        double zx = u0x, zy = u0y;
        bool track = !args.IsEmpty;
        for (int n = 0; n < maxIter; n++)
        {
            if (track) args[n] = Math.Atan2(zy, zx);
            double x2 = zx * zx, y2 = zy * zy;
            double r2 = x2 + y2;
            if (r2 > b.R2)
            {
                // Continuous (fractional) escape count.
                double logZn = Math.Log(r2) * 0.5;
                double nu = Math.Log(logZn / b.LogR) / Math.Log(2.0);
                escapeIndex = n;
                return new Orbit(true, zx, zy, n - nu);
            }
            double nzx = x2 - y2 + sx;
            zy = 2.0 * zx * zy + sy;
            zx = nzx;
        }
        escapeIndex = -1;
        return new Orbit(false, zx, zy, maxIter);
    }

    /// <summary>External angle, in turns [0, 1), of the level-1 point u_1 = u0² + s
    /// under u → u² + s — i.e. arg φ_s(u_1)/2π with φ_s the Böttcher coordinate.
    /// Read off the escaped orbit by backward lifting: at escape arg u_N ≈ arg φ(u_N);
    /// each step back halves the angle, choosing the half (t/2 or t/2 + ½) nearest
    /// arg u_k — no branch-cut product. For u0 = 0 this is the Mandelbrot parameter
    /// external angle of s. Returns NaN if the orbit does not escape within maxIter.
    /// Bailout-independent up to O(|s|/R²).</summary>
    public static double ExternalAngleTurns(double u0x, double u0y, double sx, double sy,
        int maxIter, double bailout = 128.0)
    {
        var args = new double[maxIter + 1];
        Run(u0x, u0y, sx, sy, maxIter, new Bailout(bailout), args, out int n);
        return n < 0 ? double.NaN : LiftToLevel1(args, n);
    }

    private static double LiftToLevel1(ReadOnlySpan<double> args, int escapeIndex)
    {
        const double TwoPi = 2.0 * Math.PI;
        double t = Frac(args[escapeIndex] / TwoPi);
        // Seed already past the bailout: u_1 ≈ u_0², so the level-1 angle doubles.
        if (escapeIndex == 0) return Frac(2.0 * t);
        for (int k = escapeIndex - 1; k >= 1; k--)
        {
            double a = Frac(args[k] / TwoPi);
            double t0 = 0.5 * t, t1 = t0 + 0.5;
            t = CircDist(t0, a) <= CircDist(t1, a) ? t0 : t1;
        }
        return t;
    }

    private static double Frac(double x) { double f = x - Math.Floor(x); return f >= 1.0 ? 0.0 : f; }
    private static double CircDist(double a, double b) { double d = Math.Abs(a - b) % 1.0; return Math.Min(d, 1.0 - d); }

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
        double cx, double cy, double cz, double cw, int maxIter, in Bailout b)
    {
        double qx = q0x, qy = q0y, qz = q0z, qw = q0w;
        for (int n = 0; n < maxIter; n++)
        {
            double r2 = qx * qx + qy * qy + qz * qz + qw * qw;
            if (r2 > b.R2)
            {
                double logZn = Math.Log(r2) * 0.5;
                double nu = Math.Log(logZn / b.LogR) / Math.Log(2.0);
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
        var bail = new Bailout(Math.Clamp(FractalParameters.DualOrbitBailout, 2.0, 1e6));
        double ratioSpan = Math.Max(1e-3, FractalParameters.DualOrbitRatioSpan);

        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        int width = Width, height = Height;
        double centerX = CenterX, centerY = CenterY;
        bool quat = map == DualOrbitMap.Quaternion;
        bool angles = !quat && field == DualOrbitField.ExternalAngleDelta;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowBase = y * width;
            double sy = centerY + (y - height * 0.5) * pixelPitch;
            // Per-row arg buffers for the external-angle lift (ExternalAngleDelta only).
            double[] argsZ = angles ? new double[maxIter + 1] : Array.Empty<double>();
            double[] argsC = angles ? new double[maxIter + 1] : Array.Empty<double>();
            for (int x = 0; x < width; x++)
            {
                double sx = centerX + (x - width * 0.5) * pixelPitch;

                double scalar;
                if (quat)
                {
                    // C = (0, s_x, s_y, s_z) pure-imaginary. z-orbit seed 0; c-orbit
                    // seed the decoupled pure-imaginary (cx, cy, cz) — a different
                    // plane, so the pair diverges in 4D (non-degenerate).
                    QOrbit oz = RunQuat(0, 0, 0, 0, 0, sx, sy, sZ, maxIter, bail);
                    QOrbit oc = cEqualsS
                        ? RunQuat(0, sx, sy, sZ, 0, sx, sy, sZ, maxIter, bail)
                        : RunQuat(0, cSeedX, cSeedY, cSeedZ, 0, sx, sy, sZ, maxIter, bail);
                    scalar = ScalarQ(field, oz, oc, sx, sy, sZ, maxIter, bail, ratioSpan);
                }
                else
                {
                    Orbit oz = Run(0.0, 0.0, sx, sy, maxIter, bail, argsZ, out int nZ);
                    Orbit oc = cEqualsS
                        ? Run(sx, sy, sx, sy, maxIter, bail, argsC, out int nC)
                        : Run(cSeedX, cSeedY, sx, sy, maxIter, bail, argsC, out nC);
                    scalar = angles
                        ? AngleDeltaScalar(argsZ, nZ, argsC, nC, maxIter)
                        : Scalar(field, oz, oc, sx, sy, maxIter, bail, ratioSpan);
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
        double sx, double sy, double sz, int maxIter, in Bailout b, double ratioSpan)
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
                return Math.Min(d / (2.0 * b.R), 1.0) * maxIter;
            }
            case DualOrbitField.MidpointResidual:
            {
                double mx = 0.5 * (oz.Ex + oc.Ex), my = 0.5 * (oz.Ey + oc.Ey);
                double mz = 0.5 * (oz.Ez + oc.Ez), mw = 0.5 * (oz.Ew + oc.Ew);
                double rx = mx - s0, ry = my - s1, rz = mz - s2, rw = mw - s3;
                double r = Math.Sqrt(rx * rx + ry * ry + rz * rz + rw * rw);
                return Math.Min(r / (2.0 * b.R), 1.0) * maxIter;
            }
            case DualOrbitField.GreenRatio:
                // |q²| = |q|² for quaternions, so the smooth count and G are the
                // same construction as the complex case.
                return GreenRatioScalar(oz.SmoothN, oc.SmoothN, ratioSpan, maxIter);
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
        double sx, double sy, int maxIter, in Bailout b, double ratioSpan)
    {
        if (!oz.Escaped || !oc.Escaped) return 0.0;

        switch (field)
        {
            case DualOrbitField.EscapeSeparation:
            {
                double dx = oc.Ex - oz.Ex, dy = oc.Ey - oz.Ey;
                double d = Math.Sqrt(dx * dx + dy * dy);
                return Math.Min(d / (2.0 * b.R), 1.0) * maxIter;
            }
            case DualOrbitField.MidpointResidual:
            {
                double mx = 0.5 * (oz.Ex + oc.Ex), my = 0.5 * (oz.Ey + oc.Ey);
                double rx = mx - sx, ry = my - sy;
                double r = Math.Sqrt(rx * rx + ry * ry);
                return Math.Min(r / (2.0 * b.R), 1.0) * maxIter;
            }
            case DualOrbitField.GreenRatio:
                return GreenRatioScalar(oz.SmoothN, oc.SmoothN, ratioSpan, maxIter);
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

    // A live (both-escaped) value must never read exactly 0: SmoothBuffer 0 is the
    // bounded / in-set sentinel that Relief height and histogram paths key on.
    private const double LiveFloor = 1e-3;

    // log2(G_c / G_z) = n_z − n_c exactly (G = log R · 2^−smoothN), on a centred
    // diverging scale: ±span octaves → palette ends, 0 → mid-palette.
    private static double GreenRatioScalar(double smoothZ, double smoothC, double span, int maxIter)
    {
        double octaves = smoothZ - smoothC;
        return Math.Max(LiveFloor, (0.5 + 0.5 * Math.Clamp(octaves / span, -1.0, 1.0)) * maxIter);
    }

    // (θ_c − θ_z) mod 1 at level 1, scaled to [0, maxIter). 0 unless both escape.
    private static double AngleDeltaScalar(double[] argsZ, int nZ, double[] argsC, int nC, int maxIter)
    {
        if (nZ < 0 || nC < 0) return 0.0;
        double d = Frac(LiftToLevel1(argsC, nC) - LiftToLevel1(argsZ, nZ));
        return Math.Max(LiveFloor, d * maxIter);
    }
}
