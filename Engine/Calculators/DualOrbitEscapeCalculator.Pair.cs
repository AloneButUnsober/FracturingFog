// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Pair.cs (#1115, epic #1114 S1)
//
// Per-iteration pair accumulator — the foundation the dual-orbit colouring epic
// builds on (Docs/Technical/DualOrbit-Coloring-RnD.md §1, §4). The shipped render
// keeps only two smooth counts + four flags per pixel; every pair-native field
// (secant Lyapunov, winding, divergence time, …) needs data gathered while the
// two orbits are iterated. This file adds:
//
//   • DualOrbitPairChannels — opt-in flags. None (the default) leaves the original
//     Run() path untouched: byte-identical output, no extra allocation.
//   • RunPair / RunQuatPair — iterate both orbits in lockstep with EXACTLY the
//     arithmetic of Run / RunQuat (so the cached orbit outcome is unchanged) and
//     feed a PairAccum each step while both orbits are still inside the bailout.
//   • DualOrbitPairPlanes — per-pixel float planes, allocated only for the
//     requested channels, read by later slices (S2 fields, S4 DE, S14 Jacobian).
//
// Pair algebra (complex map, verified in the doc §5): with D_n = c_n − z_n and
// σ_n = z_n + c_n, D_{n+1} = D_n·σ_n, so Σ log|σ_k| = log|D_N| − log|c| and
// Σ arg σ_k ≡ arg D_N − arg c (mod 2π). Accumulating σ instead of reading D_N
// keeps the secant sum finite when the orbits contract onto one cycle (D_N would
// underflow). The quaternion square does not commute, so that identity fails
// there: the quaternion secant sum is the telescoped log|D_N| − log|D_0| read
// directly (it saturates at round-off), and winding / itinerary / derivatives
// are NaN.
//
// Accumulation window: steps n = 0 … N−1 where N is the first step at which
// EITHER orbit is past the bailout (or maxIter). Midpoint perturbation is read at
// step N.

using System;

namespace FracturingFog;

/// <summary>Per-iteration pair data the dual-orbit calculator can gather (#1115).
/// Requested by later colouring slices / hosts via
/// <see cref="DualOrbitEscapeCalculator.PairChannels"/>. Flags; None = original
/// path.</summary>
[Flags]
public enum DualOrbitPairChannels
{
    None = 0,
    /// <summary>Σ log|z_k + c_k| and the step count N (secant Lyapunov = sum / N).</summary>
    SecantLogSum = 1 << 0,
    /// <summary>Σ arg(z_k + c_k) / 2π — unwrapped winding of D (complex map only).</summary>
    Winding = 1 << 1,
    /// <summary>min_n |D_n| (natural log) and its step index; continuous
    /// divergence time — when |D_n| first exceeds ρ·|D_0| (−1 if never).</summary>
    Separation = 1 << 2,
    /// <summary>|e|² / |m|² at step N, m = (z+c)/2, e = (z−c)/2 (split-complex
    /// coordinates).</summary>
    MidpointPerturbation = 1 << 3,
    /// <summary>Length of the common binary itinerary (sign Im) from step 1
    /// (complex map only).</summary>
    Itinerary = 1 << 4,
    /// <summary>Per-orbit holomorphic derivatives at each orbit's own escape step:
    /// dz/ds, dc/ds, dc/dc₀, as (ln|d|, arg d), plus ln|u| at escape (complex map
    /// only; NaN where the orbit is bounded).</summary>
    Derivatives = 1 << 5,

    All = SecantLogSum | Winding | Separation | MidpointPerturbation | Itinerary | Derivatives,
}

/// <summary>Per-pixel planes filled when pair channels are requested (#1115).
/// Arrays for channels that were not requested are empty. Layout = ColorBuffer.</summary>
public sealed class DualOrbitPairPlanes
{
    public DualOrbitPairChannels Channels { get; }
    public int Length { get; }

