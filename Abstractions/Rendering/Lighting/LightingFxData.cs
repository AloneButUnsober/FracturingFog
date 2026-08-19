// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// LightingFxData.cs
//
// Shared lighting + post-effect parameters used by every 3D raymarcher
// (Mandelbulb, Mandelbox, KIFS, Quaternion Julia/Mandelbrot, Bicomplex,
// Kleinian, UserBulb). Replaces the per-fractal Light/AO/Fog/Bg field set
// (Bulb*, UserBulb*, Kleinian*, etc.) with one struct so future effects
// land in one place and every 3D raymarcher gets them simultaneously.
//
// Per-fractal Camera (pos/theta/phi/dist) stays where it is — scene scale
// differs per fractal and the camera is a scene concept, not a lighting
// concept. Lights ARE camera-independent; AO/fog/shadow tuning is shared.
//
// Phase 1 of the volumetric/lighting roadmap. Default values match the
// legacy single-light + 0.15 ambient look so existing renders are pixel-
// identical until a calculator explicitly opts into Phase 2+ effects.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>S8 (#389) — light kind. Directional is the legacy default (a light
/// at infinity, constant direction, no attenuation); Point / Spot add a world
/// position + inverse-square falloff, and Spot a cone.</summary>
public enum LightType
{
    /// <summary>Legacy: infinitely-far, constant direction, no attenuation.</summary>
    Directional = 0,
    /// <summary>Omni light at <see cref="DirectionalLight.PosX"/>… with
    /// inverse-square + range falloff.</summary>
    Point,
    /// <summary>Point light restricted to a cone around Theta/Phi's direction.</summary>
    Spot,
}

/// <summary>
/// One scene light. Legacy directional light (spherical angles theta = azimuth
/// around Y, phi = elevation from +Y, packed BGRA color, scalar intensity;
/// intensity 0 = off). S8 (#389) adds Point / Spot: a world position, an
/// inverse-square range falloff, and a spot cone. <see cref="Type"/> defaults to
/// <see cref="LightType.Directional"/> so an unchanged light behaves exactly as
/// before.
/// </summary>
public struct DirectionalLight
{
    /// <summary>Azimuth around the world +Y axis (radians). For Spot, this + Phi
    /// give the cone axis (the direction the light shines).</summary>
    public double Theta;
    /// <summary>Elevation from world +Y (radians). 0 = straight up,
    /// pi/2 = horizon, pi = straight down.</summary>
    public double Phi;
    /// <summary>Scalar multiplier on diffuse + specular. 0 = light off.</summary>
    public double Intensity;
    /// <summary>0xAARRGGBB packed color. Multiplies diffuse/spec.</summary>
    public uint Color;

    // ── S8 (#389) — point / spot ──────────────────────────────────────────
    /// <summary>Light kind. Default <see cref="LightType.Directional"/>.</summary>
    public LightType Type;
    /// <summary>World-space position (Point / Spot only).</summary>
    public double PosX, PosY, PosZ;
    /// <summary>Attenuation range in world units (Point / Spot). ≤ 0 = no range
    /// window (pure inverse-square). Beyond the range the contribution is 0.</summary>
    public double Range;
    /// <summary>Spot cone inner half-angle in degrees — full intensity inside.</summary>
    public double SpotInnerDeg;
    /// <summary>Spot cone outer half-angle in degrees — zero intensity beyond;
    /// the inner→outer band is the smooth penumbra.</summary>
    public double SpotOuterDeg;

    public DirectionalLight(double theta, double phi, double intensity, uint color)
    {
        Theta = theta; Phi = phi; Intensity = intensity; Color = color;
        Type = LightType.Directional;
        PosX = PosY = PosZ = 0.0;
        Range = 0.0;
        SpotInnerDeg = 15.0;
        SpotOuterDeg = 25.0;
    }
}

/// <summary>
/// Tone-map operator selector. Applied at the end of ShadingPipeline.Shade
/// once HDR linear color is composited.
/// </summary>
/// <summary>#317 — AOV / render-buffer "view mode". Beauty is the normal shaded
/// result (default, bit-identical). Any other value makes <see cref="ShadingPipeline
/// .Shade{TDe}"/> return the chosen diagnostic buffer for each surface hit
/// instead of the beauty pass — the standard lookdev isolates (Blender/Unreal
/// "view modes"): geometry (normals / depth / raymarch step-count heat) and
/// lighting components (AO / diffuse / specular / shadow). CPU raymarchers + the
/// CPU relief path only; GPU kernels stay beauty (relief forces its CPU path
/// while a view is active). Ray-miss (sky) pixels keep the background.</summary>
public enum AovView
{
    /// <summary>Normal shaded output (default).</summary>
    Beauty = 0,
    /// <summary>Surface normal as RGB (n·0.5+0.5).</summary>
    Normals,
    /// <summary>Ray distance to the hit, grayscale (near=dark, far=light).</summary>
    Depth,
    /// <summary>Raymarch step index at hit, blue→yellow heat (cost diagnostic).</summary>
    StepCount,
    /// <summary>Ambient-occlusion term, grayscale.</summary>
    AmbientOcclusion,
    /// <summary>Diffuse direct lighting only (shadowed), RGB.</summary>
    Diffuse,
    /// <summary>Specular highlight only, RGB.</summary>
    Specular,
    /// <summary>Key-light shadow visibility, grayscale (lit=white, shadow=black).</summary>
    Shadow,
}

