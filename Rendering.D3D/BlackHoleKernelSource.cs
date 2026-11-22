// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.D3D/BlackHoleKernelSource.cs (#1972, black hole shadows epic #1922)
//
// HLSL twin of BlackHoleShadowGpuCalculator.TraceKernel (ILGPU, #1926) and of the CPU trace
// (ScatteringIntegrator.Trace over MpNullSystem, channels off): the same Dormand–Prince 5(4)
// stages and step controller, the same Majumdar–Papapetrou field, camera launch, local step
// cap and exit tests, term for term. One dependency-free source, two compilers: FXC (cs_5_0,
// D3D11, BlackHoleD3D11Kernel) and DXC (cs_6_0 → SPIR-V, Vulkan, BlackHoleVulkanKernel; the
// file is linked into Rendering.Vulkan as the Mandelbrot / Buddha / froxel sources are).
//
// REAL is double (the default trace, like the CPU) or float (the fast single-precision option,
// tolerance floored at BlackHoleShadowGpuCalculator.SingleMinRelTol on the host).
//
// Shader-model limits and their handling:
//  • cs_5_0 doubles have add / mul / compare but no divide, sqrt or pow. RCP and SQRT therefore
//    refine a float seed by Newton's method in double (two reciprocal steps: 24 → 48 → 96 bits;
//    three inverse-square-root steps and one final sqrt step). They agree with IEEE division and
//    square root to the last unit or two, so per-pixel outcomes match the CPU except at chaotic
//    edge pixels, where an ulp moves the fate (bounded by the tests, as the ILGPU float kernel's).
//  • The controller's err^(-1/5) starts from a float pow and is refined by two Newton steps in
//    double (PowM02), so the double kernel's step sequence follows the CPU's Math.Pow.
//  • Double literals carry the L suffix; the Dormand–Prince coefficients are generated here
//    with full precision (the same values as ScatteringIntegrator's constants).
//
// Buffers: t0 = parameters (StructuredBuffer<REAL>, layout below), u0 = packed fate + steps
// (uint: (gate + 1) | steps << 3), u1 = per-pixel data (delay, final px, py, pz; two uints per
// double). The cbuffer carries the integers and the row band [BandStart, BandEnd) of pixel
// indices this dispatch traces (TDR-safe banded dispatch).

using System.Globalization;

namespace FracturingFog.Rendering;

public static class BlackHoleKernelSource
{
    public const string Entry = "BhTrace";
    public const int GroupSize = 64;

    /// <summary>Index of each parameter in the t0 buffer.</summary>
    public const int PCenterX = 0, PCenterY = 1, PPitch = 2, PHoles = 3 /* 5 per hole: x y z m cap² */,
                     PEscape2 = 23, PCam = 24 /* cam xyz, f xyz, r xyz, u xyz */, PDistance = 36,
                     PRelTol = 37, PAbsTol = 38, PInitialStep = 39, ParamCount = 40;

    private static string D(double v, bool dbl) => dbl
        ? v.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e") + (v.ToString("R", CultureInfo.InvariantCulture).Contains('.') || v.ToString("R", CultureInfo.InvariantCulture).Contains('E') ? "L" : ".0L")
        : ((float)v).ToString("R", CultureInfo.InvariantCulture).Replace("E", "e") + (((float)v).ToString("R", CultureInfo.InvariantCulture).Contains('.') || ((float)v).ToString("R", CultureInfo.InvariantCulture).Contains('E') ? "f" : ".0f");

