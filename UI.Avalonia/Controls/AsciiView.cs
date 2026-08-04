// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Controls/AsciiView.cs
//
// Live ASCII / text-art display control (#227 / #228 Terminal Mode). Paints an
// AsciiFrame (a downsampled character grid produced by the render host from the
// real IColorMap-coloured frame) onto a DrawingContext with a monospace font.
//
// Display-only: IsHitTestVisible is left false by the host so the transparent
// InputSponge above it keeps driving the real render underneath. The paint is
// batched per row into runs of equal colour (a per-cell DrawText would be far
// too slow at ~160×80), and all-space runs are skipped entirely.
//
// Threading: Update(...) must be called on the UI thread (it invalidates the
// visual). The background render thread reads only LiveColumns (volatile) and
// CellAspect (constant after metrics) to decide how many columns to request —
// it never touches Bounds or other UI-thread state.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using FracturingFog.Render;

namespace FracturingFog.UI.Avalonia.Controls;

public sealed class AsciiView : Control
{
    private static readonly FontFamily MonoFamily =
        new("Consolas,Cascadia Mono,DejaVu Sans Mono,Menlo,monospace");

    private readonly Typeface _typeface = new(MonoFamily);
    private readonly double _fontSize = 14.0;
    private double _advance;    // monospace glyph advance (px)
    private double _lineHeight; // line box height (px)

    private AsciiFrame _frame;
    private readonly Dictionary<uint, IBrush> _brushCache = new();
    private readonly IBrush _monoInk = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));

    /// <summary>Columns that fit the current width at the monospace advance.
    /// Volatile so the background render thread can read it without touching
    /// UI-thread state. Updated whenever the control's bounds change.</summary>
    public volatile int LiveColumns = 80;

    /// <summary>Raised (UI thread) when a bounds change alters the fitted column
    /// count. The stored grid is a fixed Cols×Rows produced for the previous
    /// width — without a re-pull a resize would only re-centre / letterbox the
    /// old grid, never re-render at the new resolution. The host re-pumps a
    /// freshly-sized frame in response.</summary>
    public event EventHandler? LiveColumnsChanged;

    public AsciiView()
    {
        EnsureMetrics();
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    /// <summary>Cell height ÷ width of the display font — passed to the host so
    /// the downsampled grid keeps the frame's shape.</summary>
    public double CellAspect => _advance > 0 ? _lineHeight / _advance : 2.0;

    /// <summary>Replace the displayed grid and repaint. UI thread only.</summary>
    public void Update(AsciiFrame frame)
    {
        _frame = frame;
        InvalidateVisual();
    }

    /// <summary>Clear the grid (e.g. on leaving Terminal Mode).</summary>
    public void Clear()
    {
        _frame = default;
        InvalidateVisual();
    }

    private void EnsureMetrics()
    {
        if (_advance > 0) return;
        var probe = new FormattedText(
            "M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, _fontSize, Brushes.White);
        _advance = probe.Width > 0 ? probe.Width : _fontSize * 0.6;
        _lineHeight = probe.Height > 0 ? probe.Height : _fontSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            EnsureMetrics();
            int cols = _advance > 0 ? (int)(Bounds.Width / _advance) : 80;
            cols = Math.Clamp(cols, 20, 400);
            if (cols != LiveColumns)
            {
                LiveColumns = cols;
                LiveColumnsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private IBrush BrushFor(uint rgb)
    {
        if (_brushCache.TryGetValue(rgb, out var b)) return b;
        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));
        // Bound the cache so a long session over many palettes can't grow it
        // without limit; truecolor frames rarely exceed a few hundred distinct
        // colours per view, so a periodic flush is cheap and invisible.
        if (_brushCache.Count > 4096) _brushCache.Clear();
        _brushCache[rgb] = brush;
        return brush;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

        var f = _frame;
        if (f.IsEmpty) return;
        EnsureMetrics();

        double gridW = f.Cols * _advance;
        double gridH = f.Rows * _lineHeight;
        double ox = Math.Max(0, (Bounds.Width - gridW) * 0.5);
        double oy = Math.Max(0, (Bounds.Height - gridH) * 0.5);

        var sb = new StringBuilder(f.Cols);
        for (int r = 0; r < f.Rows; r++)
        {
            int baseI = r * f.Cols;
            double y = oy + r * _lineHeight;
            int c = 0;
            while (c < f.Cols)
            {
                uint col = f.HasColor ? f.Colors[baseI + c] : 0u;
                int start = c;
                sb.Clear();
                bool allSpace = true;
                while (c < f.Cols && (f.HasColor ? f.Colors[baseI + c] : 0u) == col)
                {
                    char g = f.Glyphs[baseI + c];
                    if (g != ' ') allSpace = false;
                    sb.Append(g);
                    c++;
                }
                if (allSpace) continue; // background — leave the black fill

                IBrush brush = f.HasColor ? BrushFor(col) : _monoInk;
                var ft = new FormattedText(
                    sb.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    _typeface, _fontSize, brush);
                context.DrawText(ft, new Point(ox + start * _advance, y));
            }
        }
    }
}
