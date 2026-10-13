// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ControlCenterViewModel.General.cs
//
// Explore ▸ Find for the families beyond z² + c — finder S7 (#1191).
// Multibrot (and the Generated z³/z⁴/z⁵), Burning Ship, Tricorn and
// autonomous User Equations get Zoom to minibrot, Snap to spiral and Julia
// morph through the double-precision R² Newton engine (GeneralFinder over an
// IOrbitMap). Detect period stays z² + c only. Like the Mandelbrot finders
// these only move centre / zoom / quality / first-render iterations, which the
// --batch Command builder already captures.
//
// #1242 adds: Go to angle for z^d + c with base-d angles (BaseAngle,
// MultibrotRay), and the critical points of a User Equation, one of which can
// be applied as the z₀ seed (CriticalPoints). The seed changes the render; it
// reaches the batch through the region snapshot (--param UserEquationSeed=…).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

using ReactiveUI;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class ControlCenterViewModel
{
    private FinderTrack TrackOf(FractalType t) => OrbitMaps.TrackFor(t);

    /// <summary>The current family's map for the general finders, built on the
    /// UI thread from a snapshot of its parameters. Null (with the reason) when
    /// the family has none, the User Equation does not compile or depends on
    /// n / prev, or the view is past double precision.</summary>
    private IOrbitMap? BuildOrbitMap(out string why)
    {
        var vs = Shell.Main.ViewState;
        why = "";
        if (vs.Zoom > AutoExplorer.DoubleMaxZoom)
        {
            why = $"{vs.FractalType} renders in double precision; the finders stop at zoom 1e13.";
            return null;
        }
        var fp = vs.FractalParameters;
        if (vs.FractalType != FractalType.UserEquation)
        {
            var map = OrbitMaps.For(vs.FractalType, fp);
            if (map == null) why = $"No finder for {vs.FractalType} — use Auto-explore.";
            return map;
        }

        var step = ParseUserStep(fp, out why);
        if (step == null) return null;
        if (!UserEquationMap.IsAutonomous(step))
        {
            why = "This equation depends on n / iter / prev, so it has no fixed minibrots — use Auto-explore.";
            return null;
        }
        try
        {
            string seedSrc = fp.UserEquationSeed?.Trim() ?? "";
            var seed = seedSrc.Length > 0 ? SandboxExpression.Parse(seedSrc) : null;
            return new UserEquationMap(step, seed, fp.EscapeRadius, fp.UserEquationSource!.Trim());
        }
        catch (Exception ex)
        {
            why = $"The z₀ seed does not compile: {ex.Message}";
            return null;
        }
    }

    // Parses the User Equation's step the way the calculator compiles it.
    private static SandboxExpression? ParseUserStep(FractalParameters fp, out string why)
    {
        why = "";
        string src = fp.UserEquationSource ?? "";
        if (string.IsNullOrWhiteSpace(src)) { why = "The User Equation has no source."; return null; }
        try
        {
            string dsl = FracturingFog.CalculatorGen.EquationPreprocessor.Preprocess(src, out FracturingFog.CalculatorGen.PreprocessDiagnostic? diag);
            if (diag != null) { why = $"The equation does not compile: {diag.Message}"; return null; }
            return SandboxExpression.Parse(dsl);
        }
        catch (Exception ex)
        {
            why = $"The equation does not compile: {ex.Message}";
            return null;
        }
    }

    private static V2 ToV2(in DeepComplex c) => new(c.Re.X0, c.Im.X0);
    private static DeepComplex ToDeep(V2 v) => new(v.X, v.Y);

    // S2 for the general families.
    private Task GeneralZoomToMinibrotAsync()
    {
        var map = BuildOrbitMap(out string why);
        if (map == null) { FinderStatus = why; return Task.CompletedTask; }
        var vs = Shell.Main.ViewState;
        var (w, h) = Shell.Main.RenderHost.LastPresentedSize;
        var centre = ToV2(vs.GetCenter());
        double radius = PeriodDetector.ViewDiskRadius(vs.Zoom, w, h);
        FinderStatus = $"Searching for a minibrot ({map.Name})…";
        return RunFinderAsync(async ct =>
        {
            int lastShown = 0;
            void OnPeriod(int p)
            {
                if (p - lastShown < 16 && lastShown != 0) return;
                lastShown = p;
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsFinderBusy) FinderStatus = $"Searching… trying period {p}"; });
            }
            var g = await Task.Run(() => GeneralFinder.FindMinibrot(map, centre, radius, onPeriod: OnPeriod, ct: ct), ct);
            if (!g.Found || !(g.Zoom > 0))
            {
                FinderStatus = $"No minibrot found in view (zoom {Fmt(vs.Zoom)}). Pan or zoom out and retry.";
                return;
            }
            if (g.Zoom > AutoExplorer.DoubleMaxZoom)
            {
                FinderStatus = $"Found a period-{g.Period} minibrot, but framing it needs zoom {Fmt(g.Zoom)} — past double precision (1e13).";
                return;
            }
            var plan = MinibrotJump.At(ToDeep(g.Point), g.Zoom, g.Period, vs.Quality, vs.IterLocked, vs.LockedIterations);
            if (plan is not MinibrotJumpPlan p) return;
            string note = ApplyPlan(p);
            FinderStatus = $"Period {g.Period} minibrot ({map.Name}) → zoom {Fmt(p.Zoom)}.{note}";
        });
    }

    // S3 for the general families: recentre on the Misiurewicz point.
    private void GeneralSnap(IOrbitMap map, V2 click, double reach)
    {
        var vs = Shell.Main.ViewState;
        FinderStatus = $"Searching for a spiral / branch point ({map.Name})…";
        _ = RunFinderAsync(async ct =>
        {
            var m = await Task.Run(() => GeneralFinder.FindNearMisiurewicz(map, click, reach, ct: ct), ct);
            if (!m.Found)
            {
                FinderStatus = $"No spiral / branch point within {SnapPickRadiusPx} px of the click. Click nearer its centre, or zoom in.";
                return;
            }
            Shell.RecordNavChange();
            vs.SetCenter(ToDeep(m.Point));
            Shell.Main.RenderHost.Trigger();
            string turn = m.TurnDegrees is double t ? $", turning {t:0.#}°" : "";
            FinderStatus = $"Misiurewicz point M({m.Preperiod},{m.Period}): the pattern repeats every ×{Fmt(m.Multiplier)} zoom{turn}.";
            // #1235 — a loop when the cycle is conformal (no J_c: z² + c only).
            SetLoopTarget(ToDeep(m.Point), m.Multiplier, m.TurnDegrees, m.Period, juliaPreview: false);
        });
    }

    // ── #1242: base-d external angles for z^d + c ───────────────────────

    // The Multibrot map of the current family, or null (z² + c, other families).
    private MultibrotMap? CurrentMultibrot()
    {
        var vs = Shell.Main.ViewState;
        return TrackOf(vs.FractalType) == FinderTrack.General
            ? OrbitMaps.For(vs.FractalType, vs.FractalParameters) as MultibrotMap
            : null;
    }

    // The angle hint for the current family: binary (with addresses) for z² + c,
    // base d for z^d + c.
    private string DescribeAngleForView(string text)
    {
        var vs = Shell.Main.ViewState;
        if (TrackOf(vs.FractalType) != FinderTrack.General) return DescribeAngle(text);
        if (CurrentMultibrot() is not MultibrotMap mm) return "External angles apply to z² + c and z^d + c (Mandelbrot, Multibrot) only.";
        int d = mm.Degree;
        if (d > BaseAngle.MaxBase) return $"Angles are supported up to degree {BaseAngle.MaxBase}.";
        if (string.IsNullOrWhiteSpace(text)) return $"Base-{d} angle for {mm.Name}, e.g. .(01)  .0(1)  1/{d * d - 1}";
        if (LooksLikeAddress(text)) return $"Internal addresses are z² + c only; enter a base-{d} angle or a fraction p/q.";
        if (!BaseAngle.TryParse(text, d, out var a, out var err)) return err ?? "Not an angle.";
        var (n, q) = a!.Fraction;
        string frac = q.ToString().Length <= 24 ? $" = {n}/{q}" : "";
        string kind = a.IsPeriodic
            ? $"periodic, period {a.Period}: lands on a period-{a.Period} minibrot (root or co-root)"
            : $"preperiod {a.Preperiod}, period {a.Period}: lands on a Misiurewicz point";
        return $"{a} (base {d}){frac} — {kind}.";
    }

    private Task GeneralGoToAngleAsync()
    {
        var vs = Shell.Main.ViewState;
        if (CurrentMultibrot() is not MultibrotMap mm)
        {
            FinderStatus = $"External angles apply to z² + c and z^d + c (Mandelbrot, Multibrot) only — not {vs.FractalType}.";
            return Task.CompletedTask;
        }
        int d = mm.Degree;
        string text = AngleText;
        AngleInfo = DescribeAngleForView(text);
        if (d > BaseAngle.MaxBase) { FinderStatus = AngleInfo; return Task.CompletedTask; }
        if (LooksLikeAddress(text) || !BaseAngle.TryParse(text, d, out var angle, out var err))
        {
            FinderStatus = LooksLikeAddress(text) ? "Internal addresses are z² + c only." : AngleInfo;
            return Task.CompletedTask;
        }
        FinderStatus = $"Tracing the base-{d} ray at {angle}…";
        return RunFinderAsync(async ct =>
        {
            var landing = await Task.Run(() => MultibrotRay.Land(mm, angle!, ct: ct), ct);
            string prefix = $"Ray {angle} (base {d}) → ";
            switch (landing.Status)
            {
                case RayLandingStatus.Component:
                {
                    var g = landing.Nucleus;
                    if (!(g.Zoom > 0) || g.Zoom > AutoExplorer.DoubleMaxZoom)
                    {
                        FinderStatus = $"{prefix}period-{g.Period} minibrot, but framing it needs zoom {Fmt(g.Zoom)} — past double precision (1e13).";
                        return;
                    }
                    var plan = MinibrotJump.At(ToDeep(g.Point), g.Zoom, g.Period, vs.Quality, vs.IterLocked, vs.LockedIterations);
                    if (plan is not MinibrotJumpPlan p) return;
                    string note = ApplyPlan(p);
                    FinderStatus = $"{prefix}period {g.Period} minibrot ({mm.Name}) → zoom {Fmt(p.Zoom)}.{note}";
                    break;
                }
                case RayLandingStatus.Misiurewicz:
                {
                    var m = landing.Misiurewicz;
                    var main = Shell.Main;
                    var (w, h) = main.RenderHost.LastPresentedSize;
                    double zoom = PeriodDetector.ViewDiskRadius(1.0, w, h) / landing.ContextRadius;
                    if (!(zoom > 0) || zoom > AutoExplorer.DoubleMaxZoom)
                    {
                        FinderStatus = $"{prefix}M({m.Preperiod},{m.Period}) needs zoom {Fmt(zoom)} — past double precision (1e13).";
                        return;
                    }
                    Shell.RecordNavChange();
                    vs.SetCenter(ToDeep(m.Point));
                    vs.Zoom = zoom;
                    var q = MinibrotJump.QualityFor(vs.Quality, zoom);
                    if (!ReferenceEquals(q, vs.Quality))
                    {
                        vs.Quality = q;
                        main.SetQualitySilent(q);
                        Menu.SetQualitySilent(q.Name);
                    }
                    main.RenderHost.Trigger();
                    string turn = m.TurnDegrees is double t ? $", turning {t:0.#}°" : "";
                    FinderStatus = $"{prefix}Misiurewicz point M({m.Preperiod},{m.Period}), zoom {Fmt(zoom)}: the pattern repeats every ×{Fmt(m.Multiplier)} zoom{turn}.";
                    SetLoopTarget(ToDeep(m.Point), m.Multiplier, m.TurnDegrees, m.Period, juliaPreview: false);
                    break;
                }
                default:
                    FinderStatus = $"{prefix}{landing.Detail ?? "could not land the ray."}";
                    break;
            }
        });
    }

    // ── #1242: User Equation critical points → z₀ seed ──────────────────

    private IReadOnlyList<CriticalPoint> _criticalPoints = Array.Empty<CriticalPoint>();
    private IReadOnlyList<string> _criticalPointItems = Array.Empty<string>();
    private int _criticalPointIndex = -1;
    private string _criticalPointInfo = "";

    public ReactiveCommand<Unit, Unit> FindCriticalPointsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> UseCriticalPointCommand { get; private set; } = null!;

    /// <summary>True for a User Equation (the critical-point row shows).</summary>
    public bool IsUserEquation => Shell.Main.SelectedFractalType == FractalType.UserEquation;

    public IReadOnlyList<string> CriticalPointItems
    {
        get => _criticalPointItems;
        private set { this.RaiseAndSetIfChanged(ref _criticalPointItems, value); this.RaisePropertyChanged(nameof(HasCriticalPoints)); }
    }

    public bool HasCriticalPoints => _criticalPointItems.Count > 0;

    public int CriticalPointIndex
    {
        get => _criticalPointIndex;
        set { this.RaiseAndSetIfChanged(ref _criticalPointIndex, value); this.RaisePropertyChanged(nameof(CanUseCriticalPoint)); }
    }

    /// <summary>The picked point does not move with c, so a constant seed reproduces it.</summary>
    public bool CanUseCriticalPoint
        => _criticalPointIndex >= 0 && _criticalPointIndex < _criticalPoints.Count && !_criticalPoints[_criticalPointIndex].DependsOnC;

    public string CriticalPointInfo
    {
        get => _criticalPointInfo;
        private set => this.RaiseAndSetIfChanged(ref _criticalPointInfo, value);
    }

    private void InitCriticalPoints()
    {
        FindCriticalPointsCommand = ReactiveCommand.Create(FindCriticalPoints);
        UseCriticalPointCommand = ReactiveCommand.Create(UseCriticalPoint,
            this.WhenAnyValue(x => x.CriticalPointIndex, x => x.CriticalPointItems, (_, _) => CanUseCriticalPoint));
        Shell.Main.WhenAnyValue(m => m.SelectedFractalType).Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(IsUserEquation));
            AngleInfo = DescribeAngleForView(AngleText);
            ClearCriticalPoints();
        });
    }

    private void ClearCriticalPoints()
    {
        _criticalPoints = Array.Empty<CriticalPoint>();
        CriticalPointItems = Array.Empty<string>();
        CriticalPointIndex = -1;
        CriticalPointInfo = "";
    }

    private void FindCriticalPoints()
    {
        ClearCriticalPoints();
        var vs = Shell.Main.ViewState;
        if (vs.FractalType != FractalType.UserEquation) { CriticalPointInfo = "Critical points apply to a User Equation."; return; }
        var step = ParseUserStep(vs.FractalParameters, out string why);
        if (step == null) { CriticalPointInfo = why; return; }
        if (!UserEquationMap.IsAutonomous(step)) { CriticalPointInfo = "This equation depends on n / iter / prev, so its critical points move every step."; return; }
        var c = vs.GetCenter();
        var found = CriticalPoints.Find(step, new System.Numerics.Complex(c.Re.X0, c.Im.X0), out string? err);
        if (found == null) { CriticalPointInfo = err ?? "No critical points."; return; }
        if (found.Count == 0) { CriticalPointInfo = "No critical point found within |z| ≤ 16 (f′ never vanishes there)."; return; }
        _criticalPoints = found;
        CriticalPointItems = found.Select(p =>
            $"z = {p.SeedText}" + (p.Multiplicity > 1 ? $" (×{p.Multiplicity})" : "") + (p.DependsOnC ? " — moves with c" : "")).ToList();
        string current = string.IsNullOrWhiteSpace(vs.FractalParameters.UserEquationSeed) ? "0" : vs.FractalParameters.UserEquationSeed!.Trim();
        int match = -1;
        for (int i = 0; i < found.Count && match < 0; i++)
            if (!found[i].DependsOnC && found[i].SeedText == current) match = i;
        CriticalPointIndex = match >= 0 ? match : found.ToList().FindIndex(p => !p.DependsOnC);
        int moving = found.Count(p => p.DependsOnC);
        CriticalPointInfo = $"{found.Count} critical point{(found.Count == 1 ? "" : "s")}; z₀ is now {current}"
            + (match >= 0 ? " (a critical point)." : " (not a critical point).")
            + (moving > 0 ? $" {moving} move{(moving == 1 ? "s" : "")} with c (value shown at the view centre): write its formula into the User Equation z₀ seed instead." : "");
    }

    private void UseCriticalPoint()
    {
        if (!CanUseCriticalPoint) return;
        var p = _criticalPoints[_criticalPointIndex];
        string seed = p.SeedText;
        string? value = seed == "0" ? null : seed;   // blank = z₀ = 0
        if (Shell.SetUserEquationSeed is { } set) set(value);
        else
        {
            Shell.Main.ViewState.FractalParameters.UserEquationSeed = value;
            Shell.Main.RenderHost.Trigger();
        }
        CriticalPointInfo = $"z₀ = {seed}: the render now shows this critical point's parameter set. The seed rides along in the Command builder (--param UserEquationSeed).";
    }
}
