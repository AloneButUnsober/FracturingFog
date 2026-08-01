// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// GpuShadingParams.cs
//
// P7c.1 — shared shading struct passed alongside GpuRaymarchParams + the
// per-fractal struct to every per-fractal ILGPU kernel. Lifts the subset of
// ShadingPipeline that doesn't require a per-pixel color-map lookup: three
// directional lights, soft shadow, DE-cone AO, scalar exponential fog with
// sky-gradient tint.
//
// P7c.2 — adds volumetric in-scatter fields: VolumeSteps + FogHeightFalloff +
// VolumeNoise* (Amount / Scale / Speed / Octaves) + VolumeSelfShadow* +
// VolumeStepsFalloff + SceneTime (for FBM drift). Each kernel runs the
// per-pixel volume march inline against its own DE because ILGPU can't take
// a struct-generic DE through LoadAutoGroupedStreamKernel.
//
// P7c.3 — adds one-bounce reflection: ReflectStrength (mix factor),
// ReflectSteps (march cap), ReflectMaxDist (early-bail t cap), Metallic
// (Schlick F0 ramp 0.04→1.0). Each kernel does its own reflect-march against
// its DE (same per-fractal inlining reason as P7c.2 SoftShadow).
//
// P7c.4 — adds full PBR/SSS/Triplanar/Caustics/IBL: Roughness +
// SpecularStrength (GGX D·G·F per-light), SubSurfaceStrength (Burley backlight
// lobe per-light), TriplanarKind+Scale+Strength+Tint (procedural texture
// modulating albedo pre-lighting), IblStrength (sky-gradient at the surface
// normal blended into ambient — HDRI env sampling remains GPU-blocked, the
// gradient is the same MVP fallback the CPU pipe uses), CausticsStrength +
// FloorY + Scale + Color + AnimSpeed (procedural pattern in world XZ with
// height + NdotUp gating). Cheap-palette albedo still feeds these — full
// color-map GPU port is its own future phase.
//
// All fields are pre-resolved CPU-side: light directions are normalized
// world-space unit vectors (theta/phi already evaluated through LightDir +
// orbit speed); light colors are bytes 0..255 widened to double so the
// kernel doesn't bit-unpack uint per pixel; sky colors split into top/bot
// RGB doubles for the same reason. Blittable, padding-safe, no managed refs.

using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Per-frame shading parameters shared by every GPU fractal kernel.
/// Mirrors the subset of <see cref="FracturingFog.Rendering.Lighting.LightingFxData"/>
/// that the P7c.1 GPU shade lifts (three directional lights with shadow + AO
/// + exp fog). PBR / volumetric / reflection / triplanar / IBL all sit out
/// pending later sub-phases — kernels that need them currently fall back to
/// CPU when those knobs are non-default.</summary>
public struct GpuShadingParams
{
    // ── Light 1 ────────────────────────────────────────────────────────────
    public double L1X, L1Y, L1Z;
    /// <summary>Light color as bytes-as-doubles in [0, 255]. Avoids bit-unpack
    /// in every kernel pixel.</summary>
    public double L1R, L1G, L1B;
    /// <summary>Scalar intensity. 0 = light off (kernel skips dot product).</summary>
    public double L1I;

    // ── Light 2 ────────────────────────────────────────────────────────────
    public double L2X, L2Y, L2Z;
    public double L2R, L2G, L2B;
    public double L2I;

    // ── Light 3 ────────────────────────────────────────────────────────────
    public double L3X, L3Y, L3Z;
    public double L3R, L3G, L3B;
    public double L3I;

    /// <summary>Scalar ambient floor. 1.0 = fully ambient (no diffuse).
    /// Matches LightingFxData.AmbientStrength.</summary>
    public double AmbientStrength;

