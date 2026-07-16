// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/ColorStopData.cs
//
// Tiny JSON-friendly DTO for a single gradient stop. Promoted to the
// shared Abstractions library so the host (FracturingFog WinExe) and the
// new PaletteBuilder.Lib both consume the SAME type instead of each
// defining their own copy. Previously both projects each declared a
// public sealed class FracturingFog.Models.ColorStopData; once the host
// added a ProjectReference to PaletteBuilder.Lib the duplicate-type
// definitions collided (CS1503 / CS0029 in any file that mentioned the
// name explicitly, e.g. Views/ImagePaletteDialog.cs).
//
// Shape kept minimal — Position + R/G/B + parameterless ctor. The
// System.Drawing.Color and FracturingFog.ColorStop interop helpers that
// the original host-side class exposed live in
// Models/ColorStopDataExtensions.cs (host-only, since
// System.Drawing.Color is not part of the abstraction surface).

namespace FracturingFog.Models
{
    /// <summary>
    /// Single gradient stop, JSON-friendly (avoids serializing
    /// System.Drawing.Color). See <c>ColorStopDataExtensions</c> for the
    /// ColorStop conversion helpers.
    /// </summary>
    public sealed class ColorStopData
    {
        public float Position { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
    }
}
