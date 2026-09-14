// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/UiScaleStore.cs
//
// Persists the chosen global UI scale (#809 / S1 #810) to
// AppDataPaths.Root\ui-scale.json. Mirrors AnimationSettingsStore /
// SlideshowSettingsStore — best-effort, swallows I/O failures, falls back to
// the neutral default. A one-field DTO (rather than a bare double) so future
// UI-preference fields can slot into the same file without a schema break.

using System.IO;
using System.Text.Json;

using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    /// <summary>Serializable UI-preference payload persisted by
    /// <see cref="UiScaleStore"/>. One field today; a container so more shell
    /// preferences can join later.</summary>
    public sealed class UiScalePrefs
    {
        /// <summary>The persisted global UI scale. <c>0</c> means "unset" — an
        /// older or missing file — which <see cref="UiScaleLadder.Snap"/>
        /// resolves to 100%.</summary>
        public double Scale { get; set; }
    }

    /// <summary>Loads/saves <see cref="UiScalePrefs"/> to
    /// <see cref="AppDataPaths.Root"/>\ui-scale.json. Best-effort; never throws.</summary>
    public static class UiScaleStore
    {
        private static string SettingsDir => AppDataPaths.Root;

        private static string SettingsFile => Path.Combine(SettingsDir, "ui-scale.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>Reads the persisted scale, snapped to a valid ladder step.
        /// Returns <see cref="UiScaleLadder.Default"/> when the file is absent,
        /// unreadable, or holds an out-of-range/unset value.</summary>
        public static double Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return UiScaleLadder.Default;
                var json = File.ReadAllText(SettingsFile);
                var prefs = JsonSerializer.Deserialize<UiScalePrefs>(json, JsonOpts);
                return UiScaleLadder.Snap(prefs?.Scale ?? 0);
            }
            catch
            {
                return UiScaleLadder.Default;
            }
        }

        /// <summary>Persists <paramref name="scale"/> (snapped to a ladder step).
        /// Silently no-ops on I/O failure.</summary>
        public static void Save(double scale)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var prefs = new UiScalePrefs { Scale = UiScaleLadder.Snap(scale) };
                var json = JsonSerializer.Serialize(prefs, JsonOpts);
                AtomicFile.WriteAllText(SettingsFile, json);
            }
            catch { }
        }
    }
}
