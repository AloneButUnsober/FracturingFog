// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SpinningBinaryGpu.cs (#2049, physics epic #2048)
//
// The post-Newtonian spinning binary (SpinningBinarySystem, #1945: Kidder, Phys. Rev. D 52, 821,
// 1995, Eqs. 3, 4a-4f and 6a-6b, as transcribed there) as an OdeFamily for the GPU ODE template
// (Rendering.D3D/OdeKernelSource): the relative acceleration with each PN term behind its switch,
// the spin precession, the orbital-angle rate, the step cap and the end tests, term for term and
// in the same order of operations as the C# system (divisions become multiplications by RCP, the
// template's refined reciprocal). Constants: FP(0) m1, FP(1) m2, FP(2..6) the switches PN1,
// SpinOrbit, PN2, SpinSpin, RadiationReaction (1 = on), FP(7) merge radius, FP(8) separation
// radius, FP(9) the orbit-angle cap. The end test returns body 2 for the orbit cap, so the host
// tells it apart from a collapsed step (the CPU's End = 2 vs -1).

using System;

using FracturingFog.Calculators.Gpu;

namespace FracturingFog.Scattering;

public static class SpinningBinaryGpu
{
    public static readonly OdeFamily Family = new("SpinningBinary", SpinningBinarySystem.StateSize, 0, """
void FamDeriv(REAL y[DIM], out REAL dy[DIM])
{
    REAL m1 = FP(0), m2 = FP(1), m = m1 + m2;
    REAL im = RCP(m);
    REAL mu = m1 * m2 * im, eta = mu * im, dmm = (m1 - m2) * im;
    REAL x = y[0], yy = y[1], z = y[2], vx = y[3], vy = y[4], vz = y[5];
    REAL r2 = x * x + yy * yy + z * z, r = SQRT(r2);
    REAL ir = RCP(r);
    REAL nx = x * ir, ny = yy * ir, nz = z * ir;
    REAL v2 = vx * vx + vy * vy + vz * vz;
    REAL rd = nx * vx + ny * vy + nz * vz;
    REAL mr = m * ir;
    REAL mr2 = -m * RCP(r2);
    // a_N (4a)
    REAL cn = mr2, cv = RL(0.0);
    if (FP(2) > RL(0.5))   // (4b)
    {
        cn += mr2 * ((RL(1.0) + RL(3.0) * eta) * v2 - RL(2.0) * (RL(2.0) + eta) * mr - RL(1.5) * eta * rd * rd);
        cv += mr2 * (-RL(2.0) * (RL(2.0) - eta) * rd);
    }
    if (FP(4) > RL(0.5))   // (4d)
    {
        cn += mr2 * (RL(0.75) * (RL(12.0) + RL(29.0) * eta) * mr * mr + eta * (RL(3.0) - RL(4.0) * eta) * v2 * v2
                     + RL(1.875) * eta * (RL(1.0) - RL(3.0) * eta) * rd * rd * rd * rd
                     - RL(1.5) * eta * (RL(3.0) - RL(4.0) * eta) * v2 * rd * rd
                     - RL(0.5) * eta * (RL(13.0) - RL(4.0) * eta) * mr * v2
                     - (RL(2.0) + RL(25.0) * eta + RL(2.0) * eta * eta) * mr * rd * rd);
        cv += mr2 * (-RL(0.5) * rd * (eta * (RL(15.0) + RL(4.0) * eta) * v2 - (RL(4.0) + RL(41.0) * eta + RL(8.0) * eta * eta) * mr
                                      - RL(3.0) * eta * (RL(3.0) + RL(2.0) * eta) * rd * rd));
    }
    if (FP(6) > RL(0.5))   // (4f)
    {
        REAL k = RL(1.6) * eta * m * m * RCP(r2 * r);
        cn += k * rd * (RL(18.0) * v2 + RL(0.66666666666666663) * mr - RL(25.0) * rd * rd);
        cv += -k * (RL(6.0) * v2 - RL(2.0) * mr - RL(15.0) * rd * rd);
    }
    REAL ax = cn * nx + cv * vx, ay = cn * ny + cv * vy, az = cn * nz + cv * vz;
    REAL s1x = y[6], s1y = y[7], s1z = y[8], s2x = y[9], s2y = y[10], s2z = y[11];
    REAL r3 = r2 * r;
    REAL ir3 = RCP(r3);
    bool so = FP(3) > RL(0.5), ss = FP(5) > RL(0.5);
    REAL im1 = RCP(m1), im2 = RCP(m2);
    if (so)   // (4c)
    {
        REAL sx = s1x + s2x, sy = s1y + s2y, sz = s1z + s2z;
        REAL dx = m * (s2x * im2 - s1x * im1), dyv = m * (s2y * im2 - s1y * im1), dz = m * (s2z * im2 - s1z * im1);
        REAL nvx = ny * vz - nz * vy, nvy = nz * vx - nx * vz, nvz = nx * vy - ny * vx;
        REAL t1 = nvx * (RL(2.0) * sx + dmm * dx) + nvy * (RL(2.0) * sy + dmm * dyv) + nvz * (RL(2.0) * sz + dmm * dz);
        REAL ux = RL(7.0) * sx + RL(3.0) * dmm * dx, uy = RL(7.0) * sy + RL(3.0) * dmm * dyv, uz = RL(7.0) * sz + RL(3.0) * dmm * dz;
        REAL wx = RL(3.0) * sx + dmm * dx, wy = RL(3.0) * sy + dmm * dyv, wz = RL(3.0) * sz + dmm * dz;
        ax += (RL(6.0) * nx * t1 - (vy * uz - vz * uy) + RL(3.0) * rd * (ny * wz - nz * wy)) * ir3;
        ay += (RL(6.0) * ny * t1 - (vz * ux - vx * uz) + RL(3.0) * rd * (nz * wx - nx * wz)) * ir3;
        az += (RL(6.0) * nz * t1 - (vx * uy - vy * ux) + RL(3.0) * rd * (nx * wy - ny * wx)) * ir3;
    }
    REAL ns1 = nx * s1x + ny * s1y + nz * s1z, ns2 = nx * s2x + ny * s2y + nz * s2z;
    if (ss)   // (4e)
    {
        REAL s12 = s1x * s2x + s1y * s2y + s1z * s2z;
        REAL k = -RL(3.0) * RCP(mu * r3 * r);
        ax += k * (nx * s12 + s1x * ns2 + s2x * ns1 - RL(5.0) * nx * ns1 * ns2);
        ay += k * (ny * s12 + s1y * ns2 + s2y * ns1 - RL(5.0) * ny * ns1 * ns2);
        az += k * (nz * s12 + s1z * ns2 + s2z * ns1 - RL(5.0) * nz * ns1 * ns2);
    }
    dy[0] = vx; dy[1] = vy; dy[2] = vz;
    dy[3] = ax; dy[4] = ay; dy[5] = az;
    // Spin precession (6a)-(6b).
    REAL lx = mu * (yy * vz - z * vy), ly = mu * (z * vx - x * vz), lz = mu * (x * vy - yy * vx);
    REAL c1 = so ? (RL(2.0) + RL(1.5) * m2 * im1) : RL(0.0);
    REAL c2 = so ? (RL(2.0) + RL(1.5) * m1 * im2) : RL(0.0);
    REAL o1x = c1 * lx, o1y = c1 * ly, o1z = c1 * lz;
    REAL o2x = c2 * lx, o2y = c2 * ly, o2z = c2 * lz;
    if (ss)
    {
        o1x += -s2x + RL(3.0) * ns2 * nx; o1y += -s2y + RL(3.0) * ns2 * ny; o1z += -s2z + RL(3.0) * ns2 * nz;
        o2x += -s1x + RL(3.0) * ns1 * nx; o2y += -s1y + RL(3.0) * ns1 * ny; o2z += -s1z + RL(3.0) * ns1 * nz;
    }
    dy[6] = (o1y * s1z - o1z * s1y) * ir3;
    dy[7] = (o1z * s1x - o1x * s1z) * ir3;
    dy[8] = (o1x * s1y - o1y * s1x) * ir3;
    dy[9] = (o2y * s2z - o2z * s2y) * ir3;
    dy[10] = (o2z * s2x - o2x * s2z) * ir3;
    dy[11] = (o2x * s2y - o2y * s2x) * ir3;
    // Orbital angle rate |x cross v| / r^2.
    REAL cx = yy * vz - z * vy, cy = z * vx - x * vz, cz = x * vy - yy * vx;
    dy[12] = SQRT(cx * cx + cy * cy + cz * cz) * RCP(r2);
}

REAL FamMaxStep(REAL y[DIM])
{
    REAL r = SQRT(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]);
    return max(RL(1e-6), RL(0.05) * SQRT(r * r * r * RCP(FP(0) + FP(1))));
}

void FamInit(REAL y[DIM], inout REAL ch[NCH]) { }
void FamObserve(REAL y[DIM], REAL h, inout REAL ch[NCH]) { }

int FamClassify(REAL y[DIM], REAL t, REAL ch[NCH], out int body)
{
    body = -1;
    REAL r = SQRT(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]);
    if (r < FP(7)) { body = 0; return 1; }
    if (r > FP(8)) { body = 1; return 2; }
    if (y[12] >= FP(9)) { body = 2; return 3; }
    return 0;
}
""");

