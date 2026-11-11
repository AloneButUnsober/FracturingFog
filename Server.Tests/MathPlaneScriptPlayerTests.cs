// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1698 (scripted scenes SS4) — the Script group: rows and markers come from the
// timeline, scrubbing sets MathPlaneScriptTime (clamped), Play advances it from the
// current time and stops or wraps at the end, a row click seeks to its start, and
// canvas editing waits for 0 s.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneScriptPlayerTests
{
    private const int W = 640, H = 360;
    // Steps: [0,1] show j; [1,3] t 1 with c [1,2.5]; [3,3.5] wait. D = 3.5.
    private const string Scene =
        "v1; vector i 1 0; vector j 0 1 hidden; point A 1 1; step 1 show j; step 2 ease none t 1; with 1.5 c 0 0; step 0.5 wait";

    private sealed class Rig
    {
        public readonly FractalViewState View = new() { FractalType = FractalType.MathPlane, CenterX = 0, CenterY = 0, Zoom = 0.5 };
        public readonly FractalInputController Input;
        public readonly MathPlaneSectionViewModel Vm;
        public int Renders;

        public Rig(string scene)
        {
            View.FractalParameters.MathPlaneScene = scene;
            Input = new FractalInputController(View);
            Vm = new MathPlaneSectionViewModel(View, () => Renders++, () => View.FractalType = FractalType.MathPlane);
            Input.EditHandler = Vm;
        }

        public double S => View.FractalParameters.MathPlaneScriptTime;

        public PointerInput At(double wx, double wy)
        {
            var (px, py) = MathPlaneCalculator.ToPixel(wx, wy, W, H, View.CenterX, View.CenterY, View.Zoom);
            return new PointerInput((int)Math.Floor(px), (int)Math.Floor(py), W, H, PointerButton.Left, InputModifiers.None);
        }
    }

    [Fact]
    public void AScriptlessScene_HidesTheGroup()
    {
        var r = new Rig("v1; point A 1 1");
        Assert.False(r.Vm.HasScript);
        Assert.Equal(0.0, r.Vm.ScriptDuration);
        Assert.Empty(r.Vm.ScriptRows);
    }

    [Fact]
    public void RowsAndMarkers_ComeFromTheTimeline()
    {
        var r = new Rig(Scene);
        Assert.True(r.Vm.HasScript);
        Assert.Equal(3.5, r.Vm.ScriptDuration, 12);
        Assert.Equal(new[] { "step 1 show j", "step 2 ease none t 1", "with 1.5 c 0 0", "step 0.5 wait" },
                     r.Vm.ScriptRows.Select(x => x.Text));
        Assert.Equal(new[] { 0.0, 1, 1, 3 }, r.Vm.ScriptRows.Select(x => x.Start));
        Assert.Equal(new[] { 1.0, 3, 2.5, 3.5 }, r.Vm.ScriptRows.Select(x => x.End));
        Assert.True(r.Vm.ScriptRows[2].IsWith);
        Assert.Equal(new[] { 0.0, 1, 2.5, 3, 3.5 }, r.Vm.ScriptMarkers);
        Assert.Equal("0.00 - 1.00 s", r.Vm.ScriptRows[0].Span);
    }

    [Fact]
    public void Scrubbing_SetsTheParam_ClampedToTheScript()
    {
        var r = new Rig(Scene);
        int before = r.Renders;
        r.Vm.ScriptTime = 2;
        Assert.Equal(2.0, r.S);
        Assert.Equal("2.00 / 3.50 s", r.Vm.ScriptReadout);
        Assert.True(r.Renders > before);
        r.Vm.ScriptTime = 99;
        Assert.Equal(3.5, r.S);
        r.Vm.ScriptTime = -4;
        Assert.Equal(0.0, r.S);
        r.Vm.ScriptTime = double.NaN;
        Assert.Equal(0.0, r.S);
    }

    [Theory]
    [InlineData(0.5, new[] { 0 })]
    [InlineData(1.0, new[] { 1, 2 })]
    [InlineData(2.7, new[] { 1 })]
    [InlineData(3.2, new[] { 3 })]
    [InlineData(3.5, new[] { 3 })]
    public void TheRowsRunningNow_AreMarkedCurrent(double s, int[] current)
    {
        var r = new Rig(Scene);
        r.Vm.ScriptTime = s;
        Assert.Equal(current, r.Vm.ScriptRows.Select((x, i) => (x, i)).Where(p => p.x.IsCurrent).Select(p => p.i));
    }

    [Fact]
    public void Play_AdvancesFromTheCurrentTime_AndStopsAtTheEnd()
    {
        var r = new Rig(Scene);
        r.Vm.ScriptTime = 1;
        r.Vm.PlayScript(startTimer: false);
        Assert.True(r.Vm.IsScriptPlaying);
        Assert.Equal("Pause", r.Vm.ScriptPlayLabel);
        r.Vm.AdvanceScript(0.75);
        Assert.Equal(1.75, r.S, 12);
        r.Vm.AdvanceScript(10);
        Assert.Equal(3.5, r.S, 12);
        Assert.False(r.Vm.IsScriptPlaying);
        // Play again at the end starts over.
        r.Vm.PlayScript(startTimer: false);
        Assert.Equal(0.0, r.S);
        r.Vm.AdvanceScript(0.5);
        Assert.Equal(0.5, r.S, 12);
    }

    [Fact]
    public void Loop_WrapsAtTheEnd()
    {
        var r = new Rig(Scene);
        r.Vm.ScriptLoop = true;
        r.Vm.PlayScript(startTimer: false);
        r.Vm.AdvanceScript(3.5 + 1.25);
        Assert.Equal(1.25, r.S, 9);
        Assert.True(r.Vm.IsScriptPlaying);
        r.Vm.PauseScript();
        r.Vm.AdvanceScript(2);   // ignored once paused
        Assert.Equal(1.25, r.S, 9);
    }

    [Fact]
    public void ClickingARow_SeeksToItsStart_AndPauses()
    {
        var r = new Rig(Scene);
        r.Vm.PlayScript(startTimer: false);
        r.Vm.SelectedScriptRow = r.Vm.ScriptRows[3];
        Assert.Equal(3.0, r.S);
        Assert.False(r.Vm.IsScriptPlaying);
    }

    [Fact]
    public void AShorterScript_HoldsItsEnd()
    {
        var r = new Rig(Scene);
        r.Vm.ScriptTime = 3;
        r.Vm.SceneText = "v1; vector j 0 1 hidden; step 1 show j";
        Assert.Equal(1.0, r.Vm.ScriptDuration);
        Assert.Equal(1.0, r.S);
        r.Vm.SceneText = "v1; point A 0 0";
        Assert.False(r.Vm.HasScript);
    }

    [Fact]
    public void LoadingADemo_PausesAndStartsAtZero()
    {
        var r = new Rig(Scene);
        r.Vm.ScriptTime = 2;
        r.Vm.PlayScript(startTimer: false);
        r.Vm.LoadDemo(MathPlanePresets.All[0]);
        Assert.False(r.Vm.IsScriptPlaying);
        Assert.Equal(0.0, r.S);
        Assert.Equal(0.0, r.Vm.ScriptTime);
    }

    [Fact]
    public void CanvasEditing_WaitsForTheStart()
    {
        var r = new Rig(Scene);
        r.Vm.ScriptTime = 2;
        r.Input.OnPointerDown(r.At(1, 1));
        r.Input.OnPointerMove(r.At(2, -1));
        r.Input.OnPointerUp(r.At(2, -1));
        Assert.True(MathPlaneScene.TryParse(r.View.FractalParameters.MathPlaneScene, out var mid, out _));
        Assert.Equal(new[] { 1.0, 1.0 }, mid.Objects[2].Coords);   // not edited mid-script

        r.Vm.ScriptTime = 0;
        r.View.CenterX = 0; r.View.CenterY = 0;
        r.Input.OnPointerDown(r.At(1, 1));
        r.Input.OnPointerMove(r.At(2, -1));
        r.Input.OnPointerUp(r.At(2, -1));
        Assert.True(MathPlaneScene.TryParse(r.View.FractalParameters.MathPlaneScene, out var start, out _));
        Assert.NotEqual(new[] { 1.0, 1.0 }, start.Objects[2].Coords);
        Assert.True(start.Steps.Count == 4, "the edit keeps the script");
    }
}
