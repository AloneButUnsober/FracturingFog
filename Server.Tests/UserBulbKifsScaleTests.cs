// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using Xunit;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #113 — KIFS fold chains must engage the scalar-KIFS DE (declared per-fold
// scale) or the numerical Jacobian yields blank / blobby / zero-triangle
// export. Locks the fold-scale declarations + the chain suggestion.
public class UserBulbKifsScaleTests
{
    [Theory]
    [InlineData("menger", 3.0)]
    [InlineData("sierp", 2.0)]
    [InlineData("mbox", 2.0)]
    public void FoldPrimitives_DeclareScale(string id, double expected)
    {
        var p = UserBulbChainPrimitives.GetById(id);
        Assert.NotNull(p);
        Assert.Equal(expected, p!.KifsScale);
    }

    [Fact]
    public void PowerPrimitive_HasNoKifsScale()
    {
        Assert.Equal(0.0, UserBulbChainPrimitives.GetById("bulb")!.KifsScale);
    }

    [Fact]
    public void SuggestedScale_LeadingFoldWins()
    {
        // Menger-led hybrid → scalar-KIFS scale 3 (fold dominates the DE).
        Assert.Equal(3.0, UserBulbChainPrimitives.SuggestedKifsScaleForChain(
            UserBulbChainPrimitives.MengerBulbHybrid()));
        Assert.Equal(2.0, UserBulbChainPrimitives.SuggestedKifsScaleForChain(
            UserBulbChainPrimitives.MandelboxBulbHybrid()));
    }

    [Fact]
    public void SuggestedScale_PurePower_IsZero()
    {
        var chain = new List<UserBulbChainStep>
        {
            new() { OutputName = "bulb", Source = "return z;" },
        };
        Assert.Equal(0.0, UserBulbChainPrimitives.SuggestedKifsScaleForChain(chain));
    }

    [Fact]
    public void SuggestedScale_Null_IsZero()
    {
        Assert.Equal(0.0, UserBulbChainPrimitives.SuggestedKifsScaleForChain(null));
    }
}
