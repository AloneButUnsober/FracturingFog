using System.IO;
using System.Text.Json;
using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    /// <summary>Persists <see cref="AnimationSettings"/> to
    /// <see cref="AppDataPaths.Root"/>\animation-settings.json. Mirrors
    /// <see cref="SlideshowSettingsStore"/> — best-effort, swallows I/O
    /// failures and falls back to defaults.</summary>
    public static class AnimationSettingsStore
    {
        private static string SettingsDir => AppDataPaths.Root;

        private static string SettingsFile => Path.Combine(SettingsDir, "animation-settings.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static AnimationSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return new AnimationSettings();
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AnimationSettings>(json, JsonOpts)
                    ?? new AnimationSettings();
            }
            catch
            {
                return new AnimationSettings();
            }
        }

        public static void Save(AnimationSettings settings)
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
