// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/LightingFxPresetData.cs
//
// Phase 9 — JSON-serializable mirror of LightingFxData for theme bundles.
// LightingFxData is a runtime struct with public *fields*, optimal for hot
// per-pixel reads but not picked up by System.Text.Json (which defaults to
// properties only). This class mirrors the struct as properties so a colour
// theme JSON can carry its full lighting + post-FX preset alongside its
// gradient and Phong material.
//
// Pattern: theme author calls FromFx(FractalParameters.Lighting) after dialling
// in a look in the Lighting & FX panel, attaches the result to a ColorThemeData,
// and exports. Loader: theme picker calls preset.ApplyTo(FractalParameters)
// when the theme is selected to overwrite the active lighting block.
//
// Nullable on ColorThemeData: null = "theme has no opinion, leave the user's
// Lighting params alone". Non-null = snap the user's lighting to this preset.
// Matches the same opt-in semantics as Brightness/Contrast/Adaptive elsewhere
// on ColorThemeData.

using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Models;

/// <summary>
/// JSON-friendly property-based mirror of <see cref="LightingFxData"/>.
/// Default values match <see cref="LightingFxData.CreateDefault"/> so a theme
/// with a partial preset (only e.g. fog density) still produces a legible
/// scene when applied.
/// </summary>
public sealed class LightingFxPresetData
{
    // ── Lights ────────────────────────────────────────────────────────

    public double Light1Theta { get; set; } = 0.785398163;   // PI * 0.25
    public double Light1Phi   { get; set; } = 1.413716694;   // PI * 0.45
    public double Light1Intensity { get; set; } = 1.0;
    public uint   Light1Color { get; set; } = 0xFFFFFFFFu;

    public double Light2Theta { get; set; } = 3.926990817;   // PI * 1.25
    public double Light2Phi   { get; set; } = 1.727875960;   // PI * 0.55
    public double Light2Intensity { get; set; } = 0.0;
    public uint   Light2Color { get; set; } = 0xFFB0C8FFu;

    public double Light3Theta { get; set; } = 2.356194490;   // PI * 0.75
    public double Light3Phi   { get; set; } = 0.942477796;   // PI * 0.30
    public double Light3Intensity { get; set; } = 0.0;
    public uint   Light3Color { get; set; } = 0xFFFFC890u;

    // ── Ambient / AO ──────────────────────────────────────────────────

    public double AmbientStrength { get; set; } = 0.15;
    public int    AoSamples       { get; set; } = 0;
    public double AoStrength      { get; set; } = 0.4;
    public int    SsaoSamples     { get; set; } = 0;
    public double SsaoRadius      { get; set; } = 0.2;
    public double SsaoStrength    { get; set; } = 0.5;

    // ── Shadow ────────────────────────────────────────────────────────

    public int    ShadowSteps     { get; set; } = 0;
    public double ShadowSoftK     { get; set; } = 8.0;
    public int    ShadowLightMask { get; set; } = 0x1;

    // ── Fog / Volume ──────────────────────────────────────────────────

    public double FogDensity        { get; set; } = 0.0;
    public double FogHeightFalloff  { get; set; } = 0.0;
    public int    VolumeSteps       { get; set; } = 0;
    public double VolumeNoiseAmount { get; set; } = 0.0;
    public double VolumeNoiseScale  { get; set; } = 0.3;
    public double VolumeNoiseSpeed  { get; set; } = 0.0;
    public int    VolumeNoiseOctaves { get; set; } = 3;
    public double VolumeSelfShadow   { get; set; } = 0.0;
    public int    VolumeSelfShadowSteps { get; set; } = 4;
    public double VolumeAnisotropy   { get; set; } = 0.0;

    // ── Material ──────────────────────────────────────────────────────

    public double Roughness          { get; set; } = 1.0;
    public double Metallic           { get; set; } = 0.0;
    public double SpecularStrength   { get; set; } = 0.0;
    public double SubSurfaceStrength { get; set; } = 0.0;

    // ── Sky / IBL ─────────────────────────────────────────────────────

    public SkyMode SkyMode         { get; set; } = SkyMode.Gradient;
    public uint    BgTopColor      { get; set; } = 0xFF202040u;
    public uint    BgBottomColor   { get; set; } = 0xFF101020u;
    public string? EnvironmentName { get; set; }
    public double  IblStrength     { get; set; } = 0.0;
    public bool    ShowSkyBackdrop { get; set; } = false;

    // ── Post ──────────────────────────────────────────────────────────

    public ToneMapOperator ToneMap { get; set; } = ToneMapOperator.None;
    public double Exposure            { get; set; } = 1.0;
    public double BloomThreshold      { get; set; } = 10.0;
    public double BloomStrength       { get; set; } = 0.0;
    public double ChromaticAberration { get; set; } = 0.0;
    public double LensDistortion      { get; set; } = 0.0;
    public double Vignette            { get; set; } = 0.0;
    public double LensTangentialX     { get; set; } = 0.0;
    public double LensTangentialY     { get; set; } = 0.0;
    public double AnamorphicSqueeze   { get; set; } = 1.0;

