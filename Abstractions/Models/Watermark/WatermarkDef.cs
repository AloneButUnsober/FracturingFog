// Abstractions/Models/Watermark/WatermarkDef.cs
//
// User-defined watermark — the top-line text + colors + edge placement that
// every render surface (image save, poster, slideshow, video, client/server)
// honors when the user has enabled a custom watermark. The mandatory
// program/version sub-line is composed at render time and is NOT stored here:
// users can re-style/re-place it via Placement+Justify, but not edit or hide it.
//
// JSON-only DTO so both shells (legacy WinForms WinExe, Avalonia UI library)
// and the server project can serialize / deserialize without pulling
// System.Drawing or Avalonia colour types into the abstraction surface.

namespace FracturingFog.Models
{
    /// <summary>Where the watermark block anchors on the image. Determines whether
    /// the subtext stacks below (Top/Bottom) or to the side of (Left/Right) the
    /// top-line.</summary>
    public enum WatermarkPlacement
    {
        Left,
        Top,
        Right,
        Bottom,
    }

    /// <summary>Inline alignment of the watermark block along its anchored edge.
    /// For Top/Bottom: shifts horizontally. For Left/Right: maps to Top/Center/
    /// Bottom along the vertical edge (Left → top, Center → middle, Right → bottom).</summary>
    public enum WatermarkJustify
    {
        Left,
        Center,
        Right,
    }

    /// <summary>Opaque RGB triple (no alpha). Used for fully-opaque watermark
    /// text colour.</summary>
    public sealed class RgbDef
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }

        public RgbDef() { }
        public RgbDef(byte r, byte g, byte b) { R = r; G = g; B = b; }
    }

    /// <summary>RGBA quadruple. Used for highlight + background colours where
    /// the alpha channel matters (translucent backdrop / glow halo).</summary>
    public sealed class RgbaDef
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public byte A { get; set; }

        public RgbaDef() { }
        public RgbaDef(byte r, byte g, byte b, byte a) { R = r; G = g; B = b; A = a; }
    }

    /// <summary>Saved, named watermark configuration. Persisted by
    /// <c>UserWatermarkStore</c> as JSON in %APPDATA%\FracturingFog\userwatermarks.json.</summary>
    public sealed class WatermarkDef
    {
        /// <summary>Library key — case-insensitive, must be unique within the store.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Top-line text. Empty string is allowed (then the watermark
        /// renders only the mandatory program/version subtext).</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Top-line glyph fill colour. Defaults to white when missing.</summary>
        public RgbDef TextColor { get; set; } = new RgbDef(255, 255, 255);

        /// <summary>Optional glow / outline tint behind the glyphs. Null = use
        /// the auto-computed contrast outline (current default behaviour).</summary>
        public RgbaDef? HighlightColor { get; set; }

        /// <summary>Optional filled rectangle behind the whole watermark block.
        /// Null = no backdrop.</summary>
        public RgbaDef? BackgroundColor { get; set; }

        public WatermarkPlacement Placement { get; set; } = WatermarkPlacement.Bottom;
        public WatermarkJustify Justify { get; set; } = WatermarkJustify.Right;

        /// <summary>Returns a deep copy — useful when stashing the active
        /// watermark into a region snapshot (so later edits to the library entry
        /// don't mutate the embedded copy).</summary>
        public WatermarkDef Clone() => new WatermarkDef
        {
            Name = Name,
            Text = Text,
            TextColor = new RgbDef(TextColor.R, TextColor.G, TextColor.B),
            HighlightColor = HighlightColor == null
                ? null
                : new RgbaDef(HighlightColor.R, HighlightColor.G, HighlightColor.B, HighlightColor.A),
            BackgroundColor = BackgroundColor == null
                ? null
                : new RgbaDef(BackgroundColor.R, BackgroundColor.G, BackgroundColor.B, BackgroundColor.A),
            Placement = Placement,
            Justify = Justify,
        };
    }
}
