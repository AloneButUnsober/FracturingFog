// Views/HelpDialog.axaml.cs
//
// Modal viewer for the embedded UserManual.md resource. Parses the
// markdown line-by-line into a flat StackPanel of styled TextBlocks —
// good enough for headers, code blocks, tables, and lists without
// pulling in a full markdown renderer.
//
// Supported syntax:
//   # / ## / ### → header sizes
//   ``` fenced ``` → monospace block w/ dim background
//   |table| rows → monospace, single fixed-width line each
//   - / * bullets → indented row w/ bullet glyph
//   ---  → thin separator border
//   blank → spacing
//   else → wrapped paragraph

using System;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;

namespace PaletteBuilder.Views;

public sealed partial class HelpDialog : Window
{
    private string _rawMarkdown = "";

    public HelpDialog()
    {
        AvaloniaXamlLoader.Load(this);
        LoadManual();
        this.FindControl<Button>("CloseBtn")!.Click += (_, _) => Close();
        this.FindControl<Button>("CopyBtn")!.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is null) return;
            try { await top.Clipboard.SetTextAsync(_rawMarkdown); } catch { }
        };
    }

    private void LoadManual()
    {
        var content = this.FindControl<StackPanel>("HelpContent")!;
        try
        {
            // Resource lives in PaletteBuilder.Lib.dll (AssemblyName=PaletteBuilder.Lib).
            using var stream = AssetLoader.Open(new Uri("avares://PaletteBuilder.Lib/Resources/UserManual.md"));
            using var reader = new StreamReader(stream);
            _rawMarkdown = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Failed to load manual: " + ex.Message,
                Foreground = Brush.Parse("#C86464"),
            });
            return;
        }

        Render(content, _rawMarkdown);
    }

    private static void Render(StackPanel host, string md)
    {
        bool inCode = false;
        var codeBuf = new StringBuilder();

        foreach (var rawLine in md.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.TrimEnd();

            if (line.StartsWith("```"))
            {
                if (inCode)
                {
                    host.Children.Add(CodeBlock(codeBuf.ToString()));
                    codeBuf.Clear();
                    inCode = false;
                }
                else
                {
                    inCode = true;
                }
                continue;
            }

            if (inCode)
            {
                codeBuf.AppendLine(line);
                continue;
            }

            if (string.IsNullOrEmpty(line))
            {
                host.Children.Add(new TextBlock { Height = 6 });
                continue;
            }

            if (line.StartsWith("# "))
            {
                host.Children.Add(HeaderBlock(line.Substring(2), 20, "#C8C864", true, 14));
                continue;
            }
            if (line.StartsWith("## "))
            {
                host.Children.Add(HeaderBlock(line.Substring(3), 16, "#A0C8A0", true, 12));
                continue;
            }
            if (line.StartsWith("### "))
            {
                host.Children.Add(HeaderBlock(line.Substring(4), 13, "#B4B4B4", true, 8));
                continue;
            }

            if (line == "---")
            {
                host.Children.Add(new Border
                {
                    BorderBrush = Brush.Parse("#3C3C3C"),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 6, 0, 6),
                });
                continue;
            }

            if (line.StartsWith("|"))
            {
                host.Children.Add(new TextBlock
                {
                    Text = line,
                    Foreground = Brush.Parse("#DCDCC8"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                });
                continue;
            }

            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                host.Children.Add(new TextBlock
                {
                    Text = "  • " + line.Substring(2),
                    Foreground = Brush.Parse("#DCDCDC"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                });
                continue;
            }

            // Plain paragraph
            host.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = Brush.Parse("#DCDCDC"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        }
    }

    private static TextBlock HeaderBlock(string text, double size, string color, bool bold, double topMargin)
        => new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            Foreground = Brush.Parse(color),
            Margin = new Thickness(0, topMargin, 0, 4),
        };

    private static Border CodeBlock(string text)
    {
        return new Border
        {
            Background = Brush.Parse("#101010"),
            BorderBrush = Brush.Parse("#3C3C3C"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 4),
            Child = new TextBlock
            {
                Text = text.TrimEnd('\n', '\r'),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = Brush.Parse("#C8E0C8"),
            },
        };
    }
}