public enum ToneMapOperator
{
    /// <summary>Legacy clamp [0, 255] (matches pre-Phase-7 behaviour). Default
    /// so existing renders stay pixel-identical until tone map slice lands.</summary>
    None,
    /// <summary>Reinhard: c / (1 + c). Soft rolloff, no clipping.</summary>
    Reinhard,
    /// <summary>Reinhard with adjustable white point.</summary>
    ReinhardExtended,
    /// <summary>ACES filmic (Hill 2017 fit). Cinematic, mild S-curve.</summary>
    Aces,
}

/// <summary>
/// Sky background mode. Used for ray-miss pixels and fog tint.
/// </summary>
/// <summary>
/// Procedural triplanar texture selector. Sample world-space surface position
/// projected onto each major plane (YZ / XZ / XY), blended by squared normal
/// weights. No external asset pipeline — every variant is a pure math fn of
/// position so saved scenes stay self-contained.
/// </summary>
public enum TriplanarTextureKind
{
    /// <summary>No texture. Albedo passes through unchanged.</summary>
    None,
    /// <summary>Concentric wood rings around the X axis. Soft sinusoidal grain.</summary>
    Wood,
    /// <summary>Turbulent marble veins via nested sine displacement.</summary>
    Marble,
    /// <summary>Pseudo-noise rock bump pattern via integer hash cascade.</summary>
    Rock,
    /// <summary>Checker. High-contrast diagnostic pattern; useful for
    /// verifying triplanar projection on a fresh scene.</summary>
    Checker,
}

/// <summary>Phase 20b — stereo render mode. <see cref="Off"/> = mono (legacy
/// default). <see cref="Fake"/> = single mono render + depth-parallax warp via
/// <see cref="ScreenSpacePost"/> / <see cref="StereoRender"/> (Phase 20 / 21c
/// behaviour — cheap but no parallax on close objects). <see cref="True"/> =
/// two real per-eye renders with the camera origin shifted by ±IPD/2 along the
/// right basis vector (Phase 20b — doubles render cost; eliminates the
/// close-object flatness the warp can't fix).</summary>
public enum StereoMode
{
    /// <summary>No stereo. Mono render straight to the framebuffer.</summary>
    Off,
    /// <summary>Phase 20 depth-parallax warp. Single render, cheap.</summary>
    Fake,
    /// <summary>Phase 20b two-eye render with camera offset. 2× cost.</summary>
    True,
}

/// <summary>Side-by-side packing for stereo output. <see cref="FullSbs"/> keeps
/// each eye at full width (output 2·W × H); <see cref="HalfSbs"/> squeezes each
/// eye horizontally to W/2 (output W × H, anamorphic). Half-SBS keeps the frame
/// at mono dimensions (smaller files, no encoder resize) and is the layout many
/// 3D TVs / VR players expect; the player un-squeezes it per eye.</summary>
public enum StereoLayout
{
    /// <summary>Each eye full width. Output 2·W × H.</summary>
    FullSbs,
    /// <summary>Each eye squeezed to W/2. Output W × H (anamorphic).</summary>
    HalfSbs,
}

/// <summary>Edge-ink kernel selector (Phase 23b).</summary>
public enum EdgeKernelMode
{
    /// <summary>3×3 Sobel — fast, isotropic-ish, slightly directional bias.
    /// Default — matches the original Phase 23 behaviour.</summary>
    Sobel,
    /// <summary>Frei-Chen 9-tap edge subspace — projects the 3×3 neighbourhood
    /// onto four orthonormal edge basis vectors (axial + diagonal). Captures
    /// diagonal edges Sobel under-weights, producing a more uniform stroke
    /// across all orientations.</summary>
    FreiChen,
}

public enum SkyMode
{
    /// <summary>Two-color vertical gradient between BgBottomColor and BgTopColor
    /// (lerp on ray-direction Y). Cheap; matches legacy UserBulb behaviour.</summary>
    Gradient,
    /// <summary>Solid color = BgTopColor.</summary>
    Solid,
    /// <summary>Image-based — sampled from an environment map identified by
    /// EnvironmentName. Falls back to Gradient if name unresolved.</summary>
    Hdri,
}