    // ── Soft shadow ────────────────────────────────────────────────────────
    /// <summary>IQ soft-shadow march steps. 0 = shadows off (legacy).</summary>
    public int ShadowSteps;
    /// <summary>Soft-shadow penumbra constant — higher = sharper. Mirrors
    /// LightingFxData.ShadowSoftK.</summary>
    public double ShadowSoftK;
    /// <summary>Bit 0 = light 1, bit 1 = light 2, bit 2 = light 3. Mirrors
    /// LightingFxData.ShadowLightMask.</summary>
    public int ShadowLightMask;
    /// <summary>Maximum t-distance for the soft-shadow march. Caller passes
    /// the scene-scaled value (typically 12.0 — same constant the CPU pipe
    /// uses).</summary>
    public double ShadowTMax;

    // ── DE-cone AO ─────────────────────────────────────────────────────────
    /// <summary>AO cone-sample count. 0 = AO off (legacy).</summary>
    public int AoSamples;
    /// <summary>AO occlusion scale. Mirrors LightingFxData.AoStrength.</summary>
    public double AoStrength;

    // ── Scalar exponential fog + volumetric in-scatter ─────────────────────
    /// <summary>Per-unit-length extinction. 0 = no fog (legacy). When
    /// <see cref="VolumeSteps"/> &gt; 0 this is also the volumetric base density
    /// for the single-scattering Beer–Lambert in-scatter march; otherwise the
    /// cheap exp(-T·density) sky-tint path runs.</summary>
    public double FogDensity;
    /// <summary>Sky top color (RGB bytes-as-double). Used for fog tint when
    /// FogDensity > 0. Mirrors LightingFxData.BgTopColor channels.</summary>
    public double SkyTopR, SkyTopG, SkyTopB;
    /// <summary>Sky bottom color (RGB bytes-as-double). Used for fog tint
    /// when FogDensity > 0.</summary>
    public double SkyBotR, SkyBotG, SkyBotB;

    // ── P7c.2 volumetric ───────────────────────────────────────────────────
    /// <summary>Per-pixel volume-march sample count. 0 = scalar exp-fog path
    /// only (legacy). When &gt;0 each pixel walks <c>VolumeSteps</c> samples
    /// camera→surface, optionally shadow-marched toward Light1 and modulated
    /// by FBM cloud noise; cost is <c>VolumeSteps × ShadowSteps</c> DE evals
    /// per pixel plus self-shadow FBM evals. Mirrors LightingFxData.VolumeSteps.</summary>
    public int VolumeSteps;
    /// <summary>Adaptive volumetric LOD. Past 4 world units of camera depth
    /// shrink the per-pixel step count: <c>vs / (1 + (T − 4) × k)</c>. 0 = no
    /// LOD. Mirrors LightingFxData.VolumeStepsFalloff.</summary>
    public double VolumeStepsFalloff;
    /// <summary>Fog height falloff: density scales by exp(-falloff · y). 0 =
    /// uniform. Mirrors LightingFxData.FogHeightFalloff.</summary>
    public double FogHeightFalloff;
    /// <summary>FBM density modulation amplitude [0, 1]. 0 = uniform medium;
    /// 1 = full swing between empty and ~2× density. Mirrors
    /// LightingFxData.VolumeNoiseAmount.</summary>
    public double VolumeNoiseAmount;
    /// <summary>Spatial frequency of the FBM cloud sampler (world units⁻¹).
    /// Mirrors LightingFxData.VolumeNoiseScale.</summary>
    public double VolumeNoiseScale;
    /// <summary>Cloud drift speed in rad/s, multiplied by SceneTime to advect
    /// the FBM lookup. Mirrors LightingFxData.VolumeNoiseSpeed.</summary>
    public double VolumeNoiseSpeed;
    /// <summary>FBM octave count [1, 6]. Mirrors LightingFxData.VolumeNoiseOctaves.</summary>
    public int VolumeNoiseOctaves;
    /// <summary>Cloud self-shadow strength. 0 = off. Mirrors
    /// LightingFxData.VolumeSelfShadow.</summary>
    public double VolumeSelfShadow;
    /// <summary>FBM samples along the light direction for the per-volume-sample
    /// self-shadow march [0, 16]. 0 = skip. Mirrors LightingFxData.VolumeSelfShadowSteps.</summary>
    public int VolumeSelfShadowSteps;
    /// <summary>Global scene time in seconds, drives FBM cloud advection.
    /// Mirrors LightingFxData.SceneTime.</summary>
    public double SceneTime;

