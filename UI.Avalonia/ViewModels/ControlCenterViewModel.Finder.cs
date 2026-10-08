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
    private string _finderStatus = "Find minibrots in view (z² + c).";
    private bool _isFinderBusy;
    private bool _isPickArmed;

    /// <summary>Snap-to-spiral reach around the click, in screen pixels.</summary>
    public const int SnapPickRadiusPx = 48;

    public ReactiveCommand<Unit, Unit> DetectPeriodCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ZoomToMinibrotCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SnapToSpiralCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> GoToAngleCommand { get; private set; } = null!;

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

    /// <summary>The exact-track finders model f_c(z) = z² + c only; other
    /// families get the heuristic finder (S6 #1190) instead.</summary>
    private static bool FinderSupports(FractalType t)
        => t is FractalType.Mandelbrot or FractalType.GeneratedMandelbrotZ2;

    private void InitFinder()
    {
        var idle = this.WhenAnyValue(x => x.IsFinderBusy, busy => !busy);
        DetectPeriodCommand   = ReactiveCommand.CreateFromTask(DetectPeriodAsync, idle);
        ZoomToMinibrotCommand = ReactiveCommand.CreateFromTask(ZoomToMinibrotAsync, idle);
        SnapToSpiralCommand   = ReactiveCommand.Create(ArmSnapToSpiral, idle);
        GoToAngleCommand      = ReactiveCommand.CreateFromTask(GoToAngleAsync, idle);
        CancelFinderCommand   = ReactiveCommand.Create(() =>
        {
            _finderCts?.Cancel();
            if (IsPickArmed) { DisarmPick(); FinderStatus = "Cancelled."; }
        });
    }

    // Snapshot of the live view taken on the UI thread; finders run off it.
    private readonly record struct FinderView(FFMath.DeepComplex Centre, double Zoom, double Radius);

    private bool TryFinderView(out FinderView view)
    {
        var vs = Shell.Main.ViewState;
        if (!FinderSupports(vs.FractalType))
        {
            FinderStatus = $"The minibrot finder supports z² + c (Mandelbrot) only — not {vs.FractalType}.";
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

        string note = !p.IterationsShort ? ""
            : vs.IterLocked ? $" Iterations are locked at {vs.LockedIterations:N0}; ~{p.WantedIterations:N0} resolve it."
            : $" ~{p.WantedIterations:N0} iterations resolve it; using {p.PreferredIterations:N0}.";
        FinderStatus = $"{prefix}Period {found.Period} minibrot, size {found.Size.Magnitude:G3} → zoom {Fmt(p.Zoom)} ({p.Quality.Name}).{note}";
    }

    private void ArmSnapToSpiral()
    {
        var vs = Shell.Main.ViewState;
        if (!FinderSupports(vs.FractalType))
        {
            FinderStatus = $"The spiral finder supports z² + c (Mandelbrot) only — not {vs.FractalType}.";
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
        if (!FinderSupports(vs.FractalType))
        {
            // The type changed while armed: let the press act normally.
            FinderStatus = $"The spiral finder supports z² + c (Mandelbrot) only — not {vs.FractalType}.";
            return false;
        }
        var camera = new FracturingFog.ViewState.ViewCamera(vs);
        var click = camera.WorldFromScreen(e.X, e.Y, e.ClientWidth, e.ClientHeight);
        double reach = SnapPickRadiusPx * camera.Scale(e.ClientWidth, e.ClientHeight);
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

    private static string Fmt(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
