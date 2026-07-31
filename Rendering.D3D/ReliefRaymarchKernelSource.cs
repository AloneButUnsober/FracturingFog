// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ReliefRaymarchKernelSource.cs — shared HLSL for the Relief 3D sphere-trace
// compute kernel (#157 Slice 3 / sub-issue #159 = 3a).
//
// The GPU twin of HeightfieldRaymarch2D's oblique raymarch, restricted to the
// SHADER scope of Slice 3: flat three-light Lambert + ambient, a two-colour
// gradient sky, no soft shadow / AO / PBR / IBL / reflections / fog (those are
// Slice 4). It is a line-for-line port of the CPU parity twin
// ReliefRaymarchGpu.RenderCpuMirror — keep the two in lockstep; the device gate
// (#160 D3D, #161 Vulkan) diffs a dispatch of this against that twin.
//
// Same one-source/two-compiler discipline as MandelbrotKernelSource: this file
// is DELIBERATELY dependency-free so it can be <Compile Include ... Link>-ed
// into the cross-platform Vulkan projects. The HLSL carries NO [[vk::binding]]
// attributes (they break FXC); the Vulkan side pins descriptor bindings with
// DXC -fvk-*-shift flags instead. FXC compiles cs_5_0 for D3D; DXC compiles
// cs_6_0 -spirv for Vulkan.
//
// Bindings — b0 = ReliefParams cbuffer; t0 = height (R32F, one float/cell);
// t1 = albedo (packed ARGB, one uint/pixel); t2 = cull mask (one uint/cell,
// 0 = culled, bound only when gHasKeep != 0); u0 = packed-ARGB output.
//
// NOTE (3a): this source is captured now but first COMPILED + dispatched in 3b.
// Height sampling is bilinear here; the bicubic option (Relief2DBicubicHeight)
// is deferred — the gate runs bilinear so twin == shader. Field pre-pass, cull
// mask and camera all arrive precomputed (Slice-1 cached hbuf + ReliefUniforms).

namespace FracturingFog.Rendering;

/// <summary>Shared HLSL source for the Relief 3D raymarch compute kernel. See
/// the file header for the two-compiler rationale and Slice-3 scope.</summary>
public static class ReliefRaymarchKernelSource
{
    /// <summary>Compute-shader entry point name (both compilers).</summary>
    public const string EntryPoint = "CSRelief";

    /// <summary>Compose the full relief-raymarch kernel source.</summary>
    public static string Build() => Hlsl;

