// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/GridLabelPrefsStore.cs
//
// #1559 — persists the grid-overlay label preference (GridLabelSettings) to
// AppDataPaths.Root\grid-labels.json. Mirrors UiScaleStore: best-effort,
// swallows I/O failures, falls back to the default (Auto). The overlay is a
// live-view display aid, so this is a user preference, not a render parameter.

using System;
using System.IO;
using System.Text.Json;

using FracturingFog.Abstractions;
using FracturingFog.ViewState;

namespace FracturingFog.Models
{
    /// <summary>Serializable payload persisted by <see cref="GridLabelPrefsStore"/>.</summary>
    public sealed class GridLabelPrefs
    {
        public string Mode { get; set; } = nameof(GridLabelMode.Auto);
        public int Digits { get; set; } = GridLabelSettings.Default.Digits;
    }

    /// <summary>Loads/saves <see cref="GridLabelSettings"/>. Best-effort; never throws.</summary>
    public static class GridLabelPrefsStore
    {
        private static string SettingsFile => Path.Combine(AppDataPaths.Root, "grid-labels.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static GridLabelSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return GridLabelSettings.Default;
                var prefs = JsonSerializer.Deserialize<GridLabelPrefs>(File.ReadAllText(SettingsFile), JsonOpts);
                if (prefs == null || !Enum.TryParse(prefs.Mode, ignoreCase: true, out GridLabelMode mode)
                    || !Enum.IsDefined(mode))
                    return GridLabelSettings.Default;
                return new GridLabelSettings(mode,
                    Math.Clamp(prefs.Digits, GridLabelSettings.MinDigits, GridLabelSettings.MaxDigits));
            }
            catch
            {
                return GridLabelSettings.Default;
            }
        }

        public static void Save(GridLabelSettings settings)
        {
            try
            {
                Directory.CreateDirectory(AppDataPaths.Root);
                var prefs = new GridLabelPrefs { Mode = settings.Mode.ToString(), Digits = settings.ClampedDigits };
                AtomicFile.WriteAllText(SettingsFile, JsonSerializer.Serialize(prefs, JsonOpts));
            }
            catch { }
        }
    }
}
