// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

// #994 (CB2 of #64) — the Command section's view model: generated rows, live
// validation, mode filtering, seed / copy / browse delegates.
public sealed class CommandBuilderViewModelTests
{
    private static CommandFlagRowViewModel Row(CommandBuilderViewModel vm, string flag)
        => vm.Groups.SelectMany(g => g.Rows).Single(r => r.Name == flag);

    [Fact]
    public void EveryNonModeFlag_HasExactlyOneRow()
    {
        var vm = new CommandBuilderViewModel();
        var rows = vm.Groups.SelectMany(g => g.Rows).Select(r => r.Name).ToList();
        var expected = BatchFlagCatalog.All.Where(s => !CommandComposer.IsModeFlag(s.Name)).Select(s => s.Name).ToList();
        Assert.Equal(expected.OrderBy(x => x), rows.OrderBy(x => x));
        Assert.DoesNotContain(BatchFlags.Mode, rows);
        Assert.DoesNotContain(BatchFlags.Remote, rows);
    }

    [Fact]
    public void EditingAValue_SelectsIt_AndRevalidates()
    {
        var vm = new CommandBuilderViewModel { SelectedShell = CommandShell.PowerShell };
        Assert.True(vm.HasError);                          // no source yet
        Row(vm, BatchFlags.Region).Value = "Seahorse Valley";
        Assert.True(Row(vm, BatchFlags.Region).IsSelected);
        Assert.True(vm.IsValid, vm.ValidationMessage);
        Assert.Contains("--region \"Seahorse Valley\"", vm.CommandText);   // Windows default: Command Prompt quoting
        Assert.Contains("output path", vm.Hint);           // --out is still the placeholder

        Row(vm, BatchFlags.Region).Value = "";             // clearing free text deselects
        Assert.False(Row(vm, BatchFlags.Region).IsSelected);
        Assert.True(vm.HasError);
    }

    [Fact]
    public void Checkbox_UsesTheDraftOrDefault_AndSwitchesHaveNoValue()
    {
        var vm = new CommandBuilderViewModel();
        var height = Row(vm, BatchFlags.ReliefHeight);
        Assert.Equal("", height.Value);                    // unselected: empty, default as watermark
        Assert.Equal("default 1", height.Placeholder);
        height.IsSelected = true;
        Assert.Equal("1", vm.Composer.ValueOf(BatchFlags.ReliefHeight));   // catalog default
        height.IsSelected = false;
        Assert.False(vm.Composer.IsSelected(BatchFlags.ReliefHeight));

        Row(vm, BatchFlags.Relief).IsSelected = true;
        Assert.Null(vm.Composer.ValueOf(BatchFlags.Relief));
    }

    [Fact]
    public void ChoiceRows_IgnoreValuesOutsideTheirChoices()
    {
        var vm = new CommandBuilderViewModel();
        var vt = Row(vm, BatchFlags.ViewTransform);
        vt.Value = "aces";
        Assert.Equal("aces", vm.Composer.ValueOf(BatchFlags.ViewTransform));
        vt.Value = "not-a-choice";
        Assert.Equal("aces", vm.Composer.ValueOf(BatchFlags.ViewTransform));
    }

    [Fact]
    public void ModeChange_HidesRowsAndGroupsThatDoNotApply()
    {
        var vm = new CommandBuilderViewModel();
        Assert.True(Row(vm, BatchFlags.AovExr).IsVisible);
        Assert.False(Row(vm, BatchFlags.Seconds).IsVisible);
        var video = vm.Groups.Single(g => g.Group == BatchFlagGroup.Video);
        Assert.False(video.IsVisible);

        vm.SelectedMode = vm.Modes.Single(m => m.Mode == BatchMode.Video);
        Assert.False(Row(vm, BatchFlags.AovExr).IsVisible);
        Assert.True(Row(vm, BatchFlags.Seconds).IsVisible);
        Assert.True(video.IsVisible);
        Assert.StartsWith("FracturingFog --batch --mode video", vm.CommandText);
    }

    [Fact]
    public void ModeValue_DrivesTheSelectingFlag()
    {
        var vm = new CommandBuilderViewModel { SelectedShell = CommandShell.PowerShell };
        Assert.False(vm.HasModeValue);
        vm.SelectedMode = vm.Modes.Single(m => m.Mode == BatchMode.Regrade);
        Assert.True(vm.HasModeValue);
        Assert.True(vm.ModeValueIsPath);
        vm.ModeValue = @"C:\renders\in file.exr";
        Assert.Contains(@"--regrade-exr ""C:\renders\in file.exr""", vm.CommandText);
        vm.IsRemote = true;
        Assert.False(vm.HasModeValue);
    }

