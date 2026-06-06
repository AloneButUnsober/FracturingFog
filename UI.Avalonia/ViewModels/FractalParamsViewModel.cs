using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reactive;
using FracturingFog;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// Avalonia port of the legacy WinForms <c>FractalParamsDialog</c>.
/// Wraps an existing <see cref="FractalParameters"/> instance + the active
/// <see cref="FractalType"/> and exposes per-type observable properties + a
/// set of section-visibility flags so a single .axaml can render the right
/// sub-set of controls without a code-behind switch.
/// </summary>
public sealed class FractalParamsViewModel : ViewModelBase
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
        Func<string, (double a, double b, double c, double d)>? attractorDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        FractalType = type;
        _p = parameters;
        _attractorDefaults = attractorDefaults;

        IfsPresets = ifsPresets ?? Array.Empty<string>();
        LSystemPresets = lsystemPresets ?? Array.Empty<string>();
        AttractorPresets = attractorPresets ?? new[] { "Clifford", "De Jong", "Hopalong", "Lorenz" };

        _juliaR = _p.JuliaC.Real;
        _juliaI = _p.JuliaC.Imaginary;
        _multibrotD = _p.MultibrotExponent;
        _phoenixR = _p.PhoenixP.Real;
        _phoenixI = _p.PhoenixP.Imaginary;
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
        _bulbPower = _p.BulbPower;
        _bulbIterations = _p.BulbIterations;
        _bulbCameraTheta = _p.BulbCameraTheta;
        _bulbCameraPhi = _p.BulbCameraPhi;
        _bulbCameraDistance = _p.BulbCameraDistance;

        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    public FractalType FractalType { get; }
    public IReadOnlyList<string> IfsPresets { get; }
    public IReadOnlyList<string> LSystemPresets { get; }
    public IReadOnlyList<string> AttractorPresets { get; }

    public string Title => $"{FractalType} Parameters";
    public string EmptyStateText => $"{FractalType} has no tunable parameters.";

    public bool IsJulia => FractalType == FractalType.Julia;
    public bool IsMultibrot => FractalType == FractalType.Multibrot;
    public bool IsPhoenix => FractalType == FractalType.Phoenix;
    public bool IsNewtonOrNova => FractalType is FractalType.Newton or FractalType.Nova;
    public bool IsIFS => FractalType == FractalType.IFS;
    public bool IsLSystem => FractalType == FractalType.LSystem;
    public bool IsStrangeAttractor => FractalType == FractalType.StrangeAttractor;
    public bool IsBuddhaBrot => FractalType == FractalType.BuddhaBrot;
    public bool IsMandelbulb => FractalType == FractalType.Mandelbulb;
    public bool HasNoParams =>
        !(IsJulia || IsMultibrot || IsPhoenix || IsNewtonOrNova || IsIFS
          || IsLSystem || IsStrangeAttractor || IsBuddhaBrot || IsMandelbulb);

    // ── Julia ──
    private double _juliaR;
    public double JuliaR { get => _juliaR; set { Set(ref _juliaR, Clamp(value, -2, 2)); _p.JuliaC = new Complex(_juliaR, _juliaI); Fire(); } }
    private double _juliaI;
    public double JuliaI { get => _juliaI; set { Set(ref _juliaI, Clamp(value, -2, 2)); _p.JuliaC = new Complex(_juliaR, _juliaI); Fire(); } }

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
    public double BulbCameraDistance { get => _bulbCameraDistance; set { Set(ref _bulbCameraDistance, Clamp(value, 1.5, 10)); _p.BulbCameraDistance = _bulbCameraDistance; Fire(); } }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>
    /// Live param-changed event mirroring the WinForms dialog. Host wires this
    /// to a re-render trigger; <see cref="FractalParameters"/> is mutated in
    /// place so the host only needs to refresh, not copy.
    /// </summary>
    public event Action? ParamChanged;

    /// <summary>Raised when the Close button is clicked.</summary>
    public event EventHandler? CloseRequested;

    private void Fire()
    {
        if (_suppress) return;
        ParamChanged?.Invoke();
    }

    private bool Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        this.RaiseAndSetIfChanged(ref field, value);
        return true;
    }

    private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
}
