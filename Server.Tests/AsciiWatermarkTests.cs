// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Linq;

using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>Coverage for the ASCII watermark painter (#241) — the ASCII-art
/// renderer for the resolved <see cref="WatermarkRender"/> shared by Terminal
/// Mode and the ASCII export paths.</summary>
public sealed class AsciiWatermarkTests
{
    private static AsciiCell[] BlankGrid(int cols, int rows)
    {
        var g = new AsciiCell[cols * rows];
        for (int i = 0; i < g.Length; i++) g[i] = new AsciiCell(' ', 0, 0, 0);
        return g;
    }

    private static int NonSpace(AsciiCell[] g) => g.Count(c => c.Glyph != ' ');

    private static WatermarkRender Wm(
        string top, string sub = "",
        WatermarkPlacement place = WatermarkPlacement.Bottom,
        WatermarkJustify just = WatermarkJustify.Right,
        byte r = 200, byte gc = 100, byte b = 50)
        => new()
        {
            TopText = top,
            SubText = sub,
            TextColor = new RgbDef(r, gc, b),
            Placement = place,
            Justify = just,
        };

    [Fact]
    public void Block_TopOnly_ProducesFiveRows()
    {
        var lines = AsciiWatermark.BuildLines("MANDEL", "", AsciiWatermarkStyle.Block);
        Assert.Equal(5, lines.Count);
    }

    [Fact]
    public void Block_WithSub_AddsPlainSubRow()
    {
        var lines = AsciiWatermark.BuildLines("MANDEL", "Fracturing Fog", AsciiWatermarkStyle.Block);
        Assert.Equal(6, lines.Count);
        Assert.Equal("Fracturing Fog", lines[^1]);
    }

    [Fact]
    public void PlainLabel_IsSingleRow_WithBothTexts()
    {
        var lines = AsciiWatermark.BuildLines("SEAHORSE", "FF v1 2026", AsciiWatermarkStyle.PlainLabel);
        Assert.Single(lines);
        Assert.Contains("SEAHORSE", lines[0]);
        Assert.Contains("FF v1 2026", lines[0]);
    }

    [Fact]
    public void BoxedBanner_HasBorderRows()
    {
        var lines = AsciiWatermark.BuildLines("HI", "sub", AsciiWatermarkStyle.BoxedBanner);
        Assert.StartsWith("+", lines[0]);
        Assert.EndsWith("+", lines[0]);
        Assert.StartsWith("+", lines[^1]);
        Assert.All(lines.Skip(1).Take(lines.Count - 2), l => Assert.StartsWith("|", l));
    }

    [Fact]
    public void Stamp_WritesInkCells_WithResolvedColour()
    {
        var g = BlankGrid(80, 30);
        AsciiWatermark.Stamp(g, 80, 30, Wm("ABC", r: 200, gc: 100, b: 50), AsciiWatermarkStyle.Block);

        var ink = g.Where(c => c.Glyph != ' ').ToList();
        Assert.NotEmpty(ink);
        Assert.All(ink, c => { Assert.Equal(200, c.R); Assert.Equal(100, c.G); Assert.Equal(50, c.B); });
    }

    [Fact]
    public void Stamp_EmptyText_LeavesGridUntouched()
    {
        var g = BlankGrid(40, 12);
        AsciiWatermark.Stamp(g, 40, 12, Wm("", ""), AsciiWatermarkStyle.Block);
        Assert.Equal(0, NonSpace(g));
    }

    [Fact]
    public void Stamp_BottomVsTop_PutInkInDifferentHalves()
    {
        var bottom = BlankGrid(80, 30);
        AsciiWatermark.Stamp(bottom, 80, 30, Wm("XY", place: WatermarkPlacement.Bottom), AsciiWatermarkStyle.Block);
        var top = BlankGrid(80, 30);
        AsciiWatermark.Stamp(top, 80, 30, Wm("XY", place: WatermarkPlacement.Top), AsciiWatermarkStyle.Block);

        int BottomInkRow(AsciiCell[] grid)
        {
            for (int r = 29; r >= 0; r--)
                for (int c = 0; c < 80; c++)
                    if (grid[r * 80 + c].Glyph != ' ') return r;
            return -1;
        }

        Assert.True(BottomInkRow(bottom) > 15, "bottom placement should ink the lower half");
        // Top placement's first ink row should be near the top edge.
        int firstTop = -1;
        for (int r = 0; r < 30 && firstTop < 0; r++)
            for (int c = 0; c < 80; c++)
                if (top[r * 80 + c].Glyph != ' ') { firstTop = r; break; }
        Assert.InRange(firstTop, 0, 5);
    }

    [Fact]
    public void Stamp_LowercaseInput_StillRenders()
    {
        var g = BlankGrid(60, 20);
        AsciiWatermark.Stamp(g, 60, 20, Wm("mandel"), AsciiWatermarkStyle.Block);
        Assert.True(NonSpace(g) > 0, "lowercase text should upper-case and render");
    }

    [Fact]
    public void Stamp_TinyGrid_DoesNotThrow_AndClips()
    {
        var g = BlankGrid(6, 3); // smaller than the block — must clip, not crash
        var ex = Record.Exception(() =>
            AsciiWatermark.Stamp(g, 6, 3, Wm("WIDE TEXT"), AsciiWatermarkStyle.Block));
        Assert.Null(ex);
        Assert.All(g, c => Assert.True(c.Glyph == '#' || c.Glyph == ' '));
    }

    [Fact]
    public void Stamp_PlainLabel_WritesActualTextGlyphs()
    {
        var g = BlankGrid(80, 10);
        AsciiWatermark.Stamp(g, 80, 10, Wm("ZOOM", "FF"), AsciiWatermarkStyle.PlainLabel);
        // The literal characters (not '#' ink) appear for the plain style.
        var glyphs = new string(g.Where(c => c.Glyph != ' ').Select(c => c.Glyph).ToArray());
        Assert.Contains('Z', glyphs);
        Assert.Contains('M', glyphs);
    }

    // ── overlay (string-export path, #241 follow-up) ───────────────────

    [Fact]
    public void BuildOverlay_HasInk_AndMatchesStamp()
    {
        var wm = Wm("ABC", r: 12, gc: 34, b: 56);
        var overlay = AsciiWatermark.BuildOverlay(80, 30, wm, AsciiWatermarkStyle.Block);
        Assert.True(overlay.HasInk);

        // Every inked cell in the reference stamp must be reported by the overlay
        // with the same glyph and resolved colour (they share PlaceInk).
        var grid = BlankGrid(80, 30);
        AsciiWatermark.Stamp(grid, 80, 30, wm, AsciiWatermarkStyle.Block);
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 80; x++)
            {
                var cell = grid[y * 80 + x];
                bool got = overlay.TryGet(x, y, out var ink);
                if (cell.Glyph == ' ') { Assert.False(got); continue; }
                Assert.True(got);
                Assert.Equal(cell.Glyph, ink.Glyph);
                Assert.Equal((12, 34, 56), (ink.R, ink.G, ink.B));
            }
    }

    [Fact]
    public void BuildOverlay_EmptyText_HasNoInk()
    {
        var overlay = AsciiWatermark.BuildOverlay(40, 12, Wm("", ""), AsciiWatermarkStyle.Block);
        Assert.False(overlay.HasInk);
        Assert.False(overlay.TryGet(0, 0, out _));
    }

    [Fact]
    public void BuildOverlay_TryGet_OutOfRange_False()
    {
        var overlay = AsciiWatermark.BuildOverlay(40, 12, Wm("HI"), AsciiWatermarkStyle.Block);
        Assert.False(overlay.TryGet(-1, 0, out _));
        Assert.False(overlay.TryGet(0, -1, out _));
        Assert.False(overlay.TryGet(40, 0, out _));
        Assert.False(overlay.TryGet(0, 12, out _));
    }
}