    // ── Reflection / Edge / Stereo / DoF / Anim ───────────────────────

    public double ReflectionStrength  { get; set; } = 0.0;
    public int    ReflectionSteps     { get; set; } = 0;
    public double CausticsStrength    { get; set; } = 0.0;
    public double CausticsFloorY      { get; set; } = 0.0;
    public double CausticsScale       { get; set; } = 3.0;
    public uint   CausticsColor       { get; set; } = 0xFFFFFFFFu;
    public double EdgeStrength        { get; set; } = 0.0;
    public uint   EdgeColor           { get; set; } = 0xFF000000u;
    public double EdgeThreshold       { get; set; } = 0.4;
    public EdgeKernelMode EdgeKernel  { get; set; } = EdgeKernelMode.Sobel;
    public StereoMode StereoMode      { get; set; } = StereoMode.Off;
    public double StereoEyeSeparation { get; set; } = 0.0;
    public double StereoFovDegrees    { get; set; } = 60.0;
    public double StereoConvergence   { get; set; } = 0.0;
    public double StereoMaxDisparity  { get; set; } = 0.03;
    public StereoLayout StereoLayout  { get; set; } = StereoLayout.FullSbs;
    public double DofAperture         { get; set; } = 0.0;
    public double DofFocusDistance    { get; set; } = 3.0;
    public int    DofSamples          { get; set; } = 8;
    public double SceneTime           { get; set; } = 0.0;
    public double LightOrbitSpeed     { get; set; } = 0.0;
    public double CausticsAnimSpeed   { get; set; } = 0.0;
    public int    DebugHudFlags       { get; set; } = 0;

    // ── Conversion ────────────────────────────────────────────────────

    /// <summary>Snapshot a <see cref="LightingFxData"/> into a preset DTO
    /// (e.g. after the user has dialled in a look they want a theme to
    /// remember).</summary>
    public static LightingFxPresetData FromFx(in LightingFxData fx) => new()
    {
        Light1Theta = fx.Light1.Theta, Light1Phi = fx.Light1.Phi,
        Light1Intensity = fx.Light1.Intensity, Light1Color = fx.Light1.Color,
        Light2Theta = fx.Light2.Theta, Light2Phi = fx.Light2.Phi,
        Light2Intensity = fx.Light2.Intensity, Light2Color = fx.Light2.Color,
        Light3Theta = fx.Light3.Theta, Light3Phi = fx.Light3.Phi,
        Light3Intensity = fx.Light3.Intensity, Light3Color = fx.Light3.Color,

        AmbientStrength = fx.AmbientStrength,
        AoSamples = fx.AoSamples, AoStrength = fx.AoStrength,
        SsaoSamples = fx.SsaoSamples, SsaoRadius = fx.SsaoRadius, SsaoStrength = fx.SsaoStrength,

        ShadowSteps = fx.ShadowSteps, ShadowSoftK = fx.ShadowSoftK, ShadowLightMask = fx.ShadowLightMask,

        FogDensity = fx.FogDensity, FogHeightFalloff = fx.FogHeightFalloff,
        VolumeSteps = fx.VolumeSteps, VolumeNoiseAmount = fx.VolumeNoiseAmount,
        VolumeNoiseScale = fx.VolumeNoiseScale, VolumeNoiseSpeed = fx.VolumeNoiseSpeed,
        VolumeNoiseOctaves = fx.VolumeNoiseOctaves,
        VolumeSelfShadow = fx.VolumeSelfShadow, VolumeSelfShadowSteps = fx.VolumeSelfShadowSteps,
        VolumeAnisotropy = fx.VolumeAnisotropy,

        Roughness = fx.Roughness, Metallic = fx.Metallic,
        SpecularStrength = fx.SpecularStrength, SubSurfaceStrength = fx.SubSurfaceStrength,

        SkyMode = fx.SkyMode, BgTopColor = fx.BgTopColor, BgBottomColor = fx.BgBottomColor,
        EnvironmentName = fx.EnvironmentName, IblStrength = fx.IblStrength,
        ShowSkyBackdrop = fx.ShowSkyBackdrop,

        ToneMap = fx.ToneMap, Exposure = fx.Exposure,
        BloomThreshold = fx.BloomThreshold, BloomStrength = fx.BloomStrength,
        ChromaticAberration = fx.ChromaticAberration, LensDistortion = fx.LensDistortion,
        Vignette = fx.Vignette,
        LensTangentialX = fx.LensTangentialX, LensTangentialY = fx.LensTangentialY,
        AnamorphicSqueeze = fx.AnamorphicSqueeze,

        ReflectionStrength = fx.ReflectionStrength, ReflectionSteps = fx.ReflectionSteps,
        CausticsStrength = fx.CausticsStrength, CausticsFloorY = fx.CausticsFloorY,
        CausticsScale = fx.CausticsScale, CausticsColor = fx.CausticsColor,
        EdgeStrength = fx.EdgeStrength, EdgeColor = fx.EdgeColor,
        EdgeThreshold = fx.EdgeThreshold, EdgeKernel = fx.EdgeKernel,
        StereoMode = fx.StereoMode, StereoLayout = fx.StereoLayout,
        StereoEyeSeparation = fx.StereoEyeSeparation, StereoFovDegrees = fx.StereoFovDegrees,
        StereoConvergence = fx.StereoConvergence, StereoMaxDisparity = fx.StereoMaxDisparity,
        DofAperture = fx.DofAperture, DofFocusDistance = fx.DofFocusDistance, DofSamples = fx.DofSamples,
        SceneTime = fx.SceneTime, LightOrbitSpeed = fx.LightOrbitSpeed,
        CausticsAnimSpeed = fx.CausticsAnimSpeed,
        DebugHudFlags = fx.DebugHudFlags,
    };

