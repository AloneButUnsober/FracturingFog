// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using Xunit;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #1442 — the Wada mirror spheres reuse the billiard colouring unchanged: the five
// built-in billiard themes (#630), the Categorical theme kind (#630) and billiard
// ColorGen programs (#633) all drive MirrorSpheresCalculator through
// IBilliardColorMap, with gate = exit opening and gateCount = 4.
public class MirrorSpheresColouringTests
{
    private static MirrorSpheresCalculator Render(IColorMap map, int n = 96)
    {
        var c = new MirrorSpheresCalculator(n, n)
        {
            FractalParameters = new FractalParameters { MirrorSpheresReflectance = 1.0 },
            ColorMap = map,
        };
        c.Calculate(default);
        return c;
    }

    // Records every outcome the calculator hands to the theme.
    private sealed class OutcomeProbe : IBilliardColorMap
    {
        public readonly HashSet<int> Gates = new();
        public readonly HashSet<int> GateCounts = new();
        public int MaxBounceSeen;
        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 128;
        public int Map(float smooth, float distance, int iterations) => 0;
        public int MapBilliard(int gateId, int gateCount, int bounces, int maxBounces, float pathLength)
        {
            Assert.InRange(gateId, -1, gateCount - 1);
            Assert.InRange(bounces, 0, maxBounces);
            Assert.InRange(pathLength, 0f, 1f);
            lock (Gates)
            {
                Gates.Add(gateId);
                GateCounts.Add(gateCount);
                if (bounces > MaxBounceSeen) MaxBounceSeen = bounces;
            }
            return unchecked((int)0xFF000000) | ((gateId + 1) & 0xFF);
        }
    }

    [Fact]
    public void Calculator_Hands_Themes_Four_Openings_And_Billiard_Ranges()
    {
        var probe = new OutcomeProbe();
        Render(probe);
        Assert.Equal(new HashSet<int> { 4 }, probe.GateCounts);
        foreach (int g in new[] { 0, 1, 2, 3 }) Assert.Contains(g, probe.Gates);
        Assert.True(probe.MaxBounceSeen >= 3, "no multi-bounce rays reached the theme");
    }

    public static IEnumerable<object[]> BuiltInThemes() => new[]
    {
        new object[] { new BilliardGatesMap() },
        new object[] { new BilliardGatesShadedMap() },
        new object[] { new BilliardPathLengthMap() },
        new object[] { new BilliardBounceCyclicMap() },
        new object[] { new BilliardTrappedSetMap() },
    };

    [Theory]
    [MemberData(nameof(BuiltInThemes))]
    public void Every_Builtin_Billiard_Theme_Is_Compatible_And_Varies(IColorMap theme)
    {
        Assert.True(ColorPalette.IsCompatible(theme, FractalType.MirrorSpheres), $"{theme.GetType().Name} filtered out");
        var colours = new HashSet<uint>(Render(theme).ColorBuffer);
        Assert.True(colours.Count > 3, $"{theme.GetType().Name} drew only {colours.Count} colours");
    }

    [Fact]
    public void Gate_Themes_Show_One_Colour_Per_Opening()
    {
        // BilliardGatesMap paints gate k flat; with lossless mirrors each opening's pixels
        // keep that colour exactly, so the image holds the four gate colours.
        var theme = new BilliardGatesMap();
        var expect = new HashSet<uint>();
        for (int g = 0; g < 4; g++) expect.Add(unchecked((uint)theme.MapBilliard(g, 4, 1, 128, 0.5f)));
        var seen = new HashSet<uint>();
        foreach (uint c in Render(theme).ColorBuffer) if (expect.Contains(c)) seen.Add(c);
        Assert.Equal(expect, seen);
    }

    [Fact]
    public void Categorical_Theme_Kind_Drives_The_Openings()
    {
        var data = new ColorThemeData
        {
            Name = "Wada test", Category = "User", Description = "",
            Kind = ColorThemeKind.Categorical, BilliardDrive = BilliardDrive.Flat,
            InSetColor = new InSetColorData(0x11, 0x22, 0x33) { A = 0xFF },
            Stops = new List<ColorStopData>
            {
                new() { Position = 0.00f, R = 0xE6, G = 0x9F, B = 0x00, A = 0xFF },
                new() { Position = 0.33f, R = 0x56, G = 0xB4, B = 0xE9, A = 0xFF },
                new() { Position = 0.66f, R = 0xF0, G = 0xE4, B = 0x42, A = 0xFF },
                new() { Position = 1.00f, R = 0x00, G = 0x72, B = 0xB2, A = 0xFF },
            },
        };
        var map = DataDrivenColorThemes.Create(data)!;
        Assert.IsAssignableFrom<IBilliardColorMap>(map);
        var expect = new HashSet<uint> { 0xFFE69F00u, 0xFF56B4E9u, 0xFFF0E442u, 0xFF0072B2u };
        var seen = new HashSet<uint>();
        foreach (uint c in Render(map).ColorBuffer) if (expect.Contains(c)) seen.Add(c);
        Assert.Equal(expect, seen);
    }

    [Fact]
    public void Billiard_ColorGen_Program_Drives_The_Openings()
    {
        // gateId / gateCount → four hues; the program sees gateCount = 4.
        var map = InterpretedColorMap.TryCreate("return hsv(gateId / gateCount, 1.0, 1.0);", null, out var err);
        Assert.True(map != null, err);
        Assert.IsType<InterpretedBilliardColorMap>(map);
        var colours = new HashSet<uint>(Render(map!).ColorBuffer);
        var bil = (IBilliardColorMap)map!;
        int hits = 0;
        for (int g = 0; g < 4; g++)
            if (colours.Contains(unchecked((uint)bil.MapBilliard(g, 4, 1, 128, 0.5f)))) hits++;
        Assert.Equal(4, hits);
    }
}
