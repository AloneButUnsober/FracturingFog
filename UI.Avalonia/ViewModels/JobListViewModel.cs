// ViewModels/JobListViewModel.cs
// D-5c. Paged + filterable job list backed by cluster.listJobs. Distinct
// from ClusterDashboardViewModel's embedded recent-jobs block because
// (a) admins want to filter by state ("show me only failed"), and
// (b) the page cap can run higher than the dashboard's quick-glance
// limit. Row click bubbles up via OpenJobDetailRequested so the shell
// can route to a JobDetailView with the chosen jobId.

using System;
using System.Collections.Generic;
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

public sealed class JobListViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _poll;
    private bool _disposed;

    // Filter dropdown options. "(all)" maps to null on the wire; the
    // others are exact state strings the master understands.
    public static IReadOnlyList<string> StateFilterOptions { get; } = new[]
    {
        "(all)", "queued", "planning", "rendering", "merging",
        "ready",  "failed",  "cancelled",
    };

    public JobListViewModel()
    {
        var cfg = ServerConfig.LoadOrDefault();
        Host  = "127.0.0.1";
        Port  = cfg.Port;
        Limit = 100;
        IncludeTerminal = true;
        SelectedStateFilter = "(all)";

        RefreshCommand     = ReactiveCommand.CreateFromTask(PollOnceAsync);
        CloseCommand       = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        OpenJobDetailCommand = ReactiveCommand.Create<JobListRowVm>(row =>
        {
            if (row != null) OpenJobDetailRequested?.Invoke(this, row.JobId);
        });

        // 10 s — slower than dashboard (5 s) because the list view is
        // used for browse + drill-in, not live monitoring. Operator hits
        // Refresh for an immediate pull.
        _poll = new DispatcherTimer(
            TimeSpan.FromSeconds(10),
            DispatcherPriority.Background,
            async (_, _) => { if (!_disposed) await PollOnceAsync(); });
    }

    // ── connection / filter params ──────────────────────────────────────

    private string _host = "127.0.0.1";
    public string Host { get => _host; set => this.RaiseAndSetIfChanged(ref _host, value); }

    private int _port;
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, value); }

    private int _limit;
    public int Limit { get => _limit; set => this.RaiseAndSetIfChanged(ref _limit, value); }

    private bool _includeTerminal;
    public bool IncludeTerminal
    {
        get => _includeTerminal;
        set => this.RaiseAndSetIfChanged(ref _includeTerminal, value);
    }

    private string _selectedStateFilter = "(all)";
    public string SelectedStateFilter
    {
        get => _selectedStateFilter;
        set => this.RaiseAndSetIfChanged(ref _selectedStateFilter, value);
    }

    // ── live state ──────────────────────────────────────────────────────

    private string _status = "Unknown";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string? _lastError;
    public string? LastError { get => _lastError; set => this.RaiseAndSetIfChanged(ref _lastError, value); }

    private int _totalCount;
    public int TotalCount { get => _totalCount; set => this.RaiseAndSetIfChanged(ref _totalCount, value); }

    public ObservableCollection<JobListRowVm> Jobs { get; } = new();

    // ── commands + events ───────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit>           RefreshCommand     { get; }
    public ReactiveCommand<Unit, Unit>           CloseCommand       { get; }
    public ReactiveCommand<JobListRowVm, Unit>   OpenJobDetailCommand { get; }

    public event EventHandler?         CloseRequested;
    public event EventHandler<string>? OpenJobDetailRequested;

    public void StartPolling() => _poll.Start();
    public void StopPolling()  => _poll.Stop();

    public async Task PollOnceAsync()
    {
        string certDir  = Path.Combine(ServerConfig.AppDataDir(), "cluster-certs");
        string adminPfx = Path.Combine(certDir, "admin.pfx");
        string caPfx    = Path.Combine(certDir, "ca.pfx");
        if (!File.Exists(adminPfx) || !File.Exists(caPfx))
        {
            Status    = "no cluster cert bundle";
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

            string? filter = SelectedStateFilter == "(all)" ? null : SelectedStateFilter;
            var dto = await conn.ListJobsAsync(Limit, IncludeTerminal, filter, CancellationToken.None)
                                .ConfigureAwait(true);

            TotalCount = dto.TotalCount;
            Status     = $"showing {dto.Jobs.Count} of {dto.TotalCount}"
                       + (filter == null ? "" : $" (filter: {filter})");
            LastError  = null;

            Jobs.Clear();
            foreach (var j in dto.Jobs)
                Jobs.Add(JobListRowVm.From(j));
        }
        catch (Exception ex)
        {
            Status    = $"connect failed: {ex.GetType().Name}";
            LastError = ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
    }
}

/// <summary>One row in the JobListView grid. Yellow #FFCC00 background
/// for failed/cancelled rows so problem jobs stand out without using red
/// (CLAUDE.md memory note: user is red/green colourblind).</summary>
public sealed class JobListRowVm : ReactiveObject
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

    private JobListRowVm(JobSummaryDto src)
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

    public static JobListRowVm From(JobSummaryDto src) => new(src);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KiB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " MiB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture) + " GiB";
    }
}
