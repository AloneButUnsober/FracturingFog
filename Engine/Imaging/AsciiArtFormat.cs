// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/AsciiArtFormat.cs
//
// Output-format selector for the ASCII / text-art exporter (#226). Every
// member emits UTF-8 text, so AsciiArtRenderer.Render returns a string for all
// of them and the caller writes the file. The spread deliberately covers the
// broadest useful range: plain / terminal / web / vector / high-density.

namespace FracturingFog.Imaging
{
    /// <summary>Target text-art encoding for <see cref="AsciiArtRenderer"/>.</summary>
    public enum AsciiArtFormat
    {
        /// <summary>Density ramp, monochrome. Universal, paste-anywhere (.txt).</summary>
        PlainText = 0,

        /// <summary>One glyph per cell, each wrapped in a 24-bit ANSI truecolor
        /// escape (<c>\x1b[38;2;r;g;bm</c>). Terminal, palette-faithful (.ans).</summary>
        Ansi,

        /// <summary>Upper-half block <c>▀</c> per cell: foreground = top sub-row
        /// colour, background = bottom sub-row colour. Doubles vertical
        /// resolution and fixes the cell aspect for free (.ans).</summary>
        AnsiHalfBlock,

        /// <summary>A <c>&lt;pre&gt;</c> block of colored <c>&lt;span&gt;</c>
        /// glyphs. Most portable "beautiful" output, shareable (.html).</summary>
        Html,

        /// <summary>Monospace <c>&lt;text&gt;</c> cells. Vector, resolution
        /// independent (.svg).</summary>
        Svg,

        /// <summary>2×4 Unicode braille dot cells (U+2800…), monochrome. Highest
        /// structural density per character (.txt).</summary>
        Braille,
    }
}
