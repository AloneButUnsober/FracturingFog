// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Controls/AsciiFrameRasterizer.cs
//
// Renders an AsciiFrame (glyph + colour grid) to a BGRA pixel buffer for the
// ASCII → MP4 exporter (#230). Uses the same monospace typeface and per-colour-
// run drawing as AsciiView, but onto an offscreen RenderTargetBitmap so each
// recorded frame becomes a picture the ffmpeg pipeline can encode.
//
// UI-thread only: FormattedText metrics and RenderTargetBitmap both require the
// Avalonia render stack.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using FracturingFog.Render;

namespace FracturingFog.UI.Avalonia.Controls;

/// <summary>Rasterises <see cref="AsciiFrame"/>s to BGRA buffers (0xAARRGGBB,
/// the engine's ColorBuffer layout) at a fixed monospace metric.</summary>
public sealed class AsciiFrameRasterizer
{
    private static readonly FontFamily MonoFamily =
        new("Consolas,Cascadia Mono,DejaVu Sans Mono,Menlo,monospace");

    private readonly Typeface _typeface = new(MonoFamily);
    private readonly double _fontSize;
    private double _advance;
    private double _lineHeight;

    public AsciiFrameRasterizer(double fontSize = 14.0)
    {
        _fontSize = fontSize;
        var probe = new FormattedText(
            "M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, _fontSize, Brushes.White);
        _advance = probe.Width > 0 ? probe.Width : _fontSize * 0.6;
        _lineHeight = probe.Height > 0 ? probe.Height : _fontSize;
    }

    /// <summary>Cell height ÷ width — pass to the recorder so its downsampled
    /// grid matches this font's shape.</summary>
    public double CellAspect => _advance > 0 ? _lineHeight / _advance : 2.0;

    /// <summary>Pixel size a grid of <paramref name="cols"/>×<paramref name="rows"/>
    /// rasterises to (even, ≥2 — ffmpeg requires even dimensions).</summary>
    public PixelSize PixelSizeFor(int cols, int rows)
    {
        int w = Math.Max(2, (int)Math.Ceiling(cols * _advance));
        int h = Math.Max(2, (int)Math.Ceiling(rows * _lineHeight));
        if ((w & 1) == 1) w++;
        if ((h & 1) == 1) h++;
        return new PixelSize(w, h);
    }

    /// <summary>Draw <paramref name="f"/> onto a fresh black canvas and return the
    /// BGRA pixels plus dimensions. Dimensions are stable for a given grid size.</summary>
    public (uint[] bgra, int width, int height) Rasterize(AsciiFrame f)
    {
        var size = PixelSizeFor(f.Cols, f.Rows);
        int w = size.Width, h = size.Height;

        var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, w, h));
            if (!f.IsEmpty)
            {
                var sb = new StringBuilder(f.Cols);
                for (int r = 0; r < f.Rows; r++)
                {
                    int baseI = r * f.Cols;
                    double y = r * _lineHeight;
                    int c = 0;
                    while (c < f.Cols)
                    {
                        uint col = f.HasColor ? f.Colors[baseI + c] : 0xDCDCDCu;
                        int start = c;
                        sb.Clear();
                        bool allSpace = true;
                        while (c < f.Cols && (f.HasColor ? f.Colors[baseI + c] : 0xDCDCDCu) == col)
                        {
                            char g = f.Glyphs[baseI + c];
                            if (g != ' ') allSpace = false;
                            sb.Append(g);
                            c++;
                        }
                        if (allSpace) continue;

                        var brush = new SolidColorBrush(Color.FromRgb(
                            (byte)((col >> 16) & 0xFF), (byte)((col >> 8) & 0xFF), (byte)(col & 0xFF)));
                        var ft = new FormattedText(
                            sb.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            _typeface, _fontSize, brush);
                        ctx.DrawText(ft, new Point(start * _advance, y));
                    }
                }
            }
        }

        // Copy Bgra8888 pixels out. Byte order B,G,R,A → little-endian uint =
        // 0xAARRGGBB, matching the engine's ColorBuffer layout.
        var bgra = new uint[w * h];
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), w * h * 4, w * 4);
        }
        finally { handle.Free(); }
        return (bgra, w, h);
    }
}
