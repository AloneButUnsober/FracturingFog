// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ShadingPipeline.cs
//
// Shared shading kernel for 3D raymarchers. Bottom-up port of the
// UserBulbCalculator path so every raymarcher (Mandelbulb, Mandelbox, KIFS,
// QJulia, QMandel, Bicomplex, Kleinian, UserBulb) can plug in via a single
// DistanceEstimator delegate + a ShadingInputs hit record.
//
// Phase 1 scope:
//   - Static helpers: AccumulateLight, SkyColor, LightDir, Saturate,
//     PackBgra. Bit-identical to the originals in UserBulbCalculator so
//     a switch-over compiles and renders the same pixels.
//   - Shade(...) signature stub returning a packed BGRA pixel; first
//     implementation mirrors UserBulb's Lambert + 3-light + AO + exp-fog
//     path and is wired in Phase 1b once a pixel-diff harness exists.
//   - Phase 2+ effects (soft shadow, SSAO, volumetric, IBL, bloom,
//     tonemap, reflection, SSS, edge contour) extend the same struct;
//     the contract for calculators stays Shade(...).

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.Rendering.Lighting;

/// <summary>
/// Distance-estimator delegate. Returned scalar is a lower bound on the
/// distance from (x, y, z) to the fractal surface. Used during the primary
/// raymarch by the caller; also called during shadow / AO / reflection
/// walks inside the pipeline.
/// </summary>
public delegate double DistanceEstimator(double x, double y, double z);

/// <summary>P3 — sentinel DE for shade calls that have no real estimator.
/// <see cref="Evaluate"/> returns +∞ so AO occlusion sums to 0, SoftShadow
/// returns full visibility, and reflection marches early-exit. Bit-identical
/// to the pre-P3 path when callers gate with <c>hasDe = false</c>.</summary>
public readonly struct NullDe : IDistanceEstimator
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Evaluate(double x, double y, double z) => double.PositiveInfinity;
}

