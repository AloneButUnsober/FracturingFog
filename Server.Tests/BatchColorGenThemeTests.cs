// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1684 — batch loads the saved ColorGen library (colorgen.json) so --theme
// resolves a saved ColorGen theme, built with the same options as the live
// editor's Compile & Load. The Command builder reports a session theme that is
// not saved as loaded. Writes colorgen.json under the test data root, so it joins
// the store-writer collection.
[Collection(FractalRegionLibraryCollection.Name)]
public sealed class BatchColorGenThemeTests
{
    private const string Flat = "return rgb(0.25, 0.5, 1.0);";   // packs to exactly 0xFF4080FF

    [Fact]
    public void Options_Carry_Name_Description_Trap_Shape_And_Surround_Colour()
    {
        var o = ColorGenThemeLoader.Options("T", "d", "Cross", "80112233");
        Assert.Equal("T", o.ThemeName); Assert.Equal("User", o.Category); Assert.Equal("d", o.Description);
        Assert.Equal("Cross", o.TrapShape);
        Assert.Equal(0x80112233u, o.OutOfBoundsColor);
        var bare = ColorGenThemeLoader.Options("T", null, null, "");
        Assert.Equal("Point", bare.TrapShape); Assert.Null(bare.OutOfBoundsColor); Assert.Equal("", bare.Description);
        Assert.Null(ColorGenThemeLoader.ParseArgb("not hex"));
    }

    [Fact]
    public void Saved_Themes_Resolve_By_Name_And_A_Broken_One_Is_Reported()
    {
        var store = UserColorGenStore.Instance;
        store.Load();
        string good = "Batch CG 1684 flat", wave = "Batch CG 1684 wave", bad = "Batch CG 1684 broken";
        try
        {
            store.SaveEntry(good, Flat, "flat test", "Point", "FF010203");
            store.SaveEntry(wave, "return rgb(waveLevel, 0.0, 0.0);");
            store.SaveEntry(bad, "return rgb(0.1, ;");
            var failed = ColorGenThemeLoader.RegisterSaved();
            Assert.Contains(failed, f => f.Name == bad);
            Assert.DoesNotContain(failed, f => f.Name == good || f.Name == wave);

            var map = ColorPalette.GetPaletteByName(good);
            var cg = Assert.IsAssignableFrom<InterpretedColorMap>(map);
            Assert.Equal(good, cg.DisplayName);
            Assert.Equal("flat test", cg.DisplayDescription);
            Assert.Equal(0xFF010203u, ((IColorMap)cg).OutOfBoundsColor);
            Assert.Equal(unchecked((int)0xFF4080FFu), map.Map(3f, 0f, 10));      // the DSL's colour, by hand
            Assert.IsAssignableFrom<IWaveColorMap>(ColorPalette.GetPaletteByName(wave));   // #1593 themes too
            Assert.DoesNotContain(bad, ColorPalette.GetPaletteNames());
        }
        finally
        {
            store.Remove(good); store.Remove(wave); store.Remove(bad);
        }
    }

    [Fact]
    public void A_Session_Theme_Needs_Saving_Unless_Saved_As_Loaded()
    {
        const string name = "session-only theme 1684";
        Assert.False(SessionColorGenThemes.NeedsSaving("Not loaded 1684", null));   // not a session ColorGen theme
        SessionColorGenThemes.Add(name, Flat, null, "ff010203");
        Assert.True(SessionColorGenThemes.NeedsSaving(name, null));                 // never saved
        UserColorGenEntry Saved(string src = Flat, string shape = "Point", string oob = "FF010203")
            => new() { Name = name, Source = src, TrapShape = shape, OutOfBoundsColorArgb = oob };
        Assert.False(SessionColorGenThemes.NeedsSaving(name, Saved()));             // saved as loaded (hex case-insensitive)
        Assert.True(SessionColorGenThemes.NeedsSaving(name, Saved(src: "return rgb(0.0, 0.0, 0.0);")));   // edited since
        Assert.True(SessionColorGenThemes.NeedsSaving(name, Saved(shape: "Cross")));
        Assert.True(SessionColorGenThemes.NeedsSaving(name, Saved(oob: "")));
        Assert.False(SessionColorGenThemes.NeedsSaving(name, Saved(shape: "")));    // empty shape = Point
    }

    [Fact]
    public void Command_Builder_Asks_To_Save_An_Unsaved_ColorGen_Theme()
    {
        var p = new FractalParameters();
        var gaps = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { ThemeName = "Mine", ThemeColorGenUnsaved = true, Parameters = p });
        Assert.Contains(gaps, g => g.Contains("'Mine'") && g.Contains("Save it") && g.Contains("#1684"));
        var saved = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { ThemeName = "Mine", Parameters = p });
        Assert.DoesNotContain(saved, g => g.Contains("#1684"));
        var args = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot { ThemeName = "Mine", Parameters = p }).Args;
        Assert.Contains("Mine", args);   // a saved ColorGen theme is emitted as --theme
    }
}
