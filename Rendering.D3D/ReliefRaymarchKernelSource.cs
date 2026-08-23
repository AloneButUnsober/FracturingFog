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

    /// <summary>Compose the full relief-raymarch kernel source. The pinhole variant
    /// (default) omits the DOF lens loop entirely — the [loop] wrapping the heavy
    /// TracePixel makes FXC compile pathologically slowly, so a non-DOF relief render
    /// must never pay that cost. Pass <paramref name="dof"/> true only when the render
    /// actually has the aperture open (the backend compiles that variant lazily).</summary>
    public static string Build(bool dof = false) => Hlsl + (dof ? DofEntry : PinholeEntry);

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

    int    gShadowSteps;  // 4b — IQ soft shadow; 0 = off
    float  gShadowSoftK;  // penumbra hardness
    int    gShadowMask;   // bit n enables shadow for light n
    float  gPadSh;

    int    gAoSamples;    // 4c — DE-cone AO; 0 = off
    float  gAoStrength;   // occlusion darkening amount
    float  gPadA0;
    float  gPadA1;

    float  gIblStrength;      // 4d — IBL-modulated ambient; 0 = scalar ambient
    int    gSkyMode;          // 0 Gradient, 1 Solid, 2 Hdri (→ gradient fallback)
    float  gTriplanarStrength;// 4d — procedural texture blend; 0 = off
    float  gTriplanarScale;

    int    gTriplanarKind;    // 0 None, 1 Wood, 2 Marble, 3 Rock, 4 Checker
    uint   gTriplanarTint;    // packed ARGB tint
    float  gPadT0;
    float  gPadT1;

    float  gFogDensity;       // 4e — Beer-Lambert fog; 0 = no fog
    float  gFogHeightFalloff; // density *= exp(-falloff * y)
    int    gVolumeSteps;      // >0 = single-scatter in-scatter walk; 0 = legacy exp fog
    float  gVolumeStepsFalloff;// adaptive volumetric LOD; 0 = fixed step count

    int    gEmptySkip;        // 4f — empty-space-skip; 0 = off (byte-identical march)
    int    gMipW;             // coarse max-height grid width
    int    gMipH;             // coarse max-height grid height
    int    gMipBlk;           // base cells per coarse cell

    int    gHasHdri;          // 4d-ii — HDRI equirect env bound at t4; 0 = gradient/solid
    int    gPadH0;
    int    gPadH1;
    int    gPadH2;

    float  gReflStrength;     // 4e-ii — N-bounce reflection probe; 0 = off
    int    gReflSteps;        // per-bounce march steps (<=0 → 24)
    int    gMaxBounces;       // reflection bounce count, clamped [1,6]
    int    gUseGgx;           // GGX VNDF bounce-dir sampling on/off

    float  gVolNoiseAmount;   // 4e-ii — FBM cloud density swing; 0 = mul 1 (off)
    float  gVolNoiseScale;    // world → noise-space scale
    float  gVolNoiseSpeed;    // drift speed (× gSceneTime)
    int    gVolNoiseOctaves;  // FBM octaves (<=0 → 3)

    float  gVolSelfShadow;    // cloud self-shadow strength; 0 = off
    int    gVolSelfShadowSteps;// cloud self-shadow march steps (clamped 16)
    float  gSceneTime;        // animation clock for the cloud drift
    float  gVolAnisotropy;    // #184 Slice 3 (B) — HG phase anisotropy; 0 = isotropic

    uint   gFogColor;         // #184 Slice 3 (C) — medium scattering albedo (packed ARGB)
    float  gVolPaletteStrength;// #185 (slice D) — in-scatter palette cross-fade; 0 = off
    int    gHasPalette;       // #185 — theme ramp bound at t5; 0 = no palette remap
    int    gPaletteLen;       // #185 — ramp entry count (>=2 to activate)

    float  gDofAperture;      // S3 (#389) — thin-lens aperture radius; 0 = pinhole (byte-identical)
    float  gDofFocus;         // resolved focus distance (auto = |camera| when unset)
    int    gDofSamples;       // lens taps to average when aperture > 0
    int    gEmitAov;          // S4 (#402) — write primary-hit normal+depth to u1; 0 = off

    float  gTransmission;     // S5 (#389/#406) — glass transmission; 0 = opaque (byte-identical)
    float  gIor;              // index of refraction
    float  gAbsorptionDist;   // Beer-Lambert reference distance
    uint   gAbsorptionColor;  // packed ARGB tint surviving one reference distance
};

static const float RELIEF_PI = 3.14159265358979;

StructuredBuffer<float> gHeight : register(t0);   // one float per field cell
StructuredBuffer<uint>  gAlbedo : register(t1);   // packed ARGB per output pixel
StructuredBuffer<uint>  gKeep   : register(t2);   // 0 = culled (bound iff gHasKeep)
StructuredBuffer<float> gMip    : register(t3);   // 4f coarse max-height grid (bound iff gEmptySkip)
StructuredBuffer<uint>  gHdri   : register(t4);   // 4d-ii flattened HDRI env (bound iff gHasHdri)
StructuredBuffer<uint>  gPalette: register(t5);   // #185 theme ramp (bound iff gHasPalette)

RWStructuredBuffer<uint>  gColor : register(u0);   // packed ARGB output
RWStructuredBuffer<float> gAov   : register(u1);   // S4 (#402) primary-hit normal.xyz + depth
                                                   // (4 floats/pixel; written iff gEmitAov, else a stub)

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

// 4d — procedural 2D texture sampler (greyscale [0,1]). Line-for-line twin of
// ShadingPipeline.SampleProc2D. kind: 1 Wood, 2 Marble, 3 Rock, 4 Checker.
float SampleProc2D(int kind, float u, float v)
{
    if (kind == 1) // Wood — concentric rings + angular wobble.
    {
        float r = sqrt(u * u + v * v);
        float wobble = 0.1 * sin(u * 0.3) * cos(v * 0.3);
        return 0.5 + 0.5 * sin((r + wobble) * 6.0);
    }
    if (kind == 2) // Marble — turbulent veins via nested sines.
    {
        float turb = sin(v * 2.0 + sin(u * 4.0) * 1.5);
        return 0.5 + 0.5 * sin(u * 3.0 + turb * 2.0);
    }
    if (kind == 3) // Rock — hash noise.
    {
        float a = sin(u * 12.9898 + v * 78.233) * 43758.5453;
        float n = a - floor(a);
        return clamp(0.3 + 0.7 * n, 0.0, 1.0);
    }
    if (kind == 4) // Checker.
    {
        int cu = ((int)floor(u)) & 1;
        int cv = ((int)floor(v)) & 1;
        return (cu ^ cv) == 0 ? 0.2 : 1.0;
    }
    return 1.0;
}

