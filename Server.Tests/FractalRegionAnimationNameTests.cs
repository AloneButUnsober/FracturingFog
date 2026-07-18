// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Text.Json;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Phase 3b deliverable: FractalRegion.AnimationName JSON round-trip +
/// JsonIgnoreWhenNull behaviour so legacy regions don't grow a null key.
/// </summary>
public sealed class FractalRegionAnimationNameTests
{
    [Fact]
    public void AnimationName_RoundTripsThroughJson()
    {
        var region = new FractalRegion
        {
            Name = "Test region",
            CenterX = -0.5,
            CenterY = 0.0,
            Zoom = 1.0,
            Iterations = 256,
            FractalType = FractalType.Julia,
            AnimationName = "Julia C orbit",
        };

        string json = JsonSerializer.Serialize(region);
        var round = JsonSerializer.Deserialize<FractalRegion>(json);

        Assert.NotNull(round);
        Assert.Equal("Julia C orbit", round!.AnimationName);
    }

    [Fact]
    public void NullAnimationName_OmittedFromJson()
    {
        var region = new FractalRegion
        {
            Name = "No anim",
            CenterX = 0.0,
            CenterY = 0.0,
            Zoom = 1.0,
            AnimationName = null,
        };

        string json = JsonSerializer.Serialize(region);

        Assert.DoesNotContain("AnimationName", json);
    }

    [Fact]
    public void LegacyJsonWithoutAnimationName_DeserialisesWithNull()
    {
        // Pre-roadmap JSON has no AnimationName key. Must still load.
        string legacy = """
        {
          "Name": "Legacy",
          "CenterX": -0.5,
          "CenterY": 0.0,
          "Zoom": 1.0,
          "Iterations": 512,
          "FractalType": "Mandelbrot"
        }
        """;

        var region = JsonSerializer.Deserialize<FractalRegion>(legacy);

        Assert.NotNull(region);
        Assert.Null(region!.AnimationName);
        Assert.Equal("Legacy", region.Name);
    }
}