/// <summary>
/// Shared lighting + post-effect parameter block. Lives on
/// <see cref="FracturingFog.Models.FractalParameters"/> as <c>Lighting</c>.
/// Every 3D raymarcher reads from this struct so a single ParamsView section
/// drives every scene.
///
/// Field defaults preserve the legacy "single light + 0.15 ambient" look
/// (Light1 intensity = 1.0, Lights 2/3 intensity = 0.0, AO/Shadow/Fog/Bloom
/// all 0). Activating effects costs nothing until the user dials them up.
/// </summary>
public struct LightingFxData
{
    // ── Directional lights ─────────────────────────────────────────────
    // Light 1 is the key light. Light 2 / 3 default off so the all-zero
    // default = legacy single-light render. Theta/Phi match the existing
    // UserBulb*Light* convention so a Clone-on-init from the old per-bulb
    // fields can stamp into here without recomputation.

    public DirectionalLight Light1;
    public DirectionalLight Light2;
    public DirectionalLight Light3;

    // ── Ambient + AO ───────────────────────────────────────────────────

    /// <summary>Flat ambient floor in [0, 1]. Legacy 0.15. Higher = brighter
    /// shadows / less contrast; lower = deeper blacks.</summary>
    public double AmbientStrength;

    /// <summary>DE-cone AO sample count. 0 = AO off. 4–8 is the useful range;
    /// each sample is one DE evaluation along the surface normal.</summary>
    public int AoSamples;

    /// <summary>AO darkening factor in [0, 1]. 1 = full occlusion at crease;
    /// 0 = AO computed but not applied.</summary>
    public double AoStrength;

    /// <summary>Screen-space AO sample count per pixel. 0 = off. 8–16 typical.
    /// Captures macro-scale neighbor occlusion that the DE-cone AO misses
    /// (distant ambient blockers, not just immediate creases). Runs as a
    /// post-pass over the depth + normal G-buffer. Phase 4.</summary>
    public int SsaoSamples;

    /// <summary>SSAO sample hemisphere radius in world units. Smaller = local
    /// crevice detail; larger = soft global shadowing.</summary>
    public double SsaoRadius;

    /// <summary>SSAO darkening factor [0, 1]. Combines with DE-cone AO
    /// multiplicatively (both reduce the ambient contribution).</summary>
    public double SsaoStrength;

    // ── Shadows (Phase 3) ──────────────────────────────────────────────

    /// <summary>Max DE-march steps for soft-shadow ray per light. 0 = off.
    /// 24–32 is the IQ-soft-shadow sweet spot.</summary>
    public int ShadowSteps;

    /// <summary>Inigo Quilez soft-shadow sharpness coefficient. Higher =
    /// harder edge. 0 = hard shadow (binary occlusion).</summary>
    public double ShadowSoftK;

    /// <summary>Bitmask: bit n = enable shadow tracing for Light n+1.
    /// Default 0x1 = key light only (cheap). 0x7 = all three.</summary>
    public int ShadowLightMask;

    // ── Fog (Phase 5) ──────────────────────────────────────────────────

    /// <summary>Beer–Lambert exponential fog density. 0 = off. Multiplied by
    /// ray total-T; legacy UserBulb behaviour at default scene scale.</summary>
    public double FogDensity;

    /// <summary>Height-falloff coefficient. Fog density scales by
    /// exp(-heightFalloff * y) so fog hugs the ground. 0 = uniform fog.</summary>
    public double FogHeightFalloff;

    /// <summary>Raymarched in-scatter step count (volumetric light shafts).
    /// 0 = exp fog only. 16–48 typical; cost ~ steps × shadow-per-sample.</summary>
    public int VolumeSteps;

    /// <summary>P4 — adaptive volumetric LOD knob. Shrinks the per-pixel
    /// step count by ray total-T past 4 world units: <c>vs / (1 + (T − 4) × k)</c>.
    /// 0 = no LOD (legacy bit-identical). 0.5 = default, ~30–60 % faster on
    /// deep-depth volumetric scenes with no visible quality drop. Larger
    /// values trade quality for speed; ~1.0 starts to band on dense fog.</summary>
    public double VolumeStepsFalloff;

    /// <summary>FBM-noise modulation amplitude on fog density [0, 1].
    /// 0 = uniform medium (bit-identical pre-Phase-22); 1 = density swings
    /// fully between empty space and ~2× the unmodulated density, producing
    /// cloud-like volumetric structure inside the existing in-scatter walk.
    /// Phase 22.</summary>
    public double VolumeNoiseAmount;

    /// <summary>Spatial frequency of the FBM noise sampler (world units⁻¹).
    /// Smaller = larger, fluffier cloud cells; larger = denser turbulence.
    /// Default 0.3 — roughly one cell per ~3 world units at typical scene
    /// scales. Only consulted when <see cref="VolumeNoiseAmount"/> &gt; 0.</summary>
    public double VolumeNoiseScale;

    /// <summary>Cloud drift speed (rad/s, applied via <see cref="SceneTime"/>).
    /// 0 = static clouds. Mirrors the caustics pattern (separate animation
    /// speed knob) so a paused scene leaves clouds frozen even when the
    /// scene clock is incrementing for other effects.</summary>
    public double VolumeNoiseSpeed;

    /// <summary>FBM octave count [1, 6]. More octaves = finer cloud detail at
    /// the cost of one ValueNoise3D eval per extra octave. Default 3 keeps
    /// Phase 22 cost. Phase 22b.</summary>
    public int VolumeNoiseOctaves;

