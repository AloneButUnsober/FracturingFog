// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

// #980 (#939-C) — per-orbit layer settings: region persistence, colour-blind-safe
// defaults, and the params-panel view model (theme pickers, warning).
public sealed class DualOrbitLayerSettingsTests
{
    [Fact]
    public void LayerSettings_RoundTripThroughARegion()
    {
        var p = new FractalParameters
        {
            DualOrbitColorMode = DualOrbitColorMode.PerOrbitLayers,
            DualOrbitThemeZ = "Cividis", DualOrbitThemeC = "",   // "" = main theme must survive too
            DualOrbitLayerBlend = DualOrbitLayerBlend.Screen, DualOrbitOpacityZ = 0.4, DualOrbitOpacityC = 0.9,
        };
        var restored = new FractalParameters { DualOrbitThemeC = "something else" };
        RegionFractalParams.Snapshot(FractalType.JulibrotPair, p)!.ApplyTo(restored);
        Assert.Equal(DualOrbitColorMode.PerOrbitLayers, restored.DualOrbitColorMode);
        Assert.Equal("Cividis", restored.DualOrbitThemeZ);
        Assert.Equal("", restored.DualOrbitThemeC);
        Assert.Equal(DualOrbitLayerBlend.Screen, restored.DualOrbitLayerBlend);
        Assert.Equal(0.4, restored.DualOrbitOpacityZ);
        Assert.Equal(0.9, restored.DualOrbitOpacityC);
    }

    [Fact]
    public void Defaults_AreOmittedFromTheSnapshot()
    {
        // An all-default family snapshots to null (nothing to store) — that is omission too.
        var snap = RegionFractalParams.Snapshot(FractalType.JulibrotPair, new FractalParameters());
        if (snap is null) return;
        Assert.Null(snap.DualOrbitColorMode);
        Assert.Null(snap.DualOrbitThemeZ);
        Assert.Null(snap.DualOrbitThemeC);
        Assert.Null(snap.DualOrbitLayerBlend);
        Assert.Null(snap.DualOrbitOpacityZ);
        Assert.Null(snap.DualOrbitOpacityC);
    }

    [Fact]
    public void RegionRecall_ResetsLayerSettingsToDefaults()
    {
        var live = new FractalParameters
        {
            DualOrbitColorMode = DualOrbitColorMode.PerOrbitLayers, DualOrbitThemeZ = "Cividis",
            DualOrbitOpacityC = 0.05,
        };
        var region = new FractalRegion
        {
            Name = "t", FractalType = FractalType.JulibrotPair, CenterX = 0, CenterY = 0, Zoom = 1,
            Params = RegionFractalParams.Snapshot(FractalType.JulibrotPair, new FractalParameters()),
        };
        region.ApplyFamilyParams(live);
        var d = new FractalParameters();
        Assert.Equal(d.DualOrbitColorMode, live.DualOrbitColorMode);
        Assert.Equal(d.DualOrbitThemeZ, live.DualOrbitThemeZ);
        Assert.Equal(d.DualOrbitOpacityC, live.DualOrbitOpacityC);
    }

