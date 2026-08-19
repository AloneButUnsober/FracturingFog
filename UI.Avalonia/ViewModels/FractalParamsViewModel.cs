// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reactive;
using System.Runtime.CompilerServices;
using global::Avalonia.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels.Animation;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// Avalonia port of the legacy WinForms <c>FractalParamsDialog</c>.
/// Wraps an existing <see cref="FractalParameters"/> instance + the active
/// <see cref="FractalType"/> and exposes per-type observable properties + a
/// set of section-visibility flags so a single .axaml can render the right
/// sub-set of controls without a code-behind switch.
/// </summary>
public sealed partial class FractalParamsViewModel : ViewModelBase
{
    private readonly FractalParameters _p;
    private readonly Func<string, (double a, double b, double c, double d)>? _attractorDefaults;
    private bool _suppress;

    public FractalParamsViewModel(
        FractalType type,
        FractalParameters parameters,
        IReadOnlyList<string>? ifsPresets = null,
        IReadOnlyList<string>? lsystemPresets = null,
        IReadOnlyList<string>? attractorPresets = null,
        Func<string, (double a, double b, double c, double d)>? attractorDefaults = null,
        IReadOnlyList<string>? flamePresets = null,
        AudioModulationManager? audioModulation = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        FractalType = type;
        _p = parameters;
        _attractorDefaults = attractorDefaults;

        IfsPresets = ifsPresets ?? Array.Empty<string>();
        LSystemPresets = lsystemPresets ?? Array.Empty<string>();
        AttractorPresets = attractorPresets ?? new[] { "Clifford", "De Jong", "Hopalong", "Lorenz" };
        FlamePresets = flamePresets ?? Array.Empty<string>();

        _juliaR = _p.JuliaC.Real;
        _juliaI = _p.JuliaC.Imaginary;
        _multibrotD = _p.MultibrotExponent;
        _phoenixR = _p.PhoenixP.Real;
        _phoenixI = _p.PhoenixP.Imaginary;
        _glynnR = _p.GlynnC.Real;
        _glynnI = _p.GlynnC.Imaginary;
        _logisticBurnIn = _p.LogisticBurnIn;
        _logisticSeed = _p.LogisticSeed;
        _secantOffsetR = _p.SecantInitialOffset.Real;
        _secantOffsetI = _p.SecantInitialOffset.Imaginary;
        _spiderCDecay = _p.SpiderCDecay;
        _newtonExponent = _p.NewtonExponent;
        _newtonRelaxation = _p.NewtonRelaxation;
        _ifsPresetName = _p.IFSPresetName;
        _ifsIterations = _p.IFSIterations;
        _lsystemPresetName = _p.LSystemPresetName;
        _lsystemDepth = _p.LSystemDepth;
        _attractorPresetName = _p.AttractorPresetName;
        _attractorIterations = _p.AttractorIterations;
        _attractorA = _p.AttractorA;
        _attractorB = _p.AttractorB;
        _attractorC = _p.AttractorC;
        _attractorD = _p.AttractorD;
        _buddhaSamples = _p.BuddhaSamples;
        _buddhaIterLow = _p.BuddhaIterLow;
        _buddhaIterMid = _p.BuddhaIterMid;
        _buddhaIterHigh = _p.BuddhaIterHigh;
        _buddhaColorMode = _p.BuddhaColorMode;
        _buddhaQualityMode = _p.BuddhaQualityMode;
        _buddhaMetropolis = _p.BuddhaMetropolis;
        _buddhaProgressive = _p.BuddhaProgressive;
        _buddhaSeed = _p.BuddhaSeed;
        _bulbPower = _p.BulbPower;
        _bulbIterations = _p.BulbIterations;
        _bulbCameraTheta = _p.BulbCameraTheta;
        _bulbCameraPhi = _p.BulbCameraPhi;
        _bulbCameraDistance = _p.BulbCameraDistance;
        _mandelboxScale = _p.MandelboxScale;
        _mandelboxFixedRadius = _p.MandelboxFixedRadius;
        _mandelboxMinRadius = _p.MandelboxMinRadius;
        _mandelboxIterations = _p.MandelboxIterations;
        _mandelboxCameraTheta = _p.MandelboxCameraTheta;
        _mandelboxCameraPhi = _p.MandelboxCameraPhi;
        _mandelboxCameraDistance = _p.MandelboxCameraDistance;
        _kifsFold = _p.KifsFold;
        _kifsIterations = _p.KifsIterations;
        _kifsScale = _p.KifsScale;
        _kifsOffsetX = _p.KifsOffsetX;
        _kifsOffsetY = _p.KifsOffsetY;
        _kifsOffsetZ = _p.KifsOffsetZ;
        _kifsCameraTheta = _p.KifsCameraTheta;
        _kifsCameraPhi = _p.KifsCameraPhi;
        _kifsCameraDistance = _p.KifsCameraDistance;
        _qjCX = _p.QJuliaCX;
        _qjCY = _p.QJuliaCY;
        _qjCZ = _p.QJuliaCZ;
        _qjCW = _p.QJuliaCW;
        _qjSliceW = _p.QJuliaSliceW;
        _qjIterations = _p.QJuliaIterations;
        _qjCameraTheta = _p.QJuliaCameraTheta;
        _qjCameraPhi = _p.QJuliaCameraPhi;
        _qjCameraDistance = _p.QJuliaCameraDistance;
        _qmSliceW = _p.QMandelSliceW;
        _qmIterations = _p.QMandelIterations;
        _qmCameraTheta = _p.QMandelCameraTheta;
        _qmCameraPhi = _p.QMandelCameraPhi;
        _qmCameraDistance = _p.QMandelCameraDistance;
        _plasmaRoughness = _p.PlasmaRoughness;
        _plasmaSeed = _p.PlasmaSeed;
        _acidWarpPattern = _p.AcidWarpPattern;
        _acidWarpFrequency = _p.AcidWarpFrequency;
        _acidWarpCenterX = _p.AcidWarpCenterX;
        _acidWarpCenterY = _p.AcidWarpCenterY;
        _acidWarpSeed = _p.AcidWarpSeed;
        _acidWarpWarpStrength = _p.AcidWarpWarpStrength;
        _acidWarpMorph = _p.AcidWarpMorph;
        _acidWarpFlow = _p.AcidWarpFlow;
        _domainWarpEnabled = _p.DomainWarpEnabled;
        _domainWarpStrength = _p.DomainWarpStrength;
        _domainWarpFrequency = _p.DomainWarpFrequency;
        _apolloDepth = _p.ApollonianDepth;
        _apolloMinPx = _p.ApollonianMinPixelRadius;
        _apolloColorByDepth = _p.ApollonianColorByDepth;
        _apolloRelief = _p.ApollonianRelief;
        _rtCount = _p.RandomTileCount;
        _rtSizeExponent = _p.RandomTileSizeExponent;
        _rtSeed = _p.RandomTileSeed;
        _rtGap = _p.RandomTileGap;
        _rtMinPx = _p.RandomTileMinPixelRadius;
        _rtColorByIndex = _p.RandomTileColorByIndex;
        _rtRelief = _p.RandomTileRelief;
        _rtShape = _p.RandomTileShape;
        _kleinIter = _p.KleinianIterations;
        _kleinScale = _p.KleinianSphereScale;
        _kleinCameraTheta = _p.KleinianCameraTheta;
        _kleinCameraPhi = _p.KleinianCameraPhi;
        _kleinCameraDistance = _p.KleinianCameraDistance;
        _dlaParticles = _p.DlaParticles;
        _dlaSeed = _p.DlaSeed;
        _bcSliceW = _p.BicomplexSliceW;
        _bcSliceAxis = _p.BicomplexSliceAxis;
        _bcIterations = _p.BicomplexIterations;
        _bcCameraTheta = _p.BicomplexCameraTheta;
        _bcCameraPhi = _p.BicomplexCameraPhi;
        _bcCameraDistance = _p.BicomplexCameraDistance;
        _flamePresetName = _p.FlamePresetName;
        _flameIterations = _p.FlameIterations;
        _flameGamma = _p.FlameGamma;
        _flameVibrancy = _p.FlameVibrancy;

        // #263 P4c — inline audio-reactive affordance. One row per animatable
        // scalar of this fractal type (Complex kinds excluded — out of P4 scope),
        // each bound to the shared app-scoped AudioModulationManager so a toggle
        // here and the central Audio Settings matrix edit the same binding.
        if (audioModulation != null)
        {
            var rows = new List<AudioBindingRowViewModel>();
            foreach (var d in FractalAnimatableParamsMap.For(type))
            {
                if (d.Kind == AnimatableParamKind.Complex) continue;
                rows.Add(new AudioBindingRowViewModel(d, audioModulation));
            }
            AudioDrivableParams = rows;
        }

        CloseCommand = ReactiveCommand.Create(() =>
        {
            StopJuliaAnimate();
            StopLSystemSweep();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });
        ToggleJuliaAnimateCommand   = ReactiveCommand.Create(ToggleJuliaAnimate);
        ToggleLSystemSweepCommand   = ReactiveCommand.Create(ToggleLSystemSweep);
        ExportMeshCommand           = ReactiveCommand.Create(() => ExportMeshRequested?.Invoke());
        ExportReliefMeshCommand     = ReactiveCommand.Create(() => ExportReliefMeshRequested?.Invoke());
        PickDropColorCommand        = ReactiveCommand.CreateFromTask(PickDropColorAsync);
    }

