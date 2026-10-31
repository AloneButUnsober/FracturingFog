// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1590 — colour-theme scale mode. Iterations (the default) must be the historical
// arithmetic; FitToRange spreads the palette over 0..MaxIterations. Oracles: the
// long-standing Gradient kind (which already maps smooth / max, clamped), the
// invariance of a normalised value under MaxIterations, and the 0 / 1 seam of a
// one-turn Repeat wheel.
public sealed class ColorScaleModeTests
{
    private static readonly ColorStopData[] Stops =
    {
        new() { Position = 0.0f, R = 20,  G = 40,  B = 160 },
        new() { Position = 0.4f, R = 240, G = 200, B = 30  },
        new() { Position = 1.0f, R = 250, G = 250, B = 250 },
    };

    private static ColorThemeData Theme(ColorThemeKind kind, ColorScaleMode scale, ColorWrapMode wrap = ColorWrapMode.Clamp,
        float density = 1f, float offset = 0f)
    {
        var d = new ColorThemeData
        {
            Name = $"Scale test {kind} {scale} {wrap}", Category = "Test", Kind = kind,
            ScaleMode = scale, WrapMode = wrap, ColorDensity = density, ColorOffset = offset, CycleSpeed = 0.02f,
        };
        d.Stops.AddRange(Stops.Select(s => new ColorStopData { Position = s.Position, R = s.R, G = s.G, B = s.B }));
        return d;
    }

    private static IColorMap Map(ColorThemeData d) => DataDrivenColorThemes.Create(d)!;

    // FitToRange + Clamp + density 1 + offset 0 is exactly what the Gradient kind
    // has always done (t = clamp(smooth / max)).
    [Theory]
    [InlineData(16)]
    [InlineData(400)]
    [InlineData(5000)]
    public void FitToRange_Clamp_MatchesTheGradientKind(int maxIt)
    {
        var fit = Map(Theme(ColorThemeKind.Cycling, ColorScaleMode.FitToRange));
        var grad = Map(Theme(ColorThemeKind.Gradient, ColorScaleMode.Iterations));
        for (int k = 0; k <= 40; k++)
        {
            float s = maxIt * k / 40f;
            Assert.Equal(grad.Map(s, 0f, maxIt), fit.Map(s, 0f, maxIt));
        }
    }

    // A value normalised to the range paints the same colour whatever MaxIterations
    // is — the property the Slit Cascade / Talbot presets needed iteration hacks for.
    [Theory]
    [InlineData(ColorThemeKind.Cycling, ColorWrapMode.Repeat, 1f)]
    [InlineData(ColorThemeKind.Cycling, ColorWrapMode.PingPong, 2.5f)]
    [InlineData(ColorThemeKind.Cycling, ColorWrapMode.Clamp, 1f)]
    public void FitToRange_IsIndependentOfMaxIterations(ColorThemeKind kind, ColorWrapMode wrap, float density)
    {
        var map = Map(Theme(kind, ColorScaleMode.FitToRange, wrap, density, 0.1f));
        foreach (double t in new[] { 0.0, 0.13, 0.37, 0.5, 0.81, 0.999 })
            Assert.Equal(map.Map((float)(t * 16), 0f, 16), map.Map((float)(t * 400), 0f, 400));
        // Iterations mode is not (sanity: the oracle can tell the difference).
        var iter = Map(Theme(kind, ColorScaleMode.Iterations, wrap, density, 0.1f));
        Assert.Contains(new[] { 0.13, 0.37, 0.81 }, t => iter.Map((float)(t * 16), 0f, 16) != iter.Map((float)(t * 400), 0f, 400));
    }

    // Repeat at density 1 is one turn per range: the colour at the top of the range
    // equals the colour at 0, so a phase in turns has no seam.
    [Fact]
    public void FitToRange_Repeat_IsOneTurn()
    {
        var map = Map(Theme(ColorThemeKind.Cycling, ColorScaleMode.FitToRange, ColorWrapMode.Repeat));
        Assert.Equal(map.Map(0f, 0f, 400), map.Map(400f, 0f, 400));
        Assert.NotEqual(map.Map(0f, 0f, 400), map.Map(200f, 0f, 400));
    }

