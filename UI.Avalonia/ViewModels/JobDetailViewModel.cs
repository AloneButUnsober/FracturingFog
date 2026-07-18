// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/JobDetailViewModel.cs
// D-5c. Per-job tile map for the cluster admin UI. Polls
// cluster.jobTileMap every 2 s and rebuilds the per-tile rect collection.
// Each tile is coloured by the worker that owns it (in-flight) or
// delivered it (completed) so the operator can see worker progress at a
// glance. Stable per-worker hash → HSL hex so the same worker keeps the
// same colour across refreshes.
//
// Polling cadence is faster than ClusterDashboardViewModel's 5 s because
// tile-grain progress is the whole point of this view — but slower than
// 1 Hz so the mTLS handshake CPU stays modest on jobs with hundreds of
// tiles. Stop polling when the job reaches a terminal state to avoid
// burning CPU on a frozen grid.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;
using FracturingFog.Client;
using FracturingFog.Server;
using FracturingFog.Server.Cluster.Protocol;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class JobDetailViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _poll;
    private bool _disposed;

    public JobDetailViewModel(string jobId)
    {
        var cfg = ServerConfig.LoadOrDefault();
        Host  = "127.0.0.1";
        Port  = cfg.Port;
        _jobId = jobId;

        RefreshCommand = ReactiveCommand.CreateFromTask(PollOnceAsync);
        CloseCommand   = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        _poll = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            async (_, _) => { if (!_disposed) await PollOnceAsync(); });
    }

    // ── connection ──────────────────────────────────────────────────────

    private string _host = "127.0.0.1";
    public string Host { get => _host; set => this.RaiseAndSetIfChanged(ref _host, value); }

    private int _port;
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, value); }

    private string _jobId;
    public string JobId
    {
        get => _jobId;
        set
        {
            if (string.Equals(_jobId, value, StringComparison.Ordinal)) return;
            this.RaiseAndSetIfChanged(ref _jobId, value);
            // Swap target job mid-flight: drop the old grid + immediately
            // poll the new one so the user sees fresh data without waiting
            // for the 2 s timer.
            Tiles.Clear();
            Workers.Clear();
            _ = PollOnceAsync();
        }
    }

    // ── live state ──────────────────────────────────────────────────────

    private string _jobState = "(unknown)";
    public string JobState { get => _jobState; set => this.RaiseAndSetIfChanged(ref _jobState, value); }

    private string _mode = "";
    public string Mode { get => _mode; set => this.RaiseAndSetIfChanged(ref _mode, value); }

    private string _summary = "";
    public string Summary { get => _summary; set => this.RaiseAndSetIfChanged(ref _summary, value); }

    private string? _lastError;
    public string? LastError { get => _lastError; set => this.RaiseAndSetIfChanged(ref _lastError, value); }

    private double _canvasWidth = 600;
    public double CanvasWidth { get => _canvasWidth; set => this.RaiseAndSetIfChanged(ref _canvasWidth, value); }

    private double _canvasHeight = 600;
    public double CanvasHeight { get => _canvasHeight; set => this.RaiseAndSetIfChanged(ref _canvasHeight, value); }

    private bool _hasSpatialGrid;
    /// <summary>True when the open job is image-mode and tiles carry real
    /// rect data. Video / slideshow jobs fall back to the flat list path,
    /// which the view binds to the same Tiles collection but with grid
    /// auto-layout instead of plan coordinates.</summary>
    public bool HasSpatialGrid { get => _hasSpatialGrid; set => this.RaiseAndSetIfChanged(ref _hasSpatialGrid, value); }

    public ObservableCollection<JobTileVm>      Tiles   { get; } = new();
    public ObservableCollection<JobWorkerLegendVm> Workers { get; } = new();

    // ── commands + events ───────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand   { get; }
    public event EventHandler? CloseRequested;

    public void StartPolling() => _poll.Start();
    public void StopPolling()  => _poll.Stop();

    public async Task PollOnceAsync()
    {
        string certDir  = Path.Combine(ServerConfig.AppDataDir(), "cluster-certs");
        string adminPfx = Path.Combine(certDir, "admin.pfx");
        string caPfx    = Path.Combine(certDir, "ca.pfx");
        if (!File.Exists(adminPfx) || !File.Exists(caPfx))
        {
            LastError = $"missing admin.pfx or ca.pfx under {certDir} — run the master once to mint the dev bundle.";
            return;
        }

        try
        {
            await using var conn = await FFAdminConnection.ConnectAsync(
                new FFClientConnection.ConnectOptions
                {
                    Host             = Host,
                    Port             = Port,
                    ClientCertPath   = adminPfx,
                    ServerCaCertPath = caPfx,
                },
                CancellationToken.None).ConfigureAwait(true);

            var dto = await conn.GetJobTileMapAsync(_jobId, CancellationToken.None).ConfigureAwait(true);
            LastError = null;
            JobState  = dto.JobState;
            Mode      = string.IsNullOrEmpty(dto.Mode) ? "(unknown)" : dto.Mode;

            Rebuild(dto);

            // Stop the timer when the job is terminal — no point hammering
            // the master with a polling loop on a frozen grid. The Refresh
            // button stays wired so the operator can manually re-pull.
            if (dto.JobState is "ready" or "failed" or "cancelled")
                _poll.Stop();
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void Rebuild(JobTileMapDto dto)
    {
        Tiles.Clear();
        Workers.Clear();

        bool isImage = string.Equals(dto.Mode, "image", StringComparison.OrdinalIgnoreCase)
                    && dto.ImageWidth > 0 && dto.ImageHeight > 0;
        HasSpatialGrid = isImage;

        // ── colour assignment ────────────────────────────────────────────
        // Stable per-workerId colour: hash → HSL hue, fixed S/L for
        // contrast. Pending = grey #444. Yellow #FFCC00 reserved for
        // job-level problem rows (matches dashboard convention) so we
        // don't bleed it into a worker swatch.
        var workerColours = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in dto.Tiles)
        {
            if (t.WorkerId is null) continue;
            if (workerColours.ContainsKey(t.WorkerId)) continue;
            workerColours[t.WorkerId] = HashToHexColour(t.WorkerId);
        }
        foreach (var kv in workerColours)
            Workers.Add(new JobWorkerLegendVm(kv.Key, kv.Value));

        // ── geometry ────────────────────────────────────────────────────
        // Image mode: scale plan rects into the canvas, preserving aspect.
        // Otherwise auto-tile into a near-square grid so video frame ranges
        // and slideshow slides still get a visual map (per-tile colour by
        // worker is the useful signal there too).
        if (isImage)
        {
            double scaleX = CanvasWidth  / dto.ImageWidth;
            double scaleY = CanvasHeight / dto.ImageHeight;
            double scale  = Math.Min(scaleX, scaleY);
            foreach (var t in dto.Tiles)
            {
                Tiles.Add(new JobTileVm
                {
                    TileId  = t.TileId,
                    X       = t.OffsetX * scale,
                    Y       = t.OffsetY * scale,
                    Width   = Math.Max(1, t.Width  * scale),
                    Height  = Math.Max(1, t.Height * scale),
                    State   = t.State,
                    WorkerId = t.WorkerId ?? "",
                    FillHex = ColourFor(t, workerColours),
                    Tooltip = FormatTooltip(t),
                });
            }
        }
        else
        {
            int n = dto.Tiles.Count;
            if (n <= 0) { UpdateSummary(dto, 0, 0); return; }
            int cols = (int)Math.Ceiling(Math.Sqrt(n));
            int rows = (int)Math.Ceiling((double)n / cols);
            double cellW = CanvasWidth  / cols;
            double cellH = CanvasHeight / rows;
            for (int i = 0; i < n; i++)
            {
                var t = dto.Tiles[i];
                int col = i % cols;
                int row = i / cols;
                Tiles.Add(new JobTileVm
                {
                    TileId  = t.TileId,
                    X       = col * cellW,
                    Y       = row * cellH,
                    Width   = Math.Max(1, cellW - 1),
                    Height  = Math.Max(1, cellH - 1),
                    State   = t.State,
                    WorkerId = t.WorkerId ?? "",
                    FillHex = ColourFor(t, workerColours),
                    Tooltip = FormatTooltip(t),
                });
            }
        }

        UpdateSummary(dto, dto.TilesDone, dto.TilesInFlight);
    }

    private void UpdateSummary(JobTileMapDto dto, int done, int inflight)
    {
        Summary = string.Format(CultureInfo.InvariantCulture,
            "{0} — {1} mode, {2}/{3} tiles done (+{4} in flight)",
            dto.JobState, dto.Mode, done, dto.TilesTotal, inflight);
    }

    private static string ColourFor(TileMapEntryDto t, IReadOnlyDictionary<string, string> workerColours)
    {
        if (t.State == "pending")    return "#3A3A3A";
        if (t.WorkerId is null)      return "#5A5A5A";
        if (workerColours.TryGetValue(t.WorkerId, out var hex)) return hex;
        return "#888888";
    }

    private static string FormatTooltip(TileMapEntryDto t)
    {
        string worker = string.IsNullOrEmpty(t.WorkerId) ? "(unassigned)" : t.WorkerId;
        return $"tile {t.TileId}\nstate: {t.State}\nworker: {worker}";
    }

    /// <summary>Hash a workerId to a #RRGGBB string. HSL with fixed S/L for
    /// readable contrast on the #1B1B1B grid background; hue cycles through
    /// 360° so adjacent workers stay distinguishable up to ~12 entries.</summary>
    private static string HashToHexColour(string workerId)
    {
        // FNV-1a 32 — cheap, stable, no allocs.
        uint h = 2166136261u;
        foreach (char c in workerId)
        {
            h ^= c;
            h *= 16777619u;
        }
        double hue        = (h % 360u) / 360.0;
        const double sat  = 0.55;
        const double lum  = 0.50;
        HslToRgb(hue, sat, lum, out byte r, out byte g, out byte b);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static void HslToRgb(double h, double s, double l, out byte r, out byte g, out byte b)
    {
        double c  = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double hp = h * 6.0;
        double x  = c * (1.0 - Math.Abs(hp % 2.0 - 1.0));
        double r1, g1, b1;
        if      (hp < 1) { r1 = c; g1 = x; b1 = 0; }
        else if (hp < 2) { r1 = x; g1 = c; b1 = 0; }
        else if (hp < 3) { r1 = 0; g1 = c; b1 = x; }
        else if (hp < 4) { r1 = 0; g1 = x; b1 = c; }
        else if (hp < 5) { r1 = x; g1 = 0; b1 = c; }
        else             { r1 = c; g1 = 0; b1 = x; }
        double m = l - 0.5 * c;
        r = (byte)Math.Clamp((int)Math.Round((r1 + m) * 255), 0, 255);
        g = (byte)Math.Clamp((int)Math.Round((g1 + m) * 255), 0, 255);
        b = (byte)Math.Clamp((int)Math.Round((b1 + m) * 255), 0, 255);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
    }
}

/// <summary>One tile rect on the JobDetailView canvas. Positions + size
/// are in canvas pixels (already scaled for image-mode jobs; auto-laid-out
/// for video/slideshow). <see cref="FillHex"/> is a string the XAML binds
/// directly into <c>Rectangle.Fill</c> via Avalonia's automatic
/// string → SolidColorBrush conversion.</summary>
public sealed class JobTileVm : ReactiveObject
{
    public int    TileId   { get; init; }
    public double X        { get; init; }
    public double Y        { get; init; }
    public double Width    { get; init; }
    public double Height   { get; init; }
    public string State    { get; init; } = "pending";
    public string WorkerId { get; init; } = "";
    public string FillHex  { get; init; } = "#3A3A3A";
    public string Tooltip  { get; init; } = "";
}

/// <summary>One row in the worker legend strip below the tile grid.
/// SwatchHex matches the per-tile fill so the operator can map colours
/// back to worker ids without hovering every cell.</summary>
public sealed class JobWorkerLegendVm : ReactiveObject
{
    public string WorkerId  { get; }
    public string SwatchHex { get; }

    public JobWorkerLegendVm(string workerId, string swatchHex)
    {
        WorkerId  = workerId;
        SwatchHex = swatchHex;
    }
}
