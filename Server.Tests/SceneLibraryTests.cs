using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using FracturingFog.Render;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Scene Engine Roadmap Phase S4: the Scene asset + library. Covers the
/// SceneData / SceneShot JSON round-trip (including the embedded S3 CameraTrack),
/// enum-as-string persistence, the computed total-duration, and built-in demo
/// scene sanity. Stays away from singleton mutation / disk I/O where possible so
/// tests do not pollute the dev's %APPDATA% scenes.json.
/// </summary>
/// <remarks>A few cases do touch <c>SceneLibrary.Instance</c> (Save/Load/Remove);
/// joins the non-parallel <see cref="FractalRegionLibraryCollection"/> so those
/// serialise with the other singleton-mutating classes rather than racing
/// <see cref="AssetSourceTests"/> on the shared scenes.json.</remarks>
[Collection(FractalRegionLibraryCollection.Name)]
public sealed class SceneLibraryTests
{
    // ── JSON round-trip ──────────────────────────────────────────────────────

    /// <summary>Every field on SceneData / SceneShot — including a nested
    /// CameraTrack with keys — must survive a System.Text.Json round-trip with
    /// the library's standard options. Catches JsonIgnore regressions and
    /// enum-as-string regressions, and proves the S3 camera track serialises.</summary>
    [Fact]
    public void SceneData_JsonRoundTrip_PreservesFields_IncludingCamera()
    {
        var src = new SceneData
        {
            Name = "Test scene",
            Description = "round trip",
            Category = "User",
            Tags = new List<string> { "demo", "3D" },
            Shots = new List<SceneShot>
            {
                new SceneShot
                {
                    Name = "Shot 1",
                    RegionName = "Some Region",
                    ThemeName = "Ember",
                    AnimationName = "Julia C orbit",
                    FractalType = FractalType.Mandelbulb,
                    DurationSeconds = 8.5,
                    Transition = SceneTransitionKind.ParamMorph,
                    TransitionSeconds = 1.25,
                    Camera = MakeTrack(
                        CameraInterpolation.Bezier,
                        new CameraKey(0.0, 2.0, 0.0, 0.3),
                        new CameraKey(8.5, 2.0, 6.28, 0.3)),
                },
            },
        };

        var opts = SceneLibrary.BuildJsonOptions();
        string json = JsonSerializer.Serialize(src, opts);
        var dst = JsonSerializer.Deserialize<SceneData>(json, opts);

        Assert.NotNull(dst);
        Assert.Equal(src.Name, dst!.Name);
        Assert.Equal(src.Description, dst.Description);
        Assert.Equal(src.Category, dst.Category);
        Assert.Equal(src.Tags, dst.Tags);
        Assert.Single(dst.Shots);

        var a = src.Shots[0];
        var b = dst.Shots[0];
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.RegionName, b.RegionName);
        Assert.Equal(a.ThemeName, b.ThemeName);
        Assert.Equal(a.AnimationName, b.AnimationName);
        Assert.Equal(a.FractalType, b.FractalType);
        Assert.Equal(a.DurationSeconds, b.DurationSeconds);
        Assert.Equal(a.Transition, b.Transition);
        Assert.Equal(a.TransitionSeconds, b.TransitionSeconds);