// 4d — triplanar procedural texture. Project P onto YZ/XZ/XY, sample the 2D fn
// per plane, blend by squared-normal weights, modulate albedo by grey × tint ×
// strength. Twin of ShadingPipeline.ApplyTriplanar; preserves albedo alpha (the
// relief cutout) instead of forcing 0xFF (ShadeFlat re-reads it downstream).
uint ApplyTriplanar(uint albedo, float3 P, float3 N)
{
    float wx = N.x * N.x, wy = N.y * N.y, wz = N.z * N.z;
    float sum = wx + wy + wz;
    if (sum < 1e-8) return albedo;
    float inv = 1.0 / sum; wx *= inv; wy *= inv; wz *= inv;
    float s = gTriplanarScale;
    int kind = gTriplanarKind;
    float txY = SampleProc2D(kind, P.y * s, P.z * s);
    float txX = SampleProc2D(kind, P.x * s, P.z * s);
    float txZ = SampleProc2D(kind, P.x * s, P.y * s);
    float v = clamp(wx * txY + wy * txX + wz * txZ, 0.0, 1.0);
    float Tr = ((gTriplanarTint >> 16) & 0xFF) / 255.0;
    float Tg = ((gTriplanarTint >>  8) & 0xFF) / 255.0;
    float Tb = ( gTriplanarTint        & 0xFF) / 255.0;
    float Ar = (albedo >> 16) & 0xFF;
    float Ag = (albedo >>  8) & 0xFF;
    float Ab =  albedo        & 0xFF;
    float mix = gTriplanarStrength;
    float R = Ar * (1.0 - mix) + Ar * Tr * v * mix;
    float G = Ag * (1.0 - mix) + Ag * Tg * v * mix;
    float B = Ab * (1.0 - mix) + Ab * Tb * v * mix;
    uint Rb = (uint)clamp(R, 0.0, 255.0);
    uint Gb = (uint)clamp(G, 0.0, 255.0);
    uint Bb = (uint)clamp(B, 0.0, 255.0);
    return (albedo & 0xFF000000u) | (Rb << 16) | (Gb << 8) | Bb;
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

// 4b — IQ soft shadow. March the height DE toward the light from o; min(k*h/t)
// over the walk is visibility, 0 on a hard occluder hit. Twin of
// ShadingPipeline.SoftShadow.
float SoftShadow(float3 o, float3 L, float tMin, float tMax, float k, int steps)
{
    float res = 1.0, t = tMin;
    [loop]
    for (int s = 0; s < steps; s++)
    {
        float3 p = o + L * t;
        float hh = Evaluate(p.x, p.y, p.z);
        if (hh < 1e-4) return 0.0;
        if (k > 0.0) res = min(res, k * hh / t);
        t += hh;
        if (t >= tMax) break;
    }
    return clamp(res, 0.0, 1.0);
}

// Flat three-light Lambert + scalar ambient, plus 4a Cook-Torrance GGX spec,
// 4b per-light soft shadow and 4c DE-cone AO. Diffuse: s = amb + (Sum Ii*shi*
// max(0,N.Li)*Coli/255)*(1-amb)*diffSuppress, then s *= ao. Spec (gSpecStrength>0):
// metallic F0 = lerp(0.04,albedo,gMetallic), diffuse suppressed by (1-gMetallic).
// Shadow (gShadowSteps>0) gates direct light only. AO (gAoSamples>0) darkens the
// diffuse+ambient term (not spec). 4d IBL (gIblStrength>0) blends env into the
// per-channel ambient (triplanar is applied to the albedo at the call site). All
// knobs 0 → flat-Lambert (byte-identical).
// 4d-ii — flattened-HDRI sampler. Twin of ReliefHdriBuffer.Sample / SampleUvMip:
// equirect projection, bilinear (u wraps, v clamps), roughness -> mip via
// roughness^2 * (levels-1). gHdri is a uint SSBO: the header (levels, per-mip
// offset/width/height) is read directly (integer loads are never denormal-
// flushed); the RGB pixels are float bit-patterns recovered with asfloat. Returns
// linear RGB (unclamped). See ReliefHdriBuffer.cs for the layout + the rationale.
uint HdriLevels() { return gHdri[0]; }
float3 HdriTexel(uint i) { return float3(asfloat(gHdri[i]), asfloat(gHdri[i + 1]), asfloat(gHdri[i + 2])); }

float3 HdriSampleMip(float uu, float vv, uint mip)
{
    uint levels = HdriLevels();
    if (mip >= levels) mip = levels - 1;
    uint off = gHdri[1 + 3 * mip + 0];
    uint mw  = gHdri[1 + 3 * mip + 1];
    uint mh  = gHdri[1 + 3 * mip + 2];

    uu -= floor(uu);
    vv = clamp(vv, 0.0, 1.0);
    float fx = uu * (float)(mw - 1);
    float fy = vv * (float)(mh - 1);
    int x0 = (int)floor(fx);
    int y0 = (int)floor(fy);
    int x1 = x0 + 1; if ((uint)x1 >= mw) x1 = 0;
    int y1 = min(y0 + 1, (int)mh - 1);
    float tx = fx - x0;
    float ty = fy - y0;
    uint i00 = off + (uint)((y0 * (int)mw + x0) * 3);
    uint i10 = off + (uint)((y0 * (int)mw + x1) * 3);
    uint i01 = off + (uint)((y1 * (int)mw + x0) * 3);
    uint i11 = off + (uint)((y1 * (int)mw + x1) * 3);
    float w00 = (1.0 - tx) * (1.0 - ty), w10 = tx * (1.0 - ty);
    float w01 = (1.0 - tx) * ty,         w11 = tx * ty;
    return HdriTexel(i00) * w00 + HdriTexel(i10) * w10 + HdriTexel(i01) * w01 + HdriTexel(i11) * w11;
}

float3 SampleHdri(float3 dir, float roughness)
{
    // atan2(0,0) is UNDEFINED in HLSL (→ NaN → NaN uv → out-of-bounds SSBO read →
    // black), but CPU Math.Atan2(0,0) == 0. A flat-apex normal (grad exactly 0)
    // gives dir.xz == (0,0) on both sides, so guard it to match the twin exactly.
    float az = (dir.x == 0.0 && dir.z == 0.0) ? 0.0 : atan2(dir.z, dir.x);
    float uu = 0.5 + az * (1.0 / (2.0 * RELIEF_PI));
    float vv = acos(clamp(dir.y, -1.0, 1.0)) * (1.0 / RELIEF_PI);
    uint levels = HdriLevels();
    if (roughness <= 0.0 || levels <= 1) return HdriSampleMip(uu, vv, 0);
    if (roughness > 1.0) roughness = 1.0;
    float level = roughness * roughness * (float)(levels - 1);
    uint lvl = (uint)floor(level);
    if (lvl >= levels - 1) lvl = levels - 1;
    return HdriSampleMip(uu, vv, lvl);
}

// HDRI sky along the view ray (mip 0). Twin of the no-roughness SkyColorHdri:
// linear RGB clamped to bytes (truncating cast, matching (byte)Math.Clamp).
uint HdriSkyPacked(float3 rd)
{
    float3 c = SampleHdri(rd, 0.0);
    uint R = (uint)clamp(c.r * 255.0, 0.0, 255.0);
    uint G = (uint)clamp(c.g * 255.0, 0.0, 255.0);
    uint B = (uint)clamp(c.b * 255.0, 0.0, 255.0);
    return 0xFF000000u | (R << 16) | (G << 8) | B;
}

uint GradientSky(float rdy)
{
    float t = clamp(0.5 * rdy + 0.5, 0.0, 1.0);
    float3 a = float3((gBgBottom >> 16) & 0xFF, (gBgBottom >> 8) & 0xFF, gBgBottom & 0xFF);
    float3 b = float3((gBgTop >> 16) & 0xFF, (gBgTop >> 8) & 0xFF, gBgTop & 0xFF);
    float3 c = lerp(a, b, t) + 0.5;
    return 0xFF000000u | ((uint)c.r << 16) | ((uint)c.g << 8) | (uint)c.b;
}

// 4e-ii — env ambient [0,1] along an arbitrary bounce direction. Twin of
// ShadingPipeline.SampleEnvAmbientHdri(dir,roughness): HDRI when loaded (roughness
// → mip), else the #168 gradient/solid env by dir.y.
float3 EnvAmbientDir(float3 dir, float roughness)
{
    if (gHasHdri != 0) return SampleHdri(dir, roughness);
    if (gSkyMode == 1) // Solid
        return float3((gBgTop >> 16) & 0xFF, (gBgTop >> 8) & 0xFF, gBgTop & 0xFF) / 255.0;
    float t = clamp(0.5 * (dir.y + 1.0), 0.0, 1.0);
    float3 bb = float3((gBgBottom >> 16) & 0xFF, (gBgBottom >> 8) & 0xFF, gBgBottom & 0xFF);
    float3 bt = float3((gBgTop >> 16) & 0xFF, (gBgTop >> 8) & 0xFF, gBgTop & 0xFF);
    return lerp(bb, bt, t) / 255.0;
}

// 4e-ii — packed sky along an arbitrary bounce direction. Twin of
// ShadingPipeline.SkyColorHdri(dir,roughness): HDRI (clamped bytes) else gradient.
uint SkyDirPacked(float3 dir, float roughness)
{
    if (gHasHdri != 0)
    {
        float3 c = SampleHdri(dir, roughness);
        uint R = (uint)clamp(c.r * 255.0, 0.0, 255.0);
        uint G = (uint)clamp(c.g * 255.0, 0.0, 255.0);
        uint B = (uint)clamp(c.b * 255.0, 0.0, 255.0);
        return 0xFF000000u | (R << 16) | (G << 8) | B;
    }
    return GradientSky(dir.y);
}

// 4e-ii — Wang-hash → two [0,1) uniforms for GGX VNDF sampling. Twin of
// ShadingPipeline.HashPair. NOTE the /16777216 (2^24) divisor here vs the
// /16777215 in Hash3D — they are deliberately different constants.
void HashPair(float3 p, int bounce, out float u1, out float u2)
{
    uint a = (uint)(int)(p.x * 1024.0) ^ 0x9E3779B1u;
    uint b = (uint)(int)(p.y * 1024.0) ^ 0x85EBCA77u;
    uint c = (uint)(int)(p.z * 1024.0) ^ 0xC2B2AE3Du;
    uint d = (uint)bounce ^ 0x27D4EB2Fu;
    uint h = a;
    h = (h ^ b) * 0x85EBCA6Bu;
    h = (h ^ c) * 0xC2B2AE35u;
    h = (h ^ d) * 0x27D4EB2Du;
    h ^= h >> 16;
    uint h2 = h * 0x85EBCA6Bu; h2 ^= h2 >> 13;
    u1 = (h  & 0xFFFFFFu) / 16777216.0;
    u2 = (h2 & 0xFFFFFFu) / 16777216.0;
}

// 4e-ii — GGX VNDF importance-sampled reflection dir (Heitz 2018). Twin of
// ShadingPipeline.SampleGgxReflect. V is toward the viewer; returns L = reflect(-V,H).
float3 SampleGgxReflect(float3 V, float3 N, float roughness, float u1, float u2)
{
    float sign = N.y >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (sign + N.y);
    float bComp = N.x * N.z * a;
    float3 t1 = float3(1.0 + sign * N.x * N.x * a, -sign * N.x, sign * bComp);
    float3 t2 = float3(bComp, -N.z, sign + N.z * N.z * a);

    float Vtx = dot(V, t1);
    float Vty = dot(V, t2);
    float Vtz = dot(V, N);

    float alpha = roughness * roughness;
    if (alpha < 1e-4) alpha = 1e-4;

    float3 Vh = float3(alpha * Vtx, alpha * Vty, Vtz);
    float Vhlen = sqrt(dot(Vh, Vh));
    if (Vhlen < 1e-10) Vhlen = 1e-10;
    Vh /= Vhlen;

    float lensq = Vh.x * Vh.x + Vh.y * Vh.y;
    float3 T1 = lensq > 0.0 ? float3(-Vh.y, Vh.x, 0.0) * rsqrt(lensq) : float3(1.0, 0.0, 0.0);
    float3 T2 = cross(Vh, T1);

    float r = sqrt(u1);
    float phi = 2.0 * RELIEF_PI * u2;
    float tA = r * cos(phi);
    float tB = r * sin(phi);
    float sC = 0.5 * (1.0 + Vh.z);
    tB = (1.0 - sC) * sqrt(max(0.0, 1.0 - tA * tA)) + sC * tB;

    float Nhz = sqrt(max(0.0, 1.0 - tA * tA - tB * tB));
    float3 Nh = tA * T1 + tB * T2 + Nhz * Vh;

    float3 Ht = float3(alpha * Nh.x, alpha * Nh.y, max(0.0, Nh.z));
    float Hlen = sqrt(dot(Ht, Ht));
    if (Hlen < 1e-10) Hlen = 1e-10;
    Ht /= Hlen;

    float3 H = Ht.x * t1 + Ht.y * t2 + Ht.z * N;
    float VdotH = dot(V, H);
    float3 L = 2.0 * VdotH * H - V;
    float ll = length(L);
    return ll < 1e-10 ? float3(0.0, 0.0, 0.0) : L / ll;
}

// 4e-ii — N-bounce reflection probe. Sphere-trace reflect(rd,N) along the height
// DE; per bounce add a Schlick-Fresnel-weighted env-tinted hit (distance-
// attenuated) / sky-tinted miss, fading the chain by (gReflStrength·F). Optional
// GGX VNDF bounce dir (gUseGgx; off in the gate — hash/trig float-vs-double
// landmine). Twin of ShadingPipeline.Shade Phase 16/16b. Returns 0-255 RGB.
float3 Reflections(float3 N, float3 rd, float3 P)
{
    if (gReflStrength <= 0.0) return float3(0, 0, 0);
    int reflSteps = gReflSteps > 0 ? gReflSteps : 24;
    int maxBounces = gMaxBounces > 0 ? gMaxBounces : 1;
    if (maxBounces > 6) maxBounces = 6;
    float tMaxR = 12.0;
    float bias = gEps0 * 4.0;

    float3 bO = P + N * bias;
    float rdN0 = dot(rd, N);
    float3 br = rd - 2.0 * rdN0 * N;
    if (gUseGgx != 0)
    {
        float u1, u2; HashPair(P, 0, u1, u2);
        float3 g = SampleGgxReflect(-rd, N, gRoughness, u1, u2);
        if (dot(g, N) > 0.0) br = g;
    }
    float NdotV = max(0.0, dot(N, -rd));

    float3 acc = float3(0, 0, 0);
    float chainW = gReflStrength;
    [loop]
    for (int b = 0; b < maxBounces; b++)
    {
        float f0 = 0.04 + 0.96 * gMetallic;
        float omv = 1.0 - NdotV;
        float Fc = omv * omv * omv * omv * omv;
        float F = f0 + (1.0 - f0) * Fc;
        float w = chainW * F;
        if (w < 1e-4) break;

        float tR = gEps0;
        bool hitR = false;
        float hitTR = 0.0;
        float3 hp = float3(0, 0, 0);
        [loop]
        for (int s = 0; s < reflSteps; s++)
        {
            hp = bO + br * tR;
            float hR = Evaluate(hp.x, hp.y, hp.z);
            if (hR < gEps0 * 2.0) { hitR = true; hitTR = tR; break; }
            tR += hR;
            if (tR > tMaxR) break;
        }

        if (!hitR)
        {
            uint skyR = SkyDirPacked(br, gRoughness);
            acc += float3((skyR >> 16) & 0xFF, (skyR >> 8) & 0xFF, skyR & 0xFF) * w;
            break;
        }

        float3 env = EnvAmbientDir(br, gRoughness);
        float atten = exp(-hitTR * 0.15);
        acc += env * 255.0 * atten * w;

        if (b + 1 >= maxBounces) break;

        float h = gEps0 * 2.0;
        float nbx = Evaluate(hp.x + h, hp.y, hp.z) - Evaluate(hp.x - h, hp.y, hp.z);
        float nby = Evaluate(hp.x, hp.y + h, hp.z) - Evaluate(hp.x, hp.y - h, hp.z);
        float nbz = Evaluate(hp.x, hp.y, hp.z + h) - Evaluate(hp.x, hp.y, hp.z - h);
        float nl = length(float3(nbx, nby, nbz));
        float3 n2 = nl < 1e-10 ? float3(0, 0, 0) : float3(nbx, nby, nbz) / nl;
        float rdN = dot(br, n2);
        float3 bn = br - 2.0 * rdN * n2;
        if (gUseGgx != 0)
        {
            float u1, u2; HashPair(hp, b + 1, u1, u2);
            float3 g = SampleGgxReflect(-br, n2, gRoughness, u1, u2);
            if (dot(g, n2) > 0.0) bn = g;
        }
        NdotV = max(0.0, dot(n2, -br));
        bO = hp + n2 * bias;
        br = bn;
        chainW = w;
    }
    return acc;
}

uint ShadeFlat(float3 N, float3 V, float3 P, uint albedo)
{
    float sh0 = 1.0, sh1 = 1.0, sh2 = 1.0;
    if (gShadowSteps > 0)
    {
        float bias = gEps0 * 4.0;
        float3 o = P + N * bias;
        if ((gShadowMask & 0x1) != 0 && gI0 > 0.0)
            sh0 = SoftShadow(o, gL0, gEps0, 12.0, gShadowSoftK, gShadowSteps);
        if ((gShadowMask & 0x2) != 0 && gI1 > 0.0)
            sh1 = SoftShadow(o, gL1, gEps0, 12.0, gShadowSoftK, gShadowSteps);
        if ((gShadowMask & 0x4) != 0 && gI2 > 0.0)
            sh2 = SoftShadow(o, gL2, gEps0, 12.0, gShadowSoftK, gShadowSteps);
    }

    float3 s = float3(0, 0, 0);
    Accum(gI0 * sh0, gC0, gL0, N, s);
    Accum(gI1 * sh1, gC1, gL1, N, s);
    Accum(gI2 * sh2, gC2, gL2, N, s);

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
        SpecAccum(gI0 * sh0, gC0, gL0, N, V, NdotV, a2, kg, F0, spec);
        SpecAccum(gI1 * sh1, gC1, gL1, N, V, NdotV, a2, kg, F0, spec);
        SpecAccum(gI2 * sh2, gC2, gL2, N, V, NdotV, a2, kg, F0, spec);
        diffSuppress = 1.0 - gMetallic;
    }

    // 4c — DE-cone AO. Cone-march the height DE along N; each ring's occlusion is
    // max(0, d - de(P + N*d)) / d. ao darkens the diffuse+ambient term only (spec
    // untouched), matching ShadingPipeline. gAoSamples == 0 → ao = 1 (no-op).
    float ao = 1.0;
    if (gAoSamples > 0)
    {
        float occl = 0.0, wsum = 0.0;
        [loop]
        for (int k = 1; k <= gAoSamples; k++)
        {
            float d = gEps0 * (float)(1 << k);
            float sampleD = Evaluate(P.x + N.x * d, P.y + N.y * d, P.z + N.z * d);
            occl += max(0.0, d - sampleD) / d;
            wsum += 1.0;
        }
        ao = clamp(1.0 - gAoStrength * (occl / max(wsum, 1.0)), 0.0, 1.0);
    }

    // 4d — IBL-modulated ambient. gIblStrength>0 blends the env colour (sampled
    // at the surface normal) into the scalar ambient per channel. Non-HDRI env:
    // Solid → gBgTop, else → BgBottom→BgTop gradient by N.y (twin of
    // ShadingPipeline.SampleEnvAmbient). gIblStrength==0 → flat gAmbient (no-op).
    float3 amb = float3(gAmbient, gAmbient, gAmbient);
    if (gIblStrength > 0.0)
    {
        float3 env;
        if (gHasHdri != 0) // 4d-ii — HDRI env sampled at the normal (mip 0)
            env = SampleHdri(N, 0.0);
        else if (gSkyMode == 1) // Solid
            env = float3((gBgTop >> 16) & 0xFF, (gBgTop >> 8) & 0xFF, gBgTop & 0xFF) / 255.0;
        else               // Gradient
        {
            float t = clamp(0.5 * (N.y + 1.0), 0.0, 1.0);
            float3 bb = float3((gBgBottom >> 16) & 0xFF, (gBgBottom >> 8) & 0xFF, gBgBottom & 0xFF);
            float3 bt = float3((gBgTop >> 16) & 0xFF, (gBgTop >> 8) & 0xFF, gBgTop & 0xFF);
            env = lerp(bb, bt, t) / 255.0;
        }
        amb = amb * (1.0 - gIblStrength) + env * gIblStrength;
    }
    s = amb + (s / 255.0) * (1.0 - amb) * diffSuppress;
    s = s * ao;
    // 4e-ii — N-bounce reflection probe added to the combined colour (rd = -V).
    float3 refl = Reflections(N, -V, P);
    float3 comb = alb * s + spec + refl;

    // S5 (#389/#406) — refractive (glass) transmission. Env-refraction approximation,
    // twin of ShadingPipeline.Shade + ReliefRaymarchGpu.ShadeFlat: refract the view
    // ray (rd = -V) about N, sample the env along it (Beer-Lambert-tinted), Fresnel-mix
    // with the reflected env, blend into the surface by gTransmission. 0 = opaque.
    if (gTransmission > 0.0)
    {
        float ior = gIor > 1.0 ? gIor : 1.0;
        float3 rd = -V;
        float3 tdir = refract(rd, N, 1.0 / ior);
        bool tir = dot(tdir, tdir) < 1e-8;
        if (tir) tdir = reflect(rd, N);
        float f0 = (1.0 - ior) / (1.0 + ior); f0 = f0 * f0;
        float NdotVr = max(0.0, dot(N, V));
        float omc = 1.0 - NdotVr;
        float Fr = tir ? 1.0 : f0 + (1.0 - f0) * (omc * omc * omc * omc * omc);

        uint tSky = SkyDirPacked(tdir, gRoughness);
        float3 tr = float3((tSky >> 16) & 0xFF, (tSky >> 8) & 0xFF, tSky & 0xFF);
        float3 tint = float3((gAbsorptionColor >> 16) & 0xFF, (gAbsorptionColor >> 8) & 0xFF, gAbsorptionColor & 0xFF) / 255.0;
        float3 absorb = float3(1.0, 1.0, 1.0);
        if (gAbsorptionDist > 0.0)
        {
            float d = 1.0 / gAbsorptionDist;   // Beer-Lambert over a nominal 1-unit slab
            absorb.r = tint.r >= 1.0 ? 1.0 : (tint.r <= 0.0 ? 0.0 : pow(tint.r, d));
            absorb.g = tint.g >= 1.0 ? 1.0 : (tint.g <= 0.0 ? 0.0 : pow(tint.g, d));
            absorb.b = tint.b >= 1.0 ? 1.0 : (tint.b <= 0.0 ? 0.0 : pow(tint.b, d));
        }
        tr *= absorb;

        uint rSky = SkyDirPacked(reflect(rd, N), gRoughness);
        float3 re = float3((rSky >> 16) & 0xFF, (rSky >> 8) & 0xFF, rSky & 0xFF);
        float3 gcol = re * Fr + tr * (1.0 - Fr);
        float t = min(gTransmission, 1.0);
        comb = comb * (1.0 - t) + gcol * t;
    }

    float3 o = clamp(comb + 0.5, 0.0, 255.0);
    uint A = (albedo >> 24) & 0xFFu;
    return (A << 24) | ((uint)o.r << 16) | ((uint)o.g << 8) | (uint)o.b;
}