    // ── Vol-color slice B/C GPU parity (#181) ─────────────────────────────
    /// <summary>Henyey-Greenstein phase anisotropy [-1, 1]. 0 = isotropic
    /// (legacy, bit-identical). g &gt; 0 forward-scatters the in-scatter toward
    /// each light; g &lt; 0 back-scatters. Clamped to ±0.99 in the kernel.
    /// Mirrors LightingFxData.VolumeAnisotropy.</summary>
    public double VolumeAnisotropy;
    /// <summary>Medium scattering-albedo / fog color (RGB bytes-as-double in
    /// [0, 255]). Tints the accumulated volumetric in-scatter. White
    /// (255,255,255 — the default) is a ×1 no-op → bit-identical with the
    /// pre-parity path. Mirrors LightingFxData.FogColor channels.</summary>
    public double FogR, FogG, FogB;

    // ── Vol-color slice D GPU parity (#180) ───────────────────────────────
    /// <summary>Palette-mapped volumetric strength [0, 1]. 0 = off (legacy,
    /// bit-identical). When &gt;0 the kernel cross-fades the in-scatter toward
    /// the uploaded theme gradient LUT (passed as a separate ArrayView kernel
    /// arg — it can't live on this blittable struct), keyed by optical depth
    /// (1 − transmittance). Mirrors LightingFxData.VolumePaletteStrength.</summary>
    public double VolumePaletteStrength;

    // ── P7c.3 one-bounce reflection ────────────────────────────────────────
    /// <summary>Reflection mix factor [0, 1]. 0 = off (legacy path).
    /// Mirrors LightingFxData.ReflectionStrength.</summary>
    public double ReflectStrength;
    /// <summary>Reflection sphere-trace step cap. 0 = use default (24).
    /// Mirrors LightingFxData.ReflectionSteps.</summary>
    public int ReflectSteps;
    /// <summary>Max ray-t for the reflection march. 0 = use default (12.0).</summary>
    public double ReflectMaxDist;
    /// <summary>Schlick F0 metalness ramp. 0 = dielectric (F0=0.04), 1 = metal
    /// (F0=1.0). Mirrors LightingFxData.Metallic.</summary>
    public double Metallic;
    /// <summary>Phase 16b — max reflection bounces [1, 6]. 1 = legacy single
    /// bounce (bit-identical to P7c.3). Each extra bounce sphere-traces the
    /// reflected ray against the local fractal DE; contribution attenuates by
    /// (ReflectStrength · F) per bounce. Mirrors LightingFxData.MaxBounces.</summary>
    public int ReflectBounces;

