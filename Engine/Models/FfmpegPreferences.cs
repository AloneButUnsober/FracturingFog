// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/FfmpegPreferences.cs
//
// User election for the FFmpeg first-run modal (auto-download / manual /
// skip) plus the last-known installed version string. Persisted JSON at
// %APPDATA%\FracturingFog\ffmpeg-prefs.json (Roaming) so the user's
// election follows them across machines like the rest of FracturingFog's
// user prefs. The ffmpeg binary itself stays machine-local elsewhere; only
// the election + version metadata roams. A one-shot migration moves an
// existing prefs file from the legacy %LOCALAPPDATA% path on first load.
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
using FracturingFog.Abstractions;

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

        /// <summary>#951 — whether ffmpeg was present when the user picked
        /// "Continue Without Video Save". True = a deliberate opt-out with ffmpeg
        /// installed (respected until they re-enable). False = skipped because it
        /// was missing. Null = legacy prefs written before this was tracked. Only
        /// meaningful while <see cref="Election"/> is Skip.</summary>
        public bool? SkippedWithFfmpegPresent { get; set; }

        private static string SettingsDir => AppDataPaths.Root;

        private static string PrefsFile =>
            Path.Combine(SettingsDir, "ffmpeg-prefs.json");

        private static string LegacySettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FracturingFog");

        private static string LegacyPrefsFile =>
            Path.Combine(LegacySettingsDir, "ffmpeg-prefs.json");

        private static JsonSerializerOptions BuildJsonOptions() => new()
        {
            WriteIndented = true,
        };

        private static void MigrateLegacyPrefsIfNeeded()
        {
            try
            {
                if (File.Exists(PrefsFile)) return;
                if (!File.Exists(LegacyPrefsFile)) return;
                Directory.CreateDirectory(SettingsDir);
                File.Move(LegacyPrefsFile, PrefsFile);
            }
            catch { /* migration is best-effort; fall through to defaults */ }
        }

        private static FfmpegPreferences LoadOrDefault()
        {
            MigrateLegacyPrefsIfNeeded();
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
                AtomicFile.WriteAllText(PrefsFile, json);
            }
            catch { /* non-fatal */ }
        }

        /// <summary>True when the user has explicitly opted out of video
        /// encoding. Callers treat ffmpeg as unavailable in that case even if
        /// the binary is present, so the UI stays consistent with their
        /// election until they reverse it from the FloatingMenu dialog.</summary>
        public bool IsVideoDisabledByUser() => Election == FfmpegUserElection.Skip;

        /// <summary>#951 — record a "Continue Without Video Save" pick, noting
        /// whether ffmpeg was installed at the time.</summary>
        public void ChooseSkip(bool ffmpegPresent)
        {
            Election = FfmpegUserElection.Skip;
            SkippedWithFfmpegPresent = ffmpegPresent;
        }

        /// <summary>#951 — undo a Skip election (the Setup dialog's "Re-enable
        /// video" button). Returns true when the election changed.</summary>
        public bool ReEnableVideo()
        {
            if (Election != FfmpegUserElection.Skip) return false;
            Election = FfmpegUserElection.Manual;
            SkippedWithFfmpegPresent = null;
            return true;
        }

        /// <summary>#951 — a Skip chosen because ffmpeg was missing (or of
        /// unknown provenance, legacy prefs) is stale once ffmpeg is installed:
        /// clear it so "installed" means "enabled" and the Setup dialog and the
        /// video features can't disagree. A Skip picked WITH ffmpeg present is a
        /// deliberate opt-out and is kept. Returns true when the election changed
        /// (caller saves).</summary>
        public bool ReconcileStaleSkip(bool ffmpegPresent)
        {
            if (Election != FfmpegUserElection.Skip || !ffmpegPresent) return false;
            if (SkippedWithFfmpegPresent == true) return false;
            return ReEnableVideo();
        }

        /// <summary>True when the startup modal should be suppressed even if
        /// ffmpeg.exe is missing. Manual + Skip both opt out; only None and
        /// AutoDownload re-prompt (the latter so a deleted binary triggers a
        /// re-install offer instead of silently breaking video).</summary>
        public bool SuppressStartupPrompt() =>
            Election == FfmpegUserElection.Manual ||
            Election == FfmpegUserElection.Skip;
    }
}