    // Layout note for the matching C# blob (built in 3b): scalars are grouped so
    // no field straddles a 16-byte cbuffer row. Packed colours are uints.
    public const string Hlsl = @"
cbuffer ReliefParams : register(b0)
{
    int   gW;            // output width
    int   gH;            // output height
    int   gHw;           // field width
    int   gHh;           // field height

    float gSy;           // world height per height unit
    float gAspect;       // gW / gH
    float gInvLip;       // Lipschitz normalisation
    int   gOrtho;        // 0 = perspective, 1 = orthographic

    float3 gCam;         // camera origin
    float  gTanHalf;
    float3 gFwd;         // forward
    float  gOrthoHalfV;
    float3 gRight;       // right (y == 0)
    float  gEps0;
    float3 gUp;          // up
    float  gPixelAngle;

    float3 gB;           // AABB half-extents (bx, by, bz)
    int    gMaxSteps;
    int    gGroundPlane;
    int    gShowSky;
    int    gIsolate;
    int    gHasKeep;

    float3 gL0; float gI0; float3 gC0; float gPad0;   // light 0 dir / intensity / colour(0-255)
    float3 gL1; float gI1; float3 gC1; float gPad1;
    float3 gL2; float gI2; float3 gC2; float gPad2;

    float  gAmbient;
    float  gFloorBx;
    float  gFloorBz;
    float  gPad3;

    uint   gBgTop;       // packed ARGB
    uint   gBgBottom;
    uint   gFloorAlbedo;
    uint   gDropColor;

    float  gSpecStrength; // 4a — Cook-Torrance GGX; 0 = no spec (flat Lambert)
    float  gRoughness;    // GGX roughness [0.05,1]
    float  gMetallic;     // dielectric(0) .. metal(1)
    float  gPadS;
};

static const float RELIEF_PI = 3.14159265358979;

StructuredBuffer<float> gHeight : register(t0);   // one float per field cell
StructuredBuffer<uint>  gAlbedo : register(t1);   // packed ARGB per output pixel
StructuredBuffer<uint>  gKeep   : register(t2);   // 0 = culled (bound iff gHasKeep)

RWStructuredBuffer<uint> gColor : register(u0);   // packed ARGB output

int ClampI(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

float Fetch(int x, int y)
{
    x = ClampI(x, 0, gHw - 1);
    y = ClampI(y, 0, gHh - 1);
    return gHeight[y * gHw + x];
}

bool Culled(float x, float z)
{
    if (gHasKeep == 0) return false;
    int px = (int)round((x / gAspect + 0.5) * gHw - 0.5);
    int pz = (int)round((z + 0.5) * gHh - 0.5);
    px = ClampI(px, 0, gHw - 1);
    pz = ClampI(pz, 0, gHh - 1);
    return gKeep[pz * gHw + px] == 0u;
}

// Bilinear world (x,z) -> world-space surface height.
float SampleHeight(float x, float z)
{
    float u = x / gAspect + 0.5;
    float v = z + 0.5;
    float fx = u * gHw - 0.5;
    float fy = v * gHh - 0.5;
    int x0 = (int)floor(fx), y0 = (int)floor(fy);
    float tx = fx - x0, ty = fy - y0;
    float h00 = Fetch(x0, y0),     h10 = Fetch(x0 + 1, y0);
    float h01 = Fetch(x0, y0 + 1), h11 = Fetch(x0 + 1, y0 + 1);
    float a = h00 + (h10 - h00) * tx;
    float b = h01 + (h11 - h01) * tx;
    return (a + (b - a) * ty) * gSy;
}

// Analytic bilinear-patch world-space gradient (dH/dx, dH/dz).
float2 SampleGrad(float x, float z)
{
    float u = x / gAspect + 0.5;
    float v = z + 0.5;
    float fx = u * gHw - 0.5;
    float fy = v * gHh - 0.5;
    int x0 = (int)floor(fx), y0 = (int)floor(fy);
    float tx = fx - x0, ty = fy - y0;
    float h00 = Fetch(x0, y0),     h10 = Fetch(x0 + 1, y0);
    float h01 = Fetch(x0, y0 + 1), h11 = Fetch(x0 + 1, y0 + 1);
    float dHdfx = (h10 - h00) * (1.0 - ty) + (h11 - h01) * ty;
    float dHdfy = (h01 - h00) * (1.0 - tx) + (h11 - h10) * tx;
    float gx = dHdfx * (gHw / gAspect) * gSy;
    float gz = dHdfy * gHh * gSy;
    return float2(gx, gz);
}

float Evaluate(float x, float y, float z)
{
    return Culled(x, z) ? 1e9 : (y - SampleHeight(x, z)) * gInvLip;
}

// 1-axis ray-slab clip; narrows [t0,t1] to the segment inside [lo,hi].
bool SlabHit(float o, float dcomp, float lo, float hi, inout float t0, inout float t1)
{
    if (abs(dcomp) < 1e-12) return o >= lo && o <= hi;
    float inv = 1.0 / dcomp;
    float ta = (lo - o) * inv, tb = (hi - o) * inv;
    if (ta > tb) { float tmp = ta; ta = tb; tb = tmp; }
    if (ta > t0) t0 = ta;
    if (tb < t1) t1 = tb;
    return t0 <= t1;
}

// Bilinear sample of the packed-ARGB albedo at UV in [0,1] (edge-clamped);
// keeps the alpha of the nearest texel.
uint SampleAlbedo(float u, float v)
{
    float fx = u * gW - 0.5, fy = v * gH - 0.5;
    int x0 = (int)floor(fx), y0 = (int)floor(fy);
    float tx = fx - x0, ty = fy - y0;
    int x1 = ClampI(x0 + 1, 0, gW - 1), y1 = ClampI(y0 + 1, 0, gH - 1);
    x0 = ClampI(x0, 0, gW - 1); y0 = ClampI(y0, 0, gH - 1);
    uint c00 = gAlbedo[y0 * gW + x0], c10 = gAlbedo[y0 * gW + x1];
    uint c01 = gAlbedo[y1 * gW + x0], c11 = gAlbedo[y1 * gW + x1];
    float3 t00 = float3((c00 >> 16) & 0xFF, (c00 >> 8) & 0xFF, c00 & 0xFF);
    float3 t10 = float3((c10 >> 16) & 0xFF, (c10 >> 8) & 0xFF, c10 & 0xFF);
    float3 t01 = float3((c01 >> 16) & 0xFF, (c01 >> 8) & 0xFF, c01 & 0xFF);
    float3 t11 = float3((c11 >> 16) & 0xFF, (c11 >> 8) & 0xFF, c11 & 0xFF);
    float3 a = lerp(lerp(t00, t10, tx), lerp(t01, t11, tx), ty);
    uint r = (uint)(a.r + 0.5), g = (uint)(a.g + 0.5), b = (uint)(a.b + 0.5);
    return 0xFF000000u | (r << 16) | (g << 8) | b;
}

void Accum(float intensity, float3 col, float3 L, float3 N, inout float3 s)
{
    if (intensity <= 0.0) return;
    float diffuse = max(0.0, dot(N, L)) * intensity;
    s += col * diffuse;
}

// 4a — one directional light's Cook-Torrance GGX specular. Line-for-line twin
// of ShadingPipeline.AccumulateSpec: Schlick F (per-channel F0), Smith joint G
// (Schlick-GGX), GGX D. col is the light colour in 0-255, spec accumulates in
// the same 0-255 byte space the diffuse combine uses.
void SpecAccum(float intensity, float3 col, float3 L, float3 N, float3 V,
               float NdotV, float a2, float kg, float3 F0, inout float3 spec)
{
    if (intensity <= 0.0) return;
    float NdotL = dot(N, L);
    if (NdotL <= 0.0) return;
    float3 H = L + V;
    float hl2 = dot(H, H);
    if (hl2 < 1e-12) return;
    H = H * (1.0 / sqrt(hl2));
    float NdotH = max(0.0, dot(N, H));
    float VdotH = max(0.0, dot(V, H));
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    float D = a2 / (RELIEF_PI * denom * denom);
    float G1V = NdotV / (NdotV * (1.0 - kg) + kg);
    float G1L = NdotL / (NdotL * (1.0 - kg) + kg);
    float G = G1V * G1L;
    float omv = 1.0 - VdotH;
    float Fc = omv * omv * omv * omv * omv;
    float3 F = F0 + (1.0 - F0) * Fc;
    float specBase = (D * G / max(4.0 * NdotV, 1e-4)) * gSpecStrength * intensity;
    spec += specBase * F * col;
}

// Flat three-light Lambert + scalar ambient, plus 4a Cook-Torrance GGX spec.
// Diffuse: s = amb + (Sum Ii*max(0,N.Li)*Coli/255)*(1-amb)*diffSuppress.
// Spec (gSpecStrength>0): metallic F0 = lerp(0.04, albedo, gMetallic), diffuse
// suppressed by (1-gMetallic). gSpecStrength==0 → flat-Lambert (byte-identical).
uint ShadeFlat(float3 N, float3 V, uint albedo)
{
    float3 s = float3(0, 0, 0);
    Accum(gI0, gC0, gL0, N, s);
    Accum(gI1, gC1, gL1, N, s);
    Accum(gI2, gC2, gL2, N, s);

    float3 alb = float3((albedo >> 16) & 0xFF, (albedo >> 8) & 0xFF, albedo & 0xFF);
    float3 spec = float3(0, 0, 0);
    float diffSuppress = 1.0;
    if (gSpecStrength > 0.0)
    {
        float rough = max(0.05, gRoughness);
        float a = rough * rough;
        float a2 = a * a;
        float kg = (rough + 1.0) * (rough + 1.0) / 8.0;
        float3 F0 = 0.04 + (alb / 255.0 - 0.04) * gMetallic;
        float NdotV = max(0.0, dot(N, V));
        SpecAccum(gI0, gC0, gL0, N, V, NdotV, a2, kg, F0, spec);
        SpecAccum(gI1, gC1, gL1, N, V, NdotV, a2, kg, F0, spec);
        SpecAccum(gI2, gC2, gL2, N, V, NdotV, a2, kg, F0, spec);
        diffSuppress = 1.0 - gMetallic;
    }

    float amb = gAmbient;
    s = amb + (s / 255.0) * (1.0 - amb) * diffSuppress;
    float3 o = clamp(alb * s + spec + 0.5, 0.0, 255.0);
    uint A = (albedo >> 24) & 0xFFu;
    return (A << 24) | ((uint)o.r << 16) | ((uint)o.g << 8) | (uint)o.b;
}

uint GradientSky(float rdy)
{
    float t = clamp(0.5 * rdy + 0.5, 0.0, 1.0);
    float3 a = float3((gBgBottom >> 16) & 0xFF, (gBgBottom >> 8) & 0xFF, gBgBottom & 0xFF);
    float3 b = float3((gBgTop >> 16) & 0xFF, (gBgTop >> 8) & 0xFF, gBgTop & 0xFF);
    float3 c = lerp(a, b, t) + 0.5;
    return 0xFF000000u | ((uint)c.r << 16) | ((uint)c.g << 8) | (uint)c.b;
}

[numthreads(8, 8, 1)]
void CSRelief(uint3 tid : SV_DispatchThreadID)
{
    int px = (int)tid.x;
    int py = (int)tid.y;
    if (px >= gW || py >= gH) return;
    int idx = py * gW + px;

    float ndcx = 2.0 * (px + 0.5) / gW - 1.0;
    float ndcy = 1.0 - 2.0 * (py + 0.5) / gH;

    float3 o, rd;
    if (gOrtho != 0)
    {
        float sxo = ndcx * gAspect * gOrthoHalfV, syo = ndcy * gOrthoHalfV;
        o = gCam + gRight * sxo + gUp * syo;
        rd = gFwd;
    }
    else
    {
        o = gCam;
        float a = ndcx * gAspect * gTanHalf, b = ndcy * gTanHalf;
        rd = gFwd + gRight * a + gUp * b;
        rd = normalize(rd);
    }

    float t0 = 0.0, t1 = 3.4e38;
    bool inside = SlabHit(o.x, rd.x, -gB.x, gB.x, t0, t1)
               && SlabHit(o.y, rd.y, 0.0, gB.y, t0, t1)
               && SlabHit(o.z, rd.z, -gB.z, gB.z, t0, t1);

    uint outCol;
    bool wrote = false;
    if (inside)
    {
        float t = max(t0, 0.0) + gEps0;
        float tPrev = t, d = 0.0;
        bool hit = false;
        [loop]
        for (int s = 0; s < gMaxSteps && t < t1 + gB.y; s++)
        {
            float3 pw = o + rd * t;
            d = Evaluate(pw.x, pw.y, pw.z);
            float epsT = gEps0 + gPixelAngle * t;
            if (d < epsT) { hit = true; break; }
            tPrev = t;
            t += max(d, epsT * 0.5);
        }

        if (hit)
        {
            float tLo = tPrev, tHi = t;
            [unroll]
            for (int b2 = 0; b2 < 5; b2++)
            {
                float tm = 0.5 * (tLo + tHi);
                float3 pm = o + rd * tm;
                if (Evaluate(pm.x, pm.y, pm.z) > 0.0) tLo = tm; else tHi = tm;
            }
            float tf = tHi;
            float3 hp = o + rd * tf;

            float2 g = SampleGrad(hp.x, hp.z);
            float3 N = normalize(float3(-g.x, 1.0, -g.y));

            float uu = hp.x / gAspect + 0.5, vv = hp.z + 0.5;
            uint alb = SampleAlbedo(uu, vv);
            outCol = ShadeFlat(N, -rd, alb);
            wrote = true;
        }
    }

    if (!wrote)
    {
        bool floored = false;
        if (gGroundPlane != 0 && rd.y < -1e-9)
        {
            float tp = (0.0 - o.y) / rd.y;
            if (tp > 0.0)
            {
                float gx = o.x + rd.x * tp, gz = o.z + rd.z * tp;
                if (abs(gx) <= gFloorBx && abs(gz) <= gFloorBz)
                {
                    outCol = ShadeFlat(float3(0, 1, 0), -rd, gFloorAlbedo);
                    floored = true;
                }
            }
        }
        if (!floored)
        {
            uint bg = gShowSky != 0 ? GradientSky(rd.y) : gDropColor;
            if (gIsolate != 0) bg = bg & 0x00FFFFFFu;
            outCol = bg;
        }
    }

    gColor[idx] = outCol;
}
";
}
