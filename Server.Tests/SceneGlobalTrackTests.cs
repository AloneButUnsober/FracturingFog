// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using FracturingFog.Render;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Scene Engine Roadmap S8 "global tracks": scene-wide, keyframed post/look
/// scalars sampled at global scene time. Covers the scalar evaluator (clamp /
/// interpolation / per-key ease), the LightingFxData binding round-trip, the
/// multi-track apply (later-wins), the animator, JSON persistence, and the
/// built-in "Exposure Ramp" demo.
/// </summary>
/// <remarks>Joins the non-parallel library-singleton collection because the
/// built-in test loads <c>SceneLibrary.Instance</c> (shared scenes.json).</remarks>
[Collection(FractalRegionLibraryCollection.Name)]
public sealed class SceneGlobalTrackTests
{
    // ── Evaluator ────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_EmptyTrack_Throws()
    {
        var t = new SceneGlobalTrack();
        Assert.False(t.IsActive);
        Assert.Throws<System.InvalidOperationException>(() => t.Evaluate(0.0));
    }

    [Fact]
    public void Evaluate_SingleKey_HoldsValue()
    {
        var t = new SceneGlobalTrack();
        t.Add(new SceneGlobalKey(3.0, 0.7));
        Assert.True(t.IsActive);
        Assert.Equal(0.7, t.Evaluate(-5.0), precision: 9);
        Assert.Equal(0.7, t.Evaluate(3.0), precision: 9);
        Assert.Equal(0.7, t.Evaluate(100.0), precision: 9);
    }

    [Fact]
    public void Evaluate_ClampsOutsideKeyRange()
    {
        var t = new SceneGlobalTrack { Interpolation = CameraInterpolation.Linear };
        t.Add(new SceneGlobalKey(0.0, 0.2));
        t.Add(new SceneGlobalKey(10.0, 1.2));

        Assert.Equal(0.2, t.Evaluate(-1.0), precision: 9); // below first
        Assert.Equal(1.2, t.Evaluate(99.0), precision: 9); // above last
    }

    [Fact]
    public void Evaluate_Linear_MidpointIsMean()
    {
        var t = new SceneGlobalTrack { Interpolation = CameraInterpolation.Linear };
        t.Add(new SceneGlobalKey(0.0, 0.0));
        t.Add(new SceneGlobalKey(4.0, 2.0));

        Assert.Equal(1.0, t.Evaluate(2.0), precision: 9); // linear midpoint
        Assert.Equal(0.5, t.Evaluate(1.0), precision: 9);
    }

    [Fact]
    public void Evaluate_EaseInOut_ShapesMidpointButFixesEndpoints()
    {
        var linear = new SceneGlobalTrack { Interpolation = CameraInterpolation.Linear };
        linear.Add(new SceneGlobalKey(0.0, 0.0));
        linear.Add(new SceneGlobalKey(4.0, 4.0));

        var eased = new SceneGlobalTrack { Interpolation = CameraInterpolation.Linear };
        eased.Add(new SceneGlobalKey(0.0, 0.0, CameraEase.EaseInOut));
        eased.Add(new SceneGlobalKey(4.0, 4.0));

        // Endpoints identical; the eased midpoint is smoothstep(0.5)=0.5 too, so
        // probe a quarter point where the shapes diverge.
        Assert.Equal(linear.Evaluate(0.0), eased.Evaluate(0.0), precision: 9);
        Assert.Equal(linear.Evaluate(4.0), eased.Evaluate(4.0), precision: 9);
        Assert.True(eased.Evaluate(1.0) < linear.Evaluate(1.0)); // slow start
    }

    [Fact]
    public void Evaluate_CatmullRom_PassesThroughEveryKey()
    {
        var t = new SceneGlobalTrack { Interpolation = CameraInterpolation.CatmullRom };
        t.Add(new SceneGlobalKey(0.0, 0.3));
        t.Add(new SceneGlobalKey(2.0, 1.1));
        t.Add(new SceneGlobalKey(5.0, 0.6));

        Assert.Equal(0.3, t.Evaluate(0.0), precision: 9);
        Assert.Equal(1.1, t.Evaluate(2.0), precision: 9);
        Assert.Equal(0.6, t.Evaluate(5.0), precision: 9);
    }

