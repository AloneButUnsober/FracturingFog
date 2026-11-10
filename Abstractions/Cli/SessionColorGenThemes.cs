// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Cli/SessionColorGenThemes.cs (#1684)
//
// The ColorGen themes loaded into the palette list this session (the editor's
// Compile & Load), with the source and settings each was loaded from. Batch loads
// the SAVED ColorGen library (colorgen.json), so a loaded theme reproduces headless
// only when it is saved under the same name with the same source, trap shape and
// out-of-bounds colour. The Control Center Command builder asks NeedsSaving and
// reports the theme as a fidelity gap otherwise, instead of emitting a --theme the
// batch would render differently (or replace with HSV). The shell's hot-load
// bridge records each load; UI.Avalonia reads it (it does not reference Engine,
// where the palette list lives).

using System;
using System.Collections.Generic;
using FracturingFog.Models;

namespace FracturingFog.Cli
{
    public static class SessionColorGenThemes
    {
        private readonly record struct Loaded(string Source, string TrapShape, string OutOfBoundsArgb);

        private static readonly Dictionary<string, Loaded> s_loaded = new(StringComparer.Ordinal);
        private static readonly object s_lock = new();

        /// <summary>Record a theme loaded into the palette list (replacing an earlier
        /// load of the same name).</summary>
        public static void Add(string? name, string? source = null, string? trapShape = null, string? outOfBoundsArgb = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            lock (s_lock) s_loaded[name] = new Loaded(source ?? "", Shape(trapShape), outOfBoundsArgb ?? "");
        }

        public static bool Contains(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            lock (s_lock) return s_loaded.ContainsKey(name);
        }

        /// <summary>True when <paramref name="name"/> is a ColorGen theme loaded this
        /// session that batch cannot reproduce: no saved entry of that name, or the
        /// saved source / trap shape / out-of-bounds colour differ from what is loaded.</summary>
        public static bool NeedsSaving(string? name) => NeedsSaving(name, UserColorGenStore.Instance.GetByName(name ?? ""));

        /// <summary>As <see cref="NeedsSaving(string?)"/> against a given saved entry.</summary>
        public static bool NeedsSaving(string? name, UserColorGenEntry? saved)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            Loaded loaded;
            lock (s_lock) { if (!s_loaded.TryGetValue(name, out loaded)) return false; }
            if (saved == null) return true;
            return !string.Equals(saved.Source ?? "", loaded.Source, StringComparison.Ordinal)
                || !string.Equals(Shape(saved.TrapShape), loaded.TrapShape, StringComparison.Ordinal)
                || !string.Equals((saved.OutOfBoundsColorArgb ?? "").Trim(), loaded.OutOfBoundsArgb.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string Shape(string? s) => string.IsNullOrWhiteSpace(s) ? "Point" : s;
    }
}
