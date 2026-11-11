// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1695 (scripted scenes SS1) — the script is a pure function of script time:
// at s = 0 it is the scene with its steps removed, at s >= D it is a hand-written
// final scene, values are continuous across step boundaries, and it round-trips
// through the scene text, the region snapshot and the Command builder.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Render;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneScriptTests
{
    private const int W = 240, H = 160;

    // Objects, then a script: fade j in, apply the shear while c moves, move A, hide B.
    private const string Objects =
        "v1; vector i 1 0 #E69F00; vector j 0 1 #56B4E9 hidden; point A 0.5 0.5 #009E73; "
        + "segment B -1 -1 -0.5 -1 #CC79A7; orbit 20";
    private const string Script =
        "; step 1 show j; step 2 ease none t 1; with c -1 0; step 0.5 wait; step 1.5 ease out move A 1.5 1; with 1 hide B";
    private const double D = 1 + 2 + 0.5 + 1.5;

    private static FractalParameters Params(string scene, double s = 0)
    {
        var p = new FractalParameters
        {
            MathPlaneScene = scene,
            MathPlaneMapMode = MathPlaneMapMode.Matrix,
            MathPlaneM11 = 1, MathPlaneM12 = 1, MathPlaneM21 = 0, MathPlaneM22 = 1,   // shear
            MathPlaneT = 0,
            MathPlaneCRe = -0.2, MathPlaneCIm = 0.3,
            MathPlaneScriptTime = s,
        };
        return p;
    }

    private static uint[] Render(FractalParameters p, MathPlaneCalculator? calc = null)
    {
        calc ??= new MathPlaneCalculator(W, H) { CenterX = 0, CenterY = 0, Zoom = 0.35, MaxIterations = 128 };
        calc.FractalParameters = p;
        calc.Calculate(CancellationToken.None);
        Assert.Null(calc.LastSceneError);
        return (uint[])calc.ColorBuffer.Clone();
    }

    private static MathPlaneScene Parse(string text)
    {
        Assert.True(MathPlaneScene.TryParse(text, out var s, out var e), e?.ToString());
        return s;
    }

    // ── Pure-function invariants ─────────────────────────────────────────

    [Fact]
    public void AtTimeZero_ItIsTheSceneWithItsStepsRemoved()
    {
        Assert.Equal(Render(Params(Objects)), Render(Params(Objects + Script, 0)));
    }

    [Theory]
    [InlineData(D)]
    [InlineData(D + 7)]
    public void AtOrPastTheEnd_ItIsAHandWrittenFinalScene(double s)
    {
        // Typed by hand: j shown, t = 1, c = -1, A at (1.5, 1), B hidden.
        const string final =
            "v1; vector i 1 0 #E69F00; vector j 0 1 #56B4E9; point A 1.5 1 #009E73; "
            + "segment B -1 -1 -0.5 -1 #CC79A7 hidden; orbit 20";
        var hand = Params(final);
        hand.MathPlaneT = 1; hand.MathPlaneCRe = -1; hand.MathPlaneCIm = 0;
        Assert.Equal(Render(hand), Render(Params(Objects + Script, s)));
    }

    [Fact]
    public void ADurationIsTheSumOfItsSteps_WithRunningAlongside()
    {
        Assert.Equal(D, MathPlaneScript.Duration(Parse(Objects + Script)), 12);
        // A longer 'with' extends its step.
        Assert.Equal(3.0, MathPlaneScript.Duration(Parse(Objects + "; step 1 t 1; with 3 c 0 0")), 12);
        Assert.Equal(0.0, MathPlaneScript.Duration(Parse(Objects)), 12);
    }

    [Fact]
    public void MidStepValues_FollowTheEase()
    {
        var scene = Parse(Objects + "; step 2 ease none t 1; step 2 c 1 1");
        var p = Params(Objects);
        double T(double s) => MathPlaneScript.Evaluate(scene, p, s).Params.MathPlaneT;
        Assert.Equal(0.25, T(0.5), 12);
        Assert.Equal(0.5, T(1.0), 12);
        // c from (-0.2, 0.3) to (1, 1) with smoothstep: halfway is halfway.
        var mid = MathPlaneScript.Evaluate(scene, p, 3.0).Params;
        Assert.Equal(0.4, mid.MathPlaneCRe, 12);
        Assert.Equal(0.65, mid.MathPlaneCIm, 12);
        // A quarter in, smoothstep is u^2 (3 - 2u) = 0.15625.
        var q = MathPlaneScript.Evaluate(scene, p, 2.5).Params;
        Assert.Equal(-0.2 + 1.2 * 0.15625, q.MathPlaneCRe, 12);
    }

    [Fact]
    public void ALaterMember_StartsFromWhatTheEarlierOneLeft()
    {
        var scene = Parse(Objects + "; step 1 ease none c 1 0; step 1 ease none c 0 1");
        var p = Params(Objects);
        var at = MathPlaneScript.Evaluate(scene, p, 1.5).Params;
        Assert.Equal(0.5, at.MathPlaneCRe, 12);
        Assert.Equal(0.5, at.MathPlaneCIm, 12);
        // Before the second member begins, the first one's value stands.
        var before = MathPlaneScript.Evaluate(scene, p, 0.5).Params;
        Assert.Equal(0.4, before.MathPlaneCRe, 12);
        Assert.Equal(0.15, before.MathPlaneCIm, 12);
    }

    [Fact]
    public void EveryProperty_IsContinuousAcrossStepBoundaries()
    {
        var scene = Parse(Objects + Script);
        var p = Params(Objects);
        var timed = MathPlaneScript.Layout(scene.Steps, out _);
        const double eps = 1e-9;
        foreach (double b in timed.SelectMany(m => new[] { m.Start, m.End }).Distinct())
        {
            var lo = MathPlaneScript.Evaluate(scene, p, Math.Max(0, b - eps));
            var hi = MathPlaneScript.Evaluate(scene, p, b + eps);
            Assert.Equal(lo.Params.MathPlaneT, hi.Params.MathPlaneT, 6);
            Assert.Equal(lo.Params.MathPlaneCRe, hi.Params.MathPlaneCRe, 6);
            Assert.Equal(lo.Params.MathPlaneCIm, hi.Params.MathPlaneCIm, 6);
            for (int i = 0; i < lo.Scene.Objects.Count; i++)
            {
                var (ox, oy) = MathPlaneScript.Anchor(lo.Scene.Objects[i]);
                var (nx, ny) = MathPlaneScript.Anchor(hi.Scene.Objects[i]);
                Assert.Equal(ox, nx, 6);
                Assert.Equal(oy, ny, 6);
                Assert.InRange(Math.Abs((int)(lo.Scene.Objects[i].Color >> 24) - (int)(hi.Scene.Objects[i].Color >> 24)), 0, 1);
            }
        }
    }

    [Theory]
    [InlineData(CameraEase.None)]
    [InlineData(CameraEase.EaseIn)]
    [InlineData(CameraEase.EaseOut)]
    [InlineData(CameraEase.EaseInOut)]
    public void EveryEase_RunsFromZeroToOne(CameraEase ease)
    {
        var m = new MathPlaneTimedStep(0, new MathPlaneStep(false, 2, ease, MathPlaneStepAction.Wait, Array.Empty<string>(), Array.Empty<double>()), 1, 2, ease);
        Assert.Equal(0.0, MathPlaneScript.Progress(m, 0.5));
        Assert.Equal(0.0, MathPlaneScript.Progress(m, 1.0));
        Assert.Equal(1.0, MathPlaneScript.Progress(m, 3.0));
        Assert.Equal(1.0, MathPlaneScript.Progress(m, 9.0));
        double prev = 0;
        for (int k = 1; k <= 100; k++)
        {
            double v = MathPlaneScript.Progress(m, 1 + 2 * k / 100.0);
            Assert.True(v >= prev - 1e-15, "monotone");
            prev = v;
        }
        if (ease == CameraEase.EaseInOut)
        {
            const double h = 1e-6;
            Assert.True(MathPlaneScript.Progress(m, 1 + h) / h < 1e-4, "zero slope at the start");
            Assert.True((1 - MathPlaneScript.Progress(m, 3 - h)) / h < 1e-4, "zero slope at the end");
        }
    }

    [Fact]
    public void TheDefaultEase_IsSmoothstep_AndAWithInheritsItsSteps()
    {
        var scene = Parse(Objects + "; step 2 ease in t 1; with c 0 0; step 1 t 0");
        var timed = MathPlaneScript.Layout(scene.Steps, out _);
        Assert.Equal(CameraEase.EaseIn, timed[1].Ease);
        Assert.Equal(2.0, timed[1].Duration);
        Assert.Equal(CameraEase.EaseInOut, timed[2].Ease);
    }

    [Fact]
    public void AFadeLowersTheColoursAlpha_AndHidingEndsInvisible()
    {
        var scene = Parse(Objects + "; step 2 ease none hide A");
        var p = Params(Objects);
        var half = MathPlaneScript.Evaluate(scene, p, 1).Scene.Objects.Single(o => o.Name == "A");
        Assert.Equal(128u, half.Color >> 24);
        Assert.Equal(0x009E73u, half.Color & 0xFFFFFF);
        Assert.True(half.Visible);
        var gone = MathPlaneScript.Evaluate(scene, p, 2).Scene.Objects.Single(o => o.Name == "A");
        Assert.False(gone.Visible);
    }

    [Fact]
    public void Move_KeepsAVectorsTail_AndMovesASegmentRigidly()
    {
        var scene = Parse("v1; vector v 1 0 from 2 2; segment s 0 0 1 1; step 1 move v 2 3; with move s 5 5");
        var end = MathPlaneScript.Evaluate(scene, new FractalParameters(), 1).Scene;
        var v = end.Objects[0];
        Assert.Equal(new[] { 0.0, 1.0, 2.0, 2.0 }, v.Coords);   // tip (2, 3), tail (2, 2)
        Assert.Equal(new[] { 5.0, 5.0, 6.0, 6.0 }, end.Objects[1].Coords);
    }

    [Fact]
    public void ASceneWithoutSteps_IgnoresScriptTime()
    {
        Assert.Equal(Render(Params(Objects, 0)), Render(Params(Objects, 3.7)));
    }

    [Fact]
    public void Evaluate_NeverChangesTheParamsItIsGiven()
    {
        var scene = Parse(Objects + Script);
        var p = Params(Objects + Script);
        var before = RegionFractalParams.Snapshot(FractalType.MathPlane, p)!.ToKeyValues().ToList();
        MathPlaneScript.Evaluate(scene, p, 2.2);
        Assert.Equal(before, RegionFractalParams.Snapshot(FractalType.MathPlane, p)!.ToKeyValues().ToList());
    }

    // ── Parse errors ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("; step 2 t 1; with c 0 0; with 1 t 0", "already animated")]   // t twice at once
    [InlineData("; step 1 c 0 0; with 2 wait; step 1 wait; with c 1 1", "")]    // fine: c members don't overlap
    [InlineData("; with 1 t 1", "step must come first")]
    [InlineData("; step 1 show nope", "no object called 'nope'")]
    [InlineData("; step 1 move", "object name")]
    [InlineData("; step t 1", "duration")]
    [InlineData("; step 601 wait", "0 to 600")]
    [InlineData("; step -1 wait", "0 to 600")]
    [InlineData("; step 1 ease fast wait", "none, in, out or inout")]
    [InlineData("; step 1 spin A", "unknown action")]
    [InlineData("; step 1 matrix 1 2 3", "four numbers")]
    [InlineData("; step 1 wait extra", "unexpected text")]
    [InlineData("; step 1 show A; with 1 hide A", "already animated")]
    public void BadScripts_AreErrors(string script, string expect)
    {
        bool ok = MathPlaneScene.TryParse(Objects + script, out _, out var err);
        if (expect == "") { Assert.True(ok, err?.ToString()); return; }
        Assert.False(ok);
        Assert.Contains(expect, err!.Message);
    }

    [Fact]
    public void MoveOnACurve_IsAnError()
    {
        Assert.False(MathPlaneScene.TryParse("v1; curve f explicit \"x\"; step 1 move f 1 1", out _, out var err));
        Assert.Contains("point, vector, segment or polyline", err!.Message);
    }

    [Fact]
    public void AStepMayNameAnObjectWrittenAfterIt()
    {
        Assert.True(MathPlaneScene.TryParse("v1; step 1 show p; point p 1 1 hidden", out _, out var err), err?.ToString());
    }

    // ── Round trips ──────────────────────────────────────────────────────

    [Fact]
    public void TheSceneText_RoundTrips()
    {
        var scene = Parse(Objects + Script + "; step 0 ease in matrix 0 -1 1 0; with 0.25 ease inout c 0.5 -0.5");
        var again = Parse(scene.Serialize());
        Assert.Equal(scene, again);
        Assert.Equal(scene.Serialize(), again.Serialize());
    }

    [Fact]
    public void ScriptTime_RidesTheRegionSnapshot_AndTheCommandBuilder()
    {
        var p = Params(Objects + Script, 2.25);
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane, p)!;
        var kv = snap.ToKeyValues();
        Assert.Contains(kv, x => x.Key == "MathPlaneScriptTime" && x.Value == "2.25");

        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane, CenterX = 0, CenterY = 0, Zoom = 0.35,
            Parameters = p, FamilyParams = kv,
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var perr), perr);
        var fresh = new FractalParameters { MathPlaneScene = "" };
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane, out var ferr)!.ApplyTo(fresh);
        Assert.Null(ferr);
        Assert.Equal(2.25, fresh.MathPlaneScriptTime);
        Assert.Equal(Render(p), Render(fresh));
    }

    [Fact]
    public void ScriptTime_IsAnimatable_AndCloned()
    {
        Assert.Contains(FractalAnimatableParamsMap.For(FractalType.MathPlane), d => d.ParamName == "MathPlaneScriptTime");
        var p = new FractalParameters { MathPlaneScriptTime = 1.75 };
        Assert.Equal(1.75, p.Clone().MathPlaneScriptTime);
    }

    [Fact]
    public void LoadingADemo_StartsItsScriptAtTheBeginning()
    {
        var p = new FractalParameters { MathPlaneScriptTime = 9 };
        MathPlanePresets.All[0].Apply(p);
        Assert.Equal(0.0, p.MathPlaneScriptTime);
    }

    // ── Backdrop cache ───────────────────────────────────────────────────

    [Fact]
    public void TheBackdropCache_NeverShowsAStaleBackdrop()
    {
        // c moves in the first step only; the second step moves a point over the same backdrop.
        string scene = "v1; point A 0 0; orbit 10; step 1 c -1 0; step 1 move A 0.5 0.5";
        FractalParameters At(double s)
        {
            var p = Params(scene, s);
            p.MathPlaneBackdrop = MathPlaneBackdrop.Julia;
            p.MathPlaneMapMode = MathPlaneMapMode.None;
            return p;
        }
        var shared = new MathPlaneCalculator(W, H) { CenterX = 0, CenterY = 0, Zoom = 0.35, MaxIterations = 128 };
        foreach (double s in new[] { 0.0, 0.5, 1.0, 1.4, 2.0, 0.25 })
        {
            var fresh = Render(At(s));
            Assert.Equal(fresh, Render(At(s), shared));
        }
        // Changing the backdrop family invalidates it too.
        var changed = At(2.0);
        changed.MathPlaneBackdrop = MathPlaneBackdrop.Mandelbrot;
        Assert.Equal(Render(changed), Render(changed, shared));
    }
}
