// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #962 (#941 S3) — animation lifecycle. An explicit stop restores the params the
// animation moved; a region recall stops animations WITHOUT restoring (the region's
// values win) and unticks the editor's Live Preview; re-pushing a preview mid-session
// keeps the original baseline. The bus timer is created but never ticks here (no
// Avalonia dispatcher loop), so "animation moved the param" is simulated by writing
// the param directly — the same write an animator makes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reactive.Linq;
using System.Reflection;

using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.UI.Avalonia.ViewModels.Animation;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AnimationLifecycleTests
{
    // ── AnimationBaseline (pure) ─────────────────────────────────────────────

    static AnimationData Anim(params string[] parms) => new()
    {
        Name = "t",
        Tracks = parms.Select(n => new AnimationTrack { ParamName = n, Mode = AnimationMode.Sine, Enabled = true }).ToList(),
    };

    [Fact]
    public void Baseline_restores_captured_values()
    {
        var p = new FractalParameters { JuliaC = new Complex(0.1, 0.2), DualOrbitCSeedX = 0.5 };
        var b = new AnimationBaseline();
        b.Begin(p);
        b.Capture(Anim("JuliaC", "DualOrbitCSeedX", "NoSuchParam"));
        Assert.Equal(2, b.CapturedParams.Count);

        p.JuliaC = new Complex(0.9, -0.9);
        p.DualOrbitCSeedX = 0.77;
        Assert.True(b.Restore());
        Assert.Equal(new Complex(0.1, 0.2), p.JuliaC);
        Assert.Equal(0.5, p.DualOrbitCSeedX);
        Assert.Null(b.Target);
    }

    [Fact]
    public void Baseline_capture_keeps_the_first_value_and_begin_discards()
    {
        var p = new FractalParameters { BulbPower = 8 };
        var b = new AnimationBaseline();
        b.Begin(p);
        b.Capture("BulbPower");
        p.BulbPower = 11;                 // animated
        b.Capture("BulbPower");           // re-push mid-session must not overwrite
        p.BulbPower = 12;
        b.Restore();
        Assert.Equal(8, p.BulbPower);

        b.Begin(p);
        b.Capture("BulbPower");
        p.BulbPower = 3;
        b.Begin(p);                       // authoritative change: drop, don't restore
        Assert.False(b.Restore());
        Assert.Equal(3, p.BulbPower);
    }

    // ── Animation Editor preview (AnimationBusHost) ──────────────────────────

    static AnimationEditorViewModel Editor(FractalParameters p)
    {
        AnimationBusHost.Initialize(() => { });
        var vm = new AnimationEditorViewModel(FakeService.Create(), p);
        vm.SelectedFractalType = FractalType.Julia;
        foreach (var row in vm.Tracks) row.Enabled = row.ParamName == "JuliaC";
        return vm;
    }

    [Fact]
    public void Live_preview_off_restores_what_the_preview_moved()
    {
        var p = new FractalParameters { JuliaC = new Complex(-0.8, 0.156) };
        var vm = Editor(p);
        vm.LivePreview = true;
        p.JuliaC = new Complex(0.3, 0.3);                       // animation moved it
        vm.LivePreview = false;
        Assert.Equal(new Complex(-0.8, 0.156), p.JuliaC);
    }

    [Fact]
    public void Re_pushing_the_preview_keeps_the_original_baseline()
    {
        var p = new FractalParameters { JuliaC = new Complex(-0.4, 0.6) };
        var vm = Editor(p);
        vm.LivePreview = true;
        p.JuliaC = new Complex(0.1, 0.1);
        vm.Tracks.First(r => r.ParamName == "JuliaC").FrequencyHz = 0.37;   // edit → re-push
        p.JuliaC = new Complex(0.2, 0.2);
        vm.LivePreview = false;
        Assert.Equal(new Complex(-0.4, 0.6), p.JuliaC);
    }

    [Fact]
    public void Region_jump_unticks_live_preview_without_restoring()
    {
        var p = new FractalParameters { JuliaC = new Complex(-0.4, 0.6) };
        var vm = Editor(p);
        vm.LivePreview = true;
        p.JuliaC = new Complex(0.1, 0.1);

        // what ShellViewModel.JumpToRegion does: authoritative recall, new session
        p.JuliaC = new Complex(0.285, 0.01);
        AnimationBusHost.LoadRegionAnimation(null, p, AnimationSessionMode.NewSession);
        vm.OnRegionApplied();

        Assert.False(vm.LivePreview);
        vm.Tracks.First(r => r.ParamName == "JuliaC").FrequencyHz = 0.5;     // must NOT re-push
        Assert.Empty(AnimationBusHost.Baseline.CapturedParams);
        vm.EndPreview();                                                     // closing now is a no-op
        Assert.Equal(new Complex(0.285, 0.01), p.JuliaC);
    }

    [Fact]
    public void Closing_the_editor_without_a_preview_leaves_the_region_animation_alone()
    {
        var p = new FractalParameters { JuliaC = new Complex(-0.4, 0.6) };
        var vm = Editor(p);
        AnimationBusHost.LoadRegionAnimation(Anim("JuliaC"), p, AnimationSessionMode.NewSession);
        p.JuliaC = new Complex(0.1, 0.1);                       // region animation running
        vm.EndPreview();
        Assert.Same(p, AnimationBusHost.Baseline.Target);        // session untouched
        Assert.Equal(new Complex(0.1, 0.1), p.JuliaC);
        AnimationBusHost.LoadRegionAnimation(null, p, AnimationSessionMode.NewSession);
    }

    // ── Fractal Params dialog Julia orbit (its own bus) ──────────────────────

    [Fact]
    public void Julia_animate_stop_restores_c()
    {
        var p = new FractalParameters { JuliaC = new Complex(-0.7, 0.27) };
        var vm = new FractalParamsViewModel(FractalType.Julia, p);
        vm.ToggleJuliaAnimateCommand.Execute().Subscribe();
        Assert.True(vm.JuliaAnimating);
        vm.JuliaR = 0.1;                                         // the orbit moved c
        vm.ToggleJuliaAnimateCommand.Execute().Subscribe();      // Stop
        Assert.False(vm.JuliaAnimating);
        Assert.Equal(new Complex(-0.7, 0.27), p.JuliaC);
        Assert.Equal(-0.7, vm.JuliaR);
    }

    [Fact]
    public void Julia_animate_stops_on_region_jump_and_keeps_the_region_c()
    {
        var p = new FractalParameters { JuliaC = new Complex(-0.7, 0.27) };
        var vm = new FractalParamsViewModel(FractalType.Julia, p);
        vm.ToggleJuliaAnimateCommand.Execute().Subscribe();
        vm.JuliaR = 0.1;
        p.JuliaC = new Complex(0.285, 0.01);                     // region recall
        vm.OnRegionApplied();
        Assert.False(vm.JuliaAnimating);
        Assert.Equal(new Complex(0.285, 0.01), p.JuliaC);
        Assert.Equal(0.285, vm.JuliaR);                          // dialog re-read the region
        vm.StopAnimations();                                     // later close: nothing to restore
        Assert.Equal(new Complex(0.285, 0.01), p.JuliaC);
    }

    // Minimal IColorThemeService: void → nothing, collections → empty, rest → default.
    public class FakeService : DispatchProxy
    {
        public static IColorThemeService Create() => DispatchProxy.Create<IColorThemeService, FakeService>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var rt = method!.ReturnType;
            if (rt == typeof(void)) return null;
            if (rt != typeof(string) && rt.IsAssignableFrom(typeof(string[]))) return Array.Empty<string>();
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }
}
