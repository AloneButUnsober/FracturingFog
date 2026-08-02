// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 2b — the built-in UserBulb presets (and chain primitives) now ship
// as safe DSL rather than raw C#. These tests prove:
//   1. every chain primitive body and worked-example chain parses as DSL and
//      evaluates to finite Vec3,
//   2. every seeded built-in pins Compiler = Sandbox and its body / chain
//      parses as DSL,
//   3. the load-time migration upgrades an untouched stored C# built-in to DSL
//      while leaving a user-edited entry alone (read-only built-in contract).
//
// AppDataPaths is redirected to a throwaway temp dir for the whole test process
// (see TestDataRootIsolation), so exercising the UserBulbStore singleton here
// never touches real user data. All cases live in one class so the shared
// singleton + file are mutated serially.

using System;
using System.IO;
using System.Text.Json;
using FracturingFog.Abstractions;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// Shares the region-library collection: UserBulbStore is a process-wide
// singleton persisting to one file, so these must not run in parallel with
// other classes that mutate it (e.g. MultiAssetImportTests).
[Collection(FractalRegionLibraryCollection.Name)]
public sealed class UserBulbBuiltinDslMigrationTests
{
    private static readonly string[] Extras = { "t" };

    private static void AssertParsesAndFinite(string source, string label)
    {
        var expr = SandboxBulbExpression.Parse(source, Extras);
        var env = expr.NewEnv();
        var v = expr.EvalStep(new Vec3(0.5, 0.3, -0.2), new Vec3(0.1, 0.0, 0.0), 1, env, new[] { 0.4 });
        Assert.True(double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z),
            $"[{label}] non-finite eval: {v.X},{v.Y},{v.Z}");
    }

    private static void AssertChainParsesAndFinite(System.Collections.Generic.List<UserBulbChainStep> chain, string label)
    {
        var parsed = SandboxBulbChain.Parse(chain, Extras);
        var env = parsed.NewEnv();
        var v = parsed.EvalStep(new Vec3(0.5, 0.3, -0.2), new Vec3(0.1, 0.0, 0.0), 1, env, new[] { 0.4 });
        Assert.True(double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z),
            $"[{label}] non-finite chain eval: {v.X},{v.Y},{v.Z}");
    }

    // ── 1. primitives + worked-example chains ───────────────────────────────

    [Fact]
    public void AllChainPrimitives_ParseAsDsl()
    {
        foreach (var p in UserBulbChainPrimitives.All)
            AssertParsesAndFinite(p.Source, $"primitive:{p.DefaultOutputName}");
    }

    [Fact]
    public void WorkedExampleChains_ParseAsDsl()
    {
        AssertChainParsesAndFinite(UserBulbChainPrimitives.MandelboxBulbHybrid(), "MandelboxBulbHybrid");
        AssertChainParsesAndFinite(UserBulbChainPrimitives.MengerBulbHybrid(), "MengerBulbHybrid");
        AssertChainParsesAndFinite(UserBulbChainPrimitives.KaleidoscopicIfsChain(), "KaleidoscopicIfsChain");
    }

    // ── 2. seeded built-ins are DSL + Sandbox-pinned ────────────────────────

    [Fact]
    public void SeededBuiltins_AreDslAndSandboxPinned()
    {
        var store = UserBulbStore.Instance;
        // Force a clean reseed so we inspect the shipped defaults.
        string file = AppDataPaths.Combine("userbulbs.json");
        if (File.Exists(file)) File.Delete(file);
        store.Load();

        Assert.NotEmpty(store.Equations);
        foreach (var e in store.Equations)
        {
            Assert.True(e.Settings?.Compiler == UserBulbCompilerKind.Sandbox,
                $"'{e.Name}' not pinned to the Sandbox compiler");

            if (e.Chain is { Count: > 0 })
                AssertChainParsesAndFinite(e.Chain, $"chain:{e.Name}");
            // The single-pass Source must also be valid DSL (fallback path).
            AssertParsesAndFinite(e.Source, $"source:{e.Name}");
        }
    }

    // ── 3. load-time migration of a stored file ─────────────────────────────

    [Fact]
    public void Migration_UpgradesUntouchedBuiltin_PreservesUserEdit()
    {
        var store = UserBulbStore.Instance;
        string file = AppDataPaths.Combine("userbulbs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        // A pre-Phase-2b file: one untouched raw-C# built-in, and one built-in
        // the user edited to their own C# (must be preserved verbatim).
        const string userEdited = "return Vec3.Pow(z, 3) + c; // my tweak";
        var seed = new[]
        {
            new UserBulbEntry { Name = "Mandelbulb p=8", Source = "return Vec3.Pow(z, 8) + c;" },
            new UserBulbEntry { Name = "Mandelbulb p=4", Source = userEdited },
        };
        File.WriteAllText(file, JsonSerializer.Serialize(seed, new JsonSerializerOptions { WriteIndented = true }));

        store.Load();

        var upgraded = store.GetByName("Mandelbulb p=8");
        Assert.NotNull(upgraded);
        Assert.Equal(UserBulbCompilerKind.Sandbox, upgraded!.Settings?.Compiler);
        Assert.DoesNotContain("Vec3.", upgraded.Source);           // no longer raw C#
        AssertParsesAndFinite(upgraded.Source, "migrated p=8");

        var edited = store.GetByName("Mandelbulb p=4");
        Assert.NotNull(edited);
        // User's own edit is untouched — not rewritten, not pinned.
        Assert.Equal(userEdited, edited!.Source);
        Assert.True(edited.Settings?.Compiler is null or not UserBulbCompilerKind.Sandbox);
    }
}
