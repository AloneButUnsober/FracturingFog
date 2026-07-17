// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using FracturingFog.Abstractions.Assets;
using FracturingFog.Assets;
using FracturingFog.Hosting;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// The single-asset import points widened to accept many entries per JSON file
/// (matching the regions / themes / sandbox importers). Covers both the new
/// array form and the pre-existing single-object form, which must keep working
/// — exported files in the wild are all single-object.
///
/// Runs under the test data-root redirect (TestDataRootIsolation), so the
/// stores these touch persist to a throwaway temp dir, never real user data.
/// Shares the region-library collection because UserBulbStore is another
/// process-wide save-on-mutate singleton.
/// </summary>
[Collection(FractalRegionLibraryCollection.Name)]
public sealed class MultiAssetImportTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string WriteTempJson(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-import-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }

    // ── Slideshow presets ─────────────────────────────────────────────────

    [Fact]
    public void SlideshowImport_ArrayOfPresets_ImportsEveryPreset()
    {
        string a = $"FF-Slide-A-{Guid.NewGuid():N}";
        string b = $"FF-Slide-B-{Guid.NewGuid():N}";
        string path = WriteTempJson($"[{{\"name\":\"{a}\"}},{{\"name\":\"{b}\"}}]");

        var file = SlideshowConfigLibrary.Load();
        var names = SlideshowConfigLibrary.Import(file, path);

        Assert.Equal(new[] { a, b }, names);
        Assert.Contains(file.Configs, c => c.Name == a);
        Assert.Contains(file.Configs, c => c.Name == b);
        // Last preset in the file wins the active slot.
        Assert.Equal(b, file.ActiveName);
    }

    [Fact]
    public void SlideshowImport_SinglePresetObject_StillImports()
    {
        string name = $"FF-Slide-Single-{Guid.NewGuid():N}";
        string path = WriteTempJson($"{{\"name\":\"{name}\"}}");

        var file = SlideshowConfigLibrary.Load();
        var names = SlideshowConfigLibrary.Import(file, path);

        Assert.Equal(new[] { name }, names);
        Assert.Contains(file.Configs, c => c.Name == name);
    }

    [Fact]
    public void SlideshowImport_NamelessEntriesDropped_DefaultNotClobbered()
    {
        string good = $"FF-Slide-Good-{Guid.NewGuid():N}";
        // A nameless element must not fall through to Upsert's "Default"
        // fallback and overwrite the user's Default preset.
        string path = WriteTempJson($"[{{\"name\":\"\"}},{{\"name\":\"{good}\"}}]");

        var file = SlideshowConfigLibrary.Load();
        int before = file.Configs.Count;
        var names = SlideshowConfigLibrary.Import(file, path);

        Assert.Equal(new[] { good }, names);
        Assert.Equal(before + 1, file.Configs.Count);
    }

    [Fact]
    public void SlideshowImport_MalformedFile_ReturnsEmpty()
    {
        string path = WriteTempJson("{ this is not json");
        var file = SlideshowConfigLibrary.Load();
        Assert.Empty(SlideshowConfigLibrary.Import(file, path));
    }

    // ── UserBulb equations ────────────────────────────────────────────────

    [Fact]
    public void UserBulbImport_ArrayOfSnapshots_ImportsEveryEntry()
    {
        string a = $"FF-Bulb-A-{Guid.NewGuid():N}";
        string b = $"FF-Bulb-B-{Guid.NewGuid():N}";
        string path = WriteTempJson(
            $"[{{\"Version\":1,\"Entry\":{{\"Name\":\"{a}\",\"Source\":\"z=z*z+c;\"}}}}," +
            $"{{\"Version\":1,\"Entry\":{{\"Name\":\"{b}\",\"Source\":\"z=z*z+c;\"}}}}]");

        var imported = UserBulbStore.Instance.ImportSnapshots(path);

        Assert.Equal(2, imported.Count);
        Assert.Equal(new[] { a, b }, imported.Select(s => s.Entry!.Name));
        Assert.NotNull(UserBulbStore.Instance.GetByName(a));
        Assert.NotNull(UserBulbStore.Instance.GetByName(b));
    }

    [Fact]
    public void UserBulbImport_ArrayOfLegacyBareEntries_ImportsEveryEntry()
    {
        string a = $"FF-Bulb-Legacy-A-{Guid.NewGuid():N}";
        string b = $"FF-Bulb-Legacy-B-{Guid.NewGuid():N}";
        string path = WriteTempJson(
            $"[{{\"Name\":\"{a}\",\"Source\":\"z=z*z+c;\"}}," +
            $"{{\"Name\":\"{b}\",\"Source\":\"z=z*z+c;\"}}]");

        var imported = UserBulbStore.Instance.ImportSnapshots(path);

        Assert.Equal(2, imported.Count);
        Assert.NotNull(UserBulbStore.Instance.GetByName(a));
        Assert.NotNull(UserBulbStore.Instance.GetByName(b));
    }

    [Fact]
    public void UserBulbImport_SingleSnapshotObject_StillImports()
    {
        string name = $"FF-Bulb-Single-{Guid.NewGuid():N}";
        string path = WriteTempJson(
            $"{{\"Version\":1,\"Entry\":{{\"Name\":\"{name}\",\"Source\":\"z=z*z+c;\"}}}}");

        var snapshot = UserBulbStore.Instance.ImportSnapshot(path);

        Assert.NotNull(snapshot);
        Assert.Equal(name, snapshot!.Entry!.Name);
        Assert.NotNull(UserBulbStore.Instance.GetByName(name));
    }

    [Fact]
    public void UserBulbImport_CollidingNames_RenamedNotOverwritten()
    {
        string name = $"FF-Bulb-Dup-{Guid.NewGuid():N}";
        string path = WriteTempJson(
            $"[{{\"Name\":\"{name}\",\"Source\":\"a\"}},{{\"Name\":\"{name}\",\"Source\":\"b\"}}]");

        var imported = UserBulbStore.Instance.ImportSnapshots(path);

        Assert.Equal(2, imported.Count);
        Assert.Equal(name, imported[0].Entry!.Name);
        // Second entry collides with the first and gets the suffix rename.
        Assert.Equal($"{name} (1)", imported[1].Entry!.Name);
        Assert.NotNull(UserBulbStore.Instance.GetByName($"{name} (1)"));
    }

    [Fact]
    public void UserBulbImport_MalformedFile_ReturnsEmpty()
    {
        string path = WriteTempJson("{ this is not json");
        Assert.Empty(UserBulbStore.Instance.ImportSnapshots(path));
    }

    // ── Colour themes: single-object form (Asset Manager per-row export) ───

    [Fact]
    public void ThemeImport_SingleThemeObject_RoundTripsAssetManagerExport()
    {
        // The Asset Manager's per-row ExportJson writes a bare theme object,
        // while ExportUserThemesToFile writes an array. The library importer
        // must read both or a single-theme export can't be imported back.
        string name = $"FF-Theme-Single-{Guid.NewGuid():N}";
        UserColorThemeLibrary.Instance.Load();
        UserColorThemeLibrary.Instance.ReplaceOrAdd(new ColorThemeData { Name = name });

        var source = new ColorThemeAssetSource();
        string? exported = source.ExportJson(name);
        Assert.NotNull(exported);

        // Drop it so the import has to re-add rather than skip as a duplicate.
        Assert.True(UserColorThemeLibrary.Instance.Remove(name));

        string path = WriteTempJson(exported!);
        var result = new HostColorThemeService().ImportThemesFromFile(path);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.Added);
        Assert.Contains(UserColorThemeLibrary.Instance.Themes, t => t.Name == name);
    }

    [Fact]
    public void ThemeImport_ArrayOfThemes_StillImports()
    {
        string a = $"FF-Theme-A-{Guid.NewGuid():N}";
        string b = $"FF-Theme-B-{Guid.NewGuid():N}";
        string path = WriteTempJson($"[{{\"Name\":\"{a}\"}},{{\"Name\":\"{b}\"}}]");

        var result = new HostColorThemeService().ImportThemesFromFile(path);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Added);
    }

    [Fact]
    public void ThemeImport_MalformedFile_ReportsError()
    {
        string path = WriteTempJson("{ this is not json");
        var result = new HostColorThemeService().ImportThemesFromFile(path);
        Assert.False(result.Success);
    }

    // ── Standalone JSON import for the kinds that only had the zip bundle ──
    //
    // The per-editor Import buttons route file text through AssetJsonFile.SplitEntries
    // and then the kind's own IAssetSource.ImportJson. The button wiring lives in
    // UI.Avalonia (not referenced here); this covers the pair that does the work.

    [Theory]
    [InlineData(AssetKind.Scene)]
    [InlineData(AssetKind.Animation)]
    [InlineData(AssetKind.Watermark)]
    [InlineData(AssetKind.UserEquation)]
    public void SplitEntries_ThenImportJson_LandsEveryEntryOfAnArray(AssetKind kind)
    {
        var source = AssetSourceRegistry.All().First(s => s.Kind == kind);
        string a = $"FF-{kind}-A-{Guid.NewGuid():N}";
        string b = $"FF-{kind}-B-{Guid.NewGuid():N}";

        var entries = AssetJsonFile.SplitEntries($"[{{\"Name\":\"{a}\"}},{{\"Name\":\"{b}\"}}]");
        Assert.Equal(2, entries.Count);

        foreach (var entry in entries)
            Assert.Equal(AssetImportStatus.Added, source.ImportJson(entry, overwrite: false).Status);

        var names = source.Enumerate().Select(d => d.Name).ToList();
        Assert.Contains(a, names);
        Assert.Contains(b, names);
    }

    [Theory]
    [InlineData(AssetKind.Scene)]
    [InlineData(AssetKind.Animation)]
    [InlineData(AssetKind.Watermark)]
    [InlineData(AssetKind.UserEquation)]
    public void SplitEntries_ThenImportJson_RoundTripsASingleAssetExport(AssetKind kind)
    {
        var source = AssetSourceRegistry.All().First(s => s.Kind == kind);
        string name = $"FF-{kind}-Single-{Guid.NewGuid():N}";
        Assert.Equal(AssetImportStatus.Added,
            source.ImportJson($"{{\"Name\":\"{name}\"}}", overwrite: false).Status);

        // What the editor's Export / Asset Manager row export would produce.
        string? exported = source.ExportJson(name);
        Assert.NotNull(exported);

        var entries = AssetJsonFile.SplitEntries(exported!);
        Assert.Single(entries);
        // Same name already present, so a no-overwrite re-import skips rather
        // than duplicating — proof the single-object form parsed and matched.
        Assert.Equal(AssetImportStatus.SkippedExists,
            source.ImportJson(entries[0], overwrite: false).Status);
        Assert.Equal(AssetImportStatus.Replaced,
            source.ImportJson(entries[0], overwrite: true).Status);
    }

    [Fact]
    public void SplitEntries_MalformedJson_ReturnsEmpty()
    {
        Assert.Empty(AssetJsonFile.SplitEntries("{ this is not json"));
        Assert.Empty(AssetJsonFile.SplitEntries("   "));
    }

    [Fact]
    public void SplitEntries_SingleObject_ReturnsTheDocumentUnchanged()
    {
        const string json = "{\"Name\":\"Solo\"}";
        var entries = AssetJsonFile.SplitEntries(json);
        Assert.Equal(new[] { json }, entries);
    }
}
