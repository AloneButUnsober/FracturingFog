// ViewModels/FFClientViewModel.cs
// Phase 3 client dialog. Holds:
//   • Saved connections combo (ClientConnectionStore-backed, AES vault locked)
//   • Master-password unlock state
//   • Live connection form fields (Name / Host / Port / cert paths)
//   • Saved render presets combo + form fields
//   • Render output target + return-mode radio
//   • Render button (image or video, sync or fire-and-forget)
// Block list: UserEquation / Sandbox / UserBulb are filtered out of the
// FractalType combo so the user cannot author a remote request the server
// will refuse anyway.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Client;
using FracturingFog.Models;
using FracturingFog.Server.Protocol;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class FFClientViewModel : ViewModelBase
{
    public ClientConnectionStore Connections { get; private set; }
    public RenderOptionsStore Presets { get; private set; }

    public ObservableCollection<string> ConnectionNames { get; } = new();
    public ObservableCollection<string> PresetNames { get; } = new();

    /// <summary>Populated from host IColorThemeService when supplied; otherwise empty
    /// (user can still type a region name manually).</summary>
    public ObservableCollection<string> RegionNames { get; } = new();

    /// <summary>Populated from host IColorThemeService when supplied; otherwise empty
    /// (user can still type a theme name manually).</summary>
    public ObservableCollection<string> ThemeNames { get; } = new();

    /// <summary>14 allowed types (3 user-code types filtered out).</summary>
    public ObservableCollection<string> FractalTypes { get; } = new(new[]
    {
        "Mandelbrot", "Julia", "BurningShip", "Tricorn", "Multibrot", "Phoenix",
        "Newton", "Nova", "BuddhaBrot", "IFS", "LSystem", "StrangeAttractor",
        "Mandelbulb", "TearDrop",
    });

    public ObservableCollection<string> QualityPresets { get; } = new(new[]
    {
        "Draft", "Standard", "High", "Ultra", "Extreme",
    });

    public ObservableCollection<string> Modes { get; } = new(new[] { "image", "video" });
    public ObservableCollection<string> ReturnModes { get; } = new(new[] { "inline", "saved-path" });
    public ObservableCollection<string> LosslessPresets { get; } = new(new[]
    {
        "none", "h264", "ffv1", "h264hq",
    });

    public FFClientViewModel() : this(null) { }

    public FFClientViewModel(IColorThemeService? themeService)
    {
        Connections = ClientConnectionStore.LoadOrCreate();
        Presets = RenderOptionsStore.LoadOrCreate();
        RefreshConnectionNames();
        RefreshPresetNames();
        if (themeService != null)
        {
            foreach (var n in themeService.EnumerateRegionNames()) RegionNames.Add(n);
            foreach (var n in themeService.EnumerateThemeNames()) ThemeNames.Add(n);
        }

        UnlockCommand           = ReactiveCommand.Create(Unlock);
        SaveConnectionCommand   = ReactiveCommand.Create(SaveConnection);
        DeleteConnectionCommand = ReactiveCommand.Create(DeleteConnection);
        SavePresetCommand       = ReactiveCommand.Create(SavePreset);
        DeletePresetCommand     = ReactiveCommand.Create(DeletePreset);
        BrowseClientCertCommand = ReactiveCommand.Create(() => BrowseFileRequested?.Invoke(this, ("clientCert", (Action<string>)(p => ClientCertPath = p))));
        BrowseServerCaCommand   = ReactiveCommand.Create(() => BrowseFileRequested?.Invoke(this, ("serverCa",   (Action<string>)(p => ServerCaCertPath = p))));
        BrowseOutputCommand     = ReactiveCommand.Create(() => BrowseFileRequested?.Invoke(this, ("output",     (Action<string>)(p => OutputPath = p))));
        RenderCommand           = ReactiveCommand.CreateFromTask(RenderAsync);
        CloseCommand            = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    // ── Master password state ─────────────────────────────────────────────

    private string _masterPassword = "";
    public string MasterPassword
    {
        get => _masterPassword;
        set => this.RaiseAndSetIfChanged(ref _masterPassword, value);
    }

    private bool _isUnlocked;
    public bool IsUnlocked
    {
        get => _isUnlocked;
        set => this.RaiseAndSetIfChanged(ref _isUnlocked, value);
    }

    private string _unlockStatus = "Enter master password.";
    public string UnlockStatus
    {
        get => _unlockStatus;
        set => this.RaiseAndSetIfChanged(ref _unlockStatus, value);
    }

    private void Unlock()
    {
        if (string.IsNullOrEmpty(MasterPassword))
        {
            UnlockStatus = "Empty password — cannot unlock saved entries.";
            return;
        }
        bool hasSealed = Connections.Entries.Exists(e => e.SealedPfxPassword != null);
        if (!hasSealed)
        {
            // Vault empty (or no sealed cert passwords). VerifyMasterPassword
            // can't actually verify anything in this state, so just accept the
            // pw and tell the user it becomes the master pw on first save.
            IsUnlocked = true;
            UnlockStatus = Connections.Entries.Count == 0
                ? "Unlocked (vault empty — this password becomes the master pw when you save a connection)."
                : $"Unlocked ({Connections.Entries.Count} saved, none with sealed cert pw — first sealed save sets the master pw).";
            return;
        }
        if (Connections.VerifyMasterPassword(MasterPassword))
        {
            IsUnlocked = true;
            UnlockStatus = $"Unlocked ({Connections.Entries.Count} saved).";
        }
        else
        {
            IsUnlocked = false;
            UnlockStatus = "Wrong password — the master pw is the one used on the first Save of a connection with a cert password.";
        }
    }

    // ── Connection form ───────────────────────────────────────────────────

    private string? _selectedConnection;
    public string? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedConnection, value);
            LoadConnectionIntoForm(value);
        }
    }

    private string _connectionName = "local";
    public string ConnectionName { get => _connectionName; set => this.RaiseAndSetIfChanged(ref _connectionName, value); }

    private string _host = "127.0.0.1";
    public string Host { get => _host; set => this.RaiseAndSetIfChanged(ref _host, value); }

    private int _port = 47823;
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, value); }

    private string _clientCertPath = "";
    public string ClientCertPath { get => _clientCertPath; set => this.RaiseAndSetIfChanged(ref _clientCertPath, value); }

    private string _serverCaCertPath = "";
    public string ServerCaCertPath { get => _serverCaCertPath; set => this.RaiseAndSetIfChanged(ref _serverCaCertPath, value); }

    private string _clientCertPassword = "";
    public string ClientCertPassword { get => _clientCertPassword; set => this.RaiseAndSetIfChanged(ref _clientCertPassword, value); }

    private string _connectionRemark = "";
    public string ConnectionRemark { get => _connectionRemark; set => this.RaiseAndSetIfChanged(ref _connectionRemark, value); }

    private void LoadConnectionIntoForm(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var e = Connections.Entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (e == null) return;
        ConnectionName = e.Name;
        Host = e.Host;
        Port = e.Port;
        ClientCertPath = e.ClientCertPath;
        ServerCaCertPath = e.ServerCaCertPath;
        ConnectionRemark = e.Remark ?? "";
        if (IsUnlocked && e.SealedPfxPassword != null)
        {
            try { ClientCertPassword = Connections.UnlockPfxPassword(e, MasterPassword) ?? ""; }
            catch { ClientCertPassword = ""; }
        }
        else
        {
            ClientCertPassword = "";
        }
    }

    private void SaveConnection()
    {
        if (string.IsNullOrWhiteSpace(ConnectionName))
        {
            LastError = "Connection name is required.";
            return;
        }
        if (!IsUnlocked && !string.IsNullOrEmpty(ClientCertPassword))
        {
            LastError = "Unlock master password first (need it to seal the cert password).";
            return;
        }

        var existing = Connections.Entries.FirstOrDefault(
            x => string.Equals(x.Name, ConnectionName, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new ClientConnectionEntry { Name = ConnectionName };
            Connections.Entries.Add(existing);
        }
        existing.Host = Host;
        existing.Port = Port;
        existing.ClientCertPath = ClientCertPath;
        existing.ServerCaCertPath = ServerCaCertPath;
        existing.Remark = string.IsNullOrEmpty(ConnectionRemark) ? null : ConnectionRemark;
        Connections.SealPfxPassword(existing, ClientCertPassword, MasterPassword);
        Connections.Save();
        RefreshConnectionNames();
        SelectedConnection = existing.Name;
        LastError = "Saved.";
    }

    private void DeleteConnection()
    {
        if (string.IsNullOrEmpty(SelectedConnection)) return;
        Connections.Entries.RemoveAll(x => string.Equals(x.Name, SelectedConnection, StringComparison.OrdinalIgnoreCase));
        Connections.Save();
        RefreshConnectionNames();
        SelectedConnection = null;
    }

    private void RefreshConnectionNames()
    {
        ConnectionNames.Clear();
        foreach (var e in Connections.Entries.OrderBy(x => x.Name)) ConnectionNames.Add(e.Name);
    }

    // ── Render preset form (mirrors RenderRequestDto) ─────────────────────

    private string? _selectedPreset;
    public string? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPreset, value);
            LoadPresetIntoForm(value);
        }
    }

    private string _presetName = "untitled";
    public string PresetName { get => _presetName; set => this.RaiseAndSetIfChanged(ref _presetName, value); }

    private string _mode = "image";
    public string Mode
    {
        get => _mode;
        set
        {
            this.RaiseAndSetIfChanged(ref _mode, value);
            this.RaisePropertyChanged(nameof(IsVideoMode));
            this.RaisePropertyChanged(nameof(ModeIndicator));
            this.RaisePropertyChanged(nameof(ModeIndicatorBrush));
            this.RaisePropertyChanged(nameof(RenderButtonText));
        }
    }

    /// <summary>True when Mode == "video" — bound by the view to highlight the
    /// video sub-form and surface a banner so the user can see at a glance the
    /// upcoming Render will produce a movie, not a still.</summary>
    public bool IsVideoMode => string.Equals(Mode, "video", StringComparison.OrdinalIgnoreCase);

    public string ModeIndicator => IsVideoMode ? "▶ VIDEO MODE" : "■ IMAGE MODE";

    /// <summary>Hex color for the indicator banner — orange for video, blue-grey
    /// for image. Bound as a string and converted in XAML via a SolidColorBrush.</summary>
    public string ModeIndicatorBrush => IsVideoMode ? "#D98C2A" : "#4A6480";

    /// <summary>Label used on the primary Render button so the user can see at
    /// a glance whether clicking will produce a still or a movie. The same
    /// button drives both code paths — the Mode combo decides which.</summary>
    public string RenderButtonText => IsVideoMode ? "Render Video" : "Render Image";

    private string _fractalType = "Mandelbrot";
    public string FractalType { get => _fractalType; set => this.RaiseAndSetIfChanged(ref _fractalType, value); }

    private string _regionName = "";
    public string RegionName { get => _regionName; set => this.RaiseAndSetIfChanged(ref _regionName, value); }

    private string _themeName = "HSV";
    public string ThemeName { get => _themeName; set => this.RaiseAndSetIfChanged(ref _themeName, value); }

    private string _qualityName = "Standard";
    public string QualityName { get => _qualityName; set => this.RaiseAndSetIfChanged(ref _qualityName, value); }

    private int _width = 1920;
    public int Width { get => _width; set => this.RaiseAndSetIfChanged(ref _width, value); }

    private int _height = 1080;
    public int Height { get => _height; set => this.RaiseAndSetIfChanged(ref _height, value); }

    private double? _centerX;
    public double? CenterX { get => _centerX; set => this.RaiseAndSetIfChanged(ref _centerX, value); }

    private double? _centerY;
    public double? CenterY { get => _centerY; set => this.RaiseAndSetIfChanged(ref _centerY, value); }

    private double? _zoom;
    public double? Zoom { get => _zoom; set => this.RaiseAndSetIfChanged(ref _zoom, value); }

    private int? _iterations;
    public int? Iterations { get => _iterations; set => this.RaiseAndSetIfChanged(ref _iterations, value); }

    // Video sub-form.
    private double _videoSeconds = 20.0;
    public double VideoSeconds { get => _videoSeconds; set => this.RaiseAndSetIfChanged(ref _videoSeconds, value); }

    private int _videoFps = 30;
    public int VideoFps { get => _videoFps; set => this.RaiseAndSetIfChanged(ref _videoFps, value); }

    private double _videoStartZoom = 0.5;
    public double VideoStartZoom { get => _videoStartZoom; set => this.RaiseAndSetIfChanged(ref _videoStartZoom, value); }

    private bool _videoReverse;
    public bool VideoReverse { get => _videoReverse; set => this.RaiseAndSetIfChanged(ref _videoReverse, value); }

    private string _lossless = "none";
    public string Lossless { get => _lossless; set => this.RaiseAndSetIfChanged(ref _lossless, value); }

    private bool _keepFrames;
    public bool KeepFrames { get => _keepFrames; set => this.RaiseAndSetIfChanged(ref _keepFrames, value); }

    // Output target.
    private string _outputPath = "";
    public string OutputPath { get => _outputPath; set => this.RaiseAndSetIfChanged(ref _outputPath, value); }

    private string _returnMode = "inline";
    public string ReturnMode { get => _returnMode; set => this.RaiseAndSetIfChanged(ref _returnMode, value); }

    // Optional override timeout, minutes.
    private int? _requestedMaxMinutes;
    public int? RequestedMaxMinutes { get => _requestedMaxMinutes; set => this.RaiseAndSetIfChanged(ref _requestedMaxMinutes, value); }

    public RenderRequestDto BuildRequest()
    {
        return new RenderRequestDto
        {
            Mode = Mode,
            FractalType = FractalType,
            RegionName = string.IsNullOrWhiteSpace(RegionName) ? null : RegionName,
            ThemeName = ThemeName,
            QualityName = QualityName,
            Width = Width,
            Height = Height,
            CenterX = CenterX,
            CenterY = CenterY,
            Zoom = Zoom,
            Iterations = Iterations,
            VideoSeconds = VideoSeconds,
            VideoFps = VideoFps,
            VideoStartZoom = VideoStartZoom,
            VideoReverse = VideoReverse,
            Lossless = Lossless,
            KeepFrames = KeepFrames ? true : null,
            ReturnMode = ReturnMode,
            RequestedMaxMinutes = RequestedMaxMinutes,
        };
    }

    private void LoadPresetIntoForm(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var p = Presets.FindByName(name);
        if (p == null) return;
        PresetName = p.Name;
        var r = p.Request;
        Mode = r.Mode;
        FractalType = r.FractalType;
        RegionName = r.RegionName ?? "";
        ThemeName = r.ThemeName;
        QualityName = r.QualityName;
        Width = r.Width;
        Height = r.Height;
        CenterX = r.CenterX;
        CenterY = r.CenterY;
        Zoom = r.Zoom;
        Iterations = r.Iterations;
        VideoSeconds = r.VideoSeconds;
        VideoFps = r.VideoFps;
        VideoStartZoom = r.VideoStartZoom;
        VideoReverse = r.VideoReverse;
        Lossless = r.Lossless;
        KeepFrames = r.KeepFrames ?? false;
        ReturnMode = r.ReturnMode;
        RequestedMaxMinutes = r.RequestedMaxMinutes;
        OutputPath = p.SuggestedOutputPath ?? "";
    }

    private void SavePreset()
    {
        if (string.IsNullOrWhiteSpace(PresetName)) { LastError = "Preset name required."; return; }
        var existing = Presets.FindByName(PresetName);
        if (existing == null) { existing = new RenderOptionPreset { Name = PresetName }; Presets.Presets.Add(existing); }
        existing.Request = BuildRequest();
        existing.SuggestedOutputPath = string.IsNullOrEmpty(OutputPath) ? null : OutputPath;
        Presets.Save();
        RefreshPresetNames();
        SelectedPreset = existing.Name;
        LastError = "Preset saved.";
    }

    private void DeletePreset()
    {
        if (string.IsNullOrEmpty(SelectedPreset)) return;
        Presets.Presets.RemoveAll(p => string.Equals(p.Name, SelectedPreset, StringComparison.OrdinalIgnoreCase));
        Presets.Save();
        RefreshPresetNames();
        SelectedPreset = null;
    }

    private void RefreshPresetNames()
    {
        PresetNames.Clear();
        foreach (var p in Presets.Presets.OrderBy(x => x.Name)) PresetNames.Add(p.Name);
    }

    // ── Render ────────────────────────────────────────────────────────────

    private bool _isRendering;
    public bool IsRendering { get => _isRendering; set => this.RaiseAndSetIfChanged(ref _isRendering, value); }

    private string _lastError = "";
    public string LastError { get => _lastError; set => this.RaiseAndSetIfChanged(ref _lastError, value); }

    private string _renderStatus = "";
    public string RenderStatus { get => _renderStatus; set => this.RaiseAndSetIfChanged(ref _renderStatus, value); }

    private async Task RenderAsync()
    {
        LastError = "";
        if (string.IsNullOrEmpty(SelectedConnection))
        {
            LastError = "Select a saved connection first.";
            return;
        }
        var entry = Connections.Entries.FirstOrDefault(x => string.Equals(x.Name, SelectedConnection, StringComparison.OrdinalIgnoreCase));
        if (entry == null) { LastError = "Connection vanished."; return; }
        if (entry.SealedPfxPassword != null && !IsUnlocked)
        {
            LastError = "Unlock master password to use this connection.";
            return;
        }
        if (string.IsNullOrWhiteSpace(entry.ClientCertPath))
        {
            LastError = "This connection has no client cert (.pfx) path. Open the connection, browse to the client .pfx, and Save.";
            return;
        }
        if (!File.Exists(entry.ClientCertPath))
        {
            LastError = $"Client cert not found at: {entry.ClientCertPath}";
            return;
        }
        if (!string.IsNullOrWhiteSpace(entry.ServerCaCertPath) && !File.Exists(entry.ServerCaCertPath))
        {
            LastError = $"Server CA cert not found at: {entry.ServerCaCertPath}";
            return;
        }

        string? pfxPwd = null;
        if (entry.SealedPfxPassword != null)
        {
            try { pfxPwd = Connections.UnlockPfxPassword(entry, MasterPassword); }
            catch (Exception ex) { LastError = "Vault unlock failed: " + ex.Message; return; }
        }

        IsRendering = true;
        RenderStatus = "Connecting…";
        try
        {
            await using var conn = await FFClientConnection.ConnectAsync(new FFClientConnection.ConnectOptions
            {
                Host = entry.Host,
                Port = entry.Port,
                ClientCertPath = entry.ClientCertPath,
                ClientCertPassword = pfxPwd,
                ServerCaCertPath = string.IsNullOrEmpty(entry.ServerCaCertPath) ? null : entry.ServerCaCertPath,
            }, CancellationToken.None).ConfigureAwait(false);

            RenderStatus = "Rendering…";
            var req = BuildRequest();
            RenderResponseDto resp = Mode == "video"
                ? await conn.RenderVideoAsync(req, CancellationToken.None).ConfigureAwait(false)
                : await conn.RenderImageAsync(req, CancellationToken.None).ConfigureAwait(false);

            await HandleResponseAsync(resp, req).ConfigureAwait(false);
            RenderStatus = $"Done ({resp.ElapsedMs} ms, {resp.Width}x{resp.Height}).";
        }
        catch (FFServerException ex)
        {
            LastError = $"Server: [{ex.Error.Code}] {ex.Error.Message}";
            RenderStatus = "Failed.";
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            RenderStatus = "Failed.";
        }
        finally
        {
            IsRendering = false;
        }
    }

    private async Task HandleResponseAsync(RenderResponseDto resp, RenderRequestDto req)
    {
        bool isVideo = req.Mode == "video";
        if (req.ReturnMode == "inline")
        {
            string? b64 = isVideo ? resp.Mp4BytesBase64 : resp.PngBytesBase64;
            if (string.IsNullOrEmpty(b64)) { LastError = "Server returned no bytes."; return; }
            byte[] bytes = Convert.FromBase64String(b64);
            string path = OutputPath;
            if (string.IsNullOrEmpty(path))
            {
                // Bubble up — host pops a SaveFileDialog and writes.
                var args = new SaveBytesEventArgs
                {
                    DefaultExtension = isVideo ? (req.Lossless == "ffv1" ? "mkv" : "mp4") : "png",
                    Bytes = bytes,
                };
                SaveBytesRequested?.Invoke(this, args);
                await args.Completion.Task.ConfigureAwait(false);
                if (!string.IsNullOrEmpty(args.WrittenPath))
                    LastError = $"Saved to {args.WrittenPath}";
                else
                    LastError = "User cancelled.";
                return;
            }
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            LastError = $"Saved to {path}";
        }
        else
        {
            LastError = $"Server retained: {resp.SavedPath}";
        }
    }

    // ── Commands + events ─────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit>   UnlockCommand { get; }
    public ReactiveCommand<Unit, Unit>   SaveConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit>   DeleteConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit>   SavePresetCommand { get; }
    public ReactiveCommand<Unit, Unit>   DeletePresetCommand { get; }
    public ReactiveCommand<Unit, Unit>   BrowseClientCertCommand { get; }
    public ReactiveCommand<Unit, Unit>   BrowseServerCaCommand { get; }
    public ReactiveCommand<Unit, Unit>   BrowseOutputCommand { get; }
    public ReactiveCommand<Unit, Unit>   RenderCommand { get; }
    public ReactiveCommand<Unit, Unit>   CloseCommand { get; }

    public event EventHandler? CloseRequested;
    public event EventHandler<(string kind, Action<string> assign)>? BrowseFileRequested;
    public event EventHandler<SaveBytesEventArgs>? SaveBytesRequested;
}

public sealed class SaveBytesEventArgs : EventArgs
{
    public required string DefaultExtension { get; init; }
    public required byte[] Bytes { get; init; }
    public string? WrittenPath { get; set; }
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