    // SecantLogSum
    public float[] SecantLogSum { get; } = Array.Empty<float>();
    public float[] PairSteps { get; } = Array.Empty<float>();
    // Winding
    public float[] WindingTurns { get; } = Array.Empty<float>();
    // Separation
    public float[] MinSeparationLog { get; } = Array.Empty<float>();
    public float[] MinSeparationIndex { get; } = Array.Empty<float>();
    /// <summary>Continuous divergence time T (n−1 &lt; T ≤ n at the crossing step n,
    /// log-interpolated); −1 if |D| never exceeded ρ·|D_0| in the window.</summary>
    public float[] DivergenceTime { get; } = Array.Empty<float>();
    /// <summary>|D_0| = |c-seed| (Separation channel) — the normaliser for
    /// closest-approach depth.</summary>
    public float[] InitialSeparationLog { get; } = Array.Empty<float>();
    // MidpointPerturbation
    public float[] MidpointPerturbation { get; } = Array.Empty<float>();
    // Itinerary
    public float[] ItineraryAgreement { get; } = Array.Empty<float>();
    // Derivatives
    public float[] LogAbsEscapeZ { get; } = Array.Empty<float>();
    public float[] LogAbsEscapeC { get; } = Array.Empty<float>();
    public float[] LogDzDs { get; } = Array.Empty<float>();
    public float[] ArgDzDs { get; } = Array.Empty<float>();
    public float[] LogDcDs { get; } = Array.Empty<float>();
    public float[] ArgDcDs { get; } = Array.Empty<float>();
    public float[] LogDcDc0 { get; } = Array.Empty<float>();
    public float[] ArgDcDc0 { get; } = Array.Empty<float>();

    public DualOrbitPairPlanes(DualOrbitPairChannels channels, int length)
    {
        Channels = channels;
        Length = length;
        static float[] A(bool on, int n) => on ? new float[n] : Array.Empty<float>();
        bool sec = channels.HasFlag(DualOrbitPairChannels.SecantLogSum);
        SecantLogSum = A(sec, length); PairSteps = A(sec, length);
        WindingTurns = A(channels.HasFlag(DualOrbitPairChannels.Winding), length);
        bool sep = channels.HasFlag(DualOrbitPairChannels.Separation);
        MinSeparationLog = A(sep, length); MinSeparationIndex = A(sep, length); DivergenceTime = A(sep, length);
        InitialSeparationLog = A(sep, length);
        MidpointPerturbation = A(channels.HasFlag(DualOrbitPairChannels.MidpointPerturbation), length);
        ItineraryAgreement = A(channels.HasFlag(DualOrbitPairChannels.Itinerary), length);
        bool der = channels.HasFlag(DualOrbitPairChannels.Derivatives);
        LogAbsEscapeZ = A(der, length); LogAbsEscapeC = A(der, length);
        LogDzDs = A(der, length); ArgDzDs = A(der, length);
        LogDcDs = A(der, length); ArgDcDs = A(der, length);
        LogDcDc0 = A(der, length); ArgDcDc0 = A(der, length);
    }

    /// <summary>Bytes per pixel for a channel set (diagnostics / budget tests).</summary>
    public static int BytesPerPixel(DualOrbitPairChannels ch)
    {
        int floats = 0;
        if (ch.HasFlag(DualOrbitPairChannels.SecantLogSum)) floats += 2;
        if (ch.HasFlag(DualOrbitPairChannels.Winding)) floats += 1;
        if (ch.HasFlag(DualOrbitPairChannels.Separation)) floats += 4;
        if (ch.HasFlag(DualOrbitPairChannels.MidpointPerturbation)) floats += 1;
        if (ch.HasFlag(DualOrbitPairChannels.Itinerary)) floats += 1;
        if (ch.HasFlag(DualOrbitPairChannels.Derivatives)) floats += 8;
        return floats * sizeof(float);
    }
}

public sealed partial class DualOrbitEscapeCalculator
{
    /// <summary>#1115 — pair channels to gather on the next Calculate (host / test /
    /// later-slice injection). Merged with what the selected field needs
    /// (<see cref="ChannelsFor"/>). Changing it re-iterates (part of the geometry
    /// key).</summary>
    public DualOrbitPairChannels PairChannels { get; set; }