    [Fact]
    public void Add_InsertsKeysSorted()
    {
        var t = new SceneGlobalTrack();
        t.Add(new SceneGlobalKey(5.0, 5.0));
        t.Add(new SceneGlobalKey(1.0, 1.0));
        t.Add(new SceneGlobalKey(3.0, 3.0));

        Assert.Equal(new[] { 1.0, 3.0, 5.0 }, new[] { t.Keys[0].Time, t.Keys[1].Time, t.Keys[2].Time });
        Assert.Equal(5.0, t.Duration, precision: 9);
    }

    // ── Binding onto LightingFxData ──────────────────────────────────────────

    [Theory]
    [InlineData(SceneGlobalTarget.Exposure, 0.42)]
    [InlineData(SceneGlobalTarget.BloomStrength, 0.8)]
    [InlineData(SceneGlobalTarget.BloomThreshold, 1.5)]
    [InlineData(SceneGlobalTarget.Vignette, 0.65)]
    [InlineData(SceneGlobalTarget.ChromaticAberration, 2.0)]
    public void Binding_ApplyThenRead_RoundTrips(SceneGlobalTarget target, double value)
    {
        var p = new FractalParameters();
        SceneGlobalBinding.Apply(p, target, value);
        Assert.Equal(value, SceneGlobalBinding.Read(p, target), precision: 9);
    }

    [Fact]
    public void Binding_ApplyExposure_WritesThroughTheStructProperty()
    {
        var p = new FractalParameters();
        SceneGlobalBinding.Apply(p, SceneGlobalTarget.Exposure, 0.25);
        Assert.Equal(0.25, p.Lighting.Exposure, precision: 9); // struct write persisted
    }

    // ── Multi-track apply ────────────────────────────────────────────────────

    [Fact]
    public void SceneGlobalTracks_Apply_NullOrEmpty_IsNoOp()
    {
        var p = new FractalParameters();
        double before = p.Lighting.Exposure;
        SceneGlobalTracks.Apply(null, p, 1.0);
        SceneGlobalTracks.Apply(new List<SceneGlobalTrack>(), p, 1.0);
        Assert.Equal(before, p.Lighting.Exposure, precision: 9);
    }

    [Fact]
    public void SceneGlobalTracks_Apply_EvaluatesAtGlobalTime()
    {
        var track = new SceneGlobalTrack { Interpolation = CameraInterpolation.Linear };
        track.Add(new SceneGlobalKey(0.0, 0.0));
        track.Add(new SceneGlobalKey(10.0, 1.0));

        var p = new FractalParameters();
        SceneGlobalTracks.Apply(new[] { track }, p, 5.0);
        Assert.Equal(0.5, p.Lighting.Exposure, precision: 9);
    }

    [Fact]
    public void SceneGlobalTracks_Apply_LaterTrackWinsOnSameTarget()
    {
        var a = new SceneGlobalTrack();
        a.Add(new SceneGlobalKey(0.0, 0.2));
        var b = new SceneGlobalTrack();
        b.Add(new SceneGlobalKey(0.0, 0.9));

        var p = new FractalParameters();
        SceneGlobalTracks.Apply(new[] { a, b }, p, 0.0);
        Assert.Equal(0.9, p.Lighting.Exposure, precision: 9); // b applied last
    }

    [Fact]
    public void SceneGlobalTracks_Apply_InactiveTrackSkipped()
    {
        var p = new FractalParameters();
        double before = p.Lighting.Vignette;
        SceneGlobalTracks.Apply(new[] { new SceneGlobalTrack { Target = SceneGlobalTarget.Vignette } }, p, 0.0);
        Assert.Equal(before, p.Lighting.Vignette, precision: 9); // empty track inert
    }

    // ── Animator ─────────────────────────────────────────────────────────────

    [Fact]
    public void Animator_Tick_AdvancesClockAndApplies()
    {
        var track = new SceneGlobalTrack { Interpolation = CameraInterpolation.Linear };
        track.Add(new SceneGlobalKey(0.0, 0.0));
        track.Add(new SceneGlobalKey(10.0, 1.0));

        var p = new FractalParameters();
        var anim = new SceneGlobalTrackAnimator(new[] { track }, p, startTime: 2.0);
        Assert.True(anim.HasWork);

        anim.Tick(3.0); // clock 2 → 5
        Assert.Equal(5.0, anim.Time, precision: 9);
        Assert.Equal(0.5, p.Lighting.Exposure, precision: 9);
    }

