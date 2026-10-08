// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ControlCenterViewModel.Finder.cs
//
// Explore ▸ Find group — interesting-location finder (epic #1184).
//   S1 (#1185) "Detect period": the ball scan's lowest period candidate in the
//              view (a lower bound; read-only).
//   S2 (#1186) "Zoom to minibrot": find a minibrot whose nucleus is really in
//              the view (Abstractions/Explore/NucleusFinder.FindMinibrot) and
//              frame it the way the home view frames the whole set.
//   S4 (#1188) "Go to angle": parse an external angle (.(011), .01(10), 1/7),
//              show its internal address, trace its parameter ray inward and
//              land on the minibrot / Misiurewicz point it ends at
//              (ExternalAngle, ExternalRay.Land).
//   S5 (#1189) "Julia morph": each click finds a minibrot near the target and
//              steps to the log-midpoint depth toward it (JuliaMorph), where
//              its embedded Julia set doubles the pattern; clicks stack layers.
//              The morph path is plain navigation, so the existing Video zoom /
//              --video-motion zoom into the final view renders it.
//   S7 (#1191) Multibrot, Burning Ship, Tricorn and User Equation get Zoom to
//              minibrot / Snap to spiral / Julia morph through the R² Newton
//              engine — see ControlCenterViewModel.General.cs.
//   S6 (#1190) "Surprise me" / "Descend": heuristic auto-explore for every 2D
//              family — see ControlCenterViewModel.Explore.cs.
//   S3 (#1187) "Snap to spiral": arm a one-shot click on the render
//              (IFractalInputController.PointPickHandler); the click is
//              snapped to the simplest Misiurewicz point within reach
//              (MisiurewiczFinder.FindNear) and the view recentres on it.
// Every action only moves centre / zoom / quality / first-render iterations,
// all of which the --batch Command builder already captures from the live view.

using System;
using System.Globalization;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

using ReactiveUI;

