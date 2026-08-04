// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/AsciiFrame.cs
//
// Shell-neutral payload for a single live ASCII / text-art frame (#227). The
// render host (Engine) produces it from the current frame's colour buffer +
// smooth field; the Avalonia AsciiView control consumes it to paint. Lives in
// Abstractions so the UI project (which does not reference Engine) can receive
// it across the IFractalRenderHost boundary.

namespace FracturingFog.Render
{
    /// <summary>One downsampled character grid. <see cref="Glyphs"/> and
    /// <see cref="Colors"/> are row-major, length <c>Cols*Rows</c>.
    /// <see cref="Colors"/> pack 0x00RRGGBB and are meaningful only when
    /// <see cref="HasColor"/> is true (monochrome frames leave them zero and the
    /// control supplies a default ink).</summary>
    public readonly struct AsciiFrame
    {
        public int Cols { get; }
        public int Rows { get; }
        public char[] Glyphs { get; }
        public uint[] Colors { get; }
        public bool HasColor { get; }

        public AsciiFrame(int cols, int rows, char[] glyphs, uint[] colors, bool hasColor)
        {
            Cols = cols; Rows = rows; Glyphs = glyphs; Colors = colors; HasColor = hasColor;
        }

        /// <summary>True when the grid is degenerate / not yet populated.</summary>
        public bool IsEmpty => Cols <= 0 || Rows <= 0 || Glyphs is null || Glyphs.Length < Cols * Rows;
    }
}
