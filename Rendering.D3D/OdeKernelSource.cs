// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.D3D/OdeKernelSource.cs (#2049, physics epic #2048)
//
// One HLSL template for the GPU twins of the shared scattering integrator
// (Engine/Scattering/ScatteringIntegrator.cs, #1923): the explicit Runge-Kutta 5(4) pair of
// Dormand & Prince (1980) with the error-per-step controller of Hairer, Nørsett & Wanner
// (step factor 0.9 err^(-1/5), clamped to [0.2, 5] after an accepted step and [0.2, 1] after a
// rejected one), term for term with the CPU core, including the attempt cap (8 x the step cap)
// and the collapsed-step test. A family (Calculators/Gpu/OdeFamily) supplies only its
// equations as HLSL functions:
//
//   void FamDeriv(REAL y[DIM], out REAL dy[DIM]);                   dy/dt
//   REAL FamMaxStep(REAL y[DIM]);                                    local step cap (> 0)
//   void FamInit(REAL y[DIM], inout REAL ch[NCH]);                   channels at launch
//   void FamObserve(REAL y[DIM], REAL h, inout REAL ch[NCH]);        after each accepted step
//   int  FamClassify(REAL y[DIM], REAL t, REAL ch[NCH], out int body);  0 running, 1 captured,
//                                                                    2 escaped, 3 undecided;
//                                                                    body = the captor, or any
//                                                                    family label (-1 = none)
// and reads its constants through FP(i). The host computes the launch states (exactly as the
// CPU does) and the outcome from the final state, so the kernel holds no family-specific
// launch or exit-labelling code. One dependency-free source, two compilers: FXC (cs_5_0,
// D3D11, OdeD3D11Kernel) and DXC (cs_6_0 -> SPIR-V, Vulkan, OdeVulkanKernel; this file is
// linked into Rendering.Vulkan as the black hole source is).
//
// Resumable (#2049 part 2, TDR safety): a dispatch integrates each item for at most StepBudget
// attempts, then saves its trajectory (y, h, t, steps, attempts, channels) to the State buffer
// and counts itself in Running; the host dispatches again until Running stays zero. A banded
// dispatch alone bounds how many threads run, not how long one thread runs, and a single long
// trajectory in fp64 on a GPU with slow doubles outran the Windows GPU timeout (device lost,
// process crash). The FSAL derivative k1 is recomputed from y on resume — the same value the
// uninterrupted loop carries (k7 = f(yn) and y = yn) — so a resumed trajectory is bit-identical
// to one traced in a single dispatch.
//
// Shader-model limits (as BlackHoleKernelSource, #1972): cs_5_0 doubles have add / mul /
// compare but no divide, sqrt or pow, so RCP and SQRT refine a float seed by Newton's method
// in double, and the controller's err^(-1/5) starts from a float pow refined by two Newton
// steps in double (PowM02). They agree with IEEE division / square root / Math.Pow to the last
// unit or two, so trajectories match the CPU except where chaos amplifies an ulp (basin
// boundaries), which the tests and the probe bound.
//
// Buffers: b0 = integers (item count, first pass, step budget, the step cap, strides),
// t0 = launch states (REAL, DIM per item, chunk-local), t1 = parameters (REAL: rtol, atol,
// initial step, then the family's FP(0..)), u0 = outcomes (uint words per item: fate |
// (body + 1) << 4, steps, then REALs t, y[DIM], ch[NCH]; two words per double), u1 = the
// resumable state (uint words per item: status, steps, attempts, then REALs h, t, y[DIM],
// ch[NCH]), u2 = the Running counter (one uint).

using System.Globalization;

namespace FracturingFog.Rendering;

public static class OdeKernelSource
{
    public const string Entry = "OdeTrace";
    public const int GroupSize = 64;
    /// <summary>#2045 — the largest band one dispatch may trace (65,535 groups).</summary>
    public const int MaxBandPixels = 65535 * GroupSize;
    /// <summary>Parameters before the family's: rtol, atol, initial step.</summary>
    public const int HeadParams = 3;

    /// <summary>Output words per item.</summary>
    public static int Stride(int dim, int channels, bool dbl) => 2 + (1 + dim + System.Math.Max(1, channels)) * (dbl ? 2 : 1);

    /// <summary>Resumable-state words per item.</summary>
    public static int StateStride(int dim, int channels, bool dbl) => 3 + (2 + dim + System.Math.Max(1, channels)) * (dbl ? 2 : 1);

    /// <summary>A REAL literal (double with the L suffix, or float).</summary>
    public static string Lit(double v, bool dbl)
    {
        if (dbl)
        {
            string s = v.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e");
            return s + (s.Contains('.') || s.Contains('e') ? "L" : ".0L");
        }
        string f = ((float)v).ToString("R", CultureInfo.InvariantCulture).Replace("E", "e");
        return f + (f.Contains('.') || f.Contains('e') ? "f" : ".0f");
    }

