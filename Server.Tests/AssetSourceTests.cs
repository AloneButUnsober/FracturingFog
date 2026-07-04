using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

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
public sealed class AssetSourceTests
{
    [Fact]
    public void Registry_exposes_eight_sources_in_type_tree_order()
    {
        var sources = AssetSourceRegistry.All();

        Assert.Equal(8, sources.Count);

        // Order matches the AssetKind enum / left-pane type tree.
        var expected = new[]
        {
            AssetKind.Region, AssetKind.ColorTheme, AssetKind.Animation,
            AssetKind.UserEquation, AssetKind.SandboxEquation, AssetKind.UserBulb,
            AssetKind.SlideshowConfig, AssetKind.Watermark,
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
}
