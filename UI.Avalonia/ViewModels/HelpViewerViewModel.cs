using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// Tiny markdown viewer VM. Loads a documentation file from Avalonia
/// resources (embedded `Docs/*.md`), optionally slices it to the section
/// matching <c>anchor</c>, and runs the lightweight inline renderer to
/// produce a list of styled <see cref="Control"/>s the view drops into an
/// ItemsControl.
///
/// We do NOT bring in a markdown library — the docs use a small subset
/// (h1/h2/h3, fenced code blocks, bullet lists, paragraphs, inline `code`
/// spans). HelpMarkdownRenderer below handles exactly that subset; richer
/// constructs degrade to plain paragraphs.
/// </summary>
public sealed class HelpViewerViewModel : ViewModelBase
{
    public HelpViewerViewModel(string docId, string? anchor, string title)
    {
        _title = title;
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());

        string raw = LoadDocResource(docId);
        string? sliceTitle;
        string sliced = anchor != null
            ? SliceToSection(raw, anchor, out sliceTitle)
            : (sliceTitle = null, raw).raw;

        Blocks = new();
        foreach (var ctl in HelpMarkdownRenderer.Render(sliced))
            Blocks.Add(ctl);

        HeadingPath = string.IsNullOrEmpty(sliceTitle)
            ? docId
            : $"{docId}  →  {sliceTitle}";
    }

    private string _title;
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    private string _headingPath = string.Empty;
    public string HeadingPath { get => _headingPath; private set => this.RaiseAndSetIfChanged(ref _headingPath, value); }

    public System.Collections.ObjectModel.ObservableCollection<Control> Blocks { get; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public event Action? CloseRequested;

    private static string LoadDocResource(string docId)
    {
        var uri = new Uri($"avares://FracturingFog.UI.Avalonia/Docs/{docId}");
        try
        {
            using var stream = AssetLoader.Open(uri);
            using var sr = new StreamReader(stream);
            return sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"# Help unavailable\n\nCould not load `{docId}`.\n\n```\n{ex.Message}\n```";
        }
    }

    // Find the first heading line whose text contains `anchor` (case-insensitive)
    // and return the substring from that heading forward, plus the next sibling
    // heading at the same or shallower level. Falls back to the whole doc when
    // the anchor is not found.
    private static string SliceToSection(string source, string anchor, out string? matchedTitle)
    {
        matchedTitle = null;
        var lines = source.Replace("\r\n", "\n").Split('\n');
        int start = -1;
        int startLevel = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            int level = 0;
            while (level < l.Length && l[level] == '#') level++;
            if (level == 0 || level > 6) continue;
            string headText = l.Substring(level).Trim();
            if (headText.Contains(anchor, StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                startLevel = level;
                matchedTitle = headText;
                break;
            }
        }
        if (start < 0) return source;
        int end = lines.Length;
        for (int i = start + 1; i < lines.Length; i++)
        {
            var l = lines[i];
            int level = 0;
            while (level < l.Length && l[level] == '#') level++;
            if (level >= 1 && level <= startLevel) { end = i; break; }
        }
        return string.Join('\n', lines[start..end]);
    }
}

