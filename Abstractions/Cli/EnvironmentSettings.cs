// #1669 (epic #1667, S2) — saved FF_* switch values, applied at startup.
//
// The Control Center Environment panel saves the values the user picked to
// environment.json under AppDataPaths. Program.Main applies them for the interactive
// shell only (never for --batch / --server / probes: a batch run must be reproducible
// from its command line, #1672), before any FF type reads its switch. A variable that
// is already set in the process environment (the shell, a shortcut) keeps priority.
// The user / system environment (registry) is never written.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FracturingFog.Abstractions;

namespace FracturingFog.Cli
{
    /// <summary>One saved switch value.</summary>
    public sealed record EnvSetting(string Name, string Value);

    /// <summary>Where a switch's value in this process came from.</summary>
    public enum EnvVarSource
    {
        /// <summary>Unset: the switch's built-in default.</summary>
        Default,
        /// <summary>Set before FF started (shell, shortcut, parent process).</summary>
        Shell,
        /// <summary>Applied from the saved settings at startup.</summary>
        Saved,
    }

    /// <summary>A switch's state in this process, captured at startup.</summary>
    public readonly record struct EnvVarState(string? Value, EnvVarSource Source);

    public static class EnvironmentSettings
    {
        public const string FileName = "environment.json";

        private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

        public static string DefaultPath => AppDataPaths.Combine(FileName);

        /// <summary>Loads the saved values: unreadable entries, unknown names and empty
        /// values are dropped (#964); a missing or corrupt file reads as none. The last
        /// entry for a name wins.</summary>
        public static List<EnvSetting> Load(string path)
        {
            var list = new TolerantJsonList<EnvSetting>().LoadFile(path, s_json);
            var byName = new Dictionary<string, EnvSetting>(StringComparer.Ordinal);
            foreach (var s in list)
            {
                if (s?.Name == null || string.IsNullOrWhiteSpace(s.Value)) continue;
                if (EnvVarCatalog.Find(s.Name) == null) continue;
                byName[s.Name] = s with { Value = s.Value.Trim() };
            }
            return byName.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>Writes the values (atomically). An empty list removes nothing else:
        /// it saves an empty array.</summary>
        public static void Save(string path, IEnumerable<EnvSetting> settings)
        {
            var list = settings
                .Where(s => EnvVarCatalog.Find(s.Name) != null && !string.IsNullOrWhiteSpace(s.Value))
                .GroupBy(s => s.Name, StringComparer.Ordinal).Select(g => g.Last())
                .OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(list, s_json));
        }

        /// <summary>Applies <paramref name="saved"/> through <paramref name="set"/> where
        /// <paramref name="get"/> finds no value, and returns every catalogued switch's
        /// resulting state. A value already present wins and is reported as
        /// <see cref="EnvVarSource.Shell"/>.</summary>
        public static Dictionary<string, EnvVarState> Apply(
            IReadOnlyCollection<EnvSetting> saved, Func<string, string?> get, Action<string, string> set)
        {
            var states = new Dictionary<string, EnvVarState>(StringComparer.Ordinal);
            foreach (var e in EnvVarCatalog.All)
            {
                string? v = get(e.Name);
                states[e.Name] = string.IsNullOrEmpty(v) ? new(null, EnvVarSource.Default) : new(v, EnvVarSource.Shell);
            }
            foreach (var s in saved)
            {
                if (!states.TryGetValue(s.Name, out var st) || st.Source == EnvVarSource.Shell) continue;
                set(s.Name, s.Value);
                states[s.Name] = new(s.Value, EnvVarSource.Saved);
            }
            return states;
        }

        /// <summary>Every switch's state as captured by <see cref="ApplyAtStartup"/>;
        /// empty until then (the panel then reads the live environment).</summary>
        public static IReadOnlyDictionary<string, EnvVarState> StartupStates { get; private set; }
            = new Dictionary<string, EnvVarState>();

        /// <summary>The saved values <see cref="ApplyAtStartup"/> loaded (applied or not):
        /// the panel compares its pending values with these to say a restart is needed.</summary>
        public static IReadOnlyList<EnvSetting> StartupSaved { get; private set; } = [];

        /// <summary>Whether <see cref="ApplyAtStartup"/> ran in this process.</summary>
        public static bool AppliedAtStartup { get; private set; }

        /// <summary>The interactive shell's startup hook: load the saved values from
        /// <see cref="DefaultPath"/> and apply them to this process. Never throws: a
        /// failure leaves the environment as it was.</summary>
        public static void ApplyAtStartup()
        {
            try
            {
                var saved = Load(DefaultPath);
                StartupSaved = saved;
                StartupStates = Apply(saved, Environment.GetEnvironmentVariable,
                    (n, v) => Environment.SetEnvironmentVariable(n, v));
                AppliedAtStartup = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[env] saved switches not applied: {ex.Message}");
            }
        }

        /// <summary>The state of <paramref name="name"/> in this process: the startup
        /// capture, or the live environment when startup did not run.</summary>
        public static EnvVarState Current(string name)
        {
            if (StartupStates.TryGetValue(name, out var st)) return st;
            string? v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? new(null, EnvVarSource.Default) : new(v, EnvVarSource.Shell);
        }
    }
}
