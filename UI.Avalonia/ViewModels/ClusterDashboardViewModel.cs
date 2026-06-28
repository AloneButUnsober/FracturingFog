// ViewModels/ClusterDashboardViewModel.cs
// D-5b. Polls cluster.status every 5 s through an FFAdminConnection
// opened with the local %APPDATA%\FracturingFog\cluster-certs\admin.pfx
// bundle. Builds row VMs for the workers grid + the recent-jobs grid.
//
// Yellow (#FFCC00) on stale / quiesced rows per the user's red-green
// colour-blindness — red would be invisible to them, the existing convention
// is yellow for "needs attention" states (see CLAUDE.md memory note).

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

public sealed class ClusterDashboardViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _poll;
    private bool _disposed;

    public ClusterDashboardViewModel()
    {
        var cfg = ServerConfig.LoadOrDefault();
        Host = "127.0.0.1";
        Port = cfg.Port;
        RecentJobLimit = 50;

        RefreshCommand = ReactiveCommand.CreateFromTask(PollOnceAsync);
        CloseCommand   = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        // 5 s mirrors ServerAdminViewModel — every poll opens a fresh mTLS
        // handshake (~50–100 ms server CPU). 1 Hz would impose a 5–10% CPU
        // floor on an idle master. Tile updates are not real-time-critical
        // here; the grid is for monitoring, not live throughput tracking.
        _poll = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            async (_, _) => { if (!_disposed) await PollOnceAsync(); });
    }

    // ── connection params ────────────────────────────────────────────────

    private string _host = "127.0.0.1";
    public string Host { get => _host; set => this.RaiseAndSetIfChanged(ref _host, value); }

    private int _port;
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, value); }

    private int _recentJobLimit;
    public int RecentJobLimit { get => _recentJobLimit; set => this.RaiseAndSetIfChanged(ref _recentJobLimit, value); }

    // ── live state ───────────────────────────────────────────────────────

    private string _status = "Unknown";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _isOnline;
    public bool IsOnline { get => _isOnline; set => this.RaiseAndSetIfChanged(ref _isOnline, value); }

    private int _liveWorkerCount;
    public int LiveWorkerCount { get => _liveWorkerCount; set => this.RaiseAndSetIfChanged(ref _liveWorkerCount, value); }

    private int _heartbeatIntervalSeconds = 5;
    public int HeartbeatIntervalSeconds
    {
        get => _heartbeatIntervalSeconds;
        set => this.RaiseAndSetIfChanged(ref _heartbeatIntervalSeconds, value);
    }

    private long _serverUnixSeconds;
    public long ServerUnixSeconds { get => _serverUnixSeconds; set => this.RaiseAndSetIfChanged(ref _serverUnixSeconds, value); }

    private string? _lastError;
    public string? LastError { get => _lastError; set => this.RaiseAndSetIfChanged(ref _lastError, value); }

    public ObservableCollection<ClusterWorkerRowVm> Workers { get; } = new();
    public ObservableCollection<ClusterJobRowVm>    RecentJobs { get; } = new();

    // ── commands + events ────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public event EventHandler? CloseRequested;

    public void StartPolling() => _poll.Start();
    public void StopPolling()  => _poll.Stop();

    public async Task PollOnceAsync()
    {
        string certDir   = Path.Combine(ServerConfig.AppDataDir(), "cluster-certs");
        string adminPfx  = Path.Combine(certDir, "admin.pfx");
        string caPfx     = Path.Combine(certDir, "ca.pfx");
        if (!File.Exists(adminPfx) || !File.Exists(caPfx))
        {
            IsOnline = false;
            Status   = "no cluster cert bundle";
            LastError = $"missing admin.pfx or ca.pfx under {certDir} — run the master once to generate the dev bundle.";
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
                RecentJobLimit, CancellationToken.None).ConfigureAwait(true);

            ServerUnixSeconds        = dto.ServerUnixSeconds;
            HeartbeatIntervalSeconds = dto.HeartbeatIntervalSeconds <= 0 ? 5 : dto.HeartbeatIntervalSeconds;
            LiveWorkerCount          = dto.LiveWorkerCount;
            IsOnline = true;
            Status   = $"connected to {Host}:{Port} — {dto.LiveWorkerCount} live worker(s), {dto.Jobs.Count} job(s) shown";
            LastError = null;

            RebuildWorkers(dto);
            RebuildJobs(dto);
        }
        catch (Exception ex)
        {
            IsOnline  = false;
            Status    = $"connect failed: {ex.GetType().Name}";
            LastError = ex.Message;
        }
    }

    private void RebuildWorkers(ClusterStatusDto dto)
    {
        // Replace contents in place — clearing + re-adding keeps the
        // ObservableCollection-bound DataGrid scroll position stable while
        // still picking up new rows. Per-row UPDATE-in-place would beat
        // wholesale replacement for cell flicker, but the WorkerSummaryDto
        // has no stable identity contract beyond WorkerId — defer the
        // diff-update path to D-5d when WorkerDetailView pins a single row.
        Workers.Clear();
        long now = dto.ServerUnixSeconds;
        int staleThresholdSec = (dto.HeartbeatIntervalSeconds <= 0 ? 5 : dto.HeartbeatIntervalSeconds) * 3;
        foreach (var w in dto.Workers)
        {
            Workers.Add(ClusterWorkerRowVm.From(w, now, staleThresholdSec));
        }
    }

    private void RebuildJobs(ClusterStatusDto dto)
    {
        RecentJobs.Clear();
        foreach (var j in dto.Jobs)
            RecentJobs.Add(ClusterJobRowVm.From(j));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
    }
}

