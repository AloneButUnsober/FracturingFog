// Server/ServerConfig.cs
// Runtime configuration written to %APPDATA%\FracturingFog\server-config.json
// and edited live by the Avalonia ServerAdmin dialog. Loaded once at
// --server startup; the ServerAdmin "Apply" path rewrites the file and
// triggers a soft-restart on the next idle window.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using FracturingFog.Abstractions;

namespace FracturingFog.Server;

public sealed class ServerConfig
{
    public const int DefaultPort = 47823;
    public const int DefaultMaxMinutes = 240;

    [JsonPropertyName("port")]            public int    Port            { get; set; } = DefaultPort;

    /// <summary>Listen interface. Defaults to 127.0.0.1 so a fresh install
    /// does not expose the server to the LAN — operators opt in to wider
    /// reach with --bind 0.0.0.0 (or a specific NIC address).</summary>
    [JsonPropertyName("bindAddress")]     public string BindAddress     { get; set; } = "127.0.0.1";

    [JsonPropertyName("maxMinutes")]      public int    MaxMinutes      { get; set; } = DefaultMaxMinutes;
    [JsonPropertyName("allowOverride")]   public bool   AllowOverride   { get; set; }
    [JsonPropertyName("queueDepth")]      public int    QueueDepth      { get; set; } = 1;
    [JsonPropertyName("maxConcurrentConnections")]
    public int MaxConcurrentConnections { get; set; } = 32;

    /// <summary>Sustained accepted-TCP-connection rate per remote IP, per
    /// minute. Bursts are still allowed up to <see cref="RateLimitBurst"/>.
    /// 0 disables the per-IP limiter (only the global connection cap applies).</summary>
    [JsonPropertyName("rateLimitPerMinute")] public int RateLimitPerMinute { get; set; }

    /// <summary>Maximum standing token allowance per IP. Higher values let
    /// legitimate retry loops + UI reconnects burst without penalty; lower
    /// values close attacker SYN floods faster.</summary>
    [JsonPropertyName("rateLimitBurst")]     public int RateLimitBurst     { get; set; } = 10;

    /// <summary>D-6c — per-IP per-minute cap on dispatched client-role JSON-RPC
    /// calls inside an authenticated session. Layered on top of the per-IP
    /// TCP-accept limiter (<see cref="RateLimitPerMinute"/>) which only sees
    /// connection establishment. 0 disables. Default 600 = sustained 10
    /// calls/sec per IP (room for 1 Hz status polling + occasional fetch).</summary>
    [JsonPropertyName("clientCallPerMinute")] public int ClientCallPerMinute { get; set; } = 600;

    /// <summary>D-6c — burst allowance for the client-call limiter. Larger
    /// values tolerate UI reconnect storms; smaller values catch a buggy
    /// client that loops on job.status faster.</summary>
    [JsonPropertyName("clientCallBurst")]     public int ClientCallBurst     { get; set; } = 30;

    /// <summary>D-6c — per-thumbprint per-minute cap on worker tile.next
    /// long-poll calls. Defends the dispatcher against a runaway worker
    /// spinning the long-poll. Other worker methods (heartbeat, deliver,
    /// register, error) bypass this gate. 0 disables. Default 600 = 10/sec
    /// — well above the steady-state tile.next cadence (1 call per
    /// TileNextHold ≈ 30 s) but tight enough to catch a stuck loop.</summary>
    [JsonPropertyName("workerTileNextPerMinute")] public int WorkerTileNextPerMinute { get; set; } = 600;

    /// <summary>D-6c — burst allowance for the worker tile.next limiter.
    /// Default 30 covers normal reconnect/replay storms after a worker
    /// resume.</summary>
    [JsonPropertyName("workerTileNextBurst")]     public int WorkerTileNextBurst     { get; set; } = 30;

    /// <summary>When true, restrict TLS to v1.3 only. Default false to
    /// keep older clients compatible. Set true for hardened deployments —
    /// TLS 1.2 retains a number of deprecated ciphersuites + RSA key
    /// exchange that 1.3 dropped.</summary>
    [JsonPropertyName("requireTls13")]    public bool   RequireTls13    { get; set; }

    /// <summary>Cert revocation policy applied during the TLS handshake.
    /// "none" (default) skips CRL/OCSP — appropriate for the self-signed
    /// dev bundle which has no revocation infra. "online" / "offline"
    /// map to X509RevocationMode.Online / Offline for real-PKI deployments.</summary>
    [JsonPropertyName("revocationCheckMode")]
    public string RevocationCheckMode { get; set; } = "none";

    /// <summary>Optional pin: when non-empty, the presented client cert
    /// thumbprint (hex, case-insensitive, spaces/dashes ignored) must
    /// match one of these in addition to chaining to <see cref="ClientCaCertPath"/>.
    /// Empty = chain-trust alone is sufficient.</summary>
    [JsonPropertyName("allowedClientThumbprints")]
    public System.Collections.Generic.List<string> AllowedClientThumbprints { get; set; } = new();