    /// <summary>The job settings of a frame.</summary>
    public static OdeJobTemplate Job(SpinningBinaryScene scene, bool single)
    {
        var sys = scene.System();
        double On(PnTerms f) => (scene.Terms & f) != 0 ? 1.0 : 0.0;
        return new OdeJobTemplate(Family,
            new[] { scene.M1, scene.M2, On(PnTerms.PN1), On(PnTerms.SpinOrbit), On(PnTerms.PN2), On(PnTerms.SpinSpin),
                    On(PnTerms.RadiationReaction), scene.MergeRadius, scene.SeparateRadius, sys.MaxOrbitAngle },
            scene.Settings.RelTol, scene.Settings.AbsTol, scene.Settings.InitialStep, scene.Settings.MaxSteps, single);
    }

    /// <summary>Item <paramref name="i"/> of a GPU result as the CPU's outcome (SpinningBinaryScene.Trace's gate rule).</summary>
    public static SpinningBinaryOutcome OutcomeOf(SpinningBinaryScene scene, OdeResult r, int i)
    {
        const int n = SpinningBinarySystem.StateSize;
        int end = r.Fate[i] switch
        {
            (int)ScatterFate.Captured => 0,
            (int)ScatterFate.Escaped => 1,
            (int)ScatterFate.Undecided when r.Body[i] == 2 => 2,
            _ => -1,
        };
        int gate = end;
        if (scene.Mode == SpinningBinaryOutcomeMode.MergerSide)
            gate = end switch { 0 => r.Final[i * n + 2] >= 0 ? 0 : 1, 1 => 2, _ => -1 };
        return new SpinningBinaryOutcome { Gate = gate, Orbits = r.Final[i * n + 12] / (2 * Math.PI), Time = r.T[i], Steps = r.Steps[i] };
    }
}
