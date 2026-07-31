// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/ReliefRaymarchGpu.cs
//
// #159 (Relief 3D Slice 3a) — GPU relief-raymarch parity twin + uniform block.
//
// Slice 3 ports the HeightfieldRaymarch2D sphere-trace to a compute shader on
// two backends (D3D #160, Vulkan #161). Neither backend can be run end-to-end
// on a headless box here, so this file captures the algorithm the shader runs
// as an EXACT scalar CPU twin — the parity oracle. The device gate (3b/3c)
// feeds identical inputs to the GPU and to this twin and diffs the two images;
// this file is the single source of truth for what "correct" means.
//
// The twin deliberately mirrors the SHADER'S scope, not the full CPU render:
//   • flat directional Lambert (three lights) + scalar ambient — NO soft
//     shadow / AO / PBR spec / IBL / reflections / triplanar / fog. Those are
//     Slice 4 (shader-side ShadingPipeline).
//   • a simple two-colour vertical gradient sky (BgBottom→BgTop by ray.y) —
//     NOT the full HDRI SkyColorHdri (also Slice 4).
//   • the #141 footprint-edge dissolve is omitted (cosmetic; Slice 4).
// Everything geometric IS faithful: perspective + ortho ray-gen, the AABB
// slab, the sphere trace over the bilinear/bicubic height field + cull mask,
// cone-epsilon growth, the 5-step bisection refine, the analytic bilinear-patch
// normal, bilinear albedo, the bounded ground plane and the isolate cutout.
//
// Inputs are the POST-pre-pass field the shader receives as an R32F texture
// (the Slice-1 cached compressed `hbuf`) plus the ReliefUniforms cbuffer twin —
// so no fractal pre-pass is re-run here. Build the uniforms with
// ReliefUniforms.Build, which routes the camera through the same
// HeightfieldRaymarch2D.BuildObliqueCamera the CPU render uses.

using System;

using FracturingFog.Models;

namespace FracturingFog.Rendering.Lighting;

