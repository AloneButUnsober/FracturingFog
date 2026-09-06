// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S10.10 (PaletteBuilder-Design.md, #392) — "looks" (scene colour
// scripts). A ramp + a material preset + palette-drawn lights, saved as one unit,
// plus a composer that derives a coherent look from any ramp. Deterministic → the
// colour parity twin.

using System.Collections.Generic;
using System.Linq;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class SceneLooksTests
{
    [Fact]
    public void Catalog_Is_NonEmpty_All_Valid_Unique_Names()
    {
        Assert.NotEmpty(SceneLooks.Catalog);
        Assert.All(SceneLooks.Catalog, look => Assert.True(look.IsValid, $"{look.Name} invalid"));
        var names = SceneLooks.Catalog.Select(l => l.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Gold_Is_Warm_And_Metallic()
    {
        var gold = SceneLooks.Catalog.Single(l => l.Name == "Gold");
        Assert.True(gold.Material.Metallic > 0.8f);
        Assert.True(gold.Material.Roughness < 0.4f);   // glossy
    }

    [Fact]
    public void FromRamp_HighChroma_Reads_More_Metallic_Than_Grey()
    {
        var goldish = new List<(byte, byte, byte)>
        {
            ((byte)40, (byte)24, (byte)4), ((byte)150, (byte)100, (byte)20), ((byte)230, (byte)180, (byte)70),
        };
        var grey = new List<(byte, byte, byte)>
        {
            ((byte)30, (byte)30, (byte)30), ((byte)120, (byte)120, (byte)120), ((byte)220, (byte)220, (byte)220),
        };
        var goldLook = SceneLooks.FromRamp("g", goldish);
        var greyLook = SceneLooks.FromRamp("s", grey);

        Assert.True(goldLook.Material.Metallic > greyLook.Material.Metallic);
        Assert.True(goldLook.Material.Roughness < greyLook.Material.Roughness);
        // Grey ramp → near-dielectric, matte.
        Assert.True(greyLook.Material.Metallic < 0.15f);
        Assert.True(greyLook.Material.Roughness > 0.7f);
    }

    [Fact]
    public void FromRamp_Lights_Are_Drawn_From_Ramp_Extremes()
    {
        var stops = new List<(byte, byte, byte)>
        {
            ((byte)20, (byte)10, (byte)40),    // darkest → sky
            ((byte)120, (byte)80, (byte)60),
            ((byte)245, (byte)235, (byte)210), // brightest → key
        };
        var look = SceneLooks.FromRamp("x", stops);
        Assert.Equal(((byte)245, (byte)235, (byte)210), look.Lighting.KeyTint);
        Assert.Equal(((byte)20, (byte)10, (byte)40), look.Lighting.SkyTint);
    }

    [Fact]
    public void FromRamp_Material_In_Range_And_Deterministic()
    {
        var stops = new List<(byte, byte, byte)>
        {
            ((byte)10, (byte)40, (byte)30), ((byte)80, (byte)150, (byte)110), ((byte)210, (byte)240, (byte)220),
        };
        var a = SceneLooks.FromRamp("j", stops);
        var b = SceneLooks.FromRamp("j", stops);
        Assert.Equal(a, b);
        Assert.InRange(a.Material.Roughness, 0f, 1f);
        Assert.InRange(a.Material.Metallic, 0f, 1f);
    }

    [Fact]
    public void Recolor_Keeps_Material_ReDerives_Lights()
    {
        var gold = SceneLooks.Catalog.Single(l => l.Name == "Gold");
        var newRamp = new List<(byte, byte, byte)>
        {
            ((byte)6, (byte)18, (byte)40), ((byte)40, (byte)100, (byte)160), ((byte)230, (byte)244, (byte)255),
        };
        var reskinned = SceneLooks.Recolor(gold, newRamp);

        // Material identity preserved (still "glossy metal").
        Assert.Equal(gold.Material, reskinned.Material);
        Assert.Equal(gold.Name, reskinned.Name);
        // Ramp + lights follow the new palette.
        Assert.Equal(newRamp, reskinned.Ramp);
        Assert.Equal(((byte)230, (byte)244, (byte)255), reskinned.Lighting.KeyTint);
        Assert.Equal(((byte)6, (byte)18, (byte)40), reskinned.Lighting.SkyTint);
    }

    [Fact]
    public void Emission_Hook_Defaults_Null_Except_Where_Authored()
    {
        var gold = SceneLooks.Catalog.Single(l => l.Name == "Gold");
        Assert.Null(gold.EmissionTint);      // S5 forward-hook unused by default
        var ember = SceneLooks.Catalog.Single(l => l.Name == "Ember");
        Assert.NotNull(ember.EmissionTint);  // authored glow
    }
}
