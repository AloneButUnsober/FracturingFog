// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/MathPlane3DImplicitTests.cs
//
// #1717 (Math Plane 3D T5) — implicit surfaces F(x, y, z) = 0 by marching cubes.
// Checks use facts that hold whatever the mesher does: the sphere's radius and volume,
// the plane's equation and normal, MeshValidator's topology, and the depth a look-at
// camera sees along its own axis.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Export;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlane3DImplicitTests
{
    private const int W = 320, H = 200;
    private const string Bare = "v1; axes off; grid off; ";

    private static MathPlaneObject Object(string scene)
    {
        Assert.True(MathPlane3DScene.TryParse(scene, out var s, out var err), err?.ToString());
        return s.Objects.Single();
    }

    private static MathPlane3DImplicitMesh Mesh(string scene)
        => MathPlane3DImplicitMesher.Build(Object(scene)) ?? throw new Xunit.Sdk.XunitException("no mesh");

    private static (double X, double Y, double Z) Normal(MathPlane3DImplicitMesh m, (int A, int B, int C) t)
    {
        var a = m.Vertices[t.A]; var b = m.Vertices[t.B]; var c = m.Vertices[t.C];
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
    }

    private static FractalParameters Params(string scene, double dist = 6) => new()
    {
        MathPlane3DScene = scene, MathPlane3DCameraDistance = dist, MathPlane3DCameraTheta = 0.7, MathPlane3DCameraPhi = 1.1,
    };

    private static MathPlane3DCalculator Render(FractalParameters p, double zoom = 1)
    {
        var calc = new MathPlane3DCalculator(W, H) { Zoom = zoom, FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        Assert.Null(calc.LastSceneError);
        return calc;
    }

    // ── Scene text ──

    [Fact]
    public void TheImplicitStatement_ParsesItsBox_AndRoundTrips()
    {
        var o = Object("v1; surface S implicit \"x^2 + y^2 - z\" -1 2 -3 4 -5 6 #E69F00 mesh 40");
        Assert.Equal(MathPlaneCurveKind.Implicit, o.CurveKind);
        Assert.Equal((-1.0, 2.0, -3.0, 4.0, -5.0, 6.0), MathPlane3DScene.ImplicitBox(o));
        Assert.Equal(40, o.Mesh);
        var d = Object("v1; surface S implicit \"x + y + z\"");
        Assert.Equal((-3.0, 3.0, -3.0, 3.0, -3.0, 3.0), MathPlane3DScene.ImplicitBox(d));

        Assert.True(MathPlane3DScene.TryParse("v1; surface S implicit \"x*y*z - 1\" -2 2 -2 2 -1 1 #56B4E9C0", out var s, out _));
        Assert.True(MathPlane3DScene.TryParse(s.Serialize(), out var back, out _));
        Assert.Equal(s, back);
        Assert.Contains("implicit", s.Serialize());
    }

    [Theory]
    [InlineData("surface S implicit \"x + w\"", "Unknown identifier 'w'")]
    [InlineData("surface S implicit \"x + t\"", "reads only x, y, z")]
    [InlineData("surface S implicit \"x\" -1 1 -1 1", "the box needs xmin xmax ymin ymax zmin zmax")]
    [InlineData("surface S implicit", "an implicit surface needs a quoted expression")]
    public void BadImplicitStatements_SayWhy(string stmt, string fragment)
    {
        Assert.False(MathPlane3DScene.TryParse("v1; " + stmt, out _, out var err));
        Assert.Contains(fragment, err!.Message);
    }

    // ── The mesher ──

    [Fact]
    public void TheUnitSphere_MeshesWatertight_OnTheSphere_WithOutwardNormals()
    {
        var m = Mesh("v1; surface S implicit \"x^2 + y^2 + z^2 - 1\"");
        Assert.True(m.Triangles.Length > 1000);
        Assert.False(m.Truncated);

        var report = MeshValidator.Validate(m.Vertices, m.Triangles, 1e-9);
        Assert.True(report.IsClosedManifold, report.Summary());
        Assert.Equal(0, report.DegenerateTriangleCount);

        // Every vertex on the sphere: well within one cell (6/64), in fact to the bisection's bracket.
        double cell = 6.0 / MathPlane3DScene.DefaultMesh;
        foreach (var (x, y, z) in m.Vertices)
            Assert.InRange(Math.Sqrt(x * x + y * y + z * z) - 1, -cell / 100, cell / 100);

        // The enclosed volume is the ball's (an inscribed polyhedron, a little under), and it is
        // positive: the triangles face outward. Each face does, too.
        Assert.InRange(report.SignedVolume, 4.0 / 3 * Math.PI * 0.98, 4.0 / 3 * Math.PI);
        foreach (var t in m.Triangles)
        {
            var n = Normal(m, t);
            var c = m.Vertices[t.A];
            Assert.True(n.X * c.X + n.Y * c.Y + n.Z * c.Z > 0);
        }
    }

    [Fact]
    public void APlane_IsFlat_AndFacesWhereFIsPositive()
    {
        // mesh 7 keeps the lattice off the plane (no node has x + y + z = 0).
        var m = Mesh("v1; surface P implicit \"x + y + z\" mesh 7");
        Assert.NotEmpty(m.Triangles);
        foreach (var (x, y, z) in m.Vertices) Assert.InRange(x + y + z, -1e-9, 1e-9);
        double s3 = Math.Sqrt(3);
        foreach (var t in m.Triangles)
        {
            var n = Normal(m, t);
            double len = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            Assert.True(len > 1e-12);
            Assert.Equal(1.0, (n.X + n.Y + n.Z) / (len * s3), 9);   // parallel to (1, 1, 1), same side
        }
    }

    [Fact]
    public void APole_LeavesNoFalseSheet()
    {
        // mesh 9: no lattice node on x = 0, so F = 1/x is finite at every node and changes
        // sign across x = 0 without a root.
        Assert.Empty(Mesh("v1; surface P implicit \"1/x\" mesh 9").Triangles);

        // 1/x - 1 has its root at x = 1 and its pole at x = 0: only the root meshes.
        var m = Mesh("v1; surface P implicit \"1/x - 1\" mesh 9");
        Assert.NotEmpty(m.Triangles);
        foreach (var (x, _, _) in m.Vertices) Assert.InRange(x, 1 - 1e-3, 1 + 1e-3);

        // Not real (NaN) on half the box: those cells drop, the root x = 1 still meshes.
        var g = Mesh("v1; surface G implicit \"log(x)\" mesh 9");
        Assert.NotEmpty(g.Triangles);
        foreach (var (x, _, _) in g.Vertices) Assert.InRange(x, 1 - 1e-3, 1 + 1e-3);
    }

    [Fact]
    public void ZIsARealInput_NotTheComplexXPlusIY()
    {
        // In the 2D language z = x + iy; here F = z - 0.5 is the plane z = 0.5.
        var m = Mesh("v1; surface Z implicit \"z - 0.5\" mesh 7");
        Assert.NotEmpty(m.Triangles);
        foreach (var (_, _, z) in m.Vertices) Assert.Equal(0.5, z, 9);
    }

    // ── The render ──

    [Fact]
    public void TheSphere_IsDrawn_AtTheDepthTheCameraAxisMeetsIt()
    {
        // The look-at camera's axis passes through the target (the origin) at the screen
        // centre, so it meets the unit sphere's front at distance d - 1. Under a scale-2 map
        // the drawn sphere has radius 2: d - 2.
        const double d = 6;
        foreach (var (map, r) in new[] { (false, 1.0), (true, 2.0) })
        {
            var p = Params(Bare + "surface S implicit \"x^2 + y^2 + z^2 - 1\"", d);
            if (map)
            {
                p.MathPlane3DMapOn = true; p.MathPlane3DT = 1;
                p.MathPlane3DM11 = 2; p.MathPlane3DM12 = 0; p.MathPlane3DM13 = 0;
                p.MathPlane3DM21 = 0; p.MathPlane3DM22 = 2; p.MathPlane3DM23 = 0;
                p.MathPlane3DM31 = 0; p.MathPlane3DM32 = 0; p.MathPlane3DM33 = 2;
            }
            var calc = Render(p);
            float iz = calc.ZBuffer[(H / 2) * W + W / 2];
            Assert.True(iz > 0);
            Assert.InRange(1 / iz, d - r - 0.01, d - r + 0.01);
            Assert.NotEqual(MathPlane3DCalculator.BackgroundColor, calc.ColorBuffer[(H / 2) * W + W / 2]);
        }
    }

    [Fact]
    public void TheMesh_IsKeptWhileItsGeometryIsUnchanged()
    {
        var p = Params(Bare + "surface S implicit \"x^2 + y^2 + z^2 - 1\" #4FA3FF");
        var calc = new MathPlane3DCalculator(W, H) { FractalParameters = p };
        calc.Calculate();
        Assert.Equal(1, calc.ImplicitBuilds);
        // Colour, name, another object, an orbit: no new marching cubes.
        p.MathPlane3DScene = Bare + "surface T implicit \"x^2 + y^2 + z^2 - 1\" #E69F00; point A 1 1 1";
        p.MathPlane3DCameraTheta += 0.4;
        calc.Calculate();
        Assert.Equal(1, calc.ImplicitBuilds);
        // A new resolution, box or formula: a new mesh.
        p.MathPlane3DScene = Bare + "surface T implicit \"x^2 + y^2 + z^2 - 1\" #E69F00 mesh 32";
        calc.Calculate();
        Assert.Equal(2, calc.ImplicitBuilds);
        p.MathPlane3DScene = Bare + "surface T implicit \"x^2 + y^2 + z^2 - 1\" -2 2 -2 2 -2 2 #E69F00 mesh 32";
        calc.Calculate();
        Assert.Equal(3, calc.ImplicitBuilds);
    }

    [Fact]
    public void ACancelledRender_MeshesAgainNextTime()
    {
        var p = Params(Bare + "surface S implicit \"x^2 + y^2 + z^2 - 1\"");
        var calc = new MathPlane3DCalculator(W, H) { FractalParameters = p };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => calc.Calculate(cts.Token));
        calc.Calculate();
        Assert.True(calc.ZBuffer[(H / 2) * W + W / 2] > 0);
    }

    // ── Batch ──

    [Fact]
    public void TheCommandBuilder_RoundTripsAnImplicitScene()
    {
        var p = Params("v1; surface S implicit \"(sqrt(x^2 + y^2) - 2)^2 + z^2 - 0.25\" -3 3 -3 3 -1 1 #E69F00C0 mesh 40", 7);
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane3D, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane3D, CenterX = 0, CenterY = 0, Zoom = 1.1, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.Empty(report.Gaps);
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var perr), perr);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane3D, out var ferr)!.ApplyTo(fresh);
        Assert.Null(ferr);
        Assert.Equal(p.MathPlane3DScene, fresh.MathPlane3DScene);
        Assert.Equal(Render(p, 1.1).ColorBuffer, Render(fresh, o.Zoom ?? 1).ColorBuffer);
    }
}
