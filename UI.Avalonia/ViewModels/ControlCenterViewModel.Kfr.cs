// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1615 — Explore > Coordinates "Import .kfr…": open a Kalles Fraktaler location
// (format by Karl Runmo / Claude Heiland-Allen, KF 2+; see KfrLocation and
// Docs/Resources-Bibliography.md #kf-kfr-format) and jump there. The zoom converts
// for the render panel's size, as --kfr does for the batch frame.

using System;
using System.Globalization;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;

using ReactiveUI;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.ViewState;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class ControlCenterViewModel
{
    /// <summary>Host open-file picker for a .kfr (wired by the bootstrap).</summary>
    public Func<Task<string?>>? KfrImportPathRequested;

    public ReactiveCommand<Unit, Unit> ImportKfrCommand { get; private set; } = null!;

    private string _kfrStatus = "";
    /// <summary>Result of the last .kfr import (blank until one is tried).</summary>
    public string KfrStatus
    {
        get => _kfrStatus;
        private set => this.RaiseAndSetIfChanged(ref _kfrStatus, value);
    }

    private void InitKfr() => ImportKfrCommand = ReactiveCommand.CreateFromTask(ImportKfrAsync);

    private async Task ImportKfrAsync()
    {
        if (KfrImportPathRequested is not { } pick) return;
        string? path = await pick();
        if (string.IsNullOrWhiteSpace(path)) return;
        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            KfrStatus = $"Can't read {Path.GetFileName(path)}: {ex.Message}";
            return;
        }
        KfrStatus = ApplyKfr(text, Path.GetFileName(path));
    }

    /// <summary>Jump the live view to a .kfr's location; returns the status line.</summary>
    internal string ApplyKfr(string text, string name)
    {
        if (!KfrLocation.TryParse(text, out var kfr, out string? why)) return $"{name}: {why}";
        var main = Shell.Main;
        var vs = main.ViewState;
        if (vs.FractalType != FractalType.Mandelbrot)
            return $"{name} is a Mandelbrot location: switch the fractal type to Mandelbrot first.";

        var (w, h) = main.RenderHost.LastPresentedSize;
        if (w <= 0 || h <= 0) return $"{name}: no frame yet to size the zoom for.";
        // #1876 — any depth: past 1e100 the Unbounded tier, past double the log zoom, and the
        // exact centre when the file's digits go past OD.
        double zoom = kfr!.FfZoom(w, h);
        double zoomLog10 = kfr.FfZoomLog10(w, h);

        var (x, y) = kfr.Centre();
        Shell.RecordNavChange();
        if (kfr.CentreExact() is { } exact) vs.SetCenterExact(exact);
        else vs.SetCenter(DeepComplex.FromLimbs(x[0], x[1], x[2], x[3], x[4], x[5], x[6], x[7],
                                                y[0], y[1], y[2], y[3], y[4], y[5], y[6], y[7]));
        if (FracturingFog.ViewState.ZoomLog.BeyondDouble(zoomLog10)) vs.SetZoomLog10(zoomLog10);
        else vs.Zoom = zoom;
        var q = MinibrotJump.QualityFor(vs.Quality, zoom);
        if (!ReferenceEquals(q, vs.Quality))
        {
            vs.Quality = q;
            main.SetQualitySilent(q);
            Menu.SetQualitySilent(q.Name);
        }
        string iterNote = "";
        if (kfr.Iterations is int n)
        {
            if (vs.IterLocked) iterNote = $" Iterations are locked at {vs.LockedIterations:N0}; the file asks {n:N0}.";
            else vs.PreferredIterations = n;
        }
        main.RenderHost.Trigger();
        string flip = kfr.ImagPointsUp ? " (imaginary axis up: centre conjugated)" : "";
        string kz = FracturingFog.ViewState.ZoomLog.Format(kfr.KfZoom, kfr.KfZoomLog10, 4);
        string fz = FracturingFog.ViewState.ZoomLog.Format(zoom, zoomLog10, 4);
        return $"{name}: KF zoom {kz} → zoom {fz} at {w}×{h}{flip}.{iterNote}";
    }
}
