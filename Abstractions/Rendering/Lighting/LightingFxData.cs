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

/// <summary>
/// Single directional light: spherical angles (theta = azimuth around Y,
/// phi = elevation from +Y), packed BGRA color, scalar intensity.
/// Intensity 0 = off (no contribution; skip dot product).
/// </summary>
public struct DirectionalLight
{
    /// <summary>Azimuth around the world +Y axis (radians).</summary>
    public double Theta;
    /// <summary>Elevation from world +Y (radians). 0 = straight up,
    /// pi/2 = horizon, pi = straight down.</summary>
    public double Phi;
    /// <summary>Scalar multiplier on diffuse + specular. 0 = light off.</summary>
    public double Intensity;
    /// <summary>0xAARRGGBB packed color. Multiplies diffuse/spec.</summary>
    public uint Color;

    public DirectionalLight(double theta, double phi, double intensity, uint color)
    {
        Theta = theta; Phi = phi; Intensity = intensity; Color = color;
    }
}

/// <summary>
/// Tone-map operator selector. Applied at the end of ShadingPipeline.Shade
/// once HDR linear color is composited.
/// </summary>
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
    /// eye. Phase 20b (deferred) will add true per-eye render via camera
    /// offset for higher quality at the cost of doubled render time.</summary>
    public double StereoEyeSeparation;

    /// <summary>Horizontal field of view in degrees, used to derive a focal-
    /// length-in-pixels proxy for the depth-parallax warp:
    /// <c>focalPx = width / (2 · tan(fov/2))</c>. Default 60° matches the
    /// typical fractal scene camera. Larger FOV = wider lens = stronger
    /// parallax at the same eye separation.</summary>
    public double StereoFovDegrees;

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

        Roughness          = 1.0,
        Metallic           = 0.0,
        SpecularStrength   = 0.0,
        SubSurfaceStrength = 0.0,

        TriplanarKind      = TriplanarTextureKind.None,
        TriplanarScale     = 4.0,
        TriplanarStrength  = 0.0,
        TriplanarTint      = 0xFFFFFFFFu,

        SkyMode            = SkyMode.Gradient,
        BgTopColor         = 0xFF202040u,
        BgBottomColor      = 0xFF101020u,
        EnvironmentName    = null,
        IblStrength        = 0.0,

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
        ReflectionSteps    = 0,

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

        DofAperture        = 0.0,
        DofFocusDistance   = 3.0,
        DofSamples         = 8,

        SceneTime          = 0.0,
        LightOrbitSpeed    = 0.0,
        CausticsAnimSpeed  = 0.0,

        DebugHudFlags      = 0,

        UseGpuPost         = false,
    };
}