/// <summary>CPU mirror of the GPU relief cbuffer (<c>ReliefParams</c> in
/// <c>ReliefRaymarchKernelSource</c>). Doubles here for the oracle; the GPU
/// backends pack a float blob from the same values. Lights are pre-resolved to
/// world-space direction vectors (LightDir(theta,phi)) with the light-orbit at
/// t=0 — the twin is static (orbit animation is a host/Slice-4 concern).</summary>
public readonly struct ReliefUniforms
{
    public readonly int W, H;          // output grid
    public readonly int Hw, Hh;        // height-field grid
    public readonly double Sy;         // world height per height unit
    public readonly double Aspect;     // W / H
    public readonly double InvLip;     // Lipschitz normalisation
    public readonly bool Bicubic;

    public readonly HeightfieldRaymarch2D.ReliefCamera Cam;

    // Three directional lights (world dir, already normalised), 0–255 colours.
    public readonly double L0x, L0y, L0z, I0; public readonly double C0r, C0g, C0b;
    public readonly double L1x, L1y, L1z, I1; public readonly double C1r, C1g, C1b;
    public readonly double L2x, L2y, L2z, I2; public readonly double C2r, C2g, C2b;
    public readonly double Ambient;

    // 4a — Cook-Torrance GGX material. SpecStrength == 0 → flat Lambert.
    public readonly double SpecStrength, Roughness, Metallic;

    public readonly bool ShowSky, Isolate;
    public readonly uint BgTop, BgBottom, FloorAlbedo, DropColor;

    public ReliefUniforms(int w, int h, int hw, int hh, double sy, double aspect,
        double invLip, bool bicubic, HeightfieldRaymarch2D.ReliefCamera cam,
        double l0x, double l0y, double l0z, double i0, double c0r, double c0g, double c0b,
        double l1x, double l1y, double l1z, double i1, double c1r, double c1g, double c1b,
        double l2x, double l2y, double l2z, double i2, double c2r, double c2g, double c2b,
        double ambient, bool showSky, bool isolate,
        uint bgTop, uint bgBottom, uint floorAlbedo, uint dropColor,
        double specStrength, double roughness, double metallic)
    {
        W = w; H = h; Hw = hw; Hh = hh; Sy = sy; Aspect = aspect;
        InvLip = invLip; Bicubic = bicubic; Cam = cam;
        L0x = l0x; L0y = l0y; L0z = l0z; I0 = i0; C0r = c0r; C0g = c0g; C0b = c0b;
        L1x = l1x; L1y = l1y; L1z = l1z; I1 = i1; C1r = c1r; C1g = c1g; C1b = c1b;
        L2x = l2x; L2y = l2y; L2z = l2z; I2 = i2; C2r = c2r; C2g = c2g; C2b = c2b;
        Ambient = ambient; ShowSky = showSky; Isolate = isolate;
        BgTop = bgTop; BgBottom = bgBottom; FloorAlbedo = floorAlbedo; DropColor = dropColor;
        SpecStrength = specStrength; Roughness = roughness; Metallic = metallic;
    }

    /// <summary>World-space direction of a directional light, matching
    /// <c>ShadingPipeline.LightDir</c> (θ around +Y, φ from +Y).</summary>
    private static (double x, double y, double z) LightDir(double theta, double phi)
    {
        double sinPhi = Math.Sin(phi);
        double x = sinPhi * Math.Cos(theta), y = Math.Cos(phi), z = sinPhi * Math.Sin(theta);
        double l = Math.Sqrt(x * x + y * y + z * z);
        if (l < 1e-12) l = 1.0;
        return (x / l, y / l, z / l);
    }

    /// <summary>Assemble the uniform block for a relief render. Routes the
    /// camera through <see cref="HeightfieldRaymarch2D.BuildObliqueCamera"/> so
    /// the twin, the GPU kernel and the CPU render all frame identically. Lights
    /// come from <paramref name="fx"/> (three directional lights + ambient), sky
    /// colours from BgTop/BgBottom. Field descriptors (<paramref name="sy"/>,
    /// <paramref name="invLip"/>, <paramref name="maxH"/>) come from the caller's
    /// pre-pass so this never re-runs the fractal filter chain.</summary>
    public static ReliefUniforms Build(int w, int h, int hw, int hh,
        double sy, double aspect, double invLip, double maxH,
        FractalParameters p, in LightingFxData fx)
    {
        var cam = HeightfieldRaymarch2D.BuildObliqueCamera(w, h, aspect, sy, maxH, p);
        var d0 = LightDir(fx.Light1.Theta, fx.Light1.Phi);
        var d1 = LightDir(fx.Light2.Theta, fx.Light2.Phi);
        var d2 = LightDir(fx.Light3.Theta, fx.Light3.Phi);
        return new ReliefUniforms(w, h, hw, hh, sy, aspect, invLip, p.Relief2DBicubicHeight, cam,
            d0.x, d0.y, d0.z, fx.Light1.Intensity, (fx.Light1.Color >> 16) & 0xFF, (fx.Light1.Color >> 8) & 0xFF, fx.Light1.Color & 0xFF,
            d1.x, d1.y, d1.z, fx.Light2.Intensity, (fx.Light2.Color >> 16) & 0xFF, (fx.Light2.Color >> 8) & 0xFF, fx.Light2.Color & 0xFF,
            d2.x, d2.y, d2.z, fx.Light3.Intensity, (fx.Light3.Color >> 16) & 0xFF, (fx.Light3.Color >> 8) & 0xFF, fx.Light3.Color & 0xFF,
            fx.AmbientStrength, fx.ShowSkyBackdrop, p.Relief2DIsolate,
            fx.BgTopColor, fx.BgBottomColor, HeightfieldRaymarch2D.FloorAlbedoArgb, HeightfieldRaymarch2D.DropColorArgb,
            fx.SpecularStrength, fx.Roughness, fx.Metallic);
    }
}

