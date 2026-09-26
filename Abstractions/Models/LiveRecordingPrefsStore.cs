// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/LiveRecordingPrefsStore.cs
//
// Quick Record preferences (#946): capture rate, default export choices and
// the "ask on stop" switch for instant recording (#944/#945). Persisted to
// AppDataPaths.Root\live-recording.json. Lives in Abstractions so both the
// Control Center (UI.Avalonia, no Engine reference) and the host's export
// flow read the same object. Mirrors UiScaleStore — best-effort, never throws.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using FracturingFog.Abstractions;
using FracturingFog.Render;

namespace FracturingFog.Models
{
    /// <summary>Persisted Quick Record settings.</summary>
    public sealed class LiveRecordingPrefs
    {
        /// <summary>Rate the render window is sampled at while recording (1..60).</summary>
        public int CaptureFps { get; set; } = 30;

        /// <summary>Default / last-used export choices.</summary>
        public LiveRecordingFormat Format { get; set; } = LiveRecordingFormat.Mp4H264;
        public LiveRecordingQuality Quality { get; set; } = LiveRecordingQuality.High;
        public int OutputFps { get; set; } = 30;
        public int ScalePercent { get; set; } = 100;

        /// <summary>True → show the Save Recording prompt on stop (choices are
        /// remembered). False → save straight to <see cref="OutputFolder"/> with
        /// the defaults above, no prompts.</summary>
        public bool AskOnStop { get; set; } = true;

        /// <summary>Destination for no-prompt saves. Empty → the user's Videos
        /// folder (see <see cref="ResolveOutputFolder"/>).</summary>
        public string OutputFolder { get; set; } = "";

        public LiveRecordingExportOptions ToExportOptions() => new()
        {
            Format = Format,
            Quality = Quality,
            Fps = OutputFps,
            ScalePercent = ScalePercent,
        };

        public void SetExportOptions(LiveRecordingExportOptions o)
        {
            Format = o.Format;
            Quality = o.Quality;
            OutputFps = o.Fps;
            ScalePercent = o.ScalePercent;
        }

        /// <summary><see cref="OutputFolder"/>, or the user's Videos folder
        /// (falling back to Documents / temp when that is unavailable).</summary>
        public string ResolveOutputFolder()
        {
            if (!string.IsNullOrWhiteSpace(OutputFolder)) return OutputFolder;
            foreach (var f in new[] { Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyDocuments })
            {
                string p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p)) return Path.Combine(p, "FracturingFog");
            }
            return Path.Combine(Path.GetTempPath(), "FracturingFog");
        }

        /// <summary>Clamp every field into its valid range (hand-edited or
        /// older files).</summary>
        public LiveRecordingPrefs Normalized()
        {
            CaptureFps = Math.Clamp(CaptureFps, 1, 60);
            OutputFps = Math.Clamp(OutputFps, 1, 120);
            ScalePercent = Math.Clamp(ScalePercent, 10, 100);
            if (!Enum.IsDefined(Format)) Format = LiveRecordingFormat.Mp4H264;
            if (!Enum.IsDefined(Quality)) Quality = LiveRecordingQuality.High;
            OutputFolder ??= "";
            return this;
        }
    }

    /// <summary>Loads/saves <see cref="LiveRecordingPrefs"/> to
    /// <see cref="AppDataPaths.Root"/>\live-recording.json. Best-effort.</summary>
    public static class LiveRecordingPrefsStore
    {
        private static LiveRecordingPrefs? _current;

        private static string SettingsFile => Path.Combine(AppDataPaths.Root, "live-recording.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>Process-wide instance, loaded on first use. Mutate then
        /// call <see cref="Save()"/>.</summary>
        public static LiveRecordingPrefs Current => _current ??= Load();

        public static LiveRecordingPrefs Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var p = JsonSerializer.Deserialize<LiveRecordingPrefs>(File.ReadAllText(SettingsFile), JsonOpts);
                    if (p != null) return p.Normalized();
                }
            }
            catch { }
            return new LiveRecordingPrefs();
        }

        /// <summary>Raised after <see cref="Save()"/> (on the saving thread) so
        /// open editors (Control Center Quick Record) can refresh.</summary>
        public static event Action? Changed;

        /// <summary>Persist <see cref="Current"/>. Silently no-ops on I/O failure.</summary>
        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppDataPaths.Root);
                AtomicFile.WriteAllText(SettingsFile, JsonSerializer.Serialize(Current.Normalized(), JsonOpts));
            }
            catch { }
            Changed?.Invoke();
        }

        /// <summary>Test seam: drop the cached instance so the next
        /// <see cref="Current"/> re-reads the (redirected) file.</summary>
        public static void ResetForTests() => _current = null;
    }
}