/// <summary>One row in the workers grid. Background is yellow #FFCC00 when
/// the worker is stale (no heartbeat in 3× cadence) or quiesced (draining).
/// Red would be invisible to red-green colour-blind users (see user memory).</summary>
public sealed class ClusterWorkerRowVm : ReactiveObject
{
    public string WorkerId          { get; }
    public string WorkerName        { get; }
    public string OsPlatform        { get; }
    public string CpuModel          { get; }
    public int    LogicalCores      { get; }
    public string RamGb             { get; }
    public string Gpus              { get; }
    public int    TilesInFlight     { get; }
    public string CpuPercentText    { get; }
    public string EmaMsPerKiloPx    { get; }
    public int    TileSamples       { get; }
    public string LastNote          { get; }
    public int    HeartbeatAgeSec   { get; }
    public bool   IsStale           { get; }
    public bool   Quiesced          { get; }
    public string StatusBadge       { get; }
    public string RowBackgroundHex  { get; }

    private ClusterWorkerRowVm(
        WorkerSummaryDto src, long serverNowUnixSeconds, int staleThresholdSec)
    {
        WorkerId        = src.WorkerId;
        WorkerName      = string.IsNullOrEmpty(src.WorkerName) ? src.WorkerId : src.WorkerName;
        OsPlatform      = src.OsPlatform;
        CpuModel        = src.CpuModel;
        LogicalCores    = src.LogicalCores;
        RamGb           = FormatBytesGb(src.TotalRamBytes);
        Gpus            = src.Gpus.Count == 0 ? "(none)" : string.Join(", ", src.Gpus);
        TilesInFlight   = src.TilesInFlight;
        CpuPercentText  = src.CpuPercent < 0
            ? "—"
            : src.CpuPercent.ToString("F0", CultureInfo.InvariantCulture) + "%";
        EmaMsPerKiloPx  = src.EmaMsPerKilopixel <= 0
            ? "—"
            : src.EmaMsPerKilopixel.ToString("F1", CultureInfo.InvariantCulture);
        TileSamples     = src.TileSamples;
        LastNote        = src.LastNote ?? "";
        HeartbeatAgeSec = src.LastHeartbeatUnixSeconds <= 0
            ? int.MaxValue
            : (int)Math.Max(0, serverNowUnixSeconds - src.LastHeartbeatUnixSeconds);
        IsStale         = HeartbeatAgeSec > staleThresholdSec;
        Quiesced        = src.Quiesced;

        StatusBadge =
            IsStale  ? "STALE"
          : Quiesced ? "QUIESCED"
          :            "LIVE";

        // Yellow for both warning states. Live rows take a transparent
        // background so the dark grid theme shows through unchanged.
        RowBackgroundHex = (IsStale || Quiesced) ? "#FFCC00" : "Transparent";
    }

    public static ClusterWorkerRowVm From(
        WorkerSummaryDto src, long serverNowUnixSeconds, int staleThresholdSec)
        => new(src, serverNowUnixSeconds, staleThresholdSec);

    private static string FormatBytesGb(long bytes)
    {
        if (bytes <= 0) return "—";
        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
        return gb.ToString("F1", CultureInfo.InvariantCulture) + " GiB";
    }
}

/// <summary>One row in the recent-jobs grid. Yellow background for failed /
/// cancelled rows so problem jobs stand out without using red.</summary>
public sealed class ClusterJobRowVm : ReactiveObject
{
    public string JobId            { get; }
    public string Mode             { get; }
    public string JobState         { get; }
    public string TilesProgress    { get; }
    public string ProgressPercent  { get; }
    public string CreatedLocal     { get; }
    public string ArtifactSizeText { get; }
    public string? FailReason      { get; }
    public string RowBackgroundHex { get; }

    private ClusterJobRowVm(JobSummaryDto src)
    {
        JobId       = src.JobId;
        Mode        = string.IsNullOrEmpty(src.Mode) ? "—" : src.Mode;
        JobState    = src.JobState;
        TilesProgress = src.TilesTotal > 0
            ? $"{src.TilesDone}/{src.TilesTotal} (+{src.TilesInFlight} in flight)"
            : "—";
        ProgressPercent = src.ProgressPercent.ToString("F1", CultureInfo.InvariantCulture) + "%";
        CreatedLocal = src.CreatedUnixMs <= 0
            ? "—"
            : DateTimeOffset.FromUnixTimeMilliseconds(src.CreatedUnixMs)
                .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        ArtifactSizeText = src.ArtifactBytes <= 0
            ? "—"
            : FormatBytes(src.ArtifactBytes);
        FailReason = string.IsNullOrEmpty(src.FailReason) ? null : src.FailReason;

        bool problem = src.JobState is "failed" or "cancelled";
        RowBackgroundHex = problem ? "#FFCC00" : "Transparent";
    }

    public static ClusterJobRowVm From(JobSummaryDto src) => new(src);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KiB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " MiB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture) + " GiB";
    }
}
