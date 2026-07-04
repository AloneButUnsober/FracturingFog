using System;
using System.IO;
using System.Text.Json;
using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    /// <summary>Persists <see cref="SlideshowSettings"/> to <see cref="AppDataPaths.Root"/>\slideshow-settings.json.</summary>
    public static class SlideshowSettingsStore
    {
        private static string SettingsDir => AppDataPaths.Root;

        private static string SettingsFile => Path.Combine(SettingsDir, "slideshow-settings.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static SlideshowSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return new SlideshowSettings();
                var json = File.ReadAllText(SettingsFile);
                var s = JsonSerializer.Deserialize<SlideshowSettings>(json, JsonOpts);
                return s ?? new SlideshowSettings();
            }
            catch
            {
                return new SlideshowSettings();
            }
        }

        public static void Save(SlideshowSettings settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(settings, JsonOpts);
                AtomicFile.WriteAllText(SettingsFile, json);
            }
            catch { }
        }
    }
}
