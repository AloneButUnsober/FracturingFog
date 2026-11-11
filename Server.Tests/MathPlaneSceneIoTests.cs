// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1694 — My scenes import / export. Export then import gives back every field
// (and the same pixels); imports go through the ImportedJson safeguard (a UTF-16
// file, comments, trailing commas); unreadable entries are skipped without losing
// the readable ones; clashing names get a numbered suffix, never an overwrite, and
// a built-in demo's name is always renamed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneSceneIoTests : IDisposable
{
    private const int W = 200, H = 120;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FF.MathPlaneSceneIo." + Guid.NewGuid().ToString("N"));

    public MathPlaneSceneIoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private MathPlaneSceneStore Store(string name = "store")
    {
        var s = new MathPlaneSceneStore(Path.Combine(_dir, name, MathPlaneSceneStore.FileName));
        s.Load();
        return s;
    }

    private string FilePath(string name) => Path.Combine(_dir, name);

    private static uint[] Render(MathPlanePreset d)
    {
        var p = new FractalParameters();
        d.Apply(p);
        var calc = new MathPlaneCalculator(W, H)
        {
            CenterX = d.ViewWorldX, CenterY = -d.ViewWorldY, Zoom = d.Zoom, MaxIterations = 64, FractalParameters = p,
        };
        calc.Calculate(CancellationToken.None);
        return calc.ColorBuffer;
    }

    [Fact]
    public void ExportThenImport_GivesBackEveryDemo_FieldForField_AndPixelForPixel()
    {
        var mine = MathPlanePresets.All.Select(d => d with { Name = "my " + d.Name, Idea = "note" }).ToList();
        MathPlaneSceneStore.ExportFile(FilePath("all.json"), mine);
        var target = Store();
        var r = target.ImportFile(FilePath("all.json"));
        Assert.Null(r.Error);
        Assert.Equal(mine.Count, r.Added);
        Assert.Equal(0, r.Renamed + r.Skipped);
        Assert.Equal(mine, target.Scenes);
        foreach (var d in mine.Take(6)) Assert.Equal(Render(d), Render(target.GetByName(d.Name)!));
        // Persisted: a fresh load sees them.
        Assert.Equal(mine, Store().Scenes);
    }

    [Fact]
    public void ClashingNames_GetANumber_NeverOverwrite()
    {
        var target = Store();
        target.SaveScene(MathPlanePresets.All[0] with { Name = "Orbit" });
        var incoming = new[]
        {
            MathPlanePresets.All[1] with { Name = "orbit" },          // case-insensitive clash
            MathPlanePresets.All[2] with { Name = "Orbit" },          // clashes with both
            MathPlanePresets.All[3] with { Name = MathPlanePresets.All[5].Name },   // a built-in demo's name
            MathPlanePresets.All[4] with { Name = "  fresh  " },
        };
        MathPlaneSceneStore.ExportFile(FilePath("in.json"), incoming);
        var r = target.ImportFile(FilePath("in.json"));
        Assert.Equal(4, r.Added);
        Assert.Equal(3, r.Renamed);
        Assert.Equal(new[] { "Orbit", "orbit (2)", "Orbit (3)", MathPlanePresets.All[5].Name + " (2)", "fresh" },
                     target.Scenes.Select(s => s.Name));
        Assert.Equal(MathPlanePresets.All[0].Scene, target.GetByName("Orbit")!.Scene);   // the original is untouched
        Assert.Contains("3 renamed", r.Describe());
    }

    [Fact]
    public void UnreadableEntries_AreSkipped_TheRestImported()
    {
        File.WriteAllText(FilePath("mixed.json"),
            "[{\"Name\":\"good\",\"Idea\":\"\",\"Scene\":\"point A 1 1\",\"Zoom\":0.5},"
            + "{\"Name\":\"future\",\"Scene\":\"\",\"MapMode\":\"Hyperbolic\"},"
            + "{\"Idea\":\"no name\"},"
            + "42]");
        var target = Store();
        var r = target.ImportFile(FilePath("mixed.json"));
        Assert.Equal(1, r.Added);
        Assert.Equal(3, r.Skipped);
        Assert.Equal("good", Assert.Single(target.Scenes).Name);
        Assert.Contains("3 unreadable", r.Describe());
    }

    [Fact]
    public void ImportGoesThroughTheJsonSafeguard()
    {
        // UTF-16 with a BOM, a comment and a trailing comma: what an editor on another OS may save.
        string json = "[ // shared scenes\n" + MathPlaneSceneStore.ExportJson(new[] { MathPlanePresets.All[2] with { Name = "utf16" } })[1..^1] + ",\n]";
        File.WriteAllText(FilePath("u16.json"), json, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        var target = Store();
        var r = target.ImportFile(FilePath("u16.json"));
        Assert.Null(r.Error);
        Assert.Equal(1, r.Added);
        Assert.Equal(MathPlanePresets.All[2].Scene, target.GetByName("utf16")!.Scene);
    }

    [Fact]
    public void AWindows1252File_KeepsItsAccents()
    {
        // Saved as ANSI on Windows: "é" is the single byte 0xE9, not UTF-8. A plain UTF-8
        // read turns it into U+FFFD; the safeguard detects and decodes the code page.
        string json = "[{\"Name\":\"Café orbit\",\"Idea\":\"\",\"Scene\":\"point A 1 1\",\"Zoom\":0.5}]";
        File.WriteAllBytes(FilePath("ansi.json"), Encoding.Latin1.GetBytes(json));
        var target = Store();
        var r = target.ImportFile(FilePath("ansi.json"));
        Assert.Null(r.Error);
        Assert.Equal("Café orbit", Assert.Single(target.Scenes).Name);
    }

    [Fact]
    public void ASingleSceneObject_Imports()
    {
        string one = MathPlaneSceneStore.ExportJson(new[] { MathPlanePresets.All[1] with { Name = "solo" } }).Trim()[1..^1];
        File.WriteAllText(FilePath("one.json"), one);
        var target = Store();
        Assert.Equal(1, target.ImportFile(FilePath("one.json")).Added);
        Assert.Equal("solo", Assert.Single(target.Scenes).Name);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("\"just a string\"")]
    public void ABadFile_IsAnError_AndAddsNothing(string text)
    {
        File.WriteAllText(FilePath("bad.json"), text);
        var target = Store();
        target.SaveScene(MathPlanePresets.All[0] with { Name = "keep" });
        var r = target.ImportFile(FilePath("bad.json"));
        Assert.NotNull(r.Error);
        Assert.Equal(0, r.Added);
        Assert.Equal("keep", Assert.Single(Store().Scenes).Name);
    }

    [Fact]
    public void AMissingFile_IsAnError()
    {
        var r = Store().ImportFile(FilePath("nope.json"));
        Assert.Contains("Could not read", r.Error);
    }

    // ── Section view model ──────────────────────────────────────────────

    private MathPlaneSectionViewModel Vm(MathPlaneSceneStore store)
    {
        var view = new FractalViewState { FractalType = FractalType.MathPlane, Zoom = 0.5 };
        return new MathPlaneSectionViewModel(view, () => { }, () => { }, store);
    }

    [Fact]
    public void TheSection_ExportsSelectedOrAll_AndImportsWithAStatus()
    {
        var a = Store("a");
        a.SaveScene(MathPlanePresets.All[0] with { Name = "one" });
        a.SaveScene(MathPlanePresets.All[1] with { Name = "two" });
        var vm = Vm(a);

        Assert.False(vm.ExportMyScenesTo(FilePath("sel.json"), all: false));   // nothing selected
        Assert.NotNull(vm.MySceneError);

        vm.SelectedMyScene = vm.MyScenes[1];
        Assert.True(vm.ExportMyScenesTo(FilePath("sel.json"), all: false));
        Assert.True(vm.ExportMyScenesTo(FilePath("all.json"), all: true));
        Assert.Contains("Exported 2 scenes", vm.MySceneStatus);

        var b = Store("b");
        var vb = Vm(b);
        var r = vb.ImportMyScenesFrom(FilePath("sel.json"));
        Assert.Equal(1, r.Added);
        Assert.Equal("two", Assert.Single(vb.MyScenes).Name);
        Assert.Equal("two", vb.SelectedMyScene?.Name);
        vb.ImportMyScenesFrom(FilePath("all.json"));
        Assert.Equal(new[] { "two", "one", "two (2)" }, vb.MyScenes.Select(s => s.Name));
        Assert.Contains("1 renamed", vb.MySceneStatus);

        File.WriteAllText(FilePath("bad.json"), "nope");
        vb.ImportMyScenesFrom(FilePath("bad.json"));
        Assert.NotNull(vb.MySceneError);
        Assert.Equal(3, vb.MyScenes.Count);
    }

    [Fact]
    public void TheButtons_UseTheHostsPickers_AndCancelDoesNothing()
    {
        var a = Store("pick");
        a.SaveScene(MathPlanePresets.All[0] with { Name = "one" });
        var vm = Vm(a);
        string? asked = null;
        vm.PickSaveFile = (title, suggested) => { asked = suggested; return Task.FromResult<string?>(FilePath("picked.json")); };
        vm.ExportAllMyScenesCommand.Execute().Subscribe();
        Assert.Equal("mathplane-scenes.json", asked);
        Assert.True(File.Exists(FilePath("picked.json")));

        vm.PickOpenFile = _ => Task.FromResult<string?>(null);   // cancelled
        vm.ImportMyScenesCommand.Execute().Subscribe();
        Assert.Single(vm.MyScenes);

        vm.PickOpenFile = _ => Task.FromResult<string?>(FilePath("picked.json"));
        vm.ImportMyScenesCommand.Execute().Subscribe();
        Assert.Equal(new[] { "one", "one (2)" }, vm.MyScenes.Select(s => s.Name));
    }
}
