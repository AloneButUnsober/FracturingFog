// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #630 (A2 of #626) — user-authored categorical billiard theme. The editor's
// Categorical kind produces a ColorThemeData whose Stops are the per-gate
// palette, InSetColor is the trapped colour, and BilliardDrive selects the
// secondary modulation. DataDrivenColorThemes.Create instantiates a
// DataDrivenBilliard (an IBilliardColorMap) the ChaoticBilliardCalculator
// routes through. These tests lock:
//   • Create builds an IBilliardColorMap from a Categorical theme;
//   • MapBilliard honours the gate palette / trapped colour / each drive;
//   • Export -> Create round-trips the palette + drive;
//   • the theme (incl. BilliardDrive) survives JSON persistence — the #611/#613
//     store-schema trap the issue warns about.

using System.Collections.Generic;
using System.Text.Json;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class BilliardCategoricalThemeTests
{
    private const int Red   = unchecked((int)0xFFFF0000);
    private const int Green = unchecked((int)0xFF00FF00);
    private const int Blue  = unchecked((int)0xFF0000FF);
    private const int Trap  = unchecked((int)0xFF112233);

    private static ColorThemeData MakeData(BilliardDrive drive) => new ColorThemeData
    {
        Name = "Test Billiard",
        Category = "User",
        Description = "",
        Kind = ColorThemeKind.Categorical,
        BilliardDrive = drive,
        InSetColor = new InSetColorData(0x11, 0x22, 0x33) { A = 0xFF },
        Stops = new List<ColorStopData>
        {
            new() { Position = 0.0f, R = 0xFF, G = 0x00, B = 0x00, A = 0xFF }, // gate 0 red
            new() { Position = 0.5f, R = 0x00, G = 0xFF, B = 0x00, A = 0xFF }, // gate 1 green
            new() { Position = 1.0f, R = 0x00, G = 0x00, B = 0xFF, A = 0xFF }, // gate 2 blue
        },
    };

    [Fact]
    public void Create_Builds_IBilliardColorMap()
    {
        var map = DataDrivenColorThemes.Create(MakeData(BilliardDrive.Flat));
        Assert.NotNull(map);
        Assert.IsAssignableFrom<IBilliardColorMap>(map);
    }

    [Fact]
    public void Flat_Drive_Maps_Gate_To_Palette_And_Trapped_To_InSet()
    {
        var bil = (IBilliardColorMap)DataDrivenColorThemes.Create(MakeData(BilliardDrive.Flat))!;

        Assert.Equal(Red,   bil.MapBilliard(0, 3, 4, 100, 0.2f));
        Assert.Equal(Green, bil.MapBilliard(1, 3, 4, 100, 0.2f));
        Assert.Equal(Blue,  bil.MapBilliard(2, 3, 4, 100, 0.2f));
        // Gate wraps beyond palette size.
        Assert.Equal(Red,   bil.MapBilliard(3, 3, 4, 100, 0.2f));
        // Trapped (gate < 0) -> InSetColor.
        Assert.Equal(Trap,  bil.MapBilliard(-1, 3, 200, 100, 0.0f));
    }

    [Fact]
    public void BounceShade_Darkens_With_Bounces_But_Keeps_Trapped()
    {
        var bil = (IBilliardColorMap)DataDrivenColorThemes.Create(MakeData(BilliardDrive.BounceShade))!;

        int few  = bil.MapBilliard(0, 3, 0, 100, 0f);
        int many = bil.MapBilliard(0, 3, 200, 100, 0f);
        int rFew  = (few  >> 16) & 0xFF;
        int rMany = (many >> 16) & 0xFF;
        Assert.True(rMany < rFew, "more bounces should darken the gate colour");
        Assert.Equal(Trap, bil.MapBilliard(-1, 3, 200, 100, 0f));
    }

    [Fact]
    public void BounceCyclic_Cycles_Palette_By_Bounce()
    {
        var bil = (IBilliardColorMap)DataDrivenColorThemes.Create(MakeData(BilliardDrive.BounceCyclic))!;
        // bounce indexes the palette regardless of gate.
        Assert.Equal(Red,   bil.MapBilliard(2, 3, 0, 100, 0f));
        Assert.Equal(Green, bil.MapBilliard(2, 3, 1, 100, 0f));
        Assert.Equal(Blue,  bil.MapBilliard(2, 3, 2, 100, 0f));
        Assert.Equal(Red,   bil.MapBilliard(2, 3, 3, 100, 0f));   // wraps
    }

    [Fact]
    public void PathLength_Ramps_Gradient_Endpoints()
    {
        var bil = (IBilliardColorMap)DataDrivenColorThemes.Create(MakeData(BilliardDrive.PathLength))!;
        Assert.Equal(Red,  bil.MapBilliard(0, 3, 4, 100, 0.0f));   // ramp start = first stop
        Assert.Equal(Blue, bil.MapBilliard(0, 3, 4, 100, 1.0f));   // ramp end = last stop
    }

    [Fact]
    public void Export_Then_Create_RoundTrips_Drive_And_Palette()
    {
        var map = DataDrivenColorThemes.Create(MakeData(BilliardDrive.BounceCyclic))!;
        var data = DataDrivenColorThemes.Export(map);
        Assert.NotNull(data);
        Assert.Equal(ColorThemeKind.Categorical, data!.Kind);
        Assert.Equal(BilliardDrive.BounceCyclic, data.BilliardDrive);
        Assert.Equal(3, data.Stops.Count);

        var bil2 = (IBilliardColorMap)DataDrivenColorThemes.Create(data)!;
        Assert.Equal(Green, bil2.MapBilliard(0, 3, 1, 100, 0f));
        Assert.Equal(Trap,  bil2.MapBilliard(-1, 3, 1, 100, 0f));
    }

    [Fact]
    public void Json_RoundTrip_Preserves_Kind_Drive_And_Stops()
    {
        var data = MakeData(BilliardDrive.PathLength);
        var opts = UserColorThemeLibrary.BuildJsonOptions();

        string json = JsonSerializer.Serialize(data, opts);
        var back = JsonSerializer.Deserialize<ColorThemeData>(json, opts)!;

        Assert.Equal(ColorThemeKind.Categorical, back.Kind);
        Assert.Equal(BilliardDrive.PathLength, back.BilliardDrive);
        Assert.Equal(3, back.Stops.Count);
        Assert.NotNull(back.InSetColor);

        // And it still builds the same runtime behaviour after a persistence cycle.
        var bil = (IBilliardColorMap)DataDrivenColorThemes.Create(back)!;
        Assert.Equal(Red,  bil.MapBilliard(0, 3, 4, 100, 0.0f));
        Assert.Equal(Blue, bil.MapBilliard(0, 3, 4, 100, 1.0f));
    }
}