    [Fact]
    public void RelationStatus_IsShownOnTheRow()
    {
        var vm = new CommandBuilderViewModel();
        Row(vm, BatchFlags.DofAperture).Value = "0.3";
        var ortho = Row(vm, BatchFlags.ReliefCameraOrtho);
        Assert.True(ortho.StatusIsWarning);
        Assert.Contains("conflicts with --dof-aperture", ortho.Status);
        Assert.False(ortho.IsEditable);

        var relief = Row(vm, BatchFlags.Relief);
        Assert.Contains("implied by --dof-aperture", relief.Status);
        Assert.False(relief.StatusIsWarning);
    }

    [Fact]
    public void SeedFromLive_PassesTheChosenSize_AndSurfacesGaps()
    {
        (int w, int h) asked = default;
        var vm = new CommandBuilderViewModel((w, h) =>
        {
            asked = (w, h);
            return new LiveCommandSeed(
                new[] { "--fractal", "Julia", "--x", "0", "--y", "0", "--zoom", "1", "--width", w.ToString(), "--height", h.ToString(), "--out", "<OUTPUT.png>" },
                new[] { "Stereo output (side-by-side or autostereogram)" });
        });
        Row(vm, BatchFlags.Width).Value = "640";
        vm.SeedFromLiveCommand.Execute().Subscribe();
        Assert.Equal((640, BatchDefaults.Height), asked);
        Assert.Equal("Julia", vm.Composer.ValueOf(BatchFlags.Fractal));
        Assert.True(vm.HasGaps);
        Assert.Contains("Stereo", vm.GapWarning);
        vm.ResetCommand.Execute().Subscribe();
        Assert.False(vm.HasGaps);
    }

    [Fact]
    public void Copy_SendsTheCommandText_AndFullExePathChangesTheLeader()
    {
        string? copied = null;
        var vm = new CommandBuilderViewModel(copy: t => copied = t);
        vm.CopyCommand.Execute().Subscribe();
        Assert.Equal(vm.CommandText, copied);
        Assert.StartsWith("FracturingFog --batch", vm.CommandText);
        vm.UseFullExePath = true;
        if (!string.IsNullOrEmpty(Environment.ProcessPath))
            Assert.StartsWith(ShellQuoting.Join(Environment.ProcessPath, Array.Empty<string>(), vm.SelectedShell), vm.CommandText);
    }

