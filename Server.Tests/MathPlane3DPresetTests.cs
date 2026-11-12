// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/MathPlane3DPresetTests.cs
//
// #1719 (Math Plane 3D T7) — every built-in 3D demo parses, renders headless, renders the
// same through its --param list (the batch route) and through the Command builder, and is
// named in the guide; loading never changes a built-in. Each demo's idea is also checked
// against an independent fact: the determinant it claims, a rotation's orthonormality, the
// halfway lengths of polar vs linear frames, the fold moving p across the mirror, the saddle's
// equation and the torus's tube radius.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Help;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlane3DPresetTests
{
    private const int W = 320, H = 200;

    public static IEnumerable<object[]> Demos() => MathPlane3DPresets.All.Select((_, i) => new object[] { i });

    private static MathPlane3DCalculator Render(FractalParameters p, double zoom)
    {
        var calc = new MathPlane3DCalculator(W, H) { Zoom = zoom, FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    private static MathPlane3DPreset Demo(string name)
        => MathPlane3DPresets.Find(name) ?? throw new Xunit.Sdk.XunitException("no demo " + name);

    private static Mat3 Frame(MathPlane3DPreset d)
    {
        var p = new FractalParameters();
        d.Apply(p);
        return MathPlane3DMap.Current(p);
    }

    private static double Len((double X, double Y, double Z) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    // ── Every demo ──

    [Fact]
    public void Demos_HaveUniqueNamesAndAnIdea()
    {
        var all = MathPlane3DPresets.All;
        Assert.True(all.Count >= 9);
        Assert.Equal(all.Count, all.Select(d => d.Name).Distinct().Count());
        Assert.All(all, d => Assert.False(string.IsNullOrWhiteSpace(d.Idea)));
        Assert.All(all, d => Assert.Same(d, MathPlane3DPresets.Find(d.Name)));
    }

    [Theory]
    [MemberData(nameof(Demos))]
    public void Demo_ParsesAndRendersHeadless(int i)
    {
        var d = MathPlane3DPresets.All[i];
        Assert.True(MathPlane3DScene.TryParse(d.Scene, out _, out var e), $"{d.Name}: {e}");
        var p = new FractalParameters();
        d.Apply(p);
        var calc = Render(p, d.Zoom);
        Assert.Null(calc.LastSceneError);
        // Something besides the background is drawn.
        Assert.Contains(calc.ColorBuffer, c => c != MathPlane3DCalculator.BackgroundColor);
    }

    [Theory]
    [MemberData(nameof(Demos))]
    public void Demo_ParamList_RendersTheSame(int i)
    {
        var d = MathPlane3DPresets.All[i];
        var direct = new FractalParameters();
        d.Apply(direct);
        var kv = d.ParamArgs().Select(a => { int eq = a.IndexOf('='); return new KeyValuePair<string, string>(a[..eq], a[(eq + 1)..]); });
        // Start from a different view, so every key must land.
        var viaParam = new FractalParameters { MathPlane3DScene = "", MathPlane3DCameraDistance = 3, MathPlane3DM12 = 7 };
        RegionFractalParams.FromKeyValues(kv, FractalType.MathPlane3D, out var err)!.ApplyTo(viaParam);
        Assert.Null(err);
        Assert.Equal(RegionFractalParams.Snapshot(FractalType.MathPlane3D, direct)!.ToKeyValues(),
                     RegionFractalParams.Snapshot(FractalType.MathPlane3D, viaParam)!.ToKeyValues());
        Assert.Equal(Render(direct, d.Zoom).ColorBuffer, Render(viaParam, d.Zoom).ColorBuffer);
    }

    [Theory]
    [MemberData(nameof(Demos))]
    public void Demo_RoundTripsThroughTheCommandBuilder(int i)
    {
        var d = MathPlane3DPresets.All[i];
        var p = new FractalParameters();
        d.Apply(p);
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane3D, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane3D, CenterX = 0, CenterY = 0, Zoom = d.Zoom,
            Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.Empty(report.Gaps);
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var perr), perr);
        var fresh = new FractalParameters { MathPlane3DScene = "" };
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane3D, out var ferr)!.ApplyTo(fresh);
        Assert.Null(ferr);
        Assert.Equal(Render(p, d.Zoom).ColorBuffer, Render(fresh, o.Zoom ?? 1).ColorBuffer);
    }

    [Fact]
    public void Loading_NeverChangesTheBuiltIn()
    {
        var d = MathPlane3DPresets.All[0];
        string before = d.Scene;
        var p = new FractalParameters();
        d.Apply(p);
        p.MathPlane3DScene = "v1; point A 1 1 1";
        p.MathPlane3DM11 = 9;
        Assert.Equal(before, MathPlane3DPresets.All[0].Scene);
        Assert.Equal(d.M.M11, MathPlane3DPresets.All[0].M.M11);
        var q = new FractalParameters();
        MathPlane3DPresets.All[0].Apply(q);
        Assert.Equal(before, q.MathPlane3DScene);
    }

    [Fact]
    public void Apply_ResetsEveryMathPlane3DParam()
    {
        // A demo without a map must clear what an earlier demo set (map, t, frames, camera, target).
        var p = new FractalParameters { MathPlane3DTargetX = 4, MathPlane3DTargetZ = -2 };
        Demo("Rotation about z: polar, halfway").Apply(p);
        var plain = Demo("Parametric torus");
        plain.Apply(p);
        var fresh = new FractalParameters();
        plain.Apply(fresh);
        Assert.False(p.MathPlane3DMapOn);
        Assert.Equal(RegionFractalParams.Snapshot(FractalType.MathPlane3D, fresh)!.ToKeyValues(),
                     RegionFractalParams.Snapshot(FractalType.MathPlane3D, p)!.ToKeyValues());
    }

    // ── Each demo shows what it says ──

    [Theory]
    [InlineData("Rotation about x", 0)]
    [InlineData("Rotation about y", 1)]
    [InlineData("Rotation about z: polar, halfway", 2)]
    public void TheRotations_AreRigid_AndFixTheirAxis(string name, int axis)
    {
        var m = Frame(Demo(name));
        var mtm = m.Transposed() * m;
        foreach (var (got, want) in new[] { (mtm.M11, 1.0), (mtm.M22, 1.0), (mtm.M33, 1.0), (mtm.M12, 0.0), (mtm.M13, 0.0), (mtm.M23, 0.0) })
            Assert.Equal(want, got, 9);
        Assert.Equal(1.0, m.Det, 9);
        var e = axis == 0 ? (1.0, 0.0, 0.0) : axis == 1 ? (0.0, 1.0, 0.0) : (0.0, 0.0, 1.0);
        var me = m.Apply(e);
        Assert.Equal(e, (Math.Round(me.X, 9), Math.Round(me.Y, 9), Math.Round(me.Z, 9)));
    }

    [Fact]
    public void Halfway_PolarTurns45Degrees_LinearShrinksTo1OverRoot2()
    {
        var polar = Frame(Demo("Rotation about z: polar, halfway"));
        var linear = Frame(Demo("Rotation about z: linear, halfway"));
        var pi = polar.Column(0); var li = linear.Column(0);
        Assert.Equal(1.0, Len(pi), 9);
        Assert.Equal(Math.PI / 4, Math.Atan2(pi.Y, pi.X), 9);
        Assert.Equal(Math.Sqrt(0.5), Len(li), 9);
        Assert.Equal(Math.PI / 4, Math.Atan2(li.Y, li.X), 9);   // same direction, shorter
        Assert.Equal(0.5, linear.Det, 9);
    }

    [Theory]
    [InlineData("KIFS fold: a reflection", -1.0)]
    [InlineData("Uniform scale: the Mandelbox step", 8.0)]
    [InlineData("det is the volume of the cube's image", 3.0)]
    [InlineData("Rotation about x", 1.0)]
    public void TheDeterminant_IsTheOneTheIdeaNames(string name, double det)
    {
        var d = Demo(name);
        Assert.Equal(det, Frame(d).Det, 9);
    }

    [Fact]
    public void TheFold_IsFFsSierpinskiFold_OnTheHalfBehindTheMirror()
    {
        // KifsCalculator.SierpDE: if (x + y < 0) { t = -y; y = -x; x = t; }
        static (double, double, double) SierpFold((double X, double Y, double Z) v)
            => v.X + v.Y < 0 ? (-v.Y, -v.X, v.Z) : v;
        var m = Frame(Demo("KIFS fold: a reflection"));
        var rnd = new Random(1719);
        for (int k = 0; k < 200; k++)
        {
            var v = (rnd.NextDouble() * 4 - 2, rnd.NextDouble() * 4 - 2, rnd.NextDouble() * 4 - 2);
            if (v.Item1 + v.Item2 >= 0) continue;
            Assert.Equal(SierpFold(v), m.Apply(v));
        }
        // m is an involution (a reflection), and the demo's p crosses the mirror.
        Assert.True((m * m).IsIdentity);
        Assert.True(MathPlane3DScene.TryParse(Demo("KIFS fold: a reflection").Scene, out var s, out _));
        var p = MathPlane3DScene.Tip(s.Objects.Single(o => o.Name == "p"));
        Assert.True(p.X + p.Y < 0);
        var q = m.Apply(p);
        Assert.True(q.X + q.Y > 0);
    }

    [Fact]
    public void TheSaddle_IsZEqualsXSquaredMinusYSquared()
    {
        Assert.True(MathPlane3DScene.TryParse(Demo("Saddle z = x^2 - y^2").Scene, out var s, out _));
        var mesh = MathPlane3DCalculator.BuildSurface(s.Objects.Single(o => o.Kind == MathPlaneObjectKind.Surface))!;
        Assert.All(mesh.Vertices, v => Assert.Equal(v.X * v.X - v.Y * v.Y, v.Z, 12));
        Assert.All(mesh.CellOk, Assert.True);
    }

    [Fact]
    public void TheTorus_HasTubeRadius06_RoundARingOfRadius2()
    {
        Assert.True(MathPlane3DScene.TryParse(Demo("Parametric torus").Scene, out var s, out _));
        var mesh = MathPlane3DCalculator.BuildSurface(s.Objects.Single())!;
        Assert.All(mesh.Vertices, v =>
        {
            double ring = Math.Sqrt(v.X * v.X + v.Y * v.Y) - 2;
            Assert.Equal(0.6, Math.Sqrt(ring * ring + v.Z * v.Z), 9);
        });
        Assert.All(mesh.CellOk, Assert.True);
        // u and v run a full turn, so the first and last grid columns meet.
        Assert.Equal(mesh.At(0, 5).X, mesh.At(mesh.N, 5).X, 9);
    }

    // ── Help, guide, Control Center ──

    [Fact]
    public void TheGuideNamesEveryDemo_AndTheIndexLinksIt()
    {
        string root = RepoRoot();
        string guide = File.ReadAllText(Path.Combine(root, "Docs", "User", "MathPlane3D-Guide.md"));
        foreach (var d in MathPlane3DPresets.All) Assert.Contains(d.Name, guide);
        Assert.Contains("(MathPlane3D-Guide.md)", File.ReadAllText(Path.Combine(root, "Docs", "User", "_Index.md")));
    }

    [Fact]
    public void TheHelpText_CoversTheModeAndItsDemos()
    {
        string help = HelpTextBundle.MathPlane3DText;
        foreach (var word in new[] { "Demos", "KIFS", "Mandelbox", "torus", "saddle", "Polar", "det", "Export mesh", "--math-plane-3d-mesh", "MathPlane3DScene" })
            Assert.Contains(word, help);
    }

    [Fact]
    public void LoadDemo_ReplacesSceneMapAndCamera_AndLeavesTheBuiltIn()
    {
        var view = new FractalViewState { FractalType = FractalType.MathPlane3D, Zoom = 3 };
        view.FractalParameters.MathPlane3DScene = "v1; point A 1 2 3";
        view.FractalParameters.MathPlane3DTargetX = 5;
        int renders = 0;
        var vm = new MathPlane3DSectionViewModel(view, () => renders++, () => { });
        var d = Demo("KIFS fold: a reflection");
        vm.SelectedDemo = d;
        Assert.Equal(d.Idea, vm.DemoIdea);
        vm.LoadDemoCommand.Execute().Subscribe();
        var want = new FractalParameters();
        d.Apply(want);
        Assert.Equal(RegionFractalParams.Snapshot(FractalType.MathPlane3D, want)!.ToKeyValues(),
                     RegionFractalParams.Snapshot(FractalType.MathPlane3D, view.FractalParameters)!.ToKeyValues());
        Assert.Equal(d.Zoom, view.Zoom);
        Assert.Equal(1, renders);
        Assert.Contains(vm.Rows, r => r.Contains("p"));
        Assert.True(vm.CanExportMesh || vm.MeshExportRequested == null);   // the mirror is a surface
        Assert.True(vm.HasVisibleSurface);
        // Editing afterwards never touches the demo.
        view.FractalParameters.MathPlane3DScene = "v1";
        Assert.Contains("vector p", MathPlane3DPresets.Find(d.Name)!.Scene);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
