// Client/RenderOptionsStore.cs
// Named render presets — same field shape as RenderRequestDto so the FFClient
// dialog and the --batch --remote path round-trip them losslessly. Plaintext
// JSON: nothing here is secret.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using FracturingFog.Server.Protocol;

namespace FracturingFog.Client;

public sealed class RenderOptionPreset
{
    public string Name { get; set; } = "";
    public RenderRequestDto Request { get; set; } = new();
    public string? SuggestedOutputPath { get; set; }
}

public sealed class RenderOptionsStore
{
    public List<RenderOptionPreset> Presets { get; set; } = new();

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FracturingFog", "client-render-presets.json");

    public static RenderOptionsStore LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath();
        if (!File.Exists(path)) return new RenderOptionsStore();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RenderOptionsStore>(json, JsonOpts) ?? new RenderOptionsStore();
        }
        catch { return new RenderOptionsStore(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Atomic write — same reasoning as ClientConnectionStore.Save.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    public RenderOptionPreset? FindByName(string name) =>
        Presets.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
