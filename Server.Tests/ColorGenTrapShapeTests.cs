// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #611 — ColorGen selectable trap-shape menu. The DSL `trap` input reads the
// slot-1 orbit-trap minimum, but the shape it is measured against is chosen from
// the same 19-shape list as the Color Theme Editor (OrbitTrapShape). These tests
// pin the contract:
//   * a `trap` program is orbit-aware (per-iteration sampling);
//   * the default Point shape is byte-identical to a `trapMin` render (the shape
//     menu adds capability without changing existing themes);
//   * non-Point shapes bind (render non-uniform) and are distinct from Point;
//   * `trap` stays CPU-only (no GPU palette) even when GPU orbit is enabled —
//     the 14 non-legacy shapes have no HLSL SDF yet;
//   * the shape name round-trips through GenerateOptions/the store;
//   * C# export of a `trap` theme is rejected with a clear message (deferred).

using System.Collections.Generic;
using FracturingFog;
using FracturingFog.ColorGen;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ColorGenTrapShapeTests
{
    private const int W = 48, H = 36;

    private static InterpretedColorMap Make(string src, string shape = "Point")
    {
        var opts = new GenerateOptions { ThemeName = "TrapShapeT", Category = "Test", TrapShape = shape };
        var m = InterpretedColorMap.TryCreate(src, opts, out string? err);
        Assert.Null(err);
        Assert.NotNull(m);
        return m!;
    }

    private static uint[] RenderNative(string src, string shape = "Point")
    {
        var calc = new MandelbrotCalculator(W, H)
        {
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, MaxIterations = 200,
            ColorMap = Make(src, shape),
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    [Fact]
    public void TrapInput_IsOrbitAware()
    {
        var m = Make("return hsv(saturate(trap), 0.8, 1.0);", "Star");
        Assert.IsType<InterpretedOrbitColorMap>(m);
        Assert.IsAssignableFrom<IOrbitAwareColorMap>(m);
    }

    // ParseTrapShape maps the name; the map carries it.
    [Theory]
    [InlineData("Point", OrbitTrapShape.Point)]
    [InlineData("star", OrbitTrapShape.Star)]           // case-insensitive
    [InlineData("PolarRose", OrbitTrapShape.PolarRose)]
    [InlineData("nonsense", OrbitTrapShape.Point)]       // unknown ⇒ Point
    [InlineData("", OrbitTrapShape.Point)]
    public void ParseTrapShape_AndMapCarriesIt(string name, OrbitTrapShape expected)
    {
        var m = (InterpretedOrbitColorMap)Make("return rgb(saturate(trap), 0, 0);", name);
        Assert.Equal(expected, m.TrapShape);
    }

    // Default Point shape ⇒ `trap` render is byte-identical to a `trapMin` render.
    // The shape menu must not perturb existing themes.
    [Fact]
    public void TrapPoint_IsByteIdenticalToTrapMin()
    {
        uint[] viaTrap    = RenderNative("return hsv(saturate(trap), 0.9, 1.0);", "Point");
        uint[] viaTrapMin = RenderNative("return hsv(saturate(trapMin), 0.9, 1.0);");
        Assert.Equal(viaTrapMin, viaTrap);
    }

    // Every one of the 19 shapes binds — the `trap` render is non-uniform (real
    // orbit lace, not a flat fill).
    [Theory]
    [InlineData("Point")]  [InlineData("Cross")]     [InlineData("Circle")]
    [InlineData("Line")]   [InlineData("Star")]      [InlineData("Square")]
    [InlineData("Ring")]   [InlineData("Hyperbola")] [InlineData("Lemniscate")]
    [InlineData("Cardioid")] [InlineData("DiagonalCross")] [InlineData("Triangle")]
    [InlineData("Hexagon")]  [InlineData("Heart")]   [InlineData("SineWave")]
    [InlineData("Concentric")] [InlineData("Grid")]  [InlineData("Pinwheel")]
    [InlineData("PolarRose")]
    public void EachShape_RendersNonUniform(string shape)
    {
        var distinct = new HashSet<uint>(RenderNative("return hsv(saturate(trap), 0.9, 1.0);", shape));
        Assert.True(distinct.Count >= 3, $"shape {shape} should vary, saw {distinct.Count}");
    }

    // A non-Point shape drives a DIFFERENT image than Point — proves the menu
    // actually selects the SDF (not a shared constant).
    [Theory]
    [InlineData("Cross")] [InlineData("Star")] [InlineData("Ring")]
    [InlineData("Hexagon")] [InlineData("PolarRose")]
    public void NonPointShape_DiffersFromPoint(string shape)
    {
        uint[] point = RenderNative("return hsv(saturate(trap), 0.9, 1.0);", "Point");
        uint[] other = RenderNative("return hsv(saturate(trap), 0.9, 1.0);", shape);
        Assert.NotEqual(point, other);
    }

    // `trap` is CPU-only for now (no HLSL SDF for the non-legacy shapes) — the map
    // must advertise no GPU palette even when GPU orbit is enabled.
    [Fact]
    public void TrapInput_AdvertisesNoGpuPalette_EvenWhenGpuEnabled()
    {
        bool prev = InterpretedOrbitColorMap.GpuEnabled;
        try
        {
            InterpretedOrbitColorMap.GpuEnabled = true;
            var m = (InterpretedOrbitColorMap)Make("return rgb(saturate(trap), 0, 0);", "Star");
            Assert.Equal("", m.HlslPaletteBody);
            Assert.Equal(GpuOrbitInputs.None, m.OrbitInputs);

            // A legacy fixed-shape orbit theme still gets a GPU palette — the
            // CPU-only fallback is specific to `trap`.
            var legacy = (InterpretedOrbitColorMap)Make("return rgb(saturate(trapMin), 0, 0);");
            Assert.NotEqual("", legacy.HlslPaletteBody);
        }
        finally { InterpretedOrbitColorMap.GpuEnabled = prev; }
    }

    // The shape name round-trips through the persistence store.
    [Fact]
    public void TrapShape_RoundTripsThroughStore()
    {
        var store = UserColorGenStore.Instance;
        const string name = "TrapShapeStoreRoundTrip_611";
        try
        {
            var saved = store.SaveEntry(name, "return rgb(trap,0,0);", "", "Pinwheel");
            Assert.NotNull(saved);
            Assert.Equal("Pinwheel", store.GetByName(name)!.TrapShape);
        }
        finally { store.Remove(name); }
    }

    // C# export of a `trap` theme is rejected (deferred) with a clear message;
    // a fixed-shape orbit theme still exports.
    [Fact]
    public void GenerateCSharp_TrapInput_IsRejectedForNow()
    {
        var r = ColorGenApi.Generate("return rgb(saturate(trap), 0, 0);", "TrapMenuTheme");
        Assert.False(r.Ok);
        Assert.Contains("trap", r.Error);

        var ok = ColorGenApi.Generate("return rgb(saturate(trapMin), 0, 0);", "TrapMinTheme");
        Assert.True(ok.Ok, ok.Error);
    }
}