    /// <summary>#1116 — override for the divergence amplification ratio ρ
    /// (<see cref="DualOrbitPairPlanes.DivergenceTime"/>: first time |D_n| &gt;
    /// ρ·|D_0|). Null = <c>FractalParameters.DualOrbitDivergenceRatio</c>. Part
    /// of the geometry key.</summary>
    public double? PairDivergenceRatio { get; set; }

    /// <summary>Planes from the last iteration that gathered pair channels; null
    /// when none were requested.</summary>
    public DualOrbitPairPlanes? PairPlanes { get; private set; }

    /// <summary>Channels a field / colour mode needs (#1116). Layer mode reads no
    /// field, so it needs none.</summary>
    internal static DualOrbitPairChannels ChannelsFor(DualOrbitField field, bool layers) => layers
        ? DualOrbitPairChannels.None
        : field switch
        {
            DualOrbitField.SecantLyapunov => DualOrbitPairChannels.SecantLogSum,
            DualOrbitField.DivergenceTime or DualOrbitField.ClosestApproach
                or DualOrbitField.ClosestApproachIndex => DualOrbitPairChannels.Separation,
            DualOrbitField.PairWinding => DualOrbitPairChannels.Winding,
            DualOrbitField.MidpointPerturbation => DualOrbitPairChannels.MidpointPerturbation,
            DualOrbitField.ItineraryAgreement => DualOrbitPairChannels.Itinerary,
            _ => DualOrbitPairChannels.None,
        };

    /// <summary>True for the pair-native fields (#1116): their scalar comes from
    /// the pair accumulator, and their liveness from it (not the escape flags).</summary>
    internal static bool IsPairField(DualOrbitField f)
        => f >= DualOrbitField.SecantLyapunov && f <= DualOrbitField.ItineraryAgreement;

    private const double LogFloor = 1e-300;

    /// <summary>#1116 — the separation |c_n − z_n| is read as at least
    /// RoundOffRel·|(z_n, c_n)|: below that the difference of two doubles is
    /// noise, and converged pairs would otherwise speckle (ClosestApproach /
    /// Index, DivergenceTime). Part of the field definitions (doc §3.B).</summary>
    internal const double RoundOffRel = 1e-14;
    /// <summary>Relative tie tolerance for the closest-approach index.</summary>
    internal const double MinTieRel = 1e-9;

    // Mutable per-pixel accumulator (stack-local in the row loop).
    private struct PairAccum
    {
        public DualOrbitPairChannels Ch;
        public double EpsRatio;          // ρ: divergence threshold = ρ·|D_0|
        public double Eps;               // set at step 0
        public double D0;
        public double PrevD;
        public double DivergeT;
        public double LogSigmaSum;
        public int Steps;
        public double Winding;          // turns
        public double MinSep;
        public int MinSepIdx;
        public int DivergeIdx;
        public double MidPert;
        public int ItinAgree;
        public bool ItinBroken;
        public bool Stopped;
        // derivatives at escape
        public double LogAbsEz, LogAbsEc, LogDz, ArgDz, LogDcS, ArgDcS, LogDcC, ArgDcC;

        public static PairAccum Create(DualOrbitPairChannels ch, double eps) => new()
        {
            Ch = ch, EpsRatio = eps, MinSep = double.PositiveInfinity, MinSepIdx = -1, DivergeIdx = -1,
            DivergeT = -1, D0 = double.NaN,
            MidPert = double.NaN,
            LogAbsEz = double.NaN, LogAbsEc = double.NaN, LogDz = double.NaN, ArgDz = double.NaN,
            LogDcS = double.NaN, ArgDcS = double.NaN, LogDcC = double.NaN, ArgDcC = double.NaN,
        };