    [Fact]
    public async Task Browse_UsesSaveForOutputs_AndOpenForInputs()
    {
        var vm = new CommandBuilderViewModel
        {
            SavePathRequested = name => Task.FromResult<string?>(@"D:\out\" + name),
            OpenPathRequested = () => Task.FromResult<string?>(@"D:\in\beauty.exr"),
        };
        await Row(vm, BatchFlags.Out).BrowseCommand.Execute();
        Assert.Equal(@"D:\out\render.png", vm.Composer.ValueOf(BatchFlags.Out));
        Assert.False(vm.Composer.HasPlaceholderOutput);

        vm.SelectedMode = vm.Modes.Single(m => m.Mode == BatchMode.Relight);
        await vm.BrowseModeValueCommand.Execute();
        Assert.Equal(@"D:\in\beauty.exr", vm.Composer.ValueOf(BatchFlags.RelightFrom));
    }

    [Fact]
    public void Parked_FlagsAreListedInTheHint()
    {
        var vm = new CommandBuilderViewModel();
        Row(vm, BatchFlags.AovExr).IsSelected = true;
        vm.SelectedMode = vm.Modes.Single(m => m.Mode == BatchMode.Video);
        Assert.Contains("--aov-exr", vm.Hint);
        var exr = vm.Groups.Single(g => g.Group == BatchFlagGroup.Exr);
        Assert.Equal("", exr.Summary);   // the parked flag is not counted as set here
    }

    // ── Library pickers (#995) ───────────────────────────────────────────────

    private sealed class FakeLibraries
    {
        public readonly List<(BatchFlagSource source, FractalType? fractal)> Calls = new();
        public IReadOnlyList<string> Names(BatchFlagSource source, FractalType? fractal)
        {
            Calls.Add((source, fractal));
            return source switch
            {
                BatchFlagSource.Theme  => fractal == FractalType.Julia
                    ? new[] { "— Compatible —", "Julia Glow", "Fire", "Fire", " " }
                    : new[] { "— Compatible —", "Fire", "Ocean" },
                BatchFlagSource.Region => new[] { "Seahorse Valley", "Elephant Valley" },
                BatchFlagSource.Scene  => new[] { "Intro", "Outro" },
                BatchFlagSource.SlideshowConfig => new[] { "Default", "Night" },
                BatchFlagSource.RemoteConnection => throw new InvalidOperationException("vault locked"),
                _ => Array.Empty<string>(),
            };
        }
    }

    private static (CommandBuilderViewModel vm, FakeLibraries libs) WithLibraries()
    {
        var libs = new FakeLibraries();
        var vm = new CommandBuilderViewModel { NamesProvider = libs.Names };
        return (vm, libs);
    }

    [Fact]
    public void LibraryRows_AreEditableCombos_OthersArePlainText()
    {
        var (vm, _) = WithLibraries();
        Assert.True(Row(vm, BatchFlags.Theme).HasSuggestions);
        Assert.False(Row(vm, BatchFlags.Theme).IsPlainText);
        Assert.True(Row(vm, BatchFlags.Region).HasSuggestions);
        Assert.False(Row(vm, BatchFlags.X).HasSuggestions);
        Assert.True(Row(vm, BatchFlags.X).IsPlainText);
        Assert.False(Row(vm, BatchFlags.Quality).HasSuggestions);   // fixed choices stay a plain combo
    }

    [Fact]
    public void Themes_FollowTheFractal_WithoutHeadersBlanksOrDuplicates()
    {
        var (vm, libs) = WithLibraries();
        var theme = Row(vm, BatchFlags.Theme);
        Assert.Equal(new[] { "Fire", "Ocean" }, theme.Suggestions);           // default fractal: Mandelbrot
        Assert.Contains((BatchFlagSource.Theme, (FractalType?)FractalType.Mandelbrot), libs.Calls);

        Row(vm, BatchFlags.Fractal).Value = "Julia";
        Assert.Equal(new[] { "Julia Glow", "Fire" }, theme.Suggestions);
    }

    [Fact]
    public void Regions_AreNotFilteredByTheImplicitDefaultFractal()
    {
        var (vm, libs) = WithLibraries();
        _ = Row(vm, BatchFlags.Region).Suggestions;
        Assert.Contains((BatchFlagSource.Region, (FractalType?)null), libs.Calls);
        Row(vm, BatchFlags.Fractal).Value = "Julia";
        _ = Row(vm, BatchFlags.Region).Suggestions;
        Assert.Contains((BatchFlagSource.Region, (FractalType?)FractalType.Julia), libs.Calls);
    }

    [Fact]
    public void Names_AreCached_UntilReseeded()
    {
        var libs = new FakeLibraries();
        var vm = new CommandBuilderViewModel((w, h) => new LiveCommandSeed(new[] { "--x", "0" }, Array.Empty<string>()))
        {
            NamesProvider = libs.Names,
        };
        _ = Row(vm, BatchFlags.Theme).Suggestions;
        int before = libs.Calls.Count(c => c.source == BatchFlagSource.Theme);
        _ = Row(vm, BatchFlags.Theme).Suggestions;
        Assert.Equal(before, libs.Calls.Count(c => c.source == BatchFlagSource.Theme));
        vm.SeedFromLiveCommand.Execute().Subscribe();
        _ = Row(vm, BatchFlags.Theme).Suggestions;
        Assert.True(libs.Calls.Count(c => c.source == BatchFlagSource.Theme) > before);
    }

    [Fact]
    public void AValueNotInTheLibrary_IsAWarning()
    {
        var (vm, _) = WithLibraries();
        Row(vm, BatchFlags.X).Value = "0";
        Row(vm, BatchFlags.Y).Value = "0";
        Row(vm, BatchFlags.Zoom).Value = "1";
        var theme = Row(vm, BatchFlags.Theme);
        theme.Value = "Fire";
        Assert.False(theme.HasStatus);
        theme.Value = "fire";                                                 // batch lookup is case-insensitive
        Assert.False(theme.HasStatus);
        theme.Value = "Nope";
        Assert.True(theme.StatusIsWarning);
        Assert.Contains("'Nope' is not in the themes compatible with Mandelbrot", theme.Status);
        Assert.True(vm.IsValid);                                              // a warning, not a parse error
    }

    [Fact]
    public void AThrowingOrMissingProvider_GivesNoNames_AndNoWarning()
    {
        var (vm, _) = WithLibraries();
        vm.IsRemote = true;
        var conn = Row(vm, BatchFlags.Connection);
        Assert.Empty(conn.Suggestions);                                       // provider threw
        conn.Value = "box";
        Assert.False(conn.HasStatus);

        var bare = new CommandBuilderViewModel();
        Assert.Empty(Row(bare, BatchFlags.Theme).Suggestions);
    }

    [Fact]
    public void ModeValue_OffersSavedPresetsAndScenes_AndFlagsUnknownOnes()
    {
        var (vm, _) = WithLibraries();
        vm.SelectedMode = vm.Modes.Single(m => m.Mode == BatchMode.Slideshow);
        Assert.True(vm.ModeValueIsName);
        Assert.Equal(new[] { "Default", "Night" }, vm.ModeValueSuggestions);

        vm.SelectedMode = vm.Modes.Single(m => m.Mode == BatchMode.Scene);
        Assert.Equal(new[] { "Intro", "Outro" }, vm.ModeValueSuggestions);
        vm.ModeValue = "Missing";
        Assert.Contains("not in the saved scenes", vm.Hint);
        vm.ModeValue = "Intro";
        Assert.DoesNotContain("not in the saved scenes", vm.Hint);

        vm.SelectedMode = vm.Modes.Single(m => m.Mode == BatchMode.Regrade);
        Assert.False(vm.ModeValueIsName);
        Assert.True(vm.ModeValueIsPath);
    }

    // ── #996 row value checks, swatches, light headings ──────────────────────

    [Theory]
    [InlineData(BatchFlags.ReliefHeight, "abc", "expects a number")]
    [InlineData(BatchFlags.ReliefHeight, "0", "outside > 0")]
    [InlineData(BatchFlags.ReliefStrength, "1.5", "outside 0..1")]
    [InlineData(BatchFlags.LSystemDepth, "20", "outside 0..12")]            // a range the parser does not enforce
    [InlineData(BatchFlags.Denoise, "2.5", "expects a whole number")]
    [InlineData("--light1-pos", "1,2", "expects 3 comma-separated numbers")]
    [InlineData("--light1-cone", "10,95", "each value must be within 0..90")]
    [InlineData(BatchFlags.FogColor, "#GG0000", "expects a hex colour")]
    public void BadValues_GetARowNote(string flag, string value, string note)
    {
        var vm = new CommandBuilderViewModel();
        var row = Row(vm, flag);
        row.Value = value;
        Assert.Contains(note, row.Status);
        Assert.True(row.StatusIsWarning);
    }

    [Theory]
    [InlineData(BatchFlags.ReliefHeight, "2.5")]
    [InlineData(BatchFlags.Denoise, "3")]
    [InlineData("--light1-dir", "0.5, 1.2")]
    [InlineData(BatchFlags.FogColor, "0x80FF8800")]
    public void GoodValues_HaveNoValueNote(string flag, string value)
    {
        var vm = new CommandBuilderViewModel();
        var row = Row(vm, flag);
        row.Value = value;
        Assert.Equal("", row.ValueNote);
    }

    [Fact]
    public void ColourRows_PreviewTheirValue_OrTheDefault()
    {
        var vm = new CommandBuilderViewModel();
        var fog = Row(vm, BatchFlags.FogColor);
        Assert.True(fog.IsColor);
        Assert.Equal(Avalonia.Media.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), fog.SwatchColor);   // default white
        fog.Value = "#336699";
        Assert.Equal(Avalonia.Media.Color.FromArgb(0xFF, 0x33, 0x66, 0x99), fog.SwatchColor);
        fog.Value = "#80112233";
        Assert.Equal(Avalonia.Media.Color.FromArgb(0x80, 0x11, 0x22, 0x33), fog.SwatchColor);
        Assert.False(Row(vm, BatchFlags.FogDensity).IsColor);
        Assert.Equal(Avalonia.Media.Color.FromArgb(0xFF, 0xB0, 0xC8, 0xFF), Row(vm, "--light2-color").SwatchColor);   // cool fill default
    }

