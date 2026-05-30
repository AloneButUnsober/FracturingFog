// Server/ServerConfig.cs
// Runtime configuration written to %APPDATA%\FracturingFog\server-config.json
// and edited live by the Avalonia ServerAdmin dialog. Loaded once at
// --server startup; the ServerAdmin "Apply" path rewrites the file and
// triggers a soft-restart on the next idle window.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    [JsonPropertyName("serverCertPath")]  public string? ServerCertPath { get; set; }
    [JsonPropertyName("clientCaCertPath")] public string? ClientCaCertPath { get; set; }

    [JsonPropertyName("logDir")]          public string? LogDir         { get; set; }
    [JsonPropertyName("workDir")]         public string? WorkDir        { get; set; }

    public static string DefaultConfigPath() => Path.Combine(AppDataDir(), "server-config.json");
    public static string DefaultCertDir()    => Path.Combine(AppDataDir(), "server-certs");
    public static string DefaultLogDir()     => Path.Combine(AppDataDir(), "server-logs");
    public static string DefaultWorkDir()    => Path.Combine(AppDataDir(), "server-work");

    public static string AppDataDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FracturingFog");

    public static ServerConfig LoadOrDefault(string? path = null)
    {
        path ??= DefaultConfigPath();
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<ServerConfig>(json) ?? new ServerConfig();
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
        });
        File.WriteAllText(path, json);
    }
}
