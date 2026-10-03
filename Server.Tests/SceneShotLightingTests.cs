// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1059 — scene shot lighting from Volumetric Lighting & FX presets (shared rule
// for live + export); #1060 — the shot Lighting "Edit…" request.

using System;
using System.Collections.Generic;
using System.Linq;

using FracturingFog.Abstractions.Animation;
using FracturingFog.Abstractions.Assets;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

using LibraryService = FracturingFog.Server.Tests.SceneEditorPickerRefreshTests.LibraryService;

namespace FracturingFog.Server.Tests;

public sealed class SceneShotLightingTests
{
    private static LightingFxData Base()
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.0;
        fx.Exposure = 1.0;
        return fx;
    }

    [Fact]
    public void UserPreset_ReplacesTheShotLighting()
    {
        var saved = LightingFxData.CreateDefault();
        saved.Exposure = 2.5;
        saved.FogDensity = 0.3;
        var file = new LightingFxPresetFile
        {
            Presets = { new LightingFxPreset { Name = "Studio", Data = LightingFxPresetData.FromFx(saved) } },
        };
        var shot = new SceneShot { LightingPresetName = "Studio" };

        Assert.True(SceneShotLighting.TryApplyPreset(shot, Base(), file, out var fx));
        Assert.Equal(saved.Exposure, fx.Exposure);
        Assert.Equal(saved.FogDensity, fx.FogDensity);
    }

    [Fact]
    public void BuiltInPreset_OverlaysLikeTheDialog()
    {
        var name = VolumetricFxPresets.All[0].Name;
        var shot = new SceneShot { LightingPresetName = name, LightingPresetIsBuiltIn = true };
        var current = Base();

        Assert.True(SceneShotLighting.TryApplyPreset(shot, current, null, out var fx));
        Assert.Equal(VolumetricFxPresets.ApplyByName(name, current), fx);
        Assert.NotEqual(current, fx);                       // the overlay changed something
        Assert.Equal(current.Exposure, fx.Exposure);        // …but only its fog subset
    }

    [Fact]
    public void MissingPreset_IsANoOp_AndUserNamesDontMatchBuiltIns()
    {
        var current = Base();
        var builtInName = VolumetricFxPresets.All[0].Name;

        Assert.False(SceneShotLighting.TryApplyPreset(new SceneShot { LightingPresetName = "Gone" },
            current, new LightingFxPresetFile(), out var fx1));
        Assert.Equal(current, fx1);
        // A user-kind pick with a built-in's name must not silently use the built-in.
        Assert.False(SceneShotLighting.TryApplyPreset(new SceneShot { LightingPresetName = builtInName },
            current, new LightingFxPresetFile(), out _));
        Assert.False(SceneShotLighting.TryApplyPreset(new SceneShot(), current, null, out _));
        Assert.False(SceneShotLighting.IsBuiltIn(VolumetricFxPresets.NoneName));
    }

    // ── Editor row ────────────────────────────────────────────────────────

    [Fact]
    public void LightingOptions_ListNone_BuiltIns_ThenUserPresetsAToZ()
    {
        var row = NewRow(new[] { "zeta", "Alpha" });

        Assert.Equal(SceneShotRowViewModel.LightingNone, row.LightingOptions[0]);
        var builtIns = VolumetricFxPresets.All.Select(p => SceneShotRowViewModel.LightingBuiltInPrefix + p.Name);
        Assert.Equal(builtIns, row.LightingOptions.Skip(1).Take(VolumetricFxPresets.All.Count));
        Assert.Equal(new[] { "Mine: Alpha", "Mine: zeta" }, row.LightingOptions.TakeLast(2));
    }

    [Theory]
    [InlineData("Built-in", true)]
    [InlineData("Mine", false)]
    public void LightingPick_RoundTripsThroughTheShot(string kind, bool builtIn)
    {
        string name = builtIn ? VolumetricFxPresets.All[1].Name : "Alpha";
        var row = NewRow(new[] { "Alpha" });
        row.SelectedLighting = $"{kind}: {name}";

        var shot = row.ToShot();
        Assert.Equal(name, shot.LightingPresetName);
        Assert.Equal(builtIn, shot.LightingPresetIsBuiltIn);
        Assert.Null(shot.LightingRegionName);

        var again = NewRow(new[] { "Alpha" });
        again.Populate(shot);
        Assert.Equal($"{kind}: {name}", again.SelectedLighting);
    }

    [Fact]
    public void LegacyLightingRegion_StaysSelectedAndSaved_UntilChanged()
    {
        var (svc, state) = LibraryService.Create();
        state.Regions = new() { "Old Look" };
        var vm = new SceneEditorViewModel(svc);
        var row = vm.Shots.Single();
        row.Populate(new SceneShot { LightingRegionName = "Old Look", DurationSeconds = 5 });

        Assert.Equal("Region (legacy): Old Look", row.SelectedLighting);
        Assert.Contains("Region (legacy): Old Look", row.LightingOptions);
        Assert.Equal("Old Look", row.ToShot().LightingRegionName);

        row.SelectedLighting = SceneShotRowViewModel.LightingBuiltInPrefix + VolumetricFxPresets.All[0].Name;
        var shot = row.ToShot();
        Assert.Null(shot.LightingRegionName);
        Assert.True(shot.LightingPresetIsBuiltIn);
    }

    [Fact]
    public void DeletedUserPreset_FallsBackToNone()
    {
        var row = NewRow(Array.Empty<string>());
        row.Populate(new SceneShot { LightingPresetName = "Gone", DurationSeconds = 5 });
        Assert.Equal(SceneShotRowViewModel.LightingNone, row.SelectedLighting);
        Assert.Null(row.ToShot().LightingPresetName);
    }

    [Fact]
    public void EditLighting_RaisesLightingFxEdit_WithTheBuiltInFlag()
    {
        var raised = new List<SceneEditAssetEventArgs>();
        var row = NewRow(new[] { "Alpha" }, raised.Add);
        string builtIn = VolumetricFxPresets.All[0].Name;

        row.EditLightingCommand.Execute().Subscribe();                      // none → just open
        row.SelectedLighting = "Mine: Alpha";
        row.EditLightingCommand.Execute().Subscribe();
        row.SelectedLighting = SceneShotRowViewModel.LightingBuiltInPrefix + builtIn;
        row.LightingMenu().Single(i => i.Header == "Edit preset…").Invoke!();

        Assert.Equal(new[]
        {
            new SceneEditAssetEventArgs(AssetKind.LightingFx, null, false),
            new SceneEditAssetEventArgs(AssetKind.LightingFx, "Alpha", false),
            new SceneEditAssetEventArgs(AssetKind.LightingFx, builtIn, true),
        }, raised);
    }

    private static SceneShotRowViewModel NewRow(IEnumerable<string> userPresets,
        Action<SceneEditAssetEventArgs>? onEdit = null)
    {
        var (svc, _) = LibraryService.Create();
        var source = new ScenePickerSource(svc, () => userPresets);
        return new SceneShotRowViewModel(source, Enum.GetValues<FracturingFog.FractalType>(),
            Enum.GetValues<SceneTransitionKind>(), () => { }, _ => { }, _ => { }, _ => { }, _ => { }, onEdit);
    }
}
