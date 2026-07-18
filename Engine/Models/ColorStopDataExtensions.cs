// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorStopDataExtensions.cs
//
// System.Drawing-flavoured helpers for the bare ColorStopData DTO that
// lives in FracturingFog.Abstractions. The DTO itself is intentionally
// shape-only (Position + R/G/B + parameterless ctor) so it can sit in
// the abstraction layer without dragging System.Drawing across the
// host / Avalonia / PaletteBuilder.Lib boundary.
//
// These extensions add two host-side conveniences:
//   • s.ToColorStop()          — DTO → FracturingFog.ColorStop
//   • FromColorStop(stop)      — FracturingFog.ColorStop → DTO
//
// Call sites that previously used `new ColorStopData(stop)` now call
// `ColorStopDataExtensions.FromColorStop(stop)`. Call sites that used
// `dto.ToColorStop()` are unchanged thanks to extension-method syntax.

using System.Drawing;

namespace FracturingFog.Models
{
    public static class ColorStopDataExtensions
    {
        /// <summary>DTO → runtime <c>FracturingFog.ColorStop</c>.</summary>
        public static ColorStop ToColorStop(this ColorStopData data)
            => new ColorStop(data.Position, Color.FromArgb(data.A, data.R, data.G, data.B), data.Midpoint);

        /// <summary>
        /// Runtime <c>FracturingFog.ColorStop</c> → DTO. Replaces the old
        /// <c>new ColorStopData(stop)</c> constructor which lived on the
        /// pre-Abstractions class.
        /// </summary>
        public static ColorStopData FromColorStop(ColorStop stop) => new()
        {
            Position = stop.Position,
            R = stop.Color.R,
            G = stop.Color.G,
            B = stop.Color.B,
            A = stop.Color.A,
            // 0 (struct default) round-trips as 0.5 = linear; keeps built-in
            // themes exporting a neutral midpoint.
            Midpoint = stop.Midpoint <= 0f ? 0.5f : stop.Midpoint,
        };
    }
}
