using System;
using System.IO;
using System.Text.Json;
using FracturingFog.Abstractions;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Persists <see cref="AudioSettings"/> to <see cref="AppDataPaths.Root"/>\audio-settings.json.
    /// All I/O wrapped in try/catch — settings are non-critical state.
    /// </summary>
    public static class AudioSettingsStore
    {
        private static string SettingsDir => AppDataPaths.Root;

        private static string SettingsFile => Path.Combine(SettingsDir, "audio-settings.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static AudioSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return new AudioSettings();
                var json = File.ReadAllText(SettingsFile);
                var s = JsonSerializer.Deserialize<AudioSettings>(json, JsonOpts);
                return s ?? new AudioSettings();
            }
            catch
            {
                return new AudioSettings();
            }
        }

        public static void Save(AudioSettings settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(settings, JsonOpts);
                AtomicFile.WriteAllText(SettingsFile, json);
            }
            catch
            {
                // Non-critical: ignore.
            }
        }
    }
}
