// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using FracturingFog.Abstractions.Animation;
using FracturingFog.Abstractions.Assets;
using FracturingFog.Assets;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Asset Manager (Sub-goal A) data layer. Exercises the IAssetSource adapters +
/// registry against the real library singletons (data root is redirected to a
/// throwaway temp dir by <see cref="TestDataRootIsolation"/>, so these Save()s
/// never touch real user files). The three-pane view and zip bundler live in
/// UI.Avalonia (not referenced here); ExportJson — the per-asset half of the A3
/// bundle — is covered directly.
/// </summary>
/// <remarks>Joins the non-parallel <see cref="FractalRegionLibraryCollection"/>
/// because the adapters mutate + Save() the process-wide region / animation /
/// scene library singletons over one shared (redirected) data root. Without
/// serialisation these race the other singleton-mutating classes (e.g.
/// <see cref="SceneLibraryTests"/>) — the race surfaced intermittently as a
/// null scene after a fresh ImportJson.</remarks>
[Collection(FractalRegionLibraryCollection.Name)]
public sealed class AssetSourceTests
{
    [Fact]
    public void Registry_exposes_nine_sources_in_type_tree_order()
    {
        var sources = AssetSourceRegistry.All();

        Assert.Equal(9, sources.Count);

        // Order matches the AssetKind enum / left-pane type tree.
        var expected = new[]
        {
            AssetKind.Region, AssetKind.ColorTheme, AssetKind.Animation,
            AssetKind.UserEquation, AssetKind.SandboxEquation, AssetKind.UserBulb,
            AssetKind.SlideshowConfig, AssetKind.Watermark, AssetKind.Scene,
        };
        Assert.Equal(expected, sources.Select(s => s.Kind).ToArray());

        // Every source has a non-blank heading for the tree.
        Assert.All(sources, s => Assert.False(string.IsNullOrWhiteSpace(s.DisplayName)));
    }

    [Fact]
    public void Every_source_enumerates_without_throwing()
    {
        foreach (var src in AssetSourceRegistry.All())
        {
            var ex = Record.Exception(() => src.Enumerate().ToList());
            Assert.Null(ex);
        }
    }

    [Fact]
    public void Watermark_source_reflects_store_and_exports_valid_json()
    {
        const string name = "AM_Test_Watermark";
        UserWatermarkStore.Instance.Remove(name); // clean slate
        UserWatermarkStore.Instance.SaveWatermark(new WatermarkDef { Name = name, Text = "hello" });

        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.Watermark);

        // Enumerate surfaces the saved entry with a real (non-zero) size.
        var row = src.Enumerate().SingleOrDefault(d => d.Name == name);
        Assert.NotNull(row);
        Assert.True(row!.SizeOnDisk > 0);
        Assert.Equal(AssetKind.Watermark, row.Kind);

        // ExportJson (A3 per-asset bundle payload) round-trips back to the def.
        string? json = src.ExportJson(name);
        Assert.False(string.IsNullOrWhiteSpace(json));
        var back = JsonSerializer.Deserialize<WatermarkDef>(json!);
        Assert.Equal(name, back!.Name);
        Assert.Equal("hello", back.Text);

        // ExportJson for a missing name is null, not a throw.
        Assert.Null(src.ExportJson("no-such-watermark"));