    /// <summary>The kernel source for a family (<paramref name="familyHlsl"/>, written with REAL,
    /// DIM, NCH, FP(i), RL(literal) and the helpers RCP / SQRT) at <paramref name="dim"/> state variables and
    /// <paramref name="channels"/> channels, in double (<paramref name="dbl"/>) or float.</summary>
    public static string Build(string familyHlsl, int dim, int channels, bool dbl)
    {
        string c(double v) => Lit(v, dbl);
        int nch = System.Math.Max(1, channels);
        return $$"""
#define REAL {{(dbl ? "double" : "float")}}
#define DBL {{(dbl ? 1 : 0)}}
#define DIM {{dim}}
#define NCH {{nch}}
#define RW {{(dbl ? 2 : 1)}}

cbuffer OdeInts : register(b0)
{
    uint Count; uint FirstPass; uint StepBudget; uint MaxSteps;
    uint Stride; uint StateStride; uint Pad0; uint Pad1;
};

StructuredBuffer<REAL> Launch : register(t0);
StructuredBuffer<REAL> P : register(t1);
RWStructuredBuffer<uint> Out : register(u0);
RWStructuredBuffer<uint> State : register(u1);
RWStructuredBuffer<uint> Running : register(u2);

#define FP(i) P[{{HeadParams}} + (i)]
// A REAL literal at full precision: RL(0.1) is 0.1L in double (a bare 0.1 is a float literal).
#if DBL
#define RL(x) x##L
#else
#define RL(x) x##f
#endif

#if DBL
REAL RCP(REAL a) { REAL x = (REAL)(1.0f / (float)a); x = x * (2.0L - a * x); x = x * (2.0L - a * x); return x; }
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

bool Finite(REAL v) { return v - v == {{c(0)}}; }

{{familyHlsl}}

void Put(RWStructuredBuffer<uint> buf, uint o, REAL v)
{
#if DBL
    uint lo, hi;
    asuint(v, lo, hi);
    buf[o] = lo;
    buf[o + 1] = hi;
#else
    buf[o] = asuint(v);
#endif
}

REAL Get(RWStructuredBuffer<uint> buf, uint o)
{
#if DBL
    return asdouble(buf[o], buf[o + 1]);
#else
    return asfloat(buf[o]);
#endif
}

[numthreads({{GroupSize}}, 1, 1)]
void {{Entry}}(uint3 dt : SV_DispatchThreadID)
{
    uint item = dt.x;
    if (item >= Count) return;

    const REAL A21 = {{c(1.0 / 5.0)}};
    const REAL A31 = {{c(3.0 / 40.0)}}, A32 = {{c(9.0 / 40.0)}};
    const REAL A41 = {{c(44.0 / 45.0)}}, A42 = {{c(-56.0 / 15.0)}}, A43 = {{c(32.0 / 9.0)}};
    const REAL A51 = {{c(19372.0 / 6561.0)}}, A52 = {{c(-25360.0 / 2187.0)}}, A53 = {{c(64448.0 / 6561.0)}}, A54 = {{c(-212.0 / 729.0)}};
    const REAL A61 = {{c(9017.0 / 3168.0)}}, A62 = {{c(-355.0 / 33.0)}}, A63 = {{c(46732.0 / 5247.0)}}, A64 = {{c(49.0 / 176.0)}}, A65 = {{c(-5103.0 / 18656.0)}};
    const REAL B1 = {{c(35.0 / 384.0)}}, B3 = {{c(500.0 / 1113.0)}}, B4 = {{c(125.0 / 192.0)}}, B5 = {{c(-2187.0 / 6784.0)}}, B6 = {{c(11.0 / 84.0)}};
    const REAL E1 = {{c(71.0 / 57600.0)}}, E3 = {{c(-71.0 / 16695.0)}}, E4 = {{c(71.0 / 1920.0)}};
    const REAL E5 = {{c(-17253.0 / 339200.0)}}, E6 = {{c(22.0 / 525.0)}}, E7 = {{c(-1.0 / 40.0)}};

    REAL y[DIM], k1[DIM], k2[DIM], k3[DIM], k4[DIM], k5[DIM], k6[DIM], k7[DIM], tmp[DIM], yn[DIM];
    REAL ch[NCH];
    REAL rtol = P[0] > {{c(0)}} ? P[0] : {{c(1e-9)}};
    REAL atol = P[1] > {{c(0)}} ? P[1] : {{c(1e-12)}};
    int maxSteps = max(1, (int)MaxSteps);
    int attemptCap = maxSteps * 8;
    uint so = item * StateStride;
    REAL h, t;
    int steps, attempts;
    if (FirstPass != 0)
    {
        [unroll] for (int i = 0; i < DIM; i++) y[i] = Launch[item * DIM + i];
        [unroll] for (int j = 0; j < NCH; j++) ch[j] = {{c(0)}};
        FamInit(y, ch);
        h = min(P[2] > {{c(0)}} ? P[2] : {{c(0.1)}}, FamMaxStep(y));
        t = {{c(0)}};
        steps = 0; attempts = 0;
    }
    else
    {
        if (State[so] != 0) return;   // finished in an earlier pass
        steps = (int)State[so + 1]; attempts = (int)State[so + 2];
        h = Get(State, so + 3);
        t = Get(State, so + 3 + RW);
        [unroll] for (int i = 0; i < DIM; i++) y[i] = Get(State, so + 3 + RW * (2 + i));
        [unroll] for (int j = 0; j < NCH; j++) ch[j] = Get(State, so + 3 + RW * (2 + DIM + j));
    }
    int fate = 3, body = -1;
    bool done = true;
    uint budget = max(1u, StepBudget);

    FamDeriv(y, k1);
    [loop] while (steps < maxSteps && attempts < attemptCap)
    {
        if (budget == 0) { done = false; break; }   // resume in the next pass
        budget--;
        attempts++;
        REAL hMax = FamMaxStep(y);
        if (h > hMax) h = hMax;
        if (!(h > {{c(0)}}) || !Finite(h)) break;

        [unroll] for (int i = 0; i < DIM; i++) tmp[i] = y[i] + h * (A21 * k1[i]);
        FamDeriv(tmp, k2);
        [unroll] for (int i = 0; i < DIM; i++) tmp[i] = y[i] + h * (A31 * k1[i] + A32 * k2[i]);
        FamDeriv(tmp, k3);
        [unroll] for (int i = 0; i < DIM; i++) tmp[i] = y[i] + h * (A41 * k1[i] + A42 * k2[i] + A43 * k3[i]);
        FamDeriv(tmp, k4);
        [unroll] for (int i = 0; i < DIM; i++) tmp[i] = y[i] + h * (A51 * k1[i] + A52 * k2[i] + A53 * k3[i] + A54 * k4[i]);
        FamDeriv(tmp, k5);
        [unroll] for (int i = 0; i < DIM; i++) tmp[i] = y[i] + h * (A61 * k1[i] + A62 * k2[i] + A63 * k3[i] + A64 * k4[i] + A65 * k5[i]);
        FamDeriv(tmp, k6);
        [unroll] for (int i = 0; i < DIM; i++) yn[i] = y[i] + h * (B1 * k1[i] + B3 * k3[i] + B4 * k4[i] + B5 * k5[i] + B6 * k6[i]);
        FamDeriv(yn, k7);

        REAL err = {{c(0)}};
        bool finite = true;
        [unroll] for (int i = 0; i < DIM; i++)
        {
            REAL e = h * (E1 * k1[i] + E3 * k3[i] + E4 * k4[i] + E5 * k5[i] + E6 * k6[i] + E7 * k7[i]);
            REAL sc = atol + rtol * max(abs(y[i]), abs(yn[i]));
            REAL r = abs(e) * RCP(sc);
            if (!Finite(r)) finite = false;
            else if (r > err) err = r;
        }

        if (finite && err <= {{c(1)}})
        {
            t += h;
            steps++;
            [unroll] for (int i = 0; i < DIM; i++) { y[i] = yn[i]; k1[i] = k7[i]; }
            FamObserve(y, h, ch);
            int b;
            int cl = FamClassify(y, t, ch, b);
            if (cl != 0) { fate = cl; body = b; break; }   // body: the captor, or a family's own label
            REAL f = err == {{c(0)}} ? {{c(5)}} : min({{c(5)}}, max({{c(0.2)}}, {{c(0.9)}} * PowM02(err)));
            h *= f;
        }
        else
        {
            REAL f = finite ? min({{c(1)}}, max({{c(0.2)}}, {{c(0.9)}} * PowM02(err))) : {{c(0.2)}};
            h *= f;
            if (h < {{c(1e-14)}} * max({{c(1)}}, t)) break;
        }
    }

    if (!done)
    {
        State[so] = 0;
        State[so + 1] = (uint)steps; State[so + 2] = (uint)attempts;
        Put(State, so + 3, h);
        Put(State, so + 3 + RW, t);
        [unroll] for (int i = 0; i < DIM; i++) Put(State, so + 3 + RW * (2 + i), y[i]);
        [unroll] for (int j = 0; j < NCH; j++) Put(State, so + 3 + RW * (2 + DIM + j), ch[j]);
        InterlockedAdd(Running[0], 1u);
        return;
    }
    State[so] = 1;
    uint o = item * Stride;
    Out[o] = (uint)fate | ((uint)(body + 1) << 4);
    Out[o + 1] = (uint)steps;
    Put(Out, o + 2, t);
    [unroll] for (int i = 0; i < DIM; i++) Put(Out, o + 2 + RW * (1 + i), y[i]);
    [unroll] for (int j = 0; j < NCH; j++) Put(Out, o + 2 + RW * (1 + DIM + j), ch[j]);
}
""";
    }
}