// 4e-ii — value-noise FBM cloud density. Twin of ShadingPipeline.Hash3D/
// ValueNoise3D/FbmCloud3D. The integer hash is bit-exact (int/uint ops are
// well-defined mod 2^32 on both compilers); value noise is C1-continuous across
// cell boundaries so the float-vs-double floor split is benign (tiny diffs, no
// jumps). NOTE the /16777215 divisor here vs /16777216 in HashPair.
float Hash3D(int ix, int iy, int iz)
{
    uint h = (uint)(ix * 374761393 + iy * 668265263 + iz * 2147483647);
    h = (h ^ (h >> 13)) * 1274126177u;
    h ^= h >> 16;
    return (h & 0xFFFFFFu) / 16777215.0;
}

float ValueNoise3D(float x, float y, float z)
{
    int ix = (int)floor(x), iy = (int)floor(y), iz = (int)floor(z);
    float fx = x - ix, fy = y - iy, fz = z - iz;
    float ux = fx * fx * (3.0 - 2.0 * fx);
    float uy = fy * fy * (3.0 - 2.0 * fy);
    float uz = fz * fz * (3.0 - 2.0 * fz);
    float c000 = Hash3D(ix,     iy,     iz    ), c100 = Hash3D(ix + 1, iy,     iz    );
    float c010 = Hash3D(ix,     iy + 1, iz    ), c110 = Hash3D(ix + 1, iy + 1, iz    );
    float c001 = Hash3D(ix,     iy,     iz + 1), c101 = Hash3D(ix + 1, iy,     iz + 1);
    float c011 = Hash3D(ix,     iy + 1, iz + 1), c111 = Hash3D(ix + 1, iy + 1, iz + 1);
    float x00 = c000 + (c100 - c000) * ux;
    float x10 = c010 + (c110 - c010) * ux;
    float x01 = c001 + (c101 - c001) * ux;
    float x11 = c011 + (c111 - c011) * ux;
    float y0 = x00 + (x10 - x00) * uy;
    float y1 = x01 + (x11 - x01) * uy;
    return y0 + (y1 - y0) * uz;
}

