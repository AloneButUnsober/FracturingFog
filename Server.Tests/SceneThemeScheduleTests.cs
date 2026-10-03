// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1052 — scene shot colour themes: the shared SceneThemeSchedule rule (shot →
// region curated → fallback, optional rotation) that both live playback and the
// offline exporter now use.

using System;
using System.Collections.Generic;
using System.Linq;

using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class SceneThemeScheduleTests
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
        { "Fire", "Ice", "Lava", "Moss" };

    private static string? Canon(string name)
        => Known.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Precedence_ShotTheme_ThenFirstValidCurated_ThenFallback()
    {
        var curated = new List<string> { "Deleted", "Ice", "Lava" };

        Assert.Equal("Fire", SceneThemeSchedule.ThemeAt(new SceneShot { ThemeName = "fire" }, curated, Canon, "Hsv", 0));
        Assert.Equal("Ice", SceneThemeSchedule.ThemeAt(new SceneShot(), curated, Canon, "Hsv", 0));
        Assert.Equal("Hsv", SceneThemeSchedule.ThemeAt(new SceneShot(), new List<string> { "Gone" }, Canon, "Hsv", 0));
        Assert.Equal("Hsv", SceneThemeSchedule.ThemeAt(new SceneShot { ThemeName = "Gone" }, null, Canon, "Hsv", 0));
    }

    [Fact]
    public void Rotation_StepsThroughValidCuratedThemes_OnShotTime()
    {
        var shot = new SceneShot { RotateThemes = true, ThemeRotateSeconds = 2 };
        var curated = new List<string> { "Ice", "Gone", "Lava", "Moss" };

        Assert.Equal(new[] { "Ice", "Lava", "Moss" }, SceneThemeSchedule.Rotation(shot, curated, Canon));
        string At(double t) => SceneThemeSchedule.ThemeAt(shot, curated, Canon, "Hsv", t);
        Assert.Equal("Ice", At(0));
        Assert.Equal("Ice", At(1.99));
        Assert.Equal("Lava", At(2.0));
        Assert.Equal("Moss", At(5.5));
        Assert.Equal("Ice", At(6.0));     // wraps
    }

    [Fact]
    public void Rotation_StartsAtTheShotTheme_OrPutsItFirst()
    {
        var curated = new List<string> { "Ice", "Lava", "Moss" };

        var inList = new SceneShot { RotateThemes = true, ThemeName = "Lava" };
        Assert.Equal(new[] { "Lava", "Moss", "Ice" }, SceneThemeSchedule.Rotation(inList, curated, Canon));

        var outside = new SceneShot { RotateThemes = true, ThemeName = "Fire" };
        Assert.Equal(new[] { "Fire", "Ice", "Lava", "Moss" }, SceneThemeSchedule.Rotation(outside, curated, Canon));
    }

    [Fact]
    public void Rotation_NeedsTheFlag_AndTwoOrMoreThemes()
    {
        var curated = new List<string> { "Ice", "Lava" };
        Assert.Empty(SceneThemeSchedule.Rotation(new SceneShot { RotateThemes = false }, curated, Canon));
        Assert.Empty(SceneThemeSchedule.Rotation(new SceneShot { RotateThemes = true }, new List<string> { "Ice", "Gone" }, Canon));
        Assert.Equal("Ice", SceneThemeSchedule.ThemeAt(new SceneShot { RotateThemes = true },
            new List<string> { "Ice" }, Canon, "Hsv", 10));
    }

    [Theory]
    [InlineData(0.0, 0.01, 3, 0)]       // step clamped to the 0.25 s minimum
    [InlineData(0.3, 0.01, 3, 1)]
    [InlineData(-5, 2, 3, 0)]           // negative time → start
    [InlineData(double.NaN, 2, 3, 0)]
    [InlineData(4.1, double.NaN, 3, 1)] // non-finite step → default 3 s
    public void StepIndex_IsGuarded(double t, double seconds, int count, int expected)
        => Assert.Equal(expected, SceneThemeSchedule.StepIndex(t, seconds, count));

    [Fact]
    public void Plan_SamplesTheSameScheduleAsThemeAt()
    {
        var shot = new SceneShot { RotateThemes = true, ThemeRotateSeconds = 1.5 };
        var curated = new List<string> { "Ice", "Lava", "Moss" };
        var plan = SceneThemePlan.For(shot, curated, Canon, "Hsv");

        Assert.True(plan.Rotates);
        for (double t = 0; t < 10; t += 0.37)
            Assert.Equal(SceneThemeSchedule.ThemeAt(shot, curated, Canon, "Hsv", t), plan.At(t));
    }

    [Fact]
    public void EngineResolver_UsesRealLibraryNames_AndRegionCuration()
    {
        // Real built-in theme names (independent of any user library).
        var names = ColorPalette.GetPaletteNames().Take(3).ToList();
        Assert.Equal(3, names.Count);
        var region = new FractalRegion
        {
            Name = "R",
            CuratedThemes = new List<string> { "No Such Theme", names[1], names[2] },
        };

        var stat = SceneThemeResolver.Plan(new SceneShot(), region);
        Assert.Equal(new[] { names[1] }, stat.Names);                 // first VALID curated

        var rot = SceneThemeResolver.Plan(new SceneShot { RotateThemes = true, ThemeRotateSeconds = 1 }, region);
        Assert.Equal(new[] { names[1], names[2] }, rot.Names);
        Assert.Equal(names[2], rot.At(1.0));

        var none = SceneThemeResolver.Plan(new SceneShot(), (FractalRegion?)null);
        Assert.Equal(new[] { HsvPalette.Name }, none.Names);
    }

    [Fact]
    public void ShotRotationFields_RoundTripThroughTheEditorRow()
    {
        var (svc, _) = SceneEditorPickerRefreshTests.LibraryService.Create();
        var vm = new FracturingFog.UI.Avalonia.ViewModels.SceneEditorViewModel(svc);
        var row = vm.Shots.Single();
        row.Populate(new SceneShot { RotateThemes = true, ThemeRotateSeconds = 4.5, DurationSeconds = 5 });

        var shot = row.ToShot();
        Assert.True(shot.RotateThemes);
        Assert.Equal(4.5, shot.ThemeRotateSeconds);
    }

    [Fact]
    public void PreviewButton_TogglesToStop_AndStopClearsIt()
    {
        var (svc, _) = SceneEditorPickerRefreshTests.LibraryService.Create();
        var vm = new FracturingFog.UI.Avalonia.ViewModels.SceneEditorViewModel(svc);
        vm.AddShotCommand.Execute().Subscribe();
        var (a, b) = (vm.Shots[0], vm.Shots[1]);
        int previews = 0, stops = 0;
        vm.PreviewShotRequested += (_, _) => previews++;
        vm.StopPreviewRequested += (_, _) => stops++;

        a.PreviewCommand.Execute().Subscribe();
        Assert.True(a.IsPreviewing);
        Assert.Equal("■ Stop", a.PreviewButtonText);

        b.PreviewCommand.Execute().Subscribe();          // only one previews at a time
        Assert.False(a.IsPreviewing);
        Assert.True(b.IsPreviewing);

        b.PreviewCommand.Execute().Subscribe();          // second click = Stop
        Assert.False(b.IsPreviewing);
        Assert.Equal("Preview", b.PreviewButtonText);
        Assert.Equal((2, 1), (previews, stops));

        a.PreviewCommand.Execute().Subscribe();
        vm.StopPreviewCommand.Execute().Subscribe();     // toolbar Stop clears it too
        Assert.False(a.IsPreviewing);
    }
}
