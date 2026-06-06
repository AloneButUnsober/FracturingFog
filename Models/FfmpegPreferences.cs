// Models/FfmpegPreferences.cs
//
// User election for the FFmpeg first-run modal (auto-download / manual /
// skip) plus the last-known installed version string. Persisted JSON at
// %LOCALAPPDATA%\FracturingFog\ffmpeg-prefs.json. LocalApplicationData
// chosen over ApplicationData so the prefs stay machine-local (BtbN
// binary is x64 Windows; roaming profiles can land on a non-Windows host).
//
// Election semantics:
//   None       — user has never been asked.
//   AutoDownload — user clicked "Download now" at least once. We still
//                  prompt on next missing-ffmpeg startup so a delete
//                  doesn't silently break video without re-confirmation.
//   Manual     — user picked "I'll install it myself". Suppress startup
//                  prompt; FloatingMenu button still opens the dialog.
//   Skip       — user picked "Continue without video save". Suppress
//                  prompt AND gate video UI controls (treat ffmpeg as
//                  unavailable even if the file is present, so the user
//                  isn't surprised by it re-enabling on its own — they
//                  can clear this from the FloatingMenu dialog).

using System;
using System.IO;
using System.Text.Json;

namespace FracturingFog.Models
{
    public enum FfmpegUserElection
    {
        None = 0,
        AutoDownload = 1,
        Manual = 2,
        Skip = 3,
    }

    public sealed class FfmpegPreferences
    {
        private static FfmpegPreferences? _instance;
        public static FfmpegPreferences Instance => _instance ??= LoadOrDefault();

        public FfmpegUserElection Election { get; set; } = FfmpegUserElection.None;

        /// <summary>Version string captured the last time the installer ran
        /// (raw "ffmpeg -version" first line). Used to decide whether a fresh
        /// download is newer than what's on disk.</summary>
        public string? LastInstalledVersion { get; set; }

        /// <summary>UTC time of the last successful install.</summary>
        public DateTime? LastInstalledUtc { get; set; }

        private static string SettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FracturingFog");

        private static string PrefsFile =>
            Path.Combine(SettingsDir, "ffmpeg-prefs.json");

        private static JsonSerializerOptions BuildJsonOptions() => new()
        {
            WriteIndented = true,
        };

        private static FfmpegPreferences LoadOrDefault()
        {
            try
            {
                if (File.Exists(PrefsFile))
                {
                    string json = File.ReadAllText(PrefsFile);
                    var loaded = JsonSerializer.Deserialize<FfmpegPreferences>(json, BuildJsonOptions());
                    if (loaded != null) return loaded;
                }
            }
            catch { /* corrupt prefs → start fresh */ }
            return new FfmpegPreferences();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(this, BuildJsonOptions());
                File.WriteAllText(PrefsFile, json);
            }
            catch { /* non-fatal */ }
        }

        /// <summary>True when the user has explicitly opted out of video
        /// encoding. Callers treat ffmpeg as unavailable in that case even if
        /// the binary is present, so the UI stays consistent with their
        /// election until they reverse it from the FloatingMenu dialog.</summary>
        public bool IsVideoDisabledByUser() => Election == FfmpegUserElection.Skip;

        /// <summary>True when the startup modal should be suppressed even if
        /// ffmpeg.exe is missing. Manual + Skip both opt out; only None and
        /// AutoDownload re-prompt (the latter so a deleted binary triggers a
        /// re-install offer instead of silently breaking video).</summary>
        public bool SuppressStartupPrompt() =>
            Election == FfmpegUserElection.Manual ||
            Election == FfmpegUserElection.Skip;
    }
}
