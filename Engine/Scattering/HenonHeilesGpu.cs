// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// HenonHeilesGpu.cs (#2049, physics epic #2048)
//
// The Hénon-Heiles equations (HenonHeilesSystem, #1942) as an OdeFamily for the GPU ODE
// template (Rendering.D3D/OdeKernelSource): the forces, step cap, exit test and per-trajectory
// channels, term for term with the C# system. Channels: ch[0] turning points (sign changes
// of the radial velocity x px + y py), ch[1] the smallest SQUARED distance to a saddle (the
// host takes the root, as the CPU's monotone minimum of roots is the root of the minimum),
// ch[2] the last nonzero radial velocity. FP(0) = the time limit.
//
// The host side (Trace) computes each launch exactly as HenonHeilesScene.LaunchState and
// labels the exit from the final state with HenonHeilesSystem.ExitOf, so only the
// integration runs on the GPU.

using System;
using System.Collections.Generic;
using System.Threading;

using FracturingFog.Calculators.Gpu;

namespace FracturingFog.Scattering;

public static class HenonHeilesGpu
{
    public const int Channels = 3;

    public static readonly OdeFamily Family = new("HenonHeiles", 4, Channels, BuildHlsl());

    private static string BuildHlsl()
    {
        // Non-dyadic literals go through RL() so the double kernel keeps every digit.
        string s3 = (Math.Sqrt(3.0) / 2.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
REAL HhSaddleD2(REAL x, REAL y)
{
    REAL s3 = RL({{s3}});
    REAL h = (REAL)0.5;
    REAL d0 = (x - (REAL)0) * (x - (REAL)0) + (y - (REAL)1) * (y - (REAL)1);
    REAL d1 = (x + s3) * (x + s3) + (y + h) * (y + h);
    REAL d2 = (x - s3) * (x - s3) + (y + h) * (y + h);
    REAL best = d0;
    if (d1 < best) best = d1;
    if (d2 < best) best = d2;
    return best;
}

void FamDeriv(REAL y[DIM], out REAL dy[DIM])
{
    REAL x = y[0], v = y[1];
    dy[0] = y[2];
    dy[1] = y[3];
    dy[2] = -x - (REAL)2 * x * v;
    dy[3] = -v - x * x + v * v;
}

REAL FamMaxStep(REAL y[DIM]) { return (REAL)0.25; }

void FamInit(REAL y[DIM], inout REAL ch[NCH])
{
    ch[0] = (REAL)0;
    ch[1] = HhSaddleD2(y[0], y[1]);
    ch[2] = y[0] * y[2] + y[1] * y[3];
}

void FamObserve(REAL y[DIM], REAL h, inout REAL ch[NCH])
{
    REAL radial = y[0] * y[2] + y[1] * y[3];
    REAL zero = (REAL)0;
    if ((radial < zero) != (ch[2] < zero) && radial != zero && ch[2] != zero) ch[0] += (REAL)1;
    if (radial != zero) ch[2] = radial;
    REAL d = HhSaddleD2(y[0], y[1]);
    if (d < ch[1]) ch[1] = d;
}

int FamClassify(REAL y[DIM], REAL t, REAL ch[NCH], out int body)
{
    body = -1;
    if (y[0] * y[0] + y[1] * y[1] > (REAL){{HenonHeilesSystem.ExitRadius * HenonHeilesSystem.ExitRadius}}) return 2;
    return t >= FP(0) ? 3 : 0;
}
""";
    }

    /// <summary>Trace the view points of <paramref name="items"/> (pixel indices whose launch is
    /// valid and inside the exit circle) on <paramref name="kernel"/>; null on failure.</summary>
    public static HenonHeilesOutcome[]? Trace(IOdeTraceKernel kernel, HenonHeilesScene scene, IReadOnlyList<int> items,
        Func<int, (double A, double B)> viewPoint, bool single, CancellationToken ct)
    {
        var job = new OdeJob
        {
            Family = Family,
            FamilyParams = new[] { scene.TimeCap },
            RelTol = scene.Settings.RelTol, AbsTol = scene.Settings.AbsTol,
            InitialStep = scene.Settings.InitialStep, MaxSteps = scene.Settings.MaxSteps,
            Count = items.Count, Single = single,
            Launches = (first, n, dst) =>
            {
                Span<double> s = stackalloc double[4];
                for (int i = 0; i < n; i++)
                {
                    var (a, b) = viewPoint(items[first + i]);
                    scene.LaunchState(a, b, s);
                    for (int k = 0; k < 4; k++) dst[i * 4 + k] = s[k];
                }
            },
        };
        var r = kernel.Trace(job, ct);
        if (r == null) return null;
        var outc = new HenonHeilesOutcome[items.Count];
        for (int i = 0; i < outc.Length; i++) outc[i] = OutcomeOf(r, i);
        return outc;
    }

    /// <summary>Item <paramref name="i"/> of a GPU result as the CPU's outcome: the exit labelled from the
    /// final state exactly as HenonHeilesScene.Trace does.</summary>
    public static HenonHeilesOutcome OutcomeOf(OdeResult r, int i)
    {
        double fx = r.Final[i * 4], fy = r.Final[i * 4 + 1];
        return new HenonHeilesOutcome
        {
            Valid = true,
            Exit = r.Fate[i] == (int)ScatterFate.Escaped ? HenonHeilesSystem.ExitOf(fx, fy) : -1,
            Time = r.T[i],
            Turns = (int)r.Channels[i * Channels],
            MinSaddleDistance = Math.Sqrt(Math.Max(0.0, r.Channels[i * Channels + 1])),
            Steps = r.Steps[i],
        };
    }
}