    /// <summary>Cloud self-shadow strength [0, 4]. 0 = off (bit-identical to
    /// Phase 22). When &gt;0, each volumetric sample marches a short ray
    /// toward Light1 sampling FBM density; the accumulated extinction
    /// attenuates the in-scatter as exp(-strength · accumulatedDensity).
    /// Produces visible god-ray banding inside dense clouds. Phase 22b.</summary>
    public double VolumeSelfShadow;

    /// <summary>Number of FBM samples taken along the light direction for the
    /// self-shadow march [0, 16]. 0 = skip. Default 4 = ~4× FbmCloud3D evals
    /// per volume step when self-shadow is on. Phase 22b.</summary>
    public int VolumeSelfShadowSteps;

    /// <summary>Vol-color slice B (#178) — Henyey-Greenstein phase anisotropy
    /// for the volumetric in-scatter, [-1, 1]. 0 = isotropic (bit-identical
    /// pre-slice-B). &gt;0 forward-scatters — a bright god-ray halo when the
    /// view ray points toward the light; &lt;0 back-scatters (halo away from
    /// the light). The phase is normalized so g=0 evaluates to exactly 1 (the
    /// 1/4π isotropic factor is folded into FogDensity / light intensity), and
    /// clamped internally to ±0.99 to avoid the forward-scatter singularity.
    /// Applied per light per volume step against dot(viewDir, lightDir).</summary>
    public double VolumeAnisotropy;

    /// <summary>Vol-color slice C (#179) — medium color / scattering albedo
    /// (packed BGRA). The accumulated in-scatter is multiplied by this tint,
    /// independent of the light colors, so the fog medium itself can be
    /// colored (amber haze, teal mist) while the lights stay white. Default
    /// white (0xFFFFFFFF) → ×1 → bit-identical pre-slice-C.</summary>
    public uint FogColor;

    /// <summary>Vol-color slice D (#180) — palette-mapped volumetric strength
    /// [0, 1]. 0 = off (bit-identical pre-slice-D). When &gt;0 the accumulated
    /// in-scatter is hue-remapped toward the active 3D color theme's gradient
    /// (sampled by optical depth 1−transmittance), preserving in-scatter
    /// brightness, then cross-faded by this amount — so the fog picks up the
    /// same palette as the fractal surface. Deliberately non-PBR: a stylised
    /// deviation consistent with FF's NPR surface color themes, layered on top
    /// of the physically-based light color (A) / phase (B) / medium color (C).
    /// Needs <see cref="VolumePalette"/> baked from the theme; a null/empty LUT
    /// makes this a no-op regardless of strength.</summary>
    public double VolumePaletteStrength;

    /// <summary>Vol-color slice D (#180) — runtime-only theme gradient LUT
    /// (packed ARGB, any length ≥2) the calculator bakes once per frame from
    /// its active <c>IColorMap</c> when <see cref="VolumePaletteStrength"/>
    /// &gt; 0. Sampled by normalized optical depth in
    /// <c>ShadingPipeline.VolumetricInScatter</c>. Not a user knob and not
    /// serialized — a reference field on this value type, defaulting to null
    /// (no palette → slice D is a no-op). GPU parity would upload this as its
    /// own buffer; deferred (the GPU path uses cheap-palette albedo, not the
    /// theme).</summary>
    public uint[]? VolumePalette;

    // ── Material (Phase 6 PBR-lite) ───────────────────────────────────

    /// <summary>Surface roughness for GGX specular [0, 1]. 0 = mirror,
    /// 1 = lambert. Legacy = 1.</summary>
    public double Roughness;

    /// <summary>Metallic interpolation [0, 1]. 0 = dielectric (white spec),
    /// 1 = metal (albedo-tinted spec, no diffuse). Phase 6.</summary>
    public double Metallic;

    /// <summary>Specular term strength multiplier. 0 = no spec (legacy).</summary>
    public double SpecularStrength;

    /// <summary>Sub-surface scattering strength [0, 1]. 0 = off. Cheap fake:
    /// back-lit lobe via dot(-L, V) * exp(-distInside * k). Phase 13.</summary>
    public double SubSurfaceStrength;

    // ── Refraction / transmission (S5, #389) ──────────────────────────────

    /// <summary>Transmission [0, 1]: how much the surface refracts light through
    /// itself instead of shading opaque → glass fractals. 0 = opaque (legacy,
    /// byte-identical). Blends the refracted-and-continued ray against the opaque
    /// shade by this amount.</summary>
    public double Transmission;

    /// <summary>Index of refraction for the transmissive surface. 1.0 = no bend,
    /// 1.5 ≈ glass, 1.33 ≈ water, 2.4 ≈ diamond. Only consulted when
    /// <see cref="Transmission"/> &gt; 0. Default 1.5.</summary>
    public double Ior;

