// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ControlCenterViewModel.Finder.cs
//
// Explore ▸ Find group — interesting-location finder (epic #1184).
// S1 (#1185): "Detect period" reports the period of the lowest-period
// minibrot whose nucleus lies inside the current view (ball method,
// Abstractions/Explore/PeriodDetector). Read-only diagnostic: it does not
// move the view, so there is nothing for --batch to reproduce.

using System;
using System.Globalization;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

using ReactiveUI;

using FracturingFog.Abstractions.Explore;
using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class ControlCenterViewModel
{
    private CancellationTokenSource? _finderCts;
    private string _finderStatus = "Detect the period of the minibrot in view (z² + c).";
    private bool _isFinderBusy;

    public ReactiveCommand<Unit, Unit> DetectPeriodCommand { get; private set; } = null!;
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
        private set => this.RaiseAndSetIfChanged(ref _isFinderBusy, value);
    }

    /// <summary>The exact-track finders model f_c(z) = z² + c only; other
    /// families get the heuristic finder (S6 #1190) instead.</summary>
    private static bool FinderSupports(FractalType t)
        => t is FractalType.Mandelbrot or FractalType.GeneratedMandelbrotZ2;

    private void InitFinder()
    {
        DetectPeriodCommand = ReactiveCommand.CreateFromTask(DetectPeriodAsync);
        CancelFinderCommand = ReactiveCommand.Create(() => _finderCts?.Cancel());
    }

    private async Task DetectPeriodAsync()
    {
        var main = Shell.Main;
        var vs = main.ViewState;
        if (!FinderSupports(vs.FractalType))
        {
            FinderStatus = $"Period detection supports z² + c (Mandelbrot) only — not {vs.FractalType}.";
            return;
        }

        // Snapshot the view on the UI thread; the scan runs off it.
        var centre = vs.GetCenter();
        double zoom = vs.Zoom;
        var (w, h) = main.RenderHost.LastPresentedSize;
        double radius = PeriodDetector.ViewDiskRadius(zoom, w, h);

        _finderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _finderCts = cts;
        IsFinderBusy = true;
        FinderStatus = "Scanning…";
        try
        {
            var d = await Task.Run(() => PeriodDetector.Detect(centre, radius, ct: cts.Token), cts.Token);
            string z = zoom.ToString("G4", CultureInfo.InvariantCulture);
            FinderStatus = d.Stop switch
            {
                PeriodStop.Found       => $"Period {d.Period} minibrot in view (zoom {z}).",
                PeriodStop.Escaped     => $"No minibrot in view — the whole view escapes (zoom {z}).",
                PeriodStop.BallTooLarge => $"No period found within {d.Iterations} iterations (zoom {z}). Zoom in and retry.",
                _                      => $"No period up to {PeriodDetector.DefaultMaxPeriod:N0} (zoom {z}).",
            };
        }
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
}
