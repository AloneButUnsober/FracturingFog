// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/AsciiAnimationFormat.cs
//
// Target encodings for the ASCII animation recorder (#230). Each captures a
// sequence of AsciiCell grids (one per animation frame) plus per-frame timing.

namespace FracturingFog.Imaging
{
    /// <summary>Output container for a recorded ASCII animation. See
    /// <see cref="AsciiAnimationRecorder"/>.</summary>
    public enum AsciiAnimationFormat
    {
        /// <summary>asciinema cast v2: a JSON header line followed by
        /// <c>[time,"o",data]</c> truecolor-ANSI event lines. Plays in a terminal
        /// (<c>asciinema play</c>) or the asciinema web player.</summary>
        AsciinemaCast,

        /// <summary>Self-contained animated SVG: every frame as a group, cycled by
        /// a discrete <c>&lt;animate&gt;</c> opacity track. Shareable, no player.</summary>
        AnimatedSvg,

        /// <summary>Raw truecolor-ANSI frames, each prefixed with clear+home, so a
        /// trivial player (or <c>asciinema</c>-style cat with sleeps) can replay
        /// them. The final frame is what a plain <c>cat</c> leaves on screen.</summary>
        AnsiSequence,
    }
}