        // Both orbits inside the bailout at step n (complex map).
        // Each term is gated on its channel: log / atan2 / sqrt dominate the cost.
        public void Step(int n, double zx, double zy, double cx, double cy)
        {
            Steps++;
            double sgx = zx + cx, sgy = zy + cy;
            if ((Ch & DualOrbitPairChannels.SecantLogSum) != 0)
                LogSigmaSum += 0.5 * Math.Log(Math.Max(sgx * sgx + sgy * sgy, LogFloor));   // |σ| floored at 1e-150 (coalescence)
            if ((Ch & DualOrbitPairChannels.Winding) != 0)
                Winding += Math.Atan2(sgy, sgx) / (2.0 * Math.PI);
            if ((Ch & DualOrbitPairChannels.Separation) != 0)
                Separation(n, Math.Max(Math.Sqrt((cx - zx) * (cx - zx) + (cy - zy) * (cy - zy)),
                    RoundOffRel * Math.Sqrt(zx * zx + zy * zy + cx * cx + cy * cy)));
            if ((Ch & DualOrbitPairChannels.Itinerary) != 0 && n >= 1 && !ItinBroken)
            {
                if ((zy >= 0.0) == (cy >= 0.0)) ItinAgree++;
                else ItinBroken = true;
            }
        }

        public void Separation(int n, double d)
        {
            if (n == 0) { D0 = d; Eps = EpsRatio * d; }
            // First step attaining the minimum (to MinTieRel): converged pairs sit
            // at the round-off floor every cycle — keep the earliest, not noise.
            if (d < MinSep * (1.0 - MinTieRel)) { MinSep = d; MinSepIdx = n; }
            if (DivergeIdx < 0 && Eps > 0 && d > Eps)
            {
                DivergeIdx = n;
                // Log-interpolate the crossing between steps n−1 and n.
                if (n == 0) DivergeT = 0;
                else
                {
                    double l0 = Math.Log(Math.Max(PrevD, LogFloor)), l1 = Math.Log(d), le = Math.Log(Eps);
                    double f = l1 > l0 ? Math.Clamp((le - l0) / (l1 - l0), 0.0, 1.0) : 1.0;
                    DivergeT = n - 1 + f;
                }
            }
            PrevD = d;
        }

        public void Stop(double zx, double zy, double cx, double cy)
        {
            Stopped = true;
            double mx = 0.5 * (zx + cx), my = 0.5 * (zy + cy);
            double ex = 0.5 * (zx - cx), ey = 0.5 * (zy - cy);
            double m2 = mx * mx + my * my;
            MidPert = m2 > LogFloor ? (ex * ex + ey * ey) / m2 : double.PositiveInfinity;
        }
    }

    private static (double Log, double Arg) LogArg(double x, double y)
    {
        double m = Math.Sqrt(x * x + y * y);
        if (!double.IsFinite(m)) return (double.NaN, double.NaN);
        return (Math.Log(Math.Max(m, LogFloor)), Math.Atan2(y, x));
    }

