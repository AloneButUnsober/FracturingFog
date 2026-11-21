// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Guard for a crash class: Avalonia's NumericUpDown is decimal-backed, and its XAML loader reads
// literal Minimum / Maximum / Increment / Value with decimal.Parse(text, InvariantCulture)
// (NumberStyles.Number: no exponent, at most about 7.9e28). A literal it cannot read throws
// while the view is built. #1946's Mixmaster panel had Maximum="1E+300", which took down the
// whole Params window for every fractal type. This scans every .axaml under UI.Avalonia and
// PaletteBuilder and reads each literal the same way.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class NumericUpDownLiteralTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static readonly Regex Element = new(@"<NumericUpDown\b[^>]*>", RegexOptions.Singleline);
    private static readonly Regex Attr = new(@"\b(Minimum|Maximum|Increment|Value)\s*=\s*""([^""]*)""");

    [Fact]
    public void EveryNumericUpDownLiteral_IsADecimalAvaloniaCanRead()
    {
        var bad = new List<string>();
        int seen = 0;
        foreach (var dir in new[] { "UI.Avalonia", "PaletteBuilder" })
        {
            string root = Path.Combine(RepoRoot(), dir);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                string text = File.ReadAllText(file);
                foreach (Match el in Element.Matches(text))
                {
                    var lits = new Dictionary<string, decimal>();
                    foreach (Match a in Attr.Matches(el.Value))
                    {
                        string v = a.Groups[2].Value.Trim();
                        if (v.StartsWith('{')) continue;   // a binding or markup extension
                        seen++;
                        int line = text[..el.Index].Count(c => c == '\n') + 1;
                        try { lits[a.Groups[1].Value] = decimal.Parse(v, CultureInfo.InvariantCulture); }
                        catch (Exception ex) when (ex is FormatException or OverflowException)
                        { bad.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{line} {a.Groups[1].Value}=\"{v}\" ({ex.GetType().Name})"); }
                    }
                    if (lits.TryGetValue("Minimum", out var lo) && lits.TryGetValue("Maximum", out var hi) && lo > hi)
                    {
                        int line = text[..el.Index].Count(c => c == '\n') + 1;
                        bad.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{line} Minimum {lo} > Maximum {hi}");
                    }
                }
            }
        }
        Assert.True(seen > 100, $"only {seen} literals found: is the scan looking in the right place?");
        Assert.True(bad.Count == 0, "NumericUpDown literals Avalonia cannot load:\n" + string.Join("\n", bad));
    }
}