/// <summary>#159 (Slice 3a) — scalar CPU twin of the GPU relief-raymarch
/// compute shader. See the file header for scope. Feed it the same compressed
/// height field, cull mask, albedo and <see cref="ReliefUniforms"/> the shader
/// gets; the device gate (3b/3c) diffs GPU output against this.</summary>
public static class ReliefRaymarchGpu
{
    /// <summary>Render the relief field into <paramref name="dst"/> (packed
    /// ARGB), reporting the fraction of pixels that hit the terrain. The twin of
    /// the shader entry point: one primary ray per pixel, flat Lambert shading.</summary>
    public static void RenderCpuMirror(in ReliefUniforms u, float[] hbuf, byte[]? keep,
                                       uint[] albedo, uint[] dst, out double hitFraction)
    {
        hitFraction = 0.0;
        int w = u.W, h = u.H;
        int n = w * h;
        if (w <= 2 || h <= 2 || u.Hw <= 2 || u.Hh <= 2
            || hbuf.Length < u.Hw * u.Hh || albedo.Length < n || dst.Length < n)
        {
            if (!ReferenceEquals(albedo, dst)) Array.Copy(albedo, dst, n);
            return;
        }

        var de = new HeightfieldRaymarch2D.HeightDe(
            hbuf, u.Hw, u.Hh, u.Sy, u.Aspect, u.InvLip, u.Bicubic, keep);
        var cam = u.Cam;
        double aspect = u.Aspect;

        long hitCount = 0;
        for (int py = 0; py < h; py++)
        for (int px = 0; px < w; px++)
        {
            var (col, hit) = SamplePixel(px + 0.5, py + 0.5, in u, in cam, in de, albedo);
            dst[py * w + px] = col;
            if (hit) hitCount++;
        }
        hitFraction = (double)hitCount / n;
    }

    /// <summary>One primary ray → shaded colour + terrain-hit flag. Line-for-line
    /// twin of the HLSL CSMain body (see ReliefRaymarchKernelSource).</summary>
    private static (uint col, bool terrainHit) SamplePixel(
        double sxpix, double sypix, in ReliefUniforms u,
        in HeightfieldRaymarch2D.ReliefCamera cam,
        in HeightfieldRaymarch2D.HeightDe de, uint[] albedo)
    {
        int w = u.W, h = u.H;
        double aspect = u.Aspect;
        double ndcx = 2.0 * sxpix / w - 1.0;
        double ndcy = 1.0 - 2.0 * sypix / h;

        double ox, oy, oz, rdx, rdy, rdz;
        if (cam.Ortho)
        {
            double sxo = ndcx * aspect * cam.OrthoHalfV, syo = ndcy * cam.OrthoHalfV;
            ox = cam.CamX + cam.RX * sxo + cam.UX * syo;
            oy = cam.CamY + /* rY==0 */      cam.UY * syo;
            oz = cam.CamZ + cam.RZ * sxo + cam.UZ * syo;
            rdx = cam.FX; rdy = cam.FY; rdz = cam.FZ;
        }
        else
        {
            ox = cam.CamX; oy = cam.CamY; oz = cam.CamZ;
            double a = ndcx * aspect * cam.TanHalf, b = ndcy * cam.TanHalf;
            rdx = cam.FX + cam.RX * a + cam.UX * b;
            rdy = cam.FY + /* rY==0 */    cam.UY * b;
            rdz = cam.FZ + cam.RZ * a + cam.UZ * b;
            double il = 1.0 / Math.Sqrt(rdx * rdx + rdy * rdy + rdz * rdz);
            rdx *= il; rdy *= il; rdz *= il;
        }

        // Ray-slab against the terrain AABB.
        double t0 = 0.0, t1 = double.MaxValue;
        bool inside = HeightfieldRaymarch2D.SlabHit(ox, rdx, -cam.Bx, cam.Bx, ref t0, ref t1)
                   && HeightfieldRaymarch2D.SlabHit(oy, rdy, 0.0, cam.By, ref t0, ref t1)
                   && HeightfieldRaymarch2D.SlabHit(oz, rdz, -cam.Bz, cam.Bz, ref t0, ref t1);
        if (inside)
        {
            double t = Math.Max(t0, 0.0) + cam.Eps0;
            double tPrev = t, d = 0.0;
            bool hit = false;
            for (int s = 0; s < cam.MaxSteps && t < t1 + cam.By; s++)
            {
                d = de.Evaluate(ox + rdx * t, oy + rdy * t, oz + rdz * t);
                double epsT = cam.Eps0 + cam.PixelAngle * t;
                if (d < epsT) { hit = true; break; }
                tPrev = t;
                t += Math.Max(d, epsT * 0.5);
            }

            if (hit)
            {
                double tLo = tPrev, tHi = t;
                for (int b2 = 0; b2 < 5; b2++)
                {
                    double tm = 0.5 * (tLo + tHi);
                    if (de.Evaluate(ox + rdx * tm, oy + rdy * tm, oz + rdz * tm) > 0.0)
                        tLo = tm; else tHi = tm;
                }
                double tf = tHi;
                double hx = ox + rdx * tf, hy = oy + rdy * tf, hz = oz + rdz * tf;

                var (dHx, dHz) = de.SampleGrad(hx, hz);
                double nx = -dHx, ny = 1.0, nz = -dHz;
                double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                nx /= nl; ny /= nl; nz /= nl;

                double uu = hx / aspect + 0.5, vv = hz + 0.5;
                uint alb = HeightfieldRaymarch2D.SampleAlbedoBilinear(albedo, w, h, uu, vv);
                return (ShadeFlat(nx, ny, nz, -rdx, -rdy, -rdz, alb, in u), true);
            }
        }

        // Terrain miss → bounded ground plane, else sky / drop.
        if (cam.GroundPlane && rdy < -1e-9)
        {
            double tp = (0.0 - oy) / rdy;
            if (tp > 0.0)
            {
                double gx = ox + rdx * tp, gz = oz + rdz * tp;
                if (Math.Abs(gx) <= cam.FloorBx && Math.Abs(gz) <= cam.FloorBz)
                    return (ShadeFlat(0.0, 1.0, 0.0, -rdx, -rdy, -rdz, u.FloorAlbedo, in u), false);
            }
        }
        uint bg = u.ShowSky ? GradientSky(rdy, in u) : u.DropColor;
        if (u.Isolate) bg &= 0x00FFFFFFu;
        return (bg, false);
    }

