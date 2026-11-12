// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/MathPlane3DFractalObjectTests.cs
//
// #1731 (Math Plane 3D fractal bridge B1) — a 3D fractal as a Math Plane 3D surface. The mesh
// is checked against the S9 mesh export written to an STL file (the same mesh, through the
// stated rotation), and against the distance estimate itself (every vertex on the iso level);
// the rest is the scene text, errors, the mesh cache, cancellation, the batch route and the
// Control Center.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Export;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlane3DFractalObjectTests : IDisposable
{
    private const int W = 320, H = 200;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ff-mp3d-fractal-" + Guid.NewGuid().ToString("N"));

    public MathPlane3DFractalObjectTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static IEnumerable<object[]> Types() => MeshExportCapabilities.Types.Select(t => new object[] { t });

    private static MathPlaneObject Object(string stmt)
    {
        Assert.True(MathPlane3DScene.TryParse("v1; " + stmt, out var s, out var err), err?.ToString());
        return s.Objects.Single();
    }

    private static FractalParameters Params(string scene, double dist = 6) => new()
    {
        MathPlane3DScene = "v1; axes off; grid off; " + scene,
        MathPlane3DCameraDistance = dist, MathPlane3DCameraTheta = 0.7, MathPlane3DCameraPhi = 1.1,
    };

    private static MathPlane3DCalculator Render(FractalParameters p, MathPlane3DCalculator? calc = null, CancellationToken ct = default)
    {
        calc ??= new MathPlane3DCalculator(W, H) { Zoom = 1 };
        calc.FractalParameters = p;
        calc.Calculate(ct);
        return calc;
    }

    // ── The mesh ──

    [Theory]
    [MemberData(nameof(Types))]
    public void EveryType_ParsesMeshesAndRenders(FractalType type)
    {
        var o = Object($"fractal F {type} mesh 16");
        Assert.Equal(type, MathPlane3DScene.FractalTypeOf(o));
        Assert.Null(MathPlane3DFractalMesher.Validate(o));
        var m = MathPlane3DFractalMesher.Build(o, out var err);
        Assert.Null(err);
        Assert.NotNull(m);
        Assert.NotEmpty(m!.Triangles);
        var calc = Render(Params($"fractal F {type} mesh 16"));
        Assert.Null(calc.LastSceneError);
        Assert.Contains(calc.ColorBuffer, c => c != MathPlane3DCalculator.BackgroundColor);
    }

    [Theory]
    [InlineData("Mandelbulb", "")]
    [InlineData("Mandelbulb", "BulbPower=3")]
    [InlineData("Mandelbox", "")]
    [InlineData("Kifs", "")]
    public void TheMesh_IsTheS9ExportsMesh_ThroughTheRotation(string type, string keys)
    {
        const int n = 24;
        var o = Object($"fractal F {type} {keys} mesh {n}");
        var mesh = MathPlane3DFractalMesher.Build(o, out var err)!;
        Assert.Null(err);

        // The S9 export of the same fractal (its own parameters, its own file).
        Assert.True(FractalTypeNames.TryParse(type, true, out var t));
        var p = new FractalParameters();
        if (keys.Length > 0)
            RegionFractalParams.FromKeyValues(new[] { new KeyValuePair<string, string>(keys[..keys.IndexOf('=')], keys[(keys.IndexOf('=') + 1)..]) }, t, out _)!.ApplyTo(p);
        string stl = Path.Combine(_dir, type + ".stl");
        int count = UserBulbMeshExporter.ExportMarchingCubes(stl, RaymarchMeshSampler.For(t, p)!, 0, 0, 0,
                                                             RaymarchMeshSampler.SuggestedRange(t, p), n);
        var (pos, tris) = StlMeshReader.ReadBinary(stl);
        Assert.Equal(count, mesh.Triangles.Length);
        Assert.Equal(tris.Count, mesh.Triangles.Length);

        // Undo the stated rotation (x, y, z) -> (x, -z, y): object = (x, z, -y).
        static (double, double, double) Back((double X, double Y, double Z) v) => (v.X, v.Z, -v.Y);
        for (int k = 0; k < tris.Count; k++)
        {
            var (a, b, c) = mesh.Triangles[k];
            foreach (var (mine, theirs) in new[] { (a, tris[k].A), (b, tris[k].B), (c, tris[k].C) })
            {
                var w = Back(mesh.Vertices[mine]); var g = pos[theirs];
                // STL stores float: equal to float precision.
                Assert.True(Math.Abs(w.Item1 - g.X) < 1e-5 && Math.Abs(w.Item2 - g.Y) < 1e-5 && Math.Abs(w.Item3 - g.Z) < 1e-5,
                            $"vertex {w} vs {g}");
            }
        }
    }

    [Fact]
    public void EveryVertex_IsOnTheDistanceEstimatesIsoLevel()
    {
        // The S9 core places the surface where DE = half a cell; with linear interpolation along
        // cell edges, every vertex's DE is within a cell of that.
        const int n = 32;
        var o = Object($"fractal F Mandelbulb mesh {n}");
        var mesh = MathPlane3DFractalMesher.Build(o, out _)!;
        var p = new FractalParameters();
        var de = RaymarchMeshSampler.For(FractalType.Mandelbulb, p)!;
        double step = 2 * RaymarchMeshSampler.SuggestedRange(FractalType.Mandelbulb, p) / n;
        Assert.All(mesh.Vertices, v =>
        {
            double d = de.Evaluate(v.X, v.Z, -v.Y);   // back to the fractal's own axes
            Assert.InRange(d, 0.5 * step - step, 0.5 * step + step);
        });
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void NoVertex_IsNonFinite(FractalType type)
    {
        // A NaN distance estimate at a grid corner (the Mandelbulb's at r = 0) once gave NaN vertices.
        foreach (int n in new[] { 16, 32 })
        {
            var m = MathPlane3DFractalMesher.Build(Object($"fractal F {type} mesh {n}"), out _)!;
            Assert.All(m.Vertices, v => Assert.True(double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z), $"{type} n {n}: {v}"));
        }
    }

    [Fact]
    public void TheSettings_ChangeTheMesh_AndAnUnknownKeyIsAnError()
    {
        var plain = MathPlane3DFractalMesher.Build(Object("fractal F Mandelbulb mesh 16"), out _)!;
        var p3 = MathPlane3DFractalMesher.Build(Object("fractal F Mandelbulb BulbPower=3 mesh 16"), out _)!;
        Assert.NotEqual(plain.Triangles.Length, p3.Triangles.Length);

        var bad = Object("fractal F Mandelbulb BulbPowr=3");
        Assert.Contains("Unknown --param key 'BulbPowr'", MathPlane3DFractalMesher.Validate(bad));
        Assert.Null(MathPlane3DFractalMesher.Build(bad, out var err));
        Assert.NotNull(err);

        // The render says so (message only, no statement position), and meshes nothing for it.
        var calc = Render(Params("fractal F Mandelbulb BulbPowr=3"));
        Assert.NotNull(calc.LastSceneError);
        Assert.StartsWith("fractal F: Unknown --param key", calc.LastSceneError!.ToString());
        Assert.Equal("Scene error: " + calc.LastSceneError, calc.LastPrecisionLabel);
        Assert.Empty(MathPlane3DMeshExporter.Build(Params("fractal F Mandelbulb BulbPowr=3")).Triangles);
    }

    // ── Scene text ──

    [Fact]
    public void TheStatement_ParsesAndRoundTrips()
    {
        const string text = "v1; fractal Bulb mandelbulb BulbPower=6 BulbIterations=10 #E69F0080 nolabel mesh 40";
        Assert.True(MathPlane3DScene.TryParse(text, out var s, out var err), err?.ToString());
        var o = s.Objects.Single();
        Assert.Equal(MathPlaneObjectKind.Surface, o.Kind);
        Assert.Equal(MathPlaneCurveKind.Fractal, o.CurveKind);
        Assert.Equal(new[] { "Mandelbulb", "BulbPower=6", "BulbIterations=10" }, o.Expressions);
        Assert.Equal(new[] { new KeyValuePair<string, string>("BulbPower", "6"), new KeyValuePair<string, string>("BulbIterations", "10") },
                     MathPlane3DScene.FractalKeys(o));
        Assert.Equal(0x80E69F00u, o.Color);
        Assert.False(o.ShowLabel);
        Assert.Equal(40, o.Mesh);
        string round = s.Serialize();
        Assert.Equal("v1; fractal Bulb Mandelbulb BulbPower=6 BulbIterations=10 #E69F0080 nolabel mesh 40", round);
        Assert.True(MathPlane3DScene.TryParse(round, out var s2, out _));
        Assert.Equal(s, s2);
    }

    [Theory]
    [InlineData("fractal F", "needs a 3D type")]
    [InlineData("fractal F Mandelbrot", "needs a 3D type")]
    [InlineData("fractal F UserBulb", "needs a 3D type")]
    [InlineData("fractal F Mandelbulb BulbPower=", "KEY=VALUE")]
    [InlineData("fractal F Mandelbulb BulbPower=@x.txt", "KEY=VALUE")]
    [InlineData("fractal F Mandelbulb BulbPower=3 bulbpower=4", "given twice")]
    [InlineData("fractal F Mandelbulb Cam3DDistance=4", "view setting")]
    [InlineData("fractal F Mandelbulb MathPlane3DScene=x", "view setting")]
    [InlineData("fractal F Mandelbulb 3", "too many numbers")]
    public void BadStatements_SayWhy(string stmt, string fragment)
    {
        Assert.False(MathPlane3DScene.TryParse("v1; " + stmt, out _, out var err));
        Assert.Contains(fragment, err!.Message);
    }

    // ── Cache, cancellation, export ──

    [Fact]
    public void TheMesh_IsKeptWhileItsTypeSettingsAndMeshAreUnchanged()
    {
        var calc = Render(Params("fractal F Mandelbulb mesh 16"));
        Assert.Equal(1, calc.ImplicitBuilds);
        Render(Params("fractal F Mandelbulb #56B4E9 mesh 16; point A 1 1 1"), calc);
        Render(Params("fractal G Mandelbulb mesh 16 nolabel"), calc);
        Assert.Equal(1, calc.ImplicitBuilds);
        Render(Params("fractal G Mandelbulb BulbPower=4 mesh 16"), calc);
        Assert.Equal(2, calc.ImplicitBuilds);
        Render(Params("fractal G Mandelbulb BulbPower=4 mesh 20"), calc);
        Assert.Equal(3, calc.ImplicitBuilds);
        // Another surface meshes once more, and the fractal's mesh is still kept.
        Render(Params("fractal G Mandelbulb BulbPower=4 mesh 20; surface H implicit \"x^2 + y^2 + z^2 - 1\" mesh 20"), calc);
        Assert.Equal(4, calc.ImplicitBuilds);
    }

    [Fact]
    public void ACancelledRender_MeshesAgainNextTime()
    {
        var calc = new MathPlane3DCalculator(W, H) { Zoom = 1 };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Render(Params("fractal F Mandelbulb mesh 16"), calc, cts.Token));
        Render(Params("fractal F Mandelbulb mesh 16"), calc);
        Assert.Null(calc.LastSceneError);
        Assert.Contains(calc.ColorBuffer, c => c != MathPlane3DCalculator.BackgroundColor);
    }

    [Fact]
    public void TheMeshExport_IncludesFractals()
    {
        var p = Params("fractal F Mandelbox mesh 16");
        var mesh = MathPlane3DMeshExporter.Build(p);
        Assert.Equal(1, mesh.Surfaces);
        var direct = MathPlane3DFractalMesher.Build(Object("fractal F Mandelbox mesh 16"), out _)!;
        Assert.Equal(direct.Triangles.Length, mesh.Triangles.Count);
    }

    [Fact]
    public void TheCommandBuilder_RoundTripsAFractalScene()
    {
        var p = Params("fractal F Mandelbulb BulbPower=5 #009E73 mesh 20; vector v 1 1 1", 7);
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane3D, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane3D, CenterX = 0, CenterY = 0, Zoom = 1, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.Empty(report.Gaps);
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var perr), perr);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane3D, out var ferr)!.ApplyTo(fresh);
        Assert.Null(ferr);
        Assert.Equal(Render(p).ColorBuffer, Render(fresh).ColorBuffer);
    }

    // ── Control Center ──

    [Fact]
    public void PlusFractal_AddsAMandelbulb_AndEditsItsTypeAndSettings()
    {
        var view = new FractalViewState { FractalType = FractalType.MathPlane3D, Zoom = 1 };
        view.FractalParameters.MathPlane3DScene = "v1";
        var vm = new MathPlane3DSectionViewModel(view, () => { }, () => { });
        vm.AddFractalCommand.Execute().Subscribe();
        Assert.True(vm.SelectedIsFractal);
        Assert.False(vm.HasCoords);
        Assert.False(vm.HasExpression1);
        Assert.Equal("Mandelbulb", vm.SelectedFractalType);
        Assert.True(vm.HasVisibleSurface);
        Assert.Contains("fractal S1 Mandelbulb", view.FractalParameters.MathPlane3DScene);

        vm.SelectedFractalType = "Mandelbox";
        vm.SelectedFractalSettings = "MandelboxScale=-1.5 MandelboxIterations=8";
        Assert.Null(vm.EditError);
        Assert.True(MathPlane3DScene.TryParse(view.FractalParameters.MathPlane3DScene, out var s, out _));
        Assert.Equal(new[] { "Mandelbox", "MandelboxScale=-1.5", "MandelboxIterations=8" }, s.Objects.Single().Expressions);

        string before = view.FractalParameters.MathPlane3DScene;
        vm.SelectedFractalSettings = "MandelboxScale = 2";
        Assert.NotNull(vm.EditError);
        Assert.Equal(before, view.FractalParameters.MathPlane3DScene);
        vm.SelectedFractalSettings = "Cam3DDistance=3";
        Assert.Contains("view setting", vm.EditError);
        Assert.Equal(before, view.FractalParameters.MathPlane3DScene);
        Assert.Contains("fractal S1: Mandelbox", vm.Rows.Single());
    }
}
