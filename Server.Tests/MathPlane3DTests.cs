// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1713 (Math Plane 3D T1, #1556) — the scene text, the camera's projection checked
// against an independently written pinhole formula, the z-buffer's order
// independence and occlusion, depth-tested lines, and the batch round trips
// (--param, the region snapshot, the Command builder, CameraParamBinding).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Render;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlane3DTests
{
    private const int W = 320, H = 200;

    private static FractalParameters Params(string scene, double dist = 6, double theta = 0.7, double phi = 1.1) => new()
    {
        MathPlane3DScene = scene,
        MathPlane3DCameraDistance = dist, MathPlane3DCameraTheta = theta, MathPlane3DCameraPhi = phi,
    };

    private static MathPlane3DCalculator Render(FractalParameters p, double zoom = 1)
    {
        var calc = new MathPlane3DCalculator(W, H) { Zoom = zoom, FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    // ── Scene text ───────────────────────────────────────────────────────

    [Fact]
    public void TheSceneText_RoundTrips()
    {
        const string text = "v1; axes off; ticks off; grid xz yz; labels fixed:2; point P 1 2 3 #F0E442; "
            + "vector v 1 0 2 from 1 1 1 #CC79A780 nolabel; segment s 0 0 0 1 1 1; polyline q 0 0 0 1 0 0 1 1 0 hidden";
        Assert.True(MathPlane3DScene.TryParse(text, out var scene, out var err), err?.ToString());
        Assert.False(scene.ShowAxes);
        Assert.Equal(MathPlane3DGrid.XZ | MathPlane3DGrid.YZ, scene.Grid);
        Assert.Equal(0x80CC79A7u, scene.Objects[1].Color);
        Assert.Equal((1.0, 1.0, 1.0), MathPlane3DScene.Tail(scene.Objects[1]));
        Assert.Equal((2.0, 1.0, 3.0), MathPlane3DScene.Tip(scene.Objects[1]));
        Assert.True(MathPlane3DScene.TryParse(scene.Serialize(), out var again, out _));
        Assert.Equal(scene, again);
        Assert.True(MathPlane3DScene.TryParse(MathPlane3DScene.DefaultText, out var def, out _));
        Assert.Equal(3, def.Objects.Count);
    }

    [Theory]
    [InlineData("point P 1 2", "needs 3 numbers")]
    [InlineData("segment s 0 0 0 1 1", "needs 6 numbers")]
    [InlineData("polyline q 0 0 0 1", "two or more x y z")]
    [InlineData("vector v 1 0 0 from 1 1", "'from' needs x y z")]
    [InlineData("grid xw", "xy, xz and / or yz")]
    [InlineData("point P 1 2 3; point P 0 0 0", "already used")]
    [InlineData("sphere S 0 0 0 1", "unknown statement")]
    [InlineData("v2", "unsupported scene version")]
    [InlineData("point P 1 2 3 #12345", "unknown option")]
    public void BadScenes_AreErrors(string text, string expect)
    {
        Assert.False(MathPlane3DScene.TryParse(text, out _, out var err));
        Assert.Contains(expect, err!.Message);
    }

    // ── Camera and projection ────────────────────────────────────────────

    // Written independently of the calculator: look-at with z up, 45° vertical FOV.
    private static (double X, double Y) Pinhole(double x, double y, double z, double dist, double th, double ph, double zoom, double tx = 0, double ty = 0, double tz = 0)
    {
        double d = dist / zoom;
        double ex = tx + d * Math.Sin(ph) * Math.Cos(th), ey = ty + d * Math.Sin(ph) * Math.Sin(th), ez = tz + d * Math.Cos(ph);
        // Forward f = (target - eye)/|...|; right = f x up(z); up' = right x f.
        double fx = tx - ex, fy = ty - ey, fz = tz - ez, fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
        fx /= fl; fy /= fl; fz /= fl;
        double rx = fy * 1 - fz * 0, ry = fz * 0 - fx * 1, rz = 0;
        double rl = Math.Sqrt(rx * rx + ry * ry + rz * rz); rx /= rl; ry /= rl;
        double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
        double px = x - ex, py = y - ey, pz = z - ez;
        double vx = px * rx + py * ry + pz * rz, vy = px * ux + py * uy + pz * uz, vz = px * fx + py * fy + pz * fz;
        double f = (H / 2.0) / Math.Tan(Math.PI / 8);
        return (W / 2.0 + f * vx / vz, H / 2.0 - f * vy / vz);
    }

    [Theory]
    [InlineData(1.0, 2.0, 0.5, 6.0, 0.7, 1.1, 1.0)]
    [InlineData(-1.5, 0.3, 2.0, 9.0, -2.4, 0.4, 1.7)]
    [InlineData(0.0, 0.0, 0.0, 4.0, 3.0, 2.9, 0.6)]
    public void ProjectionMatchesThePinholeFormula(double x, double y, double z, double dist, double th, double ph, double zoom)
    {
        var p = Params("", dist, th, ph);
        var cam = MathPlane3DCalculator.CameraFor(p, zoom, W, H);
        var got = cam.Project(x, y, z)!.Value;
        var want = Pinhole(x, y, z, dist, th, ph, zoom);
        Assert.Equal(want.X, got.X, 9);
        Assert.Equal(want.Y, got.Y, 9);
    }

    [Fact]
    public void TheTargetProjectsToTheCentre_AndMovesTheView()
    {
        var p = Params("", 6, 0.3, 1.0);
        p.MathPlane3DTargetX = 2; p.MathPlane3DTargetY = -1; p.MathPlane3DTargetZ = 0.5;
        var c = MathPlane3DCalculator.CameraFor(p, 1, W, H).Project(2, -1, 0.5)!.Value;
        Assert.Equal(W / 2.0, c.X, 9);
        Assert.Equal(H / 2.0, c.Y, 9);
        var off = MathPlane3DCalculator.CameraFor(p, 1, W, H).Project(3, 0, 1)!.Value;
        var want = Pinhole(3, 0, 1, 6, 0.3, 1.0, 1, 2, -1, 0.5);
        Assert.Equal(want.X, off.X, 9);
        Assert.Equal(want.Y, off.Y, 9);
    }

    [Fact]
    public void ZoomMovesTheCameraCloser()
    {
        var far = MathPlane3DCalculator.CameraFor(Params("", 6), 1, W, H);
        var near = MathPlane3DCalculator.CameraFor(Params("", 6), 2, W, H);
        double Dist(MathPlane3DCamera c) => Math.Sqrt(c.Eye.X * c.Eye.X + c.Eye.Y * c.Eye.Y + c.Eye.Z * c.Eye.Z);
        Assert.Equal(6.0, Dist(far), 9);
        Assert.Equal(3.0, Dist(near), 9);
    }

    // ── z-buffer ─────────────────────────────────────────────────────────

    private static Projected3D P(double x, double y, double z) => new(x, y, 1 / z);

    private static List<ScreenTriangle> Triangles()
    {
        var rng = new Random(1713);
        var list = new List<ScreenTriangle>();
        for (int i = 0; i < 40; i++)
        {
            double cx = rng.NextDouble() * W, cy = rng.NextDouble() * H, s = 20 + rng.NextDouble() * 60;
            // Non-coplanar: each vertex at its own depth.
            list.Add(new ScreenTriangle(P(cx, cy - s, 2 + rng.NextDouble() * 5), P(cx - s, cy + s, 2 + rng.NextDouble() * 5),
                                        P(cx + s, cy + s, 2 + rng.NextDouble() * 5), 0xFF000000u | (uint)rng.Next(0xFFFFFF)));
        }
        // A closed fan sharing edges and a centre vertex (top-left rule territory).
        for (int k = 0; k < 8; k++)
        {
            double a0 = k * Math.PI / 4, a1 = (k + 1) * Math.PI / 4;
            list.Add(new ScreenTriangle(P(160, 100, 1.5), P(160 + 40 * Math.Cos(a0), 100 + 40 * Math.Sin(a0), 1.5),
                                        P(160 + 40 * Math.Cos(a1), 100 + 40 * Math.Sin(a1), 1.5), 0xFF102030u + (uint)k));
        }
        return list;
    }

    [Fact]
    public void Occlusion_DoesNotDependOnDrawOrder()
    {
        var tris = Triangles();
        uint[] Render(IReadOnlyList<ScreenTriangle> t)
        {
            var c = new uint[W * H]; var d = new float[W * H];
            MathPlane3DCalculator.RasterizeAll(t, c, d, W, H);
            return c;
        }
        var reference = Render(tris);
        var rng = new Random(7);
        for (int trial = 0; trial < 4; trial++)
            Assert.Equal(reference, Render(tris.OrderBy(_ => rng.Next()).ToList()));
        Assert.Equal(reference, Render(Enumerable.Reverse(tris).ToList()));
    }

    [Fact]
    public void TheNearerTriangleWins_AndAClosedFanCoversEachPixelOnce()
    {
        var c = new uint[W * H]; var d = new float[W * H];
        var far = new ScreenTriangle(P(0, 0, 10), P(W, 0, 10), P(0, H, 10), 0xFF0000FF);
        var near = new ScreenTriangle(P(0, 0, 2), P(W, 0, 2), P(0, H, 2), 0xFF00FF00);
        MathPlane3DCalculator.RasterizeAll(new[] { near, far }, c, d, W, H);
        Assert.Equal(0xFF00FF00u, c[10 * W + 10]);

        // The fan's eight triangles meet at the centre: every pixel in the disc belongs to one.
        var fan = Triangles().Skip(40).ToList();
        int covered = 0;
        foreach (var t in fan)
        {
            var cc = new uint[W * H]; var dd = new float[W * H];
            MathPlane3DCalculator.Rasterize(t, cc, dd, W, H);
            covered += cc.Count(x => x != 0);
        }
        var all = new uint[W * H]; var ad = new float[W * H];
        MathPlane3DCalculator.RasterizeAll(fan, all, ad, W, H);
        Assert.Equal(all.Count(x => x != 0), covered);   // no pixel counted twice, none dropped
    }

    [Fact]
    public void DepthTest_HidesBehind_ShowsInFront_AndKeepsOnSurface()
    {
        var c = new uint[W * H]; var d = new float[W * H];
        MathPlane3DCalculator.RasterizeAll(new[] { new ScreenTriangle(P(0, 0, 5), P(W * 2, 0, 5), P(0, H * 2, 5), 0xFFFFFFFF) }, c, d, W, H);
        Assert.False(MathPlane3DCalculator.PassesDepth(d, W, H, 50, 50, 1 / 6.0));
        Assert.True(MathPlane3DCalculator.PassesDepth(d, W, H, 50, 50, 1 / 4.0));
        Assert.True(MathPlane3DCalculator.PassesDepth(d, W, H, 50, 50, 1 / 5.0));            // on the surface
        Assert.True(MathPlane3DCalculator.PassesDepth(d, W, H, 50, 50, 1 / 5.004));          // within the bias
    }

    [Fact]
    public void ALineBehindAnArrowhead_IsHidden()
    {
        // A fat vector along +x toward the camera hides part of a segment behind it.
        const string seg = "v1; axes off; grid off; segment s 0 -2 0 0 2 0 #D55E00";
        int Orange(MathPlane3DCalculator c) => c.ColorBuffer.Count(x => x == 0xFFD55E00);
        var without = Render(Params(seg, 6, 0, Math.PI / 2));
        var with = Render(Params(seg + "; vector v 3 0 0 #009E73", 6, 0, Math.PI / 2));
        Assert.True(Orange(without) > 50);
        Assert.True(Orange(with) < Orange(without), $"{Orange(with)} vs {Orange(without)}");
    }

    // ── Batch and camera round trips ─────────────────────────────────────

    [Fact]
    public void TheSceneAndCamera_RideTheSnapshot_AndParam()
    {
        var p = Params("v1; grid xz; point P 1 2 3; vector v 1 1 1", 7.5, -1.2, 0.8);
        p.MathPlane3DTargetX = 0.5;
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane3D, p)!;
        var kv = snap.ToKeyValues().ToList();
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(kv, FractalType.MathPlane3D, out var err)!.ApplyTo(fresh);
        Assert.Null(err);
        Assert.Equal(7.5, fresh.MathPlane3DCameraDistance);
        Assert.Equal(-1.2, fresh.MathPlane3DCameraTheta);
        Assert.Equal(0.8, fresh.MathPlane3DCameraPhi);
        Assert.Equal(0.5, fresh.MathPlane3DTargetX);
        Assert.True(MathPlane3DScene.TryParse(fresh.MathPlane3DScene, out var s, out _));
        Assert.Equal(2, s.Objects.Count);
        Assert.Equal(Render(p).ColorBuffer, Render(fresh).ColorBuffer);
    }

    [Fact]
    public void TheCommandBuilder_RoundTripsTheView()
    {
        var p = Params("v1; vector v 1 2 0.5; point P -1 1 1", 5, 2.2, 0.9);
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane3D, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane3D, CenterX = 0, CenterY = 0, Zoom = 1.3, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var perr), perr);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane3D, out var ferr)!.ApplyTo(fresh);
        Assert.Null(ferr);
        Assert.Equal(Render(p, 1.3).ColorBuffer, Render(fresh, o.Zoom ?? 1).ColorBuffer);
    }

    [Fact]
    public void TheCameraBinding_ReadsAndWritesTheTriple()
    {
        Assert.True(CameraParamBinding.Supports(FractalType.MathPlane3D, new FractalParameters()));
        var p = new FractalParameters();
        CameraParamBinding.Apply(p, FractalType.MathPlane3D, new CameraState(8, 1.25, 0.75));
        Assert.Equal((8.0, 1.25, 0.75), (p.MathPlane3DCameraDistance, p.MathPlane3DCameraTheta, p.MathPlane3DCameraPhi));
        var back = CameraParamBinding.Read(p, FractalType.MathPlane3D);
        Assert.Equal(8.0, back.Distance);
    }

    [Fact]
    public void ThePosterRenderer_HandsTheParamsToTheCalculator()
    {
        // Batch and export render through PosterRenderer; its calculator must receive the
        // request's params (a missing case there renders the default scene).
        var p = Params("v1; point P 1 2 3");
        var c = FracturingFog.Imaging.PosterRenderer.BuildCaptureCalculator(new FracturingFog.Imaging.PosterRequest
        {
            FractalType = FractalType.MathPlane3D, Width = W, Height = H, Zoom = 1, MaxIterations = 64,
            Quality = QualityPreset.Standard, ColorMap = new HsvPalette(), FractalParameters = p,
        }, W, H);
        var m = Assert.IsType<MathPlane3DCalculator>(c);
        Assert.Same(p, m.FractalParameters);
    }

    [Fact]
    public void RayDepth_IsTheDistanceAlongEachPixelsRay()
    {
        // A screen-filling triangle at view depth 5: the centre ray is 5 long, an off-axis
        // ray 5 / cos(angle), and an empty pixel is the miss value.
        var cam = MathPlane3DCalculator.CameraFor(Params(""), 1, W, H);
        var z = new float[W * H]; var c = new uint[W * H];
        MathPlane3DCalculator.RasterizeAll(new[] { new ScreenTriangle(P(-W, -H, 5), P(3 * W, -H, 5), P(-W, 3 * H, 5), 0xFFFFFFFF) }, c, z, W, H);
        z[0] = 0;   // nothing drawn here
        var d = MathPlane3DCalculator.RayDepth(z, cam, W, H);
        int cx = W / 2, cy = H / 2;
        double ox = (cx + 0.5 - W / 2.0) / cam.Focal, oy = (cy + 0.5 - H / 2.0) / cam.Focal;
        Assert.Equal(5 * Math.Sqrt(1 + ox * ox + oy * oy), d[cy * W + cx], 4);
        double ex = (W - 1 + 0.5 - W / 2.0) / cam.Focal, ey = (H - 1 + 0.5 - H / 2.0) / cam.Focal;
        Assert.Equal(5 * Math.Sqrt(1 + ex * ex + ey * ey), d[(H - 1) * W + W - 1], 3);
        Assert.Equal(float.PositiveInfinity, d[0]);
    }

    [Fact]
    public void AStereoEye_MovesAlongTheCameraRight()
    {
        // The eye moves right, so a point at the target appears to the left of centre by
        // focal * offset / distance (parallel eyes).
        var p = Params("v1; axes off; grid off; point P 0 0 0 #F0E442 nolabel", 6, 0.4, 1.2);
        var calc = new MathPlane3DCalculator(W, H) { Zoom = 1, FractalParameters = p, StereoEyeOffset = 0.3 };
        calc.Calculate(CancellationToken.None);
        var cam = MathPlane3DCalculator.CameraFor(p, 1, W, H);
        double want = W / 2.0 - cam.Focal * 0.3 / 6;
        var px = calc.ColorBuffer.Select((col, i) => (col, i)).Where(t => t.col == 0xFFF0E442).Select(t => t.i % W).ToList();
        Assert.NotEmpty(px);
        Assert.InRange(px.Average(), want - 1, want + 1);
    }

    [Fact]
    public void ABadScene_IsReported_NotThrown()
    {
        var c = Render(Params("point P 1 2"));
        Assert.NotNull(c.LastSceneError);
    }
}