/// <summary>
/// Minimal markdown-to-Avalonia-controls renderer. Recognises:
///   • '# ', '## ', '### '       → headings
///   • '```'                      → fenced code block
///   • lines starting with `-`, `*` → bullet list
///   • pipe tables                → Grid (header row + body rows)
///   • blank line                 → paragraph break
///   • inline `code`              → monospace foreground swap
/// Links and other constructs render as plain text — the docs use them
/// sparingly enough that losing the formatting is acceptable for an
/// in-app contextual viewer.
/// </summary>
internal static class HelpMarkdownRenderer
{
    public static IEnumerable<Control> Render(string md)
    {
        var rawLines = md.Replace("\r\n", "\n").Split('\n');
        // Materialise to a list so the table-detection lookahead can peek.
        var lines = new List<string>(rawLines);
        var paragraph = new System.Text.StringBuilder();
        bool inCode = false;
        var codeBuf = new System.Text.StringBuilder();

        Control? FlushParagraph()
        {
            if (paragraph.Length == 0) return null;
            string text = paragraph.ToString().Trim();
            paragraph.Clear();
            return BuildInlineText(text, FontWeight.Normal, 14, "#E0E0E0");
        }

        for (int idx = 0; idx < lines.Count; idx++)
        {
            string line = lines[idx];
            if (inCode)
            {
                if (line.TrimStart().StartsWith("```"))
                {
                    var p = FlushParagraph(); if (p != null) yield return p;
                    yield return BuildCodeBlock(codeBuf.ToString());
                    codeBuf.Clear();
                    inCode = false;
                }
                else
                {
                    codeBuf.AppendLine(line);
                }
                continue;
            }

            if (line.TrimStart().StartsWith("```"))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                inCode = true;
                continue;
            }

            if (line.StartsWith("# "))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                yield return BuildHeading(line[2..], 22, FontWeight.Bold, "#FFFFFF", topPad: 10);
                continue;
            }
            if (line.StartsWith("## "))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                yield return BuildHeading(line[3..], 18, FontWeight.SemiBold, "#FFFFFF", topPad: 8);
                continue;
            }
            if (line.StartsWith("### "))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                yield return BuildHeading(line[4..], 16, FontWeight.SemiBold, "#D4D4D4", topPad: 6);
                continue;
            }
            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                yield return BuildBullet(line[2..]);
                continue;
            }
            // Pipe-table detection. Header row + divider + body rows. The
            // divider must contain only `|`, `-`, `:`, and whitespace. If
            // any of those checks fail the lines fall through to the
            // paragraph buffer as before — markdown inside running prose
            // that happens to contain `|` is not misread as a table.
            if (LooksLikeTable(lines, idx, out int tableEnd, out var tableRows))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                yield return BuildTable(tableRows);
                idx = tableEnd;
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                continue;
            }
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(line);
        }
        var tail = FlushParagraph(); if (tail != null) yield return tail;
        if (inCode && codeBuf.Length > 0) yield return BuildCodeBlock(codeBuf.ToString());
    }

    private static bool LooksLikeTable(List<string> lines, int start,
        out int endIndex, out List<string[]> rows)
    {
        endIndex = start;
        rows = new List<string[]>();
        string header = lines[start].Trim();
        if (!header.StartsWith("|") || start + 1 >= lines.Count) return false;
        string divider = lines[start + 1].Trim();
        if (!divider.StartsWith("|")) return false;
        // Divider cells must be empty or a dash-run with optional colons.
        foreach (var cell in SplitRow(divider))
        {
            string t = cell.Trim();
            if (t.Length == 0) continue;
            bool ok = true;
            foreach (char ch in t)
                if (ch != '-' && ch != ':') { ok = false; break; }
            if (!ok) return false;
        }
        rows.Add(SplitRow(header));
        int i = start + 2;
        while (i < lines.Count)
        {
            string t = lines[i].Trim();
            if (!t.StartsWith("|")) break;
            rows.Add(SplitRow(t));
            i++;
        }
        endIndex = i - 1;
        return rows.Count >= 1;
    }

    private static string[] SplitRow(string row)
    {
        // Strip leading/trailing pipe so split doesn't yield empty edges.
        string inner = row.Trim();
        if (inner.StartsWith("|")) inner = inner.Substring(1);
        if (inner.EndsWith("|") && (inner.Length < 2 || inner[inner.Length - 2] != '\\'))
            inner = inner.Substring(0, inner.Length - 1);
        // Walk character by character so we can:
        //   • respect `\|` escapes (literal pipe inside a cell — used in
        //     the grammar table's "(|zr|, |zi|)" and "\|z\|" notes),
        //   • respect backtick code spans (literal pipe inside `code`
        //     should never be treated as a column separator).
        // After collection we strip the escape backslash so the rendered
        // cell shows a plain `|`.
        var parts = new List<string>();
        var buf = new System.Text.StringBuilder();
        bool inCodeSpan = false;
        for (int i = 0; i < inner.Length; i++)
        {
            char ch = inner[i];
            if (ch == '`') { inCodeSpan = !inCodeSpan; buf.Append(ch); continue; }
            if (ch == '\\' && i + 1 < inner.Length && inner[i + 1] == '|')
            {
                buf.Append('|');
                i++;
                continue;
            }
            if (ch == '|' && !inCodeSpan)
            {
                parts.Add(buf.ToString().Trim());
                buf.Clear();
                continue;
            }
            buf.Append(ch);
        }
        parts.Add(buf.ToString().Trim());
        return parts.ToArray();
    }

    private static Control BuildTable(List<string[]> rows)
    {
        int cols = 0;
        foreach (var r in rows) if (r.Length > cols) cols = r.Length;
        var grid = new Grid
        {
            Margin = new Thickness(0, 6, 0, 8),
        };
        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        // Final column stretches so wide cells (notes column) wrap.
        if (cols > 0)
            grid.ColumnDefinitions[cols - 1] = new ColumnDefinition(GridLength.Star);
        for (int r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int r = 0; r < rows.Count; r++)
        {
            bool isHeader = r == 0;
            string bg = isHeader ? "#2A2A2A" : (r % 2 == 0 ? "#222222" : "#1C1C1C");
            for (int c = 0; c < cols; c++)
            {
                string text = c < rows[r].Length ? rows[r][c] : "";
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.Parse(bg)),
                    BorderBrush = new SolidColorBrush(Color.Parse("#404040")),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4),
                    Child = BuildInlineText(
                        text,
                        isHeader ? FontWeight.SemiBold : FontWeight.Normal,
                        13,
                        isHeader ? "#FFFFFF" : "#E0E0E0"),
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        // Outer frame so the table reads as a single block.
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#404040")),
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 6, 0, 8),
            Child = grid,
        };
    }

    private static Control BuildHeading(string text, double size, FontWeight weight, string colorHex, int topPad)
    {
        return new TextBlock
        {
            Text = text.Trim(),
            FontSize = size,
            FontWeight = weight,
            Foreground = new SolidColorBrush(Color.Parse(colorHex)),
            Margin = new Thickness(0, topPad, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static Control BuildCodeBlock(string code)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#101010")),
            BorderBrush = new SolidColorBrush(Color.Parse("#404040")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 4, 0, 4),
            Child = new SelectableTextBlock
            {
                Text = code.TrimEnd(),
                FontFamily = new FontFamily("Consolas, monospace"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#9CDCFE")),
                TextWrapping = TextWrapping.NoWrap,
            },
        };
    }

    private static Control BuildBullet(string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(new TextBlock
        {
            Text = "•",
            Foreground = new SolidColorBrush(Color.Parse("#B4B4B4")),
            Margin = new Thickness(2, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        });
        Grid.SetColumn(grid.Children[^1], 0);
        var inline = BuildInlineText(text.Trim(), FontWeight.Normal, 14, "#E0E0E0");
        Grid.SetColumn(inline, 1);
        grid.Children.Add(inline);
        return grid;
    }

    // Inline-code spans (`literal`) render as monospace inside the wrapping
    // paragraph. Bold / italic markdown is rare in these docs and is treated
    // as plain text. Pipe-tables and links also pass through verbatim.
    private static Control BuildInlineText(string text, FontWeight weight, double size, string colorHex)
    {
        var tb = new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = new SolidColorBrush(Color.Parse(colorHex)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 1),
        };
        int i = 0;
        var buf = new System.Text.StringBuilder();
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                if (buf.Length > 0)
                {
                    tb.Inlines!.Add(new global::Avalonia.Controls.Documents.Run(buf.ToString()));
                    buf.Clear();
                }
                int end = text.IndexOf('`', i + 1);
                if (end < 0) { buf.Append(text[i..]); break; }
                string code = text.Substring(i + 1, end - i - 1);
                tb.Inlines!.Add(new global::Avalonia.Controls.Documents.Run(code)
                {
                    FontFamily = new FontFamily("Consolas, monospace"),
                    Foreground = new SolidColorBrush(Color.Parse("#9CDCFE")),
                });
                i = end + 1;
                continue;
            }
            buf.Append(text[i++]);
        }
        if (buf.Length > 0)
            tb.Inlines!.Add(new global::Avalonia.Controls.Documents.Run(buf.ToString()));
        return tb;
    }
}
