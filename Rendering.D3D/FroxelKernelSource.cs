// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FroxelKernelSource.cs — shared HLSL for the GPU froxel volume compute pass
// (roadmap S6, #389 / #408).
//
// The GPU twin of the pure-CPU froxel post-pass (FroxelVolumePass.Populate +
// FroxelCameraVolume.CompositeWorldDepth). Two entry points share one cbuffer:
//   * CSFroxelIntegrate — one thread per froxel COLUMN (cx,cy). Walks the DimZ
//     slices near→far, populating each with noise-modulated density → extinction
//     + a multi-light Henyey-Greenstein in-scatter, and integrating the column
//     front-to-back (Hillaire's energy-conserving accumulation) into the volume
//     buffer (float4 per cell = accumulated in-scatter RGB + transmittance).
//   * CSFroxelComposite — one thread per PIXEL. Maps the pixel's world depth to
//     a continuous slice through the exponential grid, samples the integrated
//     column (linear in slice), and composites over the fog-free beauty:
//     beauty·transmittance + inScatter·255.
//
// Same one-source/two-compiler discipline as ReliefRaymarchKernelSource /
// MandelbrotKernelSource: DELIBERATELY dependency-free so it can be
// <Compile Include ... Link>-ed into the cross-platform Vulkan project (the
// documented follow-up). NO [[vk::binding]] attributes (they break FXC); the
// Vulkan side will pin descriptors with DXC -fvk-*-shift flags. FXC compiles
// cs_5_0 for D3D; DXC will compile cs_6_0 -spirv for Vulkan.
//
// The noise (Hash3D/ValueNoise3D/FbmCloud3D), spot cone (SmoothCone) and light
// resolve (ResolveLight) are line-for-line ports of the SAME helpers in
// ReliefRaymarchKernelSource — already proven bit-exact (int hash) / within
// tolerance (value noise) by the --reliefgpuraymarch gate. The froxel populate
// samples noise WITHOUT the relief march's time drift (pure spatial), matching
// FroxelVolumePass.Populate.
//
// Bindings —
//   b0 = FroxelParams cbuffer;
//   INTEGRATE: u0 = RWStructuredBuffer<float4> volume (one/cell, written);
//   COMPOSITE: t0 = beauty (uint/pixel), t1 = worldDepth (float/pixel),
//              t2 = volume (float4/cell, read), u0 = output (uint/pixel).

namespace FracturingFog.Rendering;

/// <summary>Shared HLSL source for the GPU froxel volume compute pass (roadmap
/// S6, #408). See the file header for the two-pass structure + parity contract.</summary>
public static class FroxelKernelSource
{
    /// <summary>Populate + integrate entry point (one thread per column).</summary>
    public const string IntegrateEntry = "CSFroxelIntegrate";

    /// <summary>Composite entry point (one thread per pixel).</summary>
    public const string CompositeEntry = "CSFroxelComposite";

    /// <summary>The full kernel source (both entry points + shared helpers).</summary>
    public static string Build() => Hlsl;