        Assert.NotNull(b.Camera);
        Assert.Equal(a.Camera!.Interpolation, b.Camera!.Interpolation);
        Assert.Equal(a.Camera.Keys.Count, b.Camera.Keys.Count);
        for (int i = 0; i < a.Camera.Keys.Count; i++)
        {
            Assert.Equal(a.Camera.Keys[i].Time, b.Camera.Keys[i].Time, precision: 9);
            Assert.Equal(a.Camera.Keys[i].State, b.Camera.Keys[i].State);
        }
    }

    /// <summary>Library serialises enums as string names, not ints — humans
    /// hand-edit scenes.json.</summary>
    [Fact]
    public void SceneData_Json_UsesEnumStringNames()
    {
        var src = new SceneData
        {
            Name = "Stringy",
            Shots = new List<SceneShot>
            {
                new SceneShot
                {
                    FractalType = FractalType.Mandelbox,
                    Transition = SceneTransitionKind.LightSweep,
                },
            },
        };

        string json = JsonSerializer.Serialize(src, SceneLibrary.BuildJsonOptions());

        Assert.Contains("\"Mandelbox\"", json);
        Assert.Contains("\"LightSweep\"", json);
        Assert.DoesNotContain("\"Transition\":2", json);
    }

    /// <summary>A 2D shot leaves Camera null; the null must not serialise (the
    /// library ignores nulls) and must round-trip back to null.</summary>
    [Fact]
    public void SceneShot_NullCamera_IsOmitted_AndRoundTripsToNull()
    {
        var src = new SceneData
        {
            Name = "2D scene",
            Shots = new List<SceneShot> { new SceneShot { FractalType = FractalType.Mandelbrot } },
        };

        var opts = SceneLibrary.BuildJsonOptions();
        string json = JsonSerializer.Serialize(src, opts);
        Assert.DoesNotContain("\"Camera\"", json);

        var dst = JsonSerializer.Deserialize<SceneData>(json, opts);
        Assert.Null(dst!.Shots[0].Camera);
    }

    // ── Computed duration ────────────────────────────────────────────────────

    [Fact]
    public void TotalDuration_SumsShotDurations_IgnoringNonPositive()
    {
        var scene = new SceneData
        {
            Shots = new List<SceneShot>
            {
                new SceneShot { DurationSeconds = 5.0 },
                new SceneShot { DurationSeconds = 0.0 },   // skipped
                new SceneShot { DurationSeconds = -2.0 },  // skipped
                new SceneShot { DurationSeconds = 7.5 },
            },
        };

        Assert.Equal(12.5, scene.TotalDurationSeconds, precision: 9);
    }

    [Fact]
    public void TotalDuration_IsNotSerialised()
    {
        var scene = new SceneData
        {
            Name = "n",
            Shots = new List<SceneShot> { new SceneShot { DurationSeconds = 5.0 } },
        };
        string json = JsonSerializer.Serialize(scene, SceneLibrary.BuildJsonOptions());
        Assert.DoesNotContain("TotalDuration", json);
    }

    // ── Built-in demo scenes ─────────────────────────────────────────────────

    /// <summary>Built-in scenes are the user's first impression: each must have
    /// at least one shot, every shot a positive duration, and any shot that
    /// carries a camera must target a fractal type that actually has an orbit
    /// camera (else the S6 animator would reject it at play time).</summary>
    [Fact]
    public void BuiltIns_AreStructurallyValid()
    {
        var lib = SceneLibrary.Instance;
        lib.Load();

        var builtIns = lib.Scenes
            .Where(s => string.Equals(s.Category, "Built-in", System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(builtIns);

        foreach (var scene in builtIns)
        {
            Assert.NotEmpty(scene.Shots);
            foreach (var shot in scene.Shots)
            {
                Assert.True(shot.DurationSeconds > 0,
                    $"Built-in scene '{scene.Name}' shot '{shot.Name}' has non-positive duration.");

                if (shot.Camera != null)
                {
                    Assert.True(CameraParamBinding.Supports(shot.FractalType),
                        $"Built-in scene '{scene.Name}' shot '{shot.Name}' has a camera but " +
                        $"fractal type {shot.FractalType} has no orbit camera.");
                    Assert.NotEmpty(shot.Camera.Keys);
                }
            }
        }
    }

    /// <summary>Load() seeds at least the Mandelbulb Orbit demo. Smoke test
    /// against accidentally dropping the built-in seed.</summary>
    [Fact]
    public void BuiltIns_IncludeMandelbulbOrbit()
    {
        var lib = SceneLibrary.Instance;
        lib.Load();

        var scene = lib.GetByName("Mandelbulb Orbit");
        Assert.NotNull(scene);
        Assert.Equal(FractalType.Mandelbulb, scene!.Shots[0].FractalType);
        Assert.NotNull(scene.Shots[0].Camera);
    }

    /// <summary>The built-in orbit camera really orbits: its azimuth spans a
    /// full turn and evaluating at the midpoint gives a pose between the ends —
    /// i.e. the demo drives the S3 evaluator, not a static pose.</summary>
    [Fact]
    public void BuiltInOrbit_CameraSweepsAFullTurn()
    {
        var lib = SceneLibrary.Instance;
        lib.Load();

        var track = lib.GetByName("Mandelbulb Orbit")!.Shots[0].Camera!;
        double startTheta = track.Evaluate(0.0).Theta;
        double endTheta = track.Evaluate(track.Duration).Theta;

        Assert.Equal(0.0, startTheta, precision: 6);
        Assert.Equal(2.0 * System.Math.PI, endTheta, precision: 6); // one full turn
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static CameraTrack MakeTrack(CameraInterpolation interp, params CameraKey[] keys)
    {
        var t = new CameraTrack { Interpolation = interp };
        foreach (var k in keys) t.Add(k);
        return t;
    }
}
