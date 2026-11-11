// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1699 (scripted scenes SS5) — headless script video: --video-motion script, auto
// picking it for a scripted Math Plane scene, the frame clock (frame k at k / fps,
// the last frame on s = D), and the Command builder's video seed.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneScriptVideoTests
{
    private static BatchOptions Parse(params string[] tail)
    {
        var argv = new[] { "FracturingFog", "--batch" }.Concat(tail).ToArray();
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        return opts;
    }

    [Fact]
    public void Auto_PlaysTheScript_OnlyForAScriptedMathPlaneScene()
    {
        Assert.Equal(VideoMotionKind.Script, VideoMotionPlan.Resolve(FractalType.MathPlane, VideoMotionMode.Auto, out var n, hasScript: true));
        Assert.Null(n);
        // Without a script, auto is what it always was.
        Assert.Equal(VideoMotionPlan.Resolve(FractalType.MathPlane, VideoMotionMode.Auto, out _),
                     VideoMotionPlan.Resolve(FractalType.MathPlane, VideoMotionMode.Auto, out _, hasScript: false));
        // A script flag on another family changes nothing.
        Assert.Equal(VideoMotionKind.Zoom, VideoMotionPlan.Resolve(FractalType.Mandelbrot, VideoMotionMode.Auto, out _, hasScript: true));
        // An explicit choice wins over the script.
        Assert.Equal(VideoMotionKind.Hold, VideoMotionPlan.Resolve(FractalType.MathPlane, VideoMotionMode.Hold, out _, hasScript: true));
    }

    [Fact]
    public void ExplicitScript_WithoutOne_FallsBackToAutoWithANote()
    {
        var auto = VideoMotionPlan.Resolve(FractalType.MathPlane, VideoMotionMode.Auto, out _);
        Assert.Equal(auto, VideoMotionPlan.Resolve(FractalType.MathPlane, VideoMotionMode.Script, out var n1));
        Assert.Contains("no script", n1);
        Assert.Equal(VideoMotionKind.Zoom, VideoMotionPlan.Resolve(FractalType.Mandelbrot, VideoMotionMode.Script, out var n2, hasScript: true));
        Assert.Contains("Math Plane", n2);
        Assert.Equal(VideoMotionKind.Script, VideoMotionPlan.Resolve(FractalType.MathPlane, VideoMotionMode.Script, out var n3, hasScript: true));
        Assert.Null(n3);
    }

    [Fact]
    public void TheFlag_Parses_AndSecondsAreRemembered()
    {
        var o = Parse("--mode", "video", "--fractal", "MathPlane", "--x", "0", "--y", "0", "--zoom", "0.5", "--video-motion", "script", "--out", "a.mp4");
        Assert.Equal(VideoMotionMode.Script, o.VideoMotion);
        Assert.False(o.VideoSecondsSet);
        var s = Parse("--mode", "video", "--fractal", "MathPlane", "--x", "0", "--y", "0", "--zoom", "0.5", "--video-motion", "script", "--seconds", "3", "--out", "a.mp4");
        Assert.True(s.VideoSecondsSet);
        Assert.Equal(3.0, s.VideoSeconds);
        Assert.Contains("script", BatchFlagCatalog.All.Single(f => f.Name == BatchFlags.VideoMotion).Choices);
    }

    [Theory]
    [InlineData(2.5, 30, 76)]     // 0, 1/30 ... 75/30 = 2.5
    [InlineData(1.0, 24, 25)]
    [InlineData(0.0, 30, 2)]      // at least two frames
    [InlineData(0.01, 30, 2)]
    public void AScriptVideo_LastsTheScript_ItsLastFrameOnTheEnd(double d, int fps, int frames)
    {
        Assert.Equal(frames, VideoMotionPlan.ScriptFrameCount(d, fps));
        double last = VideoMotionPlan.ScriptFrameTime(frames - 1, fps);
        if (d * fps >= 1) Assert.Equal(d, last, 9);
    }

    [Fact]
    public void FrameK_IsAtScriptTimeKOverFps()
    {
        Assert.Equal(0.0, VideoMotionPlan.ScriptFrameTime(0, 30));
        Assert.Equal(0.5, VideoMotionPlan.ScriptFrameTime(15, 30), 15);
        Assert.Equal(2.0, VideoMotionPlan.ScriptFrameTime(60, 30), 15);
    }

    [Fact]
    public void TheVideoSeed_IsTheLiveCommandPlusScriptMotion_AndParses()
    {
        var p = new FractalParameters { MathPlaneScene = "v1; point A 0 0 hidden; step 1 show A" };
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane, CenterX = 0, CenterY = 0, Zoom = 0.5, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        var args = report.Args.Concat(BatchCommandBuilder.ScriptVideoArgs()).ToArray();
        Assert.True(BatchOptions.TryParse(args, 0, out var o, out var err), err);
        Assert.Equal(VideoMotionMode.Script, o.VideoMotion);
        Assert.Equal("video", o.Mode.ToString().ToLowerInvariant());
        var fresh = new FractalParameters { MathPlaneScene = "" };
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane, out var ferr)!.ApplyTo(fresh);
        Assert.Null(ferr);
        Assert.True(MathPlaneScene.TryParse(p.MathPlaneScene, out var want, out _));
        Assert.True(MathPlaneScene.TryParse(fresh.MathPlaneScene, out var got, out var perr2), perr2?.ToString());
        Assert.Equal(want, got);
        Assert.Single(got.Steps);
    }

    [Fact]
    public void TheScriptGroupsVideoButton_RaisesTheRequest_AndPauses()
    {
        var view = new FractalViewState { FractalType = FractalType.MathPlane, Zoom = 0.5 };
        view.FractalParameters.MathPlaneScene = "v1; point A 0 0 hidden; step 1 show A";
        var vm = new MathPlaneSectionViewModel(view, () => { }, () => { });
        int raised = 0;
        vm.ScriptVideoRequested += () => raised++;
        vm.PlayScript(startTimer: false);
        vm.ScriptVideoCommand.Execute().Subscribe();
        Assert.Equal(1, raised);
        Assert.False(vm.IsScriptPlaying);
    }
}
