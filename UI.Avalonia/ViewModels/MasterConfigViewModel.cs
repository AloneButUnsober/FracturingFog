// ViewModels/MasterConfigViewModel.cs
// D-5e. Edits the three live-tunable cluster knobs (ClusterMaxJobs,
// ClusterArtifactRetentionMinutes, ClusterTileTargetPixels) on a running
// master via cluster.config.get / cluster.config.set. Reads + writes
// through FFAdminConnection opened with the local admin.pfx bundle —
// mirrors ClusterDashboardViewModel's cert resolution.
//
// D-6c2 (#125). Extended with the four per-role rate-limiter knobs
// (ClientCallPerMinute, ClientCallBurst, WorkerTileNextPerMinute,
// WorkerTileNextBurst) wired up by D-6c1. Shares the existing Load/Apply
// round-trip — the coordinator already accepts all seven knobs in a
// single cluster.config.set, so this is purely UI growth on top of an
// existing protocol surface.
//
// No background polling: the values rarely change, and a polling timer
// would clobber an in-progress edit. Operator hits Load to refresh, Apply
// to commit. Save-side response (ClusterConfigDto) refreshes the form so
// any server-side clamp (e.g. tile pixels out of range, or rate-limiter
// floor of burst≥1) is visible.

using System;
using System.IO;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Client;
using FracturingFog.Server;
using FracturingFog.Server.Cluster.Protocol;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class MasterConfigViewModel : ViewModelBase
{
    public MasterConfigViewModel()
    {
        var cfg = ServerConfig.LoadOrDefault();
        Host = "127.0.0.1";
        Port = cfg.Port;
        // Pre-seed the form from the local config so the dialog has
        // sensible numbers before the first Load round-trip. Master may
        // hold different values if cluster.config.set was issued from
        // another admin instance; Load reconciles.
        ClusterMaxJobs                  = cfg.ClusterMaxJobs;
        ClusterArtifactRetentionMinutes = cfg.ClusterArtifactRetentionMinutes;
        ClusterTileTargetPixels         = cfg.ClusterTileTargetPixels;
        ClientCallPerMinute             = cfg.ClientCallPerMinute;
        ClientCallBurst                 = cfg.ClientCallBurst;
        WorkerTileNextPerMinute         = cfg.WorkerTileNextPerMinute;
        WorkerTileNextBurst             = cfg.WorkerTileNextBurst;

        LoadCommand  = ReactiveCommand.CreateFromTask(LoadAsync);
        ApplyCommand = ReactiveCommand.CreateFromTask(ApplyAsync);
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    private string _host = "127.0.0.1";
    public string Host { get => _host; set => this.RaiseAndSetIfChanged(ref _host, value); }

    private int _port;
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, value); }

    private int _clusterMaxJobs;
    public int ClusterMaxJobs
    {
        get => _clusterMaxJobs;
        set => this.RaiseAndSetIfChanged(ref _clusterMaxJobs, value);
    }

    private int _clusterArtifactRetentionMinutes;
    public int ClusterArtifactRetentionMinutes
    {
        get => _clusterArtifactRetentionMinutes;
        set => this.RaiseAndSetIfChanged(ref _clusterArtifactRetentionMinutes, value);
    }

    private int _clusterTileTargetPixels;
    public int ClusterTileTargetPixels
    {
        get => _clusterTileTargetPixels;
        set => this.RaiseAndSetIfChanged(ref _clusterTileTargetPixels, value);
    }

    // D-6c2 (#125) — per-role rate-limiter knobs. Server clamps perMinute
    // to >=0 (0 disables) and burst to >=1 (Bucket constructor floor).
    private int _clientCallPerMinute;
    public int ClientCallPerMinute
    {
        get => _clientCallPerMinute;
        set => this.RaiseAndSetIfChanged(ref _clientCallPerMinute, value);
    }

    private int _clientCallBurst;
    public int ClientCallBurst
    {
        get => _clientCallBurst;
        set => this.RaiseAndSetIfChanged(ref _clientCallBurst, value);
    }

    private int _workerTileNextPerMinute;
    public int WorkerTileNextPerMinute
    {
        get => _workerTileNextPerMinute;
        set => this.RaiseAndSetIfChanged(ref _workerTileNextPerMinute, value);
    }

    private int _workerTileNextBurst;
    public int WorkerTileNextBurst
    {
        get => _workerTileNextBurst;
        set => this.RaiseAndSetIfChanged(ref _workerTileNextBurst, value);
    }

    private string _status = "Not loaded";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string? _lastError;
    public string? LastError { get => _lastError; set => this.RaiseAndSetIfChanged(ref _lastError, value); }

    public ReactiveCommand<Unit, Unit> LoadCommand  { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public event EventHandler? CloseRequested;

    public async Task LoadAsync()
    {
        var (adminPfx, caPfx, dirErr) = ResolveCertBundle();
        if (dirErr != null) { Status = "no cluster cert bundle"; LastError = dirErr; return; }

        try
        {
            await using var conn = await FFAdminConnection.ConnectAsync(
                new FFClientConnection.ConnectOptions
                {
                    Host             = Host,
                    Port             = Port,
                    ClientCertPath   = adminPfx!,
                    ServerCaCertPath = caPfx!,
                },
                CancellationToken.None).ConfigureAwait(true);

            ClusterConfigDto snap = await conn.GetClusterConfigAsync(CancellationToken.None).ConfigureAwait(true);
            ApplySnapshot(snap);
            Status   = $"loaded from {Host}:{Port}";
            LastError = null;
        }
        catch (Exception ex)
        {
            Status   = $"load failed: {ex.GetType().Name}";
            LastError = ex.Message;
        }
    }

    public async Task ApplyAsync()
    {
        var (adminPfx, caPfx, dirErr) = ResolveCertBundle();
        if (dirErr != null) { Status = "no cluster cert bundle"; LastError = dirErr; return; }

        try
        {
            await using var conn = await FFAdminConnection.ConnectAsync(
                new FFClientConnection.ConnectOptions
                {
                    Host             = Host,
                    Port             = Port,
                    ClientCertPath   = adminPfx!,
                    ServerCaCertPath = caPfx!,
                },
                CancellationToken.None).ConfigureAwait(true);

            ClusterConfigDto snap = await conn.SetClusterConfigAsync(
                ClusterMaxJobs,
                ClusterArtifactRetentionMinutes,
                ClusterTileTargetPixels,
                CancellationToken.None,
                clientCallPerMinute:     ClientCallPerMinute,
                clientCallBurst:         ClientCallBurst,
                workerTileNextPerMinute: WorkerTileNextPerMinute,
                workerTileNextBurst:     WorkerTileNextBurst).ConfigureAwait(true);
            // Master may clamp tile pixels into [MinTilePixels, MaxTilePixels]
            // and rate-limiter burst/perMinute into [floor, ...]; surface the
            // post-apply values so what the operator sees matches what the
            // master is actually running with.
            ApplySnapshot(snap);
            Status   = $"applied to {Host}:{Port}";
            LastError = null;
        }
        catch (Exception ex)
        {
            Status   = $"apply failed: {ex.GetType().Name}";
            LastError = ex.Message;
        }
    }

    private void ApplySnapshot(ClusterConfigDto snap)
    {
        ClusterMaxJobs                  = snap.ClusterMaxJobs;
        ClusterArtifactRetentionMinutes = snap.ClusterArtifactRetentionMinutes;
        ClusterTileTargetPixels         = snap.ClusterTileTargetPixels;
        ClientCallPerMinute             = snap.ClientCallPerMinute;
        ClientCallBurst                 = snap.ClientCallBurst;
        WorkerTileNextPerMinute         = snap.WorkerTileNextPerMinute;
        WorkerTileNextBurst             = snap.WorkerTileNextBurst;
    }

    private (string? AdminPfx, string? CaPfx, string? Error) ResolveCertBundle()
    {
        string certDir  = Path.Combine(ServerConfig.AppDataDir(), "cluster-certs");
        string adminPfx = Path.Combine(certDir, "admin.pfx");
        string caPfx    = Path.Combine(certDir, "ca.pfx");
        if (!File.Exists(adminPfx) || !File.Exists(caPfx))
            return (null, null,
                $"missing admin.pfx or ca.pfx under {certDir} — run the master once to generate the dev bundle.");
        return (adminPfx, caPfx, null);
    }
}