    // ── P7c.4 PBR / SSS / Triplanar / Caustics / IBL ──────────────────────
    /// <summary>GGX roughness [0, 1]. 0 = mirror, 1 = lambertian. Clamped to
    /// ≥0.05 inside the spec accumulator to keep D finite. Mirrors
    /// LightingFxData.Roughness.</summary>
    public double Roughness;
    /// <summary>Cook-Torrance specular contribution scale. 0 = no spec
    /// (legacy). Mirrors LightingFxData.SpecularStrength.</summary>
    public double SpecularStrength;
    /// <summary>Burley backlight-lobe strength. 0 = no SSS (legacy). Mirrors
    /// LightingFxData.SubSurfaceStrength.</summary>
    public double SubSurfaceStrength;
    /// <summary>Procedural triplanar texture kind, cast from
    /// <see cref="FracturingFog.Rendering.Lighting.TriplanarTextureKind"/>.
    /// 0 = None (skip). 1 = Wood, 2 = Marble, 3 = Rock, 4 = Checker. Mirrors
    /// LightingFxData.TriplanarKind.</summary>
    public int TriplanarKind;
    /// <summary>Triplanar scale (world units⁻¹). Mirrors
    /// LightingFxData.TriplanarScale.</summary>
    public double TriplanarScale;
    /// <summary>Triplanar blend strength [0, 1]. 0 = pass-through (legacy).
    /// Mirrors LightingFxData.TriplanarStrength.</summary>
    public double TriplanarStrength;
    /// <summary>Triplanar tint RGB (bytes-as-double in [0, 255]). Mirrors
    /// LightingFxData.TriplanarTint channels.</summary>
    public double TriplanarTintR, TriplanarTintG, TriplanarTintB;
    /// <summary>IBL ambient strength [0, 1]. 0 = pure scalar ambient floor
    /// (legacy). When &gt;0, blend sky-gradient at the surface normal into the
    /// per-channel ambient. Mirrors LightingFxData.IblStrength.</summary>
    public double IblStrength;
    /// <summary>1 = ray-miss pixels render the sky gradient backdrop. 0 =
    /// fall back to <see cref="GpuRaymarchParams.InSetColor"/>. Mirrors
    /// LightingFxData.ShowSkyBackdrop (bool→int because ILGPU kernels
    /// can't take System.Boolean fields on every backend). 1 by default
    /// to match the post-Phase 16b CPU behaviour.</summary>
    public int ShowSkyBackdrop;
    /// <summary>Procedural caustics strength on upward-facing surfaces. 0 =
    /// off (legacy). Mirrors LightingFxData.CausticsStrength.</summary>
    public double CausticsStrength;
    /// <summary>Y of the virtual caustic focusing plane (world units). Mirrors
    /// LightingFxData.CausticsFloorY.</summary>
    public double CausticsFloorY;
    /// <summary>Caustic pattern frequency. Mirrors
    /// LightingFxData.CausticsScale.</summary>
    public double CausticsScale;
    /// <summary>Caustic tint RGB (bytes-as-double in [0, 255]). Mirrors
    /// LightingFxData.CausticsColor channels.</summary>
    public double CausticsR, CausticsG, CausticsB;
    /// <summary>Caustic phase animation speed (rad/s). Multiplied by
    /// SceneTime to drift the pattern. Mirrors LightingFxData.CausticsAnimSpeed.</summary>
    public double CausticsAnimSpeed;

