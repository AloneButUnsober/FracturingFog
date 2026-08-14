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
        // Applying defaults invalidates the "which preset am I on" hint.
        _selectedVolumetricPreset = VolumetricFxPresets.NoneName;
        this.RaisePropertyChanged(string.Empty);   // refresh every lighting knob
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
        _p.Relief2DCameraAzimuthDeg   = d.Relief2DCameraAzimuthDeg;
        _p.Relief2DCameraElevationDeg = d.Relief2DCameraElevationDeg;
        _p.Relief2DCameraFovDeg       = d.Relief2DCameraFovDeg;
        _p.Relief2DCameraZoom         = d.Relief2DCameraZoom;
        _p.Relief2DCameraOrthographic = d.Relief2DCameraOrthographic;
        _p.Relief2DSupersample        = d.Relief2DSupersample;
        _p.Relief2DHeightCurve        = d.Relief2DHeightCurve;
        _p.Relief2DBicubicHeight      = d.Relief2DBicubicHeight;
        _p.Relief2DGroundPlane        = d.Relief2DGroundPlane;
        _p.Relief2DAutoShade          = d.Relief2DAutoShade;
        _p.Relief2DEdgeFade           = d.Relief2DEdgeFade;
        _p.Relief2DHiResField         = d.Relief2DHiResField;
        _p.Relief2DFieldFloor         = d.Relief2DFieldFloor;
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
        this.RaisePropertyChanged(string.Empty);   // refresh every relief knob
        Fire();
    }

    // ── shared ────────────────────────────────────────────────────────

    private static DispatcherTimer NewDisarmTimer(Action disarm)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ResetArmSeconds) };
        t.Tick += (_, _) => disarm();
        return t;
    }
}
