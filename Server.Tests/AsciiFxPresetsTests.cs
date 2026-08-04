// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>Coverage for the ASCII FX preset catalogue (#229) shared by the
/// shell picker and the recorder.</summary>
public sealed class AsciiFxPresetsTests
{
    [Fact]
    public void Names_StartWithNone_ThenEveryPreset()
    {
        Assert.Equal(AsciiFxPresets.NoneName, AsciiFxPresets.Names[0]);
        Assert.Equal(AsciiFxPresets.All.Count + 1, AsciiFxPresets.Names.Count);
    }

    [Fact]
    public void EveryPreset_EnablesSomething()
    {
        foreach (var p in AsciiFxPresets.All)
        {
            var fx = new AsciiFxSettings();
            p.Apply(fx);
            Assert.True(fx.AnyEnabled, $"preset '{p.Name}' enabled no effects");
        }
    }

    [Fact]
    public void ApplyByName_None_IsNoOp()
    {
        var fx = new AsciiFxSettings();
        AsciiFxPresets.ApplyByName(AsciiFxPresets.NoneName, fx);
        Assert.False(fx.AnyEnabled);
        AsciiFxPresets.ApplyByName(null, fx);
        Assert.False(fx.AnyEnabled);
    }

    [Fact]
    public void ApplyByName_Matrix_EnablesRain()
    {
        var fx = new AsciiFxSettings();
        AsciiFxPresets.ApplyByName("Matrix", fx);
        Assert.True(fx.MatrixRain);
        Assert.True(fx.NeedsState);
    }

    [Fact]
    public void ApplyByName_Unknown_IsNoOp()
    {
        var fx = new AsciiFxSettings();
        AsciiFxPresets.ApplyByName("NoSuchPreset", fx);
        Assert.False(fx.AnyEnabled);
    }
}
