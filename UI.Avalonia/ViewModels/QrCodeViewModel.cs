// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/QrCodeViewModel.cs
// #1305 / S4 (#1399) — the Control Center Capture section's "QR Code" group:
// export the current view as a fractal that is also a scannable QR code.
//
// UI.Avalonia does not reference the Engine, so the encoder-backed checks and
// the render itself come in through host hooks (AvaloniaShellBootstrap):
// Preflight (does the payload fit, does the code fit the size) and
// ExportRequested (pick a path, render through PosterRenderer, decode the
// saved file). The same options feed the Command builder's live seed, so the
// export is reproducible headless with the --qr-* flags.

using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading.Tasks;

using ReactiveUI;

using FracturingFog.Imaging;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>What an export produced: whether the saved file scans, and a
/// one-line message for the status line.</summary>
public sealed record QrExportOutcome(bool Scans, string Message);

/// <summary>#1401 — what the view search found: the view to go to, the mask
/// that matches it best, and a one-line summary.</summary>
public sealed record QrSearchOutcome(FracturingFog.Abstractions.Explore.ExploreView View, int Mask, string Message);

public sealed class QrCodeViewModel : ViewModelBase
{
    public const int MinSize = 64, MaxSize = 8192, DefaultSize = 1200;

    public QrCodeViewModel()
    {
        ExportCommand = ReactiveCommand.CreateFromTask(ExportAsync, _canExport);
        FindViewCommand = ReactiveCommand.CreateFromTask(FindViewAsync, _canSearch);
        Refresh();
    }

    public ReactiveCommand<Unit, Unit> ExportCommand { get; }

    private readonly System.Reactive.Subjects.BehaviorSubject<bool> _canExport = new(false);
    private readonly System.Reactive.Subjects.BehaviorSubject<bool> _canSearch = new(false);

    /// <summary>#1401 — search around the live view for a better-matching framing.</summary>
    public ReactiveCommand<Unit, Unit> FindViewCommand { get; }

    /// <summary>Host search: run QrViewSearch on the live view at this size.
    /// Null result = nothing to report (cancelled / not available).</summary>
    public Func<QrCodeOptions, int, Task<QrSearchOutcome?>>? SearchRequested { get; set; }

    /// <summary>Navigate the live view (the Control Center wires this to the
    /// same go-to the auto-explorer uses, so Backspace returns).</summary>
    public Action<FracturingFog.Abstractions.Explore.ExploreView>? NavigateRequested { get; set; }

    /// <summary>Host check: null when the options encode and fit a w x h image.</summary>
    public Func<QrCodeOptions, int, int, string?>? Preflight { get; set; }

    /// <summary>Host export: save dialog + render + verify. Null result = cancelled.</summary>
    public Func<QrCodeOptions, int, Task<QrExportOutcome?>>? ExportRequested { get; set; }

    public IReadOnlyList<string> ErrorCorrectionChoices { get; } = new[]
    {
        "L  7%", "M  15%", "Q  25%", "H  30%",
    };

    public IReadOnlyList<string> StyleChoices { get; } = new[]
    {
        "Steer", "Dots", "Halftone", "Bevel (3D)",
    };

    public const int BevelStyleIndex = 3;

    private string _payload = "";
    /// <summary>The text the code carries (a URL, usually).</summary>
    public string Payload { get => _payload; set { this.RaiseAndSetIfChanged(ref _payload, value ?? ""); Mask = -1; Refresh(); } }

    private int _size = DefaultSize;
    /// <summary>Side of the square export, in pixels.</summary>
    public int Size
    {
        get => _size;
        set { this.RaiseAndSetIfChanged(ref _size, Math.Clamp(value, MinSize, MaxSize)); Refresh(); }
    }

    private int _eccIndex = (int)QrErrorCorrection.H;
    public int ErrorCorrectionIndex { get => _eccIndex; set { this.RaiseAndSetIfChanged(ref _eccIndex, Math.Clamp(value, 0, 3)); Mask = -1; Refresh(); } }