    [Fact]
    public void Lights_HaveOneHeadingPerLight()
    {
        var vm = new CommandBuilderViewModel();
        var lights = vm.Groups.Single(g => g.Group == BatchFlagGroup.Lights).Rows;
        var headed = lights.Where(r => r.HasSectionTitle).ToList();
        Assert.Equal(3, headed.Count);
        Assert.Equal(new[] { 1, 2, 3 }, headed.Select(r => r.Spec.LightNumber));
        Assert.StartsWith("Light 1", headed[0].SectionTitle);
        Assert.Equal("--light1-type", headed[0].Name);
    }

    [Fact]
    public void LightPosition_OnADirectionalLight_SaysWhatItNeeds()
    {
        var vm = new CommandBuilderViewModel();
        var pos = Row(vm, "--light3-pos");
        pos.Value = "0,0,3";
        Assert.Contains("needs --light3-type point|spot", pos.Status);
        Row(vm, "--light3-type").Value = "spot";
        Assert.DoesNotContain("needs", pos.Status);
    }

    // ── #997 --param row ─────────────────────────────────────────────────────

    [Fact]
    public void ParamRow_IsMultiLine_AndChecksEachLine()
    {
        var vm = new CommandBuilderViewModel
        {
            NamesProvider = (src, _) => src == BatchFlagSource.ParamKey ? new[] { "JuliaCRe", "JuliaCIm" } : Array.Empty<string>(),
        };
        var row = Row(vm, BatchFlags.Param);
        Assert.True(row.IsMultiLine);
        Assert.False(row.HasSuggestions);
        Assert.False(row.IsPlainText);
        Assert.Contains("JuliaCRe=-0.8", row.Placeholder);

        row.Value = "JuliaCRe=-0.8\r\njuliacim=0.1";
        Assert.Equal("", row.ValueNote);
        row.Value = "JuliaCRe=-0.8\nOops=1";
        Assert.Equal("line 2: unknown key 'Oops'", row.ValueNote);
        row.Value = "JuliaCRe -0.8";
        Assert.StartsWith("line 1: expects KEY=VALUE", row.ValueNote);

        var noKeys = new CommandBuilderViewModel();
        Row(noKeys, BatchFlags.Param).Value = "Anything=1";   // no key list: shape only
        Assert.Equal("", Row(noKeys, BatchFlags.Param).ValueNote);
    }

