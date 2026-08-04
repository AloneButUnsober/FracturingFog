// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/AsciiArtOptions.cs
//
// Knobs for the ASCII / text-art exporter (#226). Defaults produce a sensible
// terminal-width colored render; every field is overridable by the still-export
// UI (follow-up) or the --asciiart probe.

namespace FracturingFog.Imaging
{
    /// <summary>Render settings for <see cref="AsciiArtRenderer"/>.</summary>
    public sealed class AsciiArtOptions
    {
        /// <summary>Target character columns. Rows are derived from the source
        /// aspect ratio and <see cref="CellAspect"/> so the art is not squished.
        /// Half-block/braille formats then sub-sample each cell vertically.</summary>
        public int Columns { get; set; } = 120;

        /// <summary>Output encoding. See <see cref="AsciiArtFormat"/>.</summary>
        public AsciiArtFormat Format { get; set; } = AsciiArtFormat.Ansi;

        /// <summary>Glyph ramp, darkest → lightest. Used by PlainText/Ansi/Html/Svg.
        /// The default 10-step ramp is legible everywhere; <see cref="FineRamp"/>
        /// swaps in a 70-step ramp for smoother gradients on capable displays.</summary>
        public string Ramp { get; set; } = " .:-=+*#%@";

        /// <summary>70-step high-detail ramp, perceptual-luminance ordered.</summary>
        public const string FineRamp =
            " .'`^\",:;Il!i><~+_-?][}{1)(|\\/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$";

        /// <summary>Character cell height ÷ width for the target font. Monospace
        /// cells run ~2× tall as wide; the downsampler compensates so circles
        /// stay round. Ignored by half-block/braille (they fix aspect natively).</summary>
        public double CellAspect { get; set; } = 2.0;

        /// <summary>Invert the ramp (light background / dark subject).</summary>
        public bool Invert { get; set; }

        /// <summary>Prefer the smooth iteration count (banding-free) as the ramp
        /// driver when a smooth buffer is supplied; falls back to pixel luminance
        /// otherwise. Colored formats always tint from the pixel buffer.</summary>
        public bool UseSmoothField { get; set; } = true;

        /// <summary>Background colour token for HTML/SVG documents (CSS color).</summary>
        public string BackgroundCss { get; set; } = "#000000";

        /// <summary>CSS font-size (px) per cell for HTML/SVG. Drives SVG canvas
        /// size and the HTML <c>&lt;pre&gt;</c> scale.</summary>
        public double FontSizePx { get; set; } = 12.0;

        /// <summary>Ink threshold for the Braille format, applied after a
        /// perceptual sqrt of the normalised field (dot on when boosted field ≥
        /// this). Lower = denser dots. Ignored by other formats.</summary>
        public double BrailleThreshold { get; set; } = 0.35;

        /// <summary>Convenience: switch <see cref="Ramp"/> to <see cref="FineRamp"/>.</summary>
        public AsciiArtOptions WithFineRamp() { Ramp = FineRamp; return this; }
    }
}
