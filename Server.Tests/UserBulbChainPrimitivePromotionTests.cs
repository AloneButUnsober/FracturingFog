// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #535 — User Bulb chain primitives were hardcoded in the axaml. A user can now
// promote a saved single-source bulb equation into the "+ Primitive" menu. These
// cover the promotion plumbing:
//   1. UserBulbChainPrimitives.FromEntry maps a single-source entry and rejects a
//      chain-bearing / blank one,
//   2. PrimitiveStructuralError enforces suitability (single-source, references z),
//   3. AllWithUser appends the flagged user entries after the built-ins,
//   4. UserBulbStore.SetChainPrimitive round-trips through JSON.
//
// AppDataPaths is redirected to a throwaway temp dir for the whole test process
// (TestDataRootIsolation), so the store cases never touch real user data.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using FracturingFog.Abstractions;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class UserBulbChainPrimitivePromotionTests
{
    // ── 1. FromEntry ───────────────────────────────────────────────────────

    [Fact]
    public void FromEntry_MapsSingleSourceEntry()
    {
        var e = new UserBulbEntry { Name = "My Bulb p=5", Source = "z^5 + c" };
        var p = UserBulbChainPrimitives.FromEntry(e);

        Assert.NotNull(p);
        Assert.Equal("My Bulb p=5", p!.DisplayName);
        Assert.Equal("z^5 + c", p.Source);
        Assert.False(string.IsNullOrWhiteSpace(p.DefaultOutputName));
        // Derived id is short, lowercase, alphanumeric.
        Assert.True(p.DefaultOutputName.Length <= 12);
        Assert.All(p.DefaultOutputName, ch => Assert.True(char.IsLetterOrDigit(ch)));
    }

    [Fact]
    public void FromEntry_CarriesKifsScale()
    {
        var e = new UserBulbEntry
        {
            Name = "Fold",
            Source = "abs(z) * 3.0 - vec(2,2,0)",
            Settings = new UserBulbSnapshot { KifsScale = 3.0 },
        };
        var p = UserBulbChainPrimitives.FromEntry(e);
        Assert.NotNull(p);
        Assert.Equal(3.0, p!.KifsScale);
    }

    [Fact]
    public void FromEntry_RejectsChainAndBlank()
    {
        var chained = new UserBulbEntry
        {
            Name = "Chain",
            Source = "z^8 + c",
            Chain = new List<UserBulbChainStep> { new() { OutputName = "a", Source = "z^8 + c" } },
        };
        Assert.Null(UserBulbChainPrimitives.FromEntry(chained));

        var blank = new UserBulbEntry { Name = "Blank", Source = "   " };
        Assert.Null(UserBulbChainPrimitives.FromEntry(blank));

        Assert.Null(UserBulbChainPrimitives.FromEntry(null!));
    }

    // ── 2. PrimitiveStructuralError ─────────────────────────────────────────

    [Fact]
    public void StructuralError_OkForSingleSourceReferencingZ()
    {
        var e = new UserBulbEntry { Name = "ok", Source = "z^8 + c" };
        Assert.Null(UserBulbChainPrimitives.PrimitiveStructuralError(e));
    }

    [Fact]
    public void StructuralError_RejectsChainBlankAndNoZ()
    {
        Assert.NotNull(UserBulbChainPrimitives.PrimitiveStructuralError(null));

        var chained = new UserBulbEntry
        {
            Name = "c", Source = "z + c",
            Chain = new List<UserBulbChainStep> { new() { OutputName = "a", Source = "z + c" } },
        };
        Assert.NotNull(UserBulbChainPrimitives.PrimitiveStructuralError(chained));

        Assert.NotNull(UserBulbChainPrimitives.PrimitiveStructuralError(
            new UserBulbEntry { Name = "b", Source = "" }));

        // References no z → cannot compose in a chain.
        Assert.NotNull(UserBulbChainPrimitives.PrimitiveStructuralError(
            new UserBulbEntry { Name = "noz", Source = "vec(1,2,3) + c" }));
    }

    // ── 3. AllWithUser ──────────────────────────────────────────────────────

    [Fact]
    public void AllWithUser_AppendsFlaggedUserEntriesAfterBuiltins()
    {
        int builtinCount = UserBulbChainPrimitives.All.Count;
        var entries = new List<UserBulbEntry>
        {
            new() { Name = "Not promoted", Source = "z^2 + c", ChainPrimitive = false },
            new() { Name = "Promoted A", Source = "z^3 + c", ChainPrimitive = true },
            new() { Name = "Promoted chain (skipped)", Source = "z^4 + c", ChainPrimitive = true,
                    Chain = new List<UserBulbChainStep> { new() { OutputName = "x", Source = "z^4 + c" } } },
        };

        var all = UserBulbChainPrimitives.AllWithUser(entries);

        // Built-ins preserved at the front.
        Assert.Equal(builtinCount + 1, all.Count);
        for (int i = 0; i < builtinCount; i++)
            Assert.Equal(UserBulbChainPrimitives.All[i].DisplayName, all[i].DisplayName);
        // Only the single-source promoted entry is appended.
        Assert.Equal("Promoted A", all[^1].DisplayName);
    }

    [Fact]
    public void AllWithUser_NullEntries_IsBuiltinsOnly()
    {
        var all = UserBulbChainPrimitives.AllWithUser(null);
        Assert.Equal(UserBulbChainPrimitives.All.Count, all.Count);
    }

    // ── 4. Store round-trip ─────────────────────────────────────────────────

    [Collection(FractalRegionLibraryCollection.Name)]
    public sealed class StoreFlag
    {
        [Fact]
        public void SetChainPrimitive_PersistsThroughReload()
        {
            var store = UserBulbStore.Instance;
            string file = AppDataPaths.Combine("userbulbs.json");
            if (File.Exists(file)) File.Delete(file);
            store.Load();

            const string name = "#535 promote round-trip";
            store.SaveEquation(name, "z^7 + c");

            Assert.True(store.SetChainPrimitive(name, true));
            // Idempotent: already-set returns false.
            Assert.False(store.SetChainPrimitive(name, true));

            // Reload from disk and confirm the flag persisted.
            store.Load();
            var reloaded = store.GetByName(name);
            Assert.NotNull(reloaded);
            Assert.True(reloaded!.ChainPrimitive);

            Assert.Contains(UserBulbChainPrimitives.AllWithUser(store.Equations),
                p => p.DisplayName == name);

            // Cleanup so the shared singleton file doesn't leak the entry.
            store.SetChainPrimitive(name, false);
            store.Remove(name);
        }
    }
}
