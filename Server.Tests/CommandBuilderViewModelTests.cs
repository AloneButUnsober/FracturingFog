// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FracturingFog.Batch;
using FracturingFog.Cli;
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
        var vm = new CommandBuilderViewModel();
        Assert.True(vm.HasError);                          // no source yet
        Row(vm, BatchFlags.Region).Value = "Seahorse Valley";
        Assert.True(Row(vm, BatchFlags.Region).IsSelected);
        Assert.True(vm.IsValid, vm.ValidationMessage);
        Assert.Contains("--region \"Seahorse Valley\"", vm.CommandText);
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
        var vm = new CommandBuilderViewModel();
        Assert.False(vm.HasModeValue);
        vm.SelectedMode = vm.Modes.Single(m => m.Mode == BatchMode.Regrade);
        Assert.True(vm.HasModeValue);
        Assert.True(vm.ModeValueIsPath);
        vm.ModeValue = @"C:\renders\in file.exr";
        Assert.Contains("--regrade-exr \"C:\\renders\\in file.exr\"", vm.CommandText);
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
                new[] { "Stereo / side-by-side (SBS) output" });
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
            Assert.StartsWith(BatchCommandBuilder.Token(Environment.ProcessPath) + " --batch", vm.CommandText);
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
}
