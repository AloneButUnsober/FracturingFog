// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// LightingFxPresetLibraryTests.cs — #580.
//
// Covers the user Lighting & FX preset library + its Asset Manager adapter:
// save/load round-trip, upsert replace/add + active tracking, delete,
// export/import to a file, ParseOne validation, and the LightingFxAssetSource
// enumerate/delete/export/import path. The data root is redirected to a
// throwaway temp dir for the whole test process (TestDataRootIsolation), so
// these hit real disk harmlessly. Each test uses a unique preset name so the
// shared on-disk file can't cross-contaminate.

using System;
using System.IO;
using System.Linq;

using FracturingFog.Abstractions.Assets;
using FracturingFog.Assets;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class LightingFxPresetLibraryTests
{
    private static LightingFxPreset MakePreset(string name, double fogDensity, int volumeSteps) => new()
    {
        Name = name,
        Data = new LightingFxPresetData { FogDensity = fogDensity, VolumeSteps = volumeSteps },
    };

    [Fact]
    public void Upsert_then_Load_roundtrips_values_and_marks_active()
    {
        string name = "rt-" + Guid.NewGuid().ToString("N");
        var file = LightingFxPresetLibrary.Load();
        LightingFxPresetLibrary.Upsert(file, MakePreset(name, 0.42, 17));

        var reloaded = LightingFxPresetLibrary.Load();
        var got = LightingFxPresetLibrary.Get(reloaded, name);

        Assert.NotNull(got);
        Assert.Equal(0.42, got!.Data.FogDensity, 6);
        Assert.Equal(17, got.Data.VolumeSteps);
        Assert.Equal(name, reloaded.ActiveName);
    }

    [Fact]
    public void Upsert_replaces_same_name_without_duplicating()
    {
        string name = "dup-" + Guid.NewGuid().ToString("N");
        var file = LightingFxPresetLibrary.Load();
        LightingFxPresetLibrary.Upsert(file, MakePreset(name, 0.1, 4));
        LightingFxPresetLibrary.Upsert(file, MakePreset(name, 0.9, 40));

        var reloaded = LightingFxPresetLibrary.Load();
        Assert.Equal(1, reloaded.Presets.Count(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0.9, LightingFxPresetLibrary.Get(reloaded, name)!.Data.FogDensity, 6);
    }

    [Fact]
    public void Upsert_ignores_blank_name()
    {
        var file = LightingFxPresetLibrary.Load();
        int before = file.Presets.Count;
        LightingFxPresetLibrary.Upsert(file, MakePreset("   ", 0.5, 5));
        Assert.Equal(before, file.Presets.Count);
    }

    [Fact]
    public void Delete_removes_and_reports()
    {
        string name = "del-" + Guid.NewGuid().ToString("N");
        var file = LightingFxPresetLibrary.Load();
        LightingFxPresetLibrary.Upsert(file, MakePreset(name, 0.2, 8));

        Assert.True(LightingFxPresetLibrary.Delete(file, name));
        Assert.False(LightingFxPresetLibrary.Delete(file, name)); // already gone
        Assert.Null(LightingFxPresetLibrary.Get(LightingFxPresetLibrary.Load(), name));
    }

    [Fact]
    public void Export_then_Import_roundtrips_through_a_file()
    {
        string name = "exp-" + Guid.NewGuid().ToString("N");
        var file = LightingFxPresetLibrary.Load();
        LightingFxPresetLibrary.Upsert(file, MakePreset(name, 0.33, 12));

        string path = Path.Combine(Path.GetTempPath(), name + ".json");
        try
        {
            Assert.True(LightingFxPresetLibrary.Export(file, name, path));
            Assert.True(LightingFxPresetLibrary.Delete(file, name));

            var names = LightingFxPresetLibrary.Import(file, path);
            Assert.Contains(name, names);
            var got = LightingFxPresetLibrary.Get(LightingFxPresetLibrary.Load(), name);
            Assert.NotNull(got);
            Assert.Equal(12, got!.Data.VolumeSteps);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ParseOne_rejects_blank_and_nameless()
    {
        Assert.Null(LightingFxPresetLibrary.ParseOne(""));
        Assert.Null(LightingFxPresetLibrary.ParseOne("{\"data\":{}}"));       // no name
        Assert.Null(LightingFxPresetLibrary.ParseOne("not json"));
    }

    [Fact]
    public void Clone_is_deep_independent_of_source()
    {
        var data = new LightingFxPresetData { FogDensity = 0.5 };
        var preset = new LightingFxPreset { Name = "c", Data = data };
        var clone = preset.Clone();
        clone.Data.FogDensity = 0.99;
        Assert.Equal(0.5, data.FogDensity, 6); // original untouched
    }

    // ── Asset Manager adapter ─────────────────────────────────────────

    [Fact]
    public void AssetSource_enumerate_export_import_delete_roundtrip()
    {
        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.LightingFx);
        Assert.Equal("Lighting & FX", src.DisplayName);

        string name = "as-" + Guid.NewGuid().ToString("N");
        var file = LightingFxPresetLibrary.Load();
        LightingFxPresetLibrary.Upsert(file, MakePreset(name, 0.7, 22));

        // Enumerate sees it.
        Assert.Contains(src.Enumerate(), d => d.Name == name && d.Kind == AssetKind.LightingFx);

        // Export → delete → import restores it.
        string? json = src.ExportJson(name);
        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.True(src.Delete(name));

        var added = src.ImportJson(json!, overwrite: false);
        Assert.Equal(AssetImportStatus.Added, added.Status);

        // Re-import same name without overwrite → skipped; with overwrite → replaced.
        Assert.Equal(AssetImportStatus.SkippedExists, src.ImportJson(json!, overwrite: false).Status);
        Assert.Equal(AssetImportStatus.Replaced, src.ImportJson(json!, overwrite: true).Status);

        // Nameless JSON fails.
        Assert.Equal(AssetImportStatus.Failed, src.ImportJson("{\"data\":{}}", overwrite: true).Status);

        Assert.True(src.Delete(name));
    }
}
