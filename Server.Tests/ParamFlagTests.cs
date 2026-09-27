// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #997 (CB5 of #64) — `--param Key=Value` carries any per-family setting on the
// batch CLI by reusing the region snapshot. The oracle is the snapshot itself:
// a live state pushed through the CLI must snapshot identically on the far side.
public sealed class ParamFlagTests
{
    // A value guaranteed to differ from v (mirrors the family-property probe).
    private static object? Bump(object? v, Type t)
    {
        if (t == typeof(double)) return (double)v! + 0.123;
        if (t == typeof(float)) return (float)v! + 0.123f;
        if (t == typeof(int)) return (int)v! + 1;
        if (t == typeof(long)) return (long)v! + 1;
        if (t == typeof(bool)) return !(bool)v!;
        if (t == typeof(string)) return (v as string ?? "") + "x";
        if (t == typeof(Complex)) return (Complex)v! + new Complex(0.11, -0.07);
        if (t.IsEnum)
        {
            var values = Enum.GetValues(t);
            return values.GetValue((Array.IndexOf(values, v) + 1) % values.Length);
        }
        var list = (System.Collections.IList)Activator.CreateInstance(t)!;
        if (v is System.Collections.IEnumerable src) foreach (var e in src) list.Add(e);
        list.Add(Activator.CreateInstance(t.GetGenericArguments()[0]));
        return list;
    }

    /// <summary>A live state with every family setting moved off its default.</summary>
    private static FractalParameters Engaged(FractalType type)
    {
        var p = new FractalParameters();
        foreach (var pi in RegionFractalParams.FamilyProperties(type))
        {
            try { pi.SetValue(p, Bump(pi.GetValue(p), pi.PropertyType)); } catch { }
        }
        return p;
    }

    private static string Snap(FractalType type, FractalParameters p)
        => JsonSerializer.Serialize(RegionFractalParams.Snapshot(type, p));

