// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// VolumeRaymarch.cs (#2024, Cosmic Web epic #2020)
//
// Emission-absorption raymarch of the mapped density grid (DensityGrid) through the orbit camera:
// the CPU reference of the D3D11 / Vulkan kernel (Rendering.D3D/CosmicWebVolumeKernelSource), term
// for term in float. The front-to-back compositing of emitted colour through absorbing material is
// the standard direct volume rendering model (Drebin, Carpenter & Hanrahan, "Volume rendering",
// SIGGRAPH 1988); FF does not claim it. FF's own choices:
//   - the box is the unit cube centred on the origin (as the point splat); a ray is clipped to it
//     (slab test) and sampled at a fixed world step ds = sqrt(3) / Steps from half a step inside;
//   - the grid is periodic (as the box), sampled trilinearly between cell centres;
//   - a sample of mapped value v has extinction Opacity * v (per box side), so its opacity over a
//     step is 1 - exp(-Opacity v ds), and emits that opacity times Brightness times v^(1/gamma) of the
//     theme colour (Brightness lets a thin, faint web glow without becoming opaque): the theme at v
//     (Density colouring) or at the sample's depth across the box (Depth colouring, as the splat);
//   - the ray stops once its transmittance is below 1/512; what is left shows the background;
//   - depth AOV: the ray distance where the transmittance first falls below 1/2 (else a miss).

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.CosmicWeb;

/// <summary>#2024 — everything the raymarch kernel reads (CPU and GPU), in float.</summary>
public readonly record struct CosmicWebVolumeParams(
    float EyeX, float EyeY, float EyeZ,
    float RightX, float RightY, float RightZ,
    float UpX, float UpY, float UpZ,
    float FwdX, float FwdY, float FwdZ,
    float Focal, float Ds, float Opacity, float InvGamma, float ZFront, float ZSpan, float Brightness,
    int Width, int Height, int Grid, int Steps, bool DepthColour, uint Background)
{
    /// <summary>Number of floats in <see cref="PackFloats"/> (the kernel's t0 layout).</summary>
    public const int FloatCount = 19;

    public float[] PackFloats() => new[]
    {
        EyeX, EyeY, EyeZ, RightX, RightY, RightZ, UpX, UpY, UpZ, FwdX, FwdY, FwdZ,
        Focal, Ds, Opacity, InvGamma, ZFront, ZSpan, Brightness,
    };
}

/// <summary>#2024 — one frame: packed colours (0xAARRGGBB) and the depth AOV (+inf = miss).</summary>
public sealed record CosmicWebVolumeFrame(uint[] Colour, float[] Depth);

public static class VolumeRaymarch
{
    public const float StopTransmittance = 1f / 512f;
    public const float DepthTransmittance = 0.5f;

    /// <summary>Kernel parameters for a camera and settings.</summary>
    public static CosmicWebVolumeParams ParamsFor(MathPlane3DCamera cam, int grid, int steps, double opacity, double gamma,
        bool depthColour, uint background, double brightness = 1.0)
    {
        var (_, _, targetZ) = cam.View(0, 0, 0);
        const double halfDiag = 0.8660254037844386;
        int st = Math.Clamp(steps, 8, 4096);
        return new CosmicWebVolumeParams(
            (float)cam.Eye.X, (float)cam.Eye.Y, (float)cam.Eye.Z,
            (float)cam.Right.X, (float)cam.Right.Y, (float)cam.Right.Z,
            (float)cam.Up.X, (float)cam.Up.Y, (float)cam.Up.Z,
            (float)cam.Forward.X, (float)cam.Forward.Y, (float)cam.Forward.Z,
            (float)cam.Focal, (float)(Math.Sqrt(3.0) / st), (float)Math.Max(0.0, double.IsFinite(opacity) ? opacity : 0.0),
            (float)(1.0 / (double.IsFinite(gamma) ? Math.Clamp(gamma, 0.1, 10.0) : 2.2)),
            (float)(targetZ - halfDiag), (float)(2 * halfDiag),
            (float)(double.IsFinite(brightness) ? Math.Clamp(brightness, 0.0, 1000.0) : 1.0),
            cam.Width, cam.Height, grid, st, depthColour, background | 0xFF000000u);
    }

    /// <summary>The theme baked to 256 colours (index = round down of 256 x, clamped).</summary>
    public static uint[] BakeLut(FracturingFog.Interefaces.IColorMap map)
    {
        map.MaxIterations = 256;
        var lut = new uint[256];
        for (int i = 0; i < 256; i++) lut[i] = (uint)map.Map(i, 0f, 256) | 0xFF000000u;
        return lut;
    }

    private static float Wrapf(float u, int g)
    {
        // u in cell units; periodic.
        float m = u - g * MathF.Floor(u / g);
        return m;
    }