    /// <summary>Age in hours above which leftover job-* subdirs in
    /// <see cref="WorkDir"/> are deleted on server startup. 0 disables
    /// the sweep. Default 1 — anything older than one hour is from a
    /// previous crash or kill and is safe to discard.</summary>
    [JsonPropertyName("workDirStaleHours")] public double WorkDirStaleHours { get; set; } = 1.0;

    [JsonPropertyName("serverCertPath")]  public string? ServerCertPath { get; set; }
    [JsonPropertyName("clientCaCertPath")] public string? ClientCaCertPath { get; set; }

    /// <summary>Override directory for the self-signed dev bundle (and the
    /// implicit lookup of <c>server.pfx</c> + <c>ca.pfx</c> when explicit
    /// per-file paths are not set). Null = use <see cref="DefaultCertDir"/>.
    /// Lower precedence than <see cref="ServerCertPath"/> /
    /// <see cref="ClientCaCertPath"/>: when both per-file paths are set they
    /// win and this directory is ignored.</summary>
    [JsonPropertyName("serverCertsDir")]  public string? ServerCertsDir { get; set; }

    [JsonPropertyName("logDir")]          public string? LogDir         { get; set; }
    [JsonPropertyName("workDir")]         public string? WorkDir        { get; set; }

    // ── Cluster (D-5e) ───────────────────────────────────────────────────
    // Apply to running master only via cluster.config.set; --master picks
    // them up at startup. Defaults match the dev plan §6.12 / §9 D-6 guidance.

    /// <summary>Cap on concurrent non-terminal cluster jobs. 0 = unlimited.
    /// Submit beyond this returns "queue-full". Defends against a flood of
    /// poster jobs exhausting master disk + merge-buffer RAM.</summary>
    [JsonPropertyName("clusterMaxJobs")]
    public int ClusterMaxJobs { get; set; } = 0;

    /// <summary>How long terminal cluster jobs (ready/failed/cancelled) stay
    /// on disk before <see cref="FracturingFog.Server.Cluster.JobStore.EvictExpired"/>
    /// removes them. 0 = never evict. Default 60 minutes per the dev plan.</summary>
    [JsonPropertyName("clusterArtifactRetentionMinutes")]
    public int ClusterArtifactRetentionMinutes { get; set; } = 60;

    /// <summary>Default per-tile pixel side used by <c>TilePlanner.PlanImage</c>
    /// when the client supplies no <c>tilePixelsHint</c> and the registry has
    /// no learned EMA / worker hints. 0 = use <c>TilePlanner.DefaultTilePixels</c>
    /// (512). Larger = fewer tiles + less merge overhead; smaller = better
    /// straggler tolerance.</summary>
    [JsonPropertyName("clusterTileTargetPixels")]
    public int ClusterTileTargetPixels { get; set; } = 0;

    /// <summary>How the server resolves the watermark on a render job.
    /// "Default" preserves today's behaviour (region/theme + auto contrast).
    /// "Custom" uses a server-side saved watermark named by
    /// <see cref="ServerCustomWatermarkName"/>. "Client" honours the client's
    /// per-request override when <c>RenderRequestDto.UseClientWatermark</c>
    /// is set and the payload passes <c>WatermarkPayloadValidator</c>; falls
    /// back to Default when missing.</summary>
    [JsonPropertyName("watermarkMode")]
    public ServerWatermarkMode WatermarkMode { get; set; } = ServerWatermarkMode.Default;

    /// <summary>Name of the server-side <c>UserWatermarkStore</c> entry used
    /// when <see cref="WatermarkMode"/> is Custom. Ignored otherwise.</summary>
    [JsonPropertyName("serverCustomWatermarkName")]
    public string? ServerCustomWatermarkName { get; set; }

    public static string DefaultConfigPath() => Path.Combine(AppDataDir(), "server-config.json");
    public static string DefaultCertDir()    => Path.Combine(AppDataDir(), "server-certs");
    public static string DefaultLogDir()     => Path.Combine(AppDataDir(), "server-logs");
    public static string DefaultWorkDir()    => Path.Combine(AppDataDir(), "server-work");

    /// <summary>Resolved certs directory honouring the optional override.</summary>
    public string EffectiveCertsDir()
        => string.IsNullOrWhiteSpace(ServerCertsDir) ? DefaultCertDir() : ServerCertsDir!;

    public static string AppDataDir() => AppDataPaths.Root;

    public static ServerConfig LoadOrDefault(string? path = null)
    {
        path ??= DefaultConfigPath();
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var opts = new JsonSerializerOptions
                {
                    Converters = { new JsonStringEnumConverter() },
                };
                return JsonSerializer.Deserialize<ServerConfig>(json, opts) ?? new ServerConfig();
            }
        }
        catch { }
        return new ServerConfig();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        });
        AtomicFile.WriteAllText(path, json);
    }

}

public enum ServerWatermarkMode
{
    Default,
    Custom,
    Client,
}