    // Iterations is the historical formula: (smooth · CycleSpeed · density + offset) mod 1,
    // and a theme without the field (legacy JSON) is Iterations.
    [Fact]
    public void Iterations_IsTheHistoricalMapping_AndTheDefault()
    {
        Assert.Equal(ColorScaleMode.Iterations, new ColorThemeData().ScaleMode);
        var legacy = Theme(ColorThemeKind.Cycling, ColorScaleMode.Iterations, ColorWrapMode.Repeat);
        var explicitIter = Map(legacy);
        // Same theme, gradient kind evaluated at the historical t = frac(s · 0.02):
        var grad = Map(Theme(ColorThemeKind.Gradient, ColorScaleMode.Iterations));
        foreach (float s in new[] { 3f, 17.5f, 49f, 123f, 377f })
        {
            float raw = s * 0.02f;
            float t = ((raw % 1f) + 1f) % 1f;   // the historical Repeat wrap, float for float (raw + 1 − 1 ≠ raw)
            const int pow2 = 1 << 20;   // t·2²⁰ / 2²⁰ is exact in float, so the gradient sees t itself
            Assert.Equal(grad.Map(t * pow2, 0f, pow2), explicitIter.Map(s, 0f, 400));
        }
    }

    [Fact]
    public void ScaleMode_RoundTripsThroughThemeJson_AndLegacyJsonIsIterations()
    {
        var opts = UserColorThemeLibrary.BuildJsonOptions();
        var data = Theme(ColorThemeKind.Phong3D, ColorScaleMode.FitToRange);
        var back = JsonSerializer.Deserialize<ColorThemeData>(JsonSerializer.Serialize(data, opts), opts)!;
        Assert.Equal(ColorScaleMode.FitToRange, back.ScaleMode);

        string json = JsonSerializer.Serialize(Theme(ColorThemeKind.Cycling, ColorScaleMode.Iterations), opts);
        var doc = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(json)!;
        foreach (var key in doc.Select(p => p.Key).Where(k => k.Equals("ScaleMode", StringComparison.OrdinalIgnoreCase)).ToList())
            doc.Remove(key);
        Assert.Equal(ColorScaleMode.Iterations, JsonSerializer.Deserialize<ColorThemeData>(doc.ToJsonString(), opts)!.ScaleMode);
    }

    // The three kinds that cycle carry the mode through Create → Export (the
    // data-driven export sites), and the lit kinds honour it.
    [Theory]
    [InlineData(ColorThemeKind.Cycling)]
    [InlineData(ColorThemeKind.Phong3D)]
    [InlineData(ColorThemeKind.Pbr3D)]
    public void CyclingKinds_ExportTheMode_AndHonourIt(ColorThemeKind kind)
    {
        var map = Map(Theme(kind, ColorScaleMode.FitToRange, ColorWrapMode.Repeat));
        Assert.Equal(ColorScaleMode.FitToRange, DataDrivenColorThemes.Export(map)!.ScaleMode);
        foreach (double t in new[] { 0.2, 0.55, 0.9 })
            Assert.Equal(map.Map((float)(t * 16), 0f, 16, 0.3f, 0.4f), map.Map((float)(t * 400), 0f, 400, 0.3f, 0.4f));
    }

    // End to end: a Talbot carpet rendered with a FitToRange theme gives the same
    // picture at 400 iterations as at 16 — the iteration hack is no longer needed.
    [Fact]
    public void TalbotCarpet_LooksTheSameAtAnyIterationCount()
    {
        uint[] Render(int maxIt)
        {
            var calc = new TalbotCarpetCalculator(64, 36)
            {
                CenterX = 0, CenterY = 0.5, Zoom = 1.9, MaxIterations = maxIt,
                ColorMap = Map(Theme(ColorThemeKind.Cycling, ColorScaleMode.FitToRange)),
                FractalParameters = new FractalParameters(),
            };
            calc.Calculate(CancellationToken.None);
            return (uint[])calc.ColorBuffer.Clone();
        }
        var a = Render(16); var b = Render(400);
        int same = a.Zip(b).Count(p => p.First == p.Second);
        Assert.True(same >= a.Length * 99 / 100, $"{same} of {a.Length} pixels match");   // float rounding at band edges
    }
}
