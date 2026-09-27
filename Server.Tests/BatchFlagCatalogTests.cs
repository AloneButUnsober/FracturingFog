// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FracturingFog.Batch;
using Xunit;

namespace FracturingFog.Server.Tests;

// #993 (CB1 of #64) — the flag catalog is data the Command Builder panel trusts
// to enable / disable options, so every claim in it is checked against the
// parser itself: the flag set, value kinds, enforced ranges, implications and
// mode selection. The oracle is always BatchOptions.TryParse.
public sealed class BatchFlagCatalogTests
{
    // ── Harness ──────────────────────────────────────────────────────────────

    private static string[] BaseArgs(BatchMode mode, bool remote = false)
    {
        if (remote) return new[] { "--remote", "--connection", "c", "--render", "r", "--out", "o.png" };
        return mode switch
        {
            BatchMode.Video     => new[] { "--mode", "video", "--x", "0", "--y", "0", "--zoom", "1", "--out", "o" },
            BatchMode.Slideshow => new[] { "--slideshow", "s", "--out", "o.mp4" },
            BatchMode.Scene     => new[] { "--scene", "s", "--out", "o.mp4" },
            BatchMode.Regrade   => new[] { "--regrade-exr", "in.exr", "--out", "o.png" },
            BatchMode.Relight   => new[] { "--relight-from", "in.exr", "--out", "o.png" },
            _                   => new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" },
        };
    }

    /// <summary>The mode a flag is exercised in: its range-check mode, else its
    /// first applicable local mode (remote-only flags use the remote base).</summary>
    private static (BatchMode mode, bool remote) HomeOf(BatchFlagSpec s)
    {
        if (s.RangeMode is BatchMode rm) return (rm, false);
        foreach (BatchMode m in Enum.GetValues<BatchMode>())
            if ((s.Modes & BatchFlagCatalog.MaskOf(m)) != 0) return (m, false);
        return (BatchMode.Image, (s.Modes & BatchModes.Remote) != 0);
    }

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A valid, preferably non-default value for the flag.</summary>
    private static string? Sample(BatchFlagSpec s)
    {
        switch (s.Kind)
        {
            case BatchFlagKind.Switch: return null;
            case BatchFlagKind.Choice: return s.Choices.FirstOrDefault(c => !string.Equals(c, s.Default, StringComparison.OrdinalIgnoreCase)) ?? s.Choices[0];
            case BatchFlagKind.Text:   return "x";
            case BatchFlagKind.Path:   return "p.exr";
            case BatchFlagKind.Color:  return "#102030";
            case BatchFlagKind.Vector: return string.Join(",", Enumerable.Repeat("0.5", s.Arity));
        }
        double v = (s.Min, s.Max) switch
        {
            (double lo, double hi) => (lo + hi) / 2,
            (double lo, null)      => lo + 1,
            (null, double hi)      => hi - 1,
            _                      => 2,
        };
        return s.Kind == BatchFlagKind.Int ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : Fmt(v);
    }

    private static string[] With(string[] args, BatchFlagSpec s, string? value = null, string? spelling = null)
    {
        var l = new List<string>(args) { spelling ?? s.Name };
        if (s.TakesValue) l.Add(value ?? Sample(s)!);
        return l.ToArray();
    }

    private static bool Parse(string[] args, out BatchOptions opts, out string? error)
        => BatchOptions.TryParse(args, 0, out opts, out error);

    private static string Snapshot(BatchOptions o) => JsonSerializer.Serialize(o);

    public static IEnumerable<object[]> Flags()
        => BatchFlagCatalog.All.Where(s => s.Modes != BatchModes.None).Select(s => new object[] { s.Name });

    private static BatchFlagSpec Spec(string name) => BatchFlagCatalog.Find(name)!;

