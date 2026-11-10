// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorGenThemeLoader.cs (#1684)
//
// Turns ColorGen DSL themes into palette-list entries the same way in the live
// app and in batch. The live editor's Compile & Load and the batch loader both
// build their GenerateOptions here, so a saved theme renders headless exactly as
// it did on screen (name, description, trap shape #611, out-of-bounds colour #615).
//
// Batch calls RegisterSaved(): every entry of colorgen.json (UserColorGenStore,
// written by the editor's Save…) goes through InterpretedColorMap.TryCreate and
// ColorPalette.RegisterHotLoaded, so `--batch --theme <saved name>` resolves it.
// A theme that no longer parses is skipped and reported, never fatal.

using System;
using System.Collections.Generic;
using System.Globalization;
using FracturingFog.ColorGen;

namespace FracturingFog.Models
{
    public static class ColorGenThemeLoader
    {
        /// <summary>The options a ColorGen theme is created with: category "User" and
        /// the theme's own name, description, trap shape and out-of-bounds colour.</summary>
        public static GenerateOptions Options(string name, string? description, string? trapShape, string? outOfBoundsArgb)
            => new()
            {
                ThemeName = name,
                Category = "User",
                Description = description ?? "",
                TrapShape = string.IsNullOrWhiteSpace(trapShape) ? "Point" : trapShape,   // #611
                OutOfBoundsColor = ParseArgb(outOfBoundsArgb),                            // #615
            };

        /// <summary>#615 — a packed "AARRGGBB" hex string, or null for none / unreadable.</summary>
        public static uint? ParseArgb(string? hex)
            => !string.IsNullOrWhiteSpace(hex)
               && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed)
                ? packed : null;

        /// <summary>Parse one theme; null with <paramref name="error"/> set when it does not parse.</summary>
        public static InterpretedColorMap? Create(string source, GenerateOptions options, out string? error)
            => InterpretedColorMap.TryCreate(source, options, out error);

        /// <summary>Load colorgen.json and register every saved theme in the palette
        /// list. Returns the themes that failed to parse, with their errors.</summary>
        public static IReadOnlyList<(string Name, string Error)> RegisterSaved()
        {
            var failed = new List<(string, string)>();
            var store = UserColorGenStore.Instance;
            store.Load();
            foreach (var e in store.Entries)
            {
                if (string.IsNullOrWhiteSpace(e.Name)) continue;
                var map = Create(e.Source, Options(e.Name, e.Description, e.TrapShape, e.OutOfBoundsColorArgb), out string? err);
                if (map == null) { failed.Add((e.Name, err ?? "Theme failed to parse.")); continue; }
                ColorPalette.RegisterHotLoaded(map);
            }
            return failed;
        }
    }
}
