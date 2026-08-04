// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;

using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>Unit coverage for the ASCII animation recorder (#230): frame
/// accumulation guards and the three serialized containers (asciinema cast,
/// animated SVG, raw ANSI sequence).</summary>
public sealed class AsciiAnimationRecorderTests
{
    private static AsciiCell[] SolidGrid(int cols, int rows, char glyph, byte r, byte g, byte b)
    {
        var cells = new AsciiCell[cols * rows];
        for (int i = 0; i < cells.Length; i++) cells[i] = new AsciiCell(glyph, r, g, b);
        return cells;
    }

    private static AsciiAnimationRecorder Recorder(int frames, int cols = 4, int rows = 3)
    {
        var rec = new AsciiAnimationRecorder();
        for (int f = 0; f < frames; f++)
        {
            // Vary glyph + colour per frame so "frames differ" checks are real.
            char g = (f % 2 == 0) ? '#' : '.';
            rec.AddFrame(SolidGrid(cols, rows, g, (byte)(10 + f * 5), 100, 200), cols, rows, 0.1);
        }
        return rec;
    }

    [Fact]
    public void AddFrame_TracksGeometryCountAndDuration()
    {
        var rec = Recorder(5, cols: 6, rows: 2);
        Assert.Equal(5, rec.FrameCount);
        Assert.Equal(6, rec.Cols);
        Assert.Equal(2, rec.Rows);
        Assert.Equal(0.5, rec.TotalSeconds, 3);
    }

    [Fact]
    public void AddFrame_RejectsMismatchedGridSize()
    {
        var rec = new AsciiAnimationRecorder();
        rec.AddFrame(SolidGrid(4, 3, '#', 1, 2, 3), 4, 3, 0.1);
        Assert.Throws<ArgumentException>(() =>
            rec.AddFrame(SolidGrid(5, 3, '#', 1, 2, 3), 5, 3, 0.1));
    }

    [Fact]
    public void AddFrame_CopiesGrid_ProducerReuseIsSafe()
    {
        var rec = new AsciiAnimationRecorder();
        var buf = SolidGrid(2, 1, '#', 200, 0, 0);
        rec.AddFrame(buf, 2, 1, 0.1);
        // Producer mutates its buffer for the next frame; the recorded frame must
        // keep the original glyph.
        buf[0] = new AsciiCell(' ', 0, 0, 0);
        rec.AddFrame(buf, 2, 1, 0.1);
        string cast = rec.Serialize(AsciiAnimationFormat.AsciinemaCast);
        Assert.Contains("38;2;200;0;0", cast); // frame 0's red survived
    }

    [Fact]
    public void Serialize_EmptyRecorder_Throws()
    {
        var rec = new AsciiAnimationRecorder();
        Assert.Throws<InvalidOperationException>(() =>
            rec.Serialize(AsciiAnimationFormat.AsciinemaCast));
    }

    [Fact]
    public void Cast_HasHeaderAndOneEventPerFrame()
    {
        var rec = Recorder(4);
        string cast = rec.Serialize(AsciiAnimationFormat.AsciinemaCast);
        Assert.StartsWith("{\"version\":2", cast);
        Assert.Contains("\"width\":4", cast);
        Assert.Contains("\"height\":3", cast);
        int events = CountOccurrences(cast, ", \"o\", \"");
        Assert.Equal(4, events);
        // ESC bytes are JSON-escaped, never raw.
        Assert.DoesNotContain('\x1b', cast);
        Assert.Contains("\\u001b", cast);
    }

    [Fact]
    public void Cast_EventTimesAreMonotonicFromZero()
    {
        var rec = Recorder(3);
        string cast = rec.Serialize(AsciiAnimationFormat.AsciinemaCast);
        // First event at t=0.000.
        Assert.Contains("[0.000, \"o\", \"", cast);
        // Later events at 0.100 and 0.200.
        Assert.Contains("[0.100, \"o\", \"", cast);
        Assert.Contains("[0.200, \"o\", \"", cast);
    }

    [Fact]
    public void Svg_IsWellFormedWithOneAnimatePerFrame()
    {
        var rec = Recorder(5);
        string svg = rec.Serialize(AsciiAnimationFormat.AnimatedSvg);
        Assert.StartsWith("<svg", svg);
        Assert.EndsWith("</svg>", svg.TrimEnd());
        Assert.Equal(5, CountOccurrences(svg, "<animate "));
        Assert.Contains("repeatCount=\"indefinite\"", svg);
        Assert.Contains("calcMode=\"discrete\"", svg);
    }

    [Fact]
    public void AnsiSequence_HasClearPerFrameAndColour()
    {
        var rec = Recorder(3);
        string ans = rec.Serialize(AsciiAnimationFormat.AnsiSequence);
        Assert.Equal(3, CountOccurrences(ans, "\x1b[2J"));
        Assert.Contains("\x1b[38;2;", ans);
        Assert.Contains("\x1b[0m", ans);
    }

    [Fact]
    public void WriteToFile_RoundTrips()
    {
        var rec = Recorder(3);
        string path = Path.Combine(Path.GetTempPath(), $"ffanim-{Guid.NewGuid():N}.cast");
        try
        {
            rec.WriteToFile(AsciiAnimationFormat.AsciinemaCast, path);
            Assert.True(File.Exists(path));
            string read = File.ReadAllText(path);
            Assert.StartsWith("{\"version\":2", read);
            Assert.Equal(3, CountOccurrences(read, ", \"o\", \""));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(AsciiAnimationFormat.AsciinemaCast, ".cast")]
    [InlineData(AsciiAnimationFormat.AnimatedSvg, ".svg")]
    [InlineData(AsciiAnimationFormat.AnsiSequence, ".ans")]
    public void ExtensionFor_MapsFormat(AsciiAnimationFormat fmt, string ext)
        => Assert.Equal(ext, AsciiAnimationRecorder.ExtensionFor(fmt));

    private static int CountOccurrences(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }
}
