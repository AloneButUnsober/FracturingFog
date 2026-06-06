// Abstractions/Models/Watermark/UserWatermarkStore.cs
//
// Singleton persistence for user-defined watermarks. Mirrors UserEquationStore
// and UserBulbStore exactly: lazy instance, indented JSON, non-fatal failure
// handling, %APPDATA%\FracturingFog\userwatermarks.json.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FracturingFog.Models
{
    public sealed class UserWatermarkStore
    {
        private static UserWatermarkStore? _instance;
        public static UserWatermarkStore Instance => _instance ??= new UserWatermarkStore();

        private UserWatermarkStore() { }

        public List<WatermarkDef> Watermarks { get; } = new();

        private static string SettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FracturingFog");

        private static string WatermarksFile =>
            Path.Combine(SettingsDir, "userwatermarks.json");

        private static JsonSerializerOptions BuildJsonOptions() => new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public void Load()
        {
            try
            {
                Watermarks.Clear();
                if (!File.Exists(WatermarksFile)) return;

                string json = File.ReadAllText(WatermarksFile);
                var loaded = JsonSerializer.Deserialize<List<WatermarkDef>>(json, BuildJsonOptions());
                if (loaded == null) return;

                foreach (var w in loaded)
                    if (w != null && !string.IsNullOrWhiteSpace(w.Name)) Watermarks.Add(w);
            }
            catch
            {
                Watermarks.Clear();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(Watermarks, BuildJsonOptions());
                File.WriteAllText(WatermarksFile, json);
            }
            catch
            {
                // Non-fatal.
            }
        }

        /// <summary>Insert-or-replace by Name (case-insensitive). Persists on
        /// success and returns the stored entry. Returns null when name is blank.</summary>
        public WatermarkDef? SaveWatermark(WatermarkDef def)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.Name)) return null;

            for (int i = 0; i < Watermarks.Count; i++)
            {
                if (Watermarks[i].Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Watermarks[i] = def;
                    Save();
                    return def;
                }
            }

            Watermarks.Add(def);
            Save();
            return def;
        }

        public bool Remove(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            for (int i = 0; i < Watermarks.Count; i++)
            {
                if (Watermarks[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Watermarks.RemoveAt(i);
                    Save();
                    return true;
                }
            }
            return false;
        }

        public WatermarkDef? GetByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var w in Watermarks)
                if (w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return w;
            return null;
        }

        public IEnumerable<string> EnumerateNames()
        {
            foreach (var w in Watermarks) yield return w.Name;
        }

        public bool Exists(string name) => GetByName(name) != null;

        /// <summary>Helper for client/server: serialize a single watermark to
        /// the same JSON shape used in the file store. Bounded — callers can
        /// trust the result to be under a few KB unless someone supplies a
        /// pathological Text value.</summary>
        public static string SerializeOne(WatermarkDef def)
            => JsonSerializer.Serialize(def, BuildJsonOptions());

        /// <summary>Helper: parse a single watermark JSON blob. Throws on
        /// malformed input — callers (e.g. the server's payload validator)
        /// catch and convert into a protocol error.</summary>
        public static WatermarkDef? DeserializeOne(string json)
            => string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<WatermarkDef>(json, BuildJsonOptions());
    }
}