    // Lockstep z / c iteration. The orbit outcomes (oz, oc, nZ, nC, args) are
    // produced by the same expressions, in the same order, as Run — so a pair
    // render caches exactly what the plain render would.
    private static void RunPair(double z0x, double z0y, double c0x, double c0y, double sx, double sy,
        int maxIter, in Bailout b, Span<double> argsZ, Span<double> argsC, bool cSeedIsS,
        ref PairAccum acc, out Orbit oz, out int nZ, out Orbit oc, out int nC)
    {
        bool deriv = (acc.Ch & DualOrbitPairChannels.Derivatives) != 0;
        bool trackZ = !argsZ.IsEmpty, trackC = !argsC.IsEmpty;
        double zx = z0x, zy = z0y, cx = c0x, cy = c0y;
        double dzx = 0, dzy = 0;                        // dz/ds,  z0 = 0 independent of s
        double dcx = cSeedIsS ? 1 : 0, dcy = 0;         // dc/ds,  c0 = s in the c = s control
        double gx = 1, gy = 0;                          // dc/dc0
        bool zAlive = true, cAlive = true;
        oz = default; oc = default; nZ = -1; nC = -1;

        for (int n = 0; n < maxIter && (zAlive || cAlive); n++)
        {
            if (zAlive && trackZ) argsZ[n] = Math.Atan2(zy, zx);
            if (cAlive && trackC) argsC[n] = Math.Atan2(cy, cx);
            double zx2 = zx * zx, zy2 = zy * zy, zr2 = zx2 + zy2;
            double cx2 = cx * cx, cy2 = cy * cy, cr2 = cx2 + cy2;
            bool zEsc = zAlive && zr2 > b.R2, cEsc = cAlive && cr2 > b.R2;

            if (!acc.Stopped)
            {
                if (zEsc || cEsc || !zAlive || !cAlive) acc.Stop(zx, zy, cx, cy);
                else acc.Step(n, zx, zy, cx, cy);
            }

            if (zEsc)
            {
                double logZn = Math.Log(zr2) * 0.5;
                double nu = Math.Log(logZn / b.LogR) / Math.Log(2.0);
                oz = new Orbit(true, zx, zy, n - nu); nZ = n; zAlive = false;
                if (deriv)
                {
                    acc.LogAbsEz = logZn;
                    (acc.LogDz, acc.ArgDz) = LogArg(dzx, dzy);
                }
            }
            if (cEsc)
            {
                double logCn = Math.Log(cr2) * 0.5;
                double nu = Math.Log(logCn / b.LogR) / Math.Log(2.0);
                oc = new Orbit(true, cx, cy, n - nu); nC = n; cAlive = false;
                if (deriv)
                {
                    acc.LogAbsEc = logCn;
                    (acc.LogDcS, acc.ArgDcS) = LogArg(dcx, dcy);
                    if (!cSeedIsS) (acc.LogDcC, acc.ArgDcC) = LogArg(gx, gy);
                }
            }

            if (zAlive)
            {
                if (deriv)
                {   // z' ← 2·z·z' + 1
                    double ndx = 2.0 * (zx * dzx - zy * dzy) + 1.0;
                    dzy = 2.0 * (zx * dzy + zy * dzx);
                    dzx = ndx;
                }
                double nzx = zx2 - zy2 + sx;
                zy = 2.0 * zx * zy + sy;
                zx = nzx;
            }
            if (cAlive)
            {
                if (deriv)
                {   // c' ← 2·c·c' + 1 (wrt s);  g ← 2·c·g (wrt c0)
                    double ndx = 2.0 * (cx * dcx - cy * dcy) + 1.0;
                    dcy = 2.0 * (cx * dcy + cy * dcx);
                    dcx = ndx;
                    double ngx = 2.0 * (cx * gx - cy * gy);
                    gy = 2.0 * (cx * gy + cy * gx);
                    gx = ngx;
                }
                double ncx = cx2 - cy2 + sx;
                cy = 2.0 * cx * cy + sy;
                cx = ncx;
            }
        }
        if (zAlive) oz = new Orbit(false, zx, zy, maxIter);
        if (cAlive) oc = new Orbit(false, cx, cy, maxIter);
        if (!acc.Stopped) acc.Stop(zx, zy, cx, cy);
    }

    // Lockstep quaternion iteration (Hamilton square, as RunQuat). The product
    // identity fails (non-commutative), so the secant sum is the telescoped
    // log|D_N| − log|D_0|; winding / itinerary / derivatives stay NaN.
    private static void RunQuatPair(double c0x, double c0y, double c0z, double c0w,
        double kx, double ky, double kz, double kw, int maxIter, in Bailout b,
        ref PairAccum acc, out QOrbit oz, out QOrbit oc)
    {
        double ax = 0, ay = 0, az = 0, aw = 0;                 // z-orbit (seed 0)
        double qx = c0x, qy = c0y, qz = c0z, qw = c0w;         // c-orbit
        bool zAlive = true, cAlive = true;
        oz = default; oc = default;
        double logD0 = Math.Log(Math.Max(Norm4(qx, qy, qz, qw), LogFloor));

        for (int n = 0; n < maxIter && (zAlive || cAlive); n++)
        {
            double zr2 = ax * ax + ay * ay + az * az + aw * aw;
            double cr2 = qx * qx + qy * qy + qz * qz + qw * qw;
            bool zEsc = zAlive && zr2 > b.R2, cEsc = cAlive && cr2 > b.R2;
            double d = Math.Max(Norm4(qx - ax, qy - ay, qz - az, qw - aw),
                RoundOffRel * Math.Sqrt(zr2 + cr2));
            if (!acc.Stopped)
            {
                if (zEsc || cEsc || !zAlive || !cAlive) StopQ(ref acc, n, d, logD0, ax, ay, az, aw, qx, qy, qz, qw);
                else acc.Separation(n, d);
            }
            if (zEsc)
            {
                double logZn = Math.Log(zr2) * 0.5;
                double nu = Math.Log(logZn / b.LogR) / Math.Log(2.0);
                oz = new QOrbit(true, ax, ay, az, aw, n - nu, n); zAlive = false;
            }
            if (cEsc)
            {
                double logZn = Math.Log(cr2) * 0.5;
                double nu = Math.Log(logZn / b.LogR) / Math.Log(2.0);
                oc = new QOrbit(true, qx, qy, qz, qw, n - nu, n); cAlive = false;
            }
            if (zAlive)
            {
                double nx = ax * ax - ay * ay - az * az - aw * aw;
                double ny = 2.0 * ax * ay, nz = 2.0 * ax * az, nw = 2.0 * ax * aw;
                ax = nx + kx; ay = ny + ky; az = nz + kz; aw = nw + kw;
            }
            if (cAlive)
            {
                double nx = qx * qx - qy * qy - qz * qz - qw * qw;
                double ny = 2.0 * qx * qy, nz = 2.0 * qx * qz, nw = 2.0 * qx * qw;
                qx = nx + kx; qy = ny + ky; qz = nz + kz; qw = nw + kw;
            }
        }
        if (zAlive) oz = new QOrbit(false, ax, ay, az, aw, maxIter, -1);
        if (cAlive) oc = new QOrbit(false, qx, qy, qz, qw, maxIter, -1);
        if (!acc.Stopped)
            StopQ(ref acc, maxIter, Norm4(qx - ax, qy - ay, qz - az, qw - aw), logD0, ax, ay, az, aw, qx, qy, qz, qw);

        static double Norm4(double x, double y, double z, double w) => Math.Sqrt(x * x + y * y + z * z + w * w);
    }

    private static void StopQ(ref PairAccum acc, int n, double d, double logD0,
        double ax, double ay, double az, double aw, double qx, double qy, double qz, double qw)
    {
        acc.Stopped = true;
        acc.Steps = n;
        acc.LogSigmaSum = Math.Log(Math.Max(d, LogFloor)) - logD0;
        acc.Winding = double.NaN;
        double mx = 0.5 * (ax + qx), my = 0.5 * (ay + qy), mz = 0.5 * (az + qz), mw = 0.5 * (aw + qw);
        double ex = 0.5 * (ax - qx), ey = 0.5 * (ay - qy), ez = 0.5 * (az - qz), ew = 0.5 * (aw - qw);
        double m2 = mx * mx + my * my + mz * mz + mw * mw;
        // |e²| = |e|² holds for quaternions too (the norm is multiplicative).
        acc.MidPert = m2 > LogFloor ? (ex * ex + ey * ey + ez * ez + ew * ew) / m2 : double.PositiveInfinity;
    }

    // ── Pair-native field scalars (#1116) ────────────────────────────────────
    //
    // Each maps to [LiveFloor, maxIter] like the shipped fields (palette / Relief
    // height). `live == false` means the field has no value at this pixel and
    // the interior colour is used (e.g. a pair that never diverged, or a
    // complex-only field under the quaternion map).
    internal const double ApproachScaleNats = 8.0;      // ClosestApproach: 1 − e^(−depth/8)
    internal const double MidpointDecades = 6.0;        // MidpointPerturbation: ±6 decades