    // ── #999 shell, script, run ──────────────────────────────────────────────

    private static CommandBuilderViewModel Runnable()
    {
        var vm = new CommandBuilderViewModel();
        Row(vm, BatchFlags.X).Value = "0";
        Row(vm, BatchFlags.Y).Value = "0";
        Row(vm, BatchFlags.Zoom).Value = "1";
        return vm;
    }

    [Fact]
    public void ShellChoice_ChangesTheQuoting()
    {
        var vm = Runnable();
        Row(vm, BatchFlags.Theme).Value = "Fire 3D (PBR)";
        vm.SelectedShell = CommandShell.PowerShell;
        Assert.Contains("--theme \"Fire 3D (PBR)\"", vm.CommandText);   // same as cmd for everyday values
        vm.SelectedShell = CommandShell.Cmd;
        Assert.Contains("--theme \"Fire 3D (PBR)\"", vm.CommandText);
        vm.SelectedShell = CommandShell.Bash;
        Assert.Contains("--theme 'Fire 3D (PBR)'", vm.CommandText);
        Row(vm, BatchFlags.FogColor).Value = "#FF8800";
        Assert.Contains("--fog-color '#FF8800'", vm.CommandText);   // a bare # would start a comment
    }

    [Fact]
    public void Run_NeedsAValidCommandWithARealOutput()
    {
        var vm = Runnable();
        Assert.True(vm.IsValid);
        Assert.False(vm.CanRun);                                   // --out is the placeholder
        Row(vm, BatchFlags.Out).Value = @"C:\out\x.png";
        Assert.True(vm.CanRun);
        Row(vm, BatchFlags.Zoom).Value = "";                       // no longer valid
        Assert.False(vm.CanRun);
    }

    [Fact]
    public async Task Run_LaunchesTheExecutableWithTheArgumentList_AndReportsTheExit()
    {
        var vm = Runnable();
        Row(vm, BatchFlags.Out).Value = "o.png";
        vm.RunExecutable = () => "dotnet";                         // `dotnet --batch …` → a quick failure
        await vm.RunCommand.Execute();
        Assert.False(vm.IsRunning);
        Assert.StartsWith("Failed (exit code", vm.RunStatus);
        Assert.StartsWith("> ", vm.RunLog);
        Assert.True(vm.RunLog.Split('\n').Length > 1, vm.RunLog);   // the child's own output arrived
    }

    [Fact]
    public async Task SaveScript_WritesARunnableScriptForTheShell()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ff-cb7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vm = Runnable();
            vm.SelectedShell = CommandShell.Cmd;
            string? suggested = null;
            vm.ScriptPathRequested = (name, shell) => { suggested = name; return Task.FromResult<string?>(Path.Combine(dir, name)); };
            await vm.SaveScriptCommand.Execute();
            Assert.Equal("fracturing-fog-batch.cmd", suggested);
            string text = File.ReadAllText(Path.Combine(dir, suggested!));
            Assert.StartsWith("@echo off", text);
            Assert.Contains(vm.CommandText, text);
            Assert.StartsWith("Saved script:", vm.RunStatus);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
