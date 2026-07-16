// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/ServerAdminViewModel.cs
// Phase 3 server admin dialog. Polls server.status once per second while
// the dialog is visible. Exposes Start/Restart/Kill controls that operate
// on a local --server child process the UI spawns (Process.Start of own
// exe). The Apply button rewrites ServerConfig and signals the running
// server to soft-restart on next idle (v1: just kill + respawn).

using System;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;
using FracturingFog.Client;
using FracturingFog.Server;
using FracturingFog.Server.Protocol;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class ServerAdminViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _poll;
    private Process? _childProc;
    private bool _disposed;

    public ServerAdminViewModel()
    {
        Config = ServerConfig.LoadOrDefault();
        Port = Config.Port;
        MaxMinutes = Config.MaxMinutes;
        AllowOverride = Config.AllowOverride;
        CertsDir = Config.ServerCertsDir ?? "";
        LogDir = Config.LogDir ?? "";
        WorkDir = Config.WorkDir ?? "";
        WatermarkMode = Config.WatermarkMode;
        ServerCustomWatermarkName = Config.ServerCustomWatermarkName;
        try { FracturingFog.Models.UserWatermarkStore.Instance.Load(); } catch { }
        WatermarkNames = new System.Collections.ObjectModel.ObservableCollection<string>(
            FracturingFog.Models.UserWatermarkStore.Instance.EnumerateNames());

        StartCommand   = ReactiveCommand.Create(Start);
        RestartCommand = ReactiveCommand.Create(Restart);
        KillCommand    = ReactiveCommand.Create(Kill);
        ApplyCommand   = ReactiveCommand.Create(Apply);
        RefreshCommand = ReactiveCommand.CreateFromTask(PollOnceAsync);
        CloseCommand   = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        OpenClusterDashboardCommand = ReactiveCommand.Create(
            () => OpenClusterDashboardRequested?.Invoke(this, EventArgs.Empty));
        // D-5e — sibling launcher for the cluster-knob editor (max jobs,
        // artifact retention, tile target). Same routing shape as
        // OpenClusterDashboard: shell flips a visibility flag, MainWindow
        // owns the window lifecycle.
        OpenMasterConfigCommand = ReactiveCommand.Create(
            () => OpenMasterConfigRequested?.Invoke(this, EventArgs.Empty));
        BrowseCertsDirCommand = ReactiveCommand.Create(() => BrowseFolderRequested?.Invoke(this,
            ("certsDir", (Action<string>)(p => CertsDir = p))));
        BrowseLogDirCommand   = ReactiveCommand.Create(() => BrowseFolderRequested?.Invoke(this,
            ("logDir",   (Action<string>)(p => LogDir = p))));
        BrowseWorkDirCommand  = ReactiveCommand.Create(() => BrowseFolderRequested?.Invoke(this,
            ("workDir",  (Action<string>)(p => WorkDir = p))));

        // 5-second cadence: each poll opens a fresh mTLS handshake (no
        // pooling in v1), which costs ~50-100 ms server CPU. At 1 Hz the
        // admin dialog imposes 5-10% CPU floor on an otherwise idle
        // server. 5 s keeps the UI responsive without paying that tax.
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) =>
        {
            if (_disposed) return;
            await PollOnceAsync();
        });
    }

    public ServerConfig Config { get; private set; }

    private int _port;
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, value); }

    private int _maxMinutes;
    public int MaxMinutes { get => _maxMinutes; set => this.RaiseAndSetIfChanged(ref _maxMinutes, value); }

    private bool _allowOverride;
    public bool AllowOverride { get => _allowOverride; set => this.RaiseAndSetIfChanged(ref _allowOverride, value); }

    private string _certsDir = "";
    public string CertsDir { get => _certsDir; set => this.RaiseAndSetIfChanged(ref _certsDir, value); }

    private string _logDir = "";
    public string LogDir { get => _logDir; set => this.RaiseAndSetIfChanged(ref _logDir, value); }

    public System.Collections.ObjectModel.ObservableCollection<string> WatermarkNames { get; private set; } = new();

    private ServerWatermarkMode _watermarkMode = ServerWatermarkMode.Default;
    public ServerWatermarkMode WatermarkMode
    {
        get => _watermarkMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _watermarkMode, value);
            this.RaisePropertyChanged(nameof(IsWatermarkModeDefault));
            this.RaisePropertyChanged(nameof(IsWatermarkModeCustom));
            this.RaisePropertyChanged(nameof(IsWatermarkModeClient));
        }
    }
    public bool IsWatermarkModeDefault { get => WatermarkMode == ServerWatermarkMode.Default; set { if (value) WatermarkMode = ServerWatermarkMode.Default; } }
    public bool IsWatermarkModeCustom  { get => WatermarkMode == ServerWatermarkMode.Custom;  set { if (value) WatermarkMode = ServerWatermarkMode.Custom;  } }
    public bool IsWatermarkModeClient  { get => WatermarkMode == ServerWatermarkMode.Client;  set { if (value) WatermarkMode = ServerWatermarkMode.Client;  } }

    private string? _serverCustomWatermarkName;
    public string? ServerCustomWatermarkName
    {
        get => _serverCustomWatermarkName;
        set => this.RaiseAndSetIfChanged(ref _serverCustomWatermarkName, value);
    }

    private string _workDir = "";
    public string WorkDir { get => _workDir; set => this.RaiseAndSetIfChanged(ref _workDir, value); }

    private string _status = "Unknown";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _isOnline;
    public bool IsOnline
    {
        get => _isOnline;
        set => this.RaiseAndSetIfChanged(ref _isOnline, value);
    }

    private long _uptimeSeconds;
    public long UptimeSeconds { get => _uptimeSeconds; set => this.RaiseAndSetIfChanged(ref _uptimeSeconds, value); }

    private int _inFlight;
    public int InFlight { get => _inFlight; set => this.RaiseAndSetIfChanged(ref _inFlight, value); }

    private long _completed;
    public long Completed { get => _completed; set => this.RaiseAndSetIfChanged(ref _completed, value); }

    private long _failed;
    public long Failed { get => _failed; set => this.RaiseAndSetIfChanged(ref _failed, value); }

    private string? _lastError;
    public string? LastError { get => _lastError; set => this.RaiseAndSetIfChanged(ref _lastError, value); }

    public void StartPolling() => _poll.Start();
    public void StopPolling()  => _poll.Stop();

    public async Task PollOnceAsync()
    {
        // First cheap probe — TCP port. mTLS handshake costs more, so we
        // only attempt a server.status RPC when the port is listening AND
        // a client cert path is configured.
        bool listening = await ServerInstanceProbe.IsListeningAsync("127.0.0.1", Config.Port).ConfigureAwait(true);
        if (!listening)
        {
            IsOnline = false;
            Status = "off";
            return;
        }
        IsOnline = true;
        Status = $"listening on 127.0.0.1:{Config.Port}";

        // Try fetch detailed status if we have a self-signed bundle to use.
        string certDir = ServerConfig.DefaultCertDir();
        string clientPfx = Path.Combine(certDir, "client.pfx");
        string caPfx     = Path.Combine(certDir, "ca.pfx");
        if (!File.Exists(clientPfx) || !File.Exists(caPfx)) return;

        try
        {
            await using var conn = await FFClientConnection.ConnectAsync(new FFClientConnection.ConnectOptions
            {
                Host = "127.0.0.1",
                Port = Config.Port,
                ClientCertPath = clientPfx,
                ServerCaCertPath = caPfx,
            }, CancellationToken.None).ConfigureAwait(true);

            ServerStatusDto s = await conn.GetStatusAsync(CancellationToken.None).ConfigureAwait(true);
            UptimeSeconds = s.UptimeSeconds;
            InFlight = s.InFlight;
            Completed = s.Completed;
            Failed = s.Failed;
            LastError = string.IsNullOrEmpty(s.LastErrorCode) ? null : $"{s.LastErrorCode}: {s.LastErrorMessage}";
            Status = $"running, queue={s.InFlight}/{s.QueueDepth}, completed={s.Completed}, max={s.MaxMinutes}min";
        }
        catch (Exception ex)
        {
            Status = $"listening but status RPC failed: {ex.Message}";
        }
    }

    private void Start()
    {
        if (IsOnline)
        {
            LastError = "already running";
            return;
        }
        SpawnChild();
    }

    private void Restart()
    {
        Kill();
        Thread.Sleep(300);
        SpawnChild();
    }

    private void Kill()
    {
        try
        {
            if (_childProc != null && !_childProc.HasExited)
            {
                _childProc.Kill(entireProcessTree: false);
                _childProc.WaitForExit(2000);
            }
        }
        catch { }
        _childProc = null;
    }

    private void SpawnChild()
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName
                       ?? Assembly.GetEntryAssembly()?.Location
                       ?? "FracturingFog.exe";
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            psi.ArgumentList.Add("--server");
            psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--max-minutes"); psi.ArgumentList.Add(MaxMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (AllowOverride) psi.ArgumentList.Add("--allow-override");

            _childProc = Process.Start(psi);
            LastError = _childProc != null ? "child started" : "child failed to start";
        }
        catch (Exception ex)
        {
            LastError = "start failed: " + ex.Message;
        }
    }

    private void Apply()
    {
        Config.Port = Port;
        Config.MaxMinutes = MaxMinutes;
        Config.AllowOverride = AllowOverride;
        // Empty string in any path TextBox means "go back to default". Null
        // out on the config so EffectiveCertsDir() / LogDir resolve to the
        // shipped %APPDATA% defaults rather than persisting an empty path
        // that would later be Directory.CreateDirectory'd as the working dir.
        Config.ServerCertsDir = string.IsNullOrWhiteSpace(CertsDir) ? null : CertsDir.Trim();
        Config.LogDir         = string.IsNullOrWhiteSpace(LogDir)   ? null : LogDir.Trim();
        Config.WorkDir        = string.IsNullOrWhiteSpace(WorkDir)  ? null : WorkDir.Trim();
        Config.WatermarkMode  = WatermarkMode;
        Config.ServerCustomWatermarkName = string.IsNullOrWhiteSpace(ServerCustomWatermarkName)
            ? null : ServerCustomWatermarkName!.Trim();
        try { Config.Save(); }
        catch (Exception ex) { LastError = "save failed: " + ex.Message; return; }

        // The running server picked up --max-minutes / --allow-override at
        // spawn and does not re-read server-config.json. The previous
        // behaviour ("config saved" but render still uses the old timeout)
        // was misleading — users would change Max minutes to 1 and watch a
        // 144-second render complete because the running server still had
        // its startup value. Auto-restart when we own the child so the new
        // settings take effect immediately; otherwise surface that the
        // running server must be restarted out-of-band.
        bool childOwned = _childProc != null && !_childProc.HasExited;
        if (childOwned)
        {
            try
            {
                Restart();
                LastError = $"config saved and child restarted (max={MaxMinutes}min, port={Port})";
            }
            catch (Exception ex)
            {
                LastError = "config saved, child restart failed: " + ex.Message;
            }
            return;
        }

        if (IsOnline)
        {
            LastError = "config saved BUT a server is running that this UI did not spawn — " +
                        "stop it and start a new one for the new settings to take effect";
        }
        else
        {
            LastError = "config saved (server is off — new settings apply on next Start)";
        }
    }

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> RestartCommand { get; }
    public ReactiveCommand<Unit, Unit> KillCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenClusterDashboardCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenMasterConfigCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseCertsDirCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseLogDirCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseWorkDirCommand { get; }

    public event EventHandler? CloseRequested;
    /// <summary>Raised when the operator hits "Cluster Dashboard…". The shell
    /// flips its dashboard visibility flag in response — the SAVM itself
    /// owns no knowledge of the cluster view, mirroring how Help and other
    /// sibling windows are routed.</summary>
    public event EventHandler? OpenClusterDashboardRequested;
    /// <summary>D-5e — raised when the operator hits "Master Config…". Shell
    /// flips IsMasterConfigVisible; MainWindow drives the window lifecycle.</summary>
    public event EventHandler? OpenMasterConfigRequested;
    public event EventHandler<(string kind, Action<string> assign)>? BrowseFolderRequested;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
    }
}
