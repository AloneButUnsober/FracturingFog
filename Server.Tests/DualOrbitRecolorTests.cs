// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #981 (#939-D) — the dual-orbit calculator caches its orbits and recolours
// without iterating when only colour inputs change. The oracle throughout is a
// FRESH calculator rendering the same state from scratch.
public sealed class DualOrbitRecolorTests
{
    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint Tint = 0;
        public uint InSetColor => 0xFF102030u ^ Tint;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)((0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)) ^ Tint));
    }

    private static DualOrbitEscapeCalculator Make(FractalParameters p, IColorMap map)
        => new(96, 96) { CenterX = -0.5, CenterY = 0, Zoom = 1.0, MaxIterations = 200, FractalParameters = p, ColorMap = map };

    private static uint[] Fresh(FractalParameters p, IColorMap map)
    {
        var c = Make(p.Clone(), map);
        c.Calculate();
        return c.ColorBuffer;
    }

    [Fact]
    public void ImplementsCheapRecolor()
        => Assert.IsAssignableFrom<ISupportsCheapRecolor>(new DualOrbitEscapeCalculator(4, 4));

    [Theory]
    [InlineData(DualOrbitColorMode.Field)]
    [InlineData(DualOrbitColorMode.PerOrbitLayers)]
    public void Recolor_WithANewTheme_EqualsAFreshRender(DualOrbitColorMode mode)
    {
        var p = new FractalParameters { DualOrbitColorMode = mode, DualOrbitThemeZ = "", DualOrbitThemeC = "", DualOrbitField = DualOrbitField.GreenRatio };
        var calc = Make(p, new RampMap());
        calc.Calculate();
        var other = new RampMap { Tint = 0x00A5A5A5u };
        calc.ColorMap = other;
        calc.Recolor();
        Assert.Equal(Fresh(p, other), calc.ColorBuffer);
    }

    [Fact]
    public void Recolor_BeforeAnyCalculate_RendersFully()
    {
        var p = new FractalParameters();
        var calc = Make(p, new RampMap());
        calc.Recolor();
        Assert.Equal(Fresh(p, new RampMap()), calc.ColorBuffer);
    }

    // Parameters that must NOT invalidate the orbit cache (pure colour).
    private static readonly HashSet<string> ColourOnly = new()
    {
        nameof(FractalParameters.DualOrbitThemeZ), nameof(FractalParameters.DualOrbitThemeC),
        nameof(FractalParameters.DualOrbitLayerBlend),
        nameof(FractalParameters.DualOrbitOpacityZ), nameof(FractalParameters.DualOrbitOpacityC),
        // #1120 — Böttcher domain colouring: contours / grid / hue source.
        nameof(FractalParameters.DualOrbitContourDensity), nameof(FractalParameters.DualOrbitDomainGrid),
        nameof(FractalParameters.DualOrbitDomainPalette), nameof(FractalParameters.DualOrbitDomainMarkCuts),
        nameof(FractalParameters.DualOrbitLagColors),   // #1121
        nameof(FractalParameters.DualOrbitOutlineWidth),   // #1118
        nameof(FractalParameters.DualOrbitOutlineColorZ), nameof(FractalParameters.DualOrbitOutlineColorC),
        nameof(FractalParameters.DualOrbitPalette2D), nameof(FractalParameters.DualOrbitBivariateScale),   // #1122
        nameof(FractalParameters.DualOrbitPhaseK),
        // #1123 — the split height has its own cache; the colour orbits never re-iterate.
        nameof(FractalParameters.DualOrbitSplitHeight), nameof(FractalParameters.DualOrbitHeightField),
    };

    private static object? Mutate(object? v) => v switch
    {
        double d => d > 0.9 && d <= 1.0 ? 0.4 : d + 0.137,   // keep opacities in range
        int i => i + 1,
        bool b => !b,
        string => "Cividis",
        Enum e => Enum.GetValues(e.GetType()).GetValue(
            (Array.IndexOf(Enum.GetValues(e.GetType()), e) + 1) % Enum.GetValues(e.GetType()).Length),
        _ => v,
    };

    public static IEnumerable<object[]> DualOrbitParams()
        => typeof(FractalParameters).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(pi => pi.Name.StartsWith("DualOrbit", StringComparison.Ordinal)
                      && !pi.Name.StartsWith("DualOrbitVolume", StringComparison.Ordinal)
                      && pi.CanWrite)
            .Select(pi => new object[] { pi.Name });

    [Theory]
    [MemberData(nameof(DualOrbitParams))]
    public void EveryParameter_IsClassified_AndReuseMatchesAFreshRender(string name)
    {
        // Layer mode for layer-only params so their change is visible; Field mode
        // otherwise (the field scalar is only computed there).
        bool layerParam = ColourOnly.Contains(name);
        var p = new FractalParameters
        {
            DualOrbitColorMode = layerParam ? DualOrbitColorMode.PerOrbitLayers : DualOrbitColorMode.Field,
            DualOrbitField = DualOrbitField.GreenRatio,
        };
        var calc = Make(p, new RampMap());
        calc.Calculate();

        var pi = typeof(FractalParameters).GetProperty(name)!;
        pi.SetValue(p, Mutate(pi.GetValue(p)));
        calc.Calculate();

        if (ColourOnly.Contains(name))
            Assert.True(calc.LastCalculateReusedOrbits, $"{name} is colour-only but re-iterated");
        else
            Assert.False(calc.LastCalculateReusedOrbits,
                $"{name} did not invalidate the orbit cache — add it to GeometryKey, or to ColourOnly if it only affects colour");
        Assert.Equal(Fresh(p, new RampMap()), calc.ColorBuffer);
    }

    [Fact]
    public void InteriorAlpha_IsColourOnly()
    {
        var p = new FractalParameters();
        var calc = Make(p, new RampMap());
        calc.Calculate();
        p.InteriorAlpha = 90;
        calc.Calculate();
        Assert.True(calc.LastCalculateReusedOrbits);
        Assert.Equal(Fresh(p, new RampMap()), calc.ColorBuffer);
    }

    [Fact]
    public void ViewAndIterationChanges_Invalidate()
    {
        var p = new FractalParameters();
        var calc = Make(p, new RampMap());
        calc.Calculate();
        foreach (Action change in new Action[]
                 { () => calc.CenterX += 0.1, () => calc.CenterY += 0.1, () => calc.Zoom *= 1.5, () => calc.MaxIterations += 7 })
        {
            change();
            calc.Calculate();
            Assert.False(calc.LastCalculateReusedOrbits);
            calc.Calculate();
            Assert.True(calc.LastCalculateReusedOrbits);   // and the next identical frame reuses
        }
    }

    [Fact]
    public void Resize_Invalidates()
    {
        var p = new FractalParameters();
        var calc = Make(p, new RampMap());
        calc.Calculate();
        calc.Resize(64, 48);
        calc.Calculate();
        Assert.False(calc.LastCalculateReusedOrbits);
        Assert.Equal(64 * 48, calc.ColorBuffer.Length);
    }

    [Fact]
    public void CancelledIterate_LeavesNoCache()
    {
        var p = new FractalParameters();
        var calc = Make(p, new RampMap());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { calc.Calculate(cts.Token); } catch (OperationCanceledException) { }
        calc.Calculate();
        Assert.False(calc.LastCalculateReusedOrbits);   // must not trust a half-filled cache
        Assert.Equal(Fresh(p, new RampMap()), calc.ColorBuffer);
    }
}
