// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/MathPlane3DMeshExportTests.cs
//
// #1718 (Math Plane 3D T6) — mesh export of a scene's surfaces through the S9 writers.
// Checks are read back from the written files with the S9 readers and use facts that hold
// whatever the exporter does: a sphere's radius after a known scale, a plane's height in
// each file's axes, a full grid's topology (2n² triangles, 4n boundary edges, one piece)
// and the outward direction of a closed surface's faces.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Export;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlane3DMeshExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ff-mp3d-mesh-" + Guid.NewGuid().ToString("N"));

    public MathPlane3DMeshExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string File_(string name) => Path.Combine(_dir, name);

    private const string Sphere = "surface S implicit \"x^2 + y^2 + z^2 - 1\" -1.5 1.5 -1.5 1.5 -1.5 1.5 #56B4E9 mesh 24";
    private const string Wave = "surface W explicit \"0.3*sin(x)*cos(y)\" -2 2 -2 2 #E69F00 mesh 16";

    private static FractalParameters Params(string scene) => new() { MathPlane3DScene = "v1; " + scene };

    /// <summary>A map of uniform scale <paramref name="k"/> (linear frames), shown at <paramref name="t"/>.</summary>
    private static FractalParameters Scaled(FractalParameters p, double k, double t)
    {
        p.MathPlane3DMapOn = true;
        p.MathPlane3DM11 = k; p.MathPlane3DM22 = k; p.MathPlane3DM33 = k;
        p.MathPlane3DM12 = p.MathPlane3DM13 = p.MathPlane3DM21 = p.MathPlane3DM23 = p.MathPlane3DM31 = p.MathPlane3DM32 = 0;
        p.MathPlane3DT = t;
        p.MathPlane3DInterpolation = MathPlaneInterpolation.Linear;
        return p;
    }

    private static double Len((double X, double Y, double Z) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private static (double X, double Y, double Z) FaceNormal(IReadOnlyList<(double X, double Y, double Z)> pos, (int A, int B, int C) t)
    {
        var a = pos[t.A]; var b = pos[t.B]; var c = pos[t.C];
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
    }

    private static void AssertClose((double X, double Y, double Z) want, (double X, double Y, double Z) got)
    {
        // float precision: 2^-23 relative, with an absolute floor near 0.
        double tol = 1e-6 * Math.Max(1.0, Len(want));
        Assert.True(Math.Abs(want.X - got.X) <= tol && Math.Abs(want.Y - got.Y) <= tol && Math.Abs(want.Z - got.Z) <= tol,
            $"want {want}, got {got}");
    }

    // ── Read-back ──

    [Theory]
    [InlineData("scene.glb")]
    [InlineData("scene.gltf")]
    public void Gltf_ReadsBack_TheSamePositionsInYUp_TheSameTriangles_AndTheSurfaceColours(string name)
    {
        var p = Params(Sphere + "; " + Wave);
        var mesh = MathPlane3DMeshExporter.Build(p);
        Assert.Equal(2, mesh.Surfaces);
        string path = File_(name);
        var result = MathPlane3DMeshExporter.Export(path, p);
        Assert.Equal(mesh.Triangles.Count, result.Triangles);

        var (pos, col, tris) = GltfMeshReader.Read(path);
        Assert.Equal(mesh.Positions.Count, pos.Count);
        Assert.Equal(mesh.Triangles, tris);
        for (int k = 0; k < pos.Count; k++)
            AssertClose(MathPlane3DMeshExporter.ToYUp(mesh.Positions[k]), pos[k]);
        // Two colours: per-vertex, each vertex its own surface's RGB.
        Assert.Equal(pos.Count, col.Count);
        var want = new HashSet<(byte, byte, byte)> { (0x56, 0xB4, 0xE9), (0xE6, 0x9F, 0x00) };
        Assert.All(col, c => Assert.Contains(c, want));
        Assert.Equal(want, col.ToHashSet());
    }

    [Fact]
    public void Stl_ReadsBack_EveryTriangleCorner_InZUp()
    {
        var p = Params(Sphere + "; " + Wave);
        var mesh = MathPlane3DMeshExporter.Build(p);
        string path = File_("scene.stl");
        MathPlane3DMeshExporter.Export(path, p);

        var (pos, tris) = StlMeshReader.ReadBinary(path);
        Assert.Equal(mesh.Triangles.Count, tris.Count);
        for (int k = 0; k < tris.Count; k++)
        {
            var w = mesh.Triangles[k]; var g = tris[k];
            AssertClose(mesh.Positions[w.A], pos[g.A]);
            AssertClose(mesh.Positions[w.B], pos[g.B]);
            AssertClose(mesh.Positions[w.C], pos[g.C]);
        }
    }

    [Fact]
    public void ThePlaneZ_IsAtZInStl_AndAtYInGltf()
    {
        var p = Params("surface P explicit \"0.5\" -1 1 -1 1 mesh 4");
        MathPlane3DMeshExporter.Export(File_("p.stl"), p);
        MathPlane3DMeshExporter.Export(File_("p.glb"), p);
        var (stl, _) = StlMeshReader.ReadBinary(File_("p.stl"));
        var (glb, _, _) = GltfMeshReader.Read(File_("p.glb"));
        Assert.All(stl, v => Assert.Equal(0.5, v.Z, 6));
        Assert.All(glb, v => Assert.Equal(0.5, v.Y, 6));
        // glTF's z is the scene's -y: the grid's y range [-1, 1] lands there.
        Assert.Equal(-1.0, glb.Min(v => v.Z), 6);
        Assert.Equal(1.0, glb.Max(v => v.Z), 6);
    }

    // ── What is exported ──

    [Theory]
    [InlineData(3.0, 1.0, 3.0)]
    [InlineData(3.0, 0.5, 2.0)]   // linear frames: I + t (M - I)
    public void TheMap_AtTheCurrentT_IsApplied(double k, double t, double radius)
    {
        var p = Scaled(Params(Sphere), k, t);
        MathPlane3DMeshExporter.Export(File_("s.stl"), p);
        var (pos, _) = StlMeshReader.ReadBinary(File_("s.stl"));
        // Marching cubes places vertices on cell edges, within a small error of the sphere.
        Assert.All(pos, v => Assert.InRange(Len(v), radius * 0.985, radius * 1.015));
    }

    [Fact]
    public void TheMap_MovesExplicitSurfacesToo()
    {
        var p = Scaled(Params("surface P explicit \"0.5\" -1 1 -1 1 mesh 4"), 2, 1);
        MathPlane3DMeshExporter.Export(File_("p2.stl"), p);
        var (pos, _) = StlMeshReader.ReadBinary(File_("p2.stl"));
        Assert.All(pos, v => Assert.Equal(1.0, v.Z, 6));
        Assert.Equal(2.0, pos.Max(v => v.X), 6);
    }

    /// <summary>The NORMAL accessor of a .glb (GltfMeshReader reads positions, colours, indices only).</summary>
    private static List<(double X, double Y, double Z)> GlbNormals(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int jsonLen = BitConverter.ToInt32(b, 12);
        using var doc = System.Text.Json.JsonDocument.Parse(new ReadOnlyMemory<byte>(b, 20, jsonLen));
        int binStart = 20 + jsonLen + 8;
        var root = doc.RootElement;
        int acc = root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("NORMAL").GetInt32();
        var a = root.GetProperty("accessors")[acc];
        var bv = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        int off = binStart + (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0)
                           + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        var list = new List<(double, double, double)>();
        for (int k = 0; k < a.GetProperty("count").GetInt32(); k++, off += 12)
            list.Add((BitConverter.ToSingle(b, off), BitConverter.ToSingle(b, off + 4), BitConverter.ToSingle(b, off + 8)));
        return list;
    }

    [Fact]
    public void GltfNormals_AreUnit_InTheFilesAxes_AndPointOutOfASphere()
    {
        string path = File_("n.glb");
        MathPlane3DMeshExporter.Export(path, Params(Sphere));
        var (pos, _, _) = GltfMeshReader.Read(path);
        var nrm = GlbNormals(path);
        Assert.Equal(pos.Count, nrm.Count);
        for (int k = 0; k < pos.Count; k++)
        {
            Assert.Equal(1.0, Len(nrm[k]), 4);
            // On a unit sphere the outward normal is the position itself (to the mesh's accuracy).
            double cos = (nrm[k].X * pos[k].X + nrm[k].Y * pos[k].Y + nrm[k].Z * pos[k].Z) / Len(pos[k]);
            Assert.True(cos > 0.98, $"vertex {k}: cos {cos}");
        }
    }

    [Fact]
    public void AMirrorMap_KeepsAClosedSurface_FacingOutward()
    {
        var p = Params(Sphere);
        p.MathPlane3DMapOn = true;
        p.MathPlane3DM11 = -1; p.MathPlane3DM22 = 1; p.MathPlane3DM33 = 1;
        p.MathPlane3DT = 1;
        Assert.True(MathPlane3DMap.Current(p).Det < 0);
        var mesh = MathPlane3DMeshExporter.Build(p);
        var report = MeshValidator.Validate(mesh.Positions, mesh.Triangles);
        Assert.True(report.IsClosedManifold, report.Summary());
        Assert.True(report.SignedVolume > 0, $"signed volume {report.SignedVolume}");
        foreach (var t in mesh.Triangles)
        {
            var n = FaceNormal(mesh.Positions, t);
            var a = mesh.Positions[t.A];
            Assert.True(n.X * a.X + n.Y * a.Y + n.Z * a.Z > 0);
        }
    }

    [Fact]
    public void AnExplicitSurfaceOverAFullGrid_IsOneConnectedConsistentlyOrientedSheet()
    {
        const int n = 16;
        var p = Params(Wave);
        string path = File_("w.stl");
        var result = MathPlane3DMeshExporter.Export(path, p);
        // Grid vertices are shared by the cells round them (smooth normals in glTF).
        Assert.Equal((n + 1) * (n + 1), MathPlane3DMeshExporter.Build(p).Positions.Count);
        // Validate what the file holds (STL repeats corners; the validator welds them).
        var (pos, tris) = StlMeshReader.ReadBinary(path);
        var report = MeshValidator.Validate(pos, tris);
        Assert.Equal(2 * n * n, tris.Count);
        Assert.Equal((n + 1) * (n + 1), report.WeldedVertexCount);
        Assert.True(report.IsEdgeManifold, report.Summary());
        Assert.True(report.IsConsistentlyOriented, report.Summary());
        Assert.Equal(4 * n, report.BoundaryEdgeCount);   // only the grid's rim is open
        Assert.Equal(report.IsClosedManifold, result.Report.IsClosedManifold);

        // One piece: union the welded corners of every triangle.
        var id = new Dictionary<(long, long, long), int>();
        int Key((double X, double Y, double Z) v)
        {
            var k = ((long)Math.Round(v.X * 1e5), (long)Math.Round(v.Y * 1e5), (long)Math.Round(v.Z * 1e5));
            if (!id.TryGetValue(k, out int i)) id[k] = i = id.Count;
            return i;
        }
        var corners = tris.Select(t => (Key(pos[t.A]), Key(pos[t.B]), Key(pos[t.C]))).ToList();
        var parent = Enumerable.Range(0, id.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var (a, b, c) in corners) { parent[Find(a)] = Find(b); parent[Find(b)] = Find(c); }
        Assert.Single(Enumerable.Range(0, id.Count).Select(Find).Distinct());

        // The sheet faces up (+z), as the render winds it, for this gentle surface.
        Assert.All(tris, t => Assert.True(FaceNormal(pos, t).Z > 0));
    }

    [Fact]
    public void OneColour_IsTheMaterialBaseColour_WithoutVertexColours()
    {
        string path = File_("one.gltf");
        MathPlane3DMeshExporter.Export(path, Params(Sphere));
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("COLOR_0", json);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var f = doc.RootElement.GetProperty("materials")[0].GetProperty("pbrMetallicRoughness")
            .GetProperty("baseColorFactor").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        Assert.Equal(0x56 / 255.0, f[0], 4);
        Assert.Equal(0xB4 / 255.0, f[1], 4);
        Assert.Equal(0xE9 / 255.0, f[2], 4);
        Assert.Equal(1.0, f[3], 6);

        string two = File_("two.gltf");
        MathPlane3DMeshExporter.Export(two, Params(Sphere + "; " + Wave));
        Assert.Contains("COLOR_0", File.ReadAllText(two));
    }

    [Fact]
    public void HiddenSurfaces_AndObjectsThatAreNotSurfaces_AreLeftOut()
    {
        var p = Params("point A 1 2 3; vector v 1 0 0; curve h \"cos(t)\" \"sin(t)\" \"t/4\" 0 12; "
                       + Sphere + " hidden");
        Assert.Empty(MathPlane3DMeshExporter.Build(p).Triangles);
        string path = File_("none.glb");
        var ex = Assert.Throws<InvalidOperationException>(() => MathPlane3DMeshExporter.Export(path, p));
        Assert.Equal(MathPlane3DMeshExporter.NoSurfacesMessage, ex.Message);
        Assert.False(File.Exists(path));

        // With the sphere visible, only it is exported.
        var shown = Params("point A 1 2 3; " + Sphere + "; surface H explicit \"x\" hidden");
        Assert.Equal(1, MathPlane3DMeshExporter.Build(shown).Surfaces);
    }

    [Theory]
    [InlineData("a.obj")]
    [InlineData("a.3mf")]
    [InlineData("a")]
    public void OtherFileTypes_AreRefused(string name)
        => Assert.Throws<ArgumentException>(() => MathPlane3DMeshExporter.Export(File_(name), Params(Sphere)));

    // ── Batch ──

    [Fact]
    public void TheBatchFlag_ParsesMeshPaths_AndRefusesOtherTypes()
    {
        foreach (var ext in new[] { ".glb", ".gltf", ".STL" })
        {
            Assert.True(BatchOptions.TryParse(new[] { BatchFlags.Fractal, "MathPlane3D", "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", BatchFlags.MathPlane3DMesh, "m" + ext }, 0, out var o, out var err), err);
            Assert.Equal("m" + ext, o.MathPlane3DMesh);
        }
        Assert.False(BatchOptions.TryParse(new[] { BatchFlags.Fractal, "MathPlane3D", "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", BatchFlags.MathPlane3DMesh, "m.obj" }, 0, out _, out var bad));
        Assert.Contains(".glb, .gltf or .stl", bad);
        // The batch's list agrees with the exporter's.
        Assert.Equal(MathPlane3DMeshExporter.Extensions, BatchOptions.MathPlane3DMeshExtensions);

        var spec = BatchFlagCatalog.All.Single(f => f.Name == BatchFlags.MathPlane3DMesh);
        Assert.Equal(BatchFlagKind.Text, spec.Kind);
        Assert.Contains(BatchFlags.MathPlane3DMesh, BatchFlagCatalog.FormatUsage());
    }

    [Fact]
    public void TheCommandBuilder_EmitsTheMeshPath_AndItParsesBack()
    {
        var p = Params(Sphere);
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane3D, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane3D, Zoom = 1, Parameters = p, FamilyParams = snap.ToKeyValues(),
            MathPlane3DMeshPath = "out/scene.glb",
        });
        Assert.Empty(report.Gaps);
        int at = report.Args.ToList().IndexOf(BatchFlags.MathPlane3DMesh);
        Assert.True(at >= 0);
        Assert.Equal("out/scene.glb", report.Args[at + 1]);
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        Assert.Equal("out/scene.glb", o.MathPlane3DMesh);

        // Without a path there is no flag.
        var plain = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        { Fractal = FractalType.MathPlane3D, Zoom = 1, Parameters = p, FamilyParams = snap.ToKeyValues() });
        Assert.DoesNotContain(BatchFlags.MathPlane3DMesh, plain.Args);
    }

    // ── Control Center ──

    private static MathPlane3DSectionViewModel Vm(string scene, out FractalViewState view)
    {
        view = new FractalViewState { FractalType = FractalType.MathPlane3D, Zoom = 1 };
        view.FractalParameters.MathPlane3DScene = "v1; " + scene;
        var v = view;
        return new MathPlane3DSectionViewModel(view, () => { }, () => v.FractalType = FractalType.MathPlane3D);
    }

    [Fact]
    public void TheExportButton_NeedsTheHost_AndAVisibleSurface()
    {
        var vm = Vm("point A 1 2 3", out var view);
        Assert.False(vm.CanExportMesh);                       // no host hook
        vm.MeshExportRequested = () => Task.FromResult<MathPlane3DMeshOutcome?>(null);
        Assert.False(vm.CanExportMesh);                       // no surface
        view.FractalParameters.MathPlane3DScene = "v1; " + Sphere;
        vm.Refresh();
        Assert.True(vm.CanExportMesh);
        view.FractalParameters.MathPlane3DScene = "v1; " + Sphere + " hidden";
        vm.Refresh();
        Assert.False(vm.CanExportMesh);
    }

    [Fact]
    public void TheExportButton_IsNotified_WhenTheEditorAddsHidesOrDeletesASurface()
    {
        // The button binds to CanExportMesh, so every edit that changes it must raise it;
        // reading the getter alone would pass while the button stayed stale.
        var vm = Vm("point A 1 2 3", out _);
        vm.MeshExportRequested = () => Task.FromResult<MathPlane3DMeshOutcome?>(null);
        bool? seen = null;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.CanExportMesh)) seen = vm.CanExportMesh; };

        vm.AddImplicitCommand.Execute().Subscribe();
        Assert.True(seen);

        seen = null;
        vm.SelectedVisible = false;
        Assert.False(seen);

        seen = null;
        vm.SelectedVisible = true;
        Assert.True(seen);

        seen = null;
        vm.DeleteCommand.Execute().Subscribe();
        Assert.False(seen);

        seen = null;
        vm.AddSurfaceCommand.Execute().Subscribe();
        Assert.True(seen);
    }

    [Fact]
    public async Task TheExportButton_ShowsTheOutcome_NeutralOrYellow()
    {
        var vm = Vm(Sphere, out _);
        vm.MeshExportRequested = () => Task.FromResult<MathPlane3DMeshOutcome?>(new(true, "Saved a.glb: 1 surface"));
        await vm.ExportMeshCommand.Execute();
        Assert.Equal("Saved a.glb: 1 surface", vm.MeshMessage);
        Assert.Null(vm.MeshWarning);
        Assert.False(vm.IsExportingMesh);

        vm.MeshExportRequested = () => Task.FromResult<MathPlane3DMeshOutcome?>(new(false, MathPlane3DMeshExporter.NoSurfacesMessage));
        await vm.ExportMeshCommand.Execute();
        Assert.Null(vm.MeshMessage);
        Assert.Equal(MathPlane3DMeshExporter.NoSurfacesMessage, vm.MeshWarning);

        vm.MeshExportRequested = () => throw new IOException("disk full");
        await vm.ExportMeshCommand.Execute();
        Assert.Equal("Mesh export failed: disk full", vm.MeshWarning);

        vm.MeshExportRequested = () => Task.FromResult<MathPlane3DMeshOutcome?>(null);   // cancelled dialog
        await vm.ExportMeshCommand.Execute();
        Assert.Null(vm.MeshMessage);
        Assert.True(vm.CanExportMesh);
    }
}
