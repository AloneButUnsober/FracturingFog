// Models/UserColorThemeLibrary.cs
//
// Singleton library of user-defined colour themes persisted to JSON in
// %APPDATA%\FracturingFog\colorthemes.json.
//
// Mirrors the design of FractalRegionLibrary:
//   • Singleton, lazy-initialised on first access.
//   • System.Text.Json with indented output for human-editable files.
//   • Failures during load/save are non-fatal — user simply loses their custom
//     themes rather than crashing the app.
//
// Workflow:
//   1.  Call Load() once at startup (via ColorPalette.LoadUserThemes()).
//   2.  Add/Remove user themes; each operation auto-saves.
//   3.  After mutations, ColorPalette.RebuildUserPalettes() must be called
//       so the new themes appear in the UI combo box.  Add()/Remove() do this
//       automatically; if you mutate the Themes list directly, call it yourself.
//
// Export workflow:
//   var data = DataDrivenColorThemes.Export(someExistingTheme);
//   UserColorThemeLibrary.Instance.Add(data);   // auto-saves
//
// Import workflow:
//   File.WriteAllText(...colorthemes.json, hand-edited JSON);
//   ColorPalette.LoadUserThemes();              // re-reads file

using FracturingFog.Interefaces;
using FracturingFog.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FracturingFog.Models
{
    /// <summary>
    /// Singleton library of user-defined <see cref="ColorThemeData"/> entries.
    /// </summary>
    public sealed class UserColorThemeLibrary
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        private static UserColorThemeLibrary? _instance;

        public static UserColorThemeLibrary Instance
            => _instance ??= new UserColorThemeLibrary();

        private UserColorThemeLibrary() { }

        // ── Storage paths ─────────────────────────────────────────────────────

        private static string SettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FracturingFog");

        private static string ThemesFile =>
            Path.Combine(SettingsDir, "colorthemes.json");

        private static string ThemesFileHash => FormHelpers.GetFileHash(ThemesFile);

        // ── New Pallets file paths ─────────────────────────────────────────────────────
        // Source ships read-only inside the install dir. Use AppContext.BaseDirectory
        // so the path is stable regardless of the process's working directory
        // (Start Menu shortcuts set cwd = INSTALLFOLDER, but launches from a
        // command prompt or other location would otherwise misresolve).
        private static string ColorThemesDir => Path.Combine(AppContext.BaseDirectory, "Resources", "ColorThemes");

        private static string NewPalletsFile => Path.Combine(ColorThemesDir, "colorthemes.json");

        private static string NewPalletsFileHash => FormHelpers.GetFileHash(NewPalletsFile);

        // Per-user marker that records the hash of the source palette file we
        // already merged. Lives in AppData (writable); avoids re-merging every
        // launch and means we never have to mutate the install dir.
        private static string MergedSourceHashMarker =>
            Path.Combine(SettingsDir, "colorthemes.source.hash");

        private static void CheckForNewColorPallets()
        {
            if (string.IsNullOrEmpty(ThemesFile) ||
                string.IsNullOrEmpty(NewPalletsFile) ||
                !File.Exists(ThemesFile) ||
                !File.Exists(NewPalletsFile)) return;

            string sourceHash = NewPalletsFileHash;
            if (string.IsNullOrEmpty(sourceHash)) return;

            // Skip if we already merged this exact source file.
            if (File.Exists(MergedSourceHashMarker))
            {
                try
                {
                    if (string.Equals(File.ReadAllText(MergedSourceHashMarker).Trim(),
                                      sourceHash, StringComparison.OrdinalIgnoreCase))
                        return;
                }
                catch { /* fall through and re-merge */ }
            }

            JsonNode? themesJN;
            JsonNode? newthemesJN;
            try
            {
                themesJN = JsonNode.Parse(File.ReadAllText(ThemesFile));
                newthemesJN = JsonNode.Parse(File.ReadAllText(NewPalletsFile));
            }
            catch
            {
                return;
            }
            if (themesJN is not JsonArray themesJA || newthemesJN is not JsonArray newJA) return;

            for (int i = 0; i < newJA.Count; i++)
            {
                if (themesJA.Contains(newJA[i])) continue;
                themesJA.Add(newJA[i]?.DeepClone());
            }

            // Backup the user's existing AppData themes file (writable location).
            try
            {
                string backup = $"{ThemesFile}.{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(ThemesFile, backup, overwrite: false);
            }
            catch { /* non-fatal */ }

            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(ThemesFile, themesJA.ToString());
                File.WriteAllText(MergedSourceHashMarker, sourceHash);
            }
            catch
            {
                // Non-fatal — user keeps their existing themes; we'll retry next launch.
            }
        }

        // ── In-memory contents ────────────────────────────────────────────────

        /// <summary>
        /// Mutable list of user-defined themes.  Don't add/remove directly
        /// unless you also call <see cref="Save"/> and
        /// <see cref="ColorPalette.RebuildUserPalettes"/>.
        /// </summary>
        public List<ColorThemeData> Themes { get; } = new();

        // ── JSON options ──────────────────────────────────────────────────────

        private static JsonSerializerOptions BuildJsonOptions()
        {
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            opts.Converters.Add(new JsonStringEnumConverter());
            return opts;
        }

        // ── Persistence ───────────────────────────────────────────────────────

        /// <summary>
        /// Loads user themes from disk.  Safe to call if the file is missing
        /// or corrupt — the in-memory list is just left empty.
        /// </summary>
        public void Load()
        {
            try
            {
                Themes.Clear();
                if (!File.Exists(ThemesFile)) return;

                string json = File.ReadAllText(ThemesFile);
                var loaded = JsonSerializer.Deserialize<List<ColorThemeData>>(json, BuildJsonOptions());
                if (loaded == null) return;

                foreach (var t in loaded)
                    if (t != null) Themes.Add(t);
            }
            catch
            {
                Themes.Clear();
            }
        }

        /// <summary>
        /// Persists the current <see cref="Themes"/> list to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(Themes, BuildJsonOptions());
                File.WriteAllText(ThemesFile, json);
            }
            catch
            {
                // Non-fatal — user loses any unsaved custom themes.
            }
        }

        // ── Mutators ──────────────────────────────────────────────────────────

        /// <summary>
        /// Adds a new user theme and persists.  Returns false if a theme with
        /// the same <see cref="ColorThemeData.Name"/> already exists.
        /// </summary>
        public bool Add(ColorThemeData? data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.Name)) return false;

            foreach (var t in Themes)
                if (t.Name.Equals(data.Name, StringComparison.OrdinalIgnoreCase))
                    return false;

            Themes.Add(data);
            Save();
            ColorPalette.RebuildUserPalettes();
            return true;
        }

        /// <summary>
        /// Removes a user theme by name and persists.  Returns false if no
        /// theme with that name exists.
        /// </summary>
        public bool Remove(string name)
        {
            for (int i = 0; i < Themes.Count; i++)
            {
                if (Themes[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Themes.RemoveAt(i);
                    Save();
                    ColorPalette.RebuildUserPalettes();
                    return true;
                }
            }
            return false;
        }

        public void UpdateCheck()
        {
            CheckForNewColorPallets();
        }

        /// <summary>
        /// Exports a built-in or user theme to a JSON string.  Returns null
        /// for themes that have no exposed parameter surface (algorithmic
        /// themes such as HSV, Bernstein, Painted).
        /// </summary>
        public static string? ExportToJson(IColorMap map)
        {
            var data = DataDrivenColorThemes.Export(map);
            if (data == null) return null;
            return JsonSerializer.Serialize(data, BuildJsonOptions());
        }

        /// <summary>
        /// Convenience: export an existing theme by display name and copy it
        /// into the user library under a new name.  Returns false if the
        /// source theme isn't exportable or if the new name collides.
        /// </summary>
        public bool ExportAndAdd(string sourceThemeName, string newName)
        {
            var source = ColorPalette.GetPaletteByName(sourceThemeName);
            var data = DataDrivenColorThemes.Export(source);
            if (data == null) return false;
            data.Name = newName;
            data.Category = "User";
            return Add(data);
        }
    }
}