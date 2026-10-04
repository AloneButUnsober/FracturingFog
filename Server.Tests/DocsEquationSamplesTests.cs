// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1090 — every equation sample in the docs and the in-app help parses with
// the unified equation language (#937), so a reader who pastes one gets a
// rendering equation, not an error.
//
//   Docs (*.md):  ```equation   — the block is ONE equation (statements allowed)
//                 ```equations  — each non-blank, non-comment line is an equation
//   Help:         the "--- Title ---" snippet blocks in HelpTextBundle (User
//                 Equation, Sandbox, and the per-type "As a User Equation"
//                 sections).
//
// Minimum counts keep the test from passing vacuously if a tag is renamed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FracturingFog.Help;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DocsEquationSamplesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>(where, text) for every tagged sample in the repo's Markdown.</summary>
    public static IEnumerable<(string Where, string Text)> DocSamples()
    {
        string root = RepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "Docs"), "*.md", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "FEATURES.md"));
        foreach (string file in files)
        {
            string rel = Path.GetRelativePath(root, file);
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], @"^\s*```(equations?)\s*$");
                if (!m.Success) continue;
                int start = i + 1, j = start;
                while (j < lines.Length && !Regex.IsMatch(lines[j], @"^\s*```\s*$")) j++;
                if (m.Groups[1].Value == "equation")
                    yield return ($"{rel}:{start}", string.Join("\n", lines[start..j]));
                else
                    for (int k = start; k < j; k++)
                    {
                        string t = lines[k].Trim();
                        if (t.Length > 0 && !t.StartsWith("//")) yield return ($"{rel}:{k + 1}", lines[k]);
                    }
                i = j;
            }
        }
    }

    /// <summary>(where, text) for every "--- Title ---" snippet in the help panels.</summary>
    public static IEnumerable<(string Where, string Text)> HelpSamples()
    {
        var panels = new (string Name, string Text)[]
        {
            (nameof(HelpTextBundle.MathUserEquationText), HelpTextBundle.MathUserEquationText),
            (nameof(HelpTextBundle.MathSandboxText), HelpTextBundle.MathSandboxText),
        }.Concat(typeof(HelpTextBundle).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Math"))
            .Select(f => (f.Name, Text: (string)f.GetRawConstantValue()!))
            .Where(p => p.Text.Contains("=== As a User Equation ===")))
         .DistinctBy(p => p.Name);

        foreach (var (name, text) in panels)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], @"^\s+--- (.*) ---\s*$");
                if (!m.Success) continue;
                int j = i + 1;
                while (j < lines.Length && lines[j].Trim().Length > 0 && !lines[j].StartsWith("===")) j++;
                yield return ($"{name} '{m.Groups[1].Value}'", string.Join("\n", lines[(i + 1)..j]));
                i = j;
            }
        }
    }

    [Fact]
    public void EveryDocEquationSample_Parses()
    {
        var samples = DocSamples().ToList();
        Assert.True(samples.Count >= 150, $"only {samples.Count} tagged doc samples found");
        var bad = samples
            .Select(s => (s.Where, s.Text, Ok: EquationLanguage.TryParse(s.Text, out _, out string? err), Err: err))
            .Where(x => !x.Ok)
            .Select(x => $"{x.Where}: {x.Text.Trim()}  →  {x.Err}")
            .ToList();
        Assert.True(bad.Count == 0, "Doc samples that don't parse:\n" + string.Join("\n", bad));
    }

    /// <summary>#1100 — User Bulb 3D samples: ```bulb (one step, statements
    /// allowed) and ```bulbs (one per line), parsed with the bulb language.</summary>
    public static IEnumerable<(string Where, string Text)> BulbDocSamples()
    {
        string root = RepoRoot();
        foreach (string file in Directory.GetFiles(Path.Combine(root, "Docs"), "*.md", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], @"^\s*```(bulbs?)\s*$");
                if (!m.Success) continue;
                int start = i + 1, j = start;
                while (j < lines.Length && !Regex.IsMatch(lines[j], @"^\s*```\s*$")) j++;
                if (m.Groups[1].Value == "bulb")
                    yield return ($"{rel}:{start}", string.Join("\n", lines[start..j]));
                else
                    for (int k = start; k < j; k++)
                        if (lines[k].Trim().Length > 0 && !lines[k].Trim().StartsWith("//")) yield return ($"{rel}:{k + 1}", lines[k]);
                i = j;
            }
        }
    }

    [Fact]
    public void EveryBulbDocSample_Parses()
    {
        var samples = BulbDocSamples().ToList();
        Assert.True(samples.Count >= 20, $"only {samples.Count} tagged bulb samples found");
        var bad = new List<string>();
        foreach (var (where, text) in samples)
        {
            // Lenient identifiers: samples use params and chain-step names.
            // Functions, operators and members are still checked.
            try { SandboxBulbExpression.ParseLenient(text); }
            catch (FormatException ex) { bad.Add($"{where}: {text.Trim()}  →  {ex.Message}"); }
        }
        Assert.True(bad.Count == 0, "Bulb doc samples that don't parse:\n" + string.Join("\n", bad));
    }

    /// <summary>#1102 — the "Source:" blocks of the in-app User Bulb help.</summary>
    public static IEnumerable<(string Where, string Text)> BulbHelpSamples()
    {
        var lines = HelpTextBundle.MathUserBulbText.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^\s*Source[^:]*:\s*$")) continue;
            int j = i + 1;
            while (j < lines.Length && lines[j].Trim().Length > 0) j++;
            yield return ($"MathUserBulbText line {i + 2}", string.Join("\n", lines[(i + 1)..j]));
            i = j;
        }
    }

    [Fact]
    public void EveryBulbHelpSource_Parses()
    {
        var samples = BulbHelpSamples().ToList();
        Assert.True(samples.Count >= 14, $"only {samples.Count} bulb help sources found");
        var bad = new List<string>();
        foreach (var (where, text) in samples)
        {
            try { SandboxBulbExpression.ParseLenient(text); }
            catch (FormatException ex) { bad.Add($"{where}: {text.Trim()}  →  {ex.Message}"); }
        }
        Assert.True(bad.Count == 0, "Bulb help sources that don't parse:\n" + string.Join("\n", bad));
        Assert.Contains(HelpTextBundle.EquationGrammarText, HelpTextBundle.MathUserBulbText);
        Assert.Contains("COMPONENTWISE on vectors", HelpTextBundle.MathUserBulbText);
        Assert.DoesNotContain("Roslyn", HelpTextBundle.MathUserBulbText);
    }

    [Fact]
    public void EveryHelpEquationSnippet_Parses()
    {
        var samples = HelpSamples().ToList();
        Assert.True(samples.Count >= 50, $"only {samples.Count} help snippets found");
        var bad = samples
            .Select(s => (s.Where, s.Text, Ok: EquationLanguage.TryParse(s.Text, out _, out string? err), Err: err))
            .Where(x => !x.Ok)
            .Select(x => $"{x.Where}: {x.Text.Trim()}  →  {x.Err}")
            .ToList();
        Assert.True(bad.Count == 0, "Help snippets that don't parse:\n" + string.Join("\n", bad));
    }

    // The language's two #1088 rule changes must be what the help teaches.
    [Fact]
    public void Help_TeachesTheCurrentRules()
    {
        string g = HelpTextBundle.EquationGrammarText;
        Assert.Contains("−z^2 means −(z^2)", g);
        Assert.Contains("abs(x)    the magnitude |x|  (everywhere, conditions included)", g);
        Assert.Contains("norm(x)", g);
        Assert.Contains(g, HelpTextBundle.MathUserEquationText);
        Assert.Contains(g, HelpTextBundle.MathSandboxText);
        Assert.DoesNotContain("Roslyn", HelpTextBundle.MathUserEquationText);
        Assert.DoesNotContain("=== C# Equation ===", typeof(HelpTextBundle).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!).Aggregate("", (a, b) => a + b));
    }
}
