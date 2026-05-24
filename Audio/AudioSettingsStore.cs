using System;
using System.IO;
using System.Text.Json;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Persists <see cref="AudioSettings"/> to %APPDATA%\FracturingFog\audio-settings.json.
    /// All I/O wrapped in try/catch — settings are non-critical state.
    /// </summary>
    public static class AudioSettingsStore
    {
        private static string SettingsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FracturingFog");

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
                File.WriteAllText(SettingsFile, json);
            }
            catch
            {
                // Non-critical: ignore.
            }
        }
    }
}
