// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1637 — My scenes: Capture is the exact reverse of Apply (same pixels for every
// demo), the store round-trips every field, keeps entries it can't read, refuses
// built-in names, and the section VM saves / loads / renames / deletes through it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneSceneStoreTests : IDisposable
{
    private const int W = 240, H = 140;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FF.MathPlaneScenes." + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, MathPlaneSceneStore.FileName);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    public static IEnumerable<object[]> Demos() => MathPlanePresets.All.Select((_, i) => new object[] { i });

    private static uint[] Render(MathPlanePreset d)
    {
        var p = new FractalParameters();
        d.Apply(p);
        var calc = new MathPlaneCalculator(W, H)
        {
            CenterX = d.ViewWorldX, CenterY = -d.ViewWorldY, Zoom = d.Zoom, MaxIterations = 256, FractalParameters = p,
        };
        calc.Calculate(CancellationToken.None);
        return calc.ColorBuffer;
    }

    private MathPlaneSceneStore Fresh()
    {
        var s = new MathPlaneSceneStore(FilePath);
        s.Load();
        return s;
    }

    [Theory]
    [MemberData(nameof(Demos))]
    public void Capture_IsTheReverseOfApply(int i)
    {
        var d = MathPlanePresets.All[i];
        var p = new FractalParameters();
        d.Apply(p);
        var c = MathPlanePreset.Capture("mine", p, d.ViewWorldX, d.ViewWorldY, d.Zoom);
        Assert.Equal(d with { Name = "mine", Idea = "" }, c);
        Assert.Equal(Render(d), Render(c));
    }

    [Theory]
    [MemberData(nameof(Demos))]
    public void SaveThenLoad_KeepsEveryField(int i)
    {
        var d = MathPlanePresets.All[i] with { Name = "scene " + i, Idea = "note " + i };
        Fresh().SaveScene(d);
        var back = Fresh().GetByName("scene " + i);
        Assert.Equal(d, back);
        Assert.Equal(Render(d), Render(back!));
    }

    [Fact]
    public void BuiltInNames_AreRefused_InAnyCase()
    {
        var s = Fresh();
        string demo = MathPlanePresets.All[0].Name;
        Assert.Null(s.SaveScene(MathPlanePresets.All[0]));
        Assert.Null(s.SaveScene(MathPlanePresets.All[0] with { Name = "  " + demo.ToUpperInvariant() + " " }));
        Assert.Null(s.SaveScene(MathPlanePresets.All[0] with { Name = "   " }));
        Assert.Empty(s.Scenes);
        Assert.False(File.Exists(FilePath));

        s.SaveScene(MathPlanePresets.All[1] with { Name = "mine" });
        Assert.NotNull(s.Rename("mine", demo));
        Assert.Equal("mine", s.Scenes.Single().Name);
    }

    [Fact]
    public void SavingAnExistingName_Overwrites_CaseInsensitive()
    {
        var s = Fresh();
        s.SaveScene(MathPlanePresets.All[0] with { Name = "Mine" });
        s.SaveScene(MathPlanePresets.All[1] with { Name = "mine " });
        var only = Assert.Single(Fresh().Scenes);
        Assert.Equal("mine", only.Name);
        Assert.Equal(MathPlanePresets.All[1].Scene, only.Scene);
    }

    [Fact]
    public void Rename_RefusesAClash_AllowsACaseChange()
    {
        var s = Fresh();
        s.SaveScene(MathPlanePresets.All[0] with { Name = "a" });
        s.SaveScene(MathPlanePresets.All[1] with { Name = "b" });
        Assert.NotNull(s.Rename("a", "B"));
        Assert.Null(s.Rename("a", "A"));
        Assert.Null(s.Rename("b", "c"));
        Assert.Equal(new[] { "A", "c" }, Fresh().Scenes.Select(x => x.Name));
        Assert.True(s.Remove("c"));
        Assert.False(s.Remove("c"));
        Assert.Equal(new[] { "A" }, Fresh().Scenes.Select(x => x.Name));
    }

    [Fact]
    public void UnreadableEntries_SurviveASave()
    {
        Directory.CreateDirectory(_dir);
        // One good entry, one with an enum value this build doesn't know.
        File.WriteAllText(FilePath,
            "[{\"Name\":\"good\",\"Idea\":\"\",\"Scene\":\"point A 1 1\",\"Zoom\":0.5},"
            + "{\"Name\":\"future\",\"Idea\":\"\",\"Scene\":\"\",\"MapMode\":\"Hyperbolic\"}]");
        var s = Fresh();
        Assert.Equal("good", Assert.Single(s.Scenes).Name);
        Assert.Equal(1, s.UnreadableCount);
        s.SaveScene(MathPlanePresets.All[0] with { Name = "new" });
        string json = File.ReadAllText(FilePath);
        Assert.Contains("Hyperbolic", json);
        var again = Fresh();
        Assert.Equal(new[] { "good", "new" }, again.Scenes.Select(x => x.Name));
        Assert.Equal(1, again.UnreadableCount);
    }

    [Fact]
    public void EnumsAreStoredByName()
    {
        Fresh().SaveScene(MathPlanePresets.All.First(d => d.Backdrop == MathPlaneBackdrop.Julia) with { Name = "j" });
        string json = File.ReadAllText(FilePath);
        Assert.Contains("\"Julia\"", json);
    }

    [Fact]
    public void Section_SavesLoadsRenamesAndDeletes()
    {
        var store = Fresh();
        var view = new FractalViewState { FractalType = FractalType.MathPlane, Zoom = 0.5, CenterX = 0, CenterY = 0 };
        var vm = new MathPlaneSectionViewModel(view, () => { }, () => { }, store);
        Assert.Empty(vm.MyScenes);

        var demo = MathPlanePresets.All.First(d => d.Backdrop != MathPlaneBackdrop.None && d.ViewWorldX != 0);
        vm.LoadDemo(demo);

        // A built-in name is refused with a reason.
        vm.MySceneName = demo.Name;
        Assert.Null(vm.SaveMyScene());
        Assert.NotNull(vm.MySceneError);
        Assert.Empty(vm.MyScenes);

        vm.MySceneName = "my orbit";
        var saved = vm.SaveMyScene();
        Assert.NotNull(saved);
        Assert.Null(vm.MySceneError);
        Assert.Equal(demo with { Name = "my orbit", Idea = "" }, saved);
        Assert.Same(vm.SelectedMyScene, vm.MyScenes.Single());

        // Change everything, then load it back.
        vm.LoadDemo(MathPlanePresets.All.First(d => d.MapMode == MathPlaneMapMode.Matrix));
        vm.SelectedMyScene = vm.MyScenes.Single();
        vm.LoadMySceneCommand.Execute().Subscribe();
        var p = view.FractalParameters;
        Assert.Equal(saved, MathPlanePreset.Capture("my orbit", p, view.CenterX, -view.CenterY, view.Zoom));

        vm.MySceneName = "renamed";
        Assert.True(vm.RenameMyScene());
        Assert.Equal("renamed", Fresh().Scenes.Single().Name);

        Assert.True(vm.DeleteMyScene());
        Assert.Empty(vm.MyScenes);
        Assert.Empty(Fresh().Scenes);
        // The built-ins are untouched throughout.
        Assert.Equal(demo, MathPlanePresets.All.First(d => d.Name == demo.Name));
    }
}
