// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Views/HistogramControl.cs
//
// Tiny custom-painted RGB + luminance histogram of the preview bitmap.
// Reads pixel data from the Avalonia Bitmap exposed via DataContext.Preview.
// Recomputes on DataContext change; samples a downscaled subset for speed.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FracturingFog.UI.Avalonia.ViewModels;

namespace PaletteBuilder.Views;

public sealed class HistogramControl : Control
{
    private int[]? _r;
    private int[]? _g;
    private int[]? _b;
    private int[]? _y;
    private int _maxBin;

    public HistogramControl()
    {
        Height = 80;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Recompute();
        InvalidateVisual();
    }

    private void Recompute()
    {
        _r = _g = _b = _y = null;
        _maxBin = 0;

        if (DataContext is not ImagePaletteViewModel vm) return;
        if (vm.PreviewImage is not Bitmap bmp) return;

        int w = (int)bmp.Size.Width;
        int h = (int)bmp.Size.Height;
        if (w <= 0 || h <= 0) return;

        var fmt = PixelFormat.Bgra8888;
        try
        {
            int sw = Math.Min(w, 256);
            int sh = Math.Min(h, 256);
            using var scaled = bmp.CreateScaledBitmap(new PixelSize(sw, sh));
            byte[] buf = new byte[sw * sh * 4];
            unsafe
            {
                fixed (byte* p = buf)
                    scaled.CopyPixels(new PixelRect(0, 0, sw, sh), (IntPtr)p, buf.Length, sw * 4);
            }

            _r = new int[256]; _g = new int[256]; _b = new int[256]; _y = new int[256];
            for (int i = 0; i < buf.Length; i += 4)
            {
                byte b = buf[i], gr = buf[i + 1], r = buf[i + 2];
                _r[r]++; _g[gr]++; _b[b]++;
                int lum = (int)(0.2126 * r + 0.7152 * gr + 0.0722 * b);
                if (lum > 255) lum = 255;
                _y[lum]++;
            }
            for (int i = 0; i < 256; i++)
            {
                if (_r[i] > _maxBin) _maxBin = _r[i];
                if (_g[i] > _maxBin) _maxBin = _g[i];
                if (_b[i] > _maxBin) _maxBin = _b[i];
                if (_y[i] > _maxBin) _maxBin = _y[i];
            }
        }
        catch
        {
            _r = _g = _b = _y = null;
            _maxBin = 0;
        }
    }

    public override void Render(DrawingContext g)
    {
        base.Render(g);
        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        g.FillRectangle(new SolidColorBrush(Color.FromArgb(255, 20, 20, 20)), rect);

        if (_r is null || _maxBin == 0)
        {
            g.DrawText(new FormattedText("(no image)", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 11, new SolidColorBrush(Color.FromArgb(255, 160, 160, 160))),
                new Point(8, Bounds.Height / 2 - 6));
            return;
        }

        DrawChannel(g, _r!, Color.FromArgb(140, 220, 80, 80));
        DrawChannel(g, _g!, Color.FromArgb(140, 80, 220, 80));
        DrawChannel(g, _b!, Color.FromArgb(140, 80, 120, 220));
        DrawChannel(g, _y!, Color.FromArgb(180, 200, 200, 200), outline: true);
    }

    private void DrawChannel(DrawingContext g, int[] bins, Color color, bool outline = false)
    {
        var brush = new SolidColorBrush(color);
        double barW = Bounds.Width / 256.0;
        for (int i = 0; i < 256; i++)
        {
            double h = bins[i] / (double)_maxBin * Bounds.Height;
            if (h < 1) continue;
            var r = new Rect(i * barW, Bounds.Height - h, Math.Max(1, barW), h);
            g.FillRectangle(brush, r);
            if (outline)
                g.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(220, 240, 240, 240)), 0.5), r);
        }
    }
}
