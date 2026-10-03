// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1056 — the "Draw camera path" dialog VM and applying its result to a shot.

using System;
using System.Linq;

using FracturingFog.Render;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

using LibraryService = FracturingFog.Server.Tests.SceneEditorPickerRefreshTests.LibraryService;

namespace FracturingFog.Server.Tests;

public sealed class CameraPathDrawViewModelTests
{
    private static (double, double)[] Circle(double r, int n = 120)
        => Enumerable.Range(0, n + 1)
            .Select(i => 2 * Math.PI * i / n)
            .Select(a => (r * Math.Cos(a), r * Math.Sin(a)))
            .ToArray();

    [Fact]
    public void Stroke_FitsOnRelease_AndApplyReturnsTheTrack()
    {
        var vm = new CameraPathDrawViewModel(relief: false, durationSeconds: 6);
        Assert.False(vm.CanApply);

        var pts = Circle(3);
        vm.BeginStroke(pts[0]);
        foreach (var p in pts.Skip(1)) vm.ExtendStroke(p);
        Assert.Null(vm.Fitted);                    // not until the stroke ends
        vm.EndStroke();

        Assert.True(vm.CanApply, vm.Status);
        Assert.False(vm.HasError);
        Assert.Equal(6.0, vm.Fitted!.Keys[^1].Time, 9);

        bool? closed = null;
        vm.CloseRequested += (_, applied) => closed = applied;
        vm.ApplyCommand.Execute().Subscribe();
        Assert.True(closed);
        Assert.Same(vm.Fitted, vm.Result);
    }

    [Fact]
    public void PathThroughTheObject_IsAnError_AndCannotApply()
    {
        var vm = new CameraPathDrawViewModel(false, 5);
        vm.SetPoints(Enumerable.Range(0, 40).Select(i => (-3.0 + i * 0.15, 0.0)));

        Assert.True(vm.HasError);
        Assert.False(vm.CanApply);
        Assert.Contains("inside the object", vm.Status);
    }

    [Fact]
    public void OptionChanges_Refit_ElevationAndDuration()
    {
        var vm = new CameraPathDrawViewModel(false, 5);
        vm.SetPoints(Circle(3));
        vm.StartElevationDeg = 10;
        vm.EndElevationDeg = 40;
        vm.DurationSeconds = 8;

        var k = vm.Fitted!.Keys;
        Assert.Equal(10 * Math.PI / 180, k[0].State.Phi, 9);
        Assert.Equal(40 * Math.PI / 180, k[^1].State.Phi, 9);
        Assert.Equal(8.0, k[^1].Time, 9);
    }

    [Fact]
    public void Relief_UsesReliefRanges_AndClampsElevation()
    {
        var vm = new CameraPathDrawViewModel(relief: true, durationSeconds: 4);
        Assert.Equal(5.0, vm.MaxDistance);        // distance = 1 / zoom, zoom ≥ 0.2
        Assert.Equal(0.2, vm.ObjectRadius, 9);    // zoom ≤ 5
        vm.StartElevationDeg = 0;                 // below the relief minimum
        vm.SetPoints(Circle(1.5));

        Assert.True(vm.CanApply, vm.Status);
        Assert.Equal(CameraParamBinding.ReliefMinElevationDeg * Math.PI / 180, vm.Fitted!.Keys[0].State.Phi, 9);
    }

    [Fact]
    public void ClearAndCancel()
    {
        var vm = new CameraPathDrawViewModel(false, 5);
        vm.SetPoints(Circle(3));
        vm.ClearCommand.Execute().Subscribe();
        Assert.Empty(vm.Points);
        Assert.False(vm.CanApply);

        bool? closed = null;
        vm.CloseRequested += (_, applied) => closed = applied;
        vm.CancelCommand.Execute().Subscribe();
        Assert.False(closed);
        Assert.Null(vm.Result);
    }

    [Fact]
    public void Editor_DrawButton_RaisesRequest_AndReplaceSwapsTheKeys()
    {
        var (svc, state) = LibraryService.Create();
        state.Regions = new() { "Bulb" };
        state.RegionInfo["Bulb"] = (FractalType.Mandelbulb, false);
        var editor = new SceneEditorViewModel(svc);
        var row = editor.Shots.Single();
        row.SelectedRegion = "Bulb";
        row.AddCameraKeyCommand.Execute().Subscribe();
        SceneShotRowViewModel? asked = null;
        editor.DrawCameraPathRequested += (_, r) => asked = r;

        row.DrawCameraPathCommand.Execute().Subscribe();
        Assert.Same(row, asked);

        var vm = new CameraPathDrawViewModel(false, row.DurationSeconds);
        vm.SetPoints(Circle(3));
        row.ReplaceCameraTrack(vm.Fitted!);

        var shot = row.ToShot();
        Assert.Equal(vm.Fitted!.Keys.Count, shot.Camera!.Keys.Count);
        Assert.Equal(vm.Fitted.Interpolation, shot.Camera.Interpolation);
        Assert.Equal(3.0, shot.Camera.Keys[1].State.Distance, 6);
    }
}
