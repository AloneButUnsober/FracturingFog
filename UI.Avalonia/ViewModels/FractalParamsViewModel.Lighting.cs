// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FractalParamsViewModel.Lighting.cs
//
// Bindings for the shared LightingFxData parameter block. Every 3D raymarcher
// (Mandelbulb, Mandelbox, KIFS, Quat*, Bicomplex, Kleinian, UserBulb) reads
// the same struct so this single partial wires every scene's lights, AO,
// shadow, fog, material, sky, post knobs into FractalParamsView.
//
// FractalParameters.Lighting is a struct (value type). Setters copy the
// struct, mutate, write back — done via MutateLighting(...) so each property
// stays a one-liner.

using System;
using System.Reactive;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class FractalParamsViewModel
{
    // ── struct mutation helper ────────────────────────────────────────

    private void MutateLighting(Action<LightingFxParamRef> action)
    {
        var fx = _p.Lighting;
        action(new LightingFxParamRef(ref fx));
        _p.Lighting = fx;
    }

    /// <summary>Ref-wrapper so a setter can pass a delegate that mutates the
    /// struct in place without each property duplicating the copy/write-back
    /// boilerplate. Lifetime is bounded to the MutateLighting call so the
    /// ref-field is safe to hold.</summary>
    private readonly ref struct LightingFxParamRef
    {
        public readonly ref LightingFxData Fx;
        public LightingFxParamRef(ref LightingFxData fx) { Fx = ref fx; }
    }

    // ── HDRI Browse… helper ──────────────────────────────────────────

    /// <summary>Apply a freshly-picked HDRI path with auto-arm: switches
    /// <see cref="SkyMode"/> to <see cref="SkyMode.Hdri"/> if not already
    /// there, bumps <see cref="IblStrength"/> off zero if it's still at the
    /// default. Coalesces the three property changes into a single
    /// <c>ParamChanged</c> fire so the host re-renders once, not three
    /// times. Called from <c>FractalParamsView</c>'s Browse… handler.</summary>
    public void ApplyHdriPick(string path)
    {
        _suppress = true;
        try
        {
            EnvironmentName = path;
            if (SkyMode != SkyMode.Hdri) SkyMode = SkyMode.Hdri;
            if (IblStrength <= 0.0) IblStrength = 1.0;
        }
        finally { _suppress = false; }
        Fire();
    }

    // ── Lights ────────────────────────────────────────────────────────

    public double Light1Theta
    {
        get => _p.Lighting.Light1.Theta;
        set { MutateLighting(r => r.Fx.Light1.Theta = Clamp(value, -10, 10)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light1Phi
    {
        get => _p.Lighting.Light1.Phi;
        set { MutateLighting(r => r.Fx.Light1.Phi = Clamp(value, 0.01, 3.13)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light1Intensity
    {
        get => _p.Lighting.Light1.Intensity;
        set { MutateLighting(r => r.Fx.Light1.Intensity = Clamp(value, 0, 4)); this.RaisePropertyChanged(); Fire(); }
    }
    public uint Light1Color
    {
        get => _p.Lighting.Light1.Color;
        set { MutateLighting(r => r.Fx.Light1.Color = value); this.RaisePropertyChanged(); Fire(); }
    }

    public double Light2Theta
    {
        get => _p.Lighting.Light2.Theta;
        set { MutateLighting(r => r.Fx.Light2.Theta = Clamp(value, -10, 10)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light2Phi
    {
        get => _p.Lighting.Light2.Phi;
        set { MutateLighting(r => r.Fx.Light2.Phi = Clamp(value, 0.01, 3.13)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light2Intensity
    {
        get => _p.Lighting.Light2.Intensity;
        set { MutateLighting(r => r.Fx.Light2.Intensity = Clamp(value, 0, 4)); this.RaisePropertyChanged(); Fire(); }
    }
    public uint Light2Color
    {
        get => _p.Lighting.Light2.Color;
        set { MutateLighting(r => r.Fx.Light2.Color = value); this.RaisePropertyChanged(); Fire(); }
    }

    public double Light3Theta
    {
        get => _p.Lighting.Light3.Theta;
        set { MutateLighting(r => r.Fx.Light3.Theta = Clamp(value, -10, 10)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light3Phi
    {
        get => _p.Lighting.Light3.Phi;
        set { MutateLighting(r => r.Fx.Light3.Phi = Clamp(value, 0.01, 3.13)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light3Intensity
    {
        get => _p.Lighting.Light3.Intensity;
        set { MutateLighting(r => r.Fx.Light3.Intensity = Clamp(value, 0, 4)); this.RaisePropertyChanged(); Fire(); }
    }
    public uint Light3Color
    {
        get => _p.Lighting.Light3.Color;
        set { MutateLighting(r => r.Fx.Light3.Color = value); this.RaisePropertyChanged(); Fire(); }
    }

    // ── Light type: point / spot (roadmap S8, #404) ───────────────────
    //
    // Directional (default) keeps Theta/Phi as a constant world direction with
    // no attenuation — byte-identical to the pre-S8 render. Point/Spot add a
    // world Position, an inverse-square Range window, and (Spot) a cone from the
    // inner/outer half-angles. The engine + GPU relief kernel resolve these
    // per surface point (LightSampler). ShowPositional / ShowSpot gate the extra
    // controls so a directional light shows nothing new.

    /// <summary>Enum source for the three light-type combos.</summary>
    public Array LightTypes => Enum.GetValues(typeof(LightType));

    public LightType Light1Type
    {
        get => _p.Lighting.Light1.Type;
        set { MutateLighting(r => r.Fx.Light1.Type = value); this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(Light1ShowPositional)); this.RaisePropertyChanged(nameof(Light1ShowSpot)); Fire(); }
    }
    public bool Light1ShowPositional => _p.Lighting.Light1.Type != LightType.Directional;
    public bool Light1ShowSpot => _p.Lighting.Light1.Type == LightType.Spot;
    public double Light1PosX
    {
        get => _p.Lighting.Light1.PosX;
        set { MutateLighting(r => r.Fx.Light1.PosX = Clamp(value, -100, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light1PosY
    {
        get => _p.Lighting.Light1.PosY;
        set { MutateLighting(r => r.Fx.Light1.PosY = Clamp(value, -100, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light1PosZ
    {
        get => _p.Lighting.Light1.PosZ;
        set { MutateLighting(r => r.Fx.Light1.PosZ = Clamp(value, -100, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light1Range
    {
        get => _p.Lighting.Light1.Range;
        set { MutateLighting(r => r.Fx.Light1.Range = Clamp(value, 0, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light1SpotInnerDeg
    {
        get => _p.Lighting.Light1.SpotInnerDeg;
        set { MutateLighting(r => r.Fx.Light1.SpotInnerDeg = Clamp(value, 0, 89)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light1SpotOuterDeg
    {
        get => _p.Lighting.Light1.SpotOuterDeg;
        set { MutateLighting(r => r.Fx.Light1.SpotOuterDeg = Clamp(value, 0, 90)); this.RaisePropertyChanged(); Fire(); }
    }
    /// <summary>Area-light angular radius (deg). 0 = punctual/sharp shadow;
    /// larger softens the penumbra (roadmap S8, #404). Applies to any light type.</summary>
    public double Light1Area
    {
        get => _p.Lighting.Light1.AreaAngularRadius;
        set { MutateLighting(r => r.Fx.Light1.AreaAngularRadius = Clamp(value, 0, 90)); this.RaisePropertyChanged(); Fire(); }
    }

    public LightType Light2Type
    {
        get => _p.Lighting.Light2.Type;
        set { MutateLighting(r => r.Fx.Light2.Type = value); this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(Light2ShowPositional)); this.RaisePropertyChanged(nameof(Light2ShowSpot)); Fire(); }
    }
    public bool Light2ShowPositional => _p.Lighting.Light2.Type != LightType.Directional;
    public bool Light2ShowSpot => _p.Lighting.Light2.Type == LightType.Spot;
    public double Light2PosX
    {
        get => _p.Lighting.Light2.PosX;
        set { MutateLighting(r => r.Fx.Light2.PosX = Clamp(value, -100, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light2PosY
    {
        get => _p.Lighting.Light2.PosY;
        set { MutateLighting(r => r.Fx.Light2.PosY = Clamp(value, -100, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light2PosZ
    {
        get => _p.Lighting.Light2.PosZ;
        set { MutateLighting(r => r.Fx.Light2.PosZ = Clamp(value, -100, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light2Range
    {
        get => _p.Lighting.Light2.Range;
        set { MutateLighting(r => r.Fx.Light2.Range = Clamp(value, 0, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light2SpotInnerDeg
    {
        get => _p.Lighting.Light2.SpotInnerDeg;
        set { MutateLighting(r => r.Fx.Light2.SpotInnerDeg = Clamp(value, 0, 89)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light2SpotOuterDeg
    {
        get => _p.Lighting.Light2.SpotOuterDeg;
        set { MutateLighting(r => r.Fx.Light2.SpotOuterDeg = Clamp(value, 0, 90)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light2Area
    {
        get => _p.Lighting.Light2.AreaAngularRadius;
        set { MutateLighting(r => r.Fx.Light2.AreaAngularRadius = Clamp(value, 0, 90)); this.RaisePropertyChanged(); Fire(); }
    }

    public LightType Light3Type
    {
        get => _p.Lighting.Light3.Type;
        set { MutateLighting(r => r.Fx.Light3.Type = value); this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(Light3ShowPositional)); this.RaisePropertyChanged(nameof(Light3ShowSpot)); Fire(); }
    }
    public bool Light3ShowPositional => _p.Lighting.Light3.Type != LightType.Directional;
    public bool Light3ShowSpot => _p.Lighting.Light3.Type == LightType.Spot;
    public double Light3PosX
    {
        get => _p.Lighting.Light3.PosX;
        set { MutateLighting(r => r.Fx.Light3.PosX = Clamp(value, -100, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light3PosY
    {
        get => _p.Lighting.Light3.PosY;
        set { MutateLighting(r => r.Fx.Light3.PosY = Clamp(value, -100, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light3PosZ
    {
        get => _p.Lighting.Light3.PosZ;
        set { MutateLighting(r => r.Fx.Light3.PosZ = Clamp(value, -100, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light3Range
    {
        get => _p.Lighting.Light3.Range;
        set { MutateLighting(r => r.Fx.Light3.Range = Clamp(value, 0, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light3SpotInnerDeg
    {
        get => _p.Lighting.Light3.SpotInnerDeg;
        set { MutateLighting(r => r.Fx.Light3.SpotInnerDeg = Clamp(value, 0, 89)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light3SpotOuterDeg
    {
        get => _p.Lighting.Light3.SpotOuterDeg;
        set { MutateLighting(r => r.Fx.Light3.SpotOuterDeg = Clamp(value, 0, 90)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Light3Area
    {
        get => _p.Lighting.Light3.AreaAngularRadius;
        set { MutateLighting(r => r.Fx.Light3.AreaAngularRadius = Clamp(value, 0, 90)); this.RaisePropertyChanged(); Fire(); }
    }

    // ── Ambient / AO ──────────────────────────────────────────────────

    public double AmbientStrength
    {
        get => _p.Lighting.AmbientStrength;
        set { MutateLighting(r => r.Fx.AmbientStrength = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }
    public int AoSamples
    {
        get => _p.Lighting.AoSamples;
        set { MutateLighting(r => r.Fx.AoSamples = (int)Clamp(value, 0, 16)); this.RaisePropertyChanged(); Fire(); }
    }
    public double AoStrength
    {
        get => _p.Lighting.AoStrength;
        set { MutateLighting(r => r.Fx.AoStrength = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }
    public int SsaoSamples
    {
        get => _p.Lighting.SsaoSamples;
        set { MutateLighting(r => r.Fx.SsaoSamples = (int)Clamp(value, 0, 64)); this.RaisePropertyChanged(); Fire(); }
    }
    public double SsaoRadius
    {
        get => _p.Lighting.SsaoRadius;
        set { MutateLighting(r => r.Fx.SsaoRadius = Clamp(value, 0.001, 4)); this.RaisePropertyChanged(); Fire(); }
    }
    public double SsaoStrength
    {
        get => _p.Lighting.SsaoStrength;
        set { MutateLighting(r => r.Fx.SsaoStrength = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }

    // ── Shadow ────────────────────────────────────────────────────────

    public int ShadowSteps
    {
        get => _p.Lighting.ShadowSteps;
        set { MutateLighting(r => r.Fx.ShadowSteps = (int)Clamp(value, 0, 64)); this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(ShowVolumeShaftHint)); Fire(); }
    }

    // #309 — advisory: fog in-scatter is a flat glow until terrain shadows are
    // marched into it. Shafts (actual god-rays) need ShadowSteps > 0; VolumeSteps
    // alone only casts a uniform brighten. True when the user has fog + volume
    // steps up but shadows off, so a yellow (#FFCC00, colorblind-safe) hint shows.
    public bool ShowVolumeShaftHint
        => VolumeSteps > 0 && FogDensity > 0 && ShadowSteps == 0;
    public double ShadowSoftK
    {
        get => _p.Lighting.ShadowSoftK;
        set { MutateLighting(r => r.Fx.ShadowSoftK = Clamp(value, 0, 64)); this.RaisePropertyChanged(); Fire(); }
    }

    // ── Fog / Volumetric ─────────────────────────────────────────────

    public double FogDensity
    {
        get => _p.Lighting.FogDensity;
        set { MutateLighting(r => r.Fx.FogDensity = Clamp(value, 0, 2)); this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(ShowVolumeShaftHint)); Fire(); }
    }
    public double FogHeightFalloff
    {
        get => _p.Lighting.FogHeightFalloff;
        set { MutateLighting(r => r.Fx.FogHeightFalloff = Clamp(value, 0, 4)); this.RaisePropertyChanged(); Fire(); }
    }
    public int VolumeSteps
    {
        get => _p.Lighting.VolumeSteps;
        set { MutateLighting(r => r.Fx.VolumeSteps = (int)Clamp(value, 0, 64)); this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(ShowVolumeShaftHint)); Fire(); }
    }

    // S6 (#408) — per-light "lights the fog" toggles (VolumeLightMask bits). Clearing
    // a bit drops that light from the fog in-scatter ONLY (surfaces still lit). All on
    // by default. Same mask honoured by the per-pixel march, the froxel volume and the
    // GPU relief kernel, so toggling froxel never changes which lights fog.
    private void SetVolumeMaskBit(int bit, bool on)
    {
        MutateLighting(r =>
        {
            if (on) r.Fx.VolumeLightMask |= bit;
            else    r.Fx.VolumeLightMask &= ~bit;
        });
    }
    public bool Light1FogsVolume
    {
        get => (_p.Lighting.VolumeLightMask & 0x1) != 0;
        set { SetVolumeMaskBit(0x1, value); this.RaisePropertyChanged(); Fire(); }
    }
    public bool Light2FogsVolume
    {
        get => (_p.Lighting.VolumeLightMask & 0x2) != 0;
        set { SetVolumeMaskBit(0x2, value); this.RaisePropertyChanged(); Fire(); }
    }
    public bool Light3FogsVolume
    {
        get => (_p.Lighting.VolumeLightMask & 0x4) != 0;
        set { SetVolumeMaskBit(0x4, value); this.RaisePropertyChanged(); Fire(); }
    }
    public double VolumeNoiseAmount
    {
        get => _p.Lighting.VolumeNoiseAmount;
        set { MutateLighting(r => r.Fx.VolumeNoiseAmount = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }

    // ── Material ─────────────────────────────────────────────────────

    public double Roughness
    {
        get => _p.Lighting.Roughness;
        set { MutateLighting(r => r.Fx.Roughness = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Metallic
    {
        get => _p.Lighting.Metallic;
        set { MutateLighting(r => r.Fx.Metallic = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }
    // Glass / dielectric transmission (roadmap S5, #389). Transmission 0 = opaque
    // (byte-identical default); IOR bends the refracted environment.
    public double Transmission
    {
        get => _p.Lighting.Transmission;
        set { MutateLighting(r => r.Fx.Transmission = Clamp(value, 0, 1)); this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(GlassEnabled)); Fire(); }
    }
    public double Ior
    {
        get => _p.Lighting.Ior;
        set { MutateLighting(r => r.Fx.Ior = Clamp(value, 1.0, 3.0)); this.RaisePropertyChanged(); Fire(); }
    }
    public double AbsorptionDistance
    {
        get => _p.Lighting.AbsorptionDistance;
        set { MutateLighting(r => r.Fx.AbsorptionDistance = Clamp(value, 0.01, 10.0)); this.RaisePropertyChanged(); Fire(); }
    }
    /// <summary>IOR / absorption controls only matter once transmission is on.</summary>
    public bool GlassEnabled => _p.Lighting.Transmission > 0.0;
    // S5 (#406) — full internal glass march (real thickness + exit refraction) vs
    // the single-interface env approximation. Only bites when Transmission > 0.
    public bool RefractInternalMarch
    {
        get => _p.Lighting.RefractInternalMarch;
        set { MutateLighting(r => r.Fx.RefractInternalMarch = value); this.RaisePropertyChanged(); Fire(); }
    }
    public double SpecularStrength
    {
        get => _p.Lighting.SpecularStrength;
        set { MutateLighting(r => r.Fx.SpecularStrength = Clamp(value, 0, 4)); this.RaisePropertyChanged(); Fire(); }
    }
    public double SubSurfaceStrength
    {
        get => _p.Lighting.SubSurfaceStrength;
        set { MutateLighting(r => r.Fx.SubSurfaceStrength = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }

    // ── Sky / IBL ────────────────────────────────────────────────────

    public SkyMode SkyMode
    {
        get => _p.Lighting.SkyMode;
        set { MutateLighting(r => r.Fx.SkyMode = value); this.RaisePropertyChanged(); Fire(); }
    }
    public Array SkyModes => Enum.GetValues(typeof(SkyMode));

    public uint BgTopColor
    {
        get => _p.Lighting.BgTopColor;
        set { MutateLighting(r => r.Fx.BgTopColor = value); this.RaisePropertyChanged(); Fire(); }
    }
    public uint BgBottomColor
    {
        get => _p.Lighting.BgBottomColor;
        set { MutateLighting(r => r.Fx.BgBottomColor = value); this.RaisePropertyChanged(); Fire(); }
    }
    public string? EnvironmentName
    {
        get => _p.Lighting.EnvironmentName;
        set
        {
            MutateLighting(r => r.Fx.EnvironmentName = value);
            // Wave 4.3 — kick background preload so the next render frame
            // hits a warm HdriRegistry cache instead of N pixel threads
            // racing the same file parse.
            if (!string.IsNullOrWhiteSpace(value)) HdriProbe.Preload?.Invoke(value);
            this.RaisePropertyChanged();
            Fire();
        }
    }
    public double IblStrength
    {
        get => _p.Lighting.IblStrength;
        set { MutateLighting(r => r.Fx.IblStrength = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }

    /// <summary>When true, ray-miss pixels render the sky backdrop (HDRI or
    /// gradient). When false (default), miss pixels fall back to the colormap's
    /// InSetColor so the fractal silhouette stays clean while IBL still
    /// contributes to surface lighting. Opt in for full environment composite.</summary>
    public bool ShowSkyBackdrop
    {
        get => _p.Lighting.ShowSkyBackdrop;
        set { MutateLighting(r => r.Fx.ShowSkyBackdrop = value); this.RaisePropertyChanged(); Fire(); }
    }

    // ── Post ─────────────────────────────────────────────────────────

    public ToneMapOperator ToneMap
    {
        get => _p.Lighting.ToneMap;
        set { MutateLighting(r => r.Fx.ToneMap = value); this.RaisePropertyChanged(); Fire(); }
    }
    public Array ToneMapOperators => Enum.GetValues(typeof(ToneMapOperator));

    public double Exposure
    {
        get => _p.Lighting.Exposure;
        set { MutateLighting(r => r.Fx.Exposure = Clamp(value, 0.0625, 16)); this.RaisePropertyChanged(); Fire(); }
    }
    public double BloomThreshold
    {
        get => _p.Lighting.BloomThreshold;
        set { MutateLighting(r => r.Fx.BloomThreshold = Clamp(value, 0, 20)); this.RaisePropertyChanged(); Fire(); }
    }
    public double BloomStrength
    {
        get => _p.Lighting.BloomStrength;
        set { MutateLighting(r => r.Fx.BloomStrength = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }
    public double ChromaticAberration
    {
        get => _p.Lighting.ChromaticAberration;
        set { MutateLighting(r => r.Fx.ChromaticAberration = Clamp(value, 0, 16)); this.RaisePropertyChanged(); Fire(); }
    }
    public double LensDistortion
    {
        get => _p.Lighting.LensDistortion;
        set { MutateLighting(r => r.Fx.LensDistortion = Clamp(value, -0.5, 0.5)); this.RaisePropertyChanged(); Fire(); }
    }
    public double Vignette
    {
        get => _p.Lighting.Vignette;
        set { MutateLighting(r => r.Fx.Vignette = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }
    public double LensTangentialX
    {
        get => _p.Lighting.LensTangentialX;
        set { MutateLighting(r => r.Fx.LensTangentialX = Clamp(value, -0.1, 0.1)); this.RaisePropertyChanged(); Fire(); }
    }
    public double LensTangentialY
    {
        get => _p.Lighting.LensTangentialY;
        set { MutateLighting(r => r.Fx.LensTangentialY = Clamp(value, -0.1, 0.1)); this.RaisePropertyChanged(); Fire(); }
    }
    public double AnamorphicSqueeze
    {
        get => _p.Lighting.AnamorphicSqueeze;
        set { MutateLighting(r => r.Fx.AnamorphicSqueeze = Clamp(value, 0.25, 4)); this.RaisePropertyChanged(); Fire(); }
    }

    // ── Reflection / Stereo / DoF / Animation / Edge (Phase 13+) ─────

    public double ReflectionStrength
    {
        get => _p.Lighting.ReflectionStrength;
        set { MutateLighting(r => r.Fx.ReflectionStrength = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }
    public int ReflectionSteps
    {
        get => _p.Lighting.ReflectionSteps;
        set { MutateLighting(r => r.Fx.ReflectionSteps = (int)Clamp(value, 0, 64)); this.RaisePropertyChanged(); Fire(); }
    }
    public double EdgeStrength
    {
        get => _p.Lighting.EdgeStrength;
        set { MutateLighting(r => r.Fx.EdgeStrength = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }
    public uint EdgeColor
    {
        get => _p.Lighting.EdgeColor;
        set { MutateLighting(r => r.Fx.EdgeColor = value); this.RaisePropertyChanged(); Fire(); }
    }
    /// <summary>Hex BGRA accessor for <see cref="EdgeColor"/>. See
    /// <see cref="TriplanarTintHex"/> for the rationale.</summary>
    public string EdgeColorHex
    {
        get => _p.Lighting.EdgeColor.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            var s = (value ?? string.Empty).Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint u)) return;
            if (_p.Lighting.EdgeColor == u) return;
            MutateLighting(r => r.Fx.EdgeColor = u);
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(EdgeColor));
            Fire();
        }
    }
    public double EdgeThreshold
    {
        get => _p.Lighting.EdgeThreshold;
        set { MutateLighting(r => r.Fx.EdgeThreshold = Clamp(value, 0, 2.83)); this.RaisePropertyChanged(); Fire(); }
    }
    public EdgeKernelMode EdgeKernel
    {
        get => _p.Lighting.EdgeKernel;
        set { MutateLighting(r => r.Fx.EdgeKernel = value); this.RaisePropertyChanged(); Fire(); }
    }
    public Array EdgeKernels => Enum.GetValues(typeof(EdgeKernelMode));
    /// <summary>Phase 16b — N-bounce reflection chain depth. 1 = legacy single
    /// bounce; up to 6 for chrome / hall-of-mirrors effects. Higher counts
    /// scale linearly with per-pixel cost in the reflection path.</summary>
    public int MaxBounces
    {
        get => _p.Lighting.MaxBounces;
        set { MutateLighting(r => r.Fx.MaxBounces = (int)Clamp(value, 1, 6)); this.RaisePropertyChanged(); Fire(); }
    }

    /// <summary>Phase 20b — stereo render mode. Off / Fake (depth-parallax warp)
    /// / True (two-pass per-eye render). Engine-side knobs only at present;
    /// host orchestration follow-up wires the mode change into the render
    /// loop.</summary>
    public StereoMode StereoMode
    {
        get => _p.Lighting.StereoMode;
        set { MutateLighting(r => r.Fx.StereoMode = value); this.RaisePropertyChanged(); Fire(); }
    }
    public Array StereoModes => Enum.GetValues(typeof(StereoMode));

    public double StereoEyeSeparation
    {
        get => _p.Lighting.StereoEyeSeparation;
        set { MutateLighting(r => r.Fx.StereoEyeSeparation = Clamp(value, 0, 0.25)); this.RaisePropertyChanged(); Fire(); }
    }
    public double StereoFovDegrees
    {
        get => _p.Lighting.StereoFovDegrees;
        set { MutateLighting(r => r.Fx.StereoFovDegrees = Clamp(value, 20, 120)); this.RaisePropertyChanged(); Fire(); }
    }
    public double StereoConvergence
    {
        get => _p.Lighting.StereoConvergence;
        set { MutateLighting(r => r.Fx.StereoConvergence = Clamp(value, -0.2, 0.2)); this.RaisePropertyChanged(); Fire(); }
    }
    public double StereoMaxDisparity
    {
        get => _p.Lighting.StereoMaxDisparity;
        set { MutateLighting(r => r.Fx.StereoMaxDisparity = Clamp(value, 0, 0.15)); this.RaisePropertyChanged(); Fire(); }
    }
    public StereoLayout StereoLayout
    {
        get => _p.Lighting.StereoLayout;
        set { MutateLighting(r => r.Fx.StereoLayout = value); this.RaisePropertyChanged(); Fire(); }
    }
    public Array StereoLayouts => Enum.GetValues(typeof(StereoLayout));
    public double DofAperture
    {
        get => _p.Lighting.DofAperture;
        set { MutateLighting(r => r.Fx.DofAperture = Clamp(value, 0, 0.5)); this.RaisePropertyChanged(); Fire(); }
    }
    public double DofFocusDistance
    {
        get => _p.Lighting.DofFocusDistance;
        set { MutateLighting(r => r.Fx.DofFocusDistance = Clamp(value, 0.1, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public int DofSamples
    {
        get => _p.Lighting.DofSamples;
        set { MutateLighting(r => r.Fx.DofSamples = (int)Clamp(value, 1, 32)); this.RaisePropertyChanged(); Fire(); }
    }
    public double LightOrbitSpeed
    {
        get => _p.Lighting.LightOrbitSpeed;
        set
        {
            MutateLighting(r => r.Fx.LightOrbitSpeed = Clamp(value, -10, 10));
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsLightOrbitRunning));
            this.RaisePropertyChanged(nameof(LightOrbitToggleLabel));
            Fire();
        }
    }
    public double CausticsAnimSpeed
    {
        get => _p.Lighting.CausticsAnimSpeed;
        set
        {
            MutateLighting(r => r.Fx.CausticsAnimSpeed = Clamp(value, -10, 10));
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsCausticsRunning));
            this.RaisePropertyChanged(nameof(CausticsToggleLabel));
            Fire();
        }
    }

    // ── Cloud noise (Phase 22b) ───────────────────────────────────────
    public double VolumeNoiseScale
    {
        get => _p.Lighting.VolumeNoiseScale;
        set { MutateLighting(r => r.Fx.VolumeNoiseScale = Clamp(value, 0.01, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double VolumeNoiseSpeed
    {
        get => _p.Lighting.VolumeNoiseSpeed;
        set
        {
            MutateLighting(r => r.Fx.VolumeNoiseSpeed = Clamp(value, -10, 10));
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsVolumeNoiseRunning));
            this.RaisePropertyChanged(nameof(VolumeNoiseToggleLabel));
            Fire();
        }
    }
    public int VolumeNoiseOctaves
    {
        get => _p.Lighting.VolumeNoiseOctaves;
        set { MutateLighting(r => r.Fx.VolumeNoiseOctaves = (int)Clamp(value, 1, 6)); this.RaisePropertyChanged(); Fire(); }
    }
    public double VolumeSelfShadow
    {
        get => _p.Lighting.VolumeSelfShadow;
        set { MutateLighting(r => r.Fx.VolumeSelfShadow = Clamp(value, 0, 4)); this.RaisePropertyChanged(); Fire(); }
    }
    public int VolumeSelfShadowSteps
    {
        get => _p.Lighting.VolumeSelfShadowSteps;
        set { MutateLighting(r => r.Fx.VolumeSelfShadowSteps = (int)Clamp(value, 0, 16)); this.RaisePropertyChanged(); Fire(); }
    }
    // Vol-color slice B (#178) — Henyey-Greenstein phase anisotropy. 0 =
    // isotropic (default); >0 forward god-rays, <0 back-scatter halo.
    public double VolumeAnisotropy
    {
        get => _p.Lighting.VolumeAnisotropy;
        set { MutateLighting(r => r.Fx.VolumeAnisotropy = Clamp(value, -1, 1)); this.RaisePropertyChanged(); Fire(); }
    }
    // Vol-color slice C (#179) — medium color / scattering albedo. White =
    // no tint (default). Independent of the light colors.
    public uint FogColor
    {
        get => _p.Lighting.FogColor;
        set { MutateLighting(r => r.Fx.FogColor = value); this.RaisePropertyChanged(); Fire(); }
    }
    /// <summary>Hex BGRA accessor for <see cref="FogColor"/>. Bound to a
    /// TextBox for the same NumericUpDown-chokes-on-uint reason as
    /// <see cref="TriplanarTintHex"/>.</summary>
    public string FogColorHex
    {
        get => _p.Lighting.FogColor.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            var s = (value ?? string.Empty).Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint u)) return;
            if (_p.Lighting.FogColor == u) return;
            MutateLighting(r => r.Fx.FogColor = u);
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(FogColor));
            Fire();
        }
    }
    // Vol-color slice D (#180) — palette-mapped volumetric. 0 = off (default);
    // >0 cross-fades the in-scatter toward the active 3D theme's gradient
    // (keyed by fog optical depth). Non-PBR / stylised.
    public double VolumePaletteStrength
    {
        get => _p.Lighting.VolumePaletteStrength;
        set { MutateLighting(r => r.Fx.VolumePaletteStrength = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }

    // ── Triplanar material (Phase 14b) ────────────────────────────────
    public TriplanarTextureKind TriplanarKind
    {
        get => _p.Lighting.TriplanarKind;
        set { MutateLighting(r => r.Fx.TriplanarKind = value); this.RaisePropertyChanged(); Fire(); }
    }
    public Array TriplanarKinds => Enum.GetValues(typeof(TriplanarTextureKind));
    public double TriplanarScale
    {
        get => _p.Lighting.TriplanarScale;
        set { MutateLighting(r => r.Fx.TriplanarScale = Clamp(value, 0.01, 100)); this.RaisePropertyChanged(); Fire(); }
    }
    public double TriplanarStrength
    {
        get => _p.Lighting.TriplanarStrength;
        set { MutateLighting(r => r.Fx.TriplanarStrength = Clamp(value, 0, 1)); this.RaisePropertyChanged(); Fire(); }
    }
    public uint TriplanarTint
    {
        get => _p.Lighting.TriplanarTint;
        set { MutateLighting(r => r.Fx.TriplanarTint = value); this.RaisePropertyChanged(); Fire(); }
    }
    /// <summary>Hex BGRA accessor for <see cref="TriplanarTint"/>. Bound to
    /// the FractalParamsView TextBox because the Avalonia NumericUpDown
    /// chokes on the X8 format string and uint maxima, producing blank
    /// fields + crashes on arrow / direct entry. Phase 14b hotfix.</summary>
    public string TriplanarTintHex
    {
        get => _p.Lighting.TriplanarTint.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            var s = (value ?? string.Empty).Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint u)) return;
            if (_p.Lighting.TriplanarTint == u) return;
            MutateLighting(r => r.Fx.TriplanarTint = u);
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(TriplanarTint));
            Fire();
        }
    }

    // ── Debug HUD bits (Phase 19b) ────────────────────────────────────
    // DebugHudFlags is a bit mask: 0x1 light-direction compass,
    // 0x2 parameter bars, 0x4 scene-time clock. Expose three independent
    // booleans rather than one int knob so the UI can toggle each overlay
    // without the user having to do bit math.
    public bool DebugHudCompass
    {
        get => (_p.Lighting.DebugHudFlags & 0x1) != 0;
        set
        {
            MutateLighting(r =>
            {
                if (value) r.Fx.DebugHudFlags |= 0x1;
                else       r.Fx.DebugHudFlags &= ~0x1;
            });
            this.RaisePropertyChanged();
            Fire();
        }
    }
    public bool DebugHudBars
    {
        get => (_p.Lighting.DebugHudFlags & 0x2) != 0;
        set
        {
            MutateLighting(r =>
            {
                if (value) r.Fx.DebugHudFlags |= 0x2;
                else       r.Fx.DebugHudFlags &= ~0x2;
            });
            this.RaisePropertyChanged();
            Fire();
        }
    }
    public bool DebugHudClock
    {
        get => (_p.Lighting.DebugHudFlags & 0x4) != 0;
        set
        {
            MutateLighting(r =>
            {
                if (value) r.Fx.DebugHudFlags |= 0x4;
                else       r.Fx.DebugHudFlags &= ~0x4;
            });
            this.RaisePropertyChanged();
            Fire();
        }
    }

    // #311 — expanded artist-diagnostic HUD overlays (each a DebugHudFlags bit).
    private void SetHudBit(int bit, bool on)
    {
        MutateLighting(r =>
        {
            if (on) r.Fx.DebugHudFlags |= bit;
            else    r.Fx.DebugHudFlags &= ~bit;
        });
    }

    /// <summary>#313 — rule-of-thirds + center cross + title-safe frame.</summary>
    public bool DebugHudGuides
    {
        get => (_p.Lighting.DebugHudFlags & 0x10) != 0;
        set { SetHudBit(0x10, value); this.RaisePropertyChanged(); Fire(); }
    }

    /// <summary>#314 — over/under-exposure zebra stripes.</summary>
    public bool DebugHudZebra
    {
        get => (_p.Lighting.DebugHudFlags & 0x20) != 0;
        set { SetHudBit(0x20, value); this.RaisePropertyChanged(); Fire(); }
    }

    /// <summary>#312 — light elevation gauge + god-ray shaft-readiness lamp.</summary>
    public bool DebugHudLightGauge
    {
        get => (_p.Lighting.DebugHudFlags & 0x8) != 0;
        set { SetHudBit(0x8, value); this.RaisePropertyChanged(); Fire(); }
    }

    /// <summary>#315 — lookdev reference balls (18%-grey matte + chrome).</summary>
    public bool DebugHudReferenceBalls
    {
        get => (_p.Lighting.DebugHudFlags & 0x40) != 0;
        set { SetHudBit(0x40, value); this.RaisePropertyChanged(); Fire(); }
    }

    /// <summary>#316 — exposure false-colour (full-frame zone recolour).</summary>
    public bool DebugHudFalseColor
    {
        get => (_p.Lighting.DebugHudFlags & 0x80) != 0;
        set { SetHudBit(0x80, value); this.RaisePropertyChanged(); Fire(); }
    }

    /// <summary>#316 — luma histogram panel.</summary>
    public bool DebugHudHistogram
    {
        get => (_p.Lighting.DebugHudFlags & 0x100) != 0;
        set { SetHudBit(0x100, value); this.RaisePropertyChanged(); Fire(); }
    }

    /// <summary>#318 — numeric telemetry panel (bitmap font): resolution, active
    /// lights, fog optical depth, supersample, AOV view, frame-time/FPS. Baked
    /// into the frame (survives PNG / video export).</summary>
    public bool DebugHudTelemetry
    {
        get => (_p.Lighting.DebugHudFlags & 0x200) != 0;
        set { SetHudBit(0x200, value); this.RaisePropertyChanged(); Fire(); }
    }

    /// <summary>#317 — AOV / render-buffer view mode. Beauty = normal output;
    /// other values isolate a diagnostic buffer (normals / depth / step heat /
    /// AO / diffuse / specular / shadow). CPU raymarchers + CPU relief.</summary>
    public AovView DebugAov
    {
        get => _p.Lighting.DebugAov;
        set { MutateLighting(r => r.Fx.DebugAov = value); this.RaisePropertyChanged(); Fire(); }
    }
    public Array AovViews => Enum.GetValues(typeof(AovView));

    // ── Speed-driven effect Start/Stop toggles ──────────────────────────
    //
    // Each speed-driven effect (light orbit, caustics phase, cloud-noise
    // drift) pairs a NumericUpDown with a Start/Stop button. Stop stashes
    // the live speed and zeroes it; Start restores the stash (or a sane
    // default if stash is also 0). Manual edits still work — the setters
    // for the speed properties raise IsXxxRunning + XxxToggleLabel so the
    // button text tracks the live value.
    //
    // Stashes default to a visible-but-calm rate so a fresh Start always
    // produces motion even if the user never typed a number first.

    private double _lightOrbitSpeedStash    = 0.5;
    private double _causticsAnimSpeedStash  = 0.5;
    private double _volumeNoiseSpeedStash   = 0.5;

    public bool IsLightOrbitRunning   => LightOrbitSpeed   != 0.0;
    public bool IsCausticsRunning     => CausticsAnimSpeed != 0.0;
    public bool IsVolumeNoiseRunning  => VolumeNoiseSpeed  != 0.0;

    public string LightOrbitToggleLabel  => IsLightOrbitRunning  ? "Stop" : "Start";
    public string CausticsToggleLabel    => IsCausticsRunning    ? "Stop" : "Start";
    public string VolumeNoiseToggleLabel => IsVolumeNoiseRunning ? "Stop" : "Start";

    private ReactiveCommand<Unit, Unit>? _toggleLightOrbitCmd;
    public ReactiveCommand<Unit, Unit> ToggleLightOrbitCommand =>
        _toggleLightOrbitCmd ??= ReactiveCommand.Create(() =>
        {
            if (IsLightOrbitRunning)
            {
                _lightOrbitSpeedStash = LightOrbitSpeed;
                LightOrbitSpeed = 0.0;
            }
            else
            {
                LightOrbitSpeed = _lightOrbitSpeedStash != 0.0 ? _lightOrbitSpeedStash : 0.5;
            }
        });

    private ReactiveCommand<Unit, Unit>? _toggleCausticsCmd;
    public ReactiveCommand<Unit, Unit> ToggleCausticsCommand =>
        _toggleCausticsCmd ??= ReactiveCommand.Create(() =>
        {
            if (IsCausticsRunning)
            {
                _causticsAnimSpeedStash = CausticsAnimSpeed;
                CausticsAnimSpeed = 0.0;
            }
            else
            {
                CausticsAnimSpeed = _causticsAnimSpeedStash != 0.0 ? _causticsAnimSpeedStash : 0.5;
            }
        });

    private ReactiveCommand<Unit, Unit>? _toggleVolumeNoiseCmd;
    public ReactiveCommand<Unit, Unit> ToggleVolumeNoiseCommand =>
        _toggleVolumeNoiseCmd ??= ReactiveCommand.Create(() =>
        {
            if (IsVolumeNoiseRunning)
            {
                _volumeNoiseSpeedStash = VolumeNoiseSpeed;
                VolumeNoiseSpeed = 0.0;
            }
            else
            {
                VolumeNoiseSpeed = _volumeNoiseSpeedStash != 0.0 ? _volumeNoiseSpeedStash : 0.5;
            }
        });
}
