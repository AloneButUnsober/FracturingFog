// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FracturingFog.Abstractions;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Audio;
using FracturingFog.Batch;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.UI.Avalonia.ViewModels.Animation;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1434 — audio recipe presets: the built-ins are well-formed against the
// animatable-param map, a recipe applies only what the type can animate, apply is
// in place and undoable, user presets persist tolerantly and never shadow a
// built-in, and `--audio-preset` resolves to the same bindings the picker applies.
public sealed class AudioRecipePresetTests
{
    private sealed class FakeModSource : IAudioModulationSource
    {
        public bool IsActive { get; set; } = true;
        public long BeatCount => 0;
        public long DownbeatCount => 0;
        public AudioModulationFrame Frame { get; set; }
        public AudioModulationFrame Sample() => Frame;
        public AudioModulationFrame SampleAt(double seconds) => Frame;
    }

    private static readonly FractalType[] AllTypes = (FractalType[])Enum.GetValues(typeof(FractalType));

    private static IEnumerable<AnimatableParamDescriptor> DescriptorsNamed(string name)
        => AllTypes.SelectMany(t => FractalAnimatableParamsMap.For(t))
                   .Where(d => d.ParamName == name && d.Kind != AnimatableParamKind.Complex);

    private static readonly object s_rxLock = new();
    private static bool s_rxReady;

    private static void EnsureRx()
    {
        lock (s_rxLock)
        {
            if (s_rxReady) return;
            try { ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp(); }
            catch (InvalidOperationException) { /* already initialised elsewhere */ }
            s_rxReady = true;
        }
    }

    private static (AudioModulationManager Mgr, FractalParameters P) Live(FractalType type)
    {
        EnsureRx();
        var p = new FractalParameters();
        var bus = new ParameterAnimationBus(() => { });
        var src = new FakeModSource();
        return (new AudioModulationManager(() => src, () => { }, () => p, () => type, () => bus), p);
    }

    // ── built-ins ──────────────────────────────────────────────────────────