    private static double Hue(uint argb)
    {
        double r = ((argb >> 16) & 0xFF) / 255.0, g = ((argb >> 8) & 0xFF) / 255.0, b = (argb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d < 1e-9) return double.NaN;
        double h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    [Fact]
    public void DefaultLayerThemes_AreBuiltIn_Compatible_AndSplitOnTheBlueYellowAxis()
    {
        // Checked by colour, not name: the user is red/green colour-blind, so the two
        // layers must separate on the blue–yellow axis where the far field lands.
        var z = ColorPalette.GetPaletteByName(FractalParameters.DualOrbitDefaultThemeZ);
        var c = ColorPalette.GetPaletteByName(FractalParameters.DualOrbitDefaultThemeC);
        Assert.IsNotType<HsvPalette>(z);
        Assert.IsNotType<HsvPalette>(c);
        Assert.True(ColorPalette.IsCompatible(z, FractalType.JulibrotPair));
        Assert.True(ColorPalette.IsCompatible(c, FractalType.JulibrotPair));
        z.MaxIterations = c.MaxIterations = 256;
        foreach (float s in new[] { 1f, 2f, 4f, 6f })
        {
            double hz = Hue((uint)z.Map(s, 0f, 256)), hc = Hue((uint)c.Map(s, 0f, 256));
            Assert.InRange(hz, 190, 260);   // blue
            Assert.InRange(hc, 20, 65);     // amber / yellow
        }
    }

    // ── View model ───────────────────────────────────────────────────────────

    private static FractalParamsViewModel Vm(FractalParameters p, Func<IReadOnlyList<string>>? names)
        => new(FractalType.JulibrotPair, p, layerThemeNames: names);

    [Fact]
    public void MainThemeEntry_MapsToEmptyName_BothWays()
    {
        var p = new FractalParameters { DualOrbitThemeZ = "" };
        var vm = Vm(p, () => new[] { "Cividis" });
        Assert.Equal(FractalParamsViewModel.MainThemeLabel, vm.DualOrbitThemeZ);
        vm.DualOrbitThemeZ = "Cividis";
        Assert.Equal("Cividis", p.DualOrbitThemeZ);
        vm.DualOrbitThemeZ = FractalParamsViewModel.MainThemeLabel;
        Assert.Equal("", p.DualOrbitThemeZ);
        Assert.Equal(FractalParamsViewModel.MainThemeLabel, vm.LayerThemeNames[0]);
    }

    [Fact]
    public void HeaderRowSelection_IsIgnored()
    {
        var p = new FractalParameters { DualOrbitThemeC = "Cividis" };
        var vm = Vm(p, () => new[] { "— GradientLinear —", "Cividis" });
        vm.DualOrbitThemeC = "— GradientLinear —";
        Assert.Equal("Cividis", p.DualOrbitThemeC);
        Assert.Equal("Cividis", vm.DualOrbitThemeC);
    }

    [Fact]
    public void Warning_NamesAThemeMissingFromTheLibrary_OnlyInLayerMode()
    {
        var p = new FractalParameters { DualOrbitThemeZ = "Gone Theme", DualOrbitThemeC = "Cividis" };
        var vm = Vm(p, () => new[] { "— GradientLinear —", "Cividis" });
        Assert.False(vm.HasDualOrbitThemeWarning);          // Field mode: layers not in use
        vm.DualOrbitColorMode = DualOrbitColorMode.PerOrbitLayers;
        Assert.True(vm.IsDualOrbitLayers);
        Assert.True(vm.HasDualOrbitThemeWarning);
        Assert.Contains("'Gone Theme'", vm.DualOrbitThemeWarning);
        Assert.DoesNotContain("Cividis", vm.DualOrbitThemeWarning);
        vm.DualOrbitThemeZ = "Cividis";
        Assert.False(vm.HasDualOrbitThemeWarning);
    }

    [Fact]
    public void NoThemeList_NoWarning_AndTheListIsFetchedLazily()
    {
        var p = new FractalParameters { DualOrbitColorMode = DualOrbitColorMode.PerOrbitLayers, DualOrbitThemeZ = "Gone" };
        Assert.False(Vm(p, null).HasDualOrbitThemeWarning);   // nothing to check against

        int calls = 0;
        var vm = Vm(new FractalParameters(), () => { calls++; return new[] { "Cividis" }; });
        Assert.Equal(0, calls);                                // not touched in Field mode
        _ = vm.LayerThemeNames; _ = vm.LayerThemeNames;
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Opacities_ClampAndWriteThrough()
    {
        var p = new FractalParameters();
        var vm = Vm(p, null);
        vm.DualOrbitOpacityC = 1.7;
        vm.DualOrbitOpacityZ = -0.2;
        Assert.Equal(1.0, p.DualOrbitOpacityC);
        Assert.Equal(0.0, p.DualOrbitOpacityZ);
        vm.DualOrbitLayerBlend = DualOrbitLayerBlend.Multiply;
        Assert.Equal(DualOrbitLayerBlend.Multiply, p.DualOrbitLayerBlend);
    }
}
