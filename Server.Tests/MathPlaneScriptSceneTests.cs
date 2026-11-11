// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1700 (scripted scenes SS6) — Scene Engine shots play a Math Plane region's
// script on shot-local time. Independent check: frame k of a Play-script shot is
// pixel-identical to frame 1 of a shot (Play script off) on a copy of the region
// whose own MathPlaneScriptTime is k / fps. Plus the shot flag's persistence and
// the Scene Editor's Play-script toggle and default length.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Export;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class MathPlaneScriptSceneTests
{
    private const int W = 320, H = 180, Fps = 4;
    private const string Script =
        "v1; point A -1 0 #CC79A7; vector v 1 1 hidden; step 1 ease none move A 1 0.5; with show v";

    private static FractalRegion Region(string name, double scriptTime) => new()
    {
        Name = name,
        FractalType = FractalType.MathPlane,
        CenterX = 0, CenterY = 0, Zoom = 0.4, Iterations = 64,
        Params = new RegionFractalParams { MathPlaneScene = Script, MathPlaneScriptTime = scriptTime },
    };

    private static SceneData Scene(string region, bool play, double seconds) => new()
    {
        Name = "FF-1700-" + Guid.NewGuid().ToString("N"),
        Shots =
        {
            new SceneShot
            {
                Name = "Script", RegionName = region, FractalType = FractalType.MathPlane,
                DurationSeconds = seconds, Transition = SceneTransitionKind.Cut, PlayScript = play,
            },
        },
    };

    private static SceneVideoOptions Options(string outDir) => new()
    {
        Width = W, Height = H,
        Settings = new SceneRenderSettings { Fps = Fps, MotionBlurSubframes = 1 },
        OutputPath = outDir,
        KeepFrames = true,
    };

    private static uint[] ReadPng(string path)
    {
        using var bmp = SKBitmap.Decode(path);
        var px = new uint[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var c = bmp.GetPixel(x, y);
                px[y * W + x] = 0xFF000000u | ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;
            }
        return px;
    }

    /// <summary>Render <paramref name="scene"/> and return its kept frames, in order.</summary>
    private static List<uint[]> Frames(SceneData scene)
    {
        string outDir = Path.Combine(Path.GetTempPath(), "FracturingFog", "1700-scene", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var r = SceneVideoRenderer.Render(scene, Options(outDir), null, CancellationToken.None);
            Assert.True(r.FramesWritten > 0, r.Message);
            Assert.NotNull(r.FrameFolder);
            var files = Directory.GetFiles(r.FrameFolder!, "frame_*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
            var frames = files.Select(ReadPng).ToList();
            try { Directory.Delete(r.FrameFolder!, recursive: true); } catch { /* best effort */ }
            return frames;
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static T WithRegions<T>(Func<Func<double, string>, T> body)
    {
        var lib = FractalRegionLibrary.Instance;
        var added = new List<string>();
        string Add(double scriptTime)
        {
            var r = Region("FF-1700-" + Guid.NewGuid().ToString("N"), scriptTime);
            Assert.True(lib.AddUserRegion(r));
            added.Add(r.Name);
            return r.Name;
        }
        try { return body(Add); }
        finally { foreach (var n in added) lib.RemoveUserRegion(n); }
    }

    [Fact]
    public void APlayScriptShot_FrameK_IsTheRegionAtThatFramesShotTime()
    {
        WithRegions(add =>
        {
            var scene = Scene(add(0), play: true, seconds: 1);
            var played = Frames(scene);
            // The Scene Engine samples each frame at its own shot-local time (frame start
            // plus half the shutter); the script runs on exactly that time.
            var plan = SceneRenderPlan.Build(scene, Options("").Settings);
            Assert.Equal(plan.TotalFrames, played.Count);
            foreach (int k in new[] { 0, 1, 2, 3 })
            {
                double local = plan.Frames[k].SubFrames[0].LocalTime;
                var held = Frames(Scene(add(local), play: false, seconds: 0.25));
                Assert.Equal(held[0], played[k]);
            }
            // Sensitivity: the script moves A, so the frames differ.
            Assert.NotEqual(played[0], played[3]);
            return 0;
        });
    }

    [Fact]
    public void WithPlayScriptOff_TheShotHoldsTheRegionsOwnScriptTime()
    {
        WithRegions(add =>
        {
            var frames = Frames(Scene(add(0.5), play: false, seconds: 1));
            var reference = Frames(Scene(add(0.5), play: false, seconds: 0.25))[0];
            Assert.All(frames, f => Assert.Equal(reference, f));
            return 0;
        });
    }

    [Fact]
    public void PlayScript_DefaultsOn_ForOldScenesToo_AndRoundTrips()
    {
        Assert.True(new SceneShot().PlayScript);
        var old = JsonSerializer.Deserialize<SceneShot>("{\"Name\":\"old\",\"RegionName\":\"r\",\"DurationSeconds\":2}")!;
        Assert.True(old.PlayScript);
        var off = JsonSerializer.Deserialize<SceneShot>(JsonSerializer.Serialize(new SceneShot { PlayScript = false }))!;
        Assert.False(off.PlayScript);
    }

    [Fact]
    public void TheHelper_PlaysOnlyAScriptedMathPlaneShot()
    {
        var on = new SceneShot();
        Assert.Equal(0.75, SceneShotScript.TimeFor(on, FractalType.MathPlane, Script, 0.75));
        Assert.Equal(0.0, SceneShotScript.TimeFor(on, FractalType.MathPlane, Script, -2));
        Assert.Null(SceneShotScript.TimeFor(on, FractalType.Mandelbrot, Script, 1));
        Assert.Null(SceneShotScript.TimeFor(on, FractalType.MathPlane, "v1; point A 0 0", 1));
        Assert.Null(SceneShotScript.TimeFor(on, FractalType.MathPlane, "v1; step 1 show nope", 1));   // invalid
        Assert.Null(SceneShotScript.TimeFor(new SceneShot { PlayScript = false }, FractalType.MathPlane, Script, 1));
        Assert.Equal(1.0, SceneShotScript.ScriptSeconds(Script), 12);
        Assert.Equal(0.0, SceneShotScript.ScriptSeconds("v1; point A 0 0"));
    }

    // ── Scene Editor ─────────────────────────────────────────────────────

    public class ScriptService : DispatchProxy
    {
        public Dictionary<string, double> ScriptSeconds { get; } = new();
        public Dictionary<string, FractalType> Types { get; } = new();

        public static (IColorThemeService Service, ScriptService State) Create()
        {
            var svc = DispatchProxy.Create<IColorThemeService, ScriptService>();
            return (svc, (ScriptService)(object)svc);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IColorThemeService.EnumerateRegionNames):
                    return Types.Keys.ToArray();
                case nameof(IColorThemeService.GetRegionSceneInfo):
                    return Types.TryGetValue((string)args![0]!, out var t) ? (t, false) : ((FractalType, bool)?)null;
                case nameof(IColorThemeService.GetRegionScriptSeconds):
                    return ScriptSeconds.TryGetValue((string)args![0]!, out var s) ? s : 0.0;
            }
            var rt = method.ReturnType;
            if (rt == typeof(void)) return null;
            if (rt != typeof(string) && rt.IsAssignableFrom(typeof(string[]))) return Array.Empty<string>();
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }

    [Fact]
    public void PickingAScriptedRegion_OffersPlayScript_AndLengthsTheShot()
    {
        var (svc, st) = ScriptService.Create();
        st.Types["Scripted"] = FractalType.MathPlane; st.ScriptSeconds["Scripted"] = 12.5;
        st.Types["Plain"] = FractalType.Mandelbrot;
        var row = new SceneEditorViewModel(svc).Shots.Single();
        Assert.False(row.HasRegionScript);

        row.SelectedRegion = "Scripted";
        Assert.True(row.HasRegionScript);
        Assert.Equal("script 12.5 s", row.RegionScriptHint);
        Assert.Equal(12.5, row.DurationSeconds);
        Assert.True(row.ToShot().PlayScript);
        row.PlayScript = false;
        Assert.False(row.ToShot().PlayScript);

        // A length the user set is kept.
        var row2 = new SceneEditorViewModel(svc).Shots.Single();
        row2.DurationSeconds = 3;
        row2.SelectedRegion = "Scripted";
        Assert.Equal(3.0, row2.DurationSeconds);

        row2.SelectedRegion = "Plain";
        Assert.False(row2.HasRegionScript);
    }

    [Fact]
    public void LoadingASavedShot_KeepsItsLengthAndPlayScript()
    {
        var (svc, st) = ScriptService.Create();
        st.Types["Scripted"] = FractalType.MathPlane; st.ScriptSeconds["Scripted"] = 12.5;
        var row = new SceneEditorViewModel(svc).Shots.Single();
        row.Populate(new SceneShot { RegionName = "Scripted", FractalType = FractalType.MathPlane, DurationSeconds = 5, PlayScript = false });
        Assert.Equal(5.0, row.DurationSeconds);   // the saved length, even though it is the default value
        Assert.False(row.PlayScript);
        Assert.True(row.HasRegionScript);
    }
}
