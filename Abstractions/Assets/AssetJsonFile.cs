// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Assets/AssetJsonFile.cs
//
// Shared shape rule for saved-asset JSON files: a '[' root holds many assets
// (one element each), anything else is a single asset document. Every import
// point that accepts "one or many assets per file" splits through here, so the
// per-kind importers (IAssetSource.ImportJson and friends) only ever deal with
// one entry and stay unaware of the multi-asset form.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FracturingFog.Abstractions.Assets
{
    /// <summary>Root-shape splitter for saved-asset JSON files.</summary>
    public static class AssetJsonFile
    {
        /// <summary>Split a saved-asset file into per-entry JSON documents. An
        /// array root yields one document per element; any other root is a
        /// single-asset file returned as-is. Malformed JSON yields an empty
        /// list rather than throwing, so callers report "no entries" the same
        /// way for a bad file and an empty one.</summary>
        public static IReadOnlyList<string> SplitEntries(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return new[] { json };

                var entries = new List<string>();
                foreach (var element in doc.RootElement.EnumerateArray())
                    entries.Add(element.GetRawText());
                return entries;
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