    private static double PairScalar(DualOrbitField field, in PairAccum a, int maxIter,
        double lyapSpan, bool quat, out bool live)
    {
        live = true;
        double v;
        switch (field)
        {
            case DualOrbitField.SecantLyapunov:
            {
                // λ = mean ln|z_k + c_k| over the window; no window (an orbit
                // starts past the bailout) reads as maximally separating.
                double t = a.Steps > 0 ? 0.5 + 0.5 * Math.Clamp(a.LogSigmaSum / a.Steps / lyapSpan, -1.0, 1.0) : 1.0;
                v = t * maxIter;
                break;
            }
            case DualOrbitField.DivergenceTime:
                if (a.DivergeT < 0) { live = false; return 0.0; }
                v = a.DivergeT;
                break;
            case DualOrbitField.ClosestApproach:
            {
                // Approach depth ln(|D_0| / min|D|) ≥ 0, compressed to [0, 1).
                double depth = a.Steps > 0 && a.D0 > 0
                    ? Math.Max(0.0, Math.Log(a.D0 / Math.Max(a.MinSep, LogFloor))) : 0.0;
                v = (1.0 - Math.Exp(-depth / ApproachScaleNats)) * maxIter;
                break;
            }
            case DualOrbitField.ClosestApproachIndex:
                v = a.MinSepIdx;
                break;
            case DualOrbitField.PairWinding:
                if (quat || double.IsNaN(a.Winding)) { live = false; return 0.0; }
                v = 0.5 * maxIter + a.Winding;     // one palette unit per turn, 0 turns mid-palette
                break;
            case DualOrbitField.MidpointPerturbation:
            {
                double r = a.MidPert;
                double t = double.IsNaN(r) ? 0.0
                    : 0.5 + Math.Log10(Math.Max(r, 1e-300)) / (2.0 * MidpointDecades);
                v = Math.Clamp(t, 0.0, 1.0) * maxIter;
                break;
            }
            case DualOrbitField.ItineraryAgreement:
                if (quat) { live = false; return 0.0; }
                v = a.ItinAgree;
                break;
            default:
                live = false; return 0.0;
        }
        return Math.Clamp(v, LiveFloor, maxIter);
    }

    private static void WritePlanes(DualOrbitPairPlanes p, int idx, in PairAccum a, bool quat)
    {
        var ch = p.Channels;
        if ((ch & DualOrbitPairChannels.SecantLogSum) != 0)
        {
            p.SecantLogSum[idx] = (float)a.LogSigmaSum;
            p.PairSteps[idx] = a.Steps;
        }
        if ((ch & DualOrbitPairChannels.Winding) != 0)
            p.WindingTurns[idx] = quat ? float.NaN : (float)a.Winding;
        if ((ch & DualOrbitPairChannels.Separation) != 0)
        {
            p.MinSeparationLog[idx] = (float)Math.Log(Math.Max(a.MinSep, LogFloor));
            p.MinSeparationIndex[idx] = a.MinSepIdx;
            p.DivergenceTime[idx] = (float)a.DivergeT;
            p.InitialSeparationLog[idx] = (float)Math.Log(Math.Max(double.IsNaN(a.D0) ? 0 : a.D0, LogFloor));
        }
        if ((ch & DualOrbitPairChannels.MidpointPerturbation) != 0)
            p.MidpointPerturbation[idx] = (float)a.MidPert;
        if ((ch & DualOrbitPairChannels.Itinerary) != 0)
            p.ItineraryAgreement[idx] = quat ? float.NaN : a.ItinAgree;
        if ((ch & DualOrbitPairChannels.Derivatives) != 0)
        {
            p.LogAbsEscapeZ[idx] = (float)a.LogAbsEz; p.LogAbsEscapeC[idx] = (float)a.LogAbsEc;
            p.LogDzDs[idx] = (float)a.LogDz;   p.ArgDzDs[idx] = (float)a.ArgDz;
            p.LogDcDs[idx] = (float)a.LogDcS;  p.ArgDcDs[idx] = (float)a.ArgDcS;
            p.LogDcDc0[idx] = (float)a.LogDcC; p.ArgDcDc0[idx] = (float)a.ArgDcC;
        }
    }
}
