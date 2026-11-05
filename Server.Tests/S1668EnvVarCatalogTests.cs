// #1668 (epic #1667, S1) — guard: every "FF_..." switch the source reads has an
// EnvVarCatalog entry, and every entry is still read somewhere.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FracturingFog.Cli;
using Xunit;

namespace FracturingFog.Server.Tests;

public class S1668EnvVarCatalogTests
{
    private static readonly Regex Literal = new("\"(FF_[A-Z0-9_]+)\"", RegexOptions.Compiled);

    // The catalog and this test name every switch, so they don't count as readers.
    private static readonly string[] Excluded =
    [
        Path.Combine("Abstractions", "Cli", "EnvVarCatalog.cs"),
        Path.Combine("Server.Tests", "S1668EnvVarCatalogTests.cs"),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    internal static SortedSet<string> SwitchesInSource(string root)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, f);
            string[] parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => p is "bin" or "obj" or ".git" or ".claude")) continue;
            if (Excluded.Any(x => string.Equals(rel, x, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (Match m in Literal.Matches(File.ReadAllText(f))) found.Add(m.Groups[1].Value);
        }
        return found;
    }

    internal static (List<string> Missing, List<string> Stale) Compare(ISet<string> inSource, IEnumerable<EnvVarEntry> catalog)
    {
        var names = catalog.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        return (inSource.Where(n => !names.Contains(n)).ToList(), names.Where(n => !inSource.Contains(n)).OrderBy(n => n).ToList());
    }

    [Fact]
    public void EverySwitchInSourceIsCatalogued_AndEveryEntryIsRead()
    {
        var (missing, stale) = Compare(SwitchesInSource(RepoRoot()), EnvVarCatalog.All);
        Assert.True(missing.Count == 0,
            "FF_* switches read in source with no EnvVarCatalog entry (add them to Abstractions/Cli/EnvVarCatalog.cs): "
            + string.Join(", ", missing));
        Assert.True(stale.Count == 0,
            "EnvVarCatalog entries no source file reads any more (remove or fix): " + string.Join(", ", stale));
    }

    [Fact]
    public void Compare_FlagsAnUncataloguedSwitch_AndAStaleEntry()
    {
        var src = new SortedSet<string>(StringComparer.Ordinal) { "FF_GPU_LA", "FF_NOT_IN_CATALOG" };
        var cat = new[]
        {
            EnvVarCatalog.Find("FF_GPU_LA")!,
            new EnvVarEntry("FF_NO_LONGER_READ", EnvVarKind.Toggle, EnvVarGroup.Test, "x", "off"),
        };
        var (missing, stale) = Compare(src, cat);
        Assert.Equal(["FF_NOT_IN_CATALOG"], missing);
        Assert.Equal(["FF_NO_LONGER_READ"], stale);
    }

    [Fact]
    public void Entries_AreWellFormed()
    {
        Assert.Equal(EnvVarCatalog.All.Count, EnvVarCatalog.All.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var e in EnvVarCatalog.All)
        {
            Assert.Matches("^FF_[A-Z0-9_]+$", e.Name);
            Assert.False(string.IsNullOrWhiteSpace(e.Description), e.Name);
            Assert.False(string.IsNullOrWhiteSpace(e.Default), e.Name);
            if (e.Kind == EnvVarKind.Choice)
            {
                Assert.True(e.Choices is { Count: >= 2 }, $"{e.Name}: a choice needs at least two values");
                Assert.Contains(e.Default, e.Choices!);
            }
            else Assert.Null(e.Choices);
            if (e.Kind == EnvVarKind.Toggle)
                Assert.Equal(e.DefaultOn ? "on" : "off", e.Default);
            if (e.Kind == EnvVarKind.Number && double.TryParse(e.Default, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double d))
            {
                if (e.Min is { } lo) Assert.True(d >= lo, $"{e.Name}: default below Min");
                if (e.Max is { } hi) Assert.True(d <= hi, $"{e.Name}: default above Max");
            }
            if (e.Risk != EnvVarRisk.None) Assert.False(string.IsNullOrWhiteSpace(e.Warning), $"{e.Name}: risky switch needs a warning");
            if (e.Group is EnvVarGroup.Probe or EnvVarGroup.Test) Assert.True(e.Developer, $"{e.Name}: probe / test switches are developer-only");
            if (e.Issue != null) Assert.Matches("^#[0-9]+$", e.Issue);
        }
        Assert.Equal(EnvVarRisk.Danger, EnvVarCatalog.Find("FF_D3D_LA")!.Risk);
        Assert.Contains(EnvVarCatalog.UserFacing, e => e.Name == "FF_GPU_PERTURB");
        Assert.DoesNotContain(EnvVarCatalog.UserFacing, e => e.Group == EnvVarGroup.Probe);
    }
}