    /// <summary>The kernel source for double (<paramref name="dbl"/>) or float.</summary>
    public static string Build(bool dbl)
    {
        string c(double v) => D(v, dbl);
        string R = dbl ? "double" : "float";
        return $$"""
#define REAL {{R}}
#define DBL {{(dbl ? 1 : 0)}}

cbuffer BhInts : register(b0)
{
    uint Width; uint Height; uint HoleCount; uint Pinhole;
    uint MaxSteps; uint BandStart; uint BandEnd; uint Pad0;
};

StructuredBuffer<REAL> P : register(t0);
RWStructuredBuffer<uint> Packed : register(u0);
RWStructuredBuffer<uint> Data : register(u1);

#if DBL
REAL RCP(REAL a) { REAL x = (REAL)(1.0f / (float)a); x = x * (2.0L - a * x); x = x * (2.0L - a * x); return x; }
// err^(-1/5) for the step controller, refined in double: Newton on y^5 err = 1 from the float value
// (y <- 4y/5 + 1/(5 y^4 err)), so the step sequence follows the CPU's Math.Pow, not a float pow.
REAL PowM02(REAL e)
{
    REAL y = (REAL)pow((float)e, -0.2f);
    [unroll] for (int i = 0; i < 2; i++) { REAL y2 = y * y; y = 0.8L * y + 0.2L * RCP(y2 * y2 * e); }
    return y;
}
REAL SQRT(REAL a)
{
    if (!(a > 0.0L)) return 0.0L;
    REAL y = (REAL)rsqrt((float)a);
    y = y * (1.5L - 0.5L * a * y * y);
    y = y * (1.5L - 0.5L * a * y * y);
    y = y * (1.5L - 0.5L * a * y * y);
    REAL s = a * y;
    return s + 0.5L * (a - s * s) * y;
}
#else
REAL RCP(REAL a) { return 1.0f / a; }
REAL PowM02(REAL e) { return pow(e, -0.2f); }
REAL SQRT(REAL a) { return sqrt(a); }
#endif

struct S6 { REAL X, Y, Z, Px, Py, Pz; };

void AddHole(REAL dx, REAL dy, REAL dz, REAL m, inout REAL u, inout REAL gx, inout REAL gy, inout REAL gz)
{
    if (m == {{c(0)}}) return;
    REAL r2 = dx * dx + dy * dy + dz * dz;
    REAL r = SQRT(r2);
    REAL ir = RCP(r);
    u += m * ir;
    REAL k = -m * ir * ir * ir;
    gx += k * dx; gy += k * dy; gz += k * dz;
}

REAL Hole(int i, int k) { return P[{{PHoles}} + i * 5 + k]; }

REAL Potential(REAL x, REAL y, REAL z, out REAL gx, out REAL gy, out REAL gz)
{
    REAL u = {{c(1)}}; gx = {{c(0)}}; gy = {{c(0)}}; gz = {{c(0)}};
    [loop] for (int i = 0; i < 4; i++)
    {
        if (i >= (int)HoleCount && i > 0) break;
        AddHole(x - Hole(i, 0), y - Hole(i, 1), z - Hole(i, 2), Hole(i, 3), u, gx, gy, gz);
    }
    return u;
}

S6 Deriv(S6 s)
{
    REAL gx, gy, gz;
    REAL u = Potential(s.X, s.Y, s.Z, gx, gy, gz);
    REAL iu2 = RCP(u * u);
    REAL p2 = s.Px * s.Px + s.Py * s.Py + s.Pz * s.Pz;
    REAL f = u + p2 * iu2 * RCP(u);
    S6 d;
    d.X = s.Px * iu2; d.Y = s.Py * iu2; d.Z = s.Pz * iu2;
    d.Px = gx * f; d.Py = gy * f; d.Pz = gz * f;
    return d;
}

REAL Dist2(int i, REAL x, REAL y, REAL z)
{
    REAL dx = x - Hole(i, 0), dy = y - Hole(i, 1), dz = z - Hole(i, 2);
    return dx * dx + dy * dy + dz * dz;
}

REAL MaxStepOf(S6 s)
{
    REAL nearD = Dist2(0, s.X, s.Y, s.Z);
    [loop] for (int i = 1; i < (int)HoleCount; i++) nearD = min(nearD, Dist2(i, s.X, s.Y, s.Z));
    REAL r = SQRT(nearD);
    return max({{c(1e-9)}}, min({{c(0.25)}} * r + {{c(1e-6)}}, {{c(4.0)}} + {{c(0.05)}} * r));
}

// 0 running, 1 captured (body), 2 escaped.
int Classify(S6 s, out int body)
{
    [loop] for (int i = 0; i < (int)HoleCount; i++)
    {
        if (Dist2(i, s.X, s.Y, s.Z) < Hole(i, 4)) { body = i; return 1; }
    }
    body = -1;
    REAL r2 = s.X * s.X + s.Y * s.Y + s.Z * s.Z;
    if (r2 > P[{{PEscape2}}] && s.X * s.Px + s.Y * s.Py + s.Z * s.Pz > {{c(0)}}) return 2;
    return 0;
}

S6 Stage(S6 s, REAL h, S6 a, REAL ca, S6 b, REAL cb, S6 cc_, REAL cc, S6 d, REAL cd, S6 e, REAL ce)
{
    S6 r;
    r.X = s.X + h * (ca * a.X + cb * b.X + cc * cc_.X + cd * d.X + ce * e.X);
    r.Y = s.Y + h * (ca * a.Y + cb * b.Y + cc * cc_.Y + cd * d.Y + ce * e.Y);
    r.Z = s.Z + h * (ca * a.Z + cb * b.Z + cc * cc_.Z + cd * d.Z + ce * e.Z);
    r.Px = s.Px + h * (ca * a.Px + cb * b.Px + cc * cc_.Px + cd * d.Px + ce * e.Px);
    r.Py = s.Py + h * (ca * a.Py + cb * b.Py + cc * cc_.Py + cd * d.Py + ce * e.Py);
    r.Pz = s.Pz + h * (ca * a.Pz + cb * b.Pz + cc * cc_.Pz + cd * d.Pz + ce * e.Pz);
    return r;
}

REAL Ratio(REAL h, REAL k1, REAL k3, REAL k4, REAL k5, REAL k6, REAL k7, REAL y0, REAL y1, REAL rtol, REAL atol)
{
    REAL e = h * ({{c(71.0 / 57600.0)}} * k1 + {{c(-71.0 / 16695.0)}} * k3 + {{c(71.0 / 1920.0)}} * k4
               + {{c(-17253.0 / 339200.0)}} * k5 + {{c(22.0 / 525.0)}} * k6 + {{c(-1.0 / 40.0)}} * k7);
    REAL sc = atol + rtol * max(abs(y0), abs(y1));
    return abs(e) * RCP(sc);
}

bool Finite(REAL v) { return v - v == {{c(0)}}; }

void StoreData(uint o, REAL v, uint k)
{
#if DBL
    uint lo, hi;
    asuint(v, lo, hi);
    Data[o * 8 + k * 2] = lo;
    Data[o * 8 + k * 2 + 1] = hi;
#else
    Data[o * 4 + k] = asuint(v);
#endif
}

[numthreads({{GroupSize}}, 1, 1)]
void {{Entry}}(uint3 dt : SV_DispatchThreadID)
{
    uint idx = BandStart + dt.x;
    if (idx >= BandEnd) return;
    uint px = idx % Width;
    uint py = idx / Width;

    // BlackHoleShadowCalculator.PlaneAt(x + 0.5, y + 0.5, ...): v is the negated plane y.
    // SM5 doubles convert only from float (ftod), so integers go through float (exact below 2^24).
    REAL u = P[{{PCenterX}}] + ((REAL)(float)px + {{c(0.5)}} - (REAL)(float)Width * {{c(0.5)}}) * P[{{PPitch}}];
    REAL v = -(P[{{PCenterY}}] + ((REAL)(float)py + {{c(0.5)}} - (REAL)(float)Height * {{c(0.5)}}) * P[{{PPitch}}]);

    REAL camX = P[{{PCam}}], camY = P[{{PCam + 1}}], camZ = P[{{PCam + 2}}];
    REAL fx = P[{{PCam + 3}}], fy = P[{{PCam + 4}}], fz = P[{{PCam + 5}}];
    REAL rx = P[{{PCam + 6}}], ry = P[{{PCam + 7}}], rz = P[{{PCam + 8}}];
    REAL ux = P[{{PCam + 9}}], uy = P[{{PCam + 10}}], uz = P[{{PCam + 11}}];
    REAL ox, oy, oz, dx, dy, dz;
    if (Pinhole != 0)
    {
        REAL dist = P[{{PDistance}}];
        ox = camX; oy = camY; oz = camZ;
        dx = dist * fx + u * rx + v * ux;
        dy = dist * fy + u * ry + v * uy;
        dz = dist * fz + u * rz + v * uz;
        REAL il = RCP(SQRT(dx * dx + dy * dy + dz * dz));
        dx *= il; dy *= il; dz *= il;
    }
    else
    {
        ox = camX + u * rx + v * ux; oy = camY + u * ry + v * uy; oz = camZ + u * rz + v * uz;
        dx = fx; dy = fy; dz = fz;
    }
    REAL g0, g1, g2;
    REAL uu = Potential(ox, oy, oz, g0, g1, g2);
    REAL pm = uu * uu;
    S6 s;
    s.X = ox; s.Y = oy; s.Z = oz; s.Px = pm * dx; s.Py = pm * dy; s.Pz = pm * dz;

    const REAL A21 = {{c(1.0 / 5.0)}};
    const REAL A31 = {{c(3.0 / 40.0)}}, A32 = {{c(9.0 / 40.0)}};
    const REAL A41 = {{c(44.0 / 45.0)}}, A42 = {{c(-56.0 / 15.0)}}, A43 = {{c(32.0 / 9.0)}};
    const REAL A51 = {{c(19372.0 / 6561.0)}}, A52 = {{c(-25360.0 / 2187.0)}}, A53 = {{c(64448.0 / 6561.0)}}, A54 = {{c(-212.0 / 729.0)}};
    const REAL A61 = {{c(9017.0 / 3168.0)}}, A62 = {{c(-355.0 / 33.0)}}, A63 = {{c(46732.0 / 5247.0)}}, A64 = {{c(49.0 / 176.0)}}, A65 = {{c(-5103.0 / 18656.0)}};
    const REAL B1 = {{c(35.0 / 384.0)}}, B3 = {{c(500.0 / 1113.0)}}, B4 = {{c(125.0 / 192.0)}}, B5 = {{c(-2187.0 / 6784.0)}}, B6 = {{c(11.0 / 84.0)}};

    int maxSteps = (int)MaxSteps;
    REAL h = min(P[{{PInitialStep}}], MaxStepOf(s));
    REAL lambda = {{c(0)}}, delay = {{c(0)}};
    int steps = 0, attempts = 0, attemptCap = maxSteps * 8;
    int fate = 3, body = -1;
    S6 k1 = Deriv(s);
    S6 zero = (S6)0;
    REAL rt = P[{{PRelTol}}], at = P[{{PAbsTol}}];
    [loop] while (steps < maxSteps && attempts < attemptCap)
    {
        attempts++;
        REAL hMax = MaxStepOf(s);
        if (h > hMax) h = hMax;
        if (!(h > {{c(0)}}) || !Finite(h)) break;

        S6 k2 = Deriv(Stage(s, h, k1, A21, zero, {{c(0)}}, zero, {{c(0)}}, zero, {{c(0)}}, zero, {{c(0)}}));
        S6 k3 = Deriv(Stage(s, h, k1, A31, k2, A32, zero, {{c(0)}}, zero, {{c(0)}}, zero, {{c(0)}}));
        S6 k4 = Deriv(Stage(s, h, k1, A41, k2, A42, k3, A43, zero, {{c(0)}}, zero, {{c(0)}}));
        S6 k5 = Deriv(Stage(s, h, k1, A51, k2, A52, k3, A53, k4, A54, zero, {{c(0)}}));
        S6 k6 = Deriv(Stage(s, h, k1, A61, k2, A62, k3, A63, k4, A64, k5, A65));
        S6 yn = Stage(s, h, k1, B1, k3, B3, k4, B4, k5, B5, k6, B6);
        S6 k7 = Deriv(yn);

        REAL e0 = Ratio(h, k1.X, k3.X, k4.X, k5.X, k6.X, k7.X, s.X, yn.X, rt, at);
        REAL e1 = Ratio(h, k1.Y, k3.Y, k4.Y, k5.Y, k6.Y, k7.Y, s.Y, yn.Y, rt, at);
        REAL e2 = Ratio(h, k1.Z, k3.Z, k4.Z, k5.Z, k6.Z, k7.Z, s.Z, yn.Z, rt, at);
        REAL e3 = Ratio(h, k1.Px, k3.Px, k4.Px, k5.Px, k6.Px, k7.Px, s.Px, yn.Px, rt, at);
        REAL e4 = Ratio(h, k1.Py, k3.Py, k4.Py, k5.Py, k6.Py, k7.Py, s.Py, yn.Py, rt, at);
        REAL e5 = Ratio(h, k1.Pz, k3.Pz, k4.Pz, k5.Pz, k6.Pz, k7.Pz, s.Pz, yn.Pz, rt, at);
        bool finite = Finite(e0) && Finite(e1) && Finite(e2) && Finite(e3) && Finite(e4) && Finite(e5);
        REAL err = max(max(max(e0, e1), max(e2, e3)), max(e4, e5));

        if (finite && err <= {{c(1)}})
        {
            lambda += h;
            steps++;
            s = yn;
            k1 = k7;
            REAL q0, q1, q2;
            REAL uo = Potential(s.X, s.Y, s.Z, q0, q1, q2);
            delay += (uo * uo - {{c(1)}}) * h;
            int bb;
            int cl = Classify(s, bb);
            if (cl != 0) { fate = cl; body = bb; break; }
            REAL fac = err == {{c(0)}} ? {{c(5)}} : min({{c(5.0)}}, max({{c(0.2)}}, {{c(0.9)}} * PowM02(err)));
            h *= fac;
        }
        else
        {
            REAL fac = finite ? min({{c(1.0)}}, max({{c(0.2)}}, {{c(0.9)}} * PowM02(err))) : {{c(0.2)}};
            h *= fac;
            if (h < {{c(1e-14)}} * max({{c(1)}}, lambda)) break;
        }
    }

    int gate = fate == 1 ? body : (fate == 2 ? (int)HoleCount : -1);
    Packed[idx] = (uint)(gate + 1) | ((uint)steps << 3);
    StoreData(idx, delay, 0);
    StoreData(idx, s.Px, 1);
    StoreData(idx, s.Py, 2);
    StoreData(idx, s.Pz, 3);
}
""";
    }
}