    /// <summary>Flat three-light Lambert + scalar ambient over a packed ARGB
    /// albedo. The diffuse-only subset of ShadingPipeline's lit combine
    /// (spec/AO/IBL off): <c>s = amb + (Σ Iᵢ·max(0,N·Lᵢ)·Colᵢ / 255)·(1−amb)</c>,
    /// then <c>out = albedo · s</c>. Preserves the albedo's alpha.</summary>
    private static uint ShadeFlat(double nx, double ny, double nz,
                                  double vx, double vy, double vz, uint albedo, in ReliefUniforms u)
    {
        double sR = 0, sG = 0, sB = 0;
        Accum(u.I0, u.C0r, u.C0g, u.C0b, u.L0x, u.L0y, u.L0z, nx, ny, nz, ref sR, ref sG, ref sB);
        Accum(u.I1, u.C1r, u.C1g, u.C1b, u.L1x, u.L1y, u.L1z, nx, ny, nz, ref sR, ref sG, ref sB);
        Accum(u.I2, u.C2r, u.C2g, u.C2b, u.L2x, u.L2y, u.L2z, nx, ny, nz, ref sR, ref sG, ref sB);

        double aR = (albedo >> 16) & 0xFF, aG = (albedo >> 8) & 0xFF, aB = albedo & 0xFF;

        // 4a — Cook-Torrance GGX spec + metallic diffuse suppression. Diffuse-only
        // (byte-identical to the flat-Lambert twin) when SpecStrength == 0.
        double specR = 0, specG = 0, specB = 0, diffSuppress = 1.0;
        if (u.SpecStrength > 0)
        {
            double rough = Math.Max(0.05, u.Roughness);
            double a = rough * rough, a2 = a * a;
            double kg = (rough + 1.0) * (rough + 1.0) / 8.0;
            double F0r = 0.04 + (aR / 255.0 - 0.04) * u.Metallic;
            double F0g = 0.04 + (aG / 255.0 - 0.04) * u.Metallic;
            double F0b = 0.04 + (aB / 255.0 - 0.04) * u.Metallic;
            double NdotV = Math.Max(0.0, nx * vx + ny * vy + nz * vz);
            SpecAccum(u.I0, u.C0r, u.C0g, u.C0b, u.L0x, u.L0y, u.L0z, nx, ny, nz, vx, vy, vz, NdotV, a2, kg, F0r, F0g, F0b, u.SpecStrength, ref specR, ref specG, ref specB);
            SpecAccum(u.I1, u.C1r, u.C1g, u.C1b, u.L1x, u.L1y, u.L1z, nx, ny, nz, vx, vy, vz, NdotV, a2, kg, F0r, F0g, F0b, u.SpecStrength, ref specR, ref specG, ref specB);
            SpecAccum(u.I2, u.C2r, u.C2g, u.C2b, u.L2x, u.L2y, u.L2z, nx, ny, nz, vx, vy, vz, NdotV, a2, kg, F0r, F0g, F0b, u.SpecStrength, ref specR, ref specG, ref specB);
            diffSuppress = 1.0 - u.Metallic;
        }

        double amb = u.Ambient;
        sR = amb + (sR / 255.0) * (1.0 - amb) * diffSuppress;
        sG = amb + (sG / 255.0) * (1.0 - amb) * diffSuppress;
        sB = amb + (sB / 255.0) * (1.0 - amb) * diffSuppress;
        double r = aR * sR + specR;
        double g = aG * sG + specG;
        double b = aB * sB + specB;
        uint A = (albedo >> 24) & 0xFFu;
        return (A << 24)
             | ((uint)Math.Clamp(r + 0.5, 0, 255) << 16)
             | ((uint)Math.Clamp(g + 0.5, 0, 255) << 8)
             | (uint)Math.Clamp(b + 0.5, 0, 255);
    }