    /// <summary>0xAARRGGBB Beer-Lambert absorption tint — the color that survives
    /// one <see cref="AbsorptionDistance"/> of travel inside the medium. White
    /// (0xFFFFFFFF) = clear (no absorption). Only consulted when
    /// <see cref="Transmission"/> &gt; 0.</summary>
    public uint AbsorptionColor;

    /// <summary>Reference distance (world units) over which <see cref="AbsorptionColor"/>
    /// is reached. Larger = clearer glass. ≤ 0 disables absorption.</summary>
    public double AbsorptionDistance;

    // ── Triplanar texture (Phase 14) ──────────────────────────────────

    /// <summary>Procedural texture selector. <see cref="TriplanarTextureKind.None"/>
    /// = off (bit-identical legacy). Otherwise the surface point is projected
    /// onto each axis plane, sampled by a math function, blended by squared
    /// normal weights, then multiplied into the albedo.</summary>
    public TriplanarTextureKind TriplanarKind;

    /// <summary>World-space frequency of the procedural pattern. Default 4.
    /// Smaller = coarser features; larger = denser detail. Multiplies the
    /// surface position before sampling.</summary>
    public double TriplanarScale;

    /// <summary>Blend amount of the texture into the albedo [0, 1]. 0 = no
    /// effect; 1 = full replacement. Default 0 so legacy renders stay
    /// identical.</summary>
    public double TriplanarStrength;

    /// <summary>Tint color the procedural pattern modulates against. White
    /// (0xFFFFFFFF) lets the texture darken/lighten the albedo as a luma map;
    /// a coloured tint stylises the surface (e.g. amber wood, blue marble).</summary>
    public uint TriplanarTint;

    // ── Sky + IBL ─────────────────────────────────────────────────────

    public SkyMode SkyMode;
    /// <summary>Top-of-sky color. Used by Gradient & Solid modes; also fog tint.</summary>
    public uint BgTopColor;
    /// <summary>Bottom-of-sky color (horizon).</summary>
    public uint BgBottomColor;
    /// <summary>HDRI environment preset name. Resolved by Phase 6 IBL lookup.
    /// Null/empty = "studio" default. Stored as string for theme-editor
    /// portability; payload lives outside the params.</summary>
    public string? EnvironmentName;

    /// <summary>Environment IBL contribution to ambient [0, 1]. 0 = use
    /// flat AmbientStrength only. Phase 6.</summary>
    public double IblStrength;

    /// <summary>When <c>true</c>, ray-miss pixels render the sky backdrop
    /// (HDRI sample or gradient) — visible behind the fractal. When
    /// <c>false</c> (default), ray-miss pixels fall back to the colormap's
    /// <c>InSetColor</c> so the background stays flat while IBL continues
    /// to contribute to surface lighting. Default off so HDRI-as-light works
    /// on the fractal without the photographic backdrop competing with the
    /// fractal for visual focus; opt in for full environment composite.</summary>
    public bool ShowSkyBackdrop;

    // ── Post (Phase 7) ────────────────────────────────────────────────

    public ToneMapOperator ToneMap;
    /// <summary>Linear exposure multiplier before tone map. 1 = neutral.</summary>
    public double Exposure;
    /// <summary>Bloom threshold (HDR luminance above which pixels bloom).
    /// 0 = bloom every pixel; >= 10 = effectively off (legacy).</summary>
    public double BloomThreshold;
    /// <summary>Bloom additive strength [0, 1]. 0 = off.</summary>
    public double BloomStrength;
    /// <summary>Chromatic aberration radial offset (pixels). 0 = off. Phase 15.</summary>
    public double ChromaticAberration;
    /// <summary>Barrel/pincushion lens distortion coefficient. 0 = off. Phase 15.</summary>
    public double LensDistortion;
    /// <summary>Phase 15b — vignette strength [0, 1]. 0 = uniform. Applied as
    /// cos⁴-style radial darken multiplied by this knob; corners go to
    /// (1 − Vignette)·colour. Lens warp is applied first so the darkened
    /// corners coincide with the warped image edge. Default 0 keeps Phase 15
    /// renders bit-identical.</summary>
    public double Vignette;
    /// <summary>Phase 15b — tangential (decentering) lens distortion. Brown
    /// model 2-coefficient (p1, p2) — simulates a lens whose optical centre
    /// is offset from the sensor centre. Magnitudes typically &lt; ±0.05.
    /// Default 0 = no decentring (centred / aligned lens).</summary>
    public double LensTangentialX;
    /// <summary>Phase 15b — tangential coefficient p2. See
    /// <see cref="LensTangentialX"/>.</summary>
    public double LensTangentialY;
    /// <summary>Phase 15b — anamorphic squeeze on the lens warp Y axis [0.25, 4].
    /// 1 = isotropic / circular. &gt;1 stretches vertically (wider horizontal
    /// imaging area, scope look); &lt;1 squashes vertically. Applied after
    /// radial distortion so the corner pull is anisotropic. Default 1.</summary>
    public double AnamorphicSqueeze;

    // ── Reflection probe (Phase 16) ───────────────────────────────────

