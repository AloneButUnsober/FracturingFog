// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Cli/SessionColorGenThemes.cs (#1684)
//
// Names of the ColorGen themes loaded into the palette list this session (the
// editor's Compile & Load). Batch does not load ColorGen themes yet, so the
// Control Center Command builder reports one as a fidelity gap instead of
// emitting a --theme the batch would silently replace with HSV. The shell's
// hot-load bridge adds to it; UI.Avalonia reads it (it does not reference Engine,
// where the palette list lives).

using System;
using System.Collections.Generic;

namespace FracturingFog.Cli
{
    public static class SessionColorGenThemes
    {
        private static readonly HashSet<string> s_names = new(StringComparer.Ordinal);
        private static readonly object s_lock = new();

        public static void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            lock (s_lock) s_names.Add(name);
        }

        public static bool Contains(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            lock (s_lock) return s_names.Contains(name);
        }
    }
}