float FbmCloud3D(float x, float y, float z, int octaves)
{
    if (octaves < 1) octaves = 1; else if (octaves > 6) octaves = 6;
    float v = 0.0, amp = 0.5, freq = 1.0;
    [loop]
    for (int i = 0; i < octaves; i++)
    {
        v += amp * ValueNoise3D(x * freq, y * freq, z * freq);
        freq *= 2.0; amp *= 0.5;
    }
    return v;
}

// 4e-ii — FBM cloud density multiplier for the in-scatter walk. Twin of
// ShadingPipeline.VolumetricDensityMul. 1.0 when gVolNoiseAmount == 0.
float VolumetricDensityMul(float3 sp)
{
    if (gVolNoiseAmount <= 0.0) return 1.0;
    float t = gSceneTime * gVolNoiseSpeed;
    float scale = gVolNoiseScale;
    int oct = gVolNoiseOctaves <= 0 ? 3 : gVolNoiseOctaves;
    float n = FbmCloud3D(sp.x * scale + t, sp.y * scale + t * 0.3, sp.z * scale + t * 0.7, oct);
    float mul = 1.0 + gVolNoiseAmount * (2.0 * n - 1.0);
    return max(0.0, mul);
}

// 4e-ii — cloud self-shadow transmittance toward the key light. Twin of
// ShadingPipeline.CloudSelfShadow. 1.0 when the self-shadow / noise knobs are off.
float CloudSelfShadow(float3 sp, float3 L)
{
    if (gVolSelfShadow <= 0.0 || gVolSelfShadowSteps <= 0 || gVolNoiseAmount <= 0.0) return 1.0;
    int steps = min(gVolSelfShadowSteps, 16);
    float stepSz = 2.0 / steps;
    float t = gSceneTime * gVolNoiseSpeed;
    float scale = gVolNoiseScale;
    int oct = gVolNoiseOctaves <= 0 ? 3 : gVolNoiseOctaves;
    float accum = 0.0;
    [loop]
    for (int k = 1; k <= steps; k++)
    {
        float3 p = sp + L * stepSz * k;
        float n = FbmCloud3D(p.x * scale + t, p.y * scale + t * 0.3, p.z * scale + t * 0.7, oct);
        float d = max(0.0, 1.0 + gVolNoiseAmount * (2.0 * n - 1.0));
        accum += d * stepSz;
    }
    return exp(-gVolSelfShadow * accum);
}