    [Fact]
    public void BuiltIns_Are_WellFormed()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in AudioRecipeLibrary.BuiltIns)
        {
            Assert.True(names.Add(r.Name), $"duplicate recipe name {r.Name}");
            Assert.True(r.Name.All(c => c < 128), $"non-ASCII recipe name {r.Name}");
            Assert.True(r.IsBuiltIn);
            Assert.False(string.IsNullOrWhiteSpace(r.Description));
            Assert.NotEmpty(r.Bindings);
            Assert.Contains(AllTypes, t => r.AppliesTo(t));
            foreach (var b in r.Bindings)
            {
                var ds = DescriptorsNamed(b.ParamName).ToList();
                Assert.True(ds.Count > 0, $"{r.Name}: {b.ParamName} is not an animatable scalar of any type");
                Assert.True(b.Enabled);
                var m = b.Binding;
                Assert.True(double.IsFinite(m.OutMin) && double.IsFinite(m.OutMax) && m.OutMin < m.OutMax, $"{r.Name}/{b.ParamName} range");
                Assert.True(m.AttackSeconds >= 0 && m.ReleaseSeconds >= 0);
                foreach (var d in ds)
                {
                    if (m.Mode == AudioBindingMode.Position)
                        Assert.True(m.OutMin >= d.Min && m.OutMax <= d.Max,
                            $"{r.Name}/{b.ParamName}: {m.OutMin}..{m.OutMax} outside {d.Min}..{d.Max}");
                    // Wrap on a non-periodic target is a hard cut every lap (cookbook pitfall):
                    // only periodic targets (descriptor Wraps, or a full 0..360 degree angle).
                    if (m.Mode == AudioBindingMode.Rate && m.Edge == AudioRateEdge.Wrap)
                        Assert.True(d.Wraps || (d.Min == 0 && d.Max == 360),
                            $"{r.Name}/{b.ParamName}: Wrap on a non-periodic target");
                }
            }
            var skipped = new List<string>();
            var set = AudioRecipeLibrary.ApplySettings(r, new FractalParameters(), skipped);
            Assert.Empty(skipped);
            Assert.Equal(r.Settings.Count, set.Count);
        }
    }

    [Fact]
    public void Flow_Recipes_Turn_Morph_On_Because_Flow_Only_Shows_With_It()
    {
        foreach (var r in AudioRecipeLibrary.BuiltIns.Where(r => r.Bindings.Any(b => b.ParamName == "AcidWarpFlow")))
        {
            var p = new FractalParameters { AcidWarpMorph = false };
            AudioRecipeLibrary.ApplySettings(r, p);
            Assert.True(p.AcidWarpMorph, r.Name);
        }
        Assert.Equal(new[] { "AcidWarpMorph" },
            AudioRecipeLibrary.ImpliedSettings(AudioRecipeLibrary.Find("Acid Fog - Beat nudge")!.Bindings).Select(s => s.ParamName));
    }

    [Fact]
    public void A_Recipe_Applies_Only_What_The_Type_Can_Animate()
    {
        var spin = AudioRecipeLibrary.Find("spin with the music")!;   // case-insensitive
        Assert.True(spin.AppliesTo(FractalType.UserEquation));
        Assert.False(spin.AppliesTo(FractalType.Mandelbrot));
        Assert.Equal(new[] { "UserEquationRotationDegrees" }, spin.BindingsFor(FractalType.UserEquation).Select(b => b.ParamName));
        Assert.DoesNotContain(AudioRecipeLibrary.ForType(FractalType.Mandelbrot), r => r.Name == spin.Name);
        Assert.Contains(AudioRecipeLibrary.ForType(FractalType.AcidWarp), r => r.Name == "Acid Fog - Endless flow");
        // BindingsFor hands out copies: editing one never edits the built-in.
        spin.BindingsFor(FractalType.UserEquation)[0].Binding.OutMax = 999;
        Assert.Equal(120, spin.Bindings[0].Binding.OutMax);
    }

    [Fact]
    public void ApplySettings_Converts_And_Reports_What_It_Cannot_Set()
    {
        var r = new AudioRecipePreset
        {
            Settings =
            {
                new() { ParamName = "AcidWarpMorph", Value = "TRUE" },
                new() { ParamName = "DomainWarpStrength", Value = "0.25" },
                new() { ParamName = "NoSuchSetting", Value = "1" },
                new() { ParamName = "DomainWarpEnabled", Value = "maybe" },
                new() { ParamName = "DomainWarpFrequency", Value = "NaN" },
                new() { ParamName = "SlitCascadeWhichPathPattern", Value = "ramp" },
                new() { ParamName = "SlitCascadeWhichPathLevel", Value = "4" },
                new() { ParamName = "SlitCascadeWhichPathPattern", Value = "99" },   // parses, but no such member
            },
        };
        var p = new FractalParameters();
        var skipped = new List<string>();
        var set = AudioRecipeLibrary.ApplySettings(r, p, skipped);
        Assert.Equal(new[] { "AcidWarpMorph", "DomainWarpStrength", "SlitCascadeWhichPathPattern", "SlitCascadeWhichPathLevel" }, set);
        Assert.Equal(SlitCascadeWhichPathPattern.Ramp, p.SlitCascadeWhichPathPattern);
        Assert.Equal(4, p.SlitCascadeWhichPathLevel);
        Assert.True(p.AcidWarpMorph);
        Assert.Equal(0.25, p.DomainWarpStrength);
        Assert.False(p.DomainWarpEnabled);
        Assert.Equal(1.0, p.DomainWarpFrequency);
        Assert.Equal(4, skipped.Count);
    }

    // ── apply / undo on the live matrix ────────────────────────────────────

    [Fact]
    public void Replace_Turns_Off_Other_Bindings_And_Merge_Keeps_Them()
    {
        var (mgr, _) = Live(FractalType.AcidWarp);
        var freq = FractalAnimatableParamsMap.For(FractalType.AcidWarp).Single(d => d.ParamName == "AcidWarpFrequency");
        mgr.GetOrCreateBinding(freq);
        mgr.SetEnabled("AcidWarpFrequency", true);
        var flow = AudioRecipeLibrary.Find("Acid Fog - Endless flow")!.BindingsFor(FractalType.AcidWarp);

        Assert.Equal(new[] { "AcidWarpFlow" }, mgr.ApplyRecipe(flow, replace: false));
        Assert.Equal(new[] { "AcidWarpFlow", "AcidWarpFrequency" }, mgr.ActiveParamNames());

        mgr.ApplyRecipe(flow, replace: true);
        Assert.Equal(new[] { "AcidWarpFlow" }, mgr.ActiveParamNames());
        var b = mgr.ActiveBindings().Single().Binding;
        Assert.Equal((AudioBindingMode.Rate, AudioRateEdge.Wrap, AudioSignalKind.Rms, 0.05, 1.5, 0.8),
            (b.Mode, b.Edge, b.Source, b.OutMin, b.OutMax, b.ReleaseSeconds));
    }

    [Fact]
    public void Undo_Turns_Off_A_Binding_The_Recipe_Created()
    {
        var (mgr, _) = Live(FractalType.JulibrotPair);
        var before = mgr.ExportBindings();
        Assert.Empty(before);
        mgr.ApplyRecipe(AudioRecipeLibrary.Find("Fringes collapse on the beat")!.BindingsFor(FractalType.JulibrotPair), replace: false);
        Assert.Equal(new[] { "DualOrbitWhichPath" }, mgr.ActiveParamNames());
        mgr.RestoreBindings(before);
        Assert.Empty(mgr.ActiveParamNames());
        Assert.False(mgr.HasEnabledBindings);
    }

    [Fact]
    public void Apply_Writes_Into_The_Binding_The_Row_Holds_And_Undo_Restores_It()
    {
        var (mgr, p) = Live(FractalType.AcidWarp);
        var flowD = FractalAnimatableParamsMap.For(FractalType.AcidWarp).Single(d => d.ParamName == "AcidWarpFlow");
        var row = new AudioBindingRowViewModel(flowD, mgr);
        row.Gain = 0.5; row.Source = AudioSignalKind.High;
        var before = mgr.ExportBindings();
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        p.AcidWarpMorph = false;
        var picker = new AudioRecipePickerViewModel(mgr, FractalType.AcidWarp, new[] { row },
            AudioRecipePickerViewModel.SettingsOn(p, () => { }))
        { SelectedRecipe = AudioRecipeLibrary.Find("Acid Fog - Beat nudge") };
        picker.Apply();

        Assert.True(row.Enabled);                                   // the row's own binding object was written
        Assert.Equal(AudioSignalKind.BeatPulse, row.Source);
        Assert.Equal(1.0, row.Gain);
        Assert.Equal(AudioBindingMode.Rate, row.Mode);
        Assert.Contains(nameof(AudioBindingRowViewModel.Enabled), changed);
        Assert.Contains(nameof(AudioBindingRowViewModel.Source), changed);
        Assert.True(p.AcidWarpMorph);
        Assert.True(picker.CanUndo);

        picker.Undo();
        Assert.False(row.Enabled);
        Assert.Equal(AudioSignalKind.High, row.Source);
        Assert.Equal(0.5, row.Gain);
        Assert.False(p.AcidWarpMorph);
        Assert.False(picker.CanUndo);
        Assert.Equal(Snap(before), Snap(mgr.ExportBindings()));
    }

    private static string Snap(IEnumerable<AudioParamBinding> list)
        => string.Join(";", list.OrderBy(b => b.ParamName, StringComparer.Ordinal)
            .Select(b => $"{b.ParamName}:{b.Enabled}:{AudioBindSpec.Format(b)}"));

    // ── user presets ───────────────────────────────────────────────────────

    private static AudioRecipePreset Mine(string name) => new()
    {
        Name = name,
        Description = "test",
        Bindings =
        {
            new AudioParamBinding
            {
                ParamName = "AcidWarpFlow", Enabled = true,
                Binding = new AudioModulationBinding
                {
                    Source = AudioSignalKind.DownbeatPulse, Mode = AudioBindingMode.Rate, Edge = AudioRateEdge.PingPong,
                    OutMin = 0.1, OutMax = 2.5, Gain = 1.5, Bias = 0.1, Curve = AudioResponseCurve.Exp, Invert = true,
                    AttackSeconds = 0.02, ReleaseSeconds = 0.7,
                },
            },
            new AudioParamBinding { ParamName = "AcidWarpFrequency", Enabled = false },
        },
        Settings = { new() { ParamName = "AcidWarpMorph", Value = "true" } },
    };

    [Fact]
    public void User_Presets_Round_Trip_And_Never_Shadow_A_BuiltIn()
    {
        string name = "Test recipe " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Assert.True(AudioRecipeLibrary.TrySaveUser(Mine(name), out var err), err);
            var back = AudioRecipeLibrary.Find(name.ToUpperInvariant())!;
            Assert.False(back.IsBuiltIn);
            Assert.Equal(new[] { "AcidWarpFlow" }, back.Bindings.Select(b => b.ParamName));   // disabled ones are not saved
            Assert.Equal(Snap(Mine(name).Bindings.Take(1)), Snap(back.Bindings));
            Assert.Equal("AcidWarpMorph", back.Settings.Single().ParamName);

            var again = Mine(name); again.Bindings[0].Binding.OutMax = 3.0;
            Assert.True(AudioRecipeLibrary.TrySaveUser(again, out _));
            Assert.Single(AudioRecipeLibrary.LoadUser(), r => r.Name == name);
            Assert.Equal(3.0, AudioRecipeLibrary.Find(name)!.Bindings[0].Binding.OutMax);

            Assert.False(AudioRecipeLibrary.TrySaveUser(Mine("ACID FOG - ENDLESS FLOW"), out err));
            Assert.Contains("built-in", err);
            Assert.False(AudioRecipeLibrary.TrySaveUser(Mine("  "), out _));
            var none = Mine(name + "x"); none.Bindings.ForEach(b => b.Enabled = false);
            Assert.False(AudioRecipeLibrary.TrySaveUser(none, out _));
            Assert.False(AudioRecipeLibrary.DeleteUser("Spin with the music"));
            Assert.NotNull(AudioRecipeLibrary.Find("Spin with the music"));
        }
        finally { AudioRecipeLibrary.DeleteUser(name); }
        Assert.Null(AudioRecipeLibrary.Find(name));
    }

    [Fact]
    public void An_Unreadable_Entry_Is_Kept_On_Disk_And_Hides_Nothing_Else()
    {
        string path = AppDataPaths.Combine(AudioRecipeLibrary.FileName);
        string? keep = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                [ { "Name": "Future", "Bindings": [ { "ParamName": "X", "Enabled": true, "Binding": { "Source": "SomeFutureSignal" } } ] },
                  { "Name": "Readable", "Description": "ok", "Bindings": [ { "ParamName": "AcidWarpFlow", "Enabled": true, "Binding": { "Source": "Bass" } } ] },
                  { "Name": "Spin with the music", "Bindings": [] } ]
                """);
            var list = AudioRecipeLibrary.LoadUser();
            Assert.Equal(new[] { "Readable" }, list.Select(r => r.Name));   // a stored built-in name is ignored
            Assert.Equal(AudioSignalKind.Bass, list[0].Bindings[0].Binding.Source);

            Assert.True(AudioRecipeLibrary.TrySaveUser(Mine("Another"), out _));
            string json = File.ReadAllText(path);
            Assert.Contains("SomeFutureSignal", json);
            Assert.Contains("Readable", json);
            Assert.Contains("Another", json);
        }
        finally
        {
            if (keep != null) File.WriteAllText(path, keep); else File.Delete(path);
        }
    }

    [Fact]
    public void Picker_Save_Stores_This_Types_Enabled_Bindings_With_Their_Settings()
    {
        var (mgr, _) = Live(FractalType.AcidWarp);
        // A binding kept from another fractal type (the manager holds it across a type
        // switch) is not this type's, so it must not end up in the saved recipe.
        mgr.LoadBindings(new[] { new AudioParamBinding { ParamName = "DualOrbitWhichPath", Enabled = true } });
        mgr.ApplyRecipe(AudioRecipeLibrary.Find("Acid Fog - Endless flow")!.BindingsFor(FractalType.AcidWarp), replace: true);
        Assert.Equal(2, mgr.ExportBindings().Count(b => b.Enabled));
        var picker = new AudioRecipePickerViewModel(mgr, FractalType.AcidWarp, Array.Empty<AudioBindingRowViewModel>(), _ => null);
        string name = "Picker save " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            picker.SaveName = name;
            picker.Save();
            Assert.Equal(name, picker.SelectedRecipe?.Name);
            var saved = AudioRecipeLibrary.Find(name)!;
            Assert.Equal(new[] { "AcidWarpFlow" }, saved.Bindings.Select(b => b.ParamName));
            Assert.Equal("AcidWarpMorph", saved.Settings.Single().ParamName);

            picker.SaveName = "Acid Fog - Beat nudge";
            picker.Save();
            Assert.Contains("built-in", picker.StatusText);

            picker.SelectedRecipe = AudioRecipeLibrary.ForType(FractalType.AcidWarp).First(r => r.Name == name);
            picker.Delete();
            Assert.Null(AudioRecipeLibrary.Find(name));
        }
        finally { AudioRecipeLibrary.DeleteUser(name); }
    }

    [Fact]
    public void A_Fractal_With_No_Recipe_Still_Shows_The_Picker_So_The_First_Can_Be_Saved()
    {
        // Flame has audio binding rows (FlameGamma, FlameVibrancy) but no built-in
        // recipe: the picker must still show, with the hint, so Save can make one.
        var (mgr, p) = Live(FractalType.Flame);
        Assert.Empty(AudioRecipeLibrary.ForType(FractalType.Flame));
        var panel = new FractalParamsViewModel(FractalType.Flame, p, audioModulation: mgr);
        Assert.True(panel.HasAudioReactiveParams);
        Assert.True(panel.HasAudioRecipes);
        var picker = panel.AudioRecipes!;
        Assert.True(picker.HasNoRecipes);
        Assert.False(picker.HasRecipes);
        Assert.Null(picker.SelectedRecipe);

        string name = "First flame " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var gamma = panel.AudioDrivableParams.Single(r => r.ParamName == "FlameGamma");
            gamma.Source = AudioSignalKind.Bass;
            gamma.Enabled = true;
            picker.SaveName = name;
            picker.Save();
            Assert.Equal(name, picker.SelectedRecipe?.Name);
            Assert.True(picker.HasRecipes);
            Assert.False(picker.HasNoRecipes);
            Assert.Equal(new[] { "FlameGamma" }, AudioRecipeLibrary.Find(name)!.Bindings.Select(b => b.ParamName));
        }
        finally { AudioRecipeLibrary.DeleteUser(name); }
    }

    [Fact]
    public void Audio_Settings_Shows_The_Picker_Whenever_It_Has_Rows()
    {
        var (mgr, _) = Live(FractalType.Flame);
        var rows = mgr.DescriptorsForCurrentType().Select(d => new AudioBindingRowViewModel(d, mgr)).ToList();
        var picker = new AudioRecipePickerViewModel(mgr, FractalType.Flame, rows, _ => null);
        Assert.True(new AudioSettingsViewModel(new AudioSettings(), null, bindingRows: rows, audioRecipes: picker).HasAudioRecipes);
        Assert.False(new AudioSettingsViewModel(new AudioSettings(), null, audioRecipes: picker).HasAudioRecipes);   // nested: no matrix
    }

    [Fact]
    public void Enabling_Flow_Turns_Morph_On_From_Either_Binding_Matrix()
    {
        // #1691 — the Audio Settings rows use the same hook as the Fractured Waves
        // rows: only turning the AcidWarpFlow drive ON calls it.
        var (mgr, p) = Live(FractalType.AcidWarp);
        var rows = mgr.DescriptorsForCurrentType().Select(d => new AudioBindingRowViewModel(d, mgr)).ToList();
        int calls = 0;
        foreach (var r in rows) AudioBindingRowViewModel.TurnMorphOnWithFlow(r, () => calls++);
        rows.Single(r => r.ParamName == "AcidWarpFrequency").Enabled = true;
        Assert.Equal(0, calls);
        var flow = rows.Single(r => r.ParamName == "AcidWarpFlow");
        flow.Enabled = true;
        Assert.Equal(1, calls);
        flow.Enabled = false;
        Assert.Equal(1, calls);

        // The Fractured Waves panel wires it to its own Morph property.
        var p2 = new FractalParameters { AcidWarpMorph = false };
        var (mgr2, _) = Live(FractalType.AcidWarp);
        var panel = new FractalParamsViewModel(FractalType.AcidWarp, p2, audioModulation: mgr2);
        panel.AudioDrivableParams.Single(r => r.ParamName == "AcidWarpFlow").Enabled = true;
        Assert.True(p2.AcidWarpMorph);
    }

    // ── batch ──────────────────────────────────────────────────────────────

    [Fact]
    public void AudioPreset_Flag_Parses_And_Lies_Between_Region_And_AudioBind()
    {
        Assert.True(BatchOptions.TryParse(new[] { "--mode", "video", "--x", "0", "--y", "0", "--zoom", "1", "--out", "o",
            "--audio", "m.wav", "--audio-preset", "Spin with the music" }, 0, out var opts, out var err), err);
        Assert.Equal("Spin with the music", opts.AudioPresetName);

        var preset = AudioRecipeLibrary.Find(opts.AudioPresetName)!.BindingsFor(FractalType.JulibrotPair);
        Assert.Equal(new[] { "DualOrbitTrapAngle" }, preset.Select(b => b.ParamName));
        var region = new List<AudioParamBinding>
        {
            new() { ParamName = "DualOrbitTrapAngle", Enabled = true, Binding = new() { Source = AudioSignalKind.High } },
            new() { ParamName = "DualOrbitWhichPath", Enabled = true, Binding = new() { Source = AudioSignalKind.Mid } },
        };
        Assert.True(AudioBindSpec.TryParse("DualOrbitWhichPath=Bass,max=0.5", out var flag, out _));
        var merged = BatchAudioDrive.Merge(BatchAudioDrive.Merge(region, preset), new[] { flag });
        Assert.Equal(AudioSignalKind.Rms, merged.Single(b => b.ParamName == "DualOrbitTrapAngle").Binding.Source);   // preset over region
        Assert.Equal(AudioSignalKind.Bass, merged.Single(b => b.ParamName == "DualOrbitWhichPath").Binding.Source);  // flag over region
    }

    [Fact]
    public void Every_BuiltIn_Binding_Survives_The_AudioBind_Round_Trip()
    {
        // The Command builder writes a live preset's bindings as --audio-bind; each
        // must parse back to the same mapping.
        foreach (var b in AudioRecipeLibrary.BuiltIns.SelectMany(r => r.Bindings))
        {
            string spec = AudioBindSpec.Format(b);
            Assert.True(AudioBindSpec.TryParse(spec, out var back, out var err), $"{spec}: {err}");
            Assert.Equal(spec, AudioBindSpec.Format(back));
            Assert.Equal((b.Binding.Mode, b.Binding.Edge, b.Binding.Invert, b.Binding.Curve),
                (back.Binding.Mode, back.Binding.Edge, back.Binding.Invert, back.Binding.Curve));
        }
    }

    [Fact]
    public void Catalog_Lists_AudioPreset_As_A_Video_Flag_Needing_Audio()
    {
        var spec = BatchFlagCatalog.All.Single(f => f.Name == BatchFlags.AudioPreset);
        Assert.Equal(BatchFlagSource.AudioRecipe, spec.Source);
        Assert.Contains(BatchFlags.Audio, spec.Requires);
        Assert.NotNull(AudioRecipeLibrary.Find(spec.Example));
    }
}