    // Layout note for the matching C# blob (FroxelGpuKernel.FroxelParamsBlob):
    // scalars are grouped 16-byte-row aligned so no field straddles a cbuffer row.
    // Packed colours are uints. Field order MUST track this cbuffer.
    public const string Hlsl = @"
cbuffer FroxelParams : register(b0)
{
    int   gNx;            // grid dims
    int   gNy;
    int   gNz;
    int   gW;             // output width

    int   gH;             // output height
    float gNear;          // grid near depth
    float gFar;           // grid far depth
    float gExtent;        // froxel slab half-extent in world X/Y

    float gBaseDensity;   // fog density floor
    float gExtinction;    // extinction per unit density (== 1 from BuildMedium)
    float gAnisotropy;    // HG phase g; 0 = isotropic
    float gNoiseAmount;   // FBM heterogeneity; 0 = homogeneous

    float gNoiseScale;    // world -> noise-space scale
    int   gNoiseOctaves;  // FBM octaves (<=0 -> 3)
    float gViewX;         // view direction (camera forward) for the HG phase
    float gViewY;

    float gViewZ;
    int   gNumLights;     // active light count (BuildMedium always fills 3)
    float gFeedback;      // S6 #408 temporal: history weight in [0,0.999]; 0 = single-frame
    int   gHistoryValid;  // 1 = gHistory holds the previous frame for THIS grid; 0 = re-seed

    // Light 0 — 3 rows.
    int   gType0; uint gColor0; float gI0; float gRange0;
    float gDir0x; float gDir0y; float gDir0z; float gInner0;
    float gPos0x; float gPos0y; float gPos0z; float gOuter0;
    // Light 1.
    int   gType1; uint gColor1; float gI1; float gRange1;
    float gDir1x; float gDir1y; float gDir1z; float gInner1;
    float gPos1x; float gPos1y; float gPos1z; float gOuter1;
    // Light 2.
    int   gType2; uint gColor2; float gI2; float gRange2;
    float gDir2x; float gDir2y; float gDir2z; float gInner2;
    float gPos2x; float gPos2y; float gPos2z; float gOuter2;
};

// ---- integer value-noise FBM (twin of ReliefRaymarchKernelSource / ShadingPipeline) ----
// The integer hash is bit-exact (int/uint ops well-defined mod 2^32 on both
// compilers); value noise is C1-continuous so the float-vs-double floor split is
// benign. NOTE /16777215 divisor here (matches Hash3D, not the /16777216 HashPair).
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

// ---- spot cone + light resolve (twin of LightSampler / ReliefRaymarchKernelSource) ----
float SmoothCone(float cosA, float innerCos, float outerCos)
{
    float denom = innerCos - outerCos;
    if (denom <= 1e-9) return cosA >= innerCos ? 1.0 : 0.0;
    float t = saturate((cosA - outerCos) / denom);
    return t * t * (3.0 - 2.0 * t);
}

// Resolve a light at froxel world point P -> (unit dir-to-light, attenuation).
// Directional (type 0) -> (toDir, 1). Point (1) -> inverse-square x Karis range
// window. Spot (2) -> point x smooth cone (axis = toDir).
float4 ResolveLight(int type, float3 toDir, float3 pos, float range,
                    float innerCos, float outerCos, float3 P)
{
    if (type == 0) return float4(toDir, 1.0);
    float3 d = pos - P;
    float dist2 = dot(d, d);
    float dist = sqrt(dist2);
    float inv = dist > 1e-12 ? 1.0 / dist : 0.0;
    float3 L = d * inv;
    float atten = 1.0 / max(dist2, 1e-6);
    if (range > 0.0)
    {
        float t = dist / range;
        float t4 = t * t * t * t;
        float win = saturate(1.0 - t4);
        atten *= win * win;
    }
    if (type == 2)
        atten *= SmoothCone(dot(L, toDir), innerCos, outerCos);
    return float4(L, atten);
}

// Normalized Henyey-Greenstein phase (g=0 -> 1). Twin of FroxelVolumePass.HgPhase.
float HgPhase(float g, float cosT)
{
    if (g == 0.0) return 1.0;
    g = clamp(g, -0.99, 0.99);
    float denom = 1.0 + g * g - 2.0 * g * cosT;
    return (1.0 - g * g) / (denom * sqrt(denom));
}

// Exponential slice-boundary depth (twin of FroxelGrid.SliceDepth): 0 -> Near,
// Nz -> Far, near-dense. z in [0, Nz].
float SliceDepth(int z)
{
    float t = (float)z / (float)gNz;
    return gNear * pow(gFar / gNear, t);
}

// ---- populate + integrate one column (twin of FroxelVolumePass.Populate) ----
RWStructuredBuffer<float4> gVolume : register(u0);
// S6 #408 temporal reprojection — persistent PRE-integration scatter(rgb)+ext(a)
// grid from the previous frame, one float4/cell, laid out exactly like gVolume
// (index = (cy*Nx+cx)*Nz + z). The GPU twin of FroxelHistory: the per-cell
// scatter+extinction is exponentially blended toward this history BEFORE the
// front-to-back integration (energy-conserving), then the blended value is
// stored back as the next frame's history. Read+written only when temporal is
// active (gFeedback>0); guarded so the single-frame path is untouched and the
// buffer may be left unbound then.
RWStructuredBuffer<float4> gHistory : register(u1);

[numthreads(8, 8, 1)]
void CSFroxelIntegrate(uint3 tid : SV_DispatchThreadID)
{
    int cx = (int)tid.x, cy = (int)tid.y;
    if (cx >= gNx || cy >= gNy) return;

    float wx = ((cx + 0.5) / gNx * 2.0 - 1.0) * gExtent;
    float wy = ((cy + 0.5) / gNy * 2.0 - 1.0) * gExtent;

    // Per-light colour in [0,1] (twin of the lr/lg/lb precompute).
    float3 lc0 = float3((gColor0 >> 16) & 0xFF, (gColor0 >> 8) & 0xFF, gColor0 & 0xFF) / 255.0;
    float3 lc1 = float3((gColor1 >> 16) & 0xFF, (gColor1 >> 8) & 0xFF, gColor1 & 0xFF) / 255.0;
    float3 lc2 = float3((gColor2 >> 16) & 0xFF, (gColor2 >> 8) & 0xFF, gColor2 & 0xFF) / 255.0;
    float3 view = float3(gViewX, gViewY, gViewZ);
    int oct = gNoiseOctaves <= 0 ? 3 : gNoiseOctaves;

    float trans = 1.0, accR = 0.0, accG = 0.0, accB = 0.0;
    int baseIdx = (cy * gNx + cx) * gNz;

    [loop]
    for (int z = 0; z < gNz; z++)
    {
        float d0 = SliceDepth(z), d1 = SliceDepth(z + 1);
        float wz = 0.5 * (d0 + d1);
        float th = d1 - d0;

        float noiseMul = 1.0;
        if (gNoiseAmount > 0.0)
        {
            float n = FbmCloud3D(wx * gNoiseScale, wy * gNoiseScale, wz * gNoiseScale, oct);
            noiseMul = max(0.0, 1.0 + gNoiseAmount * (2.0 * n - 1.0));
        }
        float density = gBaseDensity * noiseMul;
        float ext = gExtinction * density;

        // Multi-light in-scatter (twin: sum each light's density x I x atten x phase).
        float3 sc = float3(0.0, 0.0, 0.0);
        float3 P = float3(wx, wy, wz);
        if (gNumLights > 0 && gI0 > 0.0)
        {
            float4 r = ResolveLight(gType0, float3(gDir0x, gDir0y, gDir0z),
                                    float3(gPos0x, gPos0y, gPos0z), gRange0, gInner0, gOuter0, P);
            float phase = HgPhase(gAnisotropy, dot(view, r.xyz));
            sc += (density * gI0 * r.w * phase) * lc0;
        }
        if (gNumLights > 1 && gI1 > 0.0)
        {
            float4 r = ResolveLight(gType1, float3(gDir1x, gDir1y, gDir1z),
                                    float3(gPos1x, gPos1y, gPos1z), gRange1, gInner1, gOuter1, P);
            float phase = HgPhase(gAnisotropy, dot(view, r.xyz));
            sc += (density * gI1 * r.w * phase) * lc1;
        }
        if (gNumLights > 2 && gI2 > 0.0)
        {
            float4 r = ResolveLight(gType2, float3(gDir2x, gDir2y, gDir2z),
                                    float3(gPos2x, gPos2y, gPos2z), gRange2, gInner2, gOuter2, P);
            float phase = HgPhase(gAnisotropy, dot(view, r.xyz));
            sc += (density * gI2 * r.w * phase) * lc2;
        }

        // S6 #408 temporal blend (twin of FroxelHistory.BlendAndStore) — blend this
        // cell's PRE-integration scatter + extinction toward the previous frame's,
        // then store the blended value as the new history. When no valid history
        // exists (first frame / grid change) the current values pass through (a=0)
        // and seed. Guarded by gFeedback>0 so the single-frame path is byte-identical
        // and never touches the (possibly unbound) history buffer.
        if (gFeedback > 0.0)
        {
            int cellIdx = baseIdx + z;
            if (gHistoryValid != 0)
            {
                float a = gFeedback;          // host clamps to [0,0.999]
                float omA = 1.0 - a;
                float4 hp = gHistory[cellIdx];
                sc  = sc  * omA + hp.rgb * a;
                ext = ext * omA + hp.a   * a;
            }
            gHistory[cellIdx] = float4(sc, ext);
        }

        // Front-to-back energy-conserving integration (twin of FroxelIntegrator).
        float sliceT = exp(-ext * th);
        float factor = ext > 1e-8 ? (1.0 - sliceT) / ext : th;
        accR += trans * sc.r * factor;
        accG += trans * sc.g * factor;
        accB += trans * sc.b * factor;
        trans *= sliceT;

        gVolume[baseIdx + z] = float4(accR, accG, accB, trans);
    }
}

// ---- composite by per-pixel world depth (twin of CompositeWorldDepth) ----
StructuredBuffer<uint>   gBeauty     : register(t0);
StructuredBuffer<float>  gDepth      : register(t1);
StructuredBuffer<float4> gVolumeRead : register(t2);
RWStructuredBuffer<uint> gOut        : register(u0);

[numthreads(8, 8, 1)]
void CSFroxelComposite(uint3 tid : SV_DispatchThreadID)
{
    int px = (int)tid.x, py = (int)tid.y;
    if (px >= gW || py >= gH) return;
    int idx = py * gW + px;

    uint p = gBeauty[idx];
    float depth = gDepth[idx];

    // Continuous slice (twin of FroxelGrid.DepthToSlice), clamped to the last
    // integrated slice (Nz-1) as CompositeWorldDepth does.
    float maxSlice = (float)(gNz - 1);
    float slice;
    if (depth <= gNear) slice = 0.0;
    else if (depth >= gFar) slice = (float)gNz;
    else slice = (float)gNz * log(depth / gNear) / log(gFar / gNear);
    if (slice > maxSlice) slice = maxSlice;

    int cx = (int)((px + 0.5) / gW * gNx);
    int cy = (int)((py + 0.5) / gH * gNy);
    cx = cx < 0 ? 0 : (cx >= gNx ? gNx - 1 : cx);
    cy = cy < 0 ? 0 : (cy >= gNy ? gNy - 1 : cy);
    int baseIdx = (cy * gNx + cx) * gNz;

    // Sample the integrated column (twin of SampleColumn).
    float3 inSc; float tr;
    if (slice <= 0.0) { inSc = float3(0.0, 0.0, 0.0); tr = 1.0; }
    else if (slice >= maxSlice)
    {
        float4 v = gVolumeRead[baseIdx + gNz - 1];
        inSc = v.rgb; tr = v.a;
    }
    else
    {
        int i0 = (int)slice;
        float f = slice - i0, omf = 1.0 - f;
        float4 a = gVolumeRead[baseIdx + i0];
        float4 b = gVolumeRead[baseIdx + i0 + 1];
        inSc = a.rgb * omf + b.rgb * f;
        tr = a.a * omf + b.a * f;
    }

    float rr = ((p >> 16) & 0xFF) * tr + inSc.r * 255.0;
    float gg = ((p >> 8) & 0xFF) * tr + inSc.g * 255.0;
    float bb = (p & 0xFF) * tr + inSc.b * 255.0;
    uint R = (uint)clamp(rr, 0.0, 255.0);
    uint G = (uint)clamp(gg, 0.0, 255.0);
    uint B = (uint)clamp(bb, 0.0, 255.0);
    gOut[idx] = (p & 0xFF000000u) | (R << 16) | (G << 8) | B;
}
";
}