    /// <summary>#135 — raised when the user asks to eyedrop a drop-colour. The
    /// host (AvaloniaShellBootstrap) drives the platform colour sampler and fills
    /// the args, mirroring the colour-theme editor's eyedropper.</summary>
    public event EventHandler<ThemeSampleColorEventArgs>? SampleColorRequested;

    /// <summary>Eyedrop a screen colour and append it to the isolate drop-colour
    /// list, enabling the colour cull on the first pick.</summary>
    private async System.Threading.Tasks.Task PickDropColorAsync()
    {
        var args = new ThemeSampleColorEventArgs();
        var handler = SampleColorRequested;
        handler?.Invoke(this, args);
        if (handler == null) { args.Completion.TrySetResult(true); return; }
        await args.Completion.Task;
        if (args.PickedR == null) return;
        string hex = $"FF{args.PickedR.Value:X2}{args.PickedG!.Value:X2}{args.PickedB!.Value:X2}";
        string cur = _p.Relief2DDropColorsCsv ?? "";
        Relief2DDropColorsCsv = string.IsNullOrWhiteSpace(cur) ? hex : cur.TrimEnd() + ", " + hex;
        Relief2DIsolateByColor = true;
    }

    /// <summary>Stop any running parameter animation. Host calls this when the
    /// dialog is closed via the window chrome (not the Close button) so the
    /// dispatcher timer doesn't leak past dialog lifetime.</summary>
    public void StopAnimations()
    {
        StopJuliaAnimate();
        StopLSystemSweep();
    }

    public FractalType FractalType { get; }

    /// <summary>#263 P4c — audio-drivable scalar params for this fractal type
    /// (inline modulation rows). Empty when no manager was supplied or the type
    /// has no animatable scalars.</summary>
    public IReadOnlyList<AudioBindingRowViewModel> AudioDrivableParams { get; }
        = Array.Empty<AudioBindingRowViewModel>();
    public bool HasAudioReactiveParams => AudioDrivableParams.Count > 0;

    public IReadOnlyList<string> IfsPresets { get; }
    public IReadOnlyList<string> LSystemPresets { get; }
    public IReadOnlyList<string> AttractorPresets { get; }
    public IReadOnlyList<string> FlamePresets { get; }

    public string Title => $"{FractalType} Parameters";
    public string EmptyStateText => $"{FractalType} has no tunable parameters.";

    public bool IsMandelbrot => FractalType == FractalType.Mandelbrot;
    public bool IsJulia => FractalType == FractalType.Julia;
    public bool IsMultibrot => FractalType == FractalType.Multibrot;
    public bool IsPhoenix => FractalType == FractalType.Phoenix;
    public bool IsGlynn => FractalType == FractalType.Glynn;
    public bool IsLogistic => FractalType == FractalType.Logistic;
    public bool IsNewtonOrNova => FractalType is FractalType.Newton or FractalType.Nova or FractalType.Halley or FractalType.Secant;
    public bool IsSecant => FractalType == FractalType.Secant;
    public bool IsSpider => FractalType == FractalType.Spider;
    public bool IsIFS => FractalType == FractalType.IFS;
    public bool IsLSystem => FractalType == FractalType.LSystem;
    public bool IsStrangeAttractor => FractalType == FractalType.StrangeAttractor;
    public bool IsBuddhaBrot => FractalType is FractalType.BuddhaBrot
        or FractalType.Nebulabrot
        or FractalType.AntiBuddhabrot
        or FractalType.AntiNebulabrot;
    public bool IsMandelbulb => FractalType == FractalType.Mandelbulb;
    public bool IsMandelbox => FractalType == FractalType.Mandelbox;
    public bool IsKifs => FractalType == FractalType.Kifs;
    public bool IsQuatJulia => FractalType == FractalType.QuaternionJulia;
    public bool IsQuatMandelbrot => FractalType == FractalType.QuaternionMandelbrot;
    public bool IsPlasma => FractalType == FractalType.Plasma;
    public bool IsAcidWarp => FractalType == FractalType.AcidWarp;
    public bool IsFlame => FractalType == FractalType.Flame;
    public bool IsApollonian => FractalType == FractalType.Apollonian;
    public bool IsKleinian => FractalType == FractalType.Kleinian;
    public bool IsBicomplexMandelbrot => FractalType == FractalType.BicomplexMandelbrot;
    public bool IsDla => FractalType == FractalType.Dla;
    public bool IsRandomTile => FractalType == FractalType.RandomTile;
    public bool IsUserEquation => FractalType == FractalType.UserEquation;
    public bool IsSandbox => FractalType == FractalType.Sandbox;

    /// <summary>
    /// Visibility flag for the shared LightingFx section. True for every
    /// 3D raymarched fractal — those are the ones whose pixels flow through
    /// <c>ShadingPipeline</c>. 2D fractals get their lighting via theme
    /// objects (Phase 8) so this section stays hidden for them.
    /// </summary>
    public bool IsAny3DRaymarcher =>
        IsMandelbulb || IsMandelbox || IsKifs
        || IsQuatJulia || IsQuatMandelbrot
        || IsBicomplexMandelbrot || IsKleinian
        || FractalType == FractalType.UserBulb;

    /// <summary>Show the "Open Lighting &amp; FX" launcher when the full shading
    /// stack applies: any 3D raymarcher, OR the Oblique 3D heightfield raymarch
    /// on a 2D fractal (#133) — the latter routes hits through the same
    /// <c>ShadingPipeline</c>, so soft shadow / AO / PBR / IBL / volumetric all
    /// reach it. Notified from the Relief2D toggles below.</summary>
    public bool ShowLightingFxLauncher =>
        IsAny3DRaymarcher || (IsRelief2DApplicable && Relief2DEnabled && Relief2DRaymarch);

    /// <summary>Visibility flag for the 2D interior-alpha section (issue #96,
    /// #382). True for the canonical Mandelbrot path plus the DSL escape-time
    /// families (UserEquation, Sandbox), which share the <c>iter &gt;= maxIt</c>
    /// in-set invariant and now scale their in-set alpha by the global knob and
    /// composite over <c>Interior2DBackground</c>.</summary>
    public bool IsInteriorAlphaApplicable => IsMandelbrot || IsUserEquation || IsSandbox;

    /// <summary>Visibility flag for the 2D heightfield-relief section (#102, #139).
    /// True for every 2D family that exposes an <c>IHeightFieldSource</c> the
    /// render host can feed to <c>HeightfieldRelief2D</c> / the Oblique 3D
    /// raymarch: the escape-time kin (smooth iteration count), the root-finding
    /// families (iteration-to-convergence height, #139), the Buddhabrot family
    /// (orbit-density height, #139), and Apollonian (synthesised sphere-cap
    /// height, #139). 3D raymarchers are excluded (already true 3D).</summary>
    public bool IsRelief2DApplicable =>
        IsMandelbrot || IsJulia || IsMultibrot || IsPhoenix
        || IsGlynn || IsSpider
        || FractalType == FractalType.BurningShip
        || FractalType == FractalType.Tricorn
        || FractalType == FractalType.Magnet1
        || FractalType == FractalType.Magnet2
        || FractalType == FractalType.GeneratedTricorn
        || FractalType == FractalType.GeneratedBurningShip
        || FractalType == FractalType.GeneratedMandelbrotZ2
        || FractalType == FractalType.GeneratedMandelbrotZ3
        || FractalType == FractalType.GeneratedMandelbrotZ4
        || FractalType == FractalType.GeneratedMandelbrotZ5
        // #139 — root-finding (iteration height), Buddhabrot family (density
        // height), Apollonian (synthesised dome height).
        || FractalType == FractalType.Newton
        || FractalType == FractalType.Nova
        || FractalType == FractalType.Halley
        || IsBuddhaBrot
        || IsApollonian
        // Random tiling — synthesised sphere-cap dome height (same path as
        // Apollonian).
        || IsRandomTile;

    /// <summary>Visibility flag for the cross-fractal domain-warp section
    /// (#253 / IDEA-3). True for the 2D escape-time family routed through
    /// <c>EscapeTimeCalculator</c> — the calculator that honours the warp.
    /// Mandelbrot is excluded: it runs on the dedicated deep-zoom calculator
    /// whose SIMD path the per-pixel warp doesn't touch.</summary>
    public bool SupportsDomainWarp =>
        IsJulia || IsMultibrot || IsPhoenix || IsGlynn || IsSpider
        || FractalType == FractalType.BurningShip
        || FractalType == FractalType.Tricorn
        || FractalType == FractalType.Magnet1
        || FractalType == FractalType.Magnet2;

    public bool HasNoParams =>
        !(IsJulia || IsMultibrot || IsPhoenix || IsGlynn || IsLogistic || IsSpider || IsNewtonOrNova || IsIFS
          || IsLSystem || IsStrangeAttractor || IsBuddhaBrot || IsMandelbulb || IsMandelbox || IsKifs
          || IsQuatJulia || IsQuatMandelbrot || IsPlasma || IsAcidWarp || IsFlame || IsApollonian || IsKleinian
          || IsBicomplexMandelbrot || IsDla || IsRandomTile || IsInteriorAlphaApplicable || IsRelief2DApplicable
          || SupportsDomainWarp);

