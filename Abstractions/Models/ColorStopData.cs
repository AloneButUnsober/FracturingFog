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

        /// <summary>
        /// Segment midpoint bias in (0,1) for the gradient segment that
        /// <em>starts</em> at this stop (Phase B / F7). 0.5 = linear (default);
        /// smaller pushes the halfway colour toward this stop, larger toward the
        /// next. 0 or out-of-range is treated as 0.5 so legacy themes are
        /// unaffected.
        /// </summary>
        public float Midpoint { get; set; } = 0.5f;
    }
}
