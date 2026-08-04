// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Imaging/AsciiWatermarkStyle.cs
//
// How the ASCII watermark painter (Engine/Imaging/AsciiWatermark.cs) renders the
// resolved WatermarkRender into a character grid. This is a rendering style only
// — it does NOT change WHAT is drawn (the text, colour, placement, and mandatory
// program sub-line all still come from WatermarkResolver); it only changes the
// glyph shapes used to draw the top-line, so Terminal Mode / ASCII export can
// carry a watermark that complements the character art instead of a TTF overlay.

namespace FracturingFog.Imaging
{
    /// <summary>Glyph style for the ASCII watermark top-line. All three honour
    /// the same resolved <see cref="WatermarkRender"/> (text / colour / placement
    /// / justify) and the mandatory program sub-line.</summary>
    public enum AsciiWatermarkStyle
    {
        /// <summary>Five-row block capitals for the top-line, plain sub-line
        /// beneath. Most legible, largest footprint.</summary>
        Block = 0,

        /// <summary>Single plain text row ("Region - Theme" · sub-line). Smallest
        /// footprint, trivially legible — the watermark as a terminal label.</summary>
        PlainLabel = 1,

        /// <summary>Block capitals wrapped in a line-drawing border box — the
        /// "terminal HUD" look. Largest footprint.</summary>
        BoxedBanner = 2,
    }
}
