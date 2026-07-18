// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Phase 2 deliverable: AnimationData JSON round-trip + AnimationLibrary
/// built-in seed sanity. Stays away from singleton mutation / disk I/O so
/// tests do not pollute the dev's %APPDATA% animations.json.
/// </summary>
public sealed class AnimationLibraryTests
{
    /// <summary>Every field on AnimationData / AnimationTrack must survive
    /// a System.Text.Json round-trip with the library's standard options.
    /// Catches JsonIgnore regressions and enum-as-string regressions.</summary>
    [Fact]
    public void AnimationData_JsonRoundTrip_PreservesFields()
    {
        var src = new AnimationData
        {
            Name = "Test anim",
            Description = "round trip",
            Category = "User",
            TargetFractalTypes = new List<FractalType> { FractalType.Julia, FractalType.Phoenix },
            Duration = 12.5,
            Tags = new List<string> { "calm", "2D" },
            Tracks = new List<AnimationTrack>
            {
                new AnimationTrack
                {
                    ParamName = "JuliaC",
                    Mode = AnimationMode.Lissajous,
                    Min = 0.3,
                    Max = 0.7,
                    FrequencyHz = 0.25,
                    PhaseOffsetRadians = 1.57,
                    Enabled = false,
                },
            },
        };

        var opts = AnimationLibrary.BuildJsonOptions();
        string json = JsonSerializer.Serialize(src, opts);
        var dst = JsonSerializer.Deserialize<AnimationData>(json, opts);

        Assert.NotNull(dst);
        Assert.Equal(src.Name, dst!.Name);
        Assert.Equal(src.Description, dst.Description);
        Assert.Equal(src.Category, dst.Category);
        Assert.Equal(src.TargetFractalTypes, dst.TargetFractalTypes);
        Assert.Equal(src.Duration, dst.Duration);
        Assert.Equal(src.Tags, dst.Tags);
        Assert.Single(dst.Tracks);

        var t0 = src.Tracks[0];
        var t1 = dst.Tracks[0];
        Assert.Equal(t0.ParamName, t1.ParamName);
        Assert.Equal(t0.Mode, t1.Mode);
        Assert.Equal(t0.Min, t1.Min);
        Assert.Equal(t0.Max, t1.Max);
        Assert.Equal(t0.FrequencyHz, t1.FrequencyHz);
        Assert.Equal(t0.PhaseOffsetRadians, t1.PhaseOffsetRadians);
        Assert.Equal(t0.Enabled, t1.Enabled);
    }

    /// <summary>Library should serialise enums as their string names, not as
    /// raw ints — humans hand-edit animations.json.</summary>
    [Fact]
    public void AnimationData_Json_UsesEnumStringNames()
    {
        var src = new AnimationData
        {
            Name = "Stringy",
            TargetFractalTypes = new List<FractalType> { FractalType.Mandelbrot },
            Tracks = new List<AnimationTrack>
            {
                new AnimationTrack
                {
                    ParamName = "MultibrotExponent",
                    Mode = AnimationMode.Triangle,
                    Min = 2, Max = 8,
                },
            },
        };

        string json = JsonSerializer.Serialize(src, AnimationLibrary.BuildJsonOptions());

        Assert.Contains("\"Triangle\"", json);
        Assert.Contains("\"Mandelbrot\"", json);
        Assert.DoesNotContain("\"Mode\":1", json);
    }

    /// <summary>Every built-in animation must reference at least one
    /// fractal type for which every track resolves via
    /// FractalAnimatableParamsMap. Built-ins are the user's first
    /// impression — a broken seed would surface as a phantom-named track in
    /// the editor.</summary>
    [Fact]
    public void BuiltIns_AllTracksResolveAgainstRegistry()
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();

        foreach (var anim in lib.Animations)
        {
            if (!string.Equals(anim.Category, "Built-in", System.StringComparison.OrdinalIgnoreCase))
                continue;

            Assert.NotEmpty(anim.TargetFractalTypes);

            foreach (var track in anim.Tracks)
            {
                bool resolvedOnAny = false;
                foreach (var ft in anim.TargetFractalTypes)
                {
                    foreach (var d in FractalAnimatableParamsMap.For(ft))
                    {
                        if (d.ParamName == track.ParamName)
                        {
                            resolvedOnAny = true;
                            break;
                        }
                    }
                    if (resolvedOnAny) break;
                }

                Assert.True(resolvedOnAny,
                    $"Built-in animation '{anim.Name}' track '{track.ParamName}' " +
                    $"does not resolve in FractalAnimatableParamsMap for any of " +
                    $"its TargetFractalTypes.");
            }
        }
    }

    /// <summary>Library Load() seeds at least the Julia C orbit built-in.
    /// Smoke test against accidentally dropping the seed.</summary>
    [Fact]
    public void BuiltIns_IncludeJuliaCOrbit()
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();

        var julia = lib.GetByName("Julia C orbit");
        Assert.NotNull(julia);
        Assert.Contains(FractalType.Julia, julia!.TargetFractalTypes);
    }
}