// 4e — Padé(2,2) approx of exp(-x) on [0,1]. Twin of ShadingPipeline.ExpNegSmall.
float ExpNegSmall(float x)
{
    return (12.0 - 6.0 * x + x * x) / (12.0 + 6.0 * x + x * x);
}

// 4e — Beer-Lambert fog + single-scatter volumetric in-scatter over a shaded
// terrain/floor pixel. Twin of ShadingPipeline's fog block: gVolumeSteps>0 (with
// gFogDensity>0, key light gI0>0) runs the in-scatter walk — per-step density
// (ground-hugging via gFogHeightFalloff) × key-light SoftShadow, Beer-Lambert
// transmittance via the Padé exp; else gFogDensity>0 blends toward the gradient
// sky by 1-exp(-tHit*gFogDensity). gFogDensity==0 → no-op (byte-identical). FBM
// cloud-noise + cloud self-shadow + reflections are deferred (4e-ii); density mul
// is 1 here. o is the primary ray origin (== camera for perspective).
// #184 — single-scatter in-scatter over an explicit air segment [tStart,tEnd]
// from o + rd·t, compositing over the incoming (br,bg,bb) as bg·T + inScatter.
// Twin of ReliefRaymarchGpu.InScatterWalk / ShadingPipeline.VolumetricInScatter
// Segment — full three-light in-scatter (#388). Shared by the surface-hit fog
// ([0,tHit]) and the sky/miss god-ray walk ([t0,t1]) so shafts form against the
// sky. Adaptive LOD keys off the far end of the segment.
// #185 (slice D) — sample the packed-ARGB theme ramp (t5) at u in [0,1] with
// linear interpolation. Returns RGB in [0,255]. Twin of ShadingPipeline.Sample
// Palette / ReliefRaymarchGpu.SamplePalette.
float3 SamplePalette(float u)
{
    u = clamp(u, 0.0, 1.0);
    int n = gPaletteLen;
    float f = u * (n - 1);
    int i0 = (int)f;
    if (i0 >= n - 1)
    {
        uint cl = gPalette[n - 1];
        return float3((cl >> 16) & 0xFF, (cl >> 8) & 0xFF, cl & 0xFF);
    }
    float t = f - i0;
    uint c0 = gPalette[i0], c1 = gPalette[i0 + 1];
    float3 v0 = float3((c0 >> 16) & 0xFF, (c0 >> 8) & 0xFF, c0 & 0xFF);
    float3 v1 = float3((c1 >> 16) & 0xFF, (c1 >> 8) & 0xFF, c1 & 0xFF);
    return v0 + (v1 - v0) * t;
}

