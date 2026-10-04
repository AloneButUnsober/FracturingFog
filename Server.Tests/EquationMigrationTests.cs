// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1088 (#937 Phase 3) — saved equation text is upgraded from language
// version 1 (-x^y = (-x)^y; a condition abs(x) = |x|²) to the current rules
// (-x^y = -(x^y); abs = |x|, norm = |x|²) without changing what it renders.
// Independent oracle: the version-1 parser (ParseLegacy) evaluating the
// ORIGINAL text, against the current parser evaluating the UPGRADED text, over
// a (z, c, n, prev) grid — bit-identical. Plus the store / import / persisted-
// calculator plumbing: backup first, version stamped, second run a no-op, and
// text already written in the current version never touched.
//
// Runs under the test data-root redirect (FractalRegionLibraryCollection).

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

using FracturingFog.Abstractions;
using FracturingFog.CalculatorGen;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class EquationMigrationTests
{
    // ── The text rewrite ──────────────────────────────────────────────────

    [Theory]
    [InlineData("-z^2 + c",                                  "(-z)^2 + c")]
    [InlineData("z*z + -c^2",                                "z*z + (-c)^2")]
    [InlineData("let zn = z / c^3 in c + cos(z) + log(-c^zn)", "let zn = z / c^3 in c + cos(z) + log((-c)^zn)")]   // the user's MandelFish
    [InlineData("sin(z * n) + cos(c^c * -c^-c)",             "sin(z * n) + cos(c^c * (-c)^-c)")]                  // Diamond Carpet
    [InlineData("--z^2 + c",                                 "(--z)^2 + c")]
    [InlineData("-z^2^2 + c",                                "(-z)^2^2 + c")]
    [InlineData("z^-c^2 + c",                                "z^(-c)^2 + c")]
    [InlineData("if abs(z) > 4 then z else z*z + c",         "if norm(z) > 4 then z else z*z + c")]
    [InlineData("if ABS(z) > 4 && abs(c) < 1 then z else c", "if norm(z) > 4 && norm(c) < 1 then z else c")]
    [InlineData("if (abs(z) > 2) then -z^2 else c",          "if (norm(z) > 2) then (-z)^2 else c")]
    [InlineData("z*z + c // -z^2 in a comment",              "z*z + c // -z^2 in a comment")]
    public void Rewrites_ToTheCurrentRules(string v1, string expected)
    {
        var r = EquationMigration.UpgradeFromVersion1(v1);
        Assert.Equal(expected, r.Source);
        Assert.Equal(expected != v1, r.Changed);
        Assert.Null(r.Note);
    }

    [Theory]
    [InlineData("z*z + c")]
    [InlineData("-(z^2) + c")]                     // already explicit
    [InlineData("(-z)^2 + c")]
    [InlineData("z^-2 + c")]                       // a signed EXPONENT reads the same both ways
    [InlineData("abs(z) > 2 ? z : c")]             // ternary abs was always |x|
    [InlineData("if abs(z) + 0 > 2 then z else c")] // not a direct comparison operand
    [InlineData("if norm(z) > 4 then z else c")]
    [InlineData("return Complex.Sin(z) + c;")]     // C# text: not parsed here, left for translation
    [InlineData("z*z +")]                          // broken: left as is
    [InlineData("")]
    public void LeavesUnaffectedText_Alone(string v1)
    {
        var r = EquationMigration.UpgradeFromVersion1(v1);
        Assert.Equal(v1, r.Source);
        Assert.False(r.Changed);
    }

    // Independent oracle: version-1 meaning of the original == current meaning of the upgrade.
    [Theory]
    [InlineData("-z^2 + c")]
    [InlineData("let zn = z / c^3 in c + cos(z) + log(-c^zn)")]
    [InlineData("let cScale = .00000000000001 in let cLinear = 9999999999 in sin(z*(n^1.3)) + ((c^-c / cLinear)*cScale) + ((-c^c / cLinear)*cScale) + ((-c^-c / cLinear)*cScale)")]
    [InlineData("sin(z * n) + (c^c * -c^-c)")]
    [InlineData("if abs(z) > 1.5 then z*z + c else -z^3 + c")]
    [InlineData("if re(z) > 0 && abs(c) < 0.6 then z*z + c else conj(z)^2 + c")]
    [InlineData("var w = -z^2; if (abs(w) > 4) return w; return w + c;")]
    public void Upgrade_PreservesTheMeaning(string v1)
    {
        var upgraded = EquationMigration.UpgradeFromVersion1(v1);
        Assert.True(upgraded.Changed, "corpus entry should need an upgrade");
        var before = SandboxExpression.ParseLegacy(v1);
        var after = SandboxExpression.Parse(upgraded.Source);
        var be = before.NewEnv();
        var ae = after.NewEnv();
        foreach (var z in new[] { new Complex(0.3, -0.2), new Complex(-1.4, 0.7), new Complex(2.2, 1.9), new Complex(-0.6, 0) })
            foreach (var c in new[] { new Complex(-0.75, 0.1), new Complex(0.28, 0.53) })
                foreach (int n in new[] { 0, 1, 7 })
                {
                    var b = before.EvalStep(z, c, n, be, z * 0.5);
                    var a = after.EvalStep(z, c, n, ae, z * 0.5);
                    Assert.Equal(b.Real, a.Real);
                    Assert.Equal(b.Imaginary, a.Imaginary);
                }
        // And the old text under the NEW rules really would have changed meaning
        // (the migration is necessary, not cosmetic).
        Assert.NotEqual(EquationLanguage.ToSExpression(before), EquationLanguage.ToSExpression(SandboxExpression.Parse(v1)));
    }

    // ── Entry version stamping ────────────────────────────────────────────

    [Fact]
    public void EntryVersion_CodeCreatedIsCurrent_OldJsonIsVersion1()
    {
        Assert.Equal(EquationMigration.CurrentLanguageVersion, new UserEquationEntry().LanguageVersion);
        Assert.Equal(EquationMigration.CurrentLanguageVersion, new SandboxEquationEntry().LanguageVersion);
        Assert.Equal(1, JsonSerializer.Deserialize<UserEquationEntry>("{\"Name\":\"a\",\"Source\":\"-z^2\"}")!.LanguageVersion);
        Assert.Equal(1, JsonSerializer.Deserialize<SandboxEquationEntry>("{\"Name\":\"a\",\"Source\":\"-z^2\"}")!.LanguageVersion);
        Assert.Equal(2, JsonSerializer.Deserialize<SandboxEquationEntry>("{\"Name\":\"a\",\"LanguageVersion\":2}")!.LanguageVersion);
        Assert.Contains("\"LanguageVersion\": 2", JsonSerializer.Serialize(new SandboxEquationEntry { Name = "a" }, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── Store migration on Load ───────────────────────────────────────────

    [Fact]
    public void SandboxStore_UpgradesLegacyEntries_OnLoad_WithBackup_Once()
    {
        string file = AppDataPaths.Combine("sandboxequations.json");
        File.WriteAllText(file, """
            [
              { "Name": "Fish", "Source": "c + cos(z) + log(-c^z)" },
              { "Name": "Plain", "Source": "z*z + c" },
              { "Name": "New", "Source": "-z^2 + c", "LanguageVersion": 2 }
            ]
            """);
        foreach (var b in Directory.GetFiles(AppDataPaths.Root, "sandboxequations.json.*.bak")) File.Delete(b);
        try
        {
            var store = SandboxEquationStore.Instance;
            store.Load();
            Assert.Equal("c + cos(z) + log((-c)^z)", store.GetByName("Fish")!.Source);
            Assert.Equal("z*z + c", store.GetByName("Plain")!.Source);
            Assert.Equal("-z^2 + c", store.GetByName("New")!.Source);   // written in the current version: untouched
            Assert.All(store.Equations, e => Assert.Equal(EquationMigration.CurrentLanguageVersion, e.LanguageVersion));

            var backups = Directory.GetFiles(AppDataPaths.Root, "sandboxequations.json.*.bak");
            Assert.Single(backups);
            Assert.Contains("log(-c^z)", File.ReadAllText(backups[0]));     // the original, kept
            Assert.Contains("\"LanguageVersion\": 2", File.ReadAllText(file));

            string afterFirst = File.ReadAllText(file);
            store.Load();                                                   // second run: no-op
            Assert.Equal(afterFirst, File.ReadAllText(file));
            Assert.Single(Directory.GetFiles(AppDataPaths.Root, "sandboxequations.json.*.bak"));
        }
        finally
        {
            File.Delete(file);
            foreach (var b in Directory.GetFiles(AppDataPaths.Root, "sandboxequations.json.*.bak")) File.Delete(b);
            SandboxEquationStore.Instance.Load();
        }
    }

    [Fact]
    public void UserEquationStore_UpgradesSourceSeedAndBailout_OnLoad()
    {
        string file = AppDataPaths.Combine("userequations.json");
        File.WriteAllText(file, """
            [
              { "Name": "Cond", "Source": "if abs(z) > 4 then z else -z^2 + c", "Kind": 1,
                "Seed": "-c^2", "BailoutCondition": "abs(-z^2) > 100" },
              { "Name": "CSharp", "Source": "return Complex.Sin(z) + c;" }
            ]
            """);
        try
        {
            var store = UserEquationStore.Instance;
            store.Load();
            var e = store.GetByName("Cond")!;
            Assert.Equal("if norm(z) > 4 then z else (-z)^2 + c", e.Source);
            Assert.Equal("(-c)^2", e.Seed);
            Assert.Equal("abs((-z)^2) > 100", e.BailoutCondition);
            Assert.Equal("return Complex.Sin(z) + c;", store.GetByName("CSharp")!.Source);   // C# text: left for translation
            Assert.NotEmpty(Directory.GetFiles(AppDataPaths.Root, "userequations.json.*.bak"));
        }
        finally
        {
            File.Delete(file);
            foreach (var b in Directory.GetFiles(AppDataPaths.Root, "userequations.json.*.bak")) File.Delete(b);
            UserEquationStore.Instance.Load();
        }
    }

    [Fact]
    public void ImportedLegacyEntry_IsUpgraded_BeforeItIsSaved()
    {
        var store = SandboxEquationStore.Instance;
        store.Load();
        var imported = JsonSerializer.Deserialize<SandboxEquationEntry>("{\"Name\":\"FF-1088-Imported\",\"Source\":\"-z^2 + c\"}")!;
        store.Equations.Add(new SandboxEquationEntry { Name = imported.Name, Source = imported.Source, LanguageVersion = imported.LanguageVersion });
        try
        {
            Assert.Equal(1, store.UpgradeLegacyEntries(persist: false));
            Assert.Equal("(-z)^2 + c", store.GetByName("FF-1088-Imported")!.Source);
            Assert.Equal(0, store.UpgradeLegacyEntries(persist: false));
        }
        finally { store.Equations.RemoveAll(e => e.Name == "FF-1088-Imported"); }
    }

    // ── Persisted (hot-loaded) calculators ────────────────────────────────

    [Fact]
    public void PersistedCalculator_LegacyMeta_IsUpgraded_OnceWithBackup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "FracturingFog.Tests", "1088-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string meta = Path.Combine(dir, "FooCalculator.meta.txt");
            File.WriteAllText(meta, "-z^2 + c");
            var up = typeof(CalculatorGenHotLoad).GetMethod("UpgradePersistedEquation", BindingFlags.NonPublic | BindingFlags.Static)!;

            string first = (string)up.Invoke(null, new object[] { dir, "FooCalculator", meta, "-z^2 + c" })!;
            Assert.Equal("(-z)^2 + c", first);
            Assert.Equal("(-z)^2 + c", File.ReadAllText(meta));
            Assert.Equal("2", File.ReadAllText(Path.Combine(dir, "FooCalculator" + CalculatorGenHotLoad.LangSuffix)));
            Assert.Single(Directory.GetFiles(dir, "FooCalculator.meta.txt.*.bak"));

            // Marked current: a second load leaves the (current-version) text alone.
            string second = (string)up.Invoke(null, new object[] { dir, "FooCalculator", meta, "-z^2 + c" })!;
            Assert.Equal("-z^2 + c", second);
            Assert.Single(Directory.GetFiles(dir, "FooCalculator.meta.txt.*.bak"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
