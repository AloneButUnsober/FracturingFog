// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Text;

using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Unit coverage for the ASCII / text-art exporter (#226): grid geometry &amp;
/// aspect correction, ramp mapping (incl. invert and smooth-vs-luma driver),
/// per-format structure, colour faithfulness to the source BGRA, the
/// extension→format map, and the file-write round-trip.
/// </summary>
public sealed class AsciiArtRendererTests
{
    // Solid BGRA (0xAARRGGBB) buffer, opaque.
    private static uint[] Solid(int w, int h, byte r, byte g, byte b)
    {
        uint argb = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = argb;
        return px;
    }

    // Horizontal grey gradient: column x has value round(x/(w-1)*255) in R=G=B.
    private static uint[] GreyGradientX(int w, int h)
    {
        var px = new uint[w * h];
        for (int x = 0; x < w; x++)
        {
            byte v = (byte)Math.Round(w == 1 ? 0 : (double)x / (w - 1) * 255.0);
            uint argb = 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | v;
            for (int y = 0; y < h; y++) px[y * w + x] = argb;
        }
        return px;
    }

    // ── geometry / aspect ─────────────────────────────────────────────

    [Fact]
    public void PlainText_GridMatchesColumnsAndAspectDerivedRows()
    {
        // 100x100 source, 10 cols, cell aspect 2 → rows = round(10 * 1 / 2) = 5.
        var px = Solid(100, 100, 200, 200, 200);
        var opt = new AsciiArtOptions
        { Format = AsciiArtFormat.PlainText, Columns = 10, CellAspect = 2.0 };

        string art = AsciiArtRenderer.Render(px, null, 100, 100, opt);
        // Trailing '\n' after each row → split yields rows + 1 (last empty).
        var rows = art.Split('\n');
        Assert.Equal("", rows[^1]);
        var content = rows[..^1];
        Assert.Equal(5, content.Length);
        Assert.All(content, line => Assert.Equal(10, line.Length));
    }

    [Fact]
    public void Columns_ClampToAtLeastOne()
    {
        var px = Solid(4, 4, 10, 10, 10);
        var opt = new AsciiArtOptions { Format = AsciiArtFormat.PlainText, Columns = 0 };
        string art = AsciiArtRenderer.Render(px, null, 4, 4, opt);
        Assert.False(string.IsNullOrEmpty(art));
        Assert.Equal(1, art.Split('\n')[0].Length);
    }

    // ── ramp mapping ──────────────────────────────────────────────────

    [Fact]
    public void PlainText_RampMapsDarkToSpaceAndBrightToMax()
    {
        // width==cols → each cell is exactly one source column (no blur).
        // height 2, aspect 2 → rows = round(10 * (2/10) / 2) = 1.
        var px = GreyGradientX(10, 2);
        var opt = new AsciiArtOptions
        { Format = AsciiArtFormat.PlainText, Columns = 10, CellAspect = 2.0, Ramp = " .:-=+*#%@" };

        string art = AsciiArtRenderer.Render(px, null, 10, 2, opt);
        string row = art.Split('\n')[0];
        Assert.Equal(10, row.Length);
        Assert.Equal(' ', row[0]);     // darkest → ramp[0]
        Assert.Equal('@', row[^1]);    // brightest → ramp[last]
    }

    [Fact]
    public void Invert_FlipsRampEnds()
    {
        var px = GreyGradientX(10, 2);
        var opt = new AsciiArtOptions
        {
            Format = AsciiArtFormat.PlainText, Columns = 10, CellAspect = 2.0,
            Ramp = " .:-=+*#%@", Invert = true,
        };
        string row = AsciiArtRenderer.Render(px, null, 10, 2, opt).Split('\n')[0];
        Assert.Equal('@', row[0]);     // inverted: darkest → ramp[last]
        Assert.Equal(' ', row[^1]);
    }

    [Fact]
    public void SmoothField_DrivesRampOverLuminance()
    {
        // Pixels are uniformly bright white (luma ≈ 1 everywhere) but the smooth
        // field ramps 0→max across x. With UseSmoothField the glyph must follow
        // the smooth field, not the (flat, saturated) luma.
        int w = 10, h = 2;
        var px = Solid(w, h, 255, 255, 255);
        var smooth = new float[w * h];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++) smooth[y * w + x] = x;   // 0..9

        var opt = new AsciiArtOptions
        {
            Format = AsciiArtFormat.PlainText, Columns = 10, CellAspect = 2.0,
            Ramp = " .:-=+*#%@", UseSmoothField = true,
        };
        string row = AsciiArtRenderer.Render(px, smooth, w, h, opt).Split('\n')[0];
        Assert.Equal(' ', row[0]);     // smooth 0 → blank end despite white pixel
        Assert.Equal('@', row[^1]);    // smooth max → full end
    }

    [Fact]
    public void SmoothField_OutlierDoesNotCrushMidtones()
    {
        // Most pixels share a modest smooth value; a few deep-boundary pixels
        // spike very high (as happens at high iteration counts). Normalising by
        // the raw max would map the modest majority to ~0 (blank), fading the art
        // to black. The percentile normaliser must keep those mid-tones dense.
        int w = 200, h = 2;
        var px = Solid(w, h, 255, 255, 255);
        var smooth = new float[w * h];
        for (int i = 0; i < smooth.Length; i++) smooth[i] = 10f;
        smooth[w - 1] = 2000f;                 // a lone deep-boundary spike (<0.5%)

        var opt = new AsciiArtOptions
        {
            Format = AsciiArtFormat.PlainText, Columns = 20, CellAspect = 2.0,
            Ramp = " .:-=+*#%@", UseSmoothField = true,
        };
        string row = AsciiArtRenderer.Render(px, smooth, w, h, opt).Split('\n')[0];
        int idx = opt.Ramp.IndexOf(row[0]); // a modest-smooth cell
        Assert.True(idx >= opt.Ramp.Length - 3,
            $"outlier crushed the mid-tones: '{row[0]}' (idx {idx})");
    }

    [Fact]
    public void RampFromColorLuma_DrivesRampFromPixelsOverSmooth()
    {
        // Opposite of the smooth-field test: the smooth field is flat-high
        // everywhere (would map every cell to '@'), but the pixel luma ramps
        // 0→bright across x. With RampFromColorLuma the glyph must follow the
        // pixels, so the left end is blank and the right end full.
        int w = 10, h = 2;
        var px = GreyGradientX(w, h);          // luma 0 → 1 across x
        var smooth = new float[w * h];
        for (int i = 0; i < smooth.Length; i++) smooth[i] = 100f; // flat, non-zero

        var opt = new AsciiArtOptions
        {
            Format = AsciiArtFormat.PlainText, Columns = 10, CellAspect = 2.0,
            Ramp = " .:-=+*#%@", UseSmoothField = true, RampFromColorLuma = true,
        };
        string row = AsciiArtRenderer.Render(px, smooth, w, h, opt).Split('\n')[0];
        Assert.Equal(' ', row[0]);     // dark pixels → blank, despite high smooth
        Assert.Equal('@', row[^1]);    // bright pixels → full
    }

    [Fact]
    public void Interior_SmoothZero_RendersBlank()
    {
        // Mixed frame: left half interior (smooth 0), right half exterior (high
        // smooth) so the field normaliser has a non-zero max. Interior cells must
        // land on the blank ramp end; exterior cells must not.
        int w = 8, h = 2;
        var px = Solid(w, h, 255, 255, 255); // uniform bright: only smooth varies
        var smooth = new float[w * h];
        for (int x = 4; x < w; x++)
            for (int y = 0; y < h; y++) smooth[y * w + x] = 50f;

        var opt = new AsciiArtOptions
        { Format = AsciiArtFormat.PlainText, Columns = 8, CellAspect = 2.0, UseSmoothField = true };

        string row = AsciiArtRenderer.Render(px, smooth, w, h, opt).Split('\n')[0];
        Assert.Equal(8, row.Length);
        for (int x = 0; x < 4; x++) Assert.Equal(' ', row[x]);   // interior → blank
        for (int x = 4; x < 8; x++) Assert.NotEqual(' ', row[x]); // exterior → ink
    }

    // ── colour faithfulness ───────────────────────────────────────────

    [Fact]
    public void Ansi_EmitsTruecolorEscapeMatchingPixel()
    {
        var px = Solid(4, 4, 48, 96, 160);
        var opt = new AsciiArtOptions { Format = AsciiArtFormat.Ansi, Columns = 4 };
        string art = AsciiArtRenderer.Render(px, null, 4, 4, opt);
        Assert.Contains("\x1b[38;2;48;96;160m", art);
        Assert.Contains("\x1b[0m", art); // per-line reset
    }

    [Fact]
    public void Html_EmitsPreAndHexColorSpan()
    {
        var px = Solid(4, 4, 48, 96, 160);
        var opt = new AsciiArtOptions { Format = AsciiArtFormat.Html, Columns = 4 };
        string art = AsciiArtRenderer.Render(px, null, 4, 4, opt);
        Assert.Contains("<pre", art);
        Assert.Contains("</pre>", art);
        Assert.Contains("color:#3060a0", art);
    }

    [Fact]
    public void Html_EscapesMarkupGlyphs()
    {
        // A mid grey lands on a ramp glyph; ensure no raw '<'/'>'/'&' leaks from
        // the ramp into markup. Use a ramp made entirely of markup-significant
        // characters so every cell exercises the escaper.
        var px = Solid(4, 4, 130, 130, 130);
        var opt = new AsciiArtOptions
        { Format = AsciiArtFormat.Html, Columns = 4, Ramp = "<>&<>&<>&<" };
        string art = AsciiArtRenderer.Render(px, null, 4, 4, opt);
        // Anchor to a real coloured span (not the <pre> tag): between its '>' and
        // the closing '</span>' only escaped entities may appear.
        int span = art.IndexOf("color:#", StringComparison.Ordinal);
        Assert.True(span >= 0);
        int open = art.IndexOf("\">", span, StringComparison.Ordinal) + 2;
        int close = art.IndexOf("</span>", open, StringComparison.Ordinal);
        Assert.True(close > open);
        string body = art[open..close];
        Assert.DoesNotContain('<', body);
        Assert.DoesNotContain('>', body);
        Assert.Contains("&", body); // an escaped entity (&lt; / &gt; / &amp;)
    }

    // ── per-format structure ──────────────────────────────────────────

    [Fact]
    public void Svg_IsWellFormedVectorDoc()
    {
        var px = GreyGradientX(20, 8);
        var opt = new AsciiArtOptions { Format = AsciiArtFormat.Svg, Columns = 20 };
        string art = AsciiArtRenderer.Render(px, null, 20, 8, opt);
        Assert.StartsWith("<svg", art.TrimStart());
        Assert.Contains("</svg>", art);
        Assert.Contains("<text", art);
        Assert.Contains("<tspan", art);
        Assert.Contains("font-family=\"monospace\"", art);
    }

    [Fact]
    public void HalfBlock_UsesUpperBlockWithFgAndBgColors()
    {
        var px = GreyGradientX(12, 8);
        var opt = new AsciiArtOptions { Format = AsciiArtFormat.AnsiHalfBlock, Columns = 12 };
        string art = AsciiArtRenderer.Render(px, null, 12, 8, opt);
        Assert.Contains("▀", art);
        Assert.Contains("\x1b[38;2;", art);  // foreground (top sub-row)
        Assert.Contains("\x1b[48;2;", art);  // background (bottom sub-row)
        Assert.Contains("\x1b[0m", art);
    }

    [Fact]
    public void Braille_AllGlyphsInBrailleBlockAndNotAllBlank()
    {
        // Bright gradient guarantees some dots cross the ink threshold.
        var px = GreyGradientX(40, 16);
        var opt = new AsciiArtOptions { Format = AsciiArtFormat.Braille, Columns = 40 };
        string art = AsciiArtRenderer.Render(px, null, 40, 16, opt);

        bool anyInk = false;
        foreach (char c in art)
        {
            if (c == '\n') continue;
            Assert.InRange(c, (char)0x2800, (char)0x28FF);
            if (c != (char)0x2800) anyInk = true;
        }
        Assert.True(anyInk, "braille output should not be entirely blank");
    }

    // ── extension map + file round-trip ───────────────────────────────

    [Theory]
    [InlineData(AsciiArtFormat.PlainText, ".txt")]
    [InlineData(AsciiArtFormat.Ansi, ".ans")]
    [InlineData(AsciiArtFormat.AnsiHalfBlock, ".ans")]
    [InlineData(AsciiArtFormat.Html, ".html")]
    [InlineData(AsciiArtFormat.Svg, ".svg")]
    [InlineData(AsciiArtFormat.Braille, ".txt")]
    public void ExtensionFor_MapsEveryFormat(AsciiArtFormat fmt, string ext)
        => Assert.Equal(ext, AsciiArtRenderer.ExtensionFor(fmt));

    [Fact]
    public void WriteToFile_WritesUtf8RoundTrippingContent()
    {
        var px = GreyGradientX(30, 12);
        var opt = new AsciiArtOptions { Format = AsciiArtFormat.Html, Columns = 30 };
        string expected = AsciiArtRenderer.Render(px, null, 30, 12, opt);

        string path = Path.Combine(Path.GetTempPath(), $"ff-ascii-{Guid.NewGuid():N}.html");
        try
        {
            AsciiArtRenderer.WriteToFile(px, null, 30, 12, opt, path);
            Assert.True(File.Exists(path));
            string actual = File.ReadAllText(path, Encoding.UTF8);
            Assert.Equal(expected, actual);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── guards ────────────────────────────────────────────────────────

    [Fact]
    public void Render_NullPixels_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => AsciiArtRenderer.Render(null!, null, 4, 4, new AsciiArtOptions()));

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-1, 4)]
    public void Render_NonPositiveDims_Throws(int w, int h)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => AsciiArtRenderer.Render(new uint[16], null, w, h, new AsciiArtOptions()));

    [Fact]
    public void Render_OnePixelSource_DoesNotThrow()
    {
        var px = Solid(1, 1, 123, 45, 67);
        string art = AsciiArtRenderer.Render(px, null, 1, 1, new AsciiArtOptions { Columns = 1 });
        Assert.False(string.IsNullOrEmpty(art));
    }

    // ── watermark string-export (#241 follow-up) ───────────────────────
    //
    // The single-frame string formats stamp the same resolved WatermarkRender the
    // live Terminal / video paths use: PlainText/Ansi via the cell grid, the
    // sub-cell formats (HTML/SVG/half-block/Braille) via the ink overlay. The
    // block-font ink glyph is '#', so its presence in an otherwise '#'-free render
    // is the watermark signal.

    private static FracturingFog.Imaging.WatermarkRender Mark(
        string top = "ZOOM",
        FracturingFog.Models.WatermarkPlacement place = FracturingFog.Models.WatermarkPlacement.Bottom)
        => new()
        {
            TopText = top,
            SubText = "FF v1",
            TextColor = new FracturingFog.Models.RgbDef(10, 220, 40),
            Placement = place,
            Justify = FracturingFog.Models.WatermarkJustify.Right,
        };

    private static AsciiArtOptions Opt(AsciiArtFormat fmt, FracturingFog.Imaging.WatermarkRender? wm)
        => new()
        {
            Format = fmt,
            Columns = 120,
            CellAspect = 2.0,
            Ramp = " .:-=+*",            // no '#' in the ramp → '#' means watermark ink
            Watermark = wm,
        };

    [Fact]
    public void PlainText_Watermark_StampsBlockInk()
    {
        var px = Solid(240, 240, 90, 90, 90);   // mid-grey → ramp mid, never '#'
        string plain = AsciiArtRenderer.Render(px, null, 240, 240, Opt(AsciiArtFormat.PlainText, null));
        string inked = AsciiArtRenderer.Render(px, null, 240, 240, Opt(AsciiArtFormat.PlainText, Mark()));
        Assert.DoesNotContain('#', plain);
        Assert.Contains('#', inked);            // block-font ink now present
    }

    [Fact]
    public void Html_Watermark_UsesResolvedColour()
    {
        var px = Solid(240, 240, 90, 90, 90);
        string html = AsciiArtRenderer.Render(px, null, 240, 240, Opt(AsciiArtFormat.Html, Mark()));
        Assert.Contains("#0adc28", html);       // 10,220,40 hex → the ink colour
        Assert.Contains(">#<", html);           // an ink glyph inside a span
    }

    [Fact]
    public void Svg_Watermark_EmitsInkGlyphAndColour()
    {
        var px = Solid(240, 240, 90, 90, 90);
        string svg = AsciiArtRenderer.Render(px, null, 240, 240, Opt(AsciiArtFormat.Svg, Mark()));
        Assert.Contains("fill=\"#0adc28\"", svg);
        Assert.Contains(">#</tspan>", svg);
    }

    [Fact]
    public void HalfBlock_Watermark_EmitsInkGlyph()
    {
        var px = Solid(240, 240, 90, 90, 90);
        string ans = AsciiArtRenderer.Render(px, null, 240, 240, Opt(AsciiArtFormat.AnsiHalfBlock, Mark()));
        // Ink paints the block glyph in the resolved colour, not the ▀ half-block.
        Assert.Contains("\x1b[38;2;10;220;40m#", ans);
    }

    [Fact]
    public void Braille_Watermark_OverridesDotCellWithInk()
    {
        var px = Solid(240, 240, 90, 90, 90);
        string plain = AsciiArtRenderer.Render(px, null, 240, 240, Opt(AsciiArtFormat.Braille, null));
        string inked = AsciiArtRenderer.Render(px, null, 240, 240, Opt(AsciiArtFormat.Braille, Mark()));
        Assert.DoesNotContain('#', plain);      // braille dots are U+28xx, never '#'
        Assert.Contains('#', inked);
    }

    [Fact]
    public void Watermark_Placement_TopVsBottom_MovesInk()
    {
        var px = Solid(240, 240, 90, 90, 90);
        string bottom = AsciiArtRenderer.Render(px, null, 240, 240,
            Opt(AsciiArtFormat.PlainText, Mark(place: FracturingFog.Models.WatermarkPlacement.Bottom)));
        string top = AsciiArtRenderer.Render(px, null, 240, 240,
            Opt(AsciiArtFormat.PlainText, Mark(place: FracturingFog.Models.WatermarkPlacement.Top)));

        var bRows = bottom.Split('\n');
        var tRows = top.Split('\n');
        int FirstInk(string[] rows)
        {
            for (int i = 0; i < rows.Length; i++) if (rows[i].Contains('#')) return i;
            return -1;
        }
        Assert.True(FirstInk(tRows) < FirstInk(bRows), "top placement inks a higher row than bottom");
    }

    [Fact]
    public void NoWatermark_LeavesRampGlyphsOnly()
    {
        // A null Watermark must not perturb the art (regression guard for the
        // overlay short-circuit).
        var px = GreyGradientX(120, 60);
        string a = AsciiArtRenderer.Render(px, null, 120, 60, Opt(AsciiArtFormat.PlainText, null));
        string b = AsciiArtRenderer.Render(px, null, 120, 60,
            new AsciiArtOptions { Format = AsciiArtFormat.PlainText, Columns = 120, CellAspect = 2.0, Ramp = " .:-=+*" });
        Assert.Equal(a, b);
    }
}
