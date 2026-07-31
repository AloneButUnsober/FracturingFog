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

    // 4b — per-light soft shadow (IQ penumbra DE-march). ShadowSteps == 0 → off.
    public readonly int ShadowSteps, ShadowLightMask;
    public readonly double ShadowSoftK;

    // 4c — DE-cone ambient occlusion. AoSamples == 0 → off.
    public readonly int AoSamples;
    public readonly double AoStrength;

    // 4d — IBL-modulated ambient (gradient/solid env) + triplanar procedural
    // texture. IblStrength == 0 → scalar ambient; TriplanarStrength == 0 → off.
    public readonly double IblStrength;
    public readonly int SkyMode;
    public readonly int TriplanarKind;
    public readonly double TriplanarStrength, TriplanarScale;
    public readonly uint TriplanarTint;

    // 4e — Beer-Lambert fog + single-scatter volumetric in-scatter. FogDensity
    // == 0 → no fog. VolumeSteps == 0 (with FogDensity > 0) → legacy exp fog;
    // VolumeSteps > 0 → in-scatter walk (key light only). FBM cloud-noise +
    // cloud self-shadow + reflections are deferred to 4e-ii.
    public readonly double FogDensity, FogHeightFalloff;
    public readonly int VolumeSteps;
    public readonly double VolumeStepsFalloff;

    // 4f — empty-space-skip max-height grid. EmptySkip == 0 → no skip (the
    // byte-identical slow march). MipW/MipH/MipBlk describe the coarse grid the
    // twin and both kernels build from hbuf via ReliefHeightMip.
    public readonly int EmptySkip, MipW, MipH, MipBlk;

    public readonly bool ShowSky, Isolate;
    public readonly uint BgTop, BgBottom, FloorAlbedo, DropColor;

    public ReliefUniforms(int w, int h, int hw, int hh, double sy, double aspect,
        double invLip, bool bicubic, HeightfieldRaymarch2D.ReliefCamera cam,
        double l0x, double l0y, double l0z, double i0, double c0r, double c0g, double c0b,
        double l1x, double l1y, double l1z, double i1, double c1r, double c1g, double c1b,
        double l2x, double l2y, double l2z, double i2, double c2r, double c2g, double c2b,
        double ambient, bool showSky, bool isolate,
        uint bgTop, uint bgBottom, uint floorAlbedo, uint dropColor,
        double specStrength, double roughness, double metallic,
        int shadowSteps, double shadowSoftK, int shadowLightMask,
        int aoSamples, double aoStrength,
        double iblStrength, int skyMode,
        int triplanarKind, double triplanarStrength, double triplanarScale, uint triplanarTint,
        double fogDensity, double fogHeightFalloff, int volumeSteps, double volumeStepsFalloff,
        int emptySkip, int mipW, int mipH, int mipBlk)
    {
        W = w; H = h; Hw = hw; Hh = hh; Sy = sy; Aspect = aspect;
        InvLip = invLip; Bicubic = bicubic; Cam = cam;
        L0x = l0x; L0y = l0y; L0z = l0z; I0 = i0; C0r = c0r; C0g = c0g; C0b = c0b;
        L1x = l1x; L1y = l1y; L1z = l1z; I1 = i1; C1r = c1r; C1g = c1g; C1b = c1b;
        L2x = l2x; L2y = l2y; L2z = l2z; I2 = i2; C2r = c2r; C2g = c2g; C2b = c2b;
        Ambient = ambient; ShowSky = showSky; Isolate = isolate;
        BgTop = bgTop; BgBottom = bgBottom; FloorAlbedo = floorAlbedo; DropColor = dropColor;
        SpecStrength = specStrength; Roughness = roughness; Metallic = metallic;
        ShadowSteps = shadowSteps; ShadowSoftK = shadowSoftK; ShadowLightMask = shadowLightMask;
        AoSamples = aoSamples; AoStrength = aoStrength;
        IblStrength = iblStrength; SkyMode = skyMode;
        TriplanarKind = triplanarKind; TriplanarStrength = triplanarStrength;
        TriplanarScale = triplanarScale; TriplanarTint = triplanarTint;
        FogDensity = fogDensity; FogHeightFalloff = fogHeightFalloff;
        VolumeSteps = volumeSteps; VolumeStepsFalloff = volumeStepsFalloff;
        EmptySkip = emptySkip; MipW = mipW; MipH = mipH; MipBlk = mipBlk;
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
            fx.SpecularStrength, fx.Roughness, fx.Metallic,
            fx.ShadowSteps, fx.ShadowSoftK, fx.ShadowLightMask,
            fx.AoSamples, fx.AoStrength,
            fx.IblStrength, (int)fx.SkyMode,
            (int)fx.TriplanarKind, fx.TriplanarStrength, fx.TriplanarScale, fx.TriplanarTint,
            fx.FogDensity, fx.FogHeightFalloff, fx.VolumeSteps, fx.VolumeStepsFalloff,
            p.Relief2DEmptySkip ? 1 : 0,
            ReliefHeightMip.GridDim(hw, ReliefHeightMip.Blk),
            ReliefHeightMip.GridDim(hh, ReliefHeightMip.Blk), ReliefHeightMip.Blk);
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
        => RenderCpuMirror(in u, hbuf, keep, albedo, dst, out hitFraction, out _);

    /// <summary>As <see cref="RenderCpuMirror(in ReliefUniforms,float[],byte[],uint[],uint[],out double)"/>,
    /// also reporting the total primary sphere-trace step count (one DE evaluation
    /// per march iteration) so the 4f empty-space-skip win is measurable headlessly:
    /// render with EmptySkip off and on and compare <paramref name="marchSteps"/>.</summary>
    public static void RenderCpuMirror(in ReliefUniforms u, float[] hbuf, byte[]? keep,
                                       uint[] albedo, uint[] dst, out double hitFraction, out long marchSteps)
    {
        hitFraction = 0.0;
        marchSteps = 0;
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

        // 4f — build the coarse max-height grid once (only when the skip is on).
        // Same pure function the GPU kernels upload as the t3 SRV.
        float[]? mip = u.EmptySkip != 0
            ? ReliefHeightMip.BuildMaxGrid(hbuf, u.Hw, u.Hh, u.MipBlk, out _, out _)
            : null;

        long hitCount = 0, steps = 0;
        for (int py = 0; py < h; py++)
        for (int px = 0; px < w; px++)
        {
            var (col, hit) = SamplePixel(px + 0.5, py + 0.5, in u, in cam, in de, albedo, mip, ref steps);
            dst[py * w + px] = col;
            if (hit) hitCount++;
        }
        hitFraction = (double)hitCount / n;
        marchSteps = steps;
    }

    /// <summary>One primary ray → shaded colour + terrain-hit flag. Line-for-line
    /// twin of the HLSL CSMain body (see ReliefRaymarchKernelSource).</summary>
    private static (uint col, bool terrainHit) SamplePixel(
        double sxpix, double sypix, in ReliefUniforms u,
        in HeightfieldRaymarch2D.ReliefCamera cam,
        in HeightfieldRaymarch2D.HeightDe de, uint[] albedo,
        float[]? mip, ref long marchSteps)
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
                marchSteps++;
                d = de.Evaluate(ox + rdx * t, oy + rdy * t, oz + rdz * t);
                double epsT = cam.Eps0 + cam.PixelAngle * t;
                if (d < epsT) { hit = true; break; }
                tPrev = t;
                double adv = Math.Max(d, epsT * 0.5);
                // 4f — empty-space skip. When the ray point is safely above the
                // coarse block max, leap to the block-max plane / cell exit instead
                // of the slope-limited point DE. Conservative (never overshoots the
                // first hit); only ever enlarges the advance.
                if (mip is not null)
                {
                    double skip = EmptySkipDist(ox + rdx * t, oy + rdy * t, oz + rdz * t,
                                                rdx, rdy, rdz, epsT, in u, mip);
                    if (skip > adv) adv = skip;
                }
                t += adv;
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
                if (u.TriplanarStrength > 0 && u.TriplanarKind != 0)
                    alb = ApplyTriplanar(alb, hx, hy, hz, nx, ny, nz, in u);
                uint shaded = ShadeFlat(nx, ny, nz, -rdx, -rdy, -rdz, hx, hy, hz, alb, in de, in u);
                shaded = ApplyFogVolume(shaded, ox, oy, oz, rdx, rdy, rdz, tf, in de, in u);
                return (shaded, true);
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
                {
                    uint fShaded = ShadeFlat(0.0, 1.0, 0.0, -rdx, -rdy, -rdz, gx, 0.0, gz, u.FloorAlbedo, in de, in u);
                    fShaded = ApplyFogVolume(fShaded, ox, oy, oz, rdx, rdy, rdz, tp, in de, in u);
                    return (fShaded, false);
                }
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
                                  double vx, double vy, double vz,
                                  double px, double py, double pz, uint albedo,
                                  in HeightfieldRaymarch2D.HeightDe de, in ReliefUniforms u)
    {
        // 4b — per-light soft shadow. IQ penumbra DE-march toward each light,
        // gating DIRECT lighting (diffuse + spec) only; ambient is left alone.
        // Origin pushed eps·4 along the normal so the first sample doesn't hit
        // the surface itself. Reuses ShadingPipeline.SoftShadow so the twin is
        // exact vs the CPU render; the HLSL SoftShadow mirrors it.
        double sh0 = 1.0, sh1 = 1.0, sh2 = 1.0;
        if (u.ShadowSteps > 0)
        {
            double eps = u.Cam.Eps0;
            double bias = eps * 4.0;
            double ox = px + nx * bias, oy = py + ny * bias, oz = pz + nz * bias;
            double k = u.ShadowSoftK;
            if ((u.ShadowLightMask & 0x1) != 0 && u.I0 > 0)
                sh0 = ShadingPipeline.SoftShadow(in de, ox, oy, oz, u.L0x, u.L0y, u.L0z, eps, 12.0, k, u.ShadowSteps);
            if ((u.ShadowLightMask & 0x2) != 0 && u.I1 > 0)
                sh1 = ShadingPipeline.SoftShadow(in de, ox, oy, oz, u.L1x, u.L1y, u.L1z, eps, 12.0, k, u.ShadowSteps);
            if ((u.ShadowLightMask & 0x4) != 0 && u.I2 > 0)
                sh2 = ShadingPipeline.SoftShadow(in de, ox, oy, oz, u.L2x, u.L2y, u.L2z, eps, 12.0, k, u.ShadowSteps);
        }

        double sR = 0, sG = 0, sB = 0;
        Accum(u.I0 * sh0, u.C0r, u.C0g, u.C0b, u.L0x, u.L0y, u.L0z, nx, ny, nz, ref sR, ref sG, ref sB);
        Accum(u.I1 * sh1, u.C1r, u.C1g, u.C1b, u.L1x, u.L1y, u.L1z, nx, ny, nz, ref sR, ref sG, ref sB);
        Accum(u.I2 * sh2, u.C2r, u.C2g, u.C2b, u.L2x, u.L2y, u.L2z, nx, ny, nz, ref sR, ref sG, ref sB);

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
            SpecAccum(u.I0 * sh0, u.C0r, u.C0g, u.C0b, u.L0x, u.L0y, u.L0z, nx, ny, nz, vx, vy, vz, NdotV, a2, kg, F0r, F0g, F0b, u.SpecStrength, ref specR, ref specG, ref specB);
            SpecAccum(u.I1 * sh1, u.C1r, u.C1g, u.C1b, u.L1x, u.L1y, u.L1z, nx, ny, nz, vx, vy, vz, NdotV, a2, kg, F0r, F0g, F0b, u.SpecStrength, ref specR, ref specG, ref specB);
            SpecAccum(u.I2 * sh2, u.C2r, u.C2g, u.C2b, u.L2x, u.L2y, u.L2z, nx, ny, nz, vx, vy, vz, NdotV, a2, kg, F0r, F0g, F0b, u.SpecStrength, ref specR, ref specG, ref specB);
            diffSuppress = 1.0 - u.Metallic;
        }

        // 4c — DE-cone AO. Cone-march the height DE along the normal; each ring's
        // occlusion is max(0, d - de(P + N*d)) / d. Darkens the diffuse+ambient
        // term only (spec left alone), a line-for-line twin of ShadingPipeline's
        // AO. AoSamples == 0 → ao = 1 (byte-identical to the no-AO twin).
        double ao = 1.0;
        if (u.AoSamples > 0)
        {
            double eps = u.Cam.Eps0;
            double occl = 0.0, wsum = 0.0;
            for (int k = 1; k <= u.AoSamples; k++)
            {
                double d = eps * (double)(1L << k);
                double sampleD = de.Evaluate(px + nx * d, py + ny * d, pz + nz * d);
                occl += Math.Max(0.0, d - sampleD) / d;
                wsum += 1.0;
            }
            ao = Math.Clamp(1.0 - u.AoStrength * (occl / Math.Max(wsum, 1.0)), 0, 1);
        }

        // 4d — IBL-modulated ambient. IblStrength>0 blends the env colour sampled
        // at the surface normal into the scalar ambient per channel; twin of
        // ShadingPipeline.SampleEnvAmbient (non-HDRI: Solid → BgTop, else
        // BgBottom→BgTop gradient by ny). IblStrength==0 → flat ambient (no-op).
        double ambR = u.Ambient, ambG = u.Ambient, ambB = u.Ambient;
        if (u.IblStrength > 0)
        {
            double eR, eG, eB;
            if (u.SkyMode == 1) // Solid
            {
                eR = ((u.BgTop >> 16) & 0xFF) / 255.0;
                eG = ((u.BgTop >> 8) & 0xFF) / 255.0;
                eB = (u.BgTop & 0xFF) / 255.0;
            }
            else                // Gradient / Hdri-fallback
            {
                double t = Math.Clamp(0.5 * (ny + 1.0), 0, 1);
                eR = ((1 - t) * ((u.BgBottom >> 16) & 0xFF) + t * ((u.BgTop >> 16) & 0xFF)) / 255.0;
                eG = ((1 - t) * ((u.BgBottom >> 8) & 0xFF) + t * ((u.BgTop >> 8) & 0xFF)) / 255.0;
                eB = ((1 - t) * (u.BgBottom & 0xFF) + t * (u.BgTop & 0xFF)) / 255.0;
            }
            double wv = u.IblStrength;
            ambR = ambR * (1 - wv) + eR * wv;
            ambG = ambG * (1 - wv) + eG * wv;
            ambB = ambB * (1 - wv) + eB * wv;
        }
        sR = ambR + (sR / 255.0) * (1.0 - ambR) * diffSuppress;
        sG = ambG + (sG / 255.0) * (1.0 - ambG) * diffSuppress;
        sB = ambB + (sB / 255.0) * (1.0 - ambB) * diffSuppress;
        sR *= ao; sG *= ao; sB *= ao;
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

    /// <summary>4d — triplanar procedural texture. Twin of
    /// <see cref="ShadingPipeline.ApplyTriplanar"/>: project the hit point onto
    /// YZ/XZ/XY, sample the 2D fn per plane, blend by squared-normal weights,
    /// modulate albedo by grey × tint × strength. Preserves the albedo alpha (the
    /// relief cutout) rather than forcing 0xFF — ShadeFlat re-reads it.</summary>
    private static uint ApplyTriplanar(uint albedo, double px, double py, double pz,
                                       double nx, double ny, double nz, in ReliefUniforms u)
    {
        double wx = nx * nx, wy = ny * ny, wz = nz * nz;
        double sum = wx + wy + wz;
        if (sum < 1e-8) return albedo;
        double inv = 1.0 / sum; wx *= inv; wy *= inv; wz *= inv;
        double s = u.TriplanarScale;
        int kind = u.TriplanarKind;
        double txY = SampleProc2D(kind, py * s, pz * s);
        double txX = SampleProc2D(kind, px * s, pz * s);
        double txZ = SampleProc2D(kind, px * s, py * s);
        double v = Math.Clamp(wx * txY + wy * txX + wz * txZ, 0, 1);
        double Tr = ((u.TriplanarTint >> 16) & 0xFF) / 255.0;
        double Tg = ((u.TriplanarTint >> 8) & 0xFF) / 255.0;
        double Tb = (u.TriplanarTint & 0xFF) / 255.0;
        double Ar = (albedo >> 16) & 0xFF, Ag = (albedo >> 8) & 0xFF, Ab = albedo & 0xFF;
        double mix = u.TriplanarStrength;
        double R = Ar * (1 - mix) + Ar * Tr * v * mix;
        double G = Ag * (1 - mix) + Ag * Tg * v * mix;
        double B = Ab * (1 - mix) + Ab * Tb * v * mix;
        uint Rb = (uint)Math.Clamp(R, 0, 255);
        uint Gb = (uint)Math.Clamp(G, 0, 255);
        uint Bb = (uint)Math.Clamp(B, 0, 255);
        return (albedo & 0xFF000000u) | (Rb << 16) | (Gb << 8) | Bb;
    }

    /// <summary>4d — procedural 2D sampler (greyscale [0,1]). Line-for-line twin
    /// of ShadingPipeline.SampleProc2D. kind: 1 Wood, 2 Marble, 3 Rock, 4 Checker.</summary>
    private static double SampleProc2D(int kind, double u, double v)
    {
        switch (kind)
        {
            case 1: // Wood
            {
                double r = Math.Sqrt(u * u + v * v);
                double wobble = 0.1 * Math.Sin(u * 0.3) * Math.Cos(v * 0.3);
                return 0.5 + 0.5 * Math.Sin((r + wobble) * 6.0);
            }
            case 2: // Marble
            {
                double turb = Math.Sin(v * 2.0 + Math.Sin(u * 4.0) * 1.5);
                return 0.5 + 0.5 * Math.Sin(u * 3.0 + turb * 2.0);
            }
            case 3: // Rock
            {
                double a = Math.Sin(u * 12.9898 + v * 78.233) * 43758.5453;
                double n = a - Math.Floor(a);
                return Math.Clamp(0.3 + 0.7 * n, 0, 1);
            }
            case 4: // Checker
            {
                int cu = (int)Math.Floor(u) & 1;
                int cv = (int)Math.Floor(v) & 1;
                return (cu ^ cv) == 0 ? 0.2 : 1.0;
            }
            default:
                return 1.0;
        }
    }

    private static void Accum(double intensity, double cr, double cg, double cb,
        double lx, double ly, double lz, double nx, double ny, double nz,
        ref double sR, ref double sG, ref double sB)
    {
        if (intensity <= 0) return;
        double diffuse = Math.Max(0.0, nx * lx + ny * ly + nz * lz) * intensity;
        sR += cr * diffuse; sG += cg * diffuse; sB += cb * diffuse;
    }

    /// <summary>4e — Beer-Lambert fog + single-scatter volumetric in-scatter,
    /// applied to a shaded terrain/floor pixel. Twin of ShadingPipeline's fog
    /// block: VolumeSteps &gt; 0 (with FogDensity &gt; 0 and key light I0 &gt; 0)
    /// runs the in-scatter walk — per-step density (optionally ground-hugging
    /// via FogHeightFalloff) × key-light SoftShadow, Beer-Lambert transmittance
    /// via the Padé exp; else FogDensity &gt; 0 blends toward the gradient sky by
    /// 1 − exp(−tHit·FogDensity). FogDensity == 0 → no-op (byte-identical). FBM
    /// cloud-noise (VolumeNoiseAmount) + cloud self-shadow are deferred to 4e-ii,
    /// so the density multiplier is 1 here. <paramref name="ox"/> is the primary
    /// ray origin (== camera for perspective); the walk samples o + rd·t.</summary>
    private static uint ApplyFogVolume(uint shaded, double ox, double oy, double oz,
        double rdx, double rdy, double rdz, double tHit,
        in HeightfieldRaymarch2D.HeightDe de, in ReliefUniforms u)
    {
        if (u.FogDensity <= 0) return shaded;
        double br = (shaded >> 16) & 0xFF, bg = (shaded >> 8) & 0xFF, bb = shaded & 0xFF;

        if (u.VolumeSteps > 0 && u.I0 > 0)
        {
            int vs = u.VolumeSteps;
            // Adaptive volumetric LOD (VolumeStepsFalloff > 0). Off in the gate →
            // no float-vs-double step-count divergence.
            if (u.VolumeStepsFalloff > 0 && tHit > 4.0)
                vs = Math.Max(4, (int)(vs / (1.0 + (tHit - 4.0) * u.VolumeStepsFalloff)));
            double stepSize = tHit / vs;
            double Lr = u.C0r, Lg = u.C0g, Lb = u.C0b, Li = u.I0;
            bool shadowOn = u.ShadowSteps > 0 && (u.ShadowLightMask & 0x1) != 0;
            double T = 1.0, inR = 0, inG = 0, inB = 0;
            for (int s = 0; s < vs; s++)
            {
                double t = (s + 0.5) * stepSize;
                double sx = ox + rdx * t, sy = oy + rdy * t, sz = oz + rdz * t;
                double density = u.FogDensity;
                if (u.FogHeightFalloff > 0)
                    density *= Math.Exp(-u.FogHeightFalloff * sy);
                double sh = 1.0;
                if (shadowOn)
                    sh = ShadingPipeline.SoftShadow(in de, sx, sy, sz, u.L0x, u.L0y, u.L0z,
                                                    u.Cam.Eps0, 12.0, u.ShadowSoftK, u.ShadowSteps);
                double scatter = density * sh * Li * stepSize;
                inR += T * scatter * Lr; inG += T * scatter * Lg; inB += T * scatter * Lb;
                double aT = density * stepSize;
                T *= aT < 1.0 ? ExpNegSmall(aT) : Math.Exp(-aT);
            }
            br = br * T + inR; bg = bg * T + inG; bb = bb * T + inB;
        }
        else
        {
            double fogF = 1.0 - Math.Exp(-tHit * u.FogDensity);
            uint sky = GradientSky(rdy, in u);
            br = br * (1 - fogF) + ((sky >> 16) & 0xFF) * fogF;
            bg = bg * (1 - fogF) + ((sky >> 8) & 0xFF) * fogF;
            bb = bb * (1 - fogF) + (sky & 0xFF) * fogF;
        }

        uint A = (shaded >> 24) & 0xFFu;
        return (A << 24)
             | ((uint)Math.Clamp(br + 0.5, 0, 255) << 16)
             | ((uint)Math.Clamp(bg + 0.5, 0, 255) << 8)
             | (uint)Math.Clamp(bb + 0.5, 0, 255);
    }

    /// <summary>Padé(2,2) approximation of exp(−x) on [0,1] — twin of
    /// ShadingPipeline.ExpNegSmall (the volumetric transmittance step).</summary>
    private static double ExpNegSmall(double x)
    {
        double num = 12.0 - 6.0 * x + x * x;
        double den = 12.0 + 6.0 * x + x * x;
        return num / den;
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

    /// <summary>4f — conservative empty-space-skip distance. Looks up the coarse
    /// block max height at (px,pz); if the ray point is above it by more than the
    /// hit epsilon, returns the min of the distance to descend to the block-max
    /// plane and the distance to exit the block's XZ cell — no terrain can be hit
    /// within that span. Returns 0 (fall back to the point DE) otherwise. Twin of
    /// the HLSL <c>EmptySkipDist</c>.</summary>
    private static double EmptySkipDist(double px, double py, double pz,
        double rdx, double rdy, double rdz, double epsT, in ReliefUniforms u, float[] mip)
    {
        double uu = px / u.Aspect + 0.5, vv = pz + 0.5;
        int cx = (int)Math.Floor(uu * u.MipW);
        int cz = (int)Math.Floor(vv * u.MipH);
        if (cx < 0) cx = 0; else if (cx > u.MipW - 1) cx = u.MipW - 1;
        if (cz < 0) cz = 0; else if (cz > u.MipH - 1) cz = u.MipH - 1;
        double hmax = mip[cz * u.MipW + cx] * u.Sy;
        if (py <= hmax + epsT) return 0.0;

        // Descend to epsT ABOVE the block max (not the plane itself) so the normal
        // march resumes with a tight hit-refine bracket instead of one spanning the
        // whole leap. Still conservative (y stays ≥ hmax over the span).
        double tPlane = rdy < -1e-9 ? (py - (hmax + epsT)) / (-rdy) : double.MaxValue;

        // Lateral exit of this coarse cell's world XZ AABB.
        double xLo = (cx / (double)u.MipW - 0.5) * u.Aspect;
        double xHi = ((cx + 1) / (double)u.MipW - 0.5) * u.Aspect;
        double zLo = cz / (double)u.MipH - 0.5;
        double zHi = (cz + 1) / (double)u.MipH - 0.5;
        double tExit = double.MaxValue;
        if (rdx > 1e-12) tExit = Math.Min(tExit, (xHi - px) / rdx);
        else if (rdx < -1e-12) tExit = Math.Min(tExit, (xLo - px) / rdx);
        if (rdz > 1e-12) tExit = Math.Min(tExit, (zHi - pz) / rdz);
        else if (rdz < -1e-12) tExit = Math.Min(tExit, (zLo - pz) / rdz);

        double skip = Math.Min(tPlane, tExit);
        return skip > 0.0 ? skip : 0.0;
    }
}
