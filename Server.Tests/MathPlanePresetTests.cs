// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1555 (Math Plane S8) — every built-in demo parses, renders headless, renders
// the same through its --param list (the batch route), and round-trips through
// the Command builder; loading never changes a built-in.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlanePresetTests
{
    private const int W = 320, H = 180;

    public static IEnumerable<object[]> Demos() => MathPlanePresets.All.Select((_, i) => new object[] { i });

    private static MathPlaneCalculator Render(FractalParameters p, MathPlanePreset d)
    {
        var calc = new MathPlaneCalculator(W, H)
        {
            CenterX = d.ViewWorldX, CenterY = -d.ViewWorldY, Zoom = d.Zoom, MaxIterations = 256, FractalParameters = p,
        };
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    [Fact]
    public void Demos_HaveUniqueNamesAndAnIdea()
    {
        var all = MathPlanePresets.All;
        Assert.True(all.Count >= 12);
        Assert.Equal(all.Count, all.Select(d => d.Name).Distinct().Count());
        Assert.All(all, d => Assert.False(string.IsNullOrWhiteSpace(d.Idea)));
    }

    [Theory]
    [MemberData(nameof(Demos))]
    public void Demo_ParsesAndRendersHeadless(int i)
    {
        var d = MathPlanePresets.All[i];
        Assert.True(MathPlaneScene.TryParse(d.Scene, out _, out var e), $"{d.Name}: {e}");
        var p = new FractalParameters();
        d.Apply(p);
        var calc = Render(p, d);
        Assert.Null(calc.LastSceneError);
        // Something besides the background is drawn.
        Assert.Contains(calc.ColorBuffer, c => c != MathPlaneCalculator.BackgroundColor);
    }

    [Theory]
    [MemberData(nameof(Demos))]
    public void Demo_ParamList_RendersTheSame(int i)
    {
        var d = MathPlanePresets.All[i];
        var direct = new FractalParameters();
        d.Apply(direct);
        var kv = d.ParamArgs().Select(a => { int eq = a.IndexOf('='); return new KeyValuePair<string, string>(a[..eq], a[(eq + 1)..]); });
        var viaParam = new FractalParameters { MathPlaneScene = "" };
        RegionFractalParams.FromKeyValues(kv, FractalType.MathPlane, out var err)!.ApplyTo(viaParam);
        Assert.Null(err);
        Assert.Equal(Render(direct, d).ColorBuffer, Render(viaParam, d).ColorBuffer);
    }

    [Theory]
    [MemberData(nameof(Demos))]
    public void Demo_RoundTripsThroughTheCommandBuilder(int i)
    {
        var d = MathPlanePresets.All[i];
        var p = new FractalParameters();
        d.Apply(p);
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane, CenterX = d.ViewWorldX, CenterY = -d.ViewWorldY, Zoom = d.Zoom,
            Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var perr), perr);
        var fresh = new FractalParameters { MathPlaneScene = "" };
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane, out var ferr)!.ApplyTo(fresh);
        Assert.Null(ferr);
        Assert.Equal(Render(p, d).ColorBuffer, Render(fresh, d).ColorBuffer);
    }

    [Fact]
    public void Loading_NeverChangesTheBuiltIn()
    {
        var d = MathPlanePresets.All[0];
        string before = d.Scene;
        var p = new FractalParameters();
        d.Apply(p);
        p.MathPlaneScene = "point A 1 1";
        p.MathPlaneM11 = 9;
        Assert.Equal(before, MathPlanePresets.All[0].Scene);
        Assert.Equal(1.0, MathPlanePresets.All[0].M11);
        var q = new FractalParameters();
        MathPlanePresets.All[0].Apply(q);
        Assert.Equal(before, q.MathPlaneScene);
    }

    [Fact]
    public void Apply_ResetsEveryMathPlaneParam()
    {
        // A demo with no map / backdrop must clear what an earlier demo set.
        var p = new FractalParameters();
        MathPlanePresets.All.First(d => d.Backdrop != MathPlaneBackdrop.None).Apply(p);
        var plain = MathPlanePresets.All.First(d => d.Backdrop == MathPlaneBackdrop.None && d.MapMode == MathPlaneMapMode.None);
        plain.Apply(p);
        Assert.Equal(MathPlaneBackdrop.None, p.MathPlaneBackdrop);
        Assert.Equal(MathPlaneMapMode.None, p.MathPlaneMapMode);
        var fresh = new FractalParameters();
        plain.Apply(fresh);
        Assert.Equal(RegionFractalParams.Snapshot(FractalType.MathPlane, fresh)!.ToKeyValues(),
                     RegionFractalParams.Snapshot(FractalType.MathPlane, p)!.ToKeyValues());
    }

    [Fact]
    public void TheGuideNamesEveryDemo()
    {
        string guide = File.ReadAllText(Path.Combine(RepoRoot(), "Docs", "User", "MathPlane-Guide.md"));
        foreach (var d in MathPlanePresets.All)
        {
            // Cobweb demos share one row ("Cobweb: fixed point / period 2 / ...").
            string key = d.Name.StartsWith("Cobweb") ? "Cobweb:" : d.Name;
            Assert.Contains(key, guide);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
