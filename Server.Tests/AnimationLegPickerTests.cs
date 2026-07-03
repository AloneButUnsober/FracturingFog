using System;
using System.Collections.Generic;

using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Animation Roadmap Phase 4: the pure per-leg animation picker. Covers the
/// selection precedence (attached-wins vs randomize), type compatibility,
/// include whitelist, tag filter, and the static-leg fallback.
/// </summary>
public sealed class AnimationLegPickerTests
{
    private static AnimationLegPicker.Candidate C(
        string name, FractalType[]? types = null, string[]? tags = null)
        => new(name,
               types ?? Array.Empty<FractalType>(),
               tags ?? Array.Empty<string>());

    // Deterministic "random" that always picks the first survivor.
    private static readonly Func<int, int> First = _ => 0;

    [Fact]
    public void EmptyLibrary_ReturnsNull()
    {
        var chosen = AnimationLegPicker.Pick(
            Array.Empty<AnimationLegPicker.Candidate>(),
            "Julia", null, false, null, null, First);
        Assert.Null(chosen);
    }

    [Fact]
    public void AttachedAnimation_WinsWhenNotRandomizing()
    {
        var lib = new[]
        {
            C("Spin", new[] { FractalType.Julia }),
            C("Drift", new[] { FractalType.Julia }),
        };
        var chosen = AnimationLegPicker.Pick(
            lib, "Julia", regionAttachedAnimation: "Drift",
            randomizeByType: false, null, null, First);
        Assert.Equal("Drift", chosen);
    }

    [Fact]
    public void Randomize_IgnoresAttachedAnimation()
    {
        var lib = new[]
        {
            C("Spin", new[] { FractalType.Julia }),
            C("Drift", new[] { FractalType.Julia }),
        };
        // randomize=true → attached ignored, First picks survivor[0]="Spin".
        var chosen = AnimationLegPicker.Pick(
            lib, "Julia", regionAttachedAnimation: "Drift",
            randomizeByType: true, null, null, First);
        Assert.Equal("Spin", chosen);
    }

    [Fact]
    public void IncompatibleType_ExcludedFromRandomPool()
    {
        var lib = new[]
        {
            C("MandelOnly", new[] { FractalType.Mandelbrot }),
            C("JuliaOnly", new[] { FractalType.Julia }),
        };
        var chosen = AnimationLegPicker.Pick(
            lib, "Julia", null, false, null, null, First);
        Assert.Equal("JuliaOnly", chosen);
    }

    [Fact]
    public void EmptyTargetTypes_IsUnconstrained()
    {
        var lib = new[] { C("AnyType", types: Array.Empty<FractalType>()) };
        var chosen = AnimationLegPicker.Pick(
            lib, "Phoenix", null, false, null, null, First);
        Assert.Equal("AnyType", chosen);
    }

    [Fact]
    public void AttachedButIncompatible_FallsThroughToRandom()
    {
        var lib = new[]
        {
            C("MandelOnly", new[] { FractalType.Mandelbrot }),
            C("JuliaOnly", new[] { FractalType.Julia }),
        };
        // Attached "MandelOnly" doesn't fit Julia region → fall through to the
        // compatible random pool = "JuliaOnly".
        var chosen = AnimationLegPicker.Pick(
            lib, "Julia", regionAttachedAnimation: "MandelOnly",
            randomizeByType: false, null, null, First);
        Assert.Equal("JuliaOnly", chosen);
    }

    [Fact]
    public void IncludeWhitelist_RestrictsPool()
    {
        var lib = new[]
        {
            C("A", new[] { FractalType.Julia }),
            C("B", new[] { FractalType.Julia }),
        };
        var chosen = AnimationLegPicker.Pick(
            lib, "Julia", null, false,
            includedAnimations: new[] { "B" }, filterTags: null, First);
        Assert.Equal("B", chosen);
    }

    [Fact]
    public void TagFilter_KeepsOnlyTaggedAnimations()
    {
        var lib = new[]
        {
            C("Calm", new[] { FractalType.Julia }, new[] { "calm" }),
            C("Wild", new[] { FractalType.Julia }, new[] { "intense" }),
        };
        var chosen = AnimationLegPicker.Pick(
            lib, "Julia", null, false, null,
            filterTags: new[] { "calm" }, First);
        Assert.Equal("Calm", chosen);
    }

    [Fact]
    public void NoCompatibleAnimation_ReturnsNullForStaticLeg()
    {
        var lib = new[] { C("MandelOnly", new[] { FractalType.Mandelbrot }) };
        var chosen = AnimationLegPicker.Pick(
            lib, "Julia", null, false, null, null, First);
        Assert.Null(chosen);
    }

    [Fact]
    public void UnparseableRegionType_DisablesCompatFilter()
    {
        var lib = new[] { C("JuliaOnly", new[] { FractalType.Julia }) };
        // Region type string doesn't parse → compat filter is skipped, so the
        // Julia-only animation still qualifies.
        var chosen = AnimationLegPicker.Pick(
            lib, "NotARealType", null, false, null, null, First);
        Assert.Equal("JuliaOnly", chosen);
    }

    [Fact]
    public void Clone_CopiesAnimationFields()
    {
        var cfg = new SlideshowConfig
        {
            Type = SlideshowType.Animation,
            IncludedAnimations = { "A", "B" },
            FilterAnimations = { "calm" },
            RandomizeAnimationsByFractalType = true,
        };
        var clone = cfg.Clone();

        Assert.Equal(SlideshowType.Animation, clone.Type);
        Assert.Equal(new[] { "A", "B" }, clone.IncludedAnimations);
        Assert.Equal(new[] { "calm" }, clone.FilterAnimations);
        Assert.True(clone.RandomizeAnimationsByFractalType);

        // Independent lists — mutating the clone must not touch the source.
        clone.IncludedAnimations.Add("C");
        Assert.Equal(2, cfg.IncludedAnimations.Count);
    }
}