/// <summary>P3 — boxes a legacy <see cref="DistanceEstimator"/> delegate
/// into an <see cref="IDistanceEstimator"/> struct so callers that still hold
/// a delegate can route through the generic Shade&lt;TDe&gt; path. The
/// delegate dispatch overhead per <see cref="Evaluate"/> call survives (this
/// is just an adapter, not a true devirtualization). Calculators that want
/// the full P3 win must build their own concrete DE struct.</summary>
public readonly struct DelegateDeAdapter : IDistanceEstimator
{
    private readonly DistanceEstimator _de;
    public DelegateDeAdapter(DistanceEstimator de) { _de = de; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Evaluate(double x, double y, double z) => _de(x, y, z);
}

/// <summary>
/// Hit record produced by a calculator's primary raymarch loop and passed
/// to <see cref="ShadingPipeline.Shade"/>. Eight doubles for the point and
/// normal; integer step / hitDist diagnostics for color drivers.
/// </summary>
public readonly struct ShadingInputs
{
    public readonly double Px, Py, Pz;        // surface hit point (world)
    public readonly double Nx, Ny, Nz;        // unit surface normal
    public readonly double Rdx, Rdy, Rdz;     // ray direction (world)
    public readonly double TotalT;            // distance traveled along ray
    public readonly double HitDist;           // last DE value at hit
    public readonly int    HitStep;           // step index at hit
    public readonly double Epsilon;           // DE epsilon used by caller

    public ShadingInputs(
        double px, double py, double pz,
        double nx, double ny, double nz,
        double rdx, double rdy, double rdz,
        double totalT, double hitDist, int hitStep, double epsilon)
    {
        Px = px; Py = py; Pz = pz;
        Nx = nx; Ny = ny; Nz = nz;
        Rdx = rdx; Rdy = rdy; Rdz = rdz;
        TotalT = totalT; HitDist = hitDist; HitStep = hitStep; Epsilon = epsilon;
    }
}

/// <summary>
/// Surface material at the hit point. The calculator picks an albedo from
/// its color map + color driver; the pipeline composites lighting on top.
/// roughness / metallic / spec / sss are sampled from <see cref="LightingFxData"/>
/// for now; Phase 14 (triplanar) feeds them per-pixel via texture lookup.
/// </summary>
public readonly struct ShadingMaterial
{
    public readonly double AlbedoR, AlbedoG, AlbedoB; // [0, 1] linear
    public readonly double Roughness;                  // [0, 1]
    public readonly double Metallic;                   // [0, 1]
    public readonly double SpecularStrength;
    public readonly double SubSurfaceStrength;

    public ShadingMaterial(
        double r, double g, double b,
        double roughness, double metallic,
        double specularStrength, double subSurfaceStrength)
    {
        AlbedoR = r; AlbedoG = g; AlbedoB = b;
        Roughness = roughness; Metallic = metallic;
        SpecularStrength = specularStrength;
        SubSurfaceStrength = subSurfaceStrength;
    }

    /// <summary>Build a material from a packed BGRA color (calculator's
    /// post-color-map output) + the active LightingFxData PBR knobs. Inverse-
    /// sRGB simplification: bytes are read as linear; matches legacy behaviour.</summary>
    public static ShadingMaterial FromPackedBgra(uint bgra, in LightingFxData fx)
    {
        double r = ((bgra >> 16) & 0xFF) / 255.0;
        double g = ((bgra >> 8) & 0xFF) / 255.0;
        double b = (bgra & 0xFF) / 255.0;
        return new ShadingMaterial(r, g, b,
            fx.Roughness, fx.Metallic, fx.SpecularStrength, fx.SubSurfaceStrength);
    }
}

/// <summary>
/// Stateless shading kernel. All effects flow through <see cref="Shade"/>.
/// Helpers are public so 2D fractals (Phase 8) and Phase 6 IBL convolution
/// can reuse the same primitives.
/// </summary>
public static class ShadingPipeline
{
    /// <summary>
    /// Composite the surface hit into a final BGRA pixel. Phase 1 scaffold:
    /// delegates to the legacy three-light + cone-AO + exp-fog path so a
    /// caller can switch over without behaviour change. Phase 2+ extends
    /// this body — calculators that already call Shade() inherit the new
    /// effects automatically.
    /// </summary>
    /// <param name="i">Primary-ray hit record.</param>
    /// <param name="m">Surface material derived from the calculator's
    /// color map and the active LightingFxData PBR knobs.</param>
    /// <param name="fx">Shared lighting + post parameters.</param>
    /// <param name="de">DE delegate. Required when fx requests AO &gt; 0,
    /// ShadowSteps &gt; 0, VolumeSteps &gt; 0, or ReflectionStrength &gt; 0.
    /// May be null when only flat lighting + fog are active.</param>
    public static uint Shade(
        in ShadingInputs i,
        in ShadingMaterial m,
        in LightingFxData fx,
        DistanceEstimator? de)
    {
        // Lights. Phase 18 — orbit theta by SceneTime · LightOrbitSpeed.
        // Lights 2/3 take 0.7× / 1.3× so they desync. Speed==0 → bit-identical.
        double orbitT = fx.SceneTime * fx.LightOrbitSpeed;
        var l1 = LightDir(fx.Light1.Theta + orbitT,        fx.Light1.Phi);
        var l2 = LightDir(fx.Light2.Theta + orbitT * 0.7,  fx.Light2.Phi);
        var l3 = LightDir(fx.Light3.Theta + orbitT * 1.3,  fx.Light3.Phi);

        // Phase 3 — per-light soft shadow. IQ DE-march toward light; min
        // (k·h/t) over walk is visibility. Shadow gates direct lighting only;
        // ambient is left alone. Origin pushed eps·4 along the normal so the
        // very first DE sample doesn't trigger on the surface itself.
        // ShadowLightMask: bit n enables shadow tracing for Light n+1.
        // ShadowSteps = 0 disables entirely (legacy behaviour).
        double sh1 = 1.0, sh2 = 1.0, sh3 = 1.0;
        if (fx.ShadowSteps > 0 && de is not null)
        {
            double bias = i.Epsilon * 4.0;
            double ox = i.Px + i.Nx * bias;
            double oy = i.Py + i.Ny * bias;
            double oz = i.Pz + i.Nz * bias;
            double tMin = i.Epsilon;
            double tMax = 12.0;  // covers all default scene radii
            int steps = fx.ShadowSteps;
            double k = fx.ShadowSoftK;
            if ((fx.ShadowLightMask & 0x1) != 0 && fx.Light1.Intensity > 0)
                sh1 = SoftShadow(de, ox, oy, oz, l1.X, l1.Y, l1.Z, tMin, tMax, k, steps);
            if ((fx.ShadowLightMask & 0x2) != 0 && fx.Light2.Intensity > 0)
                sh2 = SoftShadow(de, ox, oy, oz, l2.X, l2.Y, l2.Z, tMin, tMax, k, steps);
            if ((fx.ShadowLightMask & 0x4) != 0 && fx.Light3.Intensity > 0)
                sh3 = SoftShadow(de, ox, oy, oz, l3.X, l3.Y, l3.Z, tMin, tMax, k, steps);
        }

        double sR = 0, sG = 0, sB = 0;
        AccumulateLight(fx.Light1.Intensity * sh1, fx.Light1.Color, l1.X, l1.Y, l1.Z, i.Nx, i.Ny, i.Nz, ref sR, ref sG, ref sB);
        AccumulateLight(fx.Light2.Intensity * sh2, fx.Light2.Color, l2.X, l2.Y, l2.Z, i.Nx, i.Ny, i.Nz, ref sR, ref sG, ref sB);
        AccumulateLight(fx.Light3.Intensity * sh3, fx.Light3.Color, l3.X, l3.Y, l3.Z, i.Nx, i.Ny, i.Nz, ref sR, ref sG, ref sB);

        // DE-cone AO. Bit-identical to UserBulb when AoSamples > 0.
        double ao = 1.0;
        if (fx.AoSamples > 0 && de is not null)
        {
            double occl = 0, w = 0;
            for (int k = 1; k <= fx.AoSamples; k++)
            {
                double d = i.Epsilon * (double)(1L << k);  // P1: was Math.Pow(2, k)
                double sampleD = de(i.Px + i.Nx * d, i.Py + i.Ny * d, i.Pz + i.Nz * d);
                occl += Math.Max(0, d - sampleD) / d;
                w += 1.0;
            }
            ao = Math.Clamp(1.0 - fx.AoStrength * (occl / Math.Max(w, 1)), 0, 1);
        }

        double amb = fx.AmbientStrength;
        sR = amb + (sR / 255.0) * (1.0 - amb);
        sG = amb + (sG / 255.0) * (1.0 - amb);
        sB = amb + (sB / 255.0) * (1.0 - amb);
        sR *= ao; sG *= ao; sB *= ao;

        double br = m.AlbedoR * 255.0 * sR;
        double bg = m.AlbedoG * 255.0 * sG;
        double bb = m.AlbedoB * 255.0 * sB;

        // Exponential fog. Volume in-scatter / soft shadow land in Phase 3/5.
        // Phase 5 — volumetric in-scatter (single-scattering Beer–Lambert).
        // Activated when VolumeSteps>0, FogDensity>0, DE provided AND key light
        // (Light1) emits. Per-step shadow-toward-light gates god-rays. Cost is
        // ~VolumeSteps × ShadowSteps DE evals per pixel — defaults keep it off.
        // FogHeightFalloff scales density by exp(-falloff·y) so fog can hug
        // the ground. Phase 22 — FBM cloud-noise modulation via
        // VolumetricDensityMul, gated by VolumeNoiseAmount.
        //
        // When VolumeSteps==0 and FogDensity>0, fall back to legacy exponential
        // fog (pre-Phase-5 behaviour). When neither, no fog math runs.
        if (fx.VolumeSteps > 0 && fx.FogDensity > 0 && de is not null
            && fx.Light1.Intensity > 0)
        {
            // Reconstruct camera origin from surface point + view ray.
            double camX = i.Px - i.Rdx * i.TotalT;
            double camY = i.Py - i.Rdy * i.TotalT;
            double camZ = i.Pz - i.Rdz * i.TotalT;
            int vs = fx.VolumeSteps;
            double stepSize = i.TotalT / vs;
            double Lr = (fx.Light1.Color >> 16) & 0xFF;
            double Lg = (fx.Light1.Color >> 8) & 0xFF;
            double Lb = fx.Light1.Color & 0xFF;
            double Li = fx.Light1.Intensity;
            bool shadowOn = fx.ShadowSteps > 0 && (fx.ShadowLightMask & 0x1) != 0;
            double T = 1.0, inR = 0, inG = 0, inB = 0;
            for (int s = 0; s < vs; s++)
            {
                double t = (s + 0.5) * stepSize;
                double sx = camX + i.Rdx * t;
                double sy = camY + i.Rdy * t;
                double sz = camZ + i.Rdz * t;
                double density = fx.FogDensity;
                if (fx.FogHeightFalloff > 0)
                    density *= Math.Exp(-fx.FogHeightFalloff * sy);
                // Phase 22 — fbm cloud-noise modulation. Mul=1 when off, so
                // pre-Phase-22 renders stay bit-identical.
                density *= VolumetricDensityMul(sx, sy, sz, fx);
                double sh = 1.0;
                if (shadowOn)
                    sh = SoftShadow(de, sx, sy, sz, l1.X, l1.Y, l1.Z,
                                    i.Epsilon, 12.0, fx.ShadowSoftK, fx.ShadowSteps);
                // Phase 22b — cloud self-shadow. Returns 1 when off so
                // pre-Phase-22b renders stay bit-identical.
                sh *= CloudSelfShadow(sx, sy, sz, l1.X, l1.Y, l1.Z, fx);
                double scatter = density * sh * Li * stepSize;
                inR += T * scatter * Lr;
                inG += T * scatter * Lg;
                inB += T * scatter * Lb;
                // P1: Padé(2,2) approx of exp(-x). ~1e-4 accuracy on [0, 1];
                // density*stepSize stays small in normal scenes. Math.Exp ~15 ns,
                // Padé ~3 ns. Fall back outside the trust band.
                double aT = density * stepSize;
                T *= aT < 1.0 ? ExpNegSmall(aT) : Math.Exp(-aT);
            }
            br = br * T + inR;
            bg = bg * T + inG;
            bb = bb * T + inB;
        }
        else if (fx.FogDensity > 0)
        {
            double fogF = 1.0 - Math.Exp(-i.TotalT * fx.FogDensity);
            uint sky = SkyColor(i.Rdy, fx.BgBottomColor, fx.BgTopColor);
            br = br * (1 - fogF) + ((sky >> 16) & 0xFF) * fogF;
            bg = bg * (1 - fogF) + ((sky >> 8) & 0xFF) * fogF;
            bb = bb * (1 - fogF) + (sky & 0xFF) * fogF;
        }

        byte R = (byte)Math.Clamp(br, 0, 255);
        byte G = (byte)Math.Clamp(bg, 0, 255);
        byte B = (byte)Math.Clamp(bb, 0, 255);
        return 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
    }

    /// <summary>
    /// Bit-identical port of the legacy UserBulb shade block. Operates on
    /// the packed BGRA albedo directly so byte→float→byte round-trip
    /// quantization does not introduce ±1 differences vs the original
    /// renderer. Used by every 3D raymarcher during Phase 1b/2 swap-overs
    /// so output is provably the same before any Phase 3+ effect lands.
    /// </summary>
    /// <param name="i">Primary-ray hit record.</param>
    /// <param name="albedoBgra">Packed BGRA color from the color map.</param>
    /// <param name="fx">Shared lighting + post parameters.</param>
    /// <param name="de">DE delegate (required when AoSamples > 0).</param>
    /// <param name="pixelIndex">Linear pixel index for the G-buffer write.
    /// −1 = skip G-buffer write.</param>
    /// <param name="depthBuf">Optional depth G-buffer (Phase 4 SSAO input).</param>
    /// <param name="normalBuf">Optional normal G-buffer, 3 floats / pixel.</param>
    /// <param name="hdrBuf">Optional HDR linear color buffer (3 floats / pixel,
    /// byte-scale 0..∞). Phase 7 tonemap + bloom input. Pre-clamp values
    /// preserved so highlights aren't lost before tonemap.</param>
    /// <summary>P3 — delegate-based Shade. Boxes the delegate into
    /// <see cref="DelegateDeAdapter"/> and routes through the generic
    /// <see cref="Shade{TDe}"/>. Slower than calling Shade&lt;TDe&gt;
    /// directly with a concrete struct DE (delegate indirection survives),
    /// but keeps existing calculators source-compatible.</summary>
    public static uint Shade(
        in ShadingInputs i,
        uint albedoBgra,
        in LightingFxData fx,
        DistanceEstimator? de,
        int pixelIndex = -1,
        float[]? depthBuf = null,
        float[]? normalBuf = null,
        float[]? hdrBuf = null)
    {
        if (de is null)
        {
            var nop = default(NullDe);
            return Shade<NullDe>(in i, albedoBgra, in fx, in nop, false,
                pixelIndex, depthBuf, normalBuf, hdrBuf);
        }
        var ada = new DelegateDeAdapter(de);
        return Shade<DelegateDeAdapter>(in i, albedoBgra, in fx, in ada, true,
            pixelIndex, depthBuf, normalBuf, hdrBuf);
    }

    /// <summary>P3 — struct-generic Shade. JIT specialises one body per
    /// concrete TDe so every <c>de.Evaluate(...)</c> becomes a direct,
    /// inlinable call. <paramref name="hasDe"/> gates the AO / shadow /
    /// reflection / volumetric blocks the way <c>de is not null</c> did in
    /// the delegate path — pass <c>false</c> with a default(NullDe) when no
    /// real estimator is available.</summary>
    public static uint Shade<TDe>(
        in ShadingInputs i,
        uint albedoBgra,
        in LightingFxData fx,
        in TDe de,
        bool hasDe,
        int pixelIndex = -1,
        float[]? depthBuf = null,
        float[]? normalBuf = null,
        float[]? hdrBuf = null)
        where TDe : struct, IDistanceEstimator
    {
        // Lights. Phase 18 — orbit theta by SceneTime · LightOrbitSpeed.
        // Lights 2/3 take 0.7× / 1.3× so they desync. Speed==0 → bit-identical.
        double orbitT = fx.SceneTime * fx.LightOrbitSpeed;
        var l1 = LightDir(fx.Light1.Theta + orbitT,        fx.Light1.Phi);
        var l2 = LightDir(fx.Light2.Theta + orbitT * 0.7,  fx.Light2.Phi);
        var l3 = LightDir(fx.Light3.Theta + orbitT * 1.3,  fx.Light3.Phi);

        // Phase 3 — per-light soft shadow. IQ DE-march toward light; min
        // (k·h/t) over walk is visibility. Shadow gates direct lighting only;
        // ambient is left alone. Origin pushed eps·4 along the normal so the
        // very first DE sample doesn't trigger on the surface itself.
        // ShadowLightMask: bit n enables shadow tracing for Light n+1.
        // ShadowSteps = 0 disables entirely (legacy behaviour).
        double sh1 = 1.0, sh2 = 1.0, sh3 = 1.0;
        if (fx.ShadowSteps > 0 && hasDe)
        {
            double bias = i.Epsilon * 4.0;
            double ox = i.Px + i.Nx * bias;
            double oy = i.Py + i.Ny * bias;
            double oz = i.Pz + i.Nz * bias;
            double tMin = i.Epsilon;
            double tMax = 12.0;  // covers all default scene radii
            int steps = fx.ShadowSteps;
            double k = fx.ShadowSoftK;
            if ((fx.ShadowLightMask & 0x1) != 0 && fx.Light1.Intensity > 0)
                sh1 = SoftShadow<TDe>(in de, ox, oy, oz, l1.X, l1.Y, l1.Z, tMin, tMax, k, steps);
            if ((fx.ShadowLightMask & 0x2) != 0 && fx.Light2.Intensity > 0)
                sh2 = SoftShadow<TDe>(in de, ox, oy, oz, l2.X, l2.Y, l2.Z, tMin, tMax, k, steps);
            if ((fx.ShadowLightMask & 0x4) != 0 && fx.Light3.Intensity > 0)
                sh3 = SoftShadow<TDe>(in de, ox, oy, oz, l3.X, l3.Y, l3.Z, tMin, tMax, k, steps);
        }

        double sR = 0, sG = 0, sB = 0;
        AccumulateLight(fx.Light1.Intensity * sh1, fx.Light1.Color, l1.X, l1.Y, l1.Z, i.Nx, i.Ny, i.Nz, ref sR, ref sG, ref sB);
        AccumulateLight(fx.Light2.Intensity * sh2, fx.Light2.Color, l2.X, l2.Y, l2.Z, i.Nx, i.Ny, i.Nz, ref sR, ref sG, ref sB);
        AccumulateLight(fx.Light3.Intensity * sh3, fx.Light3.Color, l3.X, l3.Y, l3.Z, i.Nx, i.Ny, i.Nz, ref sR, ref sG, ref sB);

        // Phase 14 — Triplanar procedural texture. Modulates the albedo before
        // any lighting math runs so PBR + SSS + spec all see the textured
        // surface. None / Strength==0 → bit-identical legacy.
        uint texAlbedo = albedoBgra;
        if (fx.TriplanarKind != TriplanarTextureKind.None && fx.TriplanarStrength > 0)
        {
            texAlbedo = ApplyTriplanar(albedoBgra, fx, i.Px, i.Py, i.Pz, i.Nx, i.Ny, i.Nz);
        }

        // Phase 6 — PBR-lite specular (Cook-Torrance GGX + Schlick F + Smith G).
        // Gated by SpecularStrength > 0; bit-identical legacy when off.
        // Metallic = 1 → spec tinted by albedo (F0 = albedo), diffuse zeroed.
        // Metallic = 0 → spec uses dielectric F0 = 0.04 (white-ish), diffuse full.
        double specR = 0, specG = 0, specB = 0;
        if (fx.SpecularStrength > 0)
        {
            double vx = -i.Rdx, vy = -i.Rdy, vz = -i.Rdz;
            double NdotV = Math.Max(0.0, i.Nx * vx + i.Ny * vy + i.Nz * vz);
            double rough = Math.Max(0.05, fx.Roughness);
            double a = rough * rough;
            double a2 = a * a;
            double kg = (rough + 1.0) * (rough + 1.0) / 8.0;
            double Ar = ((texAlbedo >> 16) & 0xFF) / 255.0;
            double Ag = ((texAlbedo >>  8) & 0xFF) / 255.0;
            double Ab = ( texAlbedo        & 0xFF) / 255.0;
            double F0r = 0.04 + (Ar - 0.04) * fx.Metallic;
            double F0g = 0.04 + (Ag - 0.04) * fx.Metallic;
            double F0b = 0.04 + (Ab - 0.04) * fx.Metallic;
            AccumulateSpec(fx.Light1.Intensity * sh1, fx.Light1.Color, l1.X, l1.Y, l1.Z,
                i.Nx, i.Ny, i.Nz, vx, vy, vz, NdotV, a2, kg, F0r, F0g, F0b,
                fx.SpecularStrength, ref specR, ref specG, ref specB);
            AccumulateSpec(fx.Light2.Intensity * sh2, fx.Light2.Color, l2.X, l2.Y, l2.Z,
                i.Nx, i.Ny, i.Nz, vx, vy, vz, NdotV, a2, kg, F0r, F0g, F0b,
                fx.SpecularStrength, ref specR, ref specG, ref specB);
            AccumulateSpec(fx.Light3.Intensity * sh3, fx.Light3.Color, l3.X, l3.Y, l3.Z,
                i.Nx, i.Ny, i.Nz, vx, vy, vz, NdotV, a2, kg, F0r, F0g, F0b,
                fx.SpecularStrength, ref specR, ref specG, ref specB);
        }

        // Phase 13 — Sub-Surface Scattering (cheap Burley approximation).
        // Back-lit lobe: pow(saturate(-V · normalize(L + N·distortion)), p).
        // Translucent surfaces (wax, marble, organic) get a soft halo on the
        // side facing away from the light. Gated by SubSurfaceStrength > 0.
        // Bit-identical legacy when off.
        //
        // distortion = 0.3 (Frostbite recommendation), power = 4 — tight enough
        // that the lobe reads as backlight scatter rather than ambient wash.
        double sssR = 0, sssG = 0, sssB = 0;
        if (fx.SubSurfaceStrength > 0)
        {
            double vx = -i.Rdx, vy = -i.Rdy, vz = -i.Rdz;
            AccumulateSss(fx.Light1.Intensity * sh1, fx.Light1.Color, l1.X, l1.Y, l1.Z,
                i.Nx, i.Ny, i.Nz, vx, vy, vz, fx.SubSurfaceStrength,
                ref sssR, ref sssG, ref sssB);
            AccumulateSss(fx.Light2.Intensity * sh2, fx.Light2.Color, l2.X, l2.Y, l2.Z,
                i.Nx, i.Ny, i.Nz, vx, vy, vz, fx.SubSurfaceStrength,
                ref sssR, ref sssG, ref sssB);
            AccumulateSss(fx.Light3.Intensity * sh3, fx.Light3.Color, l3.X, l3.Y, l3.Z,
                i.Nx, i.Ny, i.Nz, vx, vy, vz, fx.SubSurfaceStrength,
                ref sssR, ref sssG, ref sssB);
        }

        double ao = 1.0;
        if (fx.AoSamples > 0 && hasDe)
        {
            double occl = 0, w = 0;
            for (int k = 1; k <= fx.AoSamples; k++)
            {
                double d = i.Epsilon * (double)(1L << k);  // P1: was Math.Pow(2, k)
                double sampleD = de.Evaluate(i.Px + i.Nx * d, i.Py + i.Ny * d, i.Pz + i.Nz * d);
                occl += Math.Max(0, d - sampleD) / d;
                w += 1.0;
            }
            ao = Math.Clamp(1.0 - fx.AoStrength * (occl / Math.Max(w, 1)), 0, 1);
        }

        // Phase 6 — IBL-modulated per-channel ambient. IblStrength==0 keeps the
        // legacy scalar AmbientStrength (bit-identical). When >0, blend env-map
        // sample at the surface normal into the ambient term per channel.
        double ambR = fx.AmbientStrength;
        double ambG = fx.AmbientStrength;
        double ambB = fx.AmbientStrength;
        if (fx.IblStrength > 0)
        {
            // Phase 6b — HDRI-aware ambient. Falls back to the gradient
            // sample when SkyMode != Hdri or the environment name doesn't
            // resolve, so legacy gradient scenes stay bit-identical.
            var env = SampleEnvAmbientHdri(i.Nx, i.Ny, i.Nz, in fx);
            double w = fx.IblStrength;
            ambR = ambR * (1.0 - w) + env.R * w;
            ambG = ambG * (1.0 - w) + env.G * w;
            ambB = ambB * (1.0 - w) + env.B * w;
        }
        // Phase 6 — metal suppresses diffuse on the spec-active path. With
        // SpecularStrength==0 we keep the legacy formula (suppress=1).
        double diffSuppress = fx.SpecularStrength > 0 ? (1.0 - fx.Metallic) : 1.0;
        sR = ambR + (sR / 255.0) * (1.0 - ambR) * diffSuppress;
        sG = ambG + (sG / 255.0) * (1.0 - ambG) * diffSuppress;
        sB = ambB + (sB / 255.0) * (1.0 - ambB) * diffSuppress;
        sR *= ao; sG *= ao; sB *= ao;

        double br = ((texAlbedo >> 16) & 0xFF) * sR + specR + sssR;
        double bg = ((texAlbedo >> 8) & 0xFF) * sG + specG + sssG;
        double bb = (texAlbedo & 0xFF) * sB + specB + sssB;

        // Phase 16 — reflection probe. March reflect(V, N) along DE;
        // on hit, cheap env-tinted ambient with distance falloff; on miss,
        // sample sky along the bounce direction. Mixed by Fresnel × strength
        // so dielectrics reflect only at grazing angles while metals reflect
        // broadly. ReflectionStrength==0 → bit-identical legacy.
        //
        // Phase 16b — N-bounce loop driven by MaxBounces (default 1 = legacy
        // single bounce). Each iteration: sphere-trace from current origin/dir
        // against DE, on hit recompute normal via central differences and
        // reflect again; on miss sample the IBL (roughness-convolved when an
        // HDRI is loaded) and stop. Per-bounce contribution scales by
        // (ReflectionStrength · F)^bounce so deeper bounces fade off.
        //
        // Cost: ~MaxBounces × ReflectionSteps DE evals per reflective pixel
        // (plus 6 extra DE evals per hit for the normal). MaxBounces &gt; 2 is
        // interactive-preview-only.
        if (fx.ReflectionStrength > 0 && hasDe)
        {
            int reflSteps = fx.ReflectionSteps > 0 ? fx.ReflectionSteps : 24;
            int maxBounces = fx.MaxBounces > 0 ? fx.MaxBounces : 1;
            if (maxBounces > 6) maxBounces = 6;
            double tMaxR = 12.0;
            double bias = i.Epsilon * 4.0;

            // Current bounce state: origin + direction + the surface normal at
            // the originating hit. Start the chain at the primary hit.
            double bOx = i.Px + i.Nx * bias;
            double bOy = i.Py + i.Ny * bias;
            double bOz = i.Pz + i.Nz * bias;
            // Initial bounce direction = mirror of the view ray about the
            // primary hit normal. Subsequent bounces re-reflect against the
            // newly-hit surface normal. Wave 4.2 — GGX VNDF sample when the
            // knob is on; falls back to mirror reflect at alpha → 0.
            double rdN0 = i.Rdx * i.Nx + i.Rdy * i.Ny + i.Rdz * i.Nz;
            double brx = i.Rdx - 2.0 * rdN0 * i.Nx;
            double bry = i.Rdy - 2.0 * rdN0 * i.Ny;
            double brz = i.Rdz - 2.0 * rdN0 * i.Nz;
            if (fx.UseGgxSampling)
            {
                var (u1, u2) = HashPair(i.Px, i.Py, i.Pz, 0);
                var g = SampleGgxReflect(-i.Rdx, -i.Rdy, -i.Rdz, i.Nx, i.Ny, i.Nz, fx.Roughness, u1, u2);
                // Reject below-horizon samples — keep mirror reflect.
                if (g.X * i.Nx + g.Y * i.Ny + g.Z * i.Nz > 0)
                {
                    brx = g.X; bry = g.Y; brz = g.Z;
                }
            }
            // NdotV at the originating surface — drives Fresnel for THIS bounce.
            double NdotV = Math.Max(0.0, i.Nx * -i.Rdx + i.Ny * -i.Rdy + i.Nz * -i.Rdz);

            // Accumulated reflection contribution (0..255 channel space).
            double accR = 0, accG = 0, accB = 0;
            // Running mix weight; multiplied by per-bounce Fresnel each step
            // so the chain fades geometrically.
            double chainW = fx.ReflectionStrength;

            for (int b = 0; b < maxBounces; b++)
            {
                // Schlick Fresnel at the originating surface. F0 ramps from
                // 0.04 (dielectric) toward 1.0 (metal). Higher metallic →
                // broader reflection across the whole hemisphere.
                double f0 = 0.04 + 0.96 * fx.Metallic;
                double omv = 1.0 - NdotV;
                double Fc = omv * omv * omv * omv * omv;
                double F = f0 + (1.0 - f0) * Fc;
                double w = chainW * F;
                if (w < 1e-4) break; // chain faded — further bounces invisible.

                // Sphere-trace the current bounce ray.
                double tR = i.Epsilon;
                bool hitR = false;
                double hitTR = 0.0;
                double hpx = 0, hpy = 0, hpz = 0;
                for (int s = 0; s < reflSteps; s++)
                {
                    hpx = bOx + brx * tR;
                    hpy = bOy + bry * tR;
                    hpz = bOz + brz * tR;
                    double hR = de.Evaluate(hpx, hpy, hpz);
                    if (hR < i.Epsilon * 2.0) { hitR = true; hitTR = tR; break; }
                    tR += hR;
                    if (tR > tMaxR) break;
                }

                if (!hitR)
                {
                    // Miss → sky-tint along the bounce dir. Mirrors the
                    // legacy single-bounce SkyColorHdri path (clamped bytes).
                    // Roughness picks the IBL mip when an HDRI is loaded —
                    // gradient sky has no convolution but the overload is
                    // cheap. Bit-identical to Phase 16 at roughness == 1.0
                    // and MaxBounces == 1.
                    uint skyR = SkyColorHdri(brx, bry, brz, fx.Roughness, in fx);
                    double mR = (skyR >> 16) & 0xFF;
                    double mG = (skyR >>  8) & 0xFF;
                    double mB =  skyR        & 0xFF;
                    accR += mR * w;
                    accG += mG * w;
                    accB += mB * w;
                    break;
                }

                // Hit — accumulate env-tinted proxy color for this bounce.
                // Mirrors the legacy SampleEnvAmbientHdri path with the new
                // roughness-aware overload.
                var envH = SampleEnvAmbientHdri(brx, bry, brz, fx.Roughness, in fx);
                double atten = Math.Exp(-hitTR * 0.15);
                accR += envH.R * 255.0 * atten * w;
                accG += envH.G * 255.0 * atten * w;
                accB += envH.B * 255.0 * atten * w;

                // If this was the last allowed bounce, stop — no need to
                // recompute the next normal.
                if (b + 1 >= maxBounces) break;

                // Recompute surface normal at the bounce hit via central
                // differences; reflect the current bounce dir about it for the
                // next iteration. Six extra DE evals per hit — gated above.
                double h = i.Epsilon * 2.0;
                double nbx = de.Evaluate(hpx + h, hpy, hpz) - de.Evaluate(hpx - h, hpy, hpz);
                double nby = de.Evaluate(hpx, hpy + h, hpz) - de.Evaluate(hpx, hpy - h, hpz);
                double nbz = de.Evaluate(hpx, hpy, hpz + h) - de.Evaluate(hpx, hpy, hpz - h);
                var n2 = Normalize3(nbx, nby, nbz);
                // Re-reflect bounce dir about the new normal. NdotV for the
                // next bounce's Fresnel = max(0, n·-bounceDir). Wave 4.2 —
                // GGX VNDF sample replaces the mirror reflect when the knob
                // is on. V = -brx/-bry/-brz (toward incoming surface).
                double rdN = brx * n2.X + bry * n2.Y + brz * n2.Z;
                double bnx = brx - 2.0 * rdN * n2.X;
                double bny = bry - 2.0 * rdN * n2.Y;
                double bnz = brz - 2.0 * rdN * n2.Z;
                if (fx.UseGgxSampling)
                {
                    var (u1, u2) = HashPair(hpx, hpy, hpz, b + 1);
                    var g = SampleGgxReflect(-brx, -bry, -brz, n2.X, n2.Y, n2.Z, fx.Roughness, u1, u2);
                    if (g.X * n2.X + g.Y * n2.Y + g.Z * n2.Z > 0)
                    {
                        bnx = g.X; bny = g.Y; bnz = g.Z;
                    }
                }
                NdotV = Math.Max(0.0, n2.X * -brx + n2.Y * -bry + n2.Z * -brz);
                bOx = hpx + n2.X * bias;
                bOy = hpy + n2.Y * bias;
                bOz = hpz + n2.Z * bias;
                brx = bnx; bry = bny; brz = bnz;
                // Fade the chain by THIS bounce's mix; deeper bounces are
                // weighted by the running product (matches a physical mirror
                // chain — each reflection loses energy to the surface).
                chainW = w;
            }

            br += accR;
            bg += accG;
            bb += accB;
        }

        // Phase 17 — fake caustics. Sample procedural pattern in world (x, z)
        // at the surface point, weighted by upward-facing surface (NdotUp) and
        // distance from the focusing plane (exp falloff). Multiplied by the key
        // light's color × intensity so caustics inherit scene tint and shadow.
        // CausticsStrength==0 → bit-identical legacy.
        if (fx.CausticsStrength > 0 && fx.Light1.Intensity > 0)
        {
            double NdotUp = i.Ny;
            if (NdotUp > 0)
            {
                double dy = i.Py - fx.CausticsFloorY;
                double heightFall = Math.Exp(-Math.Abs(dy) * 2.0);
                double causticPhase = fx.SceneTime * fx.CausticsAnimSpeed;
                double caustic = EvaluateCaustics(i.Px, i.Pz, fx.CausticsScale, causticPhase);
                double w = fx.CausticsStrength * caustic * heightFall * NdotUp * fx.Light1.Intensity * sh1;
                if (w > 0)
                {
                    double Lr = (fx.CausticsColor >> 16) & 0xFF;
                    double Lg = (fx.CausticsColor >>  8) & 0xFF;
                    double Lb =  fx.CausticsColor        & 0xFF;
                    br += Lr * w;
                    bg += Lg * w;
                    bb += Lb * w;
                }
            }
        }

        // Phase 5 — volumetric in-scatter (single-scattering Beer–Lambert).
        // Activated when VolumeSteps>0, FogDensity>0, DE provided AND key light
        // (Light1) emits. Per-step shadow-toward-light gates god-rays. Cost is
        // ~VolumeSteps × ShadowSteps DE evals per pixel — defaults keep it off.
        // FogHeightFalloff scales density by exp(-falloff·y) so fog can hug
        // the ground. Phase 22 — FBM cloud-noise modulation via
        // VolumetricDensityMul, gated by VolumeNoiseAmount.
        //
        // When VolumeSteps==0 and FogDensity>0, fall back to legacy exponential
        // fog (pre-Phase-5 behaviour). When neither, no fog math runs.
        if (fx.VolumeSteps > 0 && fx.FogDensity > 0 && hasDe
            && fx.Light1.Intensity > 0)
        {
            // Reconstruct camera origin from surface point + view ray.
            double camX = i.Px - i.Rdx * i.TotalT;
            double camY = i.Py - i.Rdy * i.TotalT;
            double camZ = i.Pz - i.Rdz * i.TotalT;
            int vs = fx.VolumeSteps;
            // P4 — adaptive volumetric LOD. Past 4 world units of camera depth
            // the in-scatter contribution is already attenuated by T → 0, so
            // shrink the step count rather than paying full ShadowSteps DE
            // evals at every sample. Falloff=0 → legacy bit-identical.
            if (fx.VolumeStepsFalloff > 0 && i.TotalT > 4.0)
                vs = Math.Max(4, (int)(vs / (1.0 + (i.TotalT - 4.0) * fx.VolumeStepsFalloff)));
            double stepSize = i.TotalT / vs;
            double Lr = (fx.Light1.Color >> 16) & 0xFF;
            double Lg = (fx.Light1.Color >> 8) & 0xFF;
            double Lb = fx.Light1.Color & 0xFF;
            double Li = fx.Light1.Intensity;
            bool shadowOn = fx.ShadowSteps > 0 && (fx.ShadowLightMask & 0x1) != 0;
            double T = 1.0, inR = 0, inG = 0, inB = 0;
            for (int s = 0; s < vs; s++)
            {
                double t = (s + 0.5) * stepSize;
                double sx = camX + i.Rdx * t;
                double sy = camY + i.Rdy * t;
                double sz = camZ + i.Rdz * t;
                double density = fx.FogDensity;
                if (fx.FogHeightFalloff > 0)
                    density *= Math.Exp(-fx.FogHeightFalloff * sy);
                // Phase 22 — fbm cloud-noise modulation. Mul=1 when off, so
                // pre-Phase-22 renders stay bit-identical.
                density *= VolumetricDensityMul(sx, sy, sz, fx);
                double sh = 1.0;
                if (shadowOn)
                    sh = SoftShadow<TDe>(in de, sx, sy, sz, l1.X, l1.Y, l1.Z,
                                          i.Epsilon, 12.0, fx.ShadowSoftK, fx.ShadowSteps);
                // Phase 22b — cloud self-shadow. Returns 1 when off so
                // pre-Phase-22b renders stay bit-identical.
                sh *= CloudSelfShadow(sx, sy, sz, l1.X, l1.Y, l1.Z, fx);
                double scatter = density * sh * Li * stepSize;
                inR += T * scatter * Lr;
                inG += T * scatter * Lg;
                inB += T * scatter * Lb;
                // P1: Padé(2,2) approx of exp(-x). ~1e-4 accuracy on [0, 1];
                // density*stepSize stays small in normal scenes. Math.Exp ~15 ns,
                // Padé ~3 ns. Fall back outside the trust band.
                double aT = density * stepSize;
                T *= aT < 1.0 ? ExpNegSmall(aT) : Math.Exp(-aT);
            }
            br = br * T + inR;
            bg = bg * T + inG;
            bb = bb * T + inB;
        }
        else if (fx.FogDensity > 0)
        {
            double fogF = 1.0 - Math.Exp(-i.TotalT * fx.FogDensity);
            uint sky = SkyColor(i.Rdy, fx.BgBottomColor, fx.BgTopColor);
            br = br * (1 - fogF) + ((sky >> 16) & 0xFF) * fogF;
            bg = bg * (1 - fogF) + ((sky >> 8) & 0xFF) * fogF;
            bb = bb * (1 - fogF) + (sky & 0xFF) * fogF;
        }

        byte R = (byte)Math.Clamp(br, 0, 255);
        byte G = (byte)Math.Clamp(bg, 0, 255);
        byte B = (byte)Math.Clamp(bb, 0, 255);

        if (pixelIndex >= 0)
        {
            if (depthBuf is not null && pixelIndex < depthBuf.Length)
                depthBuf[pixelIndex] = (float)i.TotalT;
            if (normalBuf is not null && pixelIndex * 3 + 2 < normalBuf.Length)
            {
                int n3 = pixelIndex * 3;
                normalBuf[n3]     = (float)i.Nx;
                normalBuf[n3 + 1] = (float)i.Ny;
                normalBuf[n3 + 2] = (float)i.Nz;
            }
            // Phase 7 — HDR write preserves pre-clamp values so tonemap can
            // recover highlights that the byte-clamped path loses. Same
            // float-per-channel layout as the normal buffer (3 floats / pixel).
            if (hdrBuf is not null && pixelIndex * 3 + 2 < hdrBuf.Length)
            {
                int h3 = pixelIndex * 3;
                hdrBuf[h3]     = (float)br;
                hdrBuf[h3 + 1] = (float)bg;
                hdrBuf[h3 + 2] = (float)bb;
            }
        }

        return 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
    }

    /// <summary>Resolve a (theta, phi) spherical direction to a unit world-
    /// space vector. theta = azimuth around +Y; phi = elevation from +Y.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double X, double Y, double Z) LightDir(double theta, double phi)
    {
        double sinPhi = Math.Sin(phi);
        return Normalize3(sinPhi * Math.Cos(theta), Math.Cos(phi), sinPhi * Math.Sin(theta));
    }

    /// <summary>Accumulate one directional light's diffuse contribution into
    /// the (sR, sG, sB) accumulator in 0–255 space. Bit-identical to
    /// UserBulbCalculator.AccumulateLight; passed as ref-doubles instead of
    /// returning so the hot loop avoids struct copies.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AccumulateLight(
        double intensity, uint color,
        double lx, double ly, double lz,
        double nx, double ny, double nz,
        ref double sR, ref double sG, ref double sB)
    {
        if (intensity <= 0) return;
        double diffuse = Math.Max(0.0, nx * lx + ny * ly + nz * lz) * intensity;
        sR += ((color >> 16) & 0xFF) * diffuse;
        sG += ((color >> 8) & 0xFF) * diffuse;
        sB += (color & 0xFF) * diffuse;
    }

    /// <summary>
    /// Cook-Torrance GGX specular contribution for one directional light.
    /// Schlick F (per-channel via F0r/F0g/F0b) + Smith joint G (Schlick-GGX) +
    /// GGX D. Result accumulated in 0–255 byte space; bright highlights can
    /// exceed 255 and rely on the caller's Math.Clamp (Phase 7 tonemap will
    /// preserve HDR range properly). Intensity already includes shadow factor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AccumulateSpec(
        double intensity, uint color,
        double lx, double ly, double lz,
        double nx, double ny, double nz,
        double vx, double vy, double vz,
        double NdotV, double a2, double k,
        double F0r, double F0g, double F0b,
        double specStrength,
        ref double specR, ref double specG, ref double specB)
    {
        if (intensity <= 0) return;
        double NdotL = nx * lx + ny * ly + nz * lz;
        if (NdotL <= 0) return;

        // Half vector
        double hx = lx + vx, hy = ly + vy, hz = lz + vz;
        double hLen2 = hx * hx + hy * hy + hz * hz;
        if (hLen2 < 1e-12) return;
        double invH = 1.0 / Math.Sqrt(hLen2);
        hx *= invH; hy *= invH; hz *= invH;
        double NdotH = Math.Max(0.0, nx * hx + ny * hy + nz * hz);
        double VdotH = Math.Max(0.0, vx * hx + vy * hy + vz * hz);

        // GGX D
        double denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
        double D = a2 / (Math.PI * denom * denom);

        // Smith joint G (Schlick-GGX form)
        double G1V = NdotV / (NdotV * (1.0 - k) + k);
        double G1L = NdotL / (NdotL * (1.0 - k) + k);
        double G = G1V * G1L;

        // Schlick F per channel
        double omv = 1.0 - VdotH;
        double Fc = omv * omv * omv * omv * omv;
        double Fr = F0r + (1.0 - F0r) * Fc;
        double Fg = F0g + (1.0 - F0g) * Fc;
        double Fb = F0b + (1.0 - F0b) * Fc;

        // Spec base = D·G / (4·NdotV) — NdotL canceled by Smith G inclusion.
        double specBase = (D * G / Math.Max(4.0 * NdotV, 1e-4)) * specStrength * intensity;
        double Lr = (color >> 16) & 0xFF;
        double Lg = (color >>  8) & 0xFF;
        double Lb =  color        & 0xFF;
        specR += specBase * Fr * Lr;
        specG += specBase * Fg * Lg;
        specB += specBase * Fb * Lb;
    }

    /// <summary>
    /// Triplanar procedural texture sampler. Project surface position onto
    /// each major plane (YZ / XZ / XY), sample a 2D math fn per plane,
    /// blend by squared normal weights. Modulate the input albedo by the
    /// resulting greyscale × tint × strength. Phase 14.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ApplyTriplanar(
        uint albedoBgra, in LightingFxData fx,
        double px, double py, double pz,
        double nx, double ny, double nz)
    {
        double s = fx.TriplanarScale;
        // Squared normal weights for triplanar blend. abs(n)² emphasises the
        // axis facing most directly toward each plane projection so seams
        // between projections fade smoothly.
        double wx = nx * nx, wy = ny * ny, wz = nz * nz;
        double sum = wx + wy + wz;
        if (sum < 1e-8) return albedoBgra;
        double inv = 1.0 / sum;
        wx *= inv; wy *= inv; wz *= inv;

        var kind = fx.TriplanarKind;
        // Each plane projection samples a 2D version of the procedural fn.
        double txY = SampleProc2D(kind, py * s, pz * s);
        double txX = SampleProc2D(kind, px * s, pz * s);
        double txZ = SampleProc2D(kind, px * s, py * s);
        double v = wx * txY + wy * txX + wz * txZ;
        v = Math.Clamp(v, 0, 1);

        // Tint × strength blend with albedo.
        double Tr = ((fx.TriplanarTint >> 16) & 0xFF) / 255.0;
        double Tg = ((fx.TriplanarTint >>  8) & 0xFF) / 255.0;
        double Tb = ( fx.TriplanarTint        & 0xFF) / 255.0;
        double Ar = (albedoBgra >> 16) & 0xFF;
        double Ag = (albedoBgra >>  8) & 0xFF;
        double Ab =  albedoBgra        & 0xFF;
        double mix = fx.TriplanarStrength;
        // Texture-modulated colour = albedo × (tint × v). Blend back to plain
        // albedo by (1 − strength) so the user can dial mix to taste.
        double tr = Ar * Tr * v;
        double tg = Ag * Tg * v;
        double tb = Ab * Tb * v;
        double R = Ar * (1 - mix) + tr * mix;
        double G = Ag * (1 - mix) + tg * mix;
        double B = Ab * (1 - mix) + tb * mix;
        byte Rb = (byte)Math.Clamp(R, 0, 255);
        byte Gb = (byte)Math.Clamp(G, 0, 255);
        byte Bb = (byte)Math.Clamp(B, 0, 255);
        return 0xFF000000u | ((uint)Rb << 16) | ((uint)Gb << 8) | Bb;
    }

    /// <summary>
    /// Procedural caustics pattern in (x, z) world plane. Returns intensity
    /// multiplier in [0, ~4] — most pixels read near 0 with sparse bright
    /// focused spots, matching the underwater-caustic look. Phase 17.
    ///
    /// Two crossed sin-cascades produce moving wave-fronts; the product is
    /// raised to a high power so only the brightest crests survive. Cheap
    /// (~10 flops + 4 transcendentals); no noise table needed.
    ///
    /// Phase 18 — <paramref name="time"/> adds a phase offset to each sin
    /// argument (each on a different harmonic of the base time) so the bright
    /// crests drift like rippling water. time==0 is bit-identical legacy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double EvaluateCaustics(double x, double z, double scale, double time = 0.0)
    {
        double s = scale;
        double a = Math.Sin(x * s + time) * Math.Sin(z * s * 1.3 + Math.Sin(x * s * 0.7) + time * 1.1);
        double b = Math.Sin(x * s * 1.7 + z * s * 0.5 + time * 0.9) * Math.Sin(z * s + time);
        double v = (a + b) * 0.5;
        v = 0.5 + 0.5 * v;
        // Power 6 — sharp bright crests, dark everywhere else.
        double v2 = v * v;       // 2
        double v4 = v2 * v2;     // 4
        double v6 = v4 * v2;     // 6
        // Scale up: caustics in real life are several × ambient on the spot.
        return v6 * 4.0;
    }

    /// <summary>P1 — Padé(2,2) approximation of <c>exp(-x)</c>. Accurate to
    /// ~1e-4 on x ∈ [0, 1]; ~3 ns vs ~15 ns for Math.Exp. Caller falls back
    /// to Math.Exp outside the trust band.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ExpNegSmall(double x)
    {
        double num = 12.0 - 6.0 * x + x * x;
        double den = 12.0 + 6.0 * x + x * x;
        return num / den;
    }

    /// <summary>Procedural 2D sampler dispatch. Returns greyscale [0, 1].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SampleProc2D(TriplanarTextureKind kind, double u, double v)
    {
        switch (kind)
        {
            case TriplanarTextureKind.Wood:
            {
                // Concentric rings via radius from origin + slight angular
                // wobble so the rings don't read as perfect circles.
                double r = Math.Sqrt(u * u + v * v);
                double wobble = 0.1 * Math.Sin(u * 0.3) * Math.Cos(v * 0.3);
                return 0.5 + 0.5 * Math.Sin((r + wobble) * 6.0);
            }
            case TriplanarTextureKind.Marble:
            {
                // Turbulent veins: sinusoidal cascade. Two nested sines make
                // wandering vein-like contours without a full noise function.
                double turb = Math.Sin(v * 2.0 + Math.Sin(u * 4.0) * 1.5);
                return 0.5 + 0.5 * Math.Sin(u * 3.0 + turb * 2.0);
            }
            case TriplanarTextureKind.Rock:
            {
                // Cheap hash-based noise — integer multiply-XOR + sine spread.
                // No frequency cascade; enough surface variation for an
                // organic rocky appearance at typical scales.
                double a = Math.Sin(u * 12.9898 + v * 78.233) * 43758.5453;
                double n = a - Math.Floor(a);
                return Math.Clamp(0.3 + 0.7 * n, 0, 1);
            }
            case TriplanarTextureKind.Checker:
            {
                int cu = (int)Math.Floor(u) & 1;
                int cv = (int)Math.Floor(v) & 1;
                return (cu ^ cv) == 0 ? 0.2 : 1.0;
            }
            default:
                return 1.0;
        }
    }

    /// <summary>
    /// Cheap Burley-style SSS backlight lobe. Half-vector distortion biases L
    /// through the surface toward V; the resulting cosine power produces a
    /// soft halo on the back-lit side. No DE thickness probe — for our
    /// fractal scenes the visual difference vs a constant-thickness fake is
    /// negligible and the cost stays flat. Phase 13.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AccumulateSss(
        double intensity, uint color,
        double lx, double ly, double lz,
        double nx, double ny, double nz,
        double vx, double vy, double vz,
        double strength,
        ref double sssR, ref double sssG, ref double sssB)
    {
        if (intensity <= 0) return;
        // Distorted half-vector: bias the light through the surface normal.
        const double distortion = 0.3;
        double hx = lx + nx * distortion;
        double hy = ly + ny * distortion;
        double hz = lz + nz * distortion;
        double hLen2 = hx * hx + hy * hy + hz * hz;
        if (hLen2 < 1e-12) return;
        double invH = 1.0 / Math.Sqrt(hLen2);
        hx *= invH; hy *= invH; hz *= invH;
        // Back-lit lobe: -V · h. Saturate, power-curve, scale.
        double dot = -(vx * hx + vy * hy + vz * hz);
        if (dot <= 0) return;
        double lobe = dot * dot;  // p = 2 (soft); raise to p=4 with extra mul if a tighter halo is wanted
        lobe *= lobe;             // now p = 4 — tight backlight halo
        double s = lobe * strength * intensity;
        double Lr = (color >> 16) & 0xFF;
        double Lg = (color >>  8) & 0xFF;
        double Lb =  color        & 0xFF;
        sssR += s * Lr;
        sssG += s * Lg;
        sssB += s * Lb;
    }

    /// <summary>
    /// Environment-map ambient lookup, sampled at the surface normal. Phase 6
    /// MVP: re-uses the sky-gradient (BgBottomColor → BgTopColor) so existing
    /// scenes get IBL-flavoured ambient with no extra assets. Solid mode skips
    /// the gradient. Hdri mode falls through to gradient — the HDRI-aware path
    /// lives in <see cref="SampleEnvAmbientHdri"/>; callers that have access to
    /// the full <see cref="LightingFxData"/> use that overload instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double R, double G, double B) SampleEnvAmbient(
        SkyMode mode, double ny, uint bgBot, uint bgTop)
    {
        if (mode == SkyMode.Solid)
        {
            return (
                ((bgTop >> 16) & 0xFF) / 255.0,
                ((bgTop >>  8) & 0xFF) / 255.0,
                ( bgTop        & 0xFF) / 255.0);
        }
        double t = Math.Clamp(0.5 * (ny + 1.0), 0, 1);
        double R = ((1 - t) * ((bgBot >> 16) & 0xFF) + t * ((bgTop >> 16) & 0xFF)) / 255.0;
        double G = ((1 - t) * ((bgBot >>  8) & 0xFF) + t * ((bgTop >>  8) & 0xFF)) / 255.0;
        double B = ((1 - t) * ( bgBot        & 0xFF) + t * ( bgTop        & 0xFF)) / 255.0;
        return (R, G, B);
    }

    /// <summary>
    /// Phase 6b — HDRI-aware ambient lookup. Falls through to the gradient
    /// path when SkyMode != Hdri or no HDRI is registered under
    /// <see cref="LightingFxData.EnvironmentName"/>. When an HDRI is resolved,
    /// samples the equirectangular at the surface normal direction (treating
    /// the normal's (x, y, z) as the world direction the ambient hemisphere
    /// is centred on) and returns linear RGB in roughly [0, big-number]; the
    /// caller scales by IblStrength + clamps to display range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double R, double G, double B) SampleEnvAmbientHdri(
        double nx, double ny, double nz, in LightingFxData fx)
    {
        if (fx.SkyMode == SkyMode.Hdri && TryResolveHdri(fx.EnvironmentName, out var hdri))
        {
            return hdri!.Sample(nx, ny, nz);
        }
        return SampleEnvAmbient(fx.SkyMode, ny, fx.BgBottomColor, fx.BgTopColor);
    }

    /// <summary>Phase 16b — HDRI ambient lookup with roughness convolution.
    /// Selects an <see cref="HdriImage"/> mip level by
    /// <c>roughness² · (MipLevels − 1)</c> so smooth surfaces sample mip 0
    /// (sharp) and rough surfaces sample a heavily downsampled mip (soft).
    /// Falls back to the gradient path when no HDRI is resolved — gradient
    /// has no convolution, so roughness is ignored on that branch.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double R, double G, double B) SampleEnvAmbientHdri(
        double nx, double ny, double nz, double roughness, in LightingFxData fx)
    {
        if (fx.SkyMode == SkyMode.Hdri && TryResolveHdri(fx.EnvironmentName, out var hdri))
        {
            return hdri!.Sample(nx, ny, nz, roughness);
        }
        return SampleEnvAmbient(fx.SkyMode, ny, fx.BgBottomColor, fx.BgTopColor);
    }

    /// <summary>HDRI resolver. Looks up the registry by name first; if that
    /// misses and the name appears to be a filesystem path ending in .hdr,
    /// tries to load it from disk (and caches the result so future frames
    /// hit the in-memory copy). Phase 6b.</summary>
    internal static bool TryResolveHdri(string? name, out HdriImage? hdri)
    {
        if (HdriRegistry.TryGet(name, out hdri) && hdri is not null) return true;
        if (!string.IsNullOrWhiteSpace(name)
            && (name.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".pic", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".exr", StringComparison.OrdinalIgnoreCase)))
        {
            if (HdriRegistry.TryLoadFromFile(name, out hdri) && hdri is not null) return true;
        }
        return false;
    }

    /// <summary>Phase 6b — HDRI-aware sky lookup along a view ray. Mirrors
    /// <see cref="SampleEnvAmbientHdri"/> but returns a packed BGRA so
    /// existing sky-fill code paths can drop it in. Falls back to the
    /// gradient sky when no HDRI is registered.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SkyColorHdri(double rdx, double rdy, double rdz, in LightingFxData fx)
    {
        if (fx.SkyMode == SkyMode.Hdri && TryResolveHdri(fx.EnvironmentName, out var hdri))
        {
            var (r, g, b) = hdri!.Sample(rdx, rdy, rdz);
            byte R = (byte)Math.Clamp(r * 255.0, 0, 255);
            byte G = (byte)Math.Clamp(g * 255.0, 0, 255);
            byte B = (byte)Math.Clamp(b * 255.0, 0, 255);
            return 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
        }
        return SkyColor(rdy, fx.BgBottomColor, fx.BgTopColor);
    }

    /// <summary>Phase 16b — HDRI sky lookup with roughness convolution.
    /// Used by the reflection-miss path so rougher bounces see a softer
    /// environment. Falls back to gradient when no HDRI is loaded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SkyColorHdri(double rdx, double rdy, double rdz, double roughness, in LightingFxData fx)
    {
        if (fx.SkyMode == SkyMode.Hdri && TryResolveHdri(fx.EnvironmentName, out var hdri))
        {
            var (r, g, b) = hdri!.Sample(rdx, rdy, rdz, roughness);
            byte R = (byte)Math.Clamp(r * 255.0, 0, 255);
            byte G = (byte)Math.Clamp(g * 255.0, 0, 255);
            byte B = (byte)Math.Clamp(b * 255.0, 0, 255);
            return 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
        }
        return SkyColor(rdy, fx.BgBottomColor, fx.BgTopColor);
    }

    /// <summary>Vertical gradient sky lookup. rdy is the world-Y component of
    /// the view ray; t = 0 picks bottom, t = 1 picks top. Cheap; matches
    /// SkyMode.Gradient. HDRI lookup lands in Phase 6.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SkyColor(double rdy, uint bgBot, uint bgTop)
    {
        double t = Math.Clamp(0.5 * (rdy + 1.0), 0, 1);
        byte rb = (byte)((1 - t) * ((bgBot >> 16) & 0xFF) + t * ((bgTop >> 16) & 0xFF));
        byte gb = (byte)((1 - t) * ((bgBot >> 8) & 0xFF) + t * ((bgTop >> 8) & 0xFF));
        byte bb = (byte)((1 - t) * (bgBot & 0xFF) + t * (bgTop & 0xFF));
        return 0xFF000000u | ((uint)rb << 16) | ((uint)gb << 8) | bb;
    }

    /// <summary>
    /// Phase 22 — 3-octave value-noise fbm. Cheap procedural cloud density.
    /// Returns roughly [0, 0.875] (sum of 3 amplitudes: 0.5 + 0.25 + 0.125).
    /// Doubled at call site so the practical multiplier <see cref="VolumetricDensityMul"/>
    /// can swing between 0 and ~1.75 around the original density.
    ///
    /// Hash → smoothed trilinear → octave cascade. No noise tables; everything
    /// derives from integer hash so the function is referentially transparent.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FbmCloud3D(double x, double y, double z)
        => FbmCloud3D(x, y, z, 3);

    /// <summary>Phase 22b — octave-parameterised FBM. <paramref name="octaves"/>
    /// is clamped to [1, 6]. octaves=3 reproduces the original Phase 22
    /// result bit-for-bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FbmCloud3D(double x, double y, double z, int octaves)
    {
        if (octaves < 1) octaves = 1;
        else if (octaves > 6) octaves = 6;
        double v = 0.0;
        double amp = 0.5;
        double freq = 1.0;
        for (int i = 0; i < octaves; i++)
        {
            v += amp * ValueNoise3D(x * freq, y * freq, z * freq);
            freq *= 2.0;
            amp *= 0.5;
        }
        return v;
    }

    /// <summary>
    /// Density multiplier for the Phase 5 volumetric in-scatter loop. Returns
    /// 1.0 when noise is off (bit-identical pre-Phase-22). When on, samples
    /// FBM at (worldPos · scale + time · speed · drift-axis) and remaps to
    /// <c>lerp(1, 2·noise, amount)</c> so amount=1 swings density between
    /// ~empty and ~2× the unmodulated density.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double VolumetricDensityMul(
        double sx, double sy, double sz, in LightingFxData fx)
    {
        if (fx.VolumeNoiseAmount <= 0) return 1.0;
        double t = fx.SceneTime * fx.VolumeNoiseSpeed;
        double scale = fx.VolumeNoiseScale;
        // Drift on a fixed axis (1, 0.3, 0.7) so clouds slide diagonally
        // rather than along a perfect world-axis line — more natural look.
        int oct = fx.VolumeNoiseOctaves <= 0 ? 3 : fx.VolumeNoiseOctaves;
        double n = FbmCloud3D(
            sx * scale + t,
            sy * scale + t * 0.3,
            sz * scale + t * 0.7,
            oct);
        // amount=0 → 1.0; amount=1 → 2·n (range [0, ~1.75], mean ~0.875).
        double mul = 1.0 + fx.VolumeNoiseAmount * (2.0 * n - 1.0);
        return Math.Max(0.0, mul);
    }

    /// <summary>
    /// Phase 22b — cloud self-shadow transmittance toward a directional light.
    /// Marches a fixed number of FBM samples from (sx, sy, sz) along the light
    /// direction (lx, ly, lz), accumulates extinction, and returns
    /// exp(-strength · accum). Returns 1.0 (no attenuation) when self-shadow
    /// is off so pre-Phase-22b renders stay bit-identical.
    ///
    /// March length is fixed at 2.0 world units so deeper cloud bodies cast
    /// longer shadows. Cost: <c>VolumeSelfShadowSteps</c> extra
    /// <see cref="FbmCloud3D"/> evals per volume step.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double CloudSelfShadow(
        double sx, double sy, double sz,
        double lx, double ly, double lz,
        in LightingFxData fx)
    {
        if (fx.VolumeSelfShadow <= 0 || fx.VolumeSelfShadowSteps <= 0
            || fx.VolumeNoiseAmount <= 0) return 1.0;
        int steps = Math.Min(fx.VolumeSelfShadowSteps, 16);
        const double marchLen = 2.0;
        double stepSz = marchLen / steps;
        double t = fx.SceneTime * fx.VolumeNoiseSpeed;
        double scale = fx.VolumeNoiseScale;
        int oct = fx.VolumeNoiseOctaves <= 0 ? 3 : fx.VolumeNoiseOctaves;
        double accum = 0;
        for (int k = 1; k <= steps; k++)
        {
            double px = sx + lx * stepSz * k;
            double py = sy + ly * stepSz * k;
            double pz = sz + lz * stepSz * k;
            double n = FbmCloud3D(
                px * scale + t,
                py * scale + t * 0.3,
                pz * scale + t * 0.7,
                oct);
            // Same density remap as the in-scatter walk so attenuation matches
            // visible cloud density.
            double d = Math.Max(0.0, 1.0 + fx.VolumeNoiseAmount * (2.0 * n - 1.0));
            accum += d * stepSz;
        }
        return Math.Exp(-fx.VolumeSelfShadow * accum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ValueNoise3D(double x, double y, double z)
    {
        int ix = (int)Math.Floor(x);
        int iy = (int)Math.Floor(y);
        int iz = (int)Math.Floor(z);
        double fx = x - ix, fy = y - iy, fz = z - iz;
        // Smoothstep for C1-continuous interpolation.
        double ux = fx * fx * (3.0 - 2.0 * fx);
        double uy = fy * fy * (3.0 - 2.0 * fy);
        double uz = fz * fz * (3.0 - 2.0 * fz);
        double c000 = Hash3D(ix,     iy,     iz    );
        double c100 = Hash3D(ix + 1, iy,     iz    );
        double c010 = Hash3D(ix,     iy + 1, iz    );
        double c110 = Hash3D(ix + 1, iy + 1, iz    );
        double c001 = Hash3D(ix,     iy,     iz + 1);
        double c101 = Hash3D(ix + 1, iy,     iz + 1);
        double c011 = Hash3D(ix,     iy + 1, iz + 1);
        double c111 = Hash3D(ix + 1, iy + 1, iz + 1);
        double x00 = c000 + (c100 - c000) * ux;
        double x10 = c010 + (c110 - c010) * ux;
        double x01 = c001 + (c101 - c001) * ux;
        double x11 = c011 + (c111 - c011) * ux;
        double y0 = x00 + (x10 - x00) * uy;
        double y1 = x01 + (x11 - x01) * uy;
        return y0 + (y1 - y0) * uz;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Hash3D(int ix, int iy, int iz)
    {
        // Integer hash cascade — three large primes for axis spread + a
        // multiply-XOR scramble. Yields a well-decorrelated [0, 1] scalar
        // with no period at the scene scales fractals use.
        unchecked
        {
            uint h = (uint)(ix * 374761393 + iy * 668265263 + iz * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFFu) / 16777215.0;
        }
    }

    /// <summary>Inigo Quilez soft shadow. March from origin toward ld; min
    /// (k * h / t) over the walk is the visibility coefficient.
    /// Phase 3 enables this from Shade(); helper is exposed now so Phase 2
    /// raymarchers can call it directly during their lift.</summary>
    public static double SoftShadow(
        DistanceEstimator de,
        double ox, double oy, double oz,
        double ldx, double ldy, double ldz,
        double tMin, double tMax, double k, int maxSteps)
    {
        double res = 1.0, t = tMin;
        for (int s = 0; s < maxSteps; s++)
        {
            double px = ox + ldx * t;
            double py = oy + ldy * t;
            double pz = oz + ldz * t;
            double h = de(px, py, pz);
            if (h < 1e-4) return 0.0;
            if (k > 0) res = Math.Min(res, k * h / t);
            t += h;
            if (t >= tMax) break;
        }
        return Math.Clamp(res, 0, 1);
    }

    /// <summary>P3 — struct-generic soft shadow. JIT inlines de.Evaluate
    /// when TDe is a concrete struct. Same algorithm as the delegate
    /// overload; only the dispatch differs.</summary>
    public static double SoftShadow<TDe>(
        in TDe de,
        double ox, double oy, double oz,
        double ldx, double ldy, double ldz,
        double tMin, double tMax, double k, int maxSteps)
        where TDe : struct, IDistanceEstimator
    {
        double res = 1.0, t = tMin;
        for (int s = 0; s < maxSteps; s++)
        {
            double px = ox + ldx * t;
            double py = oy + ldy * t;
            double pz = oz + ldz * t;
            double h = de.Evaluate(px, py, pz);
            if (h < 1e-4) return 0.0;
            if (k > 0) res = Math.Min(res, k * h / t);
            t += h;
            if (t >= tMax) break;
        }
        return Math.Clamp(res, 0, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double X, double Y, double Z) Normalize3(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-10) return (0.0, 0.0, 0.0);
        double inv = 1.0 / len;
        return (x * inv, y * inv, z * inv);
    }

    /// <summary>Wave 4.2 — deterministic Wang-hash seeded by per-bounce world
    /// origin + bounce index. Returns two uniforms in [0, 1) for GGX VNDF
    /// sampling. Stable across frames at the same camera, but different
    /// neighbours hash to different uniforms so the per-bounce lobe spread is
    /// spatially decorrelated without averaging cost.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (double U1, double U2) HashPair(double x, double y, double z, int bounce)
    {
        // Scale up so sub-pixel positions decorrelate; truncate to int so we
        // get a stable seed across frames. 1024 ≈ pixel-scale at typical
        // world-units.
        uint a = (uint)(int)(x * 1024.0) ^ 0x9E3779B1u;
        uint b = (uint)(int)(y * 1024.0) ^ 0x85EBCA77u;
        uint c = (uint)(int)(z * 1024.0) ^ 0xC2B2AE3Du;
        uint d = (uint)bounce ^ 0x27D4EB2Fu;
        uint h = a;
        h = (h ^ b) * 0x85EBCA6Bu;
        h = (h ^ c) * 0xC2B2AE35u;
        h = (h ^ d) * 0x27D4EB2Du;
        h ^= h >> 16;
        uint h2 = h * 0x85EBCA6Bu; h2 ^= h2 >> 13;
        // Convert to [0, 1).
        double u1 = (h  & 0xFFFFFFu) / (double)0x1000000u;
        double u2 = (h2 & 0xFFFFFFu) / (double)0x1000000u;
        return (u1, u2);
    }

    /// <summary>Wave 4.2 — GGX VNDF importance-sampled reflection direction
    /// (Heitz 2018, "Sampling the GGX Distribution of Visible Normals").
    /// Returns a reflected direction L = reflect(-V, H) where H is sampled
    /// from the visible-normal distribution at view dir V (= unit toward
    /// camera) with isotropic roughness alpha = roughness². At
    /// <paramref name="alpha"/> ≈ 0 the result collapses to mirror reflect.
    /// One sample per call — temporal/spatial decorrelation from
    /// <see cref="HashPair"/> spreads the lobe across the screen.</summary>
    internal static (double X, double Y, double Z) SampleGgxReflect(
        double vx, double vy, double vz,
        double nx, double ny, double nz,
        double roughness,
        double u1, double u2)
    {
        // Build orthonormal TBN. Frisvad 2012 — branchless basis from normal.
        double sign = ny >= 0 ? 1.0 : -1.0;
        double a = -1.0 / (sign + ny);
        double bComp = nx * nz * a;
        double t1x = 1.0 + sign * nx * nx * a;
        double t1y = -sign * nx;
        double t1z = sign * bComp;
        double t2x = bComp;
        double t2y = -nz;
        double t2z = sign + nz * nz * a;

        // V in tangent space.
        double Vtx = vx * t1x + vy * t1y + vz * t1z;
        double Vty = vx * t2x + vy * t2y + vz * t2z;
        double Vtz = vx * nx  + vy * ny  + vz * nz;

        double alpha = roughness * roughness;
        if (alpha < 1e-4) alpha = 1e-4;

        // Stretch.
        double Vhx = alpha * Vtx;
        double Vhy = alpha * Vty;
        double Vhz = Vtz;
        double Vhlen = Math.Sqrt(Vhx * Vhx + Vhy * Vhy + Vhz * Vhz);
        if (Vhlen < 1e-10) Vhlen = 1e-10;
        Vhx /= Vhlen; Vhy /= Vhlen; Vhz /= Vhlen;

        // Orthonormal basis on (T1, T2, Vh).
        double lensq = Vhx * Vhx + Vhy * Vhy;
        double T1x, T1y, T1z;
        if (lensq > 0)
        {
            double inv = 1.0 / Math.Sqrt(lensq);
            T1x = -Vhy * inv; T1y = Vhx * inv; T1z = 0;
        }
        else
        {
            T1x = 1; T1y = 0; T1z = 0;
        }
        // T2 = cross(Vh, T1).
        double T2x = Vhy * T1z - Vhz * T1y;
        double T2y = Vhz * T1x - Vhx * T1z;
        double T2z = Vhx * T1y - Vhy * T1x;

        // Sample point on disk.
        double r = Math.Sqrt(u1);
        double phi = 2.0 * Math.PI * u2;
        double tA = r * Math.Cos(phi);
        double tB = r * Math.Sin(phi);
        double s = 0.5 * (1.0 + Vhz);
        tB = (1.0 - s) * Math.Sqrt(Math.Max(0, 1.0 - tA * tA)) + s * tB;

        // Hemisphere sample in stretched space.
        double Nhz = Math.Sqrt(Math.Max(0, 1.0 - tA * tA - tB * tB));
        double Nhx_s = tA * T1x + tB * T2x + Nhz * Vhx;
        double Nhy_s = tA * T1y + tB * T2y + Nhz * Vhy;
        double Nhz_s = tA * T1z + tB * T2z + Nhz * Vhz;

        // Unstretch.
        double Hx_t = alpha * Nhx_s;
        double Hy_t = alpha * Nhy_s;
        double Hz_t = Math.Max(0, Nhz_s);
        double Hlen = Math.Sqrt(Hx_t * Hx_t + Hy_t * Hy_t + Hz_t * Hz_t);
        if (Hlen < 1e-10) Hlen = 1e-10;
        Hx_t /= Hlen; Hy_t /= Hlen; Hz_t /= Hlen;

        // Transform H back to world.
        double Hx = Hx_t * t1x + Hy_t * t2x + Hz_t * nx;
        double Hy = Hx_t * t1y + Hy_t * t2y + Hz_t * ny;
        double Hz = Hx_t * t1z + Hy_t * t2z + Hz_t * nz;

        // L = reflect(-V, H) = 2·(V·H)·H − V. (We have V toward viewer; ray
        // direction = −V. Standard mirror-around-H gives reflected ray dir.)
        double VdotH = vx * Hx + vy * Hy + vz * Hz;
        double Lx = 2.0 * VdotH * Hx - vx;
        double Ly = 2.0 * VdotH * Hy - vy;
        double Lz = 2.0 * VdotH * Hz - vz;
        var nrm = Normalize3(Lx, Ly, Lz);
        return nrm;
    }
}