    /// <summary>Trilinear, periodic, between cell centres (cell k's centre at k + 0.5 in grid units).</summary>
    public static float Sample(float[] v, int g, float px, float py, float pz)
    {
        float ux = (px + 0.5f) * g - 0.5f, uy = (py + 0.5f) * g - 0.5f, uz = (pz + 0.5f) * g - 0.5f;
        float fx0 = MathF.Floor(ux), fy0 = MathF.Floor(uy), fz0 = MathF.Floor(uz);
        float fx = ux - fx0, fy = uy - fy0, fz = uz - fz0;
        int x0 = (int)Wrapf(fx0, g), y0 = (int)Wrapf(fy0, g), z0 = (int)Wrapf(fz0, g);
        int x1 = x0 + 1 == g ? 0 : x0 + 1, y1 = y0 + 1 == g ? 0 : y0 + 1, z1 = z0 + 1 == g ? 0 : z0 + 1;
        float c000 = v[(z0 * g + y0) * g + x0], c100 = v[(z0 * g + y0) * g + x1];
        float c010 = v[(z0 * g + y1) * g + x0], c110 = v[(z0 * g + y1) * g + x1];
        float c001 = v[(z1 * g + y0) * g + x0], c101 = v[(z1 * g + y0) * g + x1];
        float c011 = v[(z1 * g + y1) * g + x0], c111 = v[(z1 * g + y1) * g + x1];
        float c00 = c000 + (c100 - c000) * fx, c10 = c010 + (c110 - c010) * fx;
        float c01 = c001 + (c101 - c001) * fx, c11 = c011 + (c111 - c011) * fx;
        float c0 = c00 + (c10 - c00) * fy, c1 = c01 + (c11 - c01) * fy;
        return c0 + (c1 - c0) * fz;
    }

    /// <summary>One pixel (the kernel's body).</summary>
    public static (uint Colour, float Depth) Pixel(in CosmicWebVolumeParams p, float[] grid, uint[] lut, int px, int py)
    {
        float vx = (px + 0.5f - p.Width * 0.5f) / p.Focal, vy = -(py + 0.5f - p.Height * 0.5f) / p.Focal;
        float dx = p.RightX * vx + p.UpX * vy + p.FwdX;
        float dy = p.RightY * vx + p.UpY * vy + p.FwdY;
        float dz = p.RightZ * vx + p.UpZ * vy + p.FwdZ;
        float inv = 1f / MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        dx *= inv; dy *= inv; dz *= inv;
        float bgR = (p.Background >> 16) & 0xFF, bgG = (p.Background >> 8) & 0xFF, bgB = p.Background & 0xFF;
        // Slab test against [-0.5, 0.5]^3.
        float t0 = 0f, t1 = 1e30f;
        if (!Slab(p.EyeX, dx, ref t0, ref t1) || !Slab(p.EyeY, dy, ref t0, ref t1) || !Slab(p.EyeZ, dz, ref t0, ref t1))
            return (p.Background, float.PositiveInfinity);
        float r = 0, gC = 0, b = 0, T = 1f, depth = float.PositiveInfinity;
        // Directional fwd projection of the ray (view depth per unit distance).
        float fdot = dx * p.FwdX + dy * p.FwdY + dz * p.FwdZ;
        for (int k = 0; k < p.Steps; k++)
        {
            float t = t0 + (k + 0.5f) * p.Ds;
            if (t >= t1) break;
            float sx = p.EyeX + t * dx, sy = p.EyeY + t * dy, sz = p.EyeZ + t * dz;
            float v = Sample(grid, p.Grid, sx, sy, sz);
            if (v <= 0f) continue;
            float a = 1f - MathF.Exp(-p.Opacity * v * p.Ds);
            float key = p.DepthColour ? Math.Clamp((t * fdot - p.ZFront) / p.ZSpan, 0f, 1f) : v;
            uint c = lut[Math.Min(255, (int)(key * 256f))];
            float e = T * a * MathF.Pow(v, p.InvGamma) * p.Brightness;
            r += e * ((c >> 16) & 0xFF); gC += e * ((c >> 8) & 0xFF); b += e * (c & 0xFF);
            T *= 1f - a;
            if (depth == float.PositiveInfinity && T < DepthTransmittance) depth = t;
            if (T < StopTransmittance) break;
        }
        r += T * bgR; gC += T * bgG; b += T * bgB;
        uint pr = (uint)Math.Clamp((int)(r + 0.5f), 0, 255), pg = (uint)Math.Clamp((int)(gC + 0.5f), 0, 255), pb = (uint)Math.Clamp((int)(b + 0.5f), 0, 255);
        return (0xFF000000u | pr << 16 | pg << 8 | pb, depth);
    }

    private static bool Slab(float o, float d, ref float t0, ref float t1)
    {
        if (MathF.Abs(d) < 1e-12f) return o >= -0.5f && o <= 0.5f;
        float a = (-0.5f - o) / d, bb = (0.5f - o) / d;
        if (a > bb) (a, bb) = (bb, a);
        t0 = MathF.Max(t0, a); t1 = MathF.Min(t1, bb);
        return t1 > t0;
    }

    /// <summary>The CPU reference frame.</summary>
    public static CosmicWebVolumeFrame Render(in CosmicWebVolumeParams p, float[] grid, uint[] lut, CancellationToken ct = default)
    {
        var pp = p;
        var colour = new uint[p.Width * p.Height];
        var depth = new float[p.Width * p.Height];
        Parallel.For(0, p.Height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            for (int x = 0; x < pp.Width; x++)
            {
                var (c, d) = Pixel(pp, grid, lut, x, y);
                colour[y * pp.Width + x] = c;
                depth[y * pp.Width + x] = d;
            }
        });
        return new CosmicWebVolumeFrame(colour, depth);
    }
}