    /// <summary>Materialise this preset as a runtime <see cref="LightingFxData"/>
    /// struct ready to assign into <see cref="FractalParameters.Lighting"/>.</summary>
    public LightingFxData ToFx() => new()
    {
        Light1 = new DirectionalLight(Light1Theta, Light1Phi, Light1Intensity, Light1Color),
        Light2 = new DirectionalLight(Light2Theta, Light2Phi, Light2Intensity, Light2Color),
        Light3 = new DirectionalLight(Light3Theta, Light3Phi, Light3Intensity, Light3Color),

        AmbientStrength = AmbientStrength,
        AoSamples = AoSamples, AoStrength = AoStrength,
        SsaoSamples = SsaoSamples, SsaoRadius = SsaoRadius, SsaoStrength = SsaoStrength,

        ShadowSteps = ShadowSteps, ShadowSoftK = ShadowSoftK, ShadowLightMask = ShadowLightMask,

        FogDensity = FogDensity, FogHeightFalloff = FogHeightFalloff,
        VolumeSteps = VolumeSteps, VolumeNoiseAmount = VolumeNoiseAmount,
        VolumeNoiseScale = VolumeNoiseScale, VolumeNoiseSpeed = VolumeNoiseSpeed,
        VolumeNoiseOctaves = VolumeNoiseOctaves,
        VolumeSelfShadow = VolumeSelfShadow, VolumeSelfShadowSteps = VolumeSelfShadowSteps,
        VolumeAnisotropy = VolumeAnisotropy,

        Roughness = Roughness, Metallic = Metallic,
        SpecularStrength = SpecularStrength, SubSurfaceStrength = SubSurfaceStrength,

        SkyMode = SkyMode, BgTopColor = BgTopColor, BgBottomColor = BgBottomColor,
        EnvironmentName = EnvironmentName, IblStrength = IblStrength,
        ShowSkyBackdrop = ShowSkyBackdrop,

        ToneMap = ToneMap, Exposure = Exposure,
        BloomThreshold = BloomThreshold, BloomStrength = BloomStrength,
        ChromaticAberration = ChromaticAberration, LensDistortion = LensDistortion,
        Vignette = Vignette,
        LensTangentialX = LensTangentialX, LensTangentialY = LensTangentialY,
        AnamorphicSqueeze = AnamorphicSqueeze,

        ReflectionStrength = ReflectionStrength, ReflectionSteps = ReflectionSteps,
        CausticsStrength = CausticsStrength, CausticsFloorY = CausticsFloorY,
        CausticsScale = CausticsScale, CausticsColor = CausticsColor,
        EdgeStrength = EdgeStrength, EdgeColor = EdgeColor,
        EdgeThreshold = EdgeThreshold, EdgeKernel = EdgeKernel,
        StereoMode = StereoMode, StereoLayout = StereoLayout,
        StereoEyeSeparation = StereoEyeSeparation, StereoFovDegrees = StereoFovDegrees,
        StereoConvergence = StereoConvergence, StereoMaxDisparity = StereoMaxDisparity,
        DofAperture = DofAperture, DofFocusDistance = DofFocusDistance, DofSamples = DofSamples,
        SceneTime = SceneTime, LightOrbitSpeed = LightOrbitSpeed,
        CausticsAnimSpeed = CausticsAnimSpeed,
        DebugHudFlags = DebugHudFlags,
    };

    /// <summary>Apply this preset to a fractal parameter set. Overwrites
    /// <see cref="FractalParameters.Lighting"/> wholesale; pair with a
    /// "Lock lighting" UI toggle in the host if the user wants to preserve
    /// their tuned-in lighting across theme changes.</summary>
    public void ApplyTo(FractalParameters parameters)
    {
        if (parameters is null) return;
        var fx = ToFx();
        parameters.Lighting = fx;
        // Wave 4.3 — kick HDRI preload so the next render frame hits a warm
        // cache instead of N pixel threads racing the file parse.
        if (!string.IsNullOrWhiteSpace(fx.EnvironmentName))
            FracturingFog.Rendering.Lighting.HdriProbe.Preload?.Invoke(fx.EnvironmentName);
    }
}
