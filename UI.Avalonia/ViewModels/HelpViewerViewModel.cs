// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
        _docId = docId;
        _title = title;
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());

        string raw = LoadDocResource(docId);
        string? sliceTitle;
        string sliced = anchor != null
            ? SliceToSection(raw, anchor, out sliceTitle)
            : (sliceTitle = null, raw).raw;

        Blocks = new();
        // Image refs in markdown resolve against the doc's folder so a User/
        // guide can `![](../Images/foo.png)` the shared image bin.
        Bitmap? Resolver(string href)
        {
            var uri = TryResolveImage(docId, href);
            if (uri == null) return null;
            try
            {
                using var s = AssetLoader.Open(uri);
                return new Bitmap(s);
            }
            catch { return null; }
        }
        foreach (var ctl in HelpMarkdownRenderer.Render(sliced, Resolver))
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
        // docId is the relative path inside `Docs/` — e.g. "User/Capture-Guide.md".
        // Legacy callers may still pass the bare filename (pre-restructure); fall
        // back to a User/ or Technical/ prefix before erroring out.
        var attempts = new List<string> { docId };
        if (!docId.Contains('/') && !docId.Contains('\\'))
        {
            attempts.Add($"User/{docId}");
            attempts.Add($"Technical/{docId}");
        }
        Exception? lastError = null;
        foreach (var rel in attempts)
        {
            var uri = new Uri($"avares://FracturingFog.UI.Avalonia/Docs/{rel}");
            try
            {
                using var stream = AssetLoader.Open(uri);
                using var sr = new StreamReader(stream);
                return sr.ReadToEnd();
            }
            catch (Exception ex) { lastError = ex; }
        }
        return $"# Help unavailable\n\nCould not load `{docId}`.\n\n```\n{lastError?.Message}\n```";
    }

    /// <summary>
    /// Resolves an `![](path)` image reference against `docId`'s folder. Paths
    /// beginning with `/Images/`, `Images/`, or `../Images/` are normalised to
    /// the embedded resource path `Docs/Images/...`.
    /// </summary>
    internal static Uri? TryResolveImage(string docId, string imageRef)
    {
        string norm = imageRef.Replace('\\', '/');
        if (norm.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            return new Uri(norm);

        // Walk relative segments against the docId folder.
        var docDir = docId.Replace('\\', '/');
        int slash = docDir.LastIndexOf('/');
        docDir = slash >= 0 ? docDir[..slash] : "";
        var parts = new List<string>(docDir.Length > 0 ? docDir.Split('/') : Array.Empty<string>());
        foreach (var seg in norm.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        var rel = string.Join('/', parts);
        return new Uri($"avares://FracturingFog.UI.Avalonia/Docs/{rel}");
    }

    private readonly string _docId;

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
///   • '# ', '## ', '### ', '#### '  → headings
///   • '```'                          → fenced code block
///   • lines starting with `-`, `*`   → bullet list
///   • lines starting with `1. `      → ordered list (kept as bullet)
///   • '![alt](path)' line             → embedded image
///   • '> '                           → blockquote / callout
///   • '---'                          → horizontal rule
///   • pipe tables                    → Grid (header row + body rows)
///   • blank line                     → paragraph break
///   • inline `code`                  → monospace foreground swap
///   • inline **bold** / *italic*     → run styling
///   • inline `$math$` and `$$math$$` → LaTeX rendered as code-styled spans
///   • inline `[label](url)`          → clickable link
///   • inline `![alt](path)`          → inline image
/// Selectable text is preferred everywhere so users can copy any region —
/// this was the #1 papercut on the previous TextBlock-only renderer.
/// LaTeX source is preserved verbatim so users can paste into KaTeX or
/// Mathematica; the static HTML site export uses KaTeX to render it live.
/// </summary>
internal static class HelpMarkdownRenderer
{
    public delegate Bitmap? ImageResolver(string href);

    public static IEnumerable<Control> Render(string md, ImageResolver? imageResolver = null)
    {
        var rawLines = md.Replace("\r\n", "\n").Split('\n');
        // Materialise to a list so the table-detection lookahead can peek.
        var lines = new List<string>(rawLines);
        var paragraph = new System.Text.StringBuilder();
        bool inCode = false;
        string codeLang = "";
        var codeBuf = new System.Text.StringBuilder();
        bool inMathBlock = false;
        var mathBuf = new System.Text.StringBuilder();

        Control? FlushParagraph()
        {
            if (paragraph.Length == 0) return null;
            string text = paragraph.ToString().Trim();
            paragraph.Clear();
            return BuildInlineText(text, FontWeight.Normal, 14, "#E0E0E0", imageResolver);
        }

        for (int idx = 0; idx < lines.Count; idx++)
        {
            string line = lines[idx];
            if (inCode)
            {
                if (line.TrimStart().StartsWith("```"))
                {
                    var p = FlushParagraph(); if (p != null) yield return p;
                    yield return BuildCodeBlock(codeBuf.ToString(), codeLang);
                    codeBuf.Clear();
                    codeLang = "";
                    inCode = false;
                }
                else
                {
                    codeBuf.AppendLine(line);
                }
                continue;
            }
            if (inMathBlock)
            {
                if (line.TrimEnd().EndsWith("$$"))
                {
                    var rest = line.TrimEnd();
                    rest = rest[..^2];
                    if (rest.Length > 0) mathBuf.AppendLine(rest);
                    var p = FlushParagraph(); if (p != null) yield return p;
                    yield return BuildMathBlock(mathBuf.ToString());
                    mathBuf.Clear();
                    inMathBlock = false;
                }
                else
                {
                    mathBuf.AppendLine(line);
                }
                continue;
            }

            if (line.TrimStart().StartsWith("```"))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                codeLang = line.TrimStart().TrimStart('`').Trim();
                inCode = true;
                continue;
            }

            // Fenced math block: '$$' alone, or `$$ … $$` on one line.
            var t = line.TrimStart();
            if (t.StartsWith("$$"))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                string after = t.Substring(2);
                if (after.TrimEnd().EndsWith("$$") && after.TrimEnd().Length >= 2)
                {
                    // Single-line $$ … $$
                    var inner = after.TrimEnd();
                    inner = inner[..^2];
                    yield return BuildMathBlock(inner);
                    continue;
                }
                if (after.Length > 0) mathBuf.AppendLine(after);
                inMathBlock = true;
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
            if (line.StartsWith("#### "))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                yield return BuildHeading(line[5..], 14, FontWeight.SemiBold, "#B4B4B4", topPad: 4);
                continue;
            }

            // Horizontal rule (`---` or `***` alone on the line).
            if (line.Trim() == "---" || line.Trim() == "***" || line.Trim() == "___")
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                yield return new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.Parse("#404040")),
                    Margin = new Thickness(0, 10, 0, 10),
                };
                continue;
            }

            // Standalone image line: ![alt](path)
            if (TryParseImage(line.Trim(), out var imgAlt, out var imgRef))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                yield return BuildImage(imgAlt, imgRef, imageResolver);
                continue;
            }

            // Block-quote / callout
            if (line.StartsWith("> "))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                yield return BuildQuote(line[2..], imageResolver);
                continue;
            }

            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                var p = FlushParagraph(); if (p != null) yield return p;
                yield return BuildBullet(line[2..], imageResolver);
                continue;
            }
            // Ordered list (limited: numeric prefix `N. `). Rendered as bullet
            // with the digit kept so the reader can still see ordering.
            if (line.Length > 2 && char.IsDigit(line[0]))
            {
                int i = 1;
                while (i < line.Length && char.IsDigit(line[i])) i++;
                if (i < line.Length - 1 && line[i] == '.' && line[i + 1] == ' ')
                {
                    var p = FlushParagraph(); if (p != null) yield return p;
                    yield return BuildOrdered(line[..i], line[(i + 2)..], imageResolver);
                    continue;
                }
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
        if (inCode && codeBuf.Length > 0) yield return BuildCodeBlock(codeBuf.ToString(), codeLang);
        if (inMathBlock && mathBuf.Length > 0) yield return BuildMathBlock(mathBuf.ToString());
    }

    private static bool TryParseImage(string line, out string alt, out string href)
    {
        alt = ""; href = "";
        if (!line.StartsWith("![")) return false;
        int closeAlt = line.IndexOf(']', 2);
        if (closeAlt < 0 || closeAlt + 1 >= line.Length || line[closeAlt + 1] != '(') return false;
        int closeHref = line.IndexOf(')', closeAlt + 2);
        if (closeHref < 0) return false;
        alt = line.Substring(2, closeAlt - 2);
        href = line.Substring(closeAlt + 2, closeHref - closeAlt - 2);
        return true;
    }

    private static Control BuildImage(string alt, string href, ImageResolver? resolver)
    {
        Bitmap? bmp = resolver?.Invoke(href);
        if (bmp != null)
        {
            var image = new global::Avalonia.Controls.Image
            {
                Source = bmp,
                Stretch = global::Avalonia.Media.Stretch.Uniform,
                StretchDirection = global::Avalonia.Media.StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 2),
            };
            var caption = new SelectableTextBlock
            {
                Text = alt,
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                Foreground = new SolidColorBrush(Color.Parse("#9C9C9C")),
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            };
            return new StackPanel
            {
                Spacing = 0,
                Children = { image, caption },
            };
        }
        // Resolver failed — show the alt text wrapped in a dashed-border box so
        // the writer can spot a broken reference at a glance.
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#FFCC00")),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.Parse("#221F08")),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 6, 0, 6),
            Child = new SelectableTextBlock
            {
                Text = $"[image not found] {href}\n{alt}",
                FontFamily = new FontFamily("Consolas, monospace"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#FFCC00")),
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    private static Control BuildQuote(string text, ImageResolver? imgs)
    {
        // Detect callout tag: `> [!NOTE]` / `[!TIP]` / `[!WARNING]` / `[!IMPORTANT]`.
        string tag = "Note";
        string accent = "#9CBDFE";   // blue
        string bg     = "#0F1A26";
        if (text.StartsWith("[!"))
        {
            int close = text.IndexOf(']');
            if (close > 2)
            {
                var t = text.Substring(2, close - 2).ToUpperInvariant();
                tag = t.ToLower();
                tag = char.ToUpperInvariant(tag[0]) + tag[1..];
                text = text[(close + 1)..].TrimStart();
                if (t == "WARNING" || t == "CAUTION")
                {
                    // User is red/green colour-blind — yellow, not red, is the
                    // canonical alert hue across this app's UI.
                    accent = "#FFCC00"; bg = "#221F08";
                }
                else if (t == "TIP")        { accent = "#A0E0A0"; bg = "#0E1A0E"; }
                else if (t == "IMPORTANT")  { accent = "#E6C0FF"; bg = "#1A0E20"; }
            }
        }
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new SelectableTextBlock
        {
            Text = tag,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse(accent)),
            Margin = new Thickness(0, 0, 0, 2),
        });
        var body = BuildInlineText(text, FontWeight.Normal, 13, "#DCDCDC", imgs);
        stack.Children.Add(body);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse(accent)),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Background = new SolidColorBrush(Color.Parse(bg)),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 4, 0, 6),
            Child = stack,
        };
    }

    private static Control BuildOrdered(string number, string text, ImageResolver? imgs)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var num = new SelectableTextBlock
        {
            Text = number + ".",
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#B4B4B4")),
            Margin = new Thickness(2, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(num, 0);
        grid.Children.Add(num);
        var inline = BuildInlineText(text.Trim(), FontWeight.Normal, 14, "#E0E0E0", imgs);
        Grid.SetColumn(inline, 1);
        grid.Children.Add(inline);
        return grid;
    }

    /// <summary>
    /// `$$ … $$` math block — rendered as a centred, dim-bordered code
    /// block. The embedded viewer keeps LaTeX source verbatim so the
    /// reader can copy it; the web export pipes the same source through
    /// KaTeX for full typeset rendering.
    /// </summary>
    private static Control BuildMathBlock(string latex)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#101820")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3A5A8A")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 8),
            Margin = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new SelectableTextBlock
            {
                Text = latex.TrimEnd(),
                FontFamily = new FontFamily("Cambria Math, Cambria, Consolas, serif"),
                FontSize = 14,
                FontStyle = FontStyle.Italic,
                Foreground = new SolidColorBrush(Color.Parse("#CDE6FF")),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
            },
        };
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
                        isHeader ? "#FFFFFF" : "#E0E0E0",
                        null),
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
        return new SelectableTextBlock
        {
            Text = text.Trim(),
            FontSize = size,
            FontWeight = weight,
            Foreground = new SolidColorBrush(Color.Parse(colorHex)),
            Margin = new Thickness(0, topPad, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static Control BuildCodeBlock(string code, string language)
    {
        var stack = new StackPanel { Spacing = 0 };
        if (!string.IsNullOrWhiteSpace(language))
        {
            stack.Children.Add(new SelectableTextBlock
            {
                Text = language,
                FontFamily = new FontFamily("Consolas, monospace"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#8888AA")),
                Margin = new Thickness(0, 0, 0, 4),
            });
        }
        stack.Children.Add(new SelectableTextBlock
        {
            Text = code.TrimEnd(),
            FontFamily = new FontFamily("Consolas, monospace"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9CDCFE")),
            TextWrapping = TextWrapping.NoWrap,
        });
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#101010")),
            BorderBrush = new SolidColorBrush(Color.Parse("#404040")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 4, 0, 4),
            Child = stack,
        };
    }

    private static Control BuildBullet(string text, ImageResolver? imgs)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var dot = new SelectableTextBlock
        {
            Text = "•",
            Foreground = new SolidColorBrush(Color.Parse("#B4B4B4")),
            Margin = new Thickness(2, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);
        var inline = BuildInlineText(text.Trim(), FontWeight.Normal, 14, "#E0E0E0", imgs);
        Grid.SetColumn(inline, 1);
        grid.Children.Add(inline);
        return grid;
    }

    // Inline-code spans (`literal`) render as monospace inside the wrapping
    // paragraph. Bold (**foo**), italic (*foo* / _foo_), inline LaTeX ($..$),
    // and links ([label](url)) are all parsed inline. Block-level constructs
    // are picked up earlier in Render().
    private static Control BuildInlineText(string text, FontWeight weight, double size, string colorHex, ImageResolver? imgs)
    {
        var tb = new SelectableTextBlock
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = new SolidColorBrush(Color.Parse(colorHex)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 1),
        };
        AppendInlines(tb.Inlines!, text, weight, colorHex);
        return tb;
    }

    private static void AppendInlines(InlineCollection inlines, string text, FontWeight baseWeight, string colorHex)
    {
        int i = 0;
        var buf = new System.Text.StringBuilder();

        void FlushBuf()
        {
            if (buf.Length == 0) return;
            inlines.Add(new Run(buf.ToString())
            {
                FontWeight = baseWeight,
                Foreground = new SolidColorBrush(Color.Parse(colorHex)),
            });
            buf.Clear();
        }

        while (i < text.Length)
        {
            char ch = text[i];

            // Inline code: `…`
            if (ch == '`')
            {
                FlushBuf();
                int end = text.IndexOf('`', i + 1);
                if (end < 0) { buf.Append(text[i..]); break; }
                string code = text.Substring(i + 1, end - i - 1);
                inlines.Add(new Run(code)
                {
                    FontFamily = new FontFamily("Consolas, monospace"),
                    Foreground = new SolidColorBrush(Color.Parse("#9CDCFE")),
                });
                i = end + 1;
                continue;
            }

            // Inline LaTeX: $…$ (single $ pair; '$$' is a block, handled above)
            if (ch == '$' && (i + 1 >= text.Length || text[i + 1] != '$'))
            {
                int end = text.IndexOf('$', i + 1);
                if (end > i)
                {
                    FlushBuf();
                    string latex = text.Substring(i + 1, end - i - 1);
                    inlines.Add(new Run(latex)
                    {
                        FontFamily = new FontFamily("Cambria Math, Cambria, Consolas, serif"),
                        FontStyle = FontStyle.Italic,
                        Foreground = new SolidColorBrush(Color.Parse("#CDE6FF")),
                    });
                    i = end + 1;
                    continue;
                }
            }

            // Bold: **text**
            if (ch == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    FlushBuf();
                    string inner = text.Substring(i + 2, end - i - 2);
                    inlines.Add(new Run(inner)
                    {
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse(colorHex)),
                    });
                    i = end + 2;
                    continue;
                }
            }

            // Italic: *text* or _text_  (avoid greedy matches on '_' inside identifiers).
            if (ch == '*' || (ch == '_' && (i == 0 || !char.IsLetterOrDigit(text[i - 1]))))
            {
                char mark = ch;
                int end = text.IndexOf(mark, i + 1);
                if (end > i && end < text.Length &&
                    (mark != '_' || end + 1 >= text.Length || !char.IsLetterOrDigit(text[end + 1])))
                {
                    string inner = text.Substring(i + 1, end - i - 1);
                    // Guard against empty match (``**`` already handled, but ``*`` ``*`` could slip in)
                    if (inner.Length > 0)
                    {
                        FlushBuf();
                        inlines.Add(new Run(inner)
                        {
                            FontStyle = FontStyle.Italic,
                            Foreground = new SolidColorBrush(Color.Parse(colorHex)),
                        });
                        i = end + 1;
                        continue;
                    }
                }
            }

            // Link: [label](url)
            if (ch == '[' && !(i > 0 && text[i - 1] == '!'))
            {
                int closeLabel = text.IndexOf(']', i + 1);
                if (closeLabel > 0 && closeLabel + 1 < text.Length && text[closeLabel + 1] == '(')
                {
                    int closeHref = text.IndexOf(')', closeLabel + 2);
                    if (closeHref > 0)
                    {
                        FlushBuf();
                        string label = text.Substring(i + 1, closeLabel - i - 1);
                        string href = text.Substring(closeLabel + 2, closeHref - closeLabel - 2);
                        inlines.Add(new Run(label)
                        {
                            Foreground = new SolidColorBrush(Color.Parse("#9CBDFF")),
                            TextDecorations = TextDecorations.Underline,
                        });
                        // Trailing href hint in dim grey so the destination is
                        // visible without HTML-style hover affordance.
                        inlines.Add(new Run($" ({href})")
                        {
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.Parse("#6A6A6A")),
                        });
                        i = closeHref + 1;
                        continue;
                    }
                }
            }

            buf.Append(text[i++]);
        }
        FlushBuf();
    }
}
