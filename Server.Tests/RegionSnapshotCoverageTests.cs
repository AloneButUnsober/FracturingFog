// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #961 (#941 S2) — the guard: every animatable parameter of every fractal family
// (FractalAnimatableParamsMap) must be carried by the region snapshot. Before this,
// 60 of 94 were not, so a saved region could not reproduce them and an animation's
// leftover value leaked into the next region. Adding an animatable param without
// region coverage fails here.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RegionSnapshotCoverageTests
{
    public static IEnumerable<object[]> AnimatableParams()
    {
        foreach (FractalType t in Enum.GetValues<FractalType>())
            foreach (var d in FractalAnimatableParamsMap.For(t))
                yield return new object[] { t, d.ParamName };
    }

    // Params that only matter while a mode is on are captured only then; engage every
    // such mode so the param itself is what's tested.
    static FractalParameters Engaged() => new()
    {
        FaithfulImplosion = true, DomainWarpEnabled = true, AcidWarpMorph = true,
    };

    static object Bump(object v) => v switch
    {
        double d => d + 0.123,
        int i => i + 1,
        Complex c => c + new Complex(0.11, -0.07),
        Enum e => Enum.GetValues(e.GetType()).GetValue(
            (Array.IndexOf(Enum.GetValues(e.GetType()), e) + 1) % Enum.GetValues(e.GetType()).Length)!,
        _ => throw new NotSupportedException(v.GetType().Name),
    };

    static PropertyInfo Prop(string name)
        => typeof(FractalParameters).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
           ?? throw new InvalidOperationException($"FractalParameters has no {name}");

    [Theory]
    [MemberData(nameof(AnimatableParams))]
    public void Snapshot_round_trips_every_animatable_param(FractalType type, string param)
    {
        var pi = Prop(param);
        var live = Engaged();
        object moved = Bump(pi.GetValue(live)!);
        pi.SetValue(live, moved);

        var snap = RegionFractalParams.Snapshot(type, live);
        Assert.True(snap != null, $"{type} has no region snapshot, so {param} is never saved");

        // through JSON, as regions.json stores it
        var stored = JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!;
        var recalled = new FractalParameters();
        stored.ApplyTo(recalled);
        Assert.Equal(moved, pi.GetValue(recalled));
    }

    [Theory]
    [MemberData(nameof(AnimatableParams))]
    public void Recalling_a_default_region_restores_every_animatable_param(FractalType type, string param)
    {
        // An animation leaves the param moved; recalling a region saved at defaults must
        // put it back (#960 reset + #961 coverage).
        var pi = Prop(param);
        object stock = pi.GetValue(new FractalParameters())!;
        var live = Engaged();
        pi.SetValue(live, Bump(pi.GetValue(live)!));

        var region = new FractalRegion
        {
            Name = "default", FractalType = type, Zoom = 1,
            Params = RegionFractalParams.Snapshot(type, new FractalParameters()),
        };
        region.ApplyFamilyParams(live);
        Assert.Equal(stock, pi.GetValue(live));
    }

    [Fact]
    public void Snapshot_at_defaults_stays_small()
    {
        // New fields are omitted at their default, so regions saved at stock values
        // don't grow (and old regions.json entries compare equal on re-save).
        foreach (var t in new[] { FractalType.Mandelbulb, FractalType.Kifs, FractalType.RandomTile,
                                  FractalType.StrangeAttractor, FractalType.UserBulb })
        {
            string json = JsonSerializer.Serialize(RegionFractalParams.Snapshot(t, new FractalParameters()),
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            foreach (var name in new[] { "BulbPower", "KifsScale", "RandomTileCount", "AttractorA", "UserBulbTime" })
                Assert.DoesNotContain($"\"{name}\"", json);
        }
    }
}