    /// <summary>One-bounce reflection contribution [0, 1]. 0 = off.
    /// Cost: ~24 extra DE evals per reflective pixel.</summary>
    public double ReflectionStrength;
    /// <summary>Max steps for reflection ray. 0 = use ShadowSteps default.</summary>
    public int ReflectionSteps;
    /// <summary>Phase 16b — max reflection bounces [1, 6]. 1 = legacy single
    /// bounce. Each extra bounce traces the reflected ray against the surface
    /// again; the contribution is attenuated by Fresnel × ReflectionStrength
    /// per bounce so it fades geometrically. Cost scales linearly with this
    /// value × ReflectionSteps DE evals per pixel; UI tooltip flags values &gt;2
    /// as preview-only. Default 1 stays bit-identical with pre-16b renders.</summary>
    public int MaxBounces;

    /// <summary>Wave 4.2 — GGX importance sampling per reflection bounce. When
    /// true, the reflect direction at each bounce is sampled from a GGX VNDF
    /// (Heitz 2018) parameterised by <see cref="Roughness"/> instead of mirror-
    /// reflecting the view ray. Deterministic Wang-hash RNG seeded by the
    /// per-bounce world-space origin so animations stay stable. Single sample
    /// per bounce — temporal/spatial decorrelation provides the lobe spread
    /// without averaging cost. Default false preserves bit-identity with 16b
    /// (mirror reflect + roughness-convolved IBL on miss).</summary>
    public bool UseGgxSampling;

    // ── Caustics (Phase 17) ───────────────────────────────────────────

    /// <summary>Fake caustics contribution [0, 1+]. 0 = off. Bright caustic
    /// pattern modulates the key light's direct lighting on upward-facing
    /// surfaces; falls off with vertical distance from a virtual focusing
    /// plane (<see cref="CausticsFloorY"/>). Cheap procedural pattern, no
    /// path tracing.</summary>
    public double CausticsStrength;

    /// <summary>World-Y of the virtual focusing plane. Caustic intensity
    /// falls off as exp(-|y - CausticsFloorY| · 2). 0 = at world origin.</summary>
    public double CausticsFloorY;

    /// <summary>Pattern frequency multiplier. Larger = denser focused spots.
    /// Default 3.</summary>
    public double CausticsScale;

    /// <summary>Tint color of the caustic highlight (packed BGRA). White
    /// (0xFFFFFFFF) gives a pure energy boost; a warm tint (amber/yellow)
    /// reads as classic underwater-sun caustics.</summary>
    public uint CausticsColor;

    // ── Edge contour (Phase 23) ───────────────────────────────────────

    /// <summary>Sobel-on-normal edge ink strength [0, 1]. 0 = off.
    /// Ink alpha at each pixel = clamp((sobelMag − EdgeThreshold) /
    /// (1 − EdgeThreshold), 0, 1) · EdgeStrength, so the strength knob acts as
    /// a maximum opacity. Phase 23.</summary>
    public double EdgeStrength;
    /// <summary>Edge ink color (packed BGRA). Black (0xFF000000) gives a
    /// classic comic-ink outline; colored values produce stylised cel-shaded
    /// rims.</summary>
    public uint EdgeColor;
    /// <summary>Sobel-magnitude threshold below which no ink is drawn [0, ~2.8].
    /// Default 0.4 — catches obvious crease edges while leaving smooth surfaces
    /// untouched. Lower = more edges (eventually noisy); higher = only the
    /// sharpest silhouettes ink. Magnitude is measured on unit-normal channels
    /// so the unit-disk maximum is sqrt(8) ≈ 2.83. Phase 23.</summary>
    public double EdgeThreshold;
    /// <summary>Edge-ink kernel selector (Phase 23b). Sobel = original
    /// fast 3×3 isotropic-ish gradient; FreiChen = 9-tap edge subspace
    /// projection, picks up diagonals more uniformly. Default Sobel
    /// preserves bit-identity with Phase 23.</summary>
    public EdgeKernelMode EdgeKernel;

    // ── Stereo (Phase 20) ─────────────────────────────────────────────

    /// <summary>Interpupillary distance for side-by-side stereo render.
    /// 0 = mono (legacy). Output width doubles when non-zero. Phase 20 ships
    /// fake-stereo: monocular render + depth-parallax warp produces the right
    /// eye. Phase 20b adds true per-eye render via camera offset for higher
    /// quality at the cost of doubled render time — driven by
    /// <see cref="StereoMode"/>.</summary>
    public double StereoEyeSeparation;

    /// <summary>Phase 20b — stereo render mode. Default <see cref="StereoMode.Off"/>
    /// = mono. When the user toggles stereo on, host code routes through
    /// <see cref="StereoMode.Fake"/> (Phase 20 depth-parallax warp; cheap) or
    /// <see cref="StereoMode.True"/> (two-eye render with camera offset, doubled
    /// cost). The legacy behaviour (StereoEyeSeparation &gt; 0 → fake stereo)
    /// is treated as <see cref="StereoMode.Fake"/> by the host bridge so old
    /// saved scenes still pick up the warp.</summary>
    public StereoMode StereoMode;

