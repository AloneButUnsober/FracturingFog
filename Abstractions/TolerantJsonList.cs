// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/TolerantJsonList.cs
//
// #966 (follow-up to #964) — per-entry JSON list persistence for user stores.
// Every store used to load its file with ONE JsonSerializer.Deserialize<List<T>>
// call and clear the list on any exception, so a single entry this build could not
// read (an enum value written by a newer / other-branch build, a hand-edit) hid ALL
// the user's entries — and the next Save() wrote the empty or seeded list back,
// deleting them from disk. #964 hit exactly that in animations.json.
//
// This helper reads an array element by element. An element that fails to
// deserialize is skipped for use but kept verbatim, and written back on save, so
// data the running build cannot understand round-trips untouched. A file that is
// not a JSON array at all is snapshotted (UserDataBackup) before anything can
// overwrite it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FracturingFog.Abstractions
{
    /// <summary>Element-by-element JSON array load/save that preserves entries the
    /// running build cannot read (#964/#966). One instance per store: it remembers the
    /// unreadable elements from the last read and re-emits them on the next write.</summary>
    public sealed class TolerantJsonList<T> where T : class
    {
        private readonly List<JsonElement> _unreadable = new();

        /// <summary>Entries in the last read that this build could not deserialize.
        /// They are hidden from the store but written back verbatim on save.</summary>
        public int UnreadableCount => _unreadable.Count;

        /// <summary>Forget any preserved entries (e.g. the store was reset on purpose).</summary>
        public void Clear() => _unreadable.Clear();

        // ── read ─────────────────────────────────────────────────────────────

        /// <summary>Load <paramref name="path"/> as a JSON array of <typeparamref name="T"/>.
        /// Missing or blank file → empty. A file that is not a JSON array is backed up
        /// (<see cref="UserDataBackup"/>, reason "unreadable") and yields empty, so the
        /// caller's next save cannot destroy the only copy. Never throws.</summary>
        public List<T> LoadFile(string path, JsonSerializerOptions? options)
            => LoadFile(path, e => e.Deserialize<T>(options));

        /// <summary><see cref="LoadFile(string, JsonSerializerOptions?)"/> with a custom
        /// per-element reader (e.g. a DTO projection). A reader that throws or returns null
        /// marks the element unreadable.</summary>
        public List<T> LoadFile(string path, Func<JsonElement, T?> read)
        {
            _unreadable.Clear();
            string json;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new List<T>();
                json = File.ReadAllText(path);
            }
            catch
            {
                return new List<T>();
            }
            if (string.IsNullOrWhiteSpace(json)) return new List<T>();

            try
            {
                using var doc = JsonDocument.Parse(json);
                return ReadArray(doc.RootElement, read);
            }
            catch
            {
                _unreadable.Clear();
                UserDataBackup.SnapshotBeforeMigration(path, "unreadable");
                return new List<T>();
            }
        }

        /// <summary>Read <paramref name="array"/> element by element with
        /// <paramref name="options"/>. Throws <see cref="JsonException"/> when it is not an
        /// array (the caller decides how to treat a wholly unreadable file).</summary>
        public List<T> ReadArray(JsonElement array, JsonSerializerOptions? options)
            => ReadArray(array, e => e.Deserialize<T>(options));

        /// <summary>Read an array node (e.g. the list property of an envelope object).
        /// A null node is an empty list.</summary>
        public List<T> ReadArray(JsonNode? array, JsonSerializerOptions? options)
        {
            if (array == null) { _unreadable.Clear(); return new List<T>(); }
            using var doc = JsonDocument.Parse(array.ToJsonString());
            return ReadArray(doc.RootElement, options);
        }

        /// <summary>Read <paramref name="array"/> element by element with a custom reader.</summary>
        public List<T> ReadArray(JsonElement array, Func<JsonElement, T?> read)
        {
            _unreadable.Clear();
            if (array.ValueKind != JsonValueKind.Array)
                throw new JsonException("expected a JSON array");

            var items = new List<T>();
            foreach (var element in array.EnumerateArray())
            {
                T? item = null;
                try { item = read(element); }
                catch { item = null; }

                if (item != null) items.Add(item);
                else if (element.ValueKind == JsonValueKind.Object) _unreadable.Add(element.Clone());
                // a JSON null / scalar element carries no user data — dropped as before
            }
            return items;
        }

        // ── write ────────────────────────────────────────────────────────────

        /// <summary>Serialize <paramref name="items"/> followed by the preserved unreadable
        /// entries. An unreadable entry whose name (<c>Name</c>/<c>name</c> property) matches
        /// a readable item's <paramref name="keyOf"/> is dropped — the readable one replaced it.</summary>
        public JsonArray WriteArray(IEnumerable<T> items, JsonSerializerOptions? options, Func<T, string?>? keyOf = null)
            => WriteArray(items, i => JsonSerializer.SerializeToNode(i, options), keyOf);

        /// <summary><see cref="WriteArray(IEnumerable{T}, JsonSerializerOptions?, Func{T, string?}?)"/>
        /// with a custom per-item writer.</summary>
        public JsonArray WriteArray(IEnumerable<T> items, Func<T, JsonNode?> write, Func<T, string?>? keyOf = null)
        {
            var array = new JsonArray();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item == null) continue;
                var node = write(item);
                if (node == null) continue;
                array.Add(node);
                string? key = keyOf?.Invoke(item);
                if (!string.IsNullOrEmpty(key)) keys.Add(key);
            }
            foreach (var raw in _unreadable)
            {
                if (keyOf != null && TryGetName(raw, out string name) && keys.Contains(name)) continue;
                array.Add(JsonNode.Parse(raw.GetRawText()));
            }
            return array;
        }

        /// <summary>Serialize to a JSON string (indentation etc. from <paramref name="options"/>).</summary>
        public string Serialize(IEnumerable<T> items, JsonSerializerOptions? options, Func<T, string?>? keyOf = null)
            => WriteArray(items, options, keyOf).ToJsonString(options ?? JsonSerializerOptions.Default);

        private static bool TryGetName(JsonElement raw, out string name)
        {
            name = string.Empty;
            if (raw.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in raw.EnumerateObject())
            {
                if (string.Equals(p.Name, "Name", StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind == JsonValueKind.String)
                {
                    name = p.Value.GetString() ?? string.Empty;
                    return name.Length > 0;
                }
            }
            return false;
        }
    }

    /// <summary>#966 — helpers for "envelope" store files: a JSON object whose entries
    /// live in one array property (e.g. <c>{ "activeName": …, "configs": [ … ] }</c>).</summary>
    public static class TolerantJsonEnvelope
    {
        /// <summary>Remove and return the array property <paramref name="propertyName"/>
        /// (matched case-insensitively) from <paramref name="root"/>, so the rest of the
        /// envelope can be deserialized on its own.</summary>
        public static JsonNode? TakeProperty(JsonObject root, string propertyName)
        {
            foreach (var kv in root)
            {
                if (string.Equals(kv.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    string key = kv.Key;
                    var value = kv.Value;
                    root.Remove(key);
                    return value;
                }
            }
            return null;
        }

        /// <summary>The JSON name <paramref name="options"/> gives the C# property
        /// <paramref name="clrName"/>.</summary>
        public static string JsonName(string clrName, JsonSerializerOptions? options)
            => options?.PropertyNamingPolicy?.ConvertName(clrName) ?? clrName;
    }
}