// #388 — single-light contribution to the in-scatter accumulator. Twin of
// ShadingPipeline.AddVolumeScatter / ReliefRaymarchGpu.AddReliefScatter:
// density · shadow · intensity · stepSize, HG-phased toward L, weighted by
// transmittance × light colour. Returns the increment; called once per light.
float3 ReliefScatter(float3 sp, float3 L, float3 rd, float3 Lc, float li, bool shOn,
                     float T, float density, float stepSize)
{
    float sh = 1.0;
    if (shOn)
        sh = SoftShadow(sp, L, gEps0, 12.0, gShadowSoftK, gShadowSteps);
    sh *= CloudSelfShadow(sp, L);              // 4e-ii — cloud self-shadow
    float scatter = density * sh * li * stepSize;
    // #184 Slice 3 (B) — Henyey-Greenstein phase, normalized so g=0 → 1.
    float g = gVolAnisotropy;
    if (g != 0.0)
    {
        g = clamp(g, -0.99, 0.99);
        float cosT = dot(rd, L);
        float denom = 1.0 + g * g - 2.0 * g * cosT;
        scatter *= (1.0 - g * g) / (denom * sqrt(denom));
    }
    return T * scatter * Lc;
}

void InScatterWalk(inout float br, inout float bg, inout float bb,
                   float3 o, float3 rd, float tStart, float tEnd)
{
    float span = tEnd - tStart;
    if (span <= 0.0) return;
    int vs = gVolumeSteps;
    if (gVolumeStepsFalloff > 0.0 && tEnd > 4.0)
        vs = max(4, (int)(vs / (1.0 + (tEnd - 4.0) * gVolumeStepsFalloff)));
    float stepSize = span / vs;
    // #388 — multi-light in-scatter: loop all three lights (intensity-gated), each
    // with its own dir / colour / ShadowMask bit. Extinction T is per-step, shared
    // across lights. A light at intensity 0 contributes nothing → the single-light
    // default (key light L0, mask & 0x1) stays byte-identical.
    bool sh0On = gShadowSteps > 0 && (gShadowMask & 0x1) != 0;
    bool sh1On = gShadowSteps > 0 && (gShadowMask & 0x2) != 0;
    bool sh2On = gShadowSteps > 0 && (gShadowMask & 0x4) != 0;
    float T = 1.0; float3 inSc = float3(0, 0, 0);
    [loop]
    for (int s = 0; s < vs; s++)
    {
        float t = tStart + (s + 0.5) * stepSize;
        float3 sp = o + rd * t;
        float density = gFogDensity;
        if (gFogHeightFalloff > 0.0)
            density *= exp(-gFogHeightFalloff * sp.y);
        density *= VolumetricDensityMul(sp);   // 4e-ii — FBM cloud modulation
        if (gI0 > 0.0)
            inSc += ReliefScatter(sp, gL0, rd, gC0, gI0, sh0On, T, density, stepSize);
        if (gI1 > 0.0)
            inSc += ReliefScatter(sp, gL1, rd, gC1, gI1, sh1On, T, density, stepSize);
        if (gI2 > 0.0)
            inSc += ReliefScatter(sp, gL2, rd, gC2, gI2, sh2On, T, density, stepSize);
        float aT = density * stepSize;
        T *= aT < 1.0 ? ExpNegSmall(aT) : exp(-aT);
    }
    // #184 Slice 3 (C) — tint accumulated in-scatter by the medium fog color.
    float3 fc = float3((gFogColor >> 16) & 0xFF, (gFogColor >> 8) & 0xFF, gFogColor & 0xFF) / 255.0;
    float3 fIn = float3(inSc.r * fc.r, inSc.g * fc.g, inSc.b * fc.b);

    // #185 (slice D) — palette-map the in-scatter through the theme ramp, keyed by
    // optical depth (1 − T). Energy-preserving hue remap + cross-fade by
    // gVolPaletteStrength. Twin of ShadingPipeline / ReliefRaymarchGpu slice-D.
    if (gHasPalette != 0 && gPaletteLen >= 2 && gVolPaletteStrength > 0.0)
    {
        float energy = fIn.r + fIn.g + fIn.b;
        if (energy > 0.0)
        {
            float3 pal = SamplePalette(1.0 - T);
            float pSum = pal.r + pal.g + pal.b;
            if (pSum > 1e-6)
            {
                float ps = min(gVolPaletteStrength, 1.0);
                float kk = energy / pSum;
                fIn = fIn * (1.0 - ps) + (pal * kk) * ps;
            }
        }
    }

    br = br * T + fIn.r; bg = bg * T + fIn.g; bb = bb * T + fIn.b;
}

uint ApplyFogVolume(uint shaded, float3 o, float3 rd, float tHit)
{
    if (gFogDensity <= 0.0) return shaded;
    float br = (shaded >> 16) & 0xFF, bg = (shaded >> 8) & 0xFF, bb = shaded & 0xFF;

    if (gVolumeSteps > 0 && (gI0 > 0.0 || gI1 > 0.0 || gI2 > 0.0))   // #388 — any light lights the fog
        InScatterWalk(br, bg, bb, o, rd, 0.0, tHit);
    else
    {
        float fogF = 1.0 - exp(-tHit * gFogDensity);
        uint sky = GradientSky(rd.y);
        br = br * (1.0 - fogF) + ((sky >> 16) & 0xFF) * fogF;
        bg = bg * (1.0 - fogF) + ((sky >>  8) & 0xFF) * fogF;
        bb = bb * (1.0 - fogF) + ( sky        & 0xFF) * fogF;
    }

    uint A = (shaded >> 24) & 0xFFu;
    uint Rb = (uint)clamp(br + 0.5, 0.0, 255.0);
    uint Gb = (uint)clamp(bg + 0.5, 0.0, 255.0);
    uint Bb = (uint)clamp(bb + 0.5, 0.0, 255.0);
    return (A << 24) | (Rb << 16) | (Gb << 8) | Bb;
}

// #184 — sky/miss god-ray composite. When the ray traversed the fog slab
// ([t0,t1]) but hit no terrain and no ground, march the air segment and
// composite the shadow-carved in-scatter over the backdrop so shafts form
// against the sky. No-op for isolate cutouts / when the volumetric gate is off.
uint ApplyFogVolumeMiss(uint bg, float3 o, float3 rd, float tStart, float tEnd)
{
    if (gIsolate != 0 || gFogDensity <= 0.0 || gVolumeSteps <= 0
        || (gI0 <= 0.0 && gI1 <= 0.0 && gI2 <= 0.0)) return bg;   // #388 — any light lights the fog
    float ts = max(tStart, 0.0);
    if (tEnd <= ts) return bg;
    float br = (bg >> 16) & 0xFF, bgc = (bg >> 8) & 0xFF, bb = bg & 0xFF;
    InScatterWalk(br, bgc, bb, o, rd, ts, tEnd);
    uint A = (bg >> 24) & 0xFFu;
    uint Rb = (uint)clamp(br + 0.5, 0.0, 255.0);
    uint Gb = (uint)clamp(bgc + 0.5, 0.0, 255.0);
    uint Bb = (uint)clamp(bb + 0.5, 0.0, 255.0);
    return (A << 24) | (Rb << 16) | (Gb << 8) | Bb;
}

// 4f — conservative empty-space-skip distance. Twin of ReliefRaymarchGpu.
// EmptySkipDist: coarse block max at (px,pz); if the ray point is above it by
// more than epsT, return min(descend-to-plane, cell-exit) — no terrain hit
// possible within that span; else 0 (fall back to the point DE).
float EmptySkipDist(float3 P, float3 rd, float epsT)
{
    float uu = P.x / gAspect + 0.5, vv = P.z + 0.5;
    int cx = (int)floor(uu * gMipW);
    int cz = (int)floor(vv * gMipH);
    cx = ClampI(cx, 0, gMipW - 1);
    cz = ClampI(cz, 0, gMipH - 1);
    float hmax = gMip[cz * gMipW + cx] * gSy;
    if (P.y <= hmax + epsT) return 0.0;

    // Descend to epsT ABOVE the block max so the normal march refines the hit with
    // a tight bracket (twin of ReliefRaymarchGpu). Still conservative.
    float tPlane = rd.y < -1e-9 ? (P.y - (hmax + epsT)) / (-rd.y) : 3.4e38;

    float xLo = (cx / (float)gMipW - 0.5) * gAspect;
    float xHi = ((cx + 1) / (float)gMipW - 0.5) * gAspect;
    float zLo = cz / (float)gMipH - 0.5;
    float zHi = (cz + 1) / (float)gMipH - 0.5;
    float tExit = 3.4e38;
    if (rd.x > 1e-12) tExit = min(tExit, (xHi - P.x) / rd.x);
    else if (rd.x < -1e-12) tExit = min(tExit, (xLo - P.x) / rd.x);
    if (rd.z > 1e-12) tExit = min(tExit, (zHi - P.z) / rd.z);
    else if (rd.z < -1e-12) tExit = min(tExit, (zLo - P.z) / rd.z);

    float skip = min(tPlane, tExit);
    return skip > 0.0 ? skip : 0.0;
}

