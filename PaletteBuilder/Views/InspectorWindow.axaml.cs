// Views/InspectorWindow.axaml.cs
//
// Modal inspector for the currently-selected palette. Three tabs:
//   Names         — every swatch with its nearest CSS / X11 colour name.
//   WCAG Contrast — N×N pair matrix with ratio + AA/AAA badge per cell.
//   Color Blindness — original + Protanopia + Deuteranopia + Tritanopia
//                     simulated strips for side-by-side comparison.
//
// Content is built programmatically — XAML stays as just the host TabControl
// because the per-swatch rows / cells are data-driven.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FracturingFog.Imaging.PaletteExtraction;
using PaletteBuilder.Services;

namespace PaletteBuilder.Views;

public sealed partial class InspectorWindow : Window
{
    public InspectorWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void Populate(IReadOnlyList<(byte R, byte G, byte B)> swatches)
    {
        var namesTab = this.FindControl<TabItem>("NamesTab");
        var contrastTab = this.FindControl<TabItem>("ContrastTab");
        var cvdTab = this.FindControl<TabItem>("CvdTab");

        if (namesTab is not null) namesTab.Content = BuildNamesView(swatches);
        if (contrastTab is not null) contrastTab.Content = BuildContrastView(swatches);
        if (cvdTab is not null) cvdTab.Content = BuildCvdView(swatches);
    }

    // ── Names tab ──────────────────────────────────────────────────────

    private Control BuildNamesView(IReadOnlyList<(byte R, byte G, byte B)> swatches)
    {
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var sp = new StackPanel { Margin = new Thickness(8), Spacing = 4 };
        foreach (var c in swatches)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("60,180,140,*"), Height = 30 };
            row.Children.Add(WithColumn(SwatchBox(c, 56), 0));
            row.Children.Add(WithColumn(Label($"#{c.R:X2}{c.G:X2}{c.B:X2}", bold: true), 1));
            row.Children.Add(WithColumn(Label($"RGB({c.R}, {c.G}, {c.B})"), 2));
            row.Children.Add(WithColumn(Label(ColorNamer.Nearest(c.R, c.G, c.B)), 3));
            AttachCopyFlyout(row, c);
            sp.Children.Add(row);
        }
        sv.Content = sp;
        return sv;
    }

    private void AttachCopyFlyout(Control host, (byte R, byte G, byte B) c)
    {
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        string rgb = $"rgb({c.R}, {c.G}, {c.B})";

        ColorSpaces.RgbToHsl(c.R, c.G, c.B, out float h, out float s, out float l);
        string hsl = $"hsl({h:0}, {s * 100:0}%, {l * 100:0}%)";

        var flyout = new MenuFlyout();
        flyout.Items.Add(MakeCopyItem("Copy HEX", hex));
        flyout.Items.Add(MakeCopyItem("Copy RGB", rgb));
        flyout.Items.Add(MakeCopyItem("Copy HSL", hsl));
        flyout.Items.Add(MakeCopyItem("Copy name", ColorNamer.Nearest(c.R, c.G, c.B)));
        host.ContextFlyout = flyout;
    }

    private MenuItem MakeCopyItem(string header, string payload)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is null) return;
            try { await top.Clipboard.SetTextAsync(payload); }
            catch { /* best-effort */ }
        };
        return mi;
    }

    // ── Contrast matrix ────────────────────────────────────────────────

    private static Control BuildContrastView(IReadOnlyList<(byte R, byte G, byte B)> swatches)
    {
        int n = swatches.Count;
        var grid = new Grid();
        for (int i = 0; i <= n; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (int i = 0; i < n; i++)
        {
            var top = SwatchBox(swatches[i], 36);
            Grid.SetRow(top, 0); Grid.SetColumn(top, i + 1);
            grid.Children.Add(top);

            var left = SwatchBox(swatches[i], 36);
            Grid.SetRow(left, i + 1); Grid.SetColumn(left, 0);
            grid.Children.Add(left);
        }

        for (int row = 0; row < n; row++)
        {
            for (int col = 0; col < n; col++)
            {
                var a = swatches[row]; var b = swatches[col];
                double ratio = WcagContrast.RatioBetween(a.R, a.G, a.B, b.R, b.G, b.B);
                var pass = WcagContrast.GradeRatio(ratio);

                var cell = new Border
                {
                    Width = 64, Height = 36,
                    Background = pass switch
                    {
                        WcagPass.AAA => Brush.Parse("#286440"),
                        WcagPass.AANormal => Brush.Parse("#28503C"),
                        WcagPass.AALarge => Brush.Parse("#4B4B28"),
                        _ => Brush.Parse("#503028"),
                    },
                    BorderBrush = Brush.Parse("#3C3C3C"),
                    BorderThickness = new Thickness(1),
                };
                var inner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                inner.Children.Add(new TextBlock
                {
                    Text = ratio.ToString("0.00") + ":1",
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                inner.Children.Add(new TextBlock
                {
                    Text = WcagContrast.FormatBadge(pass),
                    Foreground = Brushes.LightGray,
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                cell.Child = inner;
                Grid.SetRow(cell, row + 1); Grid.SetColumn(cell, col + 1);
                grid.Children.Add(cell);
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Padding = new Thickness(8), Child = grid },
        };
    }

    // ── CVD strips ─────────────────────────────────────────────────────

    private static Control BuildCvdView(IReadOnlyList<(byte R, byte G, byte B)> swatches)
    {
        var sp = new StackPanel { Margin = new Thickness(8), Spacing = 14 };
        sp.Children.Add(BuildCvdRow("Original", swatches, CvdKind.None));
        sp.Children.Add(BuildCvdRow("Protanopia", swatches, CvdKind.Protanopia));
        sp.Children.Add(BuildCvdRow("Deuteranopia", swatches, CvdKind.Deuteranopia));
        sp.Children.Add(BuildCvdRow("Tritanopia", swatches, CvdKind.Tritanopia));
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp };
    }

    private static Control BuildCvdRow(string label, IReadOnlyList<(byte R, byte G, byte B)> swatches, CvdKind kind)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush.Parse("#C8C864"),
            FontWeight = FontWeight.Bold,
        });
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Height = 48 };
        foreach (var c in swatches)
            strip.Children.Add(SwatchBox(CvdSimulator.Simulate(c.R, c.G, c.B, kind), 64));
        panel.Children.Add(strip);
        return panel;
    }

    // ── Tiny helpers ───────────────────────────────────────────────────

    private static Border SwatchBox((byte R, byte G, byte B) c, double height)
    {
        return new Border
        {
            Width = 64,
            Height = height,
            Background = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B)),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
        };
    }

    private static TextBlock Label(string text, bool bold = false) => new()
    {
        Text = text,
        Foreground = Brushes.White,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        FontFamily = bold ? new FontFamily("Consolas") : FontFamily.Default,
    };

    private static T WithColumn<T>(T ctrl, int col) where T : Control
    {
        Grid.SetColumn(ctrl, col);
        return ctrl;
    }
}