    /// <summary>Phase 20b — transient per-eye camera-offset along the right
    /// basis (world units). Set by <see cref="StereoRender.RenderTrueStereo"/>
    /// to <c>-IPD/2</c> on the left-eye pass and <c>+IPD/2</c> on the right-eye
    /// pass; reset to 0 afterwards. Each 3D calculator's <c>Calculate</c> adds
    /// <c>right · EyeOffset</c> to its camera origin right after computing the
    /// basis. Default 0 → no shift (mono).</summary>
    public double StereoEyeOffset;

    /// <summary>Horizontal field of view in degrees, used to derive a focal-
    /// length-in-pixels proxy for the depth-parallax warp:
    /// <c>focalPx = width / (2 · tan(fov/2))</c>. Default 60° matches the
    /// typical fractal scene camera. Larger FOV = wider lens = stronger
    /// parallax at the same eye separation.</summary>
    public double StereoFovDegrees;

    /// <summary>Convergence via horizontal image translation (HIT), expressed
    /// as a fraction of image width. 0 = parallel cameras (whole scene sits
    /// behind the screen; comfortable but nothing pops). Positive = cross /
    /// pull the zero-parallax plane toward the viewer so the subject sits at
    /// the screen plane and closer detail floats in front. The SBS compositor
    /// shifts the left eye by <c>+conv·width/2</c> and the right eye by
    /// <c>-conv·width/2</c> (edge-clamped). Applies to both Fake and True
    /// paths. Typical range ±0.05. Default 0 = legacy behaviour.</summary>
    public double StereoConvergence;

    /// <summary>Parallax comfort guard — the maximum on-screen horizontal
    /// disparity allowed between the two eyes, as a fraction of image width.
    /// The Fake depth-parallax warp clamps its per-pixel shift to this so a
    /// very near hit cannot produce a disparity the eyes can't fuse
    /// (divergence → eye strain). Also drives
    /// <see cref="StereoRender.SuggestEyeSeparation"/>. Default 0.03 ≈ the
    /// "1/30 rule". 0 disables the clamp.</summary>
    public double StereoMaxDisparity;

    /// <summary>Side-by-side packing — <see cref="StereoLayout.FullSbs"/>
    /// (default, 2·W × H) or <see cref="StereoLayout.HalfSbs"/> (W × H,
    /// each eye squeezed to half width). Applied by the SBS compositor after
    /// convergence.</summary>
    public StereoLayout StereoLayout;

    // ── DoF (Phase 21) ────────────────────────────────────────────────

    /// <summary>Aperture radius in world units. 0 = pinhole (no DoF).</summary>
    public double DofAperture;
    /// <summary>Focus distance from camera (world units).</summary>
    public double DofFocusDistance;
    /// <summary>Lens samples for hex-bokeh aperture. 6/12/18 typical.</summary>
    public int DofSamples;

    /// <summary>Route SSAO + (future) tonemap/bloom post-passes through the
    /// ILGPU kernel dispatcher when an accelerator is available. Falls back
    /// to the CPU path on any failure (GPU init failure, OOM, kernel throw).
    /// Phase 12.</summary>
    public bool UseGpuPost;

    /// <summary>P7 — route the primary raymarch + DE through an ILGPU kernel
    /// when a per-fractal GPU calculator is available (Engine/Calculators/Gpu/).
    /// Falls back to the CPU Calculate path on any failure (GPU init failure,
    /// OOM, kernel throw, unsupported fractal). Default off — opt-in until
    /// per-fractal GPU calculators land and reach visual parity with CPU.
    /// Distinct from <see cref="UseGpuPost"/>: that gates post-passes; this
    /// gates the primary raymarch.</summary>
    public bool UseGpuRender;

    // ── Animation (Phase 18) ──────────────────────────────────────────

    /// <summary>Global scene time in seconds. Drives Light orbit + pulse.
    /// Replaces UserBulbTime for cross-fractal animation use.</summary>
    public double SceneTime;
    /// <summary>Light 1 orbit angular speed (rad/s) around world Y. 0 = static.
    /// Light2/Light3 inherit at 0.7× and 1.3× so the three lights desync into
    /// a slow choreographed sweep instead of moving in lockstep.</summary>
    public double LightOrbitSpeed;

    /// <summary>Caustic pattern phase speed (rad/s). 0 = static caustics
    /// (bit-identical legacy). Drives an additive offset on the sin-cascade
    /// args inside <see cref="ShadingPipeline.EvaluateCaustics"/> so the
    /// focused crests drift like rippling water.</summary>
    public double CausticsAnimSpeed;

    // ── Debug HUD (Phase 19) ──────────────────────────────────────────

    /// <summary>Bitmask of debug overlays drawn on the final color buffer.
    /// 0 = off (bit-identical legacy). bit 0 (0x1) = light-direction compass
    /// in the top-right corner; bit 1 (0x2) = key-knob strength bars along
    /// the bottom; bit 2 (0x4) = scene-time tick wheel in the top-left.
    /// Pure visual — no font/text. Useful for verifying that animation +
    /// orbit math is doing what the parameters say it is.</summary>
    public int DebugHudFlags;

