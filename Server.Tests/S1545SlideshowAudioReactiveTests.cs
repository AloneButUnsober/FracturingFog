// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1545 — each saved slideshow stores its own Fractured Waves (audio-reactive) flag,
// but switching presets in the Slideshow dialog left the toggle at its last state:
// LoadWorkingIntoBindings reloaded every other field and not this one. Mutant: drop
// the `_audioReactive = _working.AudioReactive` load and the switch and OK tests fail
// (the notify test guards the RaisePropertyChanged lines instead).

using System;
using System.ComponentModel;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public class S1545SlideshowAudioReactiveTests
{
    private static SlideshowConfigFile TwoPresets() => new()
    {
        ActiveName = "Waves",
        Configs =
        {
            new SlideshowConfig { Name = "Waves", AudioReactive = true },
            new SlideshowConfig { Name = "Quiet", AudioReactive = false },
        },
    };

    [Fact]
    public void Switching_Presets_Shows_Each_Presets_Own_Flag()
    {
        var vm = new SlideshowSettingsViewModel(TwoPresets(), audioReactive: true);
        Assert.True(vm.AudioReactive);   // precondition: the active preset's flag

        vm.ActiveName = "Quiet";
        Assert.False(vm.AudioReactive);
        Assert.True(vm.TimingEnabled);

        vm.ActiveName = "Waves";
        Assert.True(vm.AudioReactive);
        Assert.False(vm.TimingEnabled);
    }

    [Fact]
    public void Switching_Presets_Notifies_The_Toggle()
    {
        var vm = new SlideshowSettingsViewModel(TwoPresets(), audioReactive: true);
        bool raised = false;
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SlideshowSettingsViewModel.AudioReactive)) raised = true;
        };
        vm.ActiveName = "Quiet";
        Assert.True(raised, "the toggle is not told the value changed");
    }

    [Fact]
    public void Ok_After_Switching_Keeps_The_Switched_Presets_Flag()
    {
        var vm = new SlideshowSettingsViewModel(TwoPresets(), audioReactive: true);
        vm.ActiveName = "Quiet";
        vm.OkCommand.Execute().Subscribe(_ => { });
        Assert.False(vm.AudioReactiveResult);
        Assert.False(vm.Result!.AudioReactive);
    }
}
