// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/AudioRuntimePreferences.cs
//
// #271 (parent #58) — user election for the Tier B "OpenAL runtime missing"
// prompt on Linux/macOS, mirroring FfmpegPreferences. When the OpenAL native
// library is absent, live mic/loopback capture is unavailable; the setup dialog
// (Hosting/AudioRuntimeSetupDialog) offers package-manager install instructions
// + a rescan, and records the user's choice here so we do not nag on every
// Audio Settings open. Persisted JSON at %APPDATA%\FracturingFog\
// audio-runtime-prefs.json (roams with the rest of the user prefs).
//
// Election semantics:
//   None   — never asked.
//   Manual — user picked "I'll install it myself". Suppress the prompt; live
//            sources re-enable automatically once a rescan/restart detects the
//            runtime.
//   Skip   — user picked "Continue without live audio". Suppress the prompt;
//            file + synth sources still work.

using System;
using System.IO;
using System.Text.Json;
using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    public enum AudioRuntimeElection
    {
        None = 0,
        Manual = 1,
        Skip = 2,
    }

    public sealed class AudioRuntimePreferences
    {
        private static AudioRuntimePreferences? _instance;
        public static AudioRuntimePreferences Instance => _instance ??= LoadOrDefault();

        public AudioRuntimeElection Election { get; set; } = AudioRuntimeElection.None;

        private static string SettingsDir => AppDataPaths.Root;

        private static string PrefsFile =>
            Path.Combine(SettingsDir, "audio-runtime-prefs.json");

        private static JsonSerializerOptions BuildJsonOptions() => new()
        {
            WriteIndented = true,
        };

        private static AudioRuntimePreferences LoadOrDefault()
        {
            try
            {
                if (File.Exists(PrefsFile))
                {
                    string json = File.ReadAllText(PrefsFile);
                    var loaded = JsonSerializer.Deserialize<AudioRuntimePreferences>(json, BuildJsonOptions());
                    if (loaded != null) return loaded;
                }
            }
            catch { /* corrupt prefs → start fresh */ }
            return new AudioRuntimePreferences();
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

        /// <summary>True when the runtime-missing prompt should be suppressed
        /// (user already elected Manual or Skip). Only <see cref="AudioRuntimeElection.None"/>
        /// still prompts.</summary>
        public bool SuppressPrompt() =>
            Election == AudioRuntimeElection.Manual ||
            Election == AudioRuntimeElection.Skip;
    }
}
