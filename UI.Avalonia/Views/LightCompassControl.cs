// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// LightCompassControl.cs
//
// In-editor directional-light compass for the Color Theme Editor. Draws a
// top-down (XZ-plane) projection of the theme's Key / Fill / Rim light rig so
// the user sees where each light points while dragging the X/Y/Z fields —
// the same convention as the on-canvas debug HUD compass
// (ScreenSpacePost.DrawLightCompass): screen X = world X, screen Y = -world Z,
// line length = the light's horizontal magnitude (a light pointing straight
// down the Y axis collapses to the centre dot).
//
// This is display-only: it reads the live LightSourceRowVm values off the
// bound ColorThemeEditorViewModel and repaints on its LightsChanged event. It
// never mutates theme state, so it stays out of the undo / dirty / preview
// pipeline entirely (issue #84 companion — "overlay works with the editor's
// lights" without touching the render path).

using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public sealed class LightCompassControl : Control
{
    /// <summary>The editor VM whose Key/Fill/Rim rows are visualised.</summary>
    public static readonly StyledProperty<ColorThemeEditorViewModel?> SourceProperty =
        AvaloniaProperty.Register<LightCompassControl, ColorThemeEditorViewModel?>(nameof(Source));

    public ColorThemeEditorViewModel? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private ColorThemeEditorViewModel? _hooked;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            if (_hooked != null) _hooked.LightsChanged -= OnLightsChanged;
            _hooked = Source;
            if (_hooked != null) _hooked.LightsChanged += OnLightsChanged;
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_hooked != null) { _hooked.LightsChanged -= OnLightsChanged; _hooked = null; }
    }

    private void OnLightsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) InvalidateVisual();
        else Dispatcher.UIThread.Post(InvalidateVisual);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        double w = Bounds.Width, h = Bounds.Height;
        if (w < 8 || h < 8) return;

        double cx = w * 0.5, cy = h * 0.5;
        double radius = Math.Min(w, h) * 0.5 - 10;
        if (radius < 4) return;

        // Backdrop + frame.
        var bg = new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0));
        ctx.DrawRectangle(bg, null, new RoundedRect(new Rect(0, 0, w, h), 6));

        var ringPen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xC8, 0xC8, 0xC8)), 1);
        var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xC8, 0xC8, 0xC8)), 1);
        var center = new Point(cx, cy);
        ctx.DrawEllipse(null, ringPen, center, radius, radius);
        // Crosshair.
        ctx.DrawLine(tickPen, new Point(cx - radius, cy), new Point(cx + radius, cy));
        ctx.DrawLine(tickPen, new Point(cx, cy - radius), new Point(cx, cy + radius));

        // Axis hints: +Z up, +X right (matches the on-canvas HUD projection).
        var axisBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xB4, 0xB4, 0xB4));
        DrawLabel(ctx, "+Z", cx - 6, cy - radius - 12, axisBrush);
        DrawLabel(ctx, "+X", cx + radius + 2, cy - 6, axisBrush);

        var vm = Source;
        if (vm == null) return;

        DrawLight(ctx, vm.KeyLight, "K", cx, cy, radius, true);
        DrawLight(ctx, vm.FillLight, "F", cx, cy, radius, true);
        DrawLight(ctx, vm.RimLight, "R", cx, cy, radius, vm.UseRim);
    }

    private static void DrawLight(
        DrawingContext ctx, LightSourceRowVm? row, string tag,
        double cx, double cy, double radius, bool active)
    {
        if (row == null || !active) return;

        double lx = row.Lx, ly = row.Ly, lz = row.Lz;
        double len = Math.Sqrt(lx * lx + ly * ly + lz * lz);
        if (len < 1e-6) return;
        // Normalise so the endpoint distance encodes only the horizontal tilt:
        // a near-vertical light lands close to centre, a grazing light at the rim.
        double nx = lx / len, nz = lz / len;
        double ex = cx + nx * radius;
        double ey = cy - nz * radius;

        var col = Color.FromRgb(row.DiffR, row.DiffG, row.DiffB);
        var brush = new SolidColorBrush(col);
        // Halo underlay keeps light lines legible against the disc + crosshair.
        var haloPen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 3.2);
        var linePen = new Pen(brush, 1.8);
        var p0 = new Point(cx, cy);
        var p1 = new Point(ex, ey);
        ctx.DrawLine(haloPen, p0, p1);
        ctx.DrawLine(linePen, p0, p1);

        // Endpoint dot + tag.
        ctx.DrawEllipse(brush, null, p1, 3.5, 3.5);
        DrawLabel(ctx, tag, ex + 4, ey - 7, brush);
    }

    private static void DrawLabel(DrawingContext ctx, string text, double x, double y, IBrush brush)
    {
        var ft = new FormattedText(
            text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 10, brush);
        ctx.DrawText(ft, new Point(x, y));
    }
}