    /// <summary>One directional light's Cook-Torrance GGX specular — scalar twin
    /// of the HLSL <c>SpecAccum</c> and of <see cref="ShadingPipeline.AccumulateSpec"/>
    /// (Schlick F per channel, Smith joint G, GGX D). Accumulates in 0–255 space.</summary>
    private static void SpecAccum(double intensity, double cr, double cg, double cb,
        double lx, double ly, double lz, double nx, double ny, double nz,
        double vx, double vy, double vz, double NdotV, double a2, double kg,
        double F0r, double F0g, double F0b, double specStrength,
        ref double specR, ref double specG, ref double specB)
    {
        if (intensity <= 0) return;
        double NdotL = nx * lx + ny * ly + nz * lz;
        if (NdotL <= 0) return;
        double hx = lx + vx, hy = ly + vy, hz = lz + vz;
        double hl2 = hx * hx + hy * hy + hz * hz;
        if (hl2 < 1e-12) return;
        double invH = 1.0 / Math.Sqrt(hl2);
        hx *= invH; hy *= invH; hz *= invH;
        double NdotH = Math.Max(0.0, nx * hx + ny * hy + nz * hz);
        double VdotH = Math.Max(0.0, vx * hx + vy * hy + vz * hz);
        double denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
        double D = a2 / (Math.PI * denom * denom);
        double G1V = NdotV / (NdotV * (1.0 - kg) + kg);
        double G1L = NdotL / (NdotL * (1.0 - kg) + kg);
        double G = G1V * G1L;
        double omv = 1.0 - VdotH;
        double Fc = omv * omv * omv * omv * omv;
        double specBase = (D * G / Math.Max(4.0 * NdotV, 1e-4)) * specStrength * intensity;
        specR += specBase * (F0r + (1.0 - F0r) * Fc) * cr;
        specG += specBase * (F0g + (1.0 - F0g) * Fc) * cg;
        specB += specBase * (F0b + (1.0 - F0b) * Fc) * cb;
    }

    private static void Accum(double intensity, double cr, double cg, double cb,
        double lx, double ly, double lz, double nx, double ny, double nz,
        ref double sR, ref double sG, ref double sB)
    {
        if (intensity <= 0) return;
        double diffuse = Math.Max(0.0, nx * lx + ny * ly + nz * lz) * intensity;
        sR += cr * diffuse; sG += cg * diffuse; sB += cb * diffuse;
    }

    /// <summary>Two-colour vertical gradient sky (BgBottom at the horizon, BgTop
    /// overhead) by ray elevation. The flat-Lambert twin's stand-in for the full
    /// HDRI sky (Slice 4).</summary>
    private static uint GradientSky(double rdy, in ReliefUniforms u)
    {
        double t = Math.Clamp(0.5 * rdy + 0.5, 0.0, 1.0);
        uint a = u.BgBottom, b = u.BgTop;
        uint R = (uint)((((a >> 16) & 0xFF) * (1 - t) + ((b >> 16) & 0xFF) * t) + 0.5);
        uint G = (uint)((((a >> 8) & 0xFF) * (1 - t) + ((b >> 8) & 0xFF) * t) + 0.5);
        uint B = (uint)(((a & 0xFF) * (1 - t) + (b & 0xFF) * t) + 0.5);
        return 0xFF000000u | (R << 16) | (G << 8) | B;
    }
}