    public static IEnumerable<object[]> Types() => Enum.GetValues<FractalType>().Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Types))]
    public void EveryFamily_RoundTripsThroughTheCliGrammar(FractalType type)
    {
        var live = Engaged(type);
        var snap = RegionFractalParams.Snapshot(type, live);
        if (snap == null) return;   // family carries nothing

        // Live → --param tokens → the real parser → Engine overlay on a fresh state.
        var args = new List<string> { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
        foreach (var (k, v) in snap.ToKeyValues()) { args.Add(BatchFlags.Param); args.Add(k + "=" + v); }
        Assert.True(BatchOptions.TryParse(args.ToArray(), 0, out var opts, out var err), err);

        var overlay = RegionFractalParams.FromKeyValues(opts.Params, type, out var error);
        Assert.True(overlay != null, error);
        var fresh = new FractalParameters();
        overlay!.ApplyTo(fresh);
        Assert.Equal(Snap(type, live), Snap(type, fresh));
    }

    [Fact]
    public void JuliaConstant_AndCamera_ApplyWhereARegionWould()
    {
        var rp = RegionFractalParams.FromKeyValues(new[]
        {
            KV("juliacre", "-0.8"), KV("JuliaCIm", "0.156"),
            KV("Cam3DDistance", "3.5"), KV("Cam3DTheta", "1.2"), KV("Cam3DPhi", "0.4"),
        }, FractalType.Mandelbulb, out var err);
        Assert.True(rp != null, err);
        var p = new FractalParameters();
        rp!.ApplyTo(p);
        Assert.Equal(new Complex(-0.8, 0.156), p.JuliaC);
        Assert.Equal((int)FractalType.Mandelbulb, rp.Cam3DFamily);   // family defaulted from --fractal
        Assert.Equal(3.5, p.BulbCameraDistance);
        Assert.Equal(1.2, p.BulbCameraTheta);
    }

    [Fact]
    public void Cam3DFamily_AcceptsATypeName_AndAnExplicitFamilyWins()
    {
        var rp = RegionFractalParams.FromKeyValues(new[]
        {
            KV("Cam3DFamily", "Mandelbox"), KV("Cam3DDistance", "4"), KV("Cam3DTheta", "0.5"), KV("Cam3DPhi", "0.2"),
        }, FractalType.Mandelbulb, out _);
        Assert.Equal((int)FractalType.Mandelbox, rp!.Cam3DFamily);
    }

    [Fact]
    public void LaterPairsWin()
    {
        var rp = RegionFractalParams.FromKeyValues(new[] { KV("MultibrotExponent", "3"), KV("MultibrotExponent", "7") }, null, out _);
        Assert.Equal(7, rp!.MultibrotExponent);
    }

    [Theory]
    [InlineData("NoSuchKey", "1", "Unknown --param key 'NoSuchKey'")]
    [InlineData("JuliaCRe", "abc", "is not a valid number")]
    [InlineData("MultibrotExponent", "2.5", "--param")]
    public void BadPairs_AreRejectedWithAMessage(string key, string value, string message)
    {
        Assert.Null(RegionFractalParams.FromKeyValues(new[] { KV(key, value) }, null, out var err));
        Assert.Contains(message, err);
    }

    [Fact]
    public void UnknownKey_SuggestsNearNames()
    {
        RegionFractalParams.FromKeyValues(new[] { KV("JuliaC", "1") }, null, out var err);
        Assert.Contains("JuliaCRe", err);
    }

    [Fact]
    public void StringValues_KeepEqualsSignsAndSpaces()
    {
        Assert.True(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o",
            "--param", "DualOrbitThemeZ=Fire 3D (PBR) = hot" }, 0, out var o, out var err), err);
        var rp = RegionFractalParams.FromKeyValues(o.Params, null, out _);
        Assert.Equal("Fire 3D (PBR) = hot", rp!.DualOrbitThemeZ);
    }

    [Theory]
    [InlineData("=1")]
    [InlineData("NoEquals")]
    [InlineData(" =x")]
    public void Parser_RejectsMalformedPairs(string pair)
    {
        Assert.False(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o", "--param", pair },
            0, out _, out var err));
        Assert.Contains("KEY=VALUE", err);
    }

    // ── Builder emission ─────────────────────────────────────────────────────

    [Fact]
    public void Builder_EmitsFamilyParams_SkippingKeysADedicatedFlagCarries()
    {
        var p = new FractalParameters { MultibrotExponent = 5, EscapeIterationScale = 2.0 };
        var kv = RegionFractalParams.Snapshot(FractalType.Multibrot, p)!.ToKeyValues();
        Assert.Contains(kv, x => x.Key == "MultibrotExponent");

        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.Multibrot, Parameters = p, FamilyParams = kv,
        });
        Assert.Contains("--multibrot-exp 5", report.Command);
        Assert.DoesNotContain("MultibrotExponent=", report.Command);
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var rp = RegionFractalParams.FromKeyValues(o.Params, FractalType.Multibrot, out var perr);
        Assert.True(rp != null, perr);
    }

    [Fact]
    public void CoveredKeys_AreRealSnapshotKeys()
    {
        foreach (var k in BatchCommandBuilder.ParamKeysCoveredByFlags)
            Assert.Contains(k, RegionFractalParams.KeyNames);
    }

    [Fact]
    public void JuliaLiveView_ReachesTheCommandAndBack()
    {
        var p = new FractalParameters { JuliaC = new Complex(-0.7269, 0.1889) };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.Julia, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.Julia, p)!.ToKeyValues(),
        });
        Assert.Contains("--param JuliaCRe=-0.7269", report.Command);

        var c = new CommandComposer();
        c.Load(report.Args);   // the Command panel holds the pairs one per line
        Assert.Contains("JuliaCIm=0.1889", c.ValueOf(BatchFlags.Param));
        Assert.True(BatchOptions.TryParse(c.Args().ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.Julia, out _)!.ApplyTo(fresh);
        Assert.Equal(p.JuliaC, fresh.JuliaC);
    }

    private static KeyValuePair<string, string> KV(string k, string v) => new(k, v);
}