    private int _styleIndex;
    /// <summary>Index into <see cref="StyleChoices"/>: 0 steer, 1 dots, 2 halftone, 3 bevel.</summary>
    public int StyleIndex
    {
        get => _styleIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _styleIndex, Math.Clamp(value, 0, BevelStyleIndex));
            this.RaisePropertyChanged(nameof(IsBevel));
            Refresh();
        }
    }

    /// <summary>#1409 — the bevel controls show only for the Bevel style.</summary>
    public bool IsBevel => _styleIndex == BevelStyleIndex;

    private double _bevelHeight = QrCodeOptions.DefaultBevelHeight;
    /// <summary>#1409 — block height as a fraction of the module.</summary>
    public double BevelHeight
    {
        get => _bevelHeight;
        set => this.RaiseAndSetIfChanged(ref _bevelHeight,
            Math.Clamp(Math.Round(value, 2), QrCodeOptions.MinBevelHeight, QrCodeOptions.MaxBevelHeight));
    }

    private double _bevelWidth = QrCodeOptions.DefaultBevelWidth;
    /// <summary>#1409 — sloped edge width as a fraction of the module.</summary>
    public double BevelWidth
    {
        get => _bevelWidth;
        set => this.RaiseAndSetIfChanged(ref _bevelWidth,
            Math.Clamp(Math.Round(value, 2), QrCodeOptions.MinBevelWidth, QrCodeOptions.MaxBevelWidth));
    }

    private bool _bevelLightFromTheme = true;
    /// <summary>#1409 — light with the theme's 3D lights (else the angles below).</summary>
    public bool BevelLightFromTheme
    {
        get => _bevelLightFromTheme;
        set { this.RaiseAndSetIfChanged(ref _bevelLightFromTheme, value); this.RaisePropertyChanged(nameof(BevelLightExplicit)); }
    }

    public bool BevelLightExplicit => !_bevelLightFromTheme;

    private double _bevelAzimuth = QrCodeOptions.DefaultBevelAzimuthDeg;
    /// <summary>#1409 — degrees counter-clockwise from east (135 = upper left).</summary>
    public double BevelAzimuth
    {
        get => _bevelAzimuth;
        set => this.RaiseAndSetIfChanged(ref _bevelAzimuth, ((Math.Round(value) % 360) + 360) % 360);
    }

    private double _bevelElevation = QrCodeOptions.DefaultBevelElevationDeg;
    public double BevelElevation
    {
        get => _bevelElevation;
        set => this.RaiseAndSetIfChanged(ref _bevelElevation, Math.Clamp(Math.Round(value), 1, 89));
    }

    private double _bevelSpecular = QrCodeOptions.DefaultBevelSpecular;
    public double BevelSpecular
    {
        get => _bevelSpecular;
        set => this.RaiseAndSetIfChanged(ref _bevelSpecular, Math.Clamp(Math.Round(value, 2), 0, 1));
    }

    private bool _relief;
    /// <summary>#1410 — build the code into the Relief 3D raymarch (used when the
    /// view is in Relief 3D raymarch mode; otherwise the flat style is exported).</summary>
    public bool Relief { get => _relief; set => this.RaiseAndSetIfChanged(ref _relief, value); }

    private double _reliefHeight = QrCodeOptions.DefaultReliefHeight;
    /// <summary>#1410 — block height in modules.</summary>
    public double ReliefHeight
    {
        get => _reliefHeight;
        set => this.RaiseAndSetIfChanged(ref _reliefHeight,
            Math.Clamp(Math.Round(value, 2), QrCodeOptions.MinReliefHeight, QrCodeOptions.MaxReliefHeight));
    }

    private double _reliefTerrain = QrCodeOptions.DefaultReliefTerrain;
    /// <summary>#1410 — share of the fractal's relief kept under the code.</summary>
    public double ReliefTerrain
    {
        get => _reliefTerrain;
        set => this.RaiseAndSetIfChanged(ref _reliefTerrain, Math.Clamp(Math.Round(value, 2), 0, 1));
    }

    private double _reliefMinElevation = QrCodeOptions.DefaultReliefMinElevation;
    /// <summary>#1410 — lowest camera elevation while the code is extruded.</summary>
    public double ReliefMinElevation
    {
        get => _reliefMinElevation;
        set => this.RaiseAndSetIfChanged(ref _reliefMinElevation,
            Math.Clamp(Math.Round(value), QrCodeOptions.MinReliefElevation, 89));
    }

    private double _bevelShadow = QrCodeOptions.DefaultBevelShadow;
    public double BevelShadow
    {
        get => _bevelShadow;
        set => this.RaiseAndSetIfChanged(ref _bevelShadow, Math.Clamp(Math.Round(value, 2), 0, 1));
    }

    private int _modulePixels;
    /// <summary>Pixels per module; 0 = auto (largest that fits).</summary>
    public int ModulePixels
    {
        get => _modulePixels;
        set { this.RaiseAndSetIfChanged(ref _modulePixels, Math.Clamp(value, 0, QrCodeOptions.MaxModulePixels)); Refresh(); }
    }

    private double _dotFraction = QrCodeOptions.DefaultDotFraction;
    public double DotFraction
    {
        get => _dotFraction;
        set
        {
            this.RaiseAndSetIfChanged(ref _dotFraction,
                Math.Clamp(Math.Round(value, 2), QrCodeOptions.MinDotFraction, QrCodeOptions.MaxDotFraction));
            Refresh();
        }
    }

    private bool _invert;
    public bool Invert { get => _invert; set { this.RaiseAndSetIfChanged(ref _invert, value); Mask = -1; Refresh(); } }

    private int _mask = -1;
    /// <summary>#1401 — preferred mask from the last view search (-1 = auto).
    /// Cleared when the payload, error correction or invert changes (the match
    /// was for that code).</summary>
    public int Mask
    {
        get => _mask;
        set
        {
            this.RaiseAndSetIfChanged(ref _mask, Math.Clamp(value, -1, 7));
            this.RaisePropertyChanged(nameof(MaskText));
        }
    }

    public string MaskText => _mask < 0 ? "Mask: auto" : $"Mask: {_mask} (from Find View)";

    private bool _shape;
    /// <summary>#1402 — shape the code toward the image (QArt).</summary>
    public bool Shape { get => _shape; set { this.RaiseAndSetIfChanged(ref _shape, value); Refresh(); } }

    private int _minVersion = 1;
    /// <summary>#1402 — smallest QR version (1..40); larger buys shaping room.
    /// Changes the code, so it clears a searched mask.</summary>
    public int MinVersion
    {
        get => _minVersion;
        set { this.RaiseAndSetIfChanged(ref _minVersion, Math.Clamp(value, 1, 40)); Mask = -1; Refresh(); }
    }

    public static QrStyle StyleAt(int index) => index switch
    {
        1 => QrStyle.Dots, 2 => QrStyle.Halftone, BevelStyleIndex => QrStyle.Bevel, _ => QrStyle.Steer,
    };

    /// <summary>The options the panel describes; null with no payload.</summary>
    public QrCodeOptions? Options => _payload.Length == 0 ? null : new QrCodeOptions
    {
        Payload = _payload,
        ErrorCorrection = (QrErrorCorrection)_eccIndex,
        Style = StyleAt(_styleIndex),
        ModulePixels = _modulePixels,
        DotFraction = _dotFraction,
        Invert = _invert,
        Mask = _mask,
        Shape = _shape,
        MinVersion = _minVersion,
        BevelHeight = _bevelHeight,
        BevelWidth = _bevelWidth,
        BevelLightFromTheme = _bevelLightFromTheme,
        BevelAzimuthDeg = _bevelAzimuth,
        BevelElevationDeg = _bevelElevation,
        BevelSpecular = _bevelSpecular,
        BevelShadow = _bevelShadow,
        Relief = _relief,
        ReliefHeight = _reliefHeight,
        ReliefTerrain = _reliefTerrain,
        ReliefMinElevationDeg = _reliefMinElevation,
    };

    private string _message = "";
    public string Message { get => _message; private set { this.RaiseAndSetIfChanged(ref _message, value); this.RaisePropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => _message.Length > 0;

    private bool _messageIsWarning;
    /// <summary>The message is a problem (shown in yellow, not red).</summary>
    public bool MessageIsWarning { get => _messageIsWarning; private set => this.RaiseAndSetIfChanged(ref _messageIsWarning, value); }

    private bool _isExporting;
    public bool IsExporting { get => _isExporting; private set { this.RaiseAndSetIfChanged(ref _isExporting, value); Refresh(); } }

    private string? _problem;
    // The shown message is the preflight's (cleared once fixed) rather than an
    // export outcome (kept until the next export or problem).
    private bool _messageIsPreflight;

    private void Refresh()
    {
        var o = Options;
        _problem = o == null ? null : Preflight?.Invoke(o, _size, _size);
        if (!_isExporting)
        {
            if (_problem != null) { Message = _problem; MessageIsWarning = true; _messageIsPreflight = true; }
            else if (_messageIsPreflight) { Message = ""; MessageIsWarning = false; _messageIsPreflight = false; }
        }
        _canExport.OnNext(!_isExporting && o != null && _problem == null && ExportRequested != null);
        _canSearch.OnNext(!_isExporting && o != null && _problem == null && SearchRequested != null);
    }

    /// <summary>Re-run the checks once the host hooks are attached.</summary>
    public void HooksAttached() => Refresh();

    private async Task FindViewAsync()
    {
        var o = Options;
        if (o == null || SearchRequested == null) return;
        IsExporting = true;
        _messageIsPreflight = false;
        Message = "Searching for a matching view…";
        MessageIsWarning = false;
        try
        {
            var found = await SearchRequested(o with { Mask = -1 }, _size);
            if (found == null) { Message = ""; return; }
            NavigateRequested?.Invoke(found.View);
            Mask = found.Mask;
            Message = found.Message + " Backspace returns.";
        }
        catch (Exception ex)
        {
            Message = "QR view search failed: " + ex.Message;
            MessageIsWarning = true;
        }
        finally
        {
            IsExporting = false;
        }
    }

    private async Task ExportAsync()
    {
        var o = Options;
        if (o == null || ExportRequested == null) return;
        IsExporting = true;
        _messageIsPreflight = false;
        Message = "Rendering QR code…";
        MessageIsWarning = false;
        try
        {
            var outcome = await ExportRequested(o, _size);
            if (outcome == null) { Message = ""; return; }
            Message = outcome.Message;
            MessageIsWarning = !outcome.Scans;
        }
        catch (Exception ex)
        {
            Message = "QR export failed: " + ex.Message;
            MessageIsWarning = true;
        }
        finally
        {
            IsExporting = false;
        }
    }
}