    /// <summary>#317 — AOV / view-mode override. <see cref="AovView.Beauty"/>
    /// (default) = normal shaded output, bit-identical. Any other value makes
    /// <see cref="ShadingPipeline.Shade{TDe}"/> return that diagnostic buffer for
    /// each surface hit (CPU raymarchers + CPU relief; miss pixels keep the
    /// background).</summary>
    public AovView DebugAov;

    // ────────────────────────────────────────────────────────────────────

    /// <summary>Construct the legacy-equivalent default set. Single key
    /// light overhead, 15% ambient, no AO/shadow/fog/bloom — matches the
    /// existing pre-Phase-1 look so calculators stay pixel-identical
    /// until they opt into new effects.</summary>
    public static LightingFxData CreateDefault() => new()
    {
        Light1 = new DirectionalLight(
            theta: Math.PI * 0.25,
            phi: Math.PI * 0.45,
            intensity: 1.0,
            color: 0xFFFFFFFFu),
        Light2 = new DirectionalLight(
            theta: Math.PI * 1.25,
            phi: Math.PI * 0.55,
            intensity: 0.0,
            color: 0xFFB0C8FFu),
        Light3 = new DirectionalLight(
            theta: Math.PI * 0.75,
            phi: Math.PI * 0.30,
            intensity: 0.0,
            color: 0xFFFFC890u),

        AmbientStrength    = 0.15,
        AoSamples          = 0,
        AoStrength         = 0.4,
        SsaoSamples        = 0,
        SsaoRadius         = 0.2,
        SsaoStrength       = 0.5,

        ShadowSteps        = 0,
        ShadowSoftK        = 8.0,
        ShadowLightMask    = 0x1,

        FogDensity         = 0.0,
        FogHeightFalloff   = 0.0,
        VolumeSteps        = 0,
        VolumeStepsFalloff = 0.5,
        VolumeNoiseAmount  = 0.0,
        VolumeNoiseScale   = 0.3,
        VolumeNoiseSpeed   = 0.0,
        VolumeNoiseOctaves = 3,
        VolumeSelfShadow   = 0.0,
        VolumeSelfShadowSteps = 4,
        VolumeAnisotropy   = 0.0,
        FogColor           = 0xFFFFFFFFu,
        VolumePaletteStrength = 0.0,
        VolumePalette      = null,

        Roughness          = 1.0,
        Metallic           = 0.0,
        SpecularStrength   = 0.0,
        SubSurfaceStrength = 0.0,
        Transmission       = 0.0,          // opaque (legacy)
        Ior                = 1.5,          // glass
        AbsorptionColor    = 0xFFFFFFFFu,  // clear
        AbsorptionDistance = 1.0,

        TriplanarKind      = TriplanarTextureKind.None,
        TriplanarScale     = 4.0,
        TriplanarStrength  = 0.0,
        TriplanarTint      = 0xFFFFFFFFu,

        SkyMode            = SkyMode.Gradient,
        BgTopColor         = 0xFF202040u,
        BgBottomColor      = 0xFF101020u,
        EnvironmentName    = null,
        IblStrength        = 0.0,
        ShowSkyBackdrop    = false,

        ToneMap            = ToneMapOperator.None,
        Exposure           = 1.0,
        BloomThreshold     = 10.0,
        BloomStrength      = 0.0,
        ChromaticAberration = 0.0,
        LensDistortion     = 0.0,
        Vignette           = 0.0,
        LensTangentialX    = 0.0,
        LensTangentialY    = 0.0,
        AnamorphicSqueeze  = 1.0,

        ReflectionStrength = 0.0,
        ReflectionSteps    = 24,   // effective default; runtime still treats 0 as "auto → 24" for legacy presets
        MaxBounces         = 1,
        UseGgxSampling     = false,

        CausticsStrength   = 0.0,
        CausticsFloorY     = 0.0,
        CausticsScale      = 3.0,
        CausticsColor      = 0xFFFFFFFFu,

        EdgeStrength       = 0.0,
        EdgeColor          = 0xFF000000u,
        EdgeThreshold      = 0.4,
        EdgeKernel         = EdgeKernelMode.Sobel,

        StereoEyeSeparation = 0.0,
        StereoFovDegrees    = 60.0,
        StereoMode          = StereoMode.Off,
        StereoEyeOffset     = 0.0,
        StereoConvergence   = 0.0,
        StereoMaxDisparity  = 0.03,
        StereoLayout        = StereoLayout.FullSbs,

        DofAperture        = 0.0,
        DofFocusDistance   = 3.0,
        DofSamples         = 8,

        SceneTime          = 0.0,
        LightOrbitSpeed    = 0.0,
        CausticsAnimSpeed  = 0.0,

        DebugHudFlags      = 0,

        UseGpuPost         = false,
        UseGpuRender       = false,
    };
}
