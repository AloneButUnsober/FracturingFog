// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FractalTypeNames.cs (#1154 / #1155)
//
// One place that turns a fractal-type NAME into a FractalType, honouring names
// that were renamed. Every reader of a type name — --batch --fractal, the
// Command composer, region / scene / animation / workspace / slideshow JSON,
// server requests, slideshow and workspace lookups — goes through here, so a
// file or script written before a rename keeps loading.
//
// Renames (numeric values unchanged, so integer-persisted data never moved):
//   DualOrbitEscape → JulibrotPair   (#1155)
//   DualOrbitVolume → Julibrot       (#1154)
// The 3D volume is a 3D slice of the classic Julibrot (z₀, c) space, and the 2D
// type reads two points of that space sharing their parameter (#1130 novelty
// research, Docs/Technical/DualOrbit-Coloring-RnD.md §8).

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FracturingFog
{
    public static class FractalTypeNames
    {
        /// <summary>Old type names → current type (case-insensitive).</summary>
        public static readonly IReadOnlyDictionary<string, FractalType> Legacy =
            new Dictionary<string, FractalType>(StringComparer.OrdinalIgnoreCase)
            {
                ["DualOrbitEscape"] = FractalType.JulibrotPair,
                ["DualOrbitVolume"] = FractalType.Julibrot,
            };

        /// <summary>Parse a fractal-type name: the current enum names, then the
        /// legacy (renamed) names. A defined integer value is accepted too, as
        /// Enum.TryParse did at the call sites this replaces.</summary>
        public static bool TryParse(string? name, bool ignoreCase, out FractalType type)
        {
            type = default;
            if (string.IsNullOrWhiteSpace(name)) return false;
            string s = name.Trim();
            if (int.TryParse(s, out int n))
            {
                type = (FractalType)n;
                return Enum.IsDefined(type);
            }
            if (Enum.TryParse(s, ignoreCase, out type) && Enum.IsDefined(type)) return true;
            // Legacy names are accepted case-insensitively: they only ever come from
            // old files / scripts, where leniency is the point.
            if (Legacy.TryGetValue(s, out type)) return true;
            type = default;
            return false;
        }

        /// <summary>The current name for a stored type name (renamed names map to
        /// their new name); unknown names come back unchanged. For string-typed
        /// lists such as SlideshowConfig.FilterFractalTypes.</summary>
        public static string Canonical(string name)
            => TryParse(name, out var t) ? t.ToString() : name;

        /// <summary>Case-insensitive <see cref="TryParse(string?, bool, out FractalType)"/>.</summary>
        public static bool TryParse(string? name, out FractalType type) => TryParse(name, true, out type);
    }

    /// <summary>#1154 / #1155 — FractalType as a JSON string (current name on
    /// write), reading current names, legacy (renamed) names and integers. Use in
    /// place of <see cref="JsonStringEnumConverter"/> for FractalType: register it
    /// BEFORE any JsonStringEnumConverter in an options' converter list, or put it
    /// on the property.</summary>
    public sealed class FractalTypeJsonConverter : JsonConverter<FractalType>
    {
        public override FractalType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int n))
                return (FractalType)n;
            if (reader.TokenType == JsonTokenType.String)
            {
                string? s = reader.GetString();
                if (FractalTypeNames.TryParse(s, out var t)) return t;
                if (int.TryParse(s, out int m)) return (FractalType)m;
                throw new JsonException($"Unknown fractal type '{s}'.");
            }
            throw new JsonException($"Unexpected token {reader.TokenType} for a fractal type.");
        }

        public override void Write(Utf8JsonWriter writer, FractalType value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());

        // Dictionary keys (Dictionary<FractalType, …>) round-trip by name too.
        public override FractalType ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? s = reader.GetString();
            if (FractalTypeNames.TryParse(s, out var t)) return t;
            if (int.TryParse(s, out int m)) return (FractalType)m;
            throw new JsonException($"Unknown fractal type '{s}'.");
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, FractalType value, JsonSerializerOptions options)
            => writer.WritePropertyName(value.ToString());
    }
}
