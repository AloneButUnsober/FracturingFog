// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ThreeBodyGpu.cs (#2049, physics epic #2048)
//
// The planar circular restricted three-body problem (ThreeBodySystem, #1943) as an OdeFamily
// for the GPU ODE template (Rendering.D3D/OdeKernelSource): the rotating-frame equations
// ẍ − 2ẏ = ∂Ω/∂x, ÿ + 2ẋ = ∂Ω/∂y, the step cap near the primaries and the fate tests (crash
// into primary 1 or 2, escape, time limit), term for term with the C# system. Constants:
// FP(0) μ, FP(1) crash radius, FP(2) escape radius, FP(3) time limit. No channels. The Fates
// view only: the Lagrange view is a Newton map, not an ODE, and stays on the CPU.

using System;

using FracturingFog.Calculators.Gpu;

namespace FracturingFog.Scattering;

public static class ThreeBodyGpu
{
    public static readonly OdeFamily Family = new("ThreeBody", 4, 0, """
void TbGrad(REAL x, REAL y, out REAL ox, out REAL oy)
{
    REAL mu = FP(0);
    REAL dx1 = x + mu, dx2 = x - RL(1.0) + mu;
    REAL r1s = dx1 * dx1 + y * y, r2s = dx2 * dx2 + y * y;
    REAL r13 = r1s * SQRT(r1s), r23 = r2s * SQRT(r2s);
    REAL a = (RL(1.0) - mu) * RCP(r13), b = mu * RCP(r23);
    ox = x - a * dx1 - b * dx2;
    oy = y - a * y - b * y;
}

void FamDeriv(REAL s[DIM], out REAL ds[DIM])
{
    REAL ox, oy;
    TbGrad(s[0], s[1], ox, oy);
    ds[0] = s[2];
    ds[1] = s[3];
    ds[2] = ox + RL(2.0) * s[3];
    ds[3] = oy - RL(2.0) * s[2];
}

REAL FamMaxStep(REAL s[DIM])
{
    REAL mu = FP(0), x = s[0], y = s[1];
    REAL r1 = SQRT((x + mu) * (x + mu) + y * y);
    REAL r2 = SQRT((x - RL(1.0) + mu) * (x - RL(1.0) + mu) + y * y);
    REAL r = min(r1, r2);
    return clamp(RL(0.1) * r, RL(1e-9), RL(0.1));
}

void FamInit(REAL s[DIM], inout REAL ch[NCH]) { }
void FamObserve(REAL s[DIM], REAL h, inout REAL ch[NCH]) { }

int FamClassify(REAL s[DIM], REAL t, REAL ch[NCH], out int body)
{
    body = -1;
    REAL mu = FP(0), x = s[0], y = s[1];
    REAL r1 = SQRT((x + mu) * (x + mu) + y * y);
    REAL r2 = SQRT((x - RL(1.0) + mu) * (x - RL(1.0) + mu) + y * y);
    if (r1 < FP(1)) { body = 0; return 1; }
    if (r2 < FP(1)) { body = 1; return 1; }
    if (x * x + y * y > FP(2) * FP(2)) return 2;
    if (t >= FP(3)) return 3;
    return 0;
}
""");

    /// <summary>The job settings of a Fates frame.</summary>
    public static OdeJobTemplate Job(ThreeBodyScene scene, bool single) => new(Family,
        new[] { scene.Mu, scene.CrashRadius, scene.EscapeRadius, scene.TimeCap },
        scene.Settings.RelTol, scene.Settings.AbsTol, scene.Settings.InitialStep, scene.Settings.MaxSteps, single);

    /// <summary>Item <paramref name="i"/> of a GPU result as the CPU's outcome (ThreeBodyScene.Trace's gate rule).</summary>
    public static ThreeBodyOutcome OutcomeOf(OdeResult r, int i)
    {
        int gate = r.Fate[i] switch
        {
            (int)ScatterFate.Captured => r.Body[i],
            (int)ScatterFate.Escaped => ThreeBodySystem.EscapeGate,
            _ => -1,
        };
        return new ThreeBodyOutcome { Valid = true, Gate = gate, Time = r.T[i], Steps = r.Steps[i] };
    }
}