using FracturingFog.Abstractions.Explore;
using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class ControlCenterViewModel
{
    private CancellationTokenSource? _finderCts;
    private string _finderStatus = "Find minibrots and spirals in view (Mandelbrot, Multibrot, Burning Ship, Tricorn, User Equation).";
    private bool _isFinderBusy;
    private bool _isPickArmed;

    /// <summary>Snap-to-spiral reach around the click, in screen pixels.</summary>
    public const int SnapPickRadiusPx = 48;

    public ReactiveCommand<Unit, Unit> DetectPeriodCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ZoomToMinibrotCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SnapToSpiralCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> GoToAngleCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> JuliaMorphCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> UndoMorphStepCommand { get; private set; } = null!;

    /// <summary>Morph target reach around the click, in screen pixels.</summary>
    public const int MorphPickRadiusPx = 48;

    // One entry per morph layer: its path line and the first-render iteration
    // hint it was applied with (restored when Undo returns to that layer —
    // Back itself clears the hint).
    private readonly System.Collections.Generic.List<(string Line, int Iterations)> _morphSteps = new();
    private bool _isMorphing;
    private string _morphPathText = "";

    /// <summary>True while Julia morph keeps re-arming the click after each step.</summary>
    public bool IsMorphing
    {
        get => _isMorphing;
        private set => this.RaiseAndSetIfChanged(ref _isMorphing, value);
    }

    /// <summary>The morph path so far, one line per step.</summary>
    public string MorphPathText
    {
        get => _morphPathText;
        private set => this.RaiseAndSetIfChanged(ref _morphPathText, value);
    }

    public bool HasMorphSteps => _morphSteps.Count > 0;

    private string _angleText = "";
    private string _angleInfo = "e.g. .(001)  .0(01)  .01(10)  1/7";

    /// <summary>External angle typed in the Find group.</summary>
    public string AngleText
    {
        get => _angleText;
        set
        {
            this.RaiseAndSetIfChanged(ref _angleText, value);
            AngleInfo = DescribeAngle(value);
        }
    }

    /// <summary>Live parse of <see cref="AngleText"/>: exact form, fraction,
    /// internal address — or why it does not parse.</summary>
    public string AngleInfo
    {
        get => _angleInfo;
        private set => this.RaiseAndSetIfChanged(ref _angleInfo, value);
    }
    public ReactiveCommand<Unit, Unit> CancelFinderCommand { get; private set; } = null!;

    /// <summary>Readout line under the Find buttons.</summary>
    public string FinderStatus
    {
        get => _finderStatus;
        private set => this.RaiseAndSetIfChanged(ref _finderStatus, value);
    }

    public bool IsFinderBusy
    {
        get => _isFinderBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isFinderBusy, value);
            this.RaisePropertyChanged(nameof(CanCancelFinder));
        }
    }

    /// <summary>True while Snap to spiral waits for a click on the render.</summary>
    public bool IsPickArmed
    {
        get => _isPickArmed;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPickArmed, value);
            this.RaisePropertyChanged(nameof(CanCancelFinder));
        }
    }

    /// <summary>Cancel is offered while a search runs or a click is armed.</summary>
    public bool CanCancelFinder => IsFinderBusy || IsPickArmed;

    /// <summary>The octuple-double finders (and Detect period / angles) model
    /// f_c(z) = z² + c only; Multibrot, Burning Ship, Tricorn and User Equation
    /// take the general track (S7 #1191); the rest Auto-explore only.</summary>
    private static bool FinderSupports(FractalType t)
        => OrbitMaps.TrackFor(t) == FinderTrack.Mandelbrot;

    private static string NoFinder(FractalType t)
        => $"No exact finder for {t} — use Auto-explore below.";

    private void InitFinder()
    {
        var idle = this.WhenAnyValue(x => x.IsFinderBusy, busy => !busy);
        DetectPeriodCommand   = ReactiveCommand.CreateFromTask(DetectPeriodAsync, idle);
        ZoomToMinibrotCommand = ReactiveCommand.CreateFromTask(ZoomToMinibrotAsync, idle);
        SnapToSpiralCommand   = ReactiveCommand.Create(ArmSnapToSpiral, idle);
        GoToAngleCommand      = ReactiveCommand.CreateFromTask(GoToAngleAsync, idle);
        JuliaMorphCommand     = ReactiveCommand.Create(ArmJuliaMorph, idle);
        UndoMorphStepCommand  = ReactiveCommand.Create(UndoMorphStep, idle);
        InitExplore();
        CancelFinderCommand   = ReactiveCommand.Create(() =>
        {
            _finderCts?.Cancel();
            if (IsPickArmed || IsMorphing)
            {
                IsMorphing = false;
                DisarmPick();
                FinderStatus = HasMorphSteps ? "Julia morph stopped. Video ▸ zoom into this view renders the morph path." : "Cancelled.";
            }
        });
    }

    // Snapshot of the live view taken on the UI thread; finders run off it.
    private readonly record struct FinderView(FFMath.DeepComplex Centre, double Zoom, double Radius);

    private bool TryFinderView(out FinderView view)
    {
        var vs = Shell.Main.ViewState;
        if (!FinderSupports(vs.FractalType))
        {
            FinderStatus = $"Detect period applies to z² + c (Mandelbrot) only — not {vs.FractalType}.";
            view = default;
            return false;
        }
        var (w, h) = Shell.Main.RenderHost.LastPresentedSize;
        view = new FinderView(vs.GetCenter(), vs.Zoom, PeriodDetector.ViewDiskRadius(vs.Zoom, w, h));
        return true;
    }

    // Runs `work` off the UI thread with a fresh cancellation token; busy state,
    // cancellation and the "Cancelled." readout are handled here.
    private async Task RunFinderAsync(Func<CancellationToken, Task> work)
    {
        _finderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _finderCts = cts;
        IsFinderBusy = true;
        try { await work(cts.Token); }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_finderCts, cts)) FinderStatus = "Cancelled.";
        }
        finally
        {
            if (ReferenceEquals(_finderCts, cts))
            {
                IsFinderBusy = false;
                _finderCts = null;
            }
            cts.Dispose();
        }
    }

    private Task DetectPeriodAsync()
    {
        if (!TryFinderView(out var v)) return Task.CompletedTask;
        FinderStatus = "Scanning…";
        return RunFinderAsync(async ct =>
        {
            var d = await Task.Run(() => PeriodDetector.Detect(v.Centre, v.Radius, ct: ct), ct);
            string z = Fmt(v.Zoom);
            FinderStatus = d.Stop switch
            {
                PeriodStop.Found        => $"Lowest period candidate in view: {d.Period} (zoom {z}). Zoom to minibrot finds the real one.",
                PeriodStop.Escaped      => $"No minibrot in view — the whole view escapes (zoom {z}).",
                PeriodStop.BallTooLarge => $"No period found within {d.Iterations} iterations (zoom {z}). Zoom in and retry.",
                _                       => $"No period up to {PeriodDetector.DefaultMaxPeriod:N0} (zoom {z}).",
            };
        });
    }

    private Task ZoomToMinibrotAsync()
    {
        var type = Shell.Main.ViewState.FractalType;
        if (TrackOf(type) == FinderTrack.General) return GeneralZoomToMinibrotAsync();
        if (TrackOf(type) == FinderTrack.None) { FinderStatus = NoFinder(type); return Task.CompletedTask; }
        if (!TryFinderView(out var v)) return Task.CompletedTask;
        FinderStatus = "Searching for a minibrot…";
        return RunFinderAsync(async ct =>
        {
            // Progress: the period being tried, throttled to the UI thread.
            int lastShown = 0;
            void OnPeriod(int p)
            {
                if (p - lastShown < 16 && lastShown != 0) return;
                lastShown = p;
                Dispatcher.UIThread.Post(() => { if (IsFinderBusy) FinderStatus = $"Searching… trying period {p}"; });
            }

            var found = await Task.Run(() => NucleusFinder.FindMinibrot(v.Centre, v.Radius, onPeriod: OnPeriod, ct: ct), ct);
            if (!found.Found)
            {
                FinderStatus = found.Status == NucleusStatus.PrecisionLimit
                    ? "Too deep: beyond octuple-double precision."
                    : $"No minibrot found in view (zoom {Fmt(v.Zoom)}). Pan or zoom out and retry.";
                return;
            }
            ApplyMinibrotJump(found);
        });
    }

    private void ApplyMinibrotJump(in NucleusResult found, string prefix = "")
    {
        var main = Shell.Main;
        var vs = main.ViewState;
        var plan = MinibrotJump.Plan(found, vs.Quality, vs.IterLocked, vs.LockedIterations);
        if (plan is not MinibrotJumpPlan p)
        {
            FinderStatus = $"{prefix}Period {found.Period} minibrot is beyond the deepest zoom (1e100).";
            return;
        }

        string note = ApplyPlan(p);
        FinderStatus = $"{prefix}Period {found.Period} minibrot, size {found.Size.Magnitude:G3} → zoom {Fmt(p.Zoom)} ({p.Quality.Name}).{note}";
    }

    // Apply a planned view change (nav history, centre, zoom, tier promotion,
    // first-render iteration hint) and return an iterations note, if any.
    private string ApplyPlan(in MinibrotJumpPlan p)
    {
        var main = Shell.Main;
        var vs = main.ViewState;
        Shell.RecordNavChange();
        vs.SetCenter(p.Center);
        vs.Zoom = p.Zoom;
        if (!ReferenceEquals(p.Quality, vs.Quality))
        {
            vs.Quality = p.Quality;
            main.SetQualitySilent(p.Quality);
            Menu.SetQualitySilent(p.Quality.Name);
        }
        if (!vs.IterLocked) vs.PreferredIterations = p.PreferredIterations;
        main.RenderHost.Trigger();

        return !p.IterationsShort ? ""
            : vs.IterLocked ? $" Iterations are locked at {vs.LockedIterations:N0}; ~{p.WantedIterations:N0} resolve it."
            : $" ~{p.WantedIterations:N0} iterations resolve it; using {p.PreferredIterations:N0}.";
    }

    private void ArmSnapToSpiral()
    {
        var vs = Shell.Main.ViewState;
        if (TrackOf(vs.FractalType) == FinderTrack.None)
        {
            FinderStatus = NoFinder(vs.FractalType);
            return;
        }
        Shell.Main.Input.PointPickHandler = OnSnapPick;
        IsPickArmed = true;
        FinderStatus = "Click a spiral centre or branch point in the render…";
    }

    private void DisarmPick()
    {
        if (Shell.Main.Input.PointPickHandler != null) Shell.Main.Input.PointPickHandler = null;
        IsPickArmed = false;
    }

    // The armed click: world point + reach in world units, then the search.
    // Runs on the UI thread (input callback); the search goes async.
    private bool OnSnapPick(FracturingFog.Input.PointerInput e)
    {
        IsPickArmed = false;   // the controller has already cleared its one-shot hook
        var vs = Shell.Main.ViewState;
        var track = TrackOf(vs.FractalType);
        if (track == FinderTrack.None)
        {
            // The type changed while armed: let the press act normally.
            FinderStatus = NoFinder(vs.FractalType);
            return false;
        }
        var camera = new FracturingFog.ViewState.ViewCamera(vs);
        var click = camera.WorldFromScreen(e.X, e.Y, e.ClientWidth, e.ClientHeight);
        double reach = SnapPickRadiusPx * camera.Scale(e.ClientWidth, e.ClientHeight);
        if (track == FinderTrack.General)
        {
            var map = BuildOrbitMap(out string why);
            if (map == null) { FinderStatus = why; return true; }
            GeneralSnap(map, ToV2(click), reach);
            return true;
        }
        FinderStatus = "Searching for a spiral / branch point…";
        _ = RunFinderAsync(async ct =>
        {
            var m = await Task.Run(() => MisiurewiczFinder.FindNear(click, reach, ct: ct), ct);
            if (!m.Found)
            {
                FinderStatus = m.Status == MisiurewiczStatus.PrecisionLimit
                    ? "Too deep: beyond octuple-double precision."
                    : $"No spiral / branch point within {SnapPickRadiusPx} px of the click. Click nearer its centre, or zoom in.";
                return;
            }
            Shell.RecordNavChange();
            vs.SetCenter(m.Point);
            Shell.Main.RenderHost.Trigger();
            double turn = m.Multiplier.Phase * 180.0 / System.Math.PI;
            FinderStatus = $"Misiurewicz point M({m.Preperiod},{m.Period}): the pattern repeats every ×{Fmt(m.Multiplier.Magnitude)} zoom, turning {turn:0.#}°.";
        });
        return true;
    }

    // ── S4: external angles ──────────────────────────────────────────────

    private static string DescribeAngle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "e.g. .(001)  .0(01)  .01(10)  1/7";
        if (!ExternalAngle.TryParse(text, out var a, out var err)) return err ?? "Not an angle.";
        var (n, d) = a!.Fraction;
        string frac = d.ToString().Length <= 24 ? $" = {n}/{d}" : "";
        string kind = a.IsPeriodic
            ? $"periodic, period {a.Period}: lands on a minibrot's root"
            : $"preperiod {a.Preperiod}, period {a.Period}: lands on a Misiurewicz point";
        var address = a.InternalAddress(maxTerms: 12);
        string addr = string.Join("→", address) + (a.IsPeriodic || address.Count < 12 ? "" : "→…");
        return $"{a}{frac} — {kind}. Internal address {addr}.";
    }

    private Task GoToAngleAsync()
    {
        var vs = Shell.Main.ViewState;
        if (!FinderSupports(vs.FractalType))
        {
            FinderStatus = $"External angles apply to z² + c (Mandelbrot) only — not {vs.FractalType}.";
            return Task.CompletedTask;
        }
        if (!ExternalAngle.TryParse(AngleText, out var angle, out var err))
        {
            FinderStatus = err ?? "Not an angle.";
            return Task.CompletedTask;
        }
        FinderStatus = $"Tracing the ray at {angle}…";
        return RunFinderAsync(async ct =>
        {
            var landing = await Task.Run(() => ExternalRay.Land(angle!, ct: ct), ct);
            string prefix = $"Ray {angle} → ";
            switch (landing.Status)
            {
                case RayLandingStatus.Component:
                    ApplyMinibrotJump(landing.Nucleus, prefix);
                    break;
                case RayLandingStatus.Misiurewicz:
                    ApplyMisiurewiczLanding(landing, prefix);
                    break;
                default:
                    FinderStatus = $"{prefix}{landing.Detail ?? "could not land the ray."}";
                    break;
            }
        });
    }

    // Centre on the Misiurewicz point, zoomed so the view radius is the ray's
    // context radius (the hub plus its first ring of structure).
    private void ApplyMisiurewiczLanding(in RayLanding landing, string prefix)
    {
        var main = Shell.Main;
        var vs = main.ViewState;
        var m = landing.Misiurewicz;
        var (w, h) = main.RenderHost.LastPresentedSize;
        double unitRadius = PeriodDetector.ViewDiskRadius(1.0, w, h);   // view radius at zoom 1
        double zoom = unitRadius / landing.ContextRadius;
        if (!(zoom <= QualityPreset.Extreme.ZoomMax))
        {
            FinderStatus = $"{prefix}M({m.Preperiod},{m.Period}) is beyond the deepest zoom (1e100).";
            return;
        }
        Shell.RecordNavChange();
        vs.SetCenter(m.Point);
        vs.Zoom = zoom;
        var q = MinibrotJump.QualityFor(vs.Quality, zoom);
        if (!ReferenceEquals(q, vs.Quality))
        {
            vs.Quality = q;
            main.SetQualitySilent(q);
            Menu.SetQualitySilent(q.Name);
        }
        main.RenderHost.Trigger();
        double turn = m.Multiplier.Phase * 180.0 / System.Math.PI;
        FinderStatus = $"{prefix}Misiurewicz point M({m.Preperiod},{m.Period}), zoom {Fmt(zoom)}: the pattern repeats every ×{Fmt(m.Multiplier.Magnitude)} zoom, turning {turn:0.#}°.";
    }

    // ── S5: Julia morph ──────────────────────────────────────────────────

    private void ArmJuliaMorph()
    {
        var vs = Shell.Main.ViewState;
        if (TrackOf(vs.FractalType) == FinderTrack.None)
        {
            FinderStatus = NoFinder(vs.FractalType);
            return;
        }
        IsMorphing = true;
        Shell.Main.Input.PointPickHandler = OnMorphPick;
        IsPickArmed = true;
        FinderStatus = HasMorphSteps
            ? "Julia morph: click the next target to add a layer (Cancel to stop)."
            : "Julia morph: click a point in the pattern to double it around that point (Cancel to stop).";
    }

    private void RearmMorph()
    {
        if (!IsMorphing) return;
        Shell.Main.Input.PointPickHandler = OnMorphPick;
        IsPickArmed = true;
    }

    private bool OnMorphPick(FracturingFog.Input.PointerInput e)
    {
        IsPickArmed = false;   // one-shot hook already cleared by the controller
        var vs = Shell.Main.ViewState;
        var track = TrackOf(vs.FractalType);
        if (track == FinderTrack.None)
        {
            IsMorphing = false;
            FinderStatus = NoFinder(vs.FractalType);
            return false;
        }
        IOrbitMap? map = null;
        if (track == FinderTrack.General)
        {
            map = BuildOrbitMap(out string why);
            if (map == null) { IsMorphing = false; FinderStatus = why; return true; }
        }
        var camera = new FracturingFog.ViewState.ViewCamera(vs);
        var target = camera.WorldFromScreen(e.X, e.Y, e.ClientWidth, e.ClientHeight);
        double reach = MorphPickRadiusPx * camera.Scale(e.ClientWidth, e.ClientHeight);
        double zoom = vs.Zoom;
        FinderStatus = "Julia morph: searching near the target…";
        _ = RunFinderAsync(async ct =>
        {
            try
            {
                // The target minibrot: nucleus, period and the zoom framing it.
                var (found, nucleus, period, minibrotZoom) = map == null
                    ? await Task.Run(() =>
                    {
                        var q = NucleusFinder.FindMinibrot(target, reach, ct: ct);
                        return (q.Found, q.Nucleus, q.Period, q.Found ? 1.0 / q.Size.Magnitude : 0.0);
                    }, ct)
                    : await Task.Run(() =>
                    {
                        var g = GeneralFinder.FindMinibrot(map, ToV2(target), reach, ct: ct);
                        return (g.Found && g.Zoom > 0, ToDeep(g.Point), g.Period, g.Zoom);
                    }, ct);
                if (!found)
                {
                    FinderStatus = $"No minibrot within {MorphPickRadiusPx} px of that point — click elsewhere in the pattern.";
                    return;
                }
                double maxZoom = map == null ? QualityPreset.Extreme.ZoomMax : AutoExplorer.DoubleMaxZoom;
                var step = JuliaMorph.Plan(nucleus, period, minibrotZoom, zoom, vs.Quality, vs.IterLocked, vs.LockedIterations,
                                           maxZoom: maxZoom);
                if (!step.Ok)
                {
                    FinderStatus = step.Refusal == JuliaMorphRefusal.NotDeeper
                        ? $"The period-{period} minibrot there is not deeper than this view — click further out in the pattern."
                        : $"That step would pass the deepest zoom ({Fmt(maxZoom)}).";
                    return;
                }
                string note = ApplyPlan(step.Plan);
                _morphSteps.Add(($"{_morphSteps.Count + 1}. period {period} → zoom {Fmt(step.Plan.Zoom)}", step.Plan.PreferredIterations));
                MorphPathText = MorphPathLines();
                this.RaisePropertyChanged(nameof(HasMorphSteps));
                FinderStatus = $"Morph layer {_morphSteps.Count}: toward the period-{period} minibrot, zoom {Fmt(step.Plan.Zoom)} ({step.Plan.Quality.Name}). Click the next target.{note}";
            }
            finally
            {
                // Re-arm after the search settles (also after a miss), unless
                // the user cancelled meanwhile.
                Dispatcher.UIThread.Post(RearmMorph);
            }
        });
        return true;
    }

    private void UndoMorphStep()
    {
        if (_morphSteps.Count == 0) return;
        if (!Shell.GoBack()) return;
        _morphSteps.RemoveAt(_morphSteps.Count - 1);
        MorphPathText = MorphPathLines();
        // Back cleared the first-render hint; the layer we returned to needs
        // its own iteration count to stay resolved.
        var vs = Shell.Main.ViewState;
        if (_morphSteps.Count > 0 && !vs.IterLocked && _morphSteps[^1].Iterations > 0)
        {
            vs.PreferredIterations = _morphSteps[^1].Iterations;
            Shell.Main.RenderHost.Trigger();
        }
        this.RaisePropertyChanged(nameof(HasMorphSteps));
        FinderStatus = _morphSteps.Count > 0 ? $"Undid layer {_morphSteps.Count + 1}." : "Undid the first layer.";
    }

    private string MorphPathLines()
        => string.Join("\n", System.Linq.Enumerable.Select(_morphSteps, m => m.Line));

    private static string Fmt(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