    // ── Interior alpha (2D) — issue #96 ──────────────────────────────────────
    // Reads/writes FractalParameters directly (no cached backing field), same as
    // the Lighting* colour accessors. Each setter mutates _p in place then Fire()s
    // so the host re-renders; ApplyView copies InteriorAlpha onto the calculator.

    public int InteriorAlpha
    {
        get => _p.InteriorAlpha;
        set
        {
            int v = (int)Clamp(value, 0, 255);
            if (_p.InteriorAlpha == v) return;
            _p.InteriorAlpha = v;
            this.RaisePropertyChanged();
            Fire();
        }
    }

    // ── 2D heightfield relief (#102) ─────────────────────────────────────────
    // Direct-write to _p (same pattern as interior alpha); the render host reads
    // these off ViewState.FractalParameters in UploadProcessedBuffer.

    public bool Relief2DEnabled
    {
        get => _p.Relief2DEnabled;
        set { if (_p.Relief2DEnabled == value) return; _p.Relief2DEnabled = value; this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(ShowLightingFxLauncher)); Fire(); }
    }
    public double Relief2DHeightScale
    {
        get => _p.Relief2DHeightScale;
        set { double v = Clamp(value, 0.0, 6.0); if (_p.Relief2DHeightScale == v) return; _p.Relief2DHeightScale = v; this.RaisePropertyChanged(); Fire(); }
    }
    public double Relief2DLightAzimuthDeg
    {
        get => _p.Relief2DLightAzimuthDeg;
        set { double v = Clamp(value, 0.0, 360.0); if (_p.Relief2DLightAzimuthDeg == v) return; _p.Relief2DLightAzimuthDeg = v; this.RaisePropertyChanged(); Fire(); }
    }
    public double Relief2DLightElevationDeg
    {
        get => _p.Relief2DLightElevationDeg;
        set { double v = Clamp(value, 1.0, 89.0); if (_p.Relief2DLightElevationDeg == v) return; _p.Relief2DLightElevationDeg = v; this.RaisePropertyChanged(); Fire(); }
    }
    public double Relief2DShadowStrength
    {
        get => _p.Relief2DShadowStrength;
        set { double v = Clamp(value, 0.0, 1.0); if (_p.Relief2DShadowStrength == v) return; _p.Relief2DShadowStrength = v; this.RaisePropertyChanged(); Fire(); }
    }
    public double Relief2DStrength
    {
        get => _p.Relief2DStrength;
        set { double v = Clamp(value, 0.0, 1.0); if (_p.Relief2DStrength == v) return; _p.Relief2DStrength = v; this.RaisePropertyChanged(); Fire(); }
    }

    // #127 — absolute-height relief (emboss path): shade the whole surface, not
    // only real slopes, so shallow whole-set views get a strong global 3D read.
    public bool Relief2DAbsolute
    {
        get => _p.Relief2DAbsolute;
        set { if (_p.Relief2DAbsolute == value) return; _p.Relief2DAbsolute = value; this.RaisePropertyChanged(); Fire(); }
    }

    // #102 Phase 2 — oblique heightfield raymarch (perspective relief + volumetric).
    public bool Relief2DRaymarch
    {
        get => _p.Relief2DRaymarch;
        set { if (_p.Relief2DRaymarch == value) return; _p.Relief2DRaymarch = value; this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(ShowLightingFxLauncher)); Fire(); }
    }
    public double Relief2DCameraAzimuthDeg
    {
        get => _p.Relief2DCameraAzimuthDeg;
        set { double v = Clamp(value, -180.0, 180.0); if (_p.Relief2DCameraAzimuthDeg == v) return; _p.Relief2DCameraAzimuthDeg = v; this.RaisePropertyChanged(); Fire(); }
    }
    public double Relief2DCameraElevationDeg
    {
        get => _p.Relief2DCameraElevationDeg;
        set { double v = Clamp(value, 5.0, 89.0); if (_p.Relief2DCameraElevationDeg == v) return; _p.Relief2DCameraElevationDeg = v; this.RaisePropertyChanged(); Fire(); }
    }
    public double Relief2DCameraFovDeg
    {
        get => _p.Relief2DCameraFovDeg;
        set { double v = Clamp(value, 15.0, 100.0); if (_p.Relief2DCameraFovDeg == v) return; _p.Relief2DCameraFovDeg = v; this.RaisePropertyChanged(); Fire(); }
    }
    public double Relief2DCameraZoom
    {
        get => _p.Relief2DCameraZoom;
        set { double v = Clamp(value, 0.2, 5.0); if (_p.Relief2DCameraZoom == v) return; _p.Relief2DCameraZoom = v; this.RaisePropertyChanged(); Fire(); }
    }
    public bool Relief2DCameraOrthographic
    {
        get => _p.Relief2DCameraOrthographic;
        set { if (_p.Relief2DCameraOrthographic == value) return; _p.Relief2DCameraOrthographic = value; this.RaisePropertyChanged(); Fire(); }
    }
    public int Relief2DSupersample
    {
        get => _p.Relief2DSupersample;
        set { int v = (int)Clamp(value, 1, 4); if (_p.Relief2DSupersample == v) return; _p.Relief2DSupersample = v; this.RaisePropertyChanged(); Fire(); }
    }
    // Depth of field (roadmap S3, #389). Aperture 0 = pinhole (byte-identical).
    public double Relief2DDofApertureRadius
    {
        get => _p.Relief2DDofApertureRadius;
        set { double v = Clamp(value, 0.0, 1.0); if (_p.Relief2DDofApertureRadius == v) return; _p.Relief2DDofApertureRadius = v; this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(DofEnabled)); Fire(); }
    }
    public double Relief2DDofFocusDistance
    {
        get => _p.Relief2DDofFocusDistance;
        set { double v = Clamp(value, 0.0, 100.0); if (_p.Relief2DDofFocusDistance == v) return; _p.Relief2DDofFocusDistance = v; this.RaisePropertyChanged(); Fire(); }
    }
    /// <summary>Focus-distance control is only meaningful once the lens is open.</summary>
    public bool DofEnabled => _p.Relief2DDofApertureRadius > 0.0;
    public FracturingFog.HeightCurve2D Relief2DHeightCurve
    {
        get => _p.Relief2DHeightCurve;
        set { if (_p.Relief2DHeightCurve == value) return; _p.Relief2DHeightCurve = value; this.RaisePropertyChanged(); Fire(); }
    }
    public Array Relief2DHeightCurves => Enum.GetValues(typeof(FracturingFog.HeightCurve2D));
    public bool Relief2DBicubicHeight
    {
        get => _p.Relief2DBicubicHeight;
        set { if (_p.Relief2DBicubicHeight == value) return; _p.Relief2DBicubicHeight = value; this.RaisePropertyChanged(); Fire(); }
    }
    public bool Relief2DGroundPlane
    {
        get => _p.Relief2DGroundPlane;
        set { if (_p.Relief2DGroundPlane == value) return; _p.Relief2DGroundPlane = value; this.RaisePropertyChanged(); Fire(); }
    }
    public bool Relief2DAutoShade
    {
        get => _p.Relief2DAutoShade;
        set { if (_p.Relief2DAutoShade == value) return; _p.Relief2DAutoShade = value; this.RaisePropertyChanged(); Fire(); }
    }
    public double Relief2DEdgeFade
    {
        get => _p.Relief2DEdgeFade;
        set { double v = Clamp(value, 0.0, 0.5); if (_p.Relief2DEdgeFade == v) return; _p.Relief2DEdgeFade = v; this.RaisePropertyChanged(); Fire(); }
    }
    public bool Relief2DHiResField        // #143
    {
        get => _p.Relief2DHiResField;
        set { if (_p.Relief2DHiResField == value) return; _p.Relief2DHiResField = value; this.RaisePropertyChanged(); Fire(); }
    }
    public int Relief2DFieldFloor         // #143
    {
        get => _p.Relief2DFieldFloor;
        set { int v = (int)Clamp(value, 480, 2160); if (_p.Relief2DFieldFloor == v) return; _p.Relief2DFieldFloor = v; this.RaisePropertyChanged(); Fire(); }
    }
    public bool Relief2DIsolate
    {
        get => _p.Relief2DIsolate;
        set { if (_p.Relief2DIsolate == value) return; _p.Relief2DIsolate = value; this.RaisePropertyChanged(); Fire(); }
    }
    public bool Relief2DIsolateByDetail
    {
        get => _p.Relief2DIsolateByDetail;
        set { if (_p.Relief2DIsolateByDetail == value) return; _p.Relief2DIsolateByDetail = value; this.RaisePropertyChanged(); Fire(); }
    }
    public double Relief2DDetailThreshold
    {
        get => _p.Relief2DDetailThreshold;
        set { double v = Clamp(value, 0.0, 1.0); if (_p.Relief2DDetailThreshold == v) return; _p.Relief2DDetailThreshold = v; this.RaisePropertyChanged(); Fire(); }
    }
    public bool Relief2DIsolateByColor
    {
        get => _p.Relief2DIsolateByColor;
        set { if (_p.Relief2DIsolateByColor == value) return; _p.Relief2DIsolateByColor = value; this.RaisePropertyChanged(); Fire(); }
    }
    public string Relief2DDropColorsCsv
    {
        get => _p.Relief2DDropColorsCsv;
        set { string v = value ?? ""; if (_p.Relief2DDropColorsCsv == v) return; _p.Relief2DDropColorsCsv = v; this.RaisePropertyChanged(); Fire(); }
    }
    public double Relief2DColorTolerance
    {
        get => _p.Relief2DColorTolerance;
        set { double v = Clamp(value, 0.0, 1.0); if (_p.Relief2DColorTolerance == v) return; _p.Relief2DColorTolerance = v; this.RaisePropertyChanged(); Fire(); }
    }

    // #138 mesh export knobs. These affect only the exported mesh (not the live
    // render), so they don't Fire() a re-render — just notify the export path +
    // the size estimate.
    /// <summary>Exported mesh relief height (world units).</summary>
    public double Relief2DMeshHeight
    {
        get => _p.Relief2DMeshHeight;
        set { double v = Clamp(value, 0.0, 1.0); if (_p.Relief2DMeshHeight == v) return; _p.Relief2DMeshHeight = v; this.RaisePropertyChanged(); }
    }
    /// <summary>Exported mesh smoothing [0,1] (despike/merge strength).</summary>
    public double Relief2DMeshSmoothing
    {
        get => _p.Relief2DMeshSmoothing;
        set { double v = Clamp(value, 0.0, 1.0); if (_p.Relief2DMeshSmoothing == v) return; _p.Relief2DMeshSmoothing = v; this.RaisePropertyChanged(); }
    }
    /// <summary>Exported mesh detail = grid resolution (longer axis, cells).</summary>
    public int Relief2DMeshGrid
    {
        get => _p.Relief2DMeshGrid;
        set { int v = (int)Clamp(value, 64, 2048); if (_p.Relief2DMeshGrid == v) return; _p.Relief2DMeshGrid = v; this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(Relief2DMeshSizeEstimate)); }
    }
    /// <summary>Exported mesh file-size budget (MB); 0 = unlimited.</summary>
    public double Relief2DMeshMaxMB
    {
        get => _p.Relief2DMeshMaxMB;
        set { double v = Clamp(value, 0.0, 500.0); if (_p.Relief2DMeshMaxMB == v) return; _p.Relief2DMeshMaxMB = v; this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(Relief2DMeshSizeEstimate)); }
    }
    /// <summary>Contoured underside [0,1]: 0 = flat back, 1 = full mirrored contour.</summary>
    public double Relief2DMeshUnderside
    {
        get => _p.Relief2DMeshUnderside;
        set { double v = Clamp(value, 0.0, 1.0); if (_p.Relief2DMeshUnderside == v) return; _p.Relief2DMeshUnderside = v; this.RaisePropertyChanged(); }
    }
    /// <summary>Rough estimate of the exported OBJ size for the current detail /
    /// budget, shown next to the sliders so the detail↔size trade-off is visible.</summary>
    public string Relief2DMeshSizeEstimate
    {
        get
        {
            int grid = _p.Relief2DMeshGrid > 0 ? _p.Relief2DMeshGrid : 512;
            if (_p.Relief2DMeshMaxMB > 0.0)
            {
                double budgetGrid = Math.Sqrt(_p.Relief2DMeshMaxMB * 1024.0 * 1024.0 / 560.0 * 1.6);
                if (budgetGrid < grid) grid = (int)budgetGrid;
            }
            // ~ grid*(grid/1.6) cells worst-case, ~560 bytes/cell.
            double cells = grid * (grid / 1.6);
            double mb = cells * 560.0 / (1024.0 * 1024.0);
            return mb >= 1.0 ? $"~{mb:0.#} MB" : $"~{mb * 1024.0:0} KB";
        }
    }

    public Interior2DBackgroundMode Interior2DBackground
    {
        get => _p.Interior2DBackground;
        set
        {
            if (_p.Interior2DBackground == value) return;
            _p.Interior2DBackground = value;
            this.RaisePropertyChanged();
            Fire();
        }
    }
    public Array Interior2DBackgroundModes => Enum.GetValues(typeof(Interior2DBackgroundMode));

    /// <summary>Hex 0xAARRGGBB accessor for the Solid/Gradient top colour.
    /// TextBox binding, LostFocus — mirrors the Lighting colour hex accessors.</summary>
    public string Interior2DBgTopHex
    {
        get => _p.Interior2DBgTop.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (!TryParseHexColor(value, out uint u) || _p.Interior2DBgTop == u) return;
            _p.Interior2DBgTop = u;
            this.RaisePropertyChanged();
            Fire();
        }
    }

    /// <summary>Hex 0xAARRGGBB accessor for the Gradient bottom (horizon) colour.</summary>
    public string Interior2DBgBottomHex
    {
        get => _p.Interior2DBgBottom.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (!TryParseHexColor(value, out uint u) || _p.Interior2DBgBottom == u) return;
            _p.Interior2DBgBottom = u;
            this.RaisePropertyChanged();
            Fire();
        }
    }

    /// <summary>Path to the image used by the Image background mode.</summary>
    public string Interior2DBgImagePath
    {
        get => _p.Interior2DBgImagePath ?? string.Empty;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_p.Interior2DBgImagePath, v, StringComparison.Ordinal)) return;
            _p.Interior2DBgImagePath = v;
            this.RaisePropertyChanged();
            Fire();
        }
    }

    private static bool TryParseHexColor(string? value, out uint result)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    // ── Julia ──
    private double _juliaR;
    public double JuliaR { get => _juliaR; set { Set(ref _juliaR, Clamp(value, -2, 2)); _p.JuliaC = new Complex(_juliaR, _juliaI); Fire(); } }
    private double _juliaI;
    public double JuliaI { get => _juliaI; set { Set(ref _juliaI, Clamp(value, -2, 2)); _p.JuliaC = new Complex(_juliaR, _juliaI); Fire(); } }

    // ── Glynn ──
    private double _glynnR;
    public double GlynnR { get => _glynnR; set { Set(ref _glynnR, Clamp(value, -2, 2)); _p.GlynnC = new Complex(_glynnR, _glynnI); Fire(); } }
    private double _glynnI;
    public double GlynnI { get => _glynnI; set { Set(ref _glynnI, Clamp(value, -2, 2)); _p.GlynnC = new Complex(_glynnR, _glynnI); Fire(); } }

    // ── Logistic ──
    private int _logisticBurnIn;
    public int LogisticBurnIn { get => _logisticBurnIn; set { Set(ref _logisticBurnIn, Math.Max(0, value)); _p.LogisticBurnIn = _logisticBurnIn; Fire(); } }
    private double _logisticSeed;
    public double LogisticSeed { get => _logisticSeed; set { Set(ref _logisticSeed, Clamp(value, 0.001, 0.999)); _p.LogisticSeed = _logisticSeed; Fire(); } }

    // ── Secant ──
    private double _secantOffsetR;
    public double SecantOffsetR { get => _secantOffsetR; set { Set(ref _secantOffsetR, Clamp(value, -2, 2)); _p.SecantInitialOffset = new Complex(_secantOffsetR, _secantOffsetI); Fire(); } }
    private double _secantOffsetI;
    public double SecantOffsetI { get => _secantOffsetI; set { Set(ref _secantOffsetI, Clamp(value, -2, 2)); _p.SecantInitialOffset = new Complex(_secantOffsetR, _secantOffsetI); Fire(); } }

    // ── Spider ──
    private double _spiderCDecay;
    public double SpiderCDecay { get => _spiderCDecay; set { Set(ref _spiderCDecay, Clamp(value, 0.0, 1.0)); _p.SpiderCDecay = _spiderCDecay; Fire(); } }

    // ── Julia animation ──
    //
    // Sweeps the Julia c constant in a circular orbit around the origin of the
    // complex plane at the current |c| radius. Forward = CCW (positive angular
    // velocity), Reverse = CW. Speed is radians per second so 6.28 ≈ one full
    // orbit per second; default 0.2 gives a calm sweep visible in real time.
    private bool _juliaAnimateForward = true;
    public bool JuliaAnimateForward
    {
        get => _juliaAnimateForward;
        set => this.RaiseAndSetIfChanged(ref _juliaAnimateForward, value);
    }
    public bool JuliaAnimateReverse
    {
        get => !_juliaAnimateForward;
        set { if (value != !_juliaAnimateForward) JuliaAnimateForward = !value; }
    }

    private double _juliaAnimateSpeed = 0.2;
    public double JuliaAnimateSpeed
    {
        get => _juliaAnimateSpeed;
        set => this.RaiseAndSetIfChanged(ref _juliaAnimateSpeed, Clamp(value, 0.001, 6.283));
    }

    private bool _juliaAnimating;
    public bool JuliaAnimating
    {
        get => _juliaAnimating;
        private set
        {
            this.RaiseAndSetIfChanged(ref _juliaAnimating, value);
            this.RaisePropertyChanged(nameof(JuliaAnimateButtonText));
        }
    }

    public string JuliaAnimateButtonText => _juliaAnimating ? "Stop" : "Animate";

    public ReactiveCommand<Unit, Unit> ToggleJuliaAnimateCommand { get; }

    // Render-pacing gate + dispatcher tick lives in ParameterAnimationBus
    // (Animation/ParameterAnimationBus.cs). The Julia orbit body lives in
    // JuliaCAnimator. Reason: the same gate handles every future animator
    // we plug in (Phase 0 of the Animation Roadmap). Behaviour for the
    // Julia-only case is preserved bit-for-bit — same 50 ms Background
    // tick, same render-completion gate, same dt cap, same |c| floor.
    private ParameterAnimationBus? _animationBus;
    private JuliaCAnimator? _juliaAnimator;

    private void EnsureAnimationBus()
    {
        if (_animationBus != null) return;
        _animationBus = new ParameterAnimationBus(Fire);
        _juliaAnimator = new JuliaCAnimator(this);
        _animationBus.Register(_juliaAnimator);
    }

    /// <summary>Host calls this after each render frame completes. Releases the
    /// animation gate so the next integrated parameter values can drive the
    /// next render.</summary>
    public void NotifyRenderCompleted() => _animationBus?.NotifyRenderCompleted();

    /// <summary>Bus-only-internal silent setter. Updates the cached fields and
    /// the underlying <see cref="FractalParameters.JuliaC"/> with property-
    /// change notifications, but suppresses <see cref="Fire"/> so the bus
    /// can coalesce a single render trigger after every enabled animator
    /// has ticked.</summary>
    internal void SetJuliaSilent(double r, double i)
    {
        _suppress = true;
        try
        {
            JuliaR = r;
            JuliaI = i;
        }
        finally { _suppress = false; }
    }

    private void ToggleJuliaAnimate()
    {
        if (_juliaAnimating) StopJuliaAnimate();
        else                 StartJuliaAnimate();
    }

    private void StartJuliaAnimate()
    {
        EnsureAnimationBus();
        _juliaAnimator!.IsEnabled = true;
        _animationBus!.Refresh();
        JuliaAnimating = true;
    }

    private void StopJuliaAnimate()
    {
        if (_juliaAnimator != null) _juliaAnimator.IsEnabled = false;
        _animationBus?.Refresh();
        JuliaAnimating = false;
    }

    // ── Multibrot ──
    private int _multibrotD;
    public int MultibrotExponent { get => _multibrotD; set { Set(ref _multibrotD, (int)Clamp(value, 2, 8)); _p.MultibrotExponent = _multibrotD; Fire(); } }

    // ── Phoenix ──
    private double _phoenixR;
    public double PhoenixR { get => _phoenixR; set { Set(ref _phoenixR, Clamp(value, -2, 2)); _p.PhoenixP = new Complex(_phoenixR, _phoenixI); Fire(); } }
    private double _phoenixI;
    public double PhoenixI { get => _phoenixI; set { Set(ref _phoenixI, Clamp(value, -2, 2)); _p.PhoenixP = new Complex(_phoenixR, _phoenixI); Fire(); } }

    // ── Newton / Nova ──
    private int _newtonExponent;
    public int NewtonExponent { get => _newtonExponent; set { Set(ref _newtonExponent, (int)Clamp(value, 2, 8)); _p.NewtonExponent = _newtonExponent; Fire(); } }
    private double _newtonRelaxation;
    public double NewtonRelaxation { get => _newtonRelaxation; set { Set(ref _newtonRelaxation, Clamp(value, 0.1, 2.0)); _p.NewtonRelaxation = _newtonRelaxation; Fire(); } }

    // ── IFS ──
    private string _ifsPresetName;
    public string IfsPresetName
    {
        get => _ifsPresetName;
        set
        {
            if (Set(ref _ifsPresetName, value))
            {
                _p.IFSPresetName = value;
                _p.IFSMaps = null; // reset override so preset name takes effect
                Fire();
            }
        }
    }
    private int _ifsIterations;
    public int IfsIterations { get => _ifsIterations; set { Set(ref _ifsIterations, (int)Clamp(value, 100_000, 20_000_000)); _p.IFSIterations = _ifsIterations; Fire(); } }

    // ── LSystem ──
    private string _lsystemPresetName;
    public string LSystemPresetName
    {
        get => _lsystemPresetName;
        set { if (Set(ref _lsystemPresetName, value)) { _p.LSystemPresetName = value; Fire(); } }
    }
    private int _lsystemDepth;
    public int LSystemDepth { get => _lsystemDepth; set { Set(ref _lsystemDepth, (int)Clamp(value, 0, 12)); _p.LSystemDepth = _lsystemDepth; Fire(); } }

    // ── LSystem Depth sweep ──
    //
    // Mirrors the AdaptiveSweep pattern in FloatingMenuViewModel but on the
    // L-System depth integer instead of the post-FX adaptive slider. Each tick
    // writes through the LSystemDepth setter so the spinner UI and the
    // underlying FractalParameters stay in sync and a re-render fires.
    //
    // Depth's small integer range (0..12) means we round each tick to the
    // nearest int — a 5 s forward sweep visibly steps through each depth so
    // the user can watch the curve add detail layer by layer.

    private const int LSystemSweepTickMs = 50;
    private const int LSystemDepthMin = 0;
    private const int LSystemDepthMax = 12;

    private AdaptiveSweepMode _lsystemSweepMode = AdaptiveSweepMode.Forward;
    public AdaptiveSweepMode LSystemSweepMode
    {
        get => _lsystemSweepMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _lsystemSweepMode, value);
            this.RaisePropertyChanged(nameof(IsLSystemForwardMode));
            this.RaisePropertyChanged(nameof(IsLSystemReverseMode));
            this.RaisePropertyChanged(nameof(IsLSystemPingPongMode));
        }
    }
    public bool IsLSystemForwardMode
    {
        get => LSystemSweepMode == AdaptiveSweepMode.Forward;
        set { if (value) LSystemSweepMode = AdaptiveSweepMode.Forward; }
    }
    public bool IsLSystemReverseMode
    {
        get => LSystemSweepMode == AdaptiveSweepMode.Reverse;
        set { if (value) LSystemSweepMode = AdaptiveSweepMode.Reverse; }
    }
    public bool IsLSystemPingPongMode
    {
        get => LSystemSweepMode == AdaptiveSweepMode.PingPong;
        set { if (value) LSystemSweepMode = AdaptiveSweepMode.PingPong; }
    }

    private bool _lsystemSweepLoop;
    public bool LSystemSweepLoop
    {
        get => _lsystemSweepLoop;
        set => this.RaiseAndSetIfChanged(ref _lsystemSweepLoop, value);
    }

    private double _lsystemSweepDurationSeconds = 5.0;
    public double LSystemSweepDurationSeconds
    {
        get => _lsystemSweepDurationSeconds;
        set => this.RaiseAndSetIfChanged(ref _lsystemSweepDurationSeconds, Clamp(value, 0.25, 600.0));
    }

    private bool _isLSystemSweeping;
    public bool IsLSystemSweeping
    {
        get => _isLSystemSweeping;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLSystemSweeping, value);
            this.RaisePropertyChanged(nameof(LSystemSweepButtonLabel));
        }
    }
    public string LSystemSweepButtonLabel => IsLSystemSweeping ? "Stop Sweep" : "Sweep";

    private DispatcherTimer? _lsystemSweepTimer;
    private DateTime _lsystemSweepStartedUtc;
    private double _lsystemSweepDurationMsSnapshot;
    private AdaptiveSweepMode _lsystemSweepActiveMode;
    private bool _lsystemSweepActiveLoop;

    public ReactiveCommand<Unit, Unit>? ToggleLSystemSweepCommand { get; private set; }

    private void ToggleLSystemSweep()
    {
        if (IsLSystemSweeping) StopLSystemSweep();
        else                   StartLSystemSweep();
    }

    private void StartLSystemSweep()
    {
        if (IsLSystemSweeping) return;
        _lsystemSweepDurationMsSnapshot = Math.Max(250.0, LSystemSweepDurationSeconds * 1000.0);
        _lsystemSweepStartedUtc = DateTime.UtcNow;
        _lsystemSweepActiveMode = LSystemSweepMode;
        _lsystemSweepActiveLoop = LSystemSweepLoop;
        LSystemDepth = _lsystemSweepActiveMode == AdaptiveSweepMode.Reverse ? LSystemDepthMax : LSystemDepthMin;
        IsLSystemSweeping = true;

        _lsystemSweepTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(LSystemSweepTickMs),
            DispatcherPriority.Render,
            OnLSystemSweepTick);
        _lsystemSweepTimer.Start();
    }

    private void StopLSystemSweep()
    {
        _lsystemSweepTimer?.Stop();
        _lsystemSweepTimer = null;
        IsLSystemSweeping = false;
    }

    private void OnLSystemSweepTick(object? sender, EventArgs e)
    {
        double elapsedMs = (DateTime.UtcNow - _lsystemSweepStartedUtc).TotalMilliseconds;
        double t = elapsedMs / _lsystemSweepDurationMsSnapshot;
        int span = LSystemDepthMax - LSystemDepthMin;

        if (t >= 1.0)
        {
            if (_lsystemSweepActiveLoop)
            {
                _lsystemSweepStartedUtc = DateTime.UtcNow;
                LSystemDepth = _lsystemSweepActiveMode == AdaptiveSweepMode.Reverse ? LSystemDepthMax : LSystemDepthMin;
                return;
            }
            LSystemDepth = _lsystemSweepActiveMode switch
            {
                AdaptiveSweepMode.Forward  => LSystemDepthMax,
                AdaptiveSweepMode.Reverse  => LSystemDepthMin,
                AdaptiveSweepMode.PingPong => LSystemDepthMin,
                _ => LSystemDepth,
            };
            StopLSystemSweep();
            return;
        }

        LSystemDepth = _lsystemSweepActiveMode switch
        {
            AdaptiveSweepMode.Forward  => LSystemDepthMin + (int)Math.Round(t * span),
            AdaptiveSweepMode.Reverse  => LSystemDepthMin + (int)Math.Round((1.0 - t) * span),
            AdaptiveSweepMode.PingPong => t < 0.5
                ? LSystemDepthMin + (int)Math.Round(t * 2.0 * span)
                : LSystemDepthMin + (int)Math.Round((1.0 - t) * 2.0 * span),
            _ => LSystemDepth,
        };
    }

    // ── Strange Attractor ──
    private string _attractorPresetName;
    public string AttractorPresetName
    {
        get => _attractorPresetName;
        set
        {
            if (!Set(ref _attractorPresetName, value)) return;
            _p.AttractorPresetName = value;
            if (_attractorDefaults is not null)
            {
                var (da, db, dc, dd) = _attractorDefaults(value);
                _suppress = true;
                try
                {
                    AttractorA = Clamp(da, -3, 3);
                    AttractorB = Clamp(db, -3, 3);
                    AttractorC = Clamp(dc, -3, 3);
                    AttractorD = Clamp(dd, -3, 3);
                }
                finally { _suppress = false; }
            }
            Fire();
        }
    }
    private int _attractorIterations;
    public int AttractorIterations { get => _attractorIterations; set { Set(ref _attractorIterations, (int)Clamp(value, 100_000, 20_000_000)); _p.AttractorIterations = _attractorIterations; Fire(); } }
    private double _attractorA;
    public double AttractorA { get => _attractorA; set { Set(ref _attractorA, Clamp(value, -3, 3)); _p.AttractorA = _attractorA; Fire(); } }
    private double _attractorB;
    public double AttractorB { get => _attractorB; set { Set(ref _attractorB, Clamp(value, -3, 3)); _p.AttractorB = _attractorB; Fire(); } }
    private double _attractorC;
    public double AttractorC { get => _attractorC; set { Set(ref _attractorC, Clamp(value, -3, 3)); _p.AttractorC = _attractorC; Fire(); } }
    private double _attractorD;
    public double AttractorD { get => _attractorD; set { Set(ref _attractorD, Clamp(value, -3, 3)); _p.AttractorD = _attractorD; Fire(); } }

    // ── BuddhaBrot ──
    private int _buddhaSamples;
    public int BuddhaSamples { get => _buddhaSamples; set { Set(ref _buddhaSamples, (int)Clamp(value, 50_000, 50_000_000)); _p.BuddhaSamples = _buddhaSamples; Fire(); } }
    private int _buddhaIterLow;
    public int BuddhaIterLow { get => _buddhaIterLow; set { Set(ref _buddhaIterLow, (int)Clamp(value, 50, 100_000)); _p.BuddhaIterLow = _buddhaIterLow; Fire(); } }
    private int _buddhaIterMid;
    public int BuddhaIterMid { get => _buddhaIterMid; set { Set(ref _buddhaIterMid, (int)Clamp(value, 100, 200_000)); _p.BuddhaIterMid = _buddhaIterMid; Fire(); } }
    private int _buddhaIterHigh;
    public int BuddhaIterHigh { get => _buddhaIterHigh; set { Set(ref _buddhaIterHigh, (int)Clamp(value, 500, 500_000)); _p.BuddhaIterHigh = _buddhaIterHigh; Fire(); } }
    private BuddhaColorMode _buddhaColorMode;
    public BuddhaColorMode BuddhaColorMode
    {
        get => _buddhaColorMode;
        set { Set(ref _buddhaColorMode, value); _p.BuddhaColorMode = value; Fire(); }
    }
    public Array BuddhaColorModes => Enum.GetValues(typeof(BuddhaColorMode));

    private BuddhaQualityMode _buddhaQualityMode;
    public BuddhaQualityMode BuddhaQualityMode
    {
        get => _buddhaQualityMode;
        set { Set(ref _buddhaQualityMode, value); _p.BuddhaQualityMode = value; Fire(); }
    }
    public Array BuddhaQualityModes => Enum.GetValues(typeof(BuddhaQualityMode));

    private bool _buddhaMetropolis;
    public bool BuddhaMetropolis
    {
        get => _buddhaMetropolis;
        set { Set(ref _buddhaMetropolis, value); _p.BuddhaMetropolis = value; Fire(); }
    }

    private bool _buddhaProgressive;
    public bool BuddhaProgressive
    {
        get => _buddhaProgressive;
        set { Set(ref _buddhaProgressive, value); _p.BuddhaProgressive = value; Fire(); }
    }

    // #193 — deterministic Monte Carlo seed. Changing it draws a different but
    // reproducible sampling of the same fractal; identical seed + params = an
    // identical image (no more 'morph' when unrelated settings change).
    private int _buddhaSeed;
    public int BuddhaSeed { get => _buddhaSeed; set { Set(ref _buddhaSeed, value); _p.BuddhaSeed = value; Fire(); } }

    // ── Mandelbulb ──
    private double _bulbPower;
    public double BulbPower { get => _bulbPower; set { Set(ref _bulbPower, Clamp(value, 2, 16)); _p.BulbPower = _bulbPower; Fire(); } }
    private int _bulbIterations;
    public int BulbIterations { get => _bulbIterations; set { Set(ref _bulbIterations, (int)Clamp(value, 2, 16)); _p.BulbIterations = _bulbIterations; Fire(); } }
    private double _bulbCameraTheta;
    public double BulbCameraTheta { get => _bulbCameraTheta; set { Set(ref _bulbCameraTheta, Clamp(value, -10, 10)); _p.BulbCameraTheta = _bulbCameraTheta; Fire(); } }
    private double _bulbCameraPhi;
    public double BulbCameraPhi { get => _bulbCameraPhi; set { Set(ref _bulbCameraPhi, Clamp(value, 0.01, 3.13)); _p.BulbCameraPhi = _bulbCameraPhi; Fire(); } }
    private double _bulbCameraDistance;
    public double BulbCameraDistance { get => _bulbCameraDistance; set { Set(ref _bulbCameraDistance, Clamp(value, 0.1, 500)); _p.BulbCameraDistance = _bulbCameraDistance; Fire(); } }

    // ── Mandelbox ──
    private double _mandelboxScale;
    public double MandelboxScale { get => _mandelboxScale; set { Set(ref _mandelboxScale, Clamp(value, -4, 4)); _p.MandelboxScale = _mandelboxScale; Fire(); } }
    private double _mandelboxFixedRadius;
    public double MandelboxFixedRadius { get => _mandelboxFixedRadius; set { Set(ref _mandelboxFixedRadius, Clamp(value, 0.1, 4)); _p.MandelboxFixedRadius = _mandelboxFixedRadius; Fire(); } }
    private double _mandelboxMinRadius;
    public double MandelboxMinRadius { get => _mandelboxMinRadius; set { Set(ref _mandelboxMinRadius, Clamp(value, 0.05, 2)); _p.MandelboxMinRadius = _mandelboxMinRadius; Fire(); } }
    private int _mandelboxIterations;
    public int MandelboxIterations { get => _mandelboxIterations; set { Set(ref _mandelboxIterations, (int)Clamp(value, 2, 32)); _p.MandelboxIterations = _mandelboxIterations; Fire(); } }
    private double _mandelboxCameraTheta;
    public double MandelboxCameraTheta { get => _mandelboxCameraTheta; set { Set(ref _mandelboxCameraTheta, Clamp(value, -10, 10)); _p.MandelboxCameraTheta = _mandelboxCameraTheta; Fire(); } }
    private double _mandelboxCameraPhi;
    public double MandelboxCameraPhi { get => _mandelboxCameraPhi; set { Set(ref _mandelboxCameraPhi, Clamp(value, 0.01, 3.13)); _p.MandelboxCameraPhi = _mandelboxCameraPhi; Fire(); } }
    private double _mandelboxCameraDistance;
    public double MandelboxCameraDistance { get => _mandelboxCameraDistance; set { Set(ref _mandelboxCameraDistance, Clamp(value, 0.1, 500)); _p.MandelboxCameraDistance = _mandelboxCameraDistance; Fire(); } }

    // ── KIFS ──
    private KifsFoldKind _kifsFold;
    public KifsFoldKind KifsFold { get => _kifsFold; set { Set(ref _kifsFold, value); _p.KifsFold = value; Fire(); } }
    public Array KifsFoldKinds => Enum.GetValues(typeof(KifsFoldKind));
    private int _kifsIterations;
    public int KifsIterations { get => _kifsIterations; set { Set(ref _kifsIterations, (int)Clamp(value, 2, 32)); _p.KifsIterations = _kifsIterations; Fire(); } }
    private double _kifsScale;
    public double KifsScale { get => _kifsScale; set { Set(ref _kifsScale, Clamp(value, 0.0, 6.0)); _p.KifsScale = _kifsScale; Fire(); } }
    private double _kifsOffsetX;
    public double KifsOffsetX { get => _kifsOffsetX; set { Set(ref _kifsOffsetX, Clamp(value, -3, 3)); _p.KifsOffsetX = _kifsOffsetX; Fire(); } }
    private double _kifsOffsetY;
    public double KifsOffsetY { get => _kifsOffsetY; set { Set(ref _kifsOffsetY, Clamp(value, -3, 3)); _p.KifsOffsetY = _kifsOffsetY; Fire(); } }
    private double _kifsOffsetZ;
    public double KifsOffsetZ { get => _kifsOffsetZ; set { Set(ref _kifsOffsetZ, Clamp(value, -3, 3)); _p.KifsOffsetZ = _kifsOffsetZ; Fire(); } }
    private double _kifsCameraTheta;
    public double KifsCameraTheta { get => _kifsCameraTheta; set { Set(ref _kifsCameraTheta, Clamp(value, -10, 10)); _p.KifsCameraTheta = _kifsCameraTheta; Fire(); } }
    private double _kifsCameraPhi;
    public double KifsCameraPhi { get => _kifsCameraPhi; set { Set(ref _kifsCameraPhi, Clamp(value, 0.01, 3.13)); _p.KifsCameraPhi = _kifsCameraPhi; Fire(); } }
    private double _kifsCameraDistance;
    public double KifsCameraDistance { get => _kifsCameraDistance; set { Set(ref _kifsCameraDistance, Clamp(value, 0.1, 500)); _p.KifsCameraDistance = _kifsCameraDistance; Fire(); } }

    // ── Quaternion Julia ──
    private double _qjCX;
    public double QJuliaCX { get => _qjCX; set { Set(ref _qjCX, Clamp(value, -2, 2)); _p.QJuliaCX = _qjCX; Fire(); } }
    private double _qjCY;
    public double QJuliaCY { get => _qjCY; set { Set(ref _qjCY, Clamp(value, -2, 2)); _p.QJuliaCY = _qjCY; Fire(); } }
    private double _qjCZ;
    public double QJuliaCZ { get => _qjCZ; set { Set(ref _qjCZ, Clamp(value, -2, 2)); _p.QJuliaCZ = _qjCZ; Fire(); } }
    private double _qjCW;
    public double QJuliaCW { get => _qjCW; set { Set(ref _qjCW, Clamp(value, -2, 2)); _p.QJuliaCW = _qjCW; Fire(); } }
    private double _qjSliceW;
    public double QJuliaSliceW { get => _qjSliceW; set { Set(ref _qjSliceW, Clamp(value, -2, 2)); _p.QJuliaSliceW = _qjSliceW; Fire(); } }
    private int _qjIterations;
    public int QJuliaIterations { get => _qjIterations; set { Set(ref _qjIterations, (int)Clamp(value, 2, 32)); _p.QJuliaIterations = _qjIterations; Fire(); } }
    private double _qjCameraTheta;
    public double QJuliaCameraTheta { get => _qjCameraTheta; set { Set(ref _qjCameraTheta, Clamp(value, -10, 10)); _p.QJuliaCameraTheta = _qjCameraTheta; Fire(); } }
    private double _qjCameraPhi;
    public double QJuliaCameraPhi { get => _qjCameraPhi; set { Set(ref _qjCameraPhi, Clamp(value, 0.01, 3.13)); _p.QJuliaCameraPhi = _qjCameraPhi; Fire(); } }
    private double _qjCameraDistance;
    public double QJuliaCameraDistance { get => _qjCameraDistance; set { Set(ref _qjCameraDistance, Clamp(value, 0.1, 500)); _p.QJuliaCameraDistance = _qjCameraDistance; Fire(); } }

    // ── Quaternion Mandelbrot ──
    private double _qmSliceW;
    public double QMandelSliceW { get => _qmSliceW; set { Set(ref _qmSliceW, Clamp(value, -2, 2)); _p.QMandelSliceW = _qmSliceW; Fire(); } }
    private int _qmIterations;
    public int QMandelIterations { get => _qmIterations; set { Set(ref _qmIterations, (int)Clamp(value, 2, 32)); _p.QMandelIterations = _qmIterations; Fire(); } }
    private double _qmCameraTheta;
    public double QMandelCameraTheta { get => _qmCameraTheta; set { Set(ref _qmCameraTheta, Clamp(value, -10, 10)); _p.QMandelCameraTheta = _qmCameraTheta; Fire(); } }
    private double _qmCameraPhi;
    public double QMandelCameraPhi { get => _qmCameraPhi; set { Set(ref _qmCameraPhi, Clamp(value, 0.01, 3.13)); _p.QMandelCameraPhi = _qmCameraPhi; Fire(); } }
    private double _qmCameraDistance;
    public double QMandelCameraDistance { get => _qmCameraDistance; set { Set(ref _qmCameraDistance, Clamp(value, 0.1, 500)); _p.QMandelCameraDistance = _qmCameraDistance; Fire(); } }

    // ── Plasma ──
    private double _plasmaRoughness;
    public double PlasmaRoughness { get => _plasmaRoughness; set { Set(ref _plasmaRoughness, Clamp(value, 0.0, 1.0)); _p.PlasmaRoughness = _plasmaRoughness; Fire(); } }
    private int _plasmaSeed;
    public int PlasmaSeed { get => _plasmaSeed; set { Set(ref _plasmaSeed, value); _p.PlasmaSeed = _plasmaSeed; Fire(); } }

    // ── Acid Warp (#247) ──
    private int _acidWarpPattern;
    public int AcidWarpPattern { get => _acidWarpPattern; set { Set(ref _acidWarpPattern, (int)Clamp(value, 0, FractalParameters.AcidWarpPatternCount - 1)); _p.AcidWarpPattern = _acidWarpPattern; Fire(); } }
    /// <summary>Upper bound for the pattern slider (inclusive).</summary>
    public int AcidWarpPatternMax => FractalParameters.AcidWarpPatternCount - 1;
    private double _acidWarpFrequency;
    public double AcidWarpFrequency { get => _acidWarpFrequency; set { Set(ref _acidWarpFrequency, Clamp(value, 0.1, 8.0)); _p.AcidWarpFrequency = _acidWarpFrequency; Fire(); } }
    private double _acidWarpCenterX;
    public double AcidWarpCenterX { get => _acidWarpCenterX; set { Set(ref _acidWarpCenterX, Clamp(value, -2.0, 2.0)); _p.AcidWarpCenterX = _acidWarpCenterX; Fire(); } }
    private double _acidWarpCenterY;
    public double AcidWarpCenterY { get => _acidWarpCenterY; set { Set(ref _acidWarpCenterY, Clamp(value, -2.0, 2.0)); _p.AcidWarpCenterY = _acidWarpCenterY; Fire(); } }
    private int _acidWarpSeed;
    public int AcidWarpSeed { get => _acidWarpSeed; set { Set(ref _acidWarpSeed, value); _p.AcidWarpSeed = _acidWarpSeed; Fire(); } }
    private double _acidWarpWarpStrength;
    public double AcidWarpWarpStrength { get => _acidWarpWarpStrength; set { Set(ref _acidWarpWarpStrength, Clamp(value, 0.0, 2.0)); _p.AcidWarpWarpStrength = _acidWarpWarpStrength; Fire(); } }
    private bool _acidWarpMorph;
    /// <summary>Enable continuous pattern morphing (blend adjacent patterns via
    /// <see cref="AcidWarpFlow"/>) instead of hard-cutting between them.</summary>
    public bool AcidWarpMorph { get => _acidWarpMorph; set { Set(ref _acidWarpMorph, value); _p.AcidWarpMorph = _acidWarpMorph; Fire(); } }
    private double _acidWarpFlow;
    /// <summary>Continuous pattern position (integer part picks the base pattern,
    /// fraction blends toward the next). Only used when <see cref="AcidWarpMorph"/>
    /// is on. Animate 0 → pattern-count for an endless morph.</summary>
    public double AcidWarpFlow { get => _acidWarpFlow; set { Set(ref _acidWarpFlow, Clamp(value, 0.0, FractalParameters.AcidWarpPatternCount)); _p.AcidWarpFlow = _acidWarpFlow; Fire(); } }
    /// <summary>Upper bound for the Flow slider (== pattern count, wraps).</summary>
    public double AcidWarpFlowMax => FractalParameters.AcidWarpPatternCount;

    // ── Cross-fractal domain warp (#253 / IDEA-3) ──
    private bool _domainWarpEnabled;
    /// <summary>Enable the cross-fractal domain warp — displaces each pixel's
    /// sampling coordinate by a sine-interference field before iterating.</summary>
    public bool DomainWarpEnabled { get => _domainWarpEnabled; set { Set(ref _domainWarpEnabled, value); _p.DomainWarpEnabled = _domainWarpEnabled; Fire(); } }
    private double _domainWarpStrength;
    /// <summary>Domain-warp strength (fraction of the half-view span). 0 = off.</summary>
    public double DomainWarpStrength { get => _domainWarpStrength; set { Set(ref _domainWarpStrength, Clamp(value, 0.0, 1.0)); _p.DomainWarpStrength = _domainWarpStrength; Fire(); } }
    private double _domainWarpFrequency;
    /// <summary>Domain-warp field frequency (spatial density of the swirl).</summary>
    public double DomainWarpFrequency { get => _domainWarpFrequency; set { Set(ref _domainWarpFrequency, Clamp(value, 0.1, 8.0)); _p.DomainWarpFrequency = _domainWarpFrequency; Fire(); } }

    // ── Apollonian ──
    private int _apolloDepth;
    public int ApollonianDepth { get => _apolloDepth; set { Set(ref _apolloDepth, (int)Clamp(value, 0, 40)); _p.ApollonianDepth = _apolloDepth; Fire(); } }
    private double _apolloMinPx;
    public double ApollonianMinPixelRadius { get => _apolloMinPx; set { Set(ref _apolloMinPx, Clamp(value, 0.25, 16.0)); _p.ApollonianMinPixelRadius = _apolloMinPx; Fire(); } }
    private bool _apolloColorByDepth;
    public bool ApollonianColorByDepth { get => _apolloColorByDepth; set { Set(ref _apolloColorByDepth, value); _p.ApollonianColorByDepth = _apolloColorByDepth; Fire(); } }
    private double _apolloRelief;
    public double ApollonianRelief { get => _apolloRelief; set { Set(ref _apolloRelief, Clamp(value, 0.0, 4.0)); _p.ApollonianRelief = _apolloRelief; Fire(); } }

    // ── Random Tiling (Bourke) ──
    private int _rtCount;
    /// <summary>Maximum number of shapes to place.</summary>
    public int RandomTileCount { get => _rtCount; set { Set(ref _rtCount, (int)Clamp(value, 1, 200000)); _p.RandomTileCount = _rtCount; Fire(); } }
    private double _rtSizeExponent;
    /// <summary>Size falloff exponent α (larger → few big + many tiny shapes).</summary>
    public double RandomTileSizeExponent { get => _rtSizeExponent; set { Set(ref _rtSizeExponent, Clamp(value, 0.2, 6.0)); _p.RandomTileSizeExponent = _rtSizeExponent; Fire(); } }
    private int _rtSeed;
    /// <summary>PRNG seed — identical settings + seed reproduce the tiling.</summary>
    public int RandomTileSeed { get => _rtSeed; set { Set(ref _rtSeed, value); _p.RandomTileSeed = _rtSeed; Fire(); } }
    private double _rtGap;
    /// <summary>Margin between shapes as a fraction of the candidate radius.</summary>
    public double RandomTileGap { get => _rtGap; set { Set(ref _rtGap, Clamp(value, 0.0, 2.0)); _p.RandomTileGap = _rtGap; Fire(); } }
    private double _rtMinPx;
    /// <summary>Stop placing when the next radius would draw below this many pixels.</summary>
    public double RandomTileMinPixelRadius { get => _rtMinPx; set { Set(ref _rtMinPx, Clamp(value, 0.25, 16.0)); _p.RandomTileMinPixelRadius = _rtMinPx; Fire(); } }
    private bool _rtColorByIndex;
    /// <summary>Colour by placement index (palette sweep) vs. log-radius.</summary>
    public bool RandomTileColorByIndex { get => _rtColorByIndex; set { Set(ref _rtColorByIndex, value); _p.RandomTileColorByIndex = _rtColorByIndex; Fire(); } }
    private double _rtRelief;
    /// <summary>Dome relief amplitude for 3D themes (0 = flat).</summary>
    public double RandomTileRelief { get => _rtRelief; set { Set(ref _rtRelief, Clamp(value, 0.0, 4.0)); _p.RandomTileRelief = _rtRelief; Fire(); } }
    private RandomTileShape _rtShape;
    /// <summary>Tile shape — Circle / Square / Triangle (polygons get random rotation).</summary>
    public RandomTileShape RandomTileShape { get => _rtShape; set { Set(ref _rtShape, value); _p.RandomTileShape = value; Fire(); } }
    public System.Array RandomTileShapes => System.Enum.GetValues(typeof(RandomTileShape));

    // ── DLA ──
    private int _dlaParticles;
    public int DlaParticles { get => _dlaParticles; set { Set(ref _dlaParticles, (int)Clamp(value, 100, 500_000)); _p.DlaParticles = _dlaParticles; Fire(); } }
    private int _dlaSeed;
    public int DlaSeed { get => _dlaSeed; set { Set(ref _dlaSeed, value); _p.DlaSeed = _dlaSeed; Fire(); } }

    // ── Bicomplex Mandelbrot ──
    private double _bcSliceW;
    public double BicomplexSliceW { get => _bcSliceW; set { Set(ref _bcSliceW, Clamp(value, -2, 2)); _p.BicomplexSliceW = _bcSliceW; Fire(); } }
    private BicomplexSliceAxis _bcSliceAxis;
    public BicomplexSliceAxis BicomplexSliceAxis { get => _bcSliceAxis; set { Set(ref _bcSliceAxis, value); _p.BicomplexSliceAxis = value; Fire(); } }
    public Array BicomplexSliceAxes => Enum.GetValues(typeof(BicomplexSliceAxis));
    private int _bcIterations;
    public int BicomplexIterations { get => _bcIterations; set { Set(ref _bcIterations, (int)Clamp(value, 2, 32)); _p.BicomplexIterations = _bcIterations; Fire(); } }
    private double _bcCameraTheta;
    public double BicomplexCameraTheta { get => _bcCameraTheta; set { Set(ref _bcCameraTheta, Clamp(value, -10, 10)); _p.BicomplexCameraTheta = _bcCameraTheta; Fire(); } }
    private double _bcCameraPhi;
    public double BicomplexCameraPhi { get => _bcCameraPhi; set { Set(ref _bcCameraPhi, Clamp(value, 0.01, 3.13)); _p.BicomplexCameraPhi = _bcCameraPhi; Fire(); } }
    private double _bcCameraDistance;
    public double BicomplexCameraDistance { get => _bcCameraDistance; set { Set(ref _bcCameraDistance, Clamp(value, 0.1, 500)); _p.BicomplexCameraDistance = _bcCameraDistance; Fire(); } }

    // ── Kleinian ──
    private int _kleinIter;
    public int KleinianIterations { get => _kleinIter; set { Set(ref _kleinIter, (int)Clamp(value, 2, 64)); _p.KleinianIterations = _kleinIter; Fire(); } }
    private double _kleinScale;
    public double KleinianSphereScale { get => _kleinScale; set { Set(ref _kleinScale, Clamp(value, 0.25, 4.0)); _p.KleinianSphereScale = _kleinScale; Fire(); } }
    private double _kleinCameraTheta;
    public double KleinianCameraTheta { get => _kleinCameraTheta; set { Set(ref _kleinCameraTheta, Clamp(value, -10, 10)); _p.KleinianCameraTheta = _kleinCameraTheta; Fire(); } }
    private double _kleinCameraPhi;
    public double KleinianCameraPhi { get => _kleinCameraPhi; set { Set(ref _kleinCameraPhi, Clamp(value, 0.01, 3.13)); _p.KleinianCameraPhi = _kleinCameraPhi; Fire(); } }
    private double _kleinCameraDistance;
    public double KleinianCameraDistance { get => _kleinCameraDistance; set { Set(ref _kleinCameraDistance, Clamp(value, 0.1, 500)); _p.KleinianCameraDistance = _kleinCameraDistance; Fire(); } }

    // ── Flame ──
    private string _flamePresetName = "Sierpinski Variation";
    public string FlamePresetName { get => _flamePresetName; set { if (Set(ref _flamePresetName, value ?? "Sierpinski Variation")) { _p.FlamePresetName = _flamePresetName; _p.FlameMaps = null; Fire(); } } }
    private int _flameIterations;
    public int FlameIterations { get => _flameIterations; set { Set(ref _flameIterations, (int)Clamp(value, 100_000, 100_000_000)); _p.FlameIterations = _flameIterations; Fire(); } }
    private double _flameGamma;
    public double FlameGamma { get => _flameGamma; set { Set(ref _flameGamma, Clamp(value, 0.5, 5.0)); _p.FlameGamma = _flameGamma; Fire(); } }
    private double _flameVibrancy;
    public double FlameVibrancy { get => _flameVibrancy; set { Set(ref _flameVibrancy, Clamp(value, 0.0, 1.0)); _p.FlameVibrancy = _flameVibrancy; Fire(); } }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>
    /// Live param-changed event mirroring the WinForms dialog. Host wires this
    /// to a re-render trigger; <see cref="FractalParameters"/> is mutated in
    /// place so the host only needs to refresh, not copy.
    /// </summary>
    public event Action? ParamChanged;

    /// <summary>Raised when the Close button is clicked.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when "Export Mesh…" is clicked for a mesh-exportable 3D
    /// raymarcher (#101). The host picks a path + runs the marching-cubes export
    /// off <see cref="FractalType"/> + <see cref="FractalParameters"/>.</summary>
    public event Action? ExportMeshRequested;

    /// <summary>True for the DE raymarchers the shared mesh exporter can build a
    /// sampler for (UserBulb has its own editor export path, so it's excluded).</summary>
    public bool CanExportMesh =>
        IsMandelbulb || IsMandelbox || IsKifs
        || IsQuatJulia || IsQuatMandelbrot
        || IsKleinian || IsBicomplexMandelbrot;

    public ReactiveCommand<Unit, Unit> ExportMeshCommand { get; }
    public ReactiveCommand<Unit, Unit> PickDropColorCommand { get; }

    /// <summary>#138 — export the Oblique 3D heightfield object as a mesh. Host
    /// pulls the active calculator's height + albedo and writes OBJ/STL.</summary>
    public event Action? ExportReliefMeshRequested;
    public ReactiveCommand<Unit, Unit> ExportReliefMeshCommand { get; }

    private void Fire()
    {
        if (_suppress) return;
        ParamChanged?.Invoke();
    }

    // NOTE: forward the caller property name. Without it CallerMemberName
    // resolves to "Set" here, so every VM-initiated PropertyChanged fired under
    // the wrong name — value bindings survived (the control pushes its own
    // value) but dependent IsEnabled / IsVisible bindings never refreshed (e.g.
    // the Domain-warp toggle enabling Strength/Frequency, AcidWarp Morph→Flow).
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        this.RaiseAndSetIfChanged(ref field, value, propertyName);
        return true;
    }

    private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
}