// The per-ray trace + shade — everything downstream of ray generation, factored
// out of CSRelief so the DOF lens loop can call it once per aperture sample.
// Returns packed ARGB for the ray o + rd. Also reports the primary-hit world-space
// normal + world-units depth (S4 #402): a terrain hit → (surface normal, tf), the
// ground plane → ((0,1,0), tp), a sky/background miss → ((0,0,0), 1e6) — the same
// convention the CPU capture uses, so the AOV write matches the twin.
uint TracePixel(float3 o, float3 rd, out float3 nrm, out float dep)
{
    nrm = float3(0.0, 0.0, 0.0);
    dep = 1e6;
    float t0 = 0.0, t1 = 3.4e38;
    bool inside = SlabHit(o.x, rd.x, -gB.x, gB.x, t0, t1)
               && SlabHit(o.y, rd.y, 0.0, gB.y, t0, t1)
               && SlabHit(o.z, rd.z, -gB.z, gB.z, t0, t1);

    uint outCol = 0;
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
            float adv = max(d, epsT * 0.5);
            // 4f — empty-space skip (conservative; only enlarges the advance).
            if (gEmptySkip != 0)
            {
                float skip = EmptySkipDist(pw, rd, epsT);
                if (skip > adv) adv = skip;
            }
            t += adv;
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
            if (gTriplanarStrength > 0.0 && gTriplanarKind != 0)
                alb = ApplyTriplanar(alb, hp, N);
            outCol = ShadeFlat(N, -rd, hp, alb);
            outCol = ApplyFogVolume(outCol, o, rd, tf);
            nrm = N; dep = tf;
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
                    outCol = ShadeFlat(float3(0, 1, 0), -rd, float3(gx, 0.0, gz), gFloorAlbedo);
                    outCol = ApplyFogVolume(outCol, o, rd, tp);
                    nrm = float3(0.0, 1.0, 0.0); dep = tp;
                    floored = true;
                }
            }
        }
        if (!floored)
        {
            uint bg = gShowSky != 0
                ? (gHasHdri != 0 ? HdriSkyPacked(rd) : GradientSky(rd.y))
                : gDropColor;
            if (gIsolate != 0) bg = bg & 0x00FFFFFFu;
            // #184 — sky/miss god-ray in-scatter over the fog slab air [t0,t1].
            else if (inside) bg = ApplyFogVolumeMiss(bg, o, rd, t0, t1);
            outCol = bg;
        }
    }

    return outCol;
}
";

    // Ray generation shared by both entry variants: pixel centre → (o, rd).
    private const string RayGen = @"
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
";

    // Pinhole entry — one centre ray per pixel. No lens loop, so FXC compiles it as
    // fast as the pre-DOF kernel; this is the variant a non-DOF relief render uses.
    private const string PinholeEntry = @"
[numthreads(8, 8, 1)]
void CSRelief(uint3 tid : SV_DispatchThreadID)
{" + RayGen + @"
    float3 nrm; float dep;
    gColor[idx] = TracePixel(o, rd, nrm, dep);
    if (gEmitAov != 0)
    {
        gAov[idx * 4 + 0] = nrm.x;
        gAov[idx * 4 + 1] = nrm.y;
        gAov[idx * 4 + 2] = nrm.z;
        gAov[idx * 4 + 3] = dep;
    }
}
";

    // DOF entry — average gDofSamples lens taps (Shirley disc + re-aim through the
    // focal point). COMPILED ONLY when a render actually has the aperture open,
    // because the [loop] wrapping the heavy TracePixel makes FXC compile
    // pathologically slowly (tens of seconds) even when the loop never runs — so it
    // must be kept out of the default pinhole shader. Twin of CameraDof.ThinLensRay
    // + ReliefRaymarchGpu.RenderCpuMirror's lens loop.
    private const string DofEntry = @"
// S3 (#389) — Shirley concentric disc map. Twin of CameraDof.ConcentricSampleDisk.
float2 ConcentricSampleDisk(float u1, float u2)
{
    float ox = 2.0 * u1 - 1.0;
    float oy = 2.0 * u2 - 1.0;
    if (ox == 0.0 && oy == 0.0) return float2(0.0, 0.0);
    float r, theta;
    if (abs(ox) > abs(oy)) { r = ox; theta = (RELIEF_PI / 4.0) * (oy / ox); }
    else                   { r = oy; theta = (RELIEF_PI / 2.0) - (RELIEF_PI / 4.0) * (ox / oy); }
    return float2(r * cos(theta), r * sin(theta));
}

[numthreads(8, 8, 1)]
void CSRelief(uint3 tid : SV_DispatchThreadID)
{" + RayGen + @"
    int taps = gDofSamples > 0 ? gDofSamples : 1;
    float focus = gDofFocus;
    float3 fp = gCam + rd * focus;
    float4 acc = float4(0.0, 0.0, 0.0, 0.0);
    float3 aovN = float3(0.0, 0.0, 0.0); float aovD = 1e6;
    [loop]
    for (int k = 0; k < taps; k++)
    {
        float u1, u2;
        HashPair(float3((float)px, (float)py, (float)k), 9, u1, u2);
        float2 dk = ConcentricSampleDisk(u1, u2);
        float2 lens = dk * gDofAperture;
        float3 lo = gCam + gRight * lens.x + gUp * lens.y;
        float3 ld = normalize(fp - lo);
        float3 nrm; float dep;
        uint c = TracePixel(lo, ld, nrm, dep);
        acc += float4((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF, (c >> 24) & 0xFF);
        if (k == 0) { aovN = nrm; aovD = dep; }   // S4 — emit the first tap's geometry (matches the twin)
    }
    float inv = 1.0 / taps;
    uint R = (uint)(acc.x * inv + 0.5);
    uint G = (uint)(acc.y * inv + 0.5);
    uint B = (uint)(acc.z * inv + 0.5);
    uint A = (uint)(acc.w * inv + 0.5);
    gColor[idx] = (A << 24) | (R << 16) | (G << 8) | B;
    if (gEmitAov != 0)
    {
        gAov[idx * 4 + 0] = aovN.x;
        gAov[idx * 4 + 1] = aovN.y;
        gAov[idx * 4 + 2] = aovN.z;
        gAov[idx * 4 + 3] = aovD;
    }
}
";
}
