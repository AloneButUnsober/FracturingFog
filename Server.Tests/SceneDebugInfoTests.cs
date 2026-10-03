// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1051 — scene debug overlay text (pure formatter) + the editor toggle.

using System;
using System.Linq;

using FracturingFog.Abstractions.Animation;
using FracturingFog.Render;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

using LibraryService = FracturingFog.Server.Tests.SceneEditorPickerRefreshTests.LibraryService;

namespace FracturingFog.Server.Tests;

public sealed class SceneDebugInfoTests
{
    private static CameraTrack Track()
    {
        var t = new CameraTrack { Interpolation = CameraInterpolation.Linear };
        t.Add(new CameraKey(0, new CameraState(2, 0, 0.3)));
        t.Add(new CameraKey(2, new CameraState(3, 1, 0.3)) { Ease = CameraEase.EaseInOut });
        t.Add(new CameraKey(5, new CameraState(4, 2, 0.3)));
        return t;
    }

    [Theory]
    [InlineData(0.0, 0, 1, 0.0, 2.0)]
    [InlineData(1.9, 0, 1, 0.0, 2.0)]
    [InlineData(2.0, 1, 2, 2.0, 5.0)]
    [InlineData(4.99, 1, 2, 2.0, 5.0)]
    [InlineData(6.0, 0, 1, 0.0, 2.0)]   // loops over the 5 s track (t = 1)
    public void Segment_FindsTheKeyPairPlaying(double t, int from, int to, double start, double end)
    {
        var seg = SceneDebugInfo.Segment(Track(), t);
        Assert.Equal((from, to, start, end), (seg.From, seg.To, seg.Start, seg.End));
    }

    [Fact]
    public void Segment_SingleAndEmptyTracks()
    {
        var one = new CameraTrack();
        one.Add(new CameraKey(0, new CameraState(1, 0, 0)));
        Assert.Equal((0, 0), (SceneDebugInfo.Segment(one, 3).From, SceneDebugInfo.Segment(one, 3).To));
        Assert.Equal(-1, SceneDebugInfo.Segment(new CameraTrack(), 1).From);
    }

    [Fact]
    public void Format_ShowsClockShotAssetsCameraAndPose()
    {
        var scene = new SceneData
        {
            Name = "Demo",
            Shots =
            {
                new SceneShot { Name = "Intro", DurationSeconds = 4, Transition = SceneTransitionKind.Cut },
                new SceneShot
                {
                    Name = "Orbit", RegionName = "Bulb", ThemeName = "Fire", AnimationName = "Spin",
                    DurationSeconds = 6, Transition = SceneTransitionKind.Cut, Camera = Track(),
                    LightingPresetName = "Mist", LightingPresetIsBuiltIn = true,
                    RotateThemes = true, ThemeRotateSeconds = 2,
                },
            },
        };
        var tl = SceneTimeline.Build(scene);
        var sample = tl.Sample(7.0);   // 3 s into shot 2

        string text = SceneDebugInfo.Format(scene, sample, 7.0, tl.TotalDuration,
            new CameraState(2.5, Math.PI / 2, Math.PI / 6), reliefCamera: false, theme: "Fire", frameMs: 42);

        Assert.Contains("Scene  \"Demo\"   t 7.00 / 10.00 s   frame 42 ms", text);
        Assert.Contains("Shot 2/2  \"Orbit\"   local 3.00 / 6.00 s", text);
        Assert.Contains("Region \"Bulb\"   Theme Fire (rotating /2 s)", text);
        Assert.Contains("Anim \"Spin\"   Light built-in \"Mist\"", text);
        Assert.Contains("Cam key 2->3 of 3   2.00-5.00 s (3.00 s)   at 3.00   Linear, ease EaseInOut", text);
        Assert.Contains("Pose dist 2.500   az 90.0°   el 30.0°", text);
    }

    [Fact]
    public void Format_ReliefPose_ShowsZoom_AndPreviewOmitsTheClock()
    {
        var scene = new SceneData { Name = "P", Shots = { new SceneShot { Name = "S", DurationSeconds = 2 } } };
        var tl = SceneTimeline.Build(scene);

        string text = SceneDebugInfo.Format(scene, tl.Sample(0), 0, 0,
            new CameraState(0.5, 0, Math.PI / 4), reliefCamera: true, preview: true);

        Assert.StartsWith("Preview  \"P\"", text);
        Assert.DoesNotContain(" t 0.00", text);
        Assert.Contains("Cam (no keys)", text);
        Assert.Contains("Pose zoom 2.000   az 0.0°   el 45.0°", text);
    }

    [Fact]
    public void EditorToggle_RaisesDebugOverlayChanged()
    {
        var (svc, _) = LibraryService.Create();
        var vm = new SceneEditorViewModel(svc);
        var seen = new System.Collections.Generic.List<bool>();
        vm.DebugOverlayChanged += (_, on) => seen.Add(on);

        vm.ShowDebugOverlay = true;
        vm.ShowDebugOverlay = true;      // no duplicate
        vm.ShowDebugOverlay = false;

        Assert.Equal(new[] { true, false }, seen);
    }
}