        // Delete routes through the store's remove path.
        Assert.True(src.Delete(name));
        Assert.DoesNotContain(src.Enumerate(), d => d.Name == name);
    }

    [Fact]
    public void UserEquation_source_exports_named_entry_json()
    {
        const string name = "AM_Test_Equation";
        UserEquationStore.Instance.Remove(name);
        UserEquationStore.Instance.SaveEquation(name, "return z*z + c;");

        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.UserEquation);

        Assert.Contains(src.Enumerate(), d => d.Name == name);
        string? json = src.ExportJson(name);
        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.Contains(name, json!);

        Assert.True(src.Delete(name));
    }

    [Fact]
    public void ImportJson_round_trips_export_and_preserves_fields()
    {
        const string name = "AM_Import_Equation";
        var store = UserEquationStore.Instance;
        store.Remove(name);
        // Promoted=true is a field the store's SaveEquation() upsert does NOT
        // carry — proves import preserves the whole entry, not just name+source.
        store.SaveEquation(name, "return z*z*z + c;");
        store.SetPromoted(name, true);

        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.UserEquation);
        string json = src.ExportJson(name)!;

        // Wipe, then import the exported JSON back — should re-add.
        Assert.True(src.Delete(name));
        var added = src.ImportJson(json, overwrite: false);
        Assert.Equal(AssetImportStatus.Added, added.Status);
        Assert.Equal(name, added.Name);

        var back = store.GetByName(name);
        Assert.NotNull(back);
        Assert.Equal("return z*z*z + c;", back!.Source);
        Assert.True(back.Promoted); // full-fidelity round-trip

        src.Delete(name);
    }

    [Fact]
    public void ImportJson_skips_or_replaces_on_name_collision_by_flag()
    {
        const string name = "AM_Import_Collision";
        var store = UserWatermarkStore.Instance;
        store.Remove(name);
        store.SaveWatermark(new WatermarkDef { Name = name, Text = "original" });

        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.Watermark);

        // A bundle payload carrying the same name but different content.
        string incoming = JsonSerializer.Serialize(new WatermarkDef { Name = name, Text = "incoming" });

        // overwrite:false leaves the existing asset untouched.
        var skipped = src.ImportJson(incoming, overwrite: false);
        Assert.Equal(AssetImportStatus.SkippedExists, skipped.Status);
        Assert.Equal("original", store.GetByName(name)!.Text);

        // overwrite:true replaces it.
        var replaced = src.ImportJson(incoming, overwrite: true);
        Assert.Equal(AssetImportStatus.Replaced, replaced.Status);
        Assert.Equal("incoming", store.GetByName(name)!.Text);

        store.Remove(name);
    }

    [Fact]
    public void ImportJson_returns_failed_on_garbage_input()
    {
        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.UserEquation);

        Assert.Equal(AssetImportStatus.Failed, src.ImportJson("not json at all", overwrite: true).Status);
        Assert.Equal(AssetImportStatus.Failed, src.ImportJson("", overwrite: true).Status);
    }

    // ── Colour-theme built-ins ───────────────────────────────────────────────

    /// <summary>The ColorTheme node surfaces the whole curated built-in roster
    /// (ColorPalette.BuiltIns), not just user-saved data-driven themes. Built-ins
    /// are read-only, carry no eager thumbnail, and expose a lazy factory that
    /// rasterises a swatch PNG on demand — kept off the enumerate hot path.</summary>
    [Fact]
    public void ColorTheme_source_lists_builtins_readonly_with_lazy_swatch_factory()
    {
        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.ColorTheme);
        var rows = src.Enumerate().ToList();

        // At least every built-in surfaces (plus any user themes on top).
        Assert.True(rows.Count >= ColorPalette.BuiltIns.Count);

        string builtinName = ColorPalette.GetStaticName(ColorPalette.BuiltIns[0]);
        var row = rows.FirstOrDefault(d => d.Name == builtinName && d.ReadOnly);
        Assert.NotNull(row);

        // Read-only, no eager bytes, but a working lazy factory.
        Assert.True(row!.ReadOnly);
        Assert.Null(row.ThumbnailBytes);
        Assert.NotNull(row.ThumbnailFactory);

        byte[]? png = row.ThumbnailFactory!();
        Assert.NotNull(png);
        Assert.True(png!.Length > 0);
        // PNG magic number (‰PNG).
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);

        // Built-ins have no user-library entry: not deletable or exportable there.
        Assert.False(src.Delete(builtinName));
        Assert.Null(src.ExportJson(builtinName));
    }

    // ── Scene source (S5) ────────────────────────────────────────────────────

    /// <summary>The Scene node surfaces the built-in demos once the library is
    /// loaded, and ExportJson emits the nested S3 camera track with enum-as-string
    /// (not the plain shared options) so a hand-edited bundle round-trips.</summary>
    [Fact]
    public void Scene_source_enumerates_builtins_and_exports_camera_as_string_enums()
    {
        SceneLibrary.Instance.Load(); // seeds the built-in demos

        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.Scene);

        var row = src.Enumerate().SingleOrDefault(d => d.Name == "Mandelbulb Orbit");
        Assert.NotNull(row);
        Assert.Equal(AssetKind.Scene, row!.Kind);
        Assert.True(row.SizeOnDisk > 0);

        string? json = src.ExportJson("Mandelbulb Orbit");
        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.Contains("\"Cut\"", json!);      // SceneTransitionKind as a string name
        Assert.Contains("\"Keys\"", json!);     // the nested CameraTrack survived
        Assert.DoesNotContain("\"Transition\":0", json!);

        Assert.Null(src.ExportJson("no-such-scene"));
    }

    /// <summary>ImportJson keys on the entry's own Name and honours the overwrite
    /// flag, persisting through SceneLibrary — the round-trip preserves the
    /// nested camera track.</summary>
    [Fact]
    public void Scene_source_import_round_trips_and_respects_overwrite()
    {
        const string name = "AM_Test_Scene";
        var lib = SceneLibrary.Instance;
        lib.Load();
        lib.Remove(name); // clean slate

        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.Scene);

        var scene = new SceneData
        {
            Name = name,
            Category = "User",
            Shots = new List<SceneShot>
            {
                new SceneShot { FractalType = FractalType.Mandelbrot, DurationSeconds = 4.0 },
            },
        };
        string json = JsonSerializer.Serialize(scene, SceneLibrary.BuildJsonOptions());

        var added = src.ImportJson(json, overwrite: false);
        Assert.Equal(AssetImportStatus.Added, added.Status);
        Assert.Equal(name, added.Name);
        Assert.Equal(4.0, lib.GetByName(name)!.Shots[0].DurationSeconds, precision: 9);

        // Same name again with overwrite off → skipped, untouched.
        Assert.Equal(AssetImportStatus.SkippedExists, src.ImportJson(json, overwrite: false).Status);

        // overwrite on → replaced.
        Assert.Equal(AssetImportStatus.Replaced, src.ImportJson(json, overwrite: true).Status);

        Assert.True(src.Delete(name));
        Assert.DoesNotContain(src.Enumerate(), d => d.Name == name);
    }
}
