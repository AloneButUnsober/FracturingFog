// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FractalParamsViewModel.Reset.cs
//
// #305 — "Defaults" buttons for the Volumetric FX (Lighting & FX) and Relief 3D
// dialogs. Both dialogs persist edits within a session but had no clean way back
// to sane defaults. Each button is a two-click arm/confirm: the first click arms
// (label flips to "Confirm reset?" and the button turns yellow — #FFCC00, the
// colorblind-safe accent), the second click applies. A short timer auto-disarms
// so a stray first click never leaves the button primed. Reset is destructive of
// in-session tuning, hence the confirm step.
//
// Lighting reset  -> LightingFxData.CreateDefault() (the whole shading struct).
// Relief 3D reset -> the Relief2D* field defaults on a fresh FractalParameters,
//                    but PRESERVING the two mode toggles (Relief2DEnabled /
//                    Relief2DRaymarch) so "Defaults" undoes tuning without
//                    silently switching the feature off under the user.
//
// Both mutate _p in place then broadcast an all-property change (empty name) so
// every bound knob re-reads, and Fire() once so the host re-renders a single time.

using System;
using System.Reactive;
using global::Avalonia.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class FractalParamsViewModel
{
    private const double ResetArmSeconds = 4.0;

    // ── Lighting & FX defaults ────────────────────────────────────────

    private bool _lightingResetArmed;
    private DispatcherTimer? _lightingResetDisarm;
    private ReactiveCommand<Unit, Unit>? _resetLightingCmd;

    /// <summary>True while the Lighting Defaults button is primed (second click
    /// applies). Drives the button's <c>armed</c> class for the yellow accent.</summary>
    public bool LightingResetArmed
    {
        get => _lightingResetArmed;
        private set
        {
            this.RaiseAndSetIfChanged(ref _lightingResetArmed, value);
            this.RaisePropertyChanged(nameof(LightingResetLabel));
        }
    }
    public string LightingResetLabel => _lightingResetArmed ? "Confirm reset?" : "Defaults";

    public ReactiveCommand<Unit, Unit> ResetLightingCommand =>
        _resetLightingCmd ??= ReactiveCommand.Create(() =>
        {
            if (_lightingResetArmed)
            {
                DisarmLightingReset();
                ResetLightingToDefaults();
            }
            else ArmLightingReset();
        });

    private void ArmLightingReset()
    {
        LightingResetArmed = true;
        _lightingResetDisarm ??= NewDisarmTimer(DisarmLightingReset);
        _lightingResetDisarm.Stop();
        _lightingResetDisarm.Start();
    }

    private void DisarmLightingReset()
    {
        _lightingResetDisarm?.Stop();
        LightingResetArmed = false;
    }

    private void ResetLightingToDefaults()
    {
        _p.Lighting = LightingFxData.CreateDefault();
        // Applying defaults invalidates the "which preset am I on" hint. Safe to
        // raise the droplist here (driven by the button, not the ComboBox).
        _selectedVolumetricPreset = VolumetricFxPresets.NoneName;
        this.RaisePropertyChanged(nameof(SelectedVolumetricPreset));
        RaiseLightingKnobsChanged();
        Fire();
    }

    // ── Relief 3D defaults ────────────────────────────────────────────

    private bool _relief3DResetArmed;
    private DispatcherTimer? _relief3DResetDisarm;
    private ReactiveCommand<Unit, Unit>? _resetRelief3DCmd;

    public bool Relief3DResetArmed
    {
        get => _relief3DResetArmed;
        private set
        {
            this.RaiseAndSetIfChanged(ref _relief3DResetArmed, value);
            this.RaisePropertyChanged(nameof(Relief3DResetLabel));
        }
    }
    public string Relief3DResetLabel => _relief3DResetArmed ? "Confirm reset?" : "Defaults";

    public ReactiveCommand<Unit, Unit> ResetRelief3DCommand =>
        _resetRelief3DCmd ??= ReactiveCommand.Create(() =>
        {
            if (_relief3DResetArmed)
            {
                DisarmRelief3DReset();
                ResetRelief3DToDefaults();
            }
            else ArmRelief3DReset();
        });

    private void ArmRelief3DReset()
    {
        Relief3DResetArmed = true;
        _relief3DResetDisarm ??= NewDisarmTimer(DisarmRelief3DReset);
        _relief3DResetDisarm.Stop();
        _relief3DResetDisarm.Start();
    }

    private void DisarmRelief3DReset()
    {
        _relief3DResetDisarm?.Stop();
        Relief3DResetArmed = false;
    }

    private void ResetRelief3DToDefaults()
    {
        var d = new FractalParameters();
        // Preserve the mode toggles — "Defaults" resets the look, it does not
        // switch Relief 3D / raymarch off under the user.
        _p.Relief2DHeightScale        = d.Relief2DHeightScale;
        _p.Relief2DLightAzimuthDeg    = d.Relief2DLightAzimuthDeg;
        _p.Relief2DLightElevationDeg  = d.Relief2DLightElevationDeg;
        _p.Relief2DShadowStrength     = d.Relief2DShadowStrength;
        _p.Relief2DStrength           = d.Relief2DStrength;
        _p.Relief2DAbsolute           = d.Relief2DAbsolute;
        _p.Relief2DCameraAzimuthDeg   = d.Relief2DCameraAzimuthDeg;
        _p.Relief2DCameraElevationDeg = d.Relief2DCameraElevationDeg;
        _p.Relief2DCameraFovDeg       = d.Relief2DCameraFovDeg;
        _p.Relief2DCameraZoom         = d.Relief2DCameraZoom;
        _p.Relief2DCameraOrthographic = d.Relief2DCameraOrthographic;
        _p.Relief2DDofApertureRadius  = d.Relief2DDofApertureRadius;
        _p.Relief2DDofFocusDistance   = d.Relief2DDofFocusDistance;
        _p.Relief2DDenoiseIterations  = d.Relief2DDenoiseIterations;   // S4 (#389)
        _p.Relief2DDenoiseColorSigma  = d.Relief2DDenoiseColorSigma;
        _p.Relief2DDenoiseNormalSigma = d.Relief2DDenoiseNormalSigma;
        _p.Relief2DDenoiseDepthSigma  = d.Relief2DDenoiseDepthSigma;
        _p.Relief2DSupersample        = d.Relief2DSupersample;
        _p.Relief2DHeightCurve        = d.Relief2DHeightCurve;
        _p.Relief2DDetailGain         = d.Relief2DDetailGain;          // #518
        _p.Relief2DDetailRadius       = d.Relief2DDetailRadius;        // #518
        _p.Relief2DHeightGamma        = d.Relief2DHeightGamma;         // #518
        _p.Relief2DBicubicHeight      = d.Relief2DBicubicHeight;
        _p.Relief2DGroundPlane        = d.Relief2DGroundPlane;
        _p.Relief2DFroxelVolumetrics  = d.Relief2DFroxelVolumetrics;   // S6 (#408)
        _p.Relief2DFroxelTemporal     = d.Relief2DFroxelTemporal;      // S6 (#408)
        _p.Relief2DFroxelTemporalFeedback = d.Relief2DFroxelTemporalFeedback;
        _p.Relief2DFroxelQuality      = d.Relief2DFroxelQuality;         // S6 (#408)
        _p.Relief2DAutoShade          = d.Relief2DAutoShade;
        _p.Relief2DEdgeFade           = d.Relief2DEdgeFade;
        _p.Relief2DHiResField         = d.Relief2DHiResField;
        _p.Relief2DFieldFloor         = d.Relief2DFieldFloor;
        _p.Relief2DFarDetail          = d.Relief2DFarDetail;          // #520
        _p.Relief2DSettleDetail       = d.Relief2DSettleDetail;       // #520 (part 3)
        _p.Relief2DIsolate            = d.Relief2DIsolate;
        _p.Relief2DIsolateByDetail    = d.Relief2DIsolateByDetail;
        _p.Relief2DDetailThreshold    = d.Relief2DDetailThreshold;
        _p.Relief2DIsolateByColor     = d.Relief2DIsolateByColor;
        _p.Relief2DDropColorsCsv      = d.Relief2DDropColorsCsv;
        _p.Relief2DColorTolerance     = d.Relief2DColorTolerance;
        _p.Relief2DMeshHeight         = d.Relief2DMeshHeight;
        _p.Relief2DMeshSmoothing      = d.Relief2DMeshSmoothing;
        _p.Relief2DMeshGrid           = d.Relief2DMeshGrid;
        _p.Relief2DMeshMaxMB          = d.Relief2DMeshMaxMB;
        _p.Relief2DMeshUnderside      = d.Relief2DMeshUnderside;
        RaiseReliefKnobsChanged();
        Fire();
    }

    // ── shared ────────────────────────────────────────────────────────

    private static DispatcherTimer NewDisarmTimer(Action disarm)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ResetArmSeconds) };
        t.Tick += (_, _) => disarm();
        return t;
    }

    private void RaiseChanged(string[] names)
    {
        foreach (var n in names) this.RaisePropertyChanged(n);
    }

    // Every Lighting & FX knob the dialogs bind (Lighting.cs). Explicit names,
    // not an empty-string broadcast: Avalonia compiled bindings (x:DataType) do
    // not reliably re-read on a blanket "" notification, and re-raising the
    // preset ComboBox's own property mid-selection reverts its visual selection.
    // Deliberately EXCLUDES SelectedVolumetricPreset for that reason.
    private static readonly string[] LightingKnobNames =
    {
        nameof(Light1Theta), nameof(Light1Phi), nameof(Light1Intensity), nameof(Light1Color),
        nameof(Light2Theta), nameof(Light2Phi), nameof(Light2Intensity), nameof(Light2Color),
        nameof(Light3Theta), nameof(Light3Phi), nameof(Light3Intensity), nameof(Light3Color),
        nameof(AmbientStrength), nameof(AoSamples), nameof(AoStrength),
        nameof(SsaoSamples), nameof(SsaoRadius), nameof(SsaoStrength),
        nameof(ShadowSteps), nameof(ShadowSoftK),
        nameof(FogDensity), nameof(FogHeightFalloff), nameof(VolumeSteps),
        nameof(VolumeNoiseAmount), nameof(VolumeNoiseScale), nameof(VolumeNoiseSpeed),
        nameof(VolumeNoiseOctaves), nameof(VolumeSelfShadow), nameof(VolumeSelfShadowSteps),
        nameof(VolumeAnisotropy), nameof(FogColor), nameof(FogColorHex), nameof(VolumePaletteStrength),
        nameof(Roughness), nameof(Metallic), nameof(SpecularStrength), nameof(SubSurfaceStrength),
        nameof(SkyMode), nameof(BgTopColor), nameof(BgBottomColor), nameof(EnvironmentName),
        nameof(IblStrength), nameof(ShowSkyBackdrop),
        nameof(ToneMap), nameof(Exposure), nameof(BloomThreshold), nameof(BloomStrength),
        nameof(ChromaticAberration), nameof(LensDistortion), nameof(Vignette),
        nameof(LensTangentialX), nameof(LensTangentialY), nameof(AnamorphicSqueeze),
        nameof(ReflectionStrength), nameof(ReflectionSteps), nameof(EdgeStrength),
        nameof(EdgeColor), nameof(EdgeColorHex), nameof(EdgeThreshold), nameof(EdgeKernel),
        nameof(MaxBounces), nameof(StereoMode), nameof(StereoEyeSeparation),
        nameof(StereoFovDegrees), nameof(StereoConvergence), nameof(StereoMaxDisparity),
        nameof(StereoLayout), nameof(DofAperture), nameof(DofFocusDistance), nameof(DofSamples),
        nameof(LightOrbitSpeed), nameof(CausticsAnimSpeed),
        nameof(TriplanarKind), nameof(TriplanarScale), nameof(TriplanarStrength),
        nameof(TriplanarTint), nameof(TriplanarTintHex),
        nameof(DebugHudCompass), nameof(DebugHudBars), nameof(DebugHudClock),
        // derived Start/Stop labels
        nameof(IsLightOrbitRunning), nameof(LightOrbitToggleLabel),
        nameof(IsCausticsRunning), nameof(CausticsToggleLabel),
        nameof(IsVolumeNoiseRunning), nameof(VolumeNoiseToggleLabel),
    };

    /// <summary>Raise a change for every Lighting &amp; FX knob so the dialog
    /// sliders/readouts re-read after an in-place mutation of _p.Lighting.</summary>
    private void RaiseLightingKnobsChanged() => RaiseChanged(LightingKnobNames);

    // Every Relief 3D knob the dialog binds (main VM partial).
    private static readonly string[] ReliefKnobNames =
    {
        nameof(Relief2DHeightScale), nameof(Relief2DLightAzimuthDeg), nameof(Relief2DLightElevationDeg),
        nameof(Relief2DShadowStrength), nameof(Relief2DStrength), nameof(Relief2DAbsolute),
        nameof(Relief2DCameraAzimuthDeg), nameof(Relief2DCameraElevationDeg), nameof(Relief2DCameraFovDeg),
        nameof(Relief2DCameraZoom), nameof(Relief2DCameraOrthographic), nameof(Relief2DSupersample),
        nameof(Relief2DDofApertureRadius), nameof(Relief2DDofFocusDistance), nameof(DofEnabled),
        nameof(Relief2DDenoiseIterations), nameof(Relief2DDenoiseColorSigma),
        nameof(Relief2DDenoiseNormalSigma), nameof(Relief2DDenoiseDepthSigma), nameof(DenoiseEnabled),
        nameof(Relief2DHeightCurve), nameof(Relief2DDetailGain), nameof(Relief2DDetailRadius),
        nameof(Relief2DHeightGamma), nameof(Relief2DBicubicHeight), nameof(Relief2DGroundPlane),
        nameof(Relief2DFroxelVolumetrics),
        nameof(Relief2DFroxelTemporal), nameof(Relief2DFroxelTemporalFeedback),
        nameof(Relief2DFroxelQuality),
        nameof(Relief2DAutoShade), nameof(Relief2DEdgeFade), nameof(Relief2DHiResField),
        nameof(Relief2DFieldFloor), nameof(Relief2DFarDetail), nameof(Relief2DSettleDetail),
        nameof(Relief2DIsolate), nameof(Relief2DIsolateByDetail),
        nameof(Relief2DDetailThreshold), nameof(Relief2DIsolateByColor), nameof(Relief2DDropColorsCsv),
        nameof(Relief2DColorTolerance), nameof(Relief2DMeshHeight), nameof(Relief2DMeshSmoothing),
        nameof(Relief2DMeshGrid), nameof(Relief2DMeshMaxMB), nameof(Relief2DMeshUnderside),
        nameof(Relief2DMeshSizeEstimate),
    };

    private void RaiseReliefKnobsChanged() => RaiseChanged(ReliefKnobNames);
}
