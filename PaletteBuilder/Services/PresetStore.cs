// Services/PresetStore.cs
//
// File-backed persistence for ExtractionPreset and RecentFiles.
//
// Layout:
//   %APPDATA%\PaletteBuilder\
//     recent.json
//     presets\
//       <PresetName>.palettebuilder.json
//
// Filenames are sanitised — slashes and reserved chars become underscores —
// so the preset Name field can contain anything the user types. ListPresets
// returns presets sorted by filename (stable, predictable order).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PaletteBuilder.Models;

namespace PaletteBuilder.Services;

public sealed class PresetStore
{
    public const string PresetExtension = ".palettebuilder.json";

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string RootDirectory { get; }
    public string PresetsDirectory { get; }
    public string RecentFilesPath { get; }

    public PresetStore() : this(DefaultRoot()) { }

    public PresetStore(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        PresetsDirectory = Path.Combine(rootDirectory, "presets");
        RecentFilesPath = Path.Combine(rootDirectory, "recent.json");
        Directory.CreateDirectory(PresetsDirectory);
    }

    private static string DefaultRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PaletteBuilder");

    // ── Presets ────────────────────────────────────────────────────────

    public IReadOnlyList<string> ListPresetNames()
    {
        if (!Directory.Exists(PresetsDirectory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(PresetsDirectory, "*" + PresetExtension)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!.Substring(0, n.Length - PresetExtension.Length))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ExtractionPreset? LoadPreset(string name)
    {
        var path = PathForPreset(name);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ExtractionPreset>(stream, s_jsonOpts);
        }
        catch (Exception) { return null; }
    }

    public void SavePreset(ExtractionPreset preset)
    {
        if (preset == null) throw new ArgumentNullException(nameof(preset));
        if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = "Untitled";
        var path = PathForPreset(preset.Name);
        Directory.CreateDirectory(PresetsDirectory);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, preset, s_jsonOpts);
    }

    public bool DeletePreset(string name)
    {
        var path = PathForPreset(name);
        if (!File.Exists(path)) return false;
        try { File.Delete(path); return true; }
        catch { return false; }
    }

    private string PathForPreset(string name)
        => Path.Combine(PresetsDirectory, Sanitise(name) + PresetExtension);

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    // ── Recent files ───────────────────────────────────────────────────

    public RecentFiles LoadRecent()
    {
        if (!File.Exists(RecentFilesPath)) return new RecentFiles();
        try
        {
            using var stream = File.OpenRead(RecentFilesPath);
            return JsonSerializer.Deserialize<RecentFiles>(stream, s_jsonOpts) ?? new RecentFiles();
        }
        catch { return new RecentFiles(); }
    }

    public void SaveRecent(RecentFiles recent)
    {
        if (recent == null) throw new ArgumentNullException(nameof(recent));
        Directory.CreateDirectory(RootDirectory);
        using var stream = File.Create(RecentFilesPath);
        JsonSerializer.Serialize(stream, recent, s_jsonOpts);
    }

    /// <summary>
    /// Push <paramref name="path"/> to the front of the MRU list, dedup case-
    /// insensitively, cap to <see cref="RecentFiles.MaxItems"/>, persist.
    /// </summary>
    public RecentFiles PushRecent(string path)
    {
        var recent = LoadRecent();
        recent.Paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        recent.Paths.Insert(0, path);
        if (recent.Paths.Count > RecentFiles.MaxItems)
            recent.Paths.RemoveRange(RecentFiles.MaxItems, recent.Paths.Count - RecentFiles.MaxItems);
        SaveRecent(recent);
        return recent;
    }
}