    /// <summary>Build a kernel-ready shading struct from a CPU
    /// <see cref="LightingFxData"/>. Resolves light directions through the
    /// same <see cref="ShadingPipeline.LightDir"/> + scene-time orbit
    /// (lights 2/3 desync at 0.7× / 1.3×) the CPU pipe uses, so GPU and CPU
    /// paths see the same light vectors. <paramref name="shadowTMax"/> is the
    /// scene-scaled shadow-march cap — CPU pipe defaults to 12.0; callers
    /// with bigger fractal scenes pass a larger value.</summary>
    public static GpuShadingParams Build(in LightingFxData fx, double shadowTMax = 12.0)
    {
        double orbitT = fx.SceneTime * fx.LightOrbitSpeed;
        var l1 = ShadingPipeline.LightDir(fx.Light1.Theta + orbitT,        fx.Light1.Phi);
        var l2 = ShadingPipeline.LightDir(fx.Light2.Theta + orbitT * 0.7,  fx.Light2.Phi);
        var l3 = ShadingPipeline.LightDir(fx.Light3.Theta + orbitT * 1.3,  fx.Light3.Phi);

        return new GpuShadingParams
        {
            L1X = l1.X, L1Y = l1.Y, L1Z = l1.Z,
            L1R = (fx.Light1.Color >> 16) & 0xFF,
            L1G = (fx.Light1.Color >>  8) & 0xFF,
            L1B =  fx.Light1.Color        & 0xFF,
            L1I = fx.Light1.Intensity,

            L2X = l2.X, L2Y = l2.Y, L2Z = l2.Z,
            L2R = (fx.Light2.Color >> 16) & 0xFF,
            L2G = (fx.Light2.Color >>  8) & 0xFF,
            L2B =  fx.Light2.Color        & 0xFF,
            L2I = fx.Light2.Intensity,

            L3X = l3.X, L3Y = l3.Y, L3Z = l3.Z,
            L3R = (fx.Light3.Color >> 16) & 0xFF,
            L3G = (fx.Light3.Color >>  8) & 0xFF,
            L3B =  fx.Light3.Color        & 0xFF,
            L3I = fx.Light3.Intensity,

            AmbientStrength = fx.AmbientStrength,

            ShadowSteps     = fx.ShadowSteps,
            ShadowSoftK     = fx.ShadowSoftK,
            ShadowLightMask = fx.ShadowLightMask,
            ShadowTMax      = shadowTMax,

            AoSamples   = fx.AoSamples,
            AoStrength  = fx.AoStrength,

            // P7c.2 — FogDensity feeds both paths. Kernel picks scalar exp-fog
            // when VolumeSteps == 0, single-scattering volumetric otherwise.
            FogDensity = fx.FogDensity,
            SkyTopR = (fx.BgTopColor >> 16) & 0xFF,
            SkyTopG = (fx.BgTopColor >>  8) & 0xFF,
            SkyTopB =  fx.BgTopColor        & 0xFF,
            SkyBotR = (fx.BgBottomColor >> 16) & 0xFF,
            SkyBotG = (fx.BgBottomColor >>  8) & 0xFF,
            SkyBotB =  fx.BgBottomColor        & 0xFF,

            VolumeSteps           = fx.VolumeSteps,
            VolumeStepsFalloff    = fx.VolumeStepsFalloff,
            FogHeightFalloff      = fx.FogHeightFalloff,
            VolumeNoiseAmount     = fx.VolumeNoiseAmount,
            VolumeNoiseScale      = fx.VolumeNoiseScale,
            VolumeNoiseSpeed      = fx.VolumeNoiseSpeed,
            VolumeNoiseOctaves    = fx.VolumeNoiseOctaves,
            VolumeSelfShadow      = fx.VolumeSelfShadow,
            VolumeSelfShadowSteps = fx.VolumeSelfShadowSteps,
            SceneTime             = fx.SceneTime,

            // Slice B/C parity — anisotropy 0 + white fog default → kernel
            // in-scatter stays bit-identical with the single-light path.
            VolumeAnisotropy = fx.VolumeAnisotropy,
            FogR = (fx.FogColor >> 16) & 0xFF,
            FogG = (fx.FogColor >>  8) & 0xFF,
            FogB =  fx.FogColor        & 0xFF,

            // Slice D — strength gates the palette remap; the LUT itself is
            // uploaded separately (ArrayView), not carried on this struct.
            VolumePaletteStrength = fx.VolumePaletteStrength,

            ReflectStrength = fx.ReflectionStrength,
            ReflectSteps    = fx.ReflectionSteps,
            ReflectMaxDist  = 12.0,
            Metallic        = fx.Metallic,
            ReflectBounces  = fx.MaxBounces > 0 ? fx.MaxBounces : 1,

            // P7c.4 — PBR / SSS / Triplanar / Caustics / IBL. All default-zero
            // so a stock LightingFxData renders bit-identical to P7c.3.
            Roughness          = fx.Roughness,
            SpecularStrength   = fx.SpecularStrength,
            SubSurfaceStrength = fx.SubSurfaceStrength,

            TriplanarKind      = (int)fx.TriplanarKind,
            TriplanarScale     = fx.TriplanarScale,
            TriplanarStrength  = fx.TriplanarStrength,
            TriplanarTintR     = (fx.TriplanarTint >> 16) & 0xFF,
            TriplanarTintG     = (fx.TriplanarTint >>  8) & 0xFF,
            TriplanarTintB     =  fx.TriplanarTint        & 0xFF,

            IblStrength        = fx.IblStrength,
            ShowSkyBackdrop    = fx.ShowSkyBackdrop ? 1 : 0,

            CausticsStrength   = fx.CausticsStrength,
            CausticsFloorY     = fx.CausticsFloorY,
            CausticsScale      = fx.CausticsScale,
            CausticsR          = (fx.CausticsColor >> 16) & 0xFF,
            CausticsG          = (fx.CausticsColor >>  8) & 0xFF,
            CausticsB          =  fx.CausticsColor        & 0xFF,
            CausticsAnimSpeed  = fx.CausticsAnimSpeed,
        };
    }
}