    [Fact]
    public void Animator_NoActiveTracks_HasNoWork()
    {
        var anim = new SceneGlobalTrackAnimator(
            new[] { new SceneGlobalTrack() }, new FractalParameters());
        Assert.False(anim.HasWork);
    }

    [Fact]
    public void Animator_Disabled_DoesNotApply()
    {
        var track = new SceneGlobalTrack();
        track.Add(new SceneGlobalKey(0.0, 0.33));
        var p = new FractalParameters();
        double before = p.Lighting.Exposure;

        var anim = new SceneGlobalTrackAnimator(new[] { track }, p) { IsEnabled = false };
        anim.Tick(1.0);
        Assert.Equal(before, p.Lighting.Exposure, precision: 9);
    }

    // ── JSON persistence ─────────────────────────────────────────────────────

    [Fact]
    public void SceneData_WithGlobalTracks_JsonRoundTrips_AsEnumStrings()
    {
        var src = new SceneData
        {
            Name = "Global scene",
            GlobalTracks = new List<SceneGlobalTrack>
            {
                new SceneGlobalTrack
                {
                    Target = SceneGlobalTarget.Vignette,
                    Interpolation = CameraInterpolation.CatmullRom,
                    Keys =
                    {
                        new SceneGlobalKey(0.0, 0.0, CameraEase.EaseIn),
                        new SceneGlobalKey(4.0, 0.6),
                    },
                },
            },
        };

        var opts = SceneLibrary.BuildJsonOptions();
        string json = JsonSerializer.Serialize(src, opts);

        Assert.Contains("\"Vignette\"", json);   // target as string
        Assert.Contains("\"EaseIn\"", json);      // ease as string
        Assert.DoesNotContain("\"Duration\"", json); // computed, not serialised

        var dst = JsonSerializer.Deserialize<SceneData>(json, opts);
        Assert.NotNull(dst);
        var t = Assert.Single(dst!.GlobalTracks);
        Assert.Equal(SceneGlobalTarget.Vignette, t.Target);
        Assert.Equal(CameraInterpolation.CatmullRom, t.Interpolation);
        Assert.Equal(2, t.Keys.Count);
        Assert.Equal(0.0, t.Keys[0].Time, precision: 9);
        Assert.Equal(0.6, t.Keys[1].Value, precision: 9);
        Assert.Equal(CameraEase.EaseIn, t.Keys[0].Ease);
    }

    [Fact]
    public void SceneData_NoGlobalTracks_SerialisesEmptyList()
    {
        var src = new SceneData { Name = "plain" };
        var opts = SceneLibrary.BuildJsonOptions();
        var dst = JsonSerializer.Deserialize<SceneData>(
            JsonSerializer.Serialize(src, opts), opts);
        Assert.NotNull(dst);
        Assert.Empty(dst!.GlobalTracks); // default empty, not null
    }

    // ── Built-in demo ────────────────────────────────────────────────────────

    [Fact]
    public void BuiltIn_ExposureRamp_HasGlobalExposureTrack_ThatBreathes()
    {
        var lib = SceneLibrary.Instance;
        lib.Load();

        var scene = lib.GetByName("Exposure Ramp");
        Assert.NotNull(scene);
        var track = Assert.Single(scene!.GlobalTracks);
        Assert.Equal(SceneGlobalTarget.Exposure, track.Target);

        double duration = scene.Shots[0].DurationSeconds;
        double start = track.Evaluate(0.0);
        double mid = track.Evaluate(duration * 0.5);
        double end = track.Evaluate(duration);

        // Breathes: dark opening → bright over-exposed peak mid-clip → back to
        // near-black, so the whole clip visibly differs from "Mandelbulb Orbit".
        Assert.True(start < 0.3, $"expected a dark opening, got {start}");
        Assert.True(mid > 1.0, $"expected a bright peak mid-clip, got {mid}");
        Assert.True(end < 0.3, $"expected a dark close, got {end}");
    }
}
