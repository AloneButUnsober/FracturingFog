// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.D3D/CosmicWebVolumeKernelSource.cs (#2024, Cosmic Web epic #2020)
//
// HLSL twin of the CPU reference raymarch (Engine/CosmicWeb/VolumeRaymarch.Pixel), term for term
// in float (the emission-absorption model of direct volume rendering, Drebin, Carpenter & Hanrahan,
// SIGGRAPH 1988): the slab test against the unit cube, fixed world steps from half a step inside, the
// periodic trilinear sample between cell centres, emission-absorption compositing front to back,
// the early stop and the depth AOV. One dependency-free source, two compilers: FXC (cs_5_0, D3D11,
// CosmicWebVolumeD3D11Kernel) and DXC (cs_6_0 -> SPIR-V, Vulkan, CosmicWebVolumeVulkanKernel; this
// file is linked into Rendering.Vulkan as the black hole source is). Differences from the CPU come
// only from the GPU's exp / pow / rcp, a few units in the last place, which can move a colour by a
// unit or flip a theme index at a boundary (bounded by the parity tests and the probe).
//
// Buffers: b0 = integers (+ the pixel band [BandStart, BandEnd) of this dispatch), t0 = float
// parameters (CosmicWebVolumeParams.PackFloats), t1 = the theme LUT (256 uint), t2 = the mapped
// grid (G^3 float, x fastest), u0 = colour (uint 0xAARRGGBB), u1 = depth (float bits; +inf = miss).

namespace FracturingFog.Rendering;

public static class CosmicWebVolumeKernelSource
{
    public const string Entry = "CwVolume";
    public const int GroupSize = 64;
    /// <summary>#2045 — the largest band one dispatch may march: 65,535 groups (D3D11's per-dimension limit
    /// and Vulkan's guaranteed minimum maxComputeWorkGroupCount[0]) of GroupSize pixels.</summary>
    public const int MaxBandPixels = 65535 * GroupSize;

    public const string Source = """
cbuffer CwInts : register(b0)
{
    uint Width; uint Height; uint Grid; uint Steps;
    uint DepthColour; uint Background; uint BandStart; uint BandEnd;
};

StructuredBuffer<float> P : register(t0);
StructuredBuffer<uint> Lut : register(t1);
StructuredBuffer<float> Vol : register(t2);
RWStructuredBuffer<uint> Colour : register(u0);
RWStructuredBuffer<uint> Depth : register(u1);

float Wrapf(float u, float g) { return u - g * floor(u / g); }

float SampleVol(float3 p)
{
    float g = (float)Grid;
    float3 u = (p + 0.5f) * g - 0.5f;
    float3 f0 = floor(u);
    float3 f = u - f0;
    uint x0 = (uint)Wrapf(f0.x, g), y0 = (uint)Wrapf(f0.y, g), z0 = (uint)Wrapf(f0.z, g);
    uint x1 = x0 + 1 == Grid ? 0 : x0 + 1, y1 = y0 + 1 == Grid ? 0 : y0 + 1, z1 = z0 + 1 == Grid ? 0 : z0 + 1;
    float c000 = Vol[(z0 * Grid + y0) * Grid + x0], c100 = Vol[(z0 * Grid + y0) * Grid + x1];
    float c010 = Vol[(z0 * Grid + y1) * Grid + x0], c110 = Vol[(z0 * Grid + y1) * Grid + x1];
    float c001 = Vol[(z1 * Grid + y0) * Grid + x0], c101 = Vol[(z1 * Grid + y0) * Grid + x1];
    float c011 = Vol[(z1 * Grid + y1) * Grid + x0], c111 = Vol[(z1 * Grid + y1) * Grid + x1];
    float c00 = c000 + (c100 - c000) * f.x, c10 = c010 + (c110 - c010) * f.x;
    float c01 = c001 + (c101 - c001) * f.x, c11 = c011 + (c111 - c011) * f.x;
    float c0 = c00 + (c10 - c00) * f.y, c1 = c01 + (c11 - c01) * f.y;
    return c0 + (c1 - c0) * f.z;
}

bool Slab(float o, float d, inout float t0, inout float t1)
{
    if (abs(d) < 1e-12f) return o >= -0.5f && o <= 0.5f;
    float a = (-0.5f - o) / d, b = (0.5f - o) / d;
    if (a > b) { float s = a; a = b; b = s; }
    t0 = max(t0, a); t1 = min(t1, b);
    return t1 > t0;
}

[numthreads(64, 1, 1)]
void CwVolume(uint3 dt : SV_DispatchThreadID)
{
    uint idx = BandStart + dt.x;
    if (idx >= BandEnd) return;
    uint px = idx % Width, py = idx / Width;
    float3 eye = float3(P[0], P[1], P[2]);
    float3 right = float3(P[3], P[4], P[5]);
    float3 up = float3(P[6], P[7], P[8]);
    float3 fwd = float3(P[9], P[10], P[11]);
    float focal = P[12], ds = P[13], opacity = P[14], invGamma = P[15], zFront = P[16], zSpan = P[17], brightness = P[18];
    float vx = ((float)px + 0.5f - (float)Width * 0.5f) / focal;
    float vy = -((float)py + 0.5f - (float)Height * 0.5f) / focal;
    float3 d = right * vx + up * vy + fwd;
    d *= 1.0f / sqrt(dot(d, d));
    float bgR = (float)((Background >> 16) & 0xFF), bgG = (float)((Background >> 8) & 0xFF), bgB = (float)(Background & 0xFF);
    float t0 = 0.0f, t1 = 1e30f;
    if (!Slab(eye.x, d.x, t0, t1) || !Slab(eye.y, d.y, t0, t1) || !Slab(eye.z, d.z, t0, t1))
    {
        Colour[idx] = Background;
        Depth[idx] = 0x7F800000;
        return;
    }
    float r = 0, g = 0, b = 0, T = 1.0f;
    uint depthBits = 0x7F800000;
    bool hasDepth = false;
    float fdot = dot(d, fwd);
    for (uint k = 0; k < Steps; k++)
    {
        float t = t0 + ((float)k + 0.5f) * ds;
        if (t >= t1) break;
        float v = SampleVol(eye + t * d);
        if (v <= 0.0f) continue;
        float a = 1.0f - exp(-opacity * v * ds);
        float key = DepthColour != 0 ? clamp((t * fdot - zFront) / zSpan, 0.0f, 1.0f) : v;
        uint c = Lut[min(255, (uint)(key * 256.0f))];
        float e = T * a * pow(v, invGamma) * brightness;
        r += e * (float)((c >> 16) & 0xFF); g += e * (float)((c >> 8) & 0xFF); b += e * (float)(c & 0xFF);
        T *= 1.0f - a;
        if (!hasDepth && T < 0.5f) { depthBits = asuint(t); hasDepth = true; }
        if (T < 1.0f / 512.0f) break;
    }
    r += T * bgR; g += T * bgG; b += T * bgB;
    uint pr = (uint)clamp((int)(r + 0.5f), 0, 255), pg = (uint)clamp((int)(g + 0.5f), 0, 255), pb = (uint)clamp((int)(b + 0.5f), 0, 255);
    Colour[idx] = 0xFF000000 | (pr << 16) | (pg << 8) | pb;
    Depth[idx] = depthBits;
}
""";
}