    // ── The flag set matches the parser ──────────────────────────────────────

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    [Fact]
    public void ParserCases_AreExactlyTheCatalog()
    {
        string src = File.ReadAllText(RepoFile("Abstractions", "Batch", "BatchOptions.cs"));
        var parser = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(src, @"case\s+""(-[^""]*)""\s*:")) parser.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(src, @"case\s+BatchFlags\.(\w+)\s*:"))
        {
            var v = (string)typeof(BatchFlags).GetField(m.Groups[1].Value)!.GetValue(null)!;
            if (v.StartsWith('-')) parser.Add(v);   // light field names ("type", ...) are not flags
        }

        var catalog = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in BatchFlagCatalog.All.Where(s => s.LightNumber == 0))
        {
            catalog.Add(s.Name);
            foreach (var a in s.Aliases) catalog.Add(a);
        }

        Assert.True(parser.SetEquals(catalog),
            "Parser-only: " + string.Join(" ", parser.Except(catalog)) +
            " | Catalog-only: " + string.Join(" ", catalog.Except(parser)));
    }

    [Fact]
    public void EveryBatchFlagsConst_IsACanonicalCatalogName()
    {
        var consts = typeof(BatchFlags).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(v => v.StartsWith("--", StringComparison.Ordinal));
        foreach (var c in consts)
            Assert.True(BatchFlagCatalog.All.Any(s => s.Name == c), $"{c} has no catalog entry");
    }

    [Fact]
    public void Names_AreUnique_AndLightsCoverEveryField()
    {
        var all = BatchFlagCatalog.All.SelectMany(s => s.Aliases.Prepend(s.Name)).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(24, BatchFlagCatalog.All.Count(s => s.LightNumber > 0));   // 3 lights x 8 fields
        Assert.Same(Spec("--region"), Spec("-R"));                              // alias + case-insensitive
    }

    // ── Every flag, alias and choice parses ──────────────────────────────────

    [Theory]
    [MemberData(nameof(Flags))]
    public void Flag_AndEveryAlias_Parse(string name)
    {
        var s = Spec(name);
        var (mode, remote) = HomeOf(s);
        foreach (var spelling in s.Aliases.Prepend(s.Name))
            Assert.True(Parse(With(BaseArgs(mode, remote), s, spelling: spelling), out _, out var err), $"{spelling}: {err}");
    }

    [Fact]
    public void EveryChoice_Parses()
    {
        foreach (var s in BatchFlagCatalog.All.Where(s => s.Kind == BatchFlagKind.Choice))
            foreach (var c in s.Choices)
            {
                var (mode, remote) = HomeOf(s);
                if (s.Name == BatchFlags.Mode) mode = Enum.Parse<BatchMode>(c, ignoreCase: true);
                Assert.True(Parse(With(BaseArgs(mode, remote), s, c), out _, out var err), $"{s.Name} {c}: {err}");
            }
    }

    [Theory]
    [MemberData(nameof(Flags))]
    public void SelectsMode_IsWhatTheParserDoes(string name)
    {
        var s = Spec(name);
        if (HomeOf(s).remote) return;   // --remote alone does not validate
        Assert.True(Parse(With(BaseArgs(BatchMode.Image), s), out var o, out var err), err);
        if (s.SelectsMode is BatchMode m) Assert.Equal(m, o.Mode);
        else if (s.Name != BatchFlags.Mode) Assert.Equal(BatchMode.Image, o.Mode);
    }

    // ── Ranges ───────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Flags))]
    public void EnforcedRange_MatchesTheValidator(string name)
    {
        var s = Spec(name);
        if (!s.RangeEnforced) return;
        Assert.True(s.Kind is BatchFlagKind.Int or BatchFlagKind.Double, $"{name}: only numeric flags have ranges");
        var (mode, remote) = HomeOf(s);
        var b = BaseArgs(mode, remote);
        double eps = s.Kind == BatchFlagKind.Int ? 1 : 1e-6;
        string V(double d) => s.Kind == BatchFlagKind.Int ? ((long)d).ToString(CultureInfo.InvariantCulture) : Fmt(d);

        if (s.Min is double lo)
        {
            Assert.Equal(!s.MinExclusive, Parse(With(b, s, V(lo)), out _, out _));
            Assert.False(Parse(With(b, s, V(lo - eps)), out _, out _), $"{name} accepted {lo - eps}");
            if (s.MinExclusive) Assert.True(Parse(With(b, s, V(lo + eps)), out _, out var e0), $"{name}: {e0}");
        }
        if (s.Max is double hi)
        {
            Assert.True(Parse(With(b, s, V(hi)), out _, out var e1), $"{name} rejected its max: {e1}");
            Assert.False(Parse(With(b, s, V(hi + eps)), out _, out _), $"{name} accepted {hi + eps}");
        }
    }

    // ── Relations ────────────────────────────────────────────────────────────

    [Fact]
    public void Relations_ReferenceRealFlags_AndConflictsAreSymmetric()
    {
        foreach (var s in BatchFlagCatalog.All)
        {
            foreach (var t in s.Implies.Concat(s.Requires).Concat(s.ConflictsWith).Concat(s.ChoiceImplies.Values.SelectMany(v => v)))
                Assert.True(BatchFlagCatalog.All.Any(x => x.Name == t), $"{s.Name} names unknown flag {t}");
            foreach (var t in s.Implies.Concat(s.ChoiceImplies.Values.SelectMany(v => v)))
                Assert.Equal(BatchFlagKind.Switch, Spec(t).Kind);
            foreach (var c in s.ChoiceImplies.Keys)
                Assert.Contains(c, s.Choices, StringComparer.OrdinalIgnoreCase);   // only a switch can be implied wholesale
            foreach (var t in s.ConflictsWith)
                Assert.Contains(s.Name, Spec(t).ConflictsWith);
            Assert.DoesNotContain(s.Name, s.Implies.Concat(s.Requires).Concat(s.ConflictsWith));
        }
    }

    private static HashSet<string> ImpliedClosure(BatchFlagSpec s, string? value = null)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>(s.Implies);
        if (value != null && s.ChoiceImplies.TryGetValue(value, out var byValue))
            foreach (var t in byValue) stack.Push(t);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (!seen.Add(t)) continue;
            foreach (var u in Spec(t).Implies) stack.Push(u);
        }
        return seen;
    }

    [Theory]
    [MemberData(nameof(Flags))]
    public void DeclaredImplications_AreWhatTheParserDoes(string name)
    {
        var s = Spec(name);
        var (mode, remote) = HomeOf(s);
        var withS = With(BaseArgs(mode, remote), s);
        Assert.True(Parse(withS, out var a, out var err), err);
        foreach (var t in ImpliedClosure(s, Sample(s)))
        {
            Assert.True(Parse(With(withS, Spec(t)), out var b, out var err2), err2);
            Assert.True(Snapshot(a) == Snapshot(b), $"{name} is declared to imply {t}, but adding {t} changes the parse");
        }
    }

    [Theory]
    [MemberData(nameof(Flags))]
    public void NoUndeclaredSwitchImplications(string name)
    {
        // If adding switch T never changes the parse of S, S already implies T
        // and the catalog must say so (the panel relies on it to grey T out).
        var s = Spec(name);
        var (mode, remote) = HomeOf(s);
        var withS = With(BaseArgs(mode, remote), s);
        Assert.True(Parse(withS, out var a, out var err), err);
        var implied = ImpliedClosure(s, Sample(s));
        foreach (var t in BatchFlagCatalog.All.Where(x => x.Kind == BatchFlagKind.Switch && x.Modes != BatchModes.None))
        {
            if (t.Name == s.Name || implied.Contains(t.Name) || withS.Contains(t.Name)) continue;   // already in the base args
            if (!Parse(With(withS, t), out var b, out _)) continue;
            Assert.False(Snapshot(a) == Snapshot(b), $"{name} silently implies {t.Name} — add it to Implies");
        }
    }

    // ── Usage text ───────────────────────────────────────────────────────────

    [Fact]
    public void Usage_ListsEveryFlag_AndFitsTheWidth()
    {
        string usage = BatchFlagCatalog.FormatUsage();
        foreach (var s in BatchFlagCatalog.All)
        {
            string shown = s.LightNumber > 0 ? s.Name.Replace($"--light{s.LightNumber}-", "--lightN-") : s.Name;
            Assert.Contains(shown, usage);
            foreach (var a in s.Aliases) Assert.Contains(a, usage);
        }
        Assert.DoesNotContain("--light2-", usage);
        foreach (var line in usage.Split('\n'))
            Assert.True(line.TrimEnd('\r').Length <= 100, $"usage line too long: {line}");
    }

    [Fact]
    public void AppliesIn_FollowsTheModeMask()
    {
        Assert.True(BatchFlagCatalog.AppliesIn(Spec(BatchFlags.AovExr), BatchMode.Image));
        Assert.False(BatchFlagCatalog.AppliesIn(Spec(BatchFlags.AovExr), BatchMode.Video));
        Assert.True(BatchFlagCatalog.AppliesIn(Spec(BatchFlags.Out), BatchMode.Image, remote: true));
        Assert.False(BatchFlagCatalog.AppliesIn(Spec(BatchFlags.Relief), BatchMode.Image, remote: true));
        Assert.False(BatchFlagCatalog.AppliesIn(Spec(BatchFlags.ReliefFroxelTemporal), BatchMode.Image));
    }
}
