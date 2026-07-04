// ViewModels/WorkerDetailViewModel.cs
// D-5d. Per-worker drill-in for the cluster admin UI. Polls cluster.status
// every 5 s through an FFAdminConnection (matches dashboard cadence — the
// payload already carries every worker, so the detail view filters
// client-side rather than asking for a per-worker RPC).
//
// Exposes capability metadata (CPU model, RAM, GPUs, supported fractal
// types, engine SHA, protocol version), live telemetry (in-flight tiles,
// CPU %, free RAM, EMA throughput, last note, heartbeat age), and three
// action commands: Quiesce, Resume, Kill — all backed by the existing
// cluster.quiesceWorker / cluster.killWorker RPCs from D-5a.
//
// Single-instance window parameterised by WorkerId, mirroring JobDetailView.
// Yellow #FFCC00 for the stale/quiesced badge per the user's red-green
// colour-blindness note.

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;
using FracturingFog.Client;
using FracturingFog.Server;
using FracturingFog.Server.Cluster.Protocol;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class WorkerDetailViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _poll;
    private bool _disposed;

    public WorkerDetailViewModel(string workerId)
    {
        var cfg = ServerConfig.LoadOrDefault();
        Host     = "127.0.0.1";
        Port     = cfg.Port;
        _workerId = workerId;

        RefreshCommand = ReactiveCommand.CreateFromTask(PollOnceAsync);
        QuiesceCommand = ReactiveCommand.CreateFromTask(() => SetQuiesceAsync(true));
        ResumeCommand  = ReactiveCommand.CreateFromTask(() => SetQuiesceAsync(false));
        KillCommand    = ReactiveCommand.CreateFromTask(KillAsync);
        CloseCommand   = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        _poll = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            async (_, _) => { if (!_disposed) await PollOnceAsync(); });
    }

    // ── connection + target ──────────────────────────────────────────────

    private string _host = "127.0.0.1";
    public string Host { get => _host; set => this.RaiseAndSetIfChanged(ref _host, value); }

    private int _port;
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, value); }

    private string _workerId;
    public string WorkerId
    {
        get => _workerId;
        set
        {
            if (string.Equals(_workerId, value, StringComparison.Ordinal)) return;
            this.RaiseAndSetIfChanged(ref _workerId, value);
            // Mirror JobDetailView swap path: clear stale fields + immediate
            // poll so the user sees fresh data without waiting for the timer.
            ClearLiveState();
            _ = PollOnceAsync();
        }
    }

    // ── identity / capabilities ──────────────────────────────────────────

    private string _workerName = "";
    public string WorkerName { get => _workerName; set => this.RaiseAndSetIfChanged(ref _workerName, value); }

    private string _osPlatform = "";
    public string OsPlatform { get => _osPlatform; set => this.RaiseAndSetIfChanged(ref _osPlatform, value); }

    private string _cpuModel = "";
    public string CpuModel { get => _cpuModel; set => this.RaiseAndSetIfChanged(ref _cpuModel, value); }

    private int _logicalCores;
    public int LogicalCores { get => _logicalCores; set => this.RaiseAndSetIfChanged(ref _logicalCores, value); }

    private string _totalRam = "—";
    public string TotalRam { get => _totalRam; set => this.RaiseAndSetIfChanged(ref _totalRam, value); }

    private string _gpus = "(none)";
    public string Gpus { get => _gpus; set => this.RaiseAndSetIfChanged(ref _gpus, value); }

    private string _supportedFractalTypes = "—";
    public string SupportedFractalTypes
    {
        get => _supportedFractalTypes;
        set => this.RaiseAndSetIfChanged(ref _supportedFractalTypes, value);
    }

    private int _maxConcurrentTiles;
    public int MaxConcurrentTiles { get => _maxConcurrentTiles; set => this.RaiseAndSetIfChanged(ref _maxConcurrentTiles, value); }

    private int _preferredTilePixels;
    public int PreferredTilePixels { get => _preferredTilePixels; set => this.RaiseAndSetIfChanged(ref _preferredTilePixels, value); }

    private string _engineBuildSha = "—";
    public string EngineBuildSha { get => _engineBuildSha; set => this.RaiseAndSetIfChanged(ref _engineBuildSha, value); }

    private string _protocolVersion = "—";
    public string ProtocolVersion { get => _protocolVersion; set => this.RaiseAndSetIfChanged(ref _protocolVersion, value); }

    private string _registeredAtLocal = "—";
    public string RegisteredAtLocal { get => _registeredAtLocal; set => this.RaiseAndSetIfChanged(ref _registeredAtLocal, value); }

    // ── live telemetry ──────────────────────────────────────────────────

    private int _tilesInFlight;
    public int TilesInFlight { get => _tilesInFlight; set => this.RaiseAndSetIfChanged(ref _tilesInFlight, value); }

    private string _cpuPercentText = "—";
    public string CpuPercentText { get => _cpuPercentText; set => this.RaiseAndSetIfChanged(ref _cpuPercentText, value); }

    private string _freeRam = "—";
    public string FreeRam { get => _freeRam; set => this.RaiseAndSetIfChanged(ref _freeRam, value); }

    private string _emaMsPerKilopixel = "—";
    public string EmaMsPerKilopixel { get => _emaMsPerKilopixel; set => this.RaiseAndSetIfChanged(ref _emaMsPerKilopixel, value); }

    private int _tileSamples;
    public int TileSamples { get => _tileSamples; set => this.RaiseAndSetIfChanged(ref _tileSamples, value); }

    private string _lastNote = "";
    public string LastNote { get => _lastNote; set => this.RaiseAndSetIfChanged(ref _lastNote, value); }

    private string _heartbeatAge = "—";
    public string HeartbeatAge { get => _heartbeatAge; set => this.RaiseAndSetIfChanged(ref _heartbeatAge, value); }

    private bool _isStale;
    public bool IsStale { get => _isStale; set => this.RaiseAndSetIfChanged(ref _isStale, value); }

    private bool _quiesced;
    public bool Quiesced { get => _quiesced; set => this.RaiseAndSetIfChanged(ref _quiesced, value); }

    private string _statusBadge = "—";
    public string StatusBadge { get => _statusBadge; set => this.RaiseAndSetIfChanged(ref _statusBadge, value); }

    private string _statusBackgroundHex = "Transparent";
    public string StatusBackgroundHex
    {
        get => _statusBackgroundHex;
        set => this.RaiseAndSetIfChanged(ref _statusBackgroundHex, value);
    }

    private bool _isPresent;
    /// <summary>True when the most recent cluster.status snapshot still
    /// contained an entry for <see cref="WorkerId"/>. Drives the
    /// "(no longer in registry)" status badge after a successful Kill.</summary>
    public bool IsPresent { get => _isPresent; set => this.RaiseAndSetIfChanged(ref _isPresent, value); }

    private string? _lastError;
    public string? LastError { get => _lastError; set => this.RaiseAndSetIfChanged(ref _lastError, value); }

    private string _actionStatus = "";
    /// <summary>Free-text status line surfaced after a Quiesce/Resume/Kill
    /// click — separate from <see cref="LastError"/> so a successful action
    /// doesn't clear a connection-level error message.</summary>
    public string ActionStatus { get => _actionStatus; set => this.RaiseAndSetIfChanged(ref _actionStatus, value); }

    // ── commands + events ───────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> QuiesceCommand { get; }
    public ReactiveCommand<Unit, Unit> ResumeCommand  { get; }
    public ReactiveCommand<Unit, Unit> KillCommand    { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand   { get; }
    public event EventHandler? CloseRequested;

    public void StartPolling() => _poll.Start();
    public void StopPolling()  => _poll.Stop();

    public async Task PollOnceAsync()
    {
        if (!TryGetCertBundle(out string adminPfx, out string caPfx, out string err))
        {
            LastError = err;
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

            ClusterStatusDto dto = await conn.GetClusterStatusAsync(
                recentJobLimit: 1, CancellationToken.None).ConfigureAwait(true);
            LastError = null;

            int staleThresholdSec = (dto.HeartbeatIntervalSeconds <= 0 ? 5 : dto.HeartbeatIntervalSeconds) * 3;
            var w = dto.Workers.FirstOrDefault(
                x => string.Equals(x.WorkerId, _workerId, StringComparison.Ordinal));

            if (w is null)
            {
                IsPresent   = false;
                StatusBadge = "GONE";
                StatusBackgroundHex = "#FFCC00";
                // Clear telemetry but keep capabilities — they were valid at
                // last sighting and pointing the operator at an empty grid
                // would lose context after a Kill.
                TilesInFlight     = 0;
                CpuPercentText    = "—";
                FreeRam           = "—";
                HeartbeatAge      = "—";
                return;
            }

            ApplyWorkerSnapshot(w, dto.ServerUnixSeconds, staleThresholdSec);
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void ApplyWorkerSnapshot(WorkerSummaryDto w, long serverNowUnix, int staleThresholdSec)
    {
        IsPresent           = true;
        WorkerName          = string.IsNullOrEmpty(w.WorkerName) ? w.WorkerId : w.WorkerName;
        OsPlatform          = string.IsNullOrEmpty(w.OsPlatform) ? "—" : w.OsPlatform;
        CpuModel            = string.IsNullOrEmpty(w.CpuModel)   ? "—" : w.CpuModel;
        LogicalCores        = w.LogicalCores;
        TotalRam            = FormatBytesGb(w.TotalRamBytes);
        Gpus                = w.Gpus.Count == 0 ? "(none)" : string.Join(", ", w.Gpus);
        SupportedFractalTypes = w.SupportedFractalTypes.Count == 0
            ? "—" : string.Join(", ", w.SupportedFractalTypes);
        MaxConcurrentTiles  = w.MaxConcurrentTiles;
        PreferredTilePixels = w.PreferredTilePixels;
        EngineBuildSha      = string.IsNullOrEmpty(w.EngineBuildSha) ? "—" : w.EngineBuildSha;
        ProtocolVersion     = string.IsNullOrEmpty(w.ProtocolVersion) ? "—" : w.ProtocolVersion;

        RegisteredAtLocal = w.RegisteredAtUnixSeconds <= 0
            ? "—"
            : DateTimeOffset.FromUnixTimeSeconds(w.RegisteredAtUnixSeconds)
                .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        TilesInFlight   = w.TilesInFlight;
        CpuPercentText  = w.CpuPercent < 0
            ? "—"
            : w.CpuPercent.ToString("F0", CultureInfo.InvariantCulture) + "%";
        FreeRam         = FormatBytesGb(w.FreeRamBytes);
        EmaMsPerKilopixel = w.EmaMsPerKilopixel <= 0
            ? "—"
            : w.EmaMsPerKilopixel.ToString("F1", CultureInfo.InvariantCulture);
        TileSamples     = w.TileSamples;
        LastNote        = w.LastNote ?? "";

        int ageSec = w.LastHeartbeatUnixSeconds <= 0
            ? int.MaxValue
            : (int)Math.Max(0, serverNowUnix - w.LastHeartbeatUnixSeconds);
        HeartbeatAge = ageSec == int.MaxValue
            ? "—"
            : ageSec.ToString(CultureInfo.InvariantCulture) + " s";
        IsStale  = ageSec > staleThresholdSec;
        Quiesced = w.Quiesced;

        StatusBadge =
            IsStale  ? "STALE"
          : Quiesced ? "QUIESCED"
          :            "LIVE";
        StatusBackgroundHex = (IsStale || Quiesced) ? "#FFCC00" : "Transparent";
    }

    private async Task SetQuiesceAsync(bool quiesced)
    {
        if (!TryGetCertBundle(out string adminPfx, out string caPfx, out string err))
        {
            ActionStatus = err;
            return;
        }
        try
        {
            await using var conn = await FFAdminConnection.ConnectAsync(
                new FFClientConnection.ConnectOptions
                {
                    Host = Host, Port = Port,
                    ClientCertPath = adminPfx, ServerCaCertPath = caPfx,
                },
                CancellationToken.None).ConfigureAwait(true);

            WorkerQuiesceAckDto ack = await conn.SetWorkerQuiescedAsync(
                _workerId, quiesced, CancellationToken.None).ConfigureAwait(true);
            ActionStatus = $"quiesce {(ack.PreviousState ? "on" : "off")} → {(ack.CurrentState ? "on" : "off")}";
            // Refresh local view so the badge flips without waiting for the
            // 5 s poll tick.
            await PollOnceAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ActionStatus = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task KillAsync()
    {
        if (!TryGetCertBundle(out string adminPfx, out string caPfx, out string err))
        {
            ActionStatus = err;
            return;
        }
        try
        {
            await using var conn = await FFAdminConnection.ConnectAsync(
                new FFClientConnection.ConnectOptions
                {
                    Host = Host, Port = Port,
                    ClientCertPath = adminPfx, ServerCaCertPath = caPfx,
                },
                CancellationToken.None).ConfigureAwait(true);

            WorkerKillAckDto ack = await conn.KillWorkerAsync(
                _workerId, CancellationToken.None).ConfigureAwait(true);
            ActionStatus = ack.Removed
                ? "kill: worker removed from registry"
                : "kill: worker was already absent (idempotent)";
            await PollOnceAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ActionStatus = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private bool TryGetCertBundle(out string adminPfx, out string caPfx, out string error)
    {
        string certDir = Path.Combine(ServerConfig.AppDataDir(), "cluster-certs");
        adminPfx = Path.Combine(certDir, "admin.pfx");
        caPfx    = Path.Combine(certDir, "ca.pfx");
        if (!File.Exists(adminPfx) || !File.Exists(caPfx))
        {
            error = $"missing admin.pfx or ca.pfx under {certDir} — run the master once to mint the dev bundle.";
            return false;
        }
        error = "";
        return true;
    }

    private void ClearLiveState()
    {
        IsPresent       = false;
        WorkerName      = "";
        OsPlatform      = "";
        CpuModel        = "";
        LogicalCores    = 0;
        TotalRam        = "—";
        Gpus            = "(none)";
        SupportedFractalTypes = "—";
        MaxConcurrentTiles  = 0;
        PreferredTilePixels = 0;
        EngineBuildSha    = "—";
        ProtocolVersion   = "—";
        RegisteredAtLocal = "—";
        TilesInFlight   = 0;
        CpuPercentText  = "—";
        FreeRam         = "—";
        EmaMsPerKilopixel = "—";
        TileSamples     = 0;
        LastNote        = "";
        HeartbeatAge    = "—";
        IsStale = false;
        Quiesced = false;
        StatusBadge = "—";
        StatusBackgroundHex = "Transparent";
        ActionStatus = "";
        LastError = null;
    }

    private static string FormatBytesGb(long bytes)
    {
        if (bytes <= 0) return "—";
        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
        return gb.ToString("F1", CultureInfo.InvariantCulture) + " GiB";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
    }
}
