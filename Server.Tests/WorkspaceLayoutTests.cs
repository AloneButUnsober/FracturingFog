// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.IO;
using System.Linq;

using FracturingFog.Abstractions;
using FracturingFog.Abstractions.Assets;
using FracturingFog.Assets;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Window-arrangement workspace data layer (#433, slice 1/3 — #469): the
/// <see cref="WorkspaceLayoutLibrary"/> file gateway plus the
/// <see cref="WorkspaceAssetSource"/> Asset Manager adapter. Data root is
/// redirected to a throwaway temp dir by the shared fixture, so these Save()s
/// never touch real user files. Joins the same non-parallel collection as
/// <see cref="AssetSourceTests"/> because both mutate + Save() process-wide state
/// over one shared (redirected) data root.
/// </summary>
[Collection(FractalRegionLibraryCollection.Name)]
public sealed class WorkspaceLayoutTests
{
    private static WorkspaceLayout SampleLayout(string name) => new()
    {
        Name = name,
        RenderWindow = new RenderWindowState
        {
            Shape = RenderWindowShape.Toy,
            DisplayState = WindowDisplayState.Maximized,
            X = 100, Y = 120, Width = 1280, Height = 720,
            ResolutionName = "1080p",
            Topmost = true,
            AboveDialogs = true,
            Monitor = new MonitorRef { Index = 1, X = 1920, Y = 0, Width = 2560, Height = 1440 },
        },
        Satellites =
        {
            new SatelliteWindowState
            {
                Role = WindowRole.UserBulb, Visible = true,
                X = 50, Y = 60, Width = 760, Height = 520,
                Monitor = new MonitorRef { Index = 0, X = 0, Y = 0, Width = 1920, Height = 1080 },
            },
            new SatelliteWindowState
            {
                Role = WindowRole.MiniMap, Visible = false,
                X = 10, Y = 10, Width = 240, Height = 200,
            },
        },
    };

    // Give each test its own workspace file so a stray leftover cannot cross-talk.
    private static void ClearLibrary()
    {
        var file = WorkspaceLayoutLibrary.Load();
        foreach (var name in file.Layouts.Select(w => w.Name).ToList())
            WorkspaceLayoutLibrary.Delete(file, name);
    }

    [Fact]
    public void Empty_library_loads_without_default_entry()
    {
        ClearLibrary();
        var file = WorkspaceLayoutLibrary.Load();

        Assert.Empty(file.Layouts);
        Assert.Null(file.ActiveName);
        Assert.Null(WorkspaceLayoutLibrary.GetActive(file));
    }

    [Fact]
    public void Upsert_adds_marks_active_and_persists_across_load()
    {
        ClearLibrary();
        var file = WorkspaceLayoutLibrary.Load();

        WorkspaceLayoutLibrary.Upsert(file, SampleLayout("WS_Alpha"));

        // Re-load from disk proves the Save() landed.
        var reloaded = WorkspaceLayoutLibrary.Load();
        Assert.Single(reloaded.Layouts);
        Assert.Equal("WS_Alpha", reloaded.ActiveName);

        var active = WorkspaceLayoutLibrary.GetActive(reloaded);
        Assert.NotNull(active);
        Assert.Equal(RenderWindowShape.Toy, active!.RenderWindow.Shape);
        Assert.Equal(WindowDisplayState.Maximized, active.RenderWindow.DisplayState);
        Assert.True(active.RenderWindow.Topmost);
        Assert.True(active.RenderWindow.AboveDialogs);
        Assert.Equal("1080p", active.RenderWindow.ResolutionName);
        Assert.Equal(1, active.RenderWindow.Monitor!.Index);
        Assert.Equal(2, active.Satellites.Count);
        Assert.Equal(WindowRole.UserBulb, active.Satellites[0].Role);
        Assert.True(active.Satellites[0].Visible);

        ClearLibrary();
    }

    [Fact]
    public void Upsert_replaces_same_name_in_place()
    {
        ClearLibrary();
        var file = WorkspaceLayoutLibrary.Load();

        WorkspaceLayoutLibrary.Upsert(file, SampleLayout("WS_Dup"));
        var second = SampleLayout("WS_Dup");
        second.RenderWindow.Shape = RenderWindowShape.Span;
        WorkspaceLayoutLibrary.Upsert(file, second);

        var reloaded = WorkspaceLayoutLibrary.Load();
        Assert.Single(reloaded.Layouts);
        Assert.Equal(RenderWindowShape.Span, reloaded.Layouts[0].RenderWindow.Shape);

        ClearLibrary();
    }

    [Fact]
    public void Upsert_rejects_blank_name()
    {
        ClearLibrary();
        var file = WorkspaceLayoutLibrary.Load();

        WorkspaceLayoutLibrary.Upsert(file, SampleLayout(""));
        Assert.Empty(WorkspaceLayoutLibrary.Load().Layouts);
    }

    [Fact]
    public void Delete_removes_and_reassigns_active()
    {
        ClearLibrary();
        var file = WorkspaceLayoutLibrary.Load();
        WorkspaceLayoutLibrary.Upsert(file, SampleLayout("WS_One"));
        WorkspaceLayoutLibrary.Upsert(file, SampleLayout("WS_Two"));

        Assert.True(WorkspaceLayoutLibrary.Delete(file, "WS_Two"));
        Assert.False(WorkspaceLayoutLibrary.Delete(file, "no-such"));

        var reloaded = WorkspaceLayoutLibrary.Load();
        Assert.Single(reloaded.Layouts);
        Assert.Equal("WS_One", reloaded.ActiveName); // active fell back to survivor

        ClearLibrary();
    }

    [Fact]
    public void Export_then_import_round_trips_via_file()
    {
        ClearLibrary();
        var file = WorkspaceLayoutLibrary.Load();
        WorkspaceLayoutLibrary.Upsert(file, SampleLayout("WS_Export"));

        string path = Path.Combine(AppDataPaths.Root, "ws_export_test.json");
        Assert.True(WorkspaceLayoutLibrary.Export(file, "WS_Export", path));

        ClearLibrary();
        var empty = WorkspaceLayoutLibrary.Load();
        var names = WorkspaceLayoutLibrary.Import(empty, path);

        Assert.Equal(new[] { "WS_Export" }, names.ToArray());
        var back = WorkspaceLayoutLibrary.Get(WorkspaceLayoutLibrary.Load(), "WS_Export");
        Assert.NotNull(back);
        Assert.Equal(RenderWindowShape.Toy, back!.RenderWindow.Shape);
        Assert.Equal(2, back.Satellites.Count);

        try { File.Delete(path); } catch { }
        ClearLibrary();
    }

    // ── Asset source adapter ─────────────────────────────────────────────────

    [Fact]
    public void Asset_source_enumerates_and_exports_valid_json()
    {
        ClearLibrary();
        var file = WorkspaceLayoutLibrary.Load();
        WorkspaceLayoutLibrary.Upsert(file, SampleLayout("WS_Asset"));

        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.Workspace);

        var row = src.Enumerate().SingleOrDefault(d => d.Name == "WS_Asset");
        Assert.NotNull(row);
        Assert.True(row!.SizeOnDisk > 0);
        Assert.Equal(AssetKind.Workspace, row.Kind);

        string? json = src.ExportJson("WS_Asset");
        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.Contains("WS_Asset", json!);
        Assert.Null(src.ExportJson("no-such-workspace"));

        ClearLibrary();
    }

    [Fact]
    public void Asset_source_import_respects_overwrite_flag()
    {
        ClearLibrary();
        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.Workspace);

        // Seed one, export its JSON, then mutate the payload to prove overwrite.
        var file = WorkspaceLayoutLibrary.Load();
        WorkspaceLayoutLibrary.Upsert(file, SampleLayout("WS_Collide"));
        string json = src.ExportJson("WS_Collide")!;

        // Same name, overwrite off → skipped.
        Assert.Equal(AssetImportStatus.SkippedExists, src.ImportJson(json, overwrite: false).Status);

        // overwrite on → replaced.
        Assert.Equal(AssetImportStatus.Replaced, src.ImportJson(json, overwrite: true).Status);

        // Fresh name → added.
        var fresh = SampleLayout("WS_Fresh");
        string freshJson = System.Text.Json.JsonSerializer.Serialize(fresh);
        var added = src.ImportJson(freshJson, overwrite: false);
        Assert.Equal(AssetImportStatus.Added, added.Status);
        Assert.Equal("WS_Fresh", added.Name);

        Assert.True(src.Delete("WS_Fresh"));
        ClearLibrary();
    }

    [Fact]
    public void Asset_source_import_fails_on_garbage()
    {
        var src = AssetSourceRegistry.All().Single(s => s.Kind == AssetKind.Workspace);
        Assert.Equal(AssetImportStatus.Failed, src.ImportJson("not json", overwrite: true).Status);
        Assert.Equal(AssetImportStatus.Failed, src.ImportJson("", overwrite: true).Status);
        // Well-formed JSON but no name → failed (can't key the store).
        Assert.Equal(AssetImportStatus.Failed, src.ImportJson("{\"satellites\":[]}", overwrite: true).Status);
    }
}
