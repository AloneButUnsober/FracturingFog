// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1154 / #1155 — Dual-Orbit Volume → Julibrot, Dual-Orbit Escape → Julibrot Pair.
// Same numeric values; every reader of a type NAME accepts the old names (CLI,
// region / scene JSON, slideshow filters), writers emit the new ones, and the
// user-facing labels changed. The DualOrbit* parameter keys are untouched.
public sealed class FractalTypeRenameTests
{
    [Theory]
    [InlineData("JulibrotPair", FractalType.JulibrotPair)]
    [InlineData("julibrotpair", FractalType.JulibrotPair)]
    [InlineData("DualOrbitEscape", FractalType.JulibrotPair)]
    [InlineData("dualorbitescape", FractalType.JulibrotPair)]
    [InlineData("Julibrot", FractalType.Julibrot)]
    [InlineData("DualOrbitVolume", FractalType.Julibrot)]
    [InlineData("Mandelbrot", FractalType.Mandelbrot)]
    public void Names_Parse_IncludingTheLegacyOnes(string name, FractalType want)
    {
        Assert.True(FractalTypeNames.TryParse(name, out var t));
        Assert.Equal(want, t);
    }

    [Fact]
    public void NumericValues_AreUnchanged_AndIntegersStillParse()
    {
        // The rename kept the enum positions: integer-persisted data never moved.
        Assert.True(FractalTypeNames.TryParse(((int)FractalType.JulibrotPair).ToString(), out var t));
        Assert.Equal(FractalType.JulibrotPair, t);
        Assert.False(FractalTypeNames.TryParse("NoSuchFractal", out _));
        Assert.False(FractalTypeNames.TryParse("999999", out _));
        Assert.False(FractalTypeNames.TryParse("", out _));
    }

    [Fact]
    public void Canonical_MapsOldFilterNames_AndLeavesOthers()
    {
        Assert.Equal("JulibrotPair", FractalTypeNames.Canonical("DualOrbitEscape"));
        Assert.Equal("Julibrot", FractalTypeNames.Canonical("DualOrbitVolume"));
        Assert.Equal("Mandelbrot", FractalTypeNames.Canonical("Mandelbrot"));
        Assert.Equal("Whatever", FractalTypeNames.Canonical("Whatever"));
    }

    [Theory]
    [InlineData("DualOrbitEscape", FractalType.JulibrotPair)]
    [InlineData("DualOrbitVolume", FractalType.Julibrot)]
    [InlineData("JulibrotPair", FractalType.JulibrotPair)]
    public void BatchFractalFlag_AcceptsOldAndNewNames(string name, FractalType want)
    {
        var args = new[] { "--fractal", name, "--x", "-0.6", "--y", "0", "--zoom", "1", "--out", "x.png" };
        Assert.True(BatchOptions.TryParse(args, 0, out var o, out var err), err);
        Assert.Equal(want, o.FractalType);
    }

    [Fact]
    public void CommandBuilder_EmitsTheNewName_AndItParses()
    {
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.JulibrotPair, Parameters = new FractalParameters(),
        });
        Assert.Contains("JulibrotPair", report.Args);
        Assert.DoesNotContain(report.Args, a => a.Contains("DualOrbitEscape", StringComparison.Ordinal));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        Assert.Equal(FractalType.JulibrotPair, o.FractalType);
    }

    [Fact]
    public void RegionJson_ReadsTheOldName_WritesTheNew()
    {
        var r = JsonSerializer.Deserialize<FractalRegion>("""{ "Name": "old", "FractalType": "DualOrbitEscape" }""")!;
        Assert.Equal(FractalType.JulibrotPair, r.FractalType);
        var v = JsonSerializer.Deserialize<FractalRegion>("""{ "Name": "oldv", "FractalType": "DualOrbitVolume" }""")!;
        Assert.Equal(FractalType.Julibrot, v.FractalType);
        string json = JsonSerializer.Serialize(r);
        Assert.Contains("\"JulibrotPair\"", json);
        Assert.DoesNotContain("DualOrbitEscape", json);
        // numbers still read
        int n = (int)FractalType.Julibrot;
        Assert.Equal(FractalType.Julibrot, JsonSerializer.Deserialize<FractalRegion>($$"""{ "FractalType": {{n}} }""")!.FractalType);
    }

    [Fact]
    public void SceneJson_ReadsTheOldName()
    {
        var s = JsonSerializer.Deserialize<SceneData>("""{ "Shots": [ { "FractalType": "DualOrbitVolume" } ] }""", SceneLibrary.BuildJsonOptions())!;
        Assert.Equal(FractalType.Julibrot, s.Shots.Single().FractalType);
        Assert.Contains("\"Julibrot\"", JsonSerializer.Serialize(s, SceneLibrary.BuildJsonOptions()));
    }

    [Fact]
    public void DisplayNames_AreTheNewOnes()
    {
        Assert.Equal("Julibrot Pair", Fractals.FractalNameByNameType[FractalType.JulibrotPair]);
        Assert.Equal("Julibrot", Fractals.FractalNameByNameType[FractalType.Julibrot]);
    }

    // The DualOrbit* parameter keys are deliberately unchanged: saved --param /
    // region keys keep working under the new type name.
    [Fact]
    public void ParameterKeys_AreUnchanged()
    {
        var p = new FractalParameters { DualOrbitField = DualOrbitField.SecantLyapunov };
        var kv = RegionFractalParams.Snapshot(FractalType.JulibrotPair, p)!.ToKeyValues();
        Assert.Contains(kv, x => x.Key == "DualOrbitField");
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(kv, FractalType.JulibrotPair, out _)!.ApplyTo(fresh);
        Assert.Equal(DualOrbitField.SecantLyapunov, fresh.DualOrbitField);
    }
}
