// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1714 (Math Plane 3D T2, #1556) — surfaces, space curves, shading and the translucent
// pass. Invariants checked independently of the renderer's own arithmetic: the blend
// against a hand-computed "over", back-to-front order against the input order, a pole
// cut (no drawn cell straddles it), the silhouette across mesh resolutions, a line lying
// on a surface staying visible (the depth bias), near-plane clipping, and the batch
// round trip of the new statements.

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

public sealed class MathPlane3DSurfaceTests
{
    private const int W = 320, H = 200;
    private const string Bare = "v1; axes off; grid off; ";

    private static FractalParameters Params(string scene, double dist = 6, double theta = 0.7, double phi = 1.1) => new()
    {
        MathPlane3DScene = scene,
        MathPlane3DCameraDistance = dist, MathPlane3DCameraTheta = theta, MathPlane3DCameraPhi = phi,
    };

    private static MathPlane3DCalculator Render(FractalParameters p, double zoom = 1)
    {
        var calc = new MathPlane3DCalculator(W, H) { Zoom = zoom, FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        Assert.Null(calc.LastSceneError);
        return calc;
    }

    private static int Ch(uint c, int sh) => (int)((c >> sh) & 0xFF);

    // Straight-alpha "over" written out per channel, independently of the renderer's Blend.
    private static double Over(double dst, double src, double a) => src * a / 255.0 + dst * (1 - a / 255.0);

    private static Projected3D P(double x, double y, double z) => new(x, y, 1 / z);

    // ── Scene text ───────────────────────────────────────────────────────

    [Fact]
    public void TheNewStatements_RoundTrip()
    {
        const string text = "v1; shading flat; fill xy yz; "
            + "surface s explicit \"sin(x)*cos(y)\" -2 2 -1 3 #56B4E9 mesh 32; "
            + "surface d explicit \"x*y\"; "
            + "surface sph parametric \"cos(u)*sin(v)\" \"sin(u)*sin(v)\" \"cos(v)\" 0 6.3 0 3.2 #E69F0080 nolabel; "
            + "curve h \"cos(t)\" \"sin(t)\" \"t/4\" 0 12 #F0E442 hidden";
        Assert.True(MathPlane3DScene.TryParse(text, out var scene, out var err), err?.ToString());
        Assert.True(scene.FlatShading);
        Assert.Equal(MathPlane3DGrid.XY | MathPlane3DGrid.YZ, scene.Fill);
        var s = scene.Objects[0];
        Assert.Equal(MathPlaneObjectKind.Surface, s.Kind);
        Assert.Equal(32, MathPlane3DScene.MeshOf(s));
        Assert.Equal((-2.0, 2.0, -1.0, 3.0), MathPlane3DScene.SurfaceDomain(s));
        var d = scene.Objects[1];
        Assert.Equal(MathPlane3DScene.DefaultMesh, MathPlane3DScene.MeshOf(d));
        Assert.Equal((-3.0, 3.0, -3.0, 3.0), MathPlane3DScene.SurfaceDomain(d));
        Assert.Equal(MathPlaneCurveKind.Parametric, scene.Objects[2].CurveKind);
        Assert.Equal(3, scene.Objects[2].Expressions.Count);
        Assert.Equal(0x80E69F00u, scene.Objects[2].Color);
        Assert.Equal(MathPlaneObjectKind.Curve, scene.Objects[3].Kind);
        Assert.Equal((0.0, 12.0), scene.Objects[3].Domain);
        Assert.True(MathPlane3DScene.TryParse(scene.Serialize(), out var again, out var err2), err2?.ToString());
        Assert.Equal(scene, again);
    }

    [Theory]
    [InlineData("surface s explicit \"x + z\"", "reads only x, y")]
    [InlineData("surface s parametric \"u\" \"v\" \"x\" 0 1 0 1", "reads only u, v")]
    [InlineData("surface s parametric \"u\" \"v\" \"u*v\"", "umin umax vmin vmax")]
    [InlineData("surface s parametric \"u\" \"v\" 0 1 0 1", "three quoted expressions")]
    [InlineData("surface s explicit \"x\" -1 1 2", "xmin xmax ymin ymax")]
    [InlineData("surface s explicit \"x\" 1 -1 0 1", "max must be above")]
    [InlineData("surface s conic \"x\"", "explicit or parametric")]
    [InlineData("surface s explicit \"x\" mesh 1", "2-256")]
    [InlineData("surface s explicit \"x\" mesh 300", "2-256")]
    [InlineData("point P 0 0 0 mesh 4", "unknown option 'mesh'")]
    [InlineData("curve c \"t\" \"t\" \"t\"", "tmin tmax")]
    [InlineData("curve c \"t\" \"x\" \"t\" 0 1", "reads only t")]
    [InlineData("shading glossy", "smooth or flat")]
    [InlineData("fill xw", "'fill' takes")]
    public void BadSurfaceStatements_AreErrors(string text, string expect)
    {
        Assert.False(MathPlane3DScene.TryParse(text, out _, out var err));
        Assert.Contains(expect, err!.Message);
    }

    [Fact]
    public void UAndV_AreSurfaceOnly_SoThe2DLanguageKeepsThemFree()
    {
        Assert.True(PlotExpression.TryParse("let u = 2 in u * x", out var e2d, out var err), err);
        Assert.Equal(6.0, e2d!.Eval(x: 3));
        Assert.False(PlotExpression.TryParse("u + v", out _, out _));
        Assert.True(PlotExpression.TryParse("u * 10 + v", surface: true, out var es, out err), err);
        Assert.Equal(new[] { "u", "v" }, es!.UsedVariables());
        Assert.Equal(23.0, es.EvalUV(2, 3));
        Assert.False(PlotExpression.TryParse("let u = 1 in u", surface: true, out _, out _));
    }

    // ── The translucent pass ─────────────────────────────────────────────

    [Fact]
    public void ATranslucentTriangle_BlendsOverTheOpaqueOne_AndHidesBehindIt()
    {
        var color = new uint[W * H];
        var depth = new float[W * H];
        const uint opaque = 0xFF2080C0, glass = 0x99F0E442;
        MathPlane3DCalculator.RasterizeAll(new[] { new ScreenTriangle(P(-W, -H, 5), P(3 * W, -H, 5), P(-W, 3 * H, 5), opaque) }, color, depth, W, H);
        var before = (uint[])color.Clone();
        // Behind the opaque triangle: nothing changes.
        MathPlane3DCalculator.RasterizeBlend(new ScreenTriangle(P(-W, -H, 9), P(3 * W, -H, 9), P(-W, 3 * H, 9), glass), color, depth, W, H);
        Assert.Equal(before, color);
        // In front: "over" per channel, within rounding.
        MathPlane3DCalculator.RasterizeBlend(new ScreenTriangle(P(-W, -H, 2), P(3 * W, -H, 2), P(-W, 3 * H, 2), glass), color, depth, W, H);
        uint got = color[100 * W + 160];
        foreach (int sh in new[] { 16, 8, 0 })
            Assert.InRange(Ch(got, sh), Over(Ch(opaque, sh), Ch(glass, sh), 0x99) - 1, Over(Ch(opaque, sh), Ch(glass, sh), 0x99) + 1);
        // The blend never writes depth.
        Assert.All(depth, z => Assert.Equal(1f / 5f, z));
    }

    [Fact]
    public void Blend_IsExactAtTheEnds_AndSymmetricInTheMiddle()
    {
        var rng = new Random(3);
        for (int k = 0; k < 200; k++)
        {
            uint dst = 0xFF000000u | (uint)rng.Next(0x1000000), rgb = (uint)rng.Next(0x1000000);
            Assert.Equal(dst, MathPlane3DCalculator.Blend(dst, rgb));                       // alpha 0
            Assert.Equal(0xFF000000u | rgb, MathPlane3DCalculator.Blend(dst, 0xFF000000u | rgb)); // alpha 255
        }
        // Half-way between black and white is 128 (127.5 rounded half up).
        Assert.Equal(0xFF808080u, MathPlane3DCalculator.Blend(0xFF000000, 0x80FFFFFF), new ChannelTolerance(1));
    }

    private sealed class ChannelTolerance(int tol) : IEqualityComparer<uint>
    {
        public bool Equals(uint a, uint b) => new[] { 24, 16, 8, 0 }.All(sh => Math.Abs(Ch(a, sh) - Ch(b, sh)) <= tol);
        public int GetHashCode(uint v) => 0;
    }

    [Fact]
    public void SortBackToFront_PutsTheFarthestFirst_WhateverTheInputOrder()
    {
        var rng = new Random(7);
        var tris = Enumerable.Range(0, 30).Select(k =>
        {
            double z = 1 + rng.NextDouble() * 20;
            return new ScreenTriangle(P(0, 0, z), P(10, 0, z + 1), P(0, 10, z + 2), (uint)k);
        }).ToList();
        var sorted = MathPlane3DCalculator.SortBackToFront(tris);
        var reversed = MathPlane3DCalculator.SortBackToFront(Enumerable.Reverse(tris));
        Assert.Equal(sorted, reversed);
        for (int i = 1; i < sorted.Count; i++)
        {
            double Mean(ScreenTriangle t) => (1 / t.A.InvZ + 1 / t.B.InvZ + 1 / t.C.InvZ) / 3;
            Assert.True(Mean(sorted[i - 1]) >= Mean(sorted[i]));
        }
    }

    // A top-down camera on horizontal planes: every plane has the normal (0, 0, 1), so all get
    // the same Lambert factor, measured from an opaque white plane.
    private static FractalParameters TopDown(string scene) => Params(Bare + "shading flat; " + scene, 8, 0.3, 0.05);
    private const string Domain = " -3 3 -3 3 ";
    private static uint Centre(MathPlane3DCalculator c) => c.ColorBuffer[(H / 2) * W + W / 2];

    [Fact]
    public void ATranslucentPlane_OverAnOpaqueOne_GivesTheExpectedBlend()
    {
        double k = Ch(Centre(Render(TopDown("surface w explicit \"0\"" + Domain + "#FFFFFF nolabel mesh 4"))), 16) / 255.0;
        Assert.InRange(k, 0.3, 1.0);
        const uint floor = 0xFFD55E00, glass = 0x8056B4E9;
        uint got = Centre(Render(TopDown($"surface lo explicit \"0\"{Domain}#D55E00 nolabel mesh 4; surface hi explicit \"1\"{Domain}#56B4E980 nolabel mesh 4")));
        foreach (int sh in new[] { 16, 8, 0 })
        {
            double expect = Over(Math.Round(Ch(floor, sh) * k), Math.Round(Ch(glass, sh) * k), 0x80);
            Assert.InRange(Ch(got, sh), expect - 2, expect + 2);
        }
    }

    [Fact]
    public void TwoStackedTranslucentPlanes_BlendFarThenNear_WhateverTheTextOrder()
    {
        double k = Ch(Centre(Render(TopDown("surface w explicit \"0\"" + Domain + "#FFFFFF nolabel mesh 4"))), 16) / 255.0;
        const string far = "surface a explicit \"0\"" + Domain + "#D55E0080 nolabel mesh 4";
        const string near = "surface b explicit \"1\"" + Domain + "#56B4E9A0 nolabel mesh 4";
        var one = Render(TopDown(far + "; " + near)).ColorBuffer;
        var two = Render(TopDown(near + "; " + far)).ColorBuffer;
        Assert.Equal(one, two);
        uint got = one[(H / 2) * W + W / 2];
        uint bg = MathPlane3DCalculator.BackgroundColor;
        foreach (int sh in new[] { 16, 8, 0 })
        {
            double afterFar = Over(Ch(bg, sh), Math.Round(Ch(0xD55E00u, sh) * k), 0x80);
            double expect = Over(afterFar, Math.Round(Ch(0x56B4E9u, sh) * k), 0xA0);
            Assert.InRange(Ch(got, sh), expect - 2, expect + 2);
        }
    }

    [Fact]
    public void LinesBehindATranslucentSurface_AreTinted_AndInFrontAreNot()
    {
        // A yellow segment below a translucent plane and one above it, seen from the top.
        const string glass = "surface g explicit \"0\"" + Domain + "#0072B2A0 nolabel mesh 4";
        var c = Render(TopDown(glass + "; segment lo -2 -0.5 -1 2 -0.5 -1 #F0E442 nolabel; segment hi -2 0.5 1 2 0.5 1 #F0E442 nolabel"));
        var cam = MathPlane3DCalculator.CameraFor(c.FractalParameters, 1, W, H);
        uint At((double X, double Y, double Z) p)
        {
            var q = cam.Project(p.X, p.Y, p.Z)!.Value;
            return c.ColorBuffer[(int)q.Y * W + (int)q.X];
        }
        uint below = At((0, -0.5, -1)), above = At((0, 0.5, 1));
        Assert.Equal(0xFFF0E442u, above);
        Assert.NotEqual(0xFFF0E442u, below);
        Assert.True(Ch(below, 0) > Ch(0xF0E442u, 0), "the blue tint raises the blue channel of the hidden line");
    }

    // ── Surfaces ─────────────────────────────────────────────────────────

    [Fact]
    public void APole_CutsTheSurface_InsteadOfSpikingIt()
    {
        Assert.True(MathPlane3DScene.TryParse("surface p explicit \"1/(x-0.05)\" -2 2 -2 2 mesh 48", out var scene, out _));
        var m = MathPlane3DCalculator.BuildSurface(scene.Objects[0])!;
        int n = m.N, drawn = 0;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                if (!m.CellOk[j * n + i]) continue;
                drawn++;
                double x0 = m.At(i, j).X, x1 = m.At(i + 1, j).X;
                Assert.False(x0 < 0.05 && x1 > 0.05, $"cell {i},{j} straddles the pole");
                foreach (var q in new[] { m.At(i, j), m.At(i + 1, j), m.At(i, j + 1), m.At(i + 1, j + 1) })
                    Assert.True(double.IsFinite(q.Z));
            }
        Assert.True(drawn >= n * (n - 3), $"only the cells at the pole are cut ({drawn} of {n * n} drawn)");
        // A smooth surface loses nothing.
        Assert.True(MathPlane3DScene.TryParse("surface q explicit \"x^2 - y^2\" mesh 16", out var smooth, out _));
        Assert.All(MathPlane3DCalculator.BuildSurface(smooth.Objects[0])!.CellOk, Assert.True);
    }

    [Fact]
    public void NonFiniteSamples_DropTheirCells()
    {
        Assert.True(MathPlane3DScene.TryParse("surface r explicit \"sqrt(1 - x^2 - y^2)\" -1.2 1.2 -1.2 1.2 mesh 24", out var scene, out _));
        var m = MathPlane3DCalculator.BuildSurface(scene.Objects[0])!;
        for (int j = 0; j < m.N; j++)
            for (int i = 0; i < m.N; i++)
            {
                var r2 = new[] { m.At(i, j), m.At(i + 1, j), m.At(i, j + 1), m.At(i + 1, j + 1) }
                    .Select(q => q.X * q.X + q.Y * q.Y).ToArray();
                // Samples on the circle itself are decided by round-off; skip those cells.
                if (r2.All(r => r < 1 - 1e-9)) Assert.True(m.CellOk[j * m.N + i], $"inside cell {i},{j}");
                else if (r2.Any(r => r > 1 + 1e-9)) Assert.False(m.CellOk[j * m.N + i], $"outside cell {i},{j}");
            }
    }

    [Fact]
    public void AParametricSurface_SamplesItsFormula()
    {
        Assert.True(MathPlane3DScene.TryParse("surface s parametric \"2*cos(u)*sin(v)\" \"2*sin(u)*sin(v)\" \"2*cos(v)\" 0 6.2832 0 3.1416 mesh 12", out var scene, out _));
        var m = MathPlane3DCalculator.BuildSurface(scene.Objects[0])!;
        Assert.All(m.Vertices, q => Assert.Equal(2.0, Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z), 9));
        Assert.All(m.CellOk, Assert.True);
    }

    [Fact]
    public void AMeshResolutionChange_KeepsTheSilhouetteWithinOnePixel()
    {
        static bool[] Mask(int mesh)
        {
            var c = Render(Params(Bare + $"surface s explicit \"0.25*x^2 - 0.3*y\" #E69F00 nolabel mesh {mesh}", 9, 0.8, 1.0));
            return c.ColorBuffer.Select(p => p != MathPlane3DCalculator.BackgroundColor).ToArray();
        }
        var coarse = Mask(24);
        var fine = Mask(128);
        Assert.True(fine.Count(x => x) > W * H / 10, "the surface is on screen");
        bool Near(bool[] m, int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx >= 0 && yy >= 0 && xx < W && yy < H && m[yy * W + xx]) return true;
                }
            return false;
        }
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (coarse[y * W + x] && !fine[y * W + x]) Assert.True(Near(fine, x, y), $"coarse-only pixel {x},{y}");
                if (fine[y * W + x] && !coarse[y * W + x]) Assert.True(Near(coarse, x, y), $"fine-only pixel {x},{y}");
            }
    }

    [Fact]
    public void ALineLyingOnASurface_StaysVisible()
    {
        const string curve = "curve c \"t\" \"0.5\" \"0.3*t\" -2 2 #F0E442 nolabel";
        static int Yellow(MathPlane3DCalculator c) => c.ColorBuffer.Count(p => Ch(p, 16) > 200 && Ch(p, 8) > 200 && Ch(p, 0) < 120);
        int alone = Yellow(Render(Params(Bare + curve, 7, 0.9, 0.9)));
        int onSurface = Yellow(Render(Params(Bare + "surface s explicit \"0.3*x\" #404040 nolabel; " + curve, 7, 0.9, 0.9)));
        Assert.True(alone > 100);
        Assert.True(onSurface > 0.85 * alone, $"{onSurface} of {alone} curve pixels survive on the surface");
    }

    [Fact]
    public void GouraudInterpolatesTheVertexColours_FlatDoesNot()
    {
        var color = new uint[W * H];
        var depth = new float[W * H];
        var t = new ScreenTriangle(P(10, 10, 3), P(310, 10, 3), P(10, 190, 3), 0xFFFF0000, 0xFF00FF00, 0xFF0000FF);
        MathPlane3DCalculator.Rasterize(t, color, depth, W, H);
        // The centroid gets a third of each.
        uint c = color[70 * W + 110];
        foreach (int sh in new[] { 16, 8, 0 }) Assert.InRange(Ch(c, sh), 85 - 3, 85 + 3);
        // Near a vertex, mostly that vertex's colour.
        Assert.True(Ch(color[12 * W + 12], 16) > 240);

        static int Shades(string shading) =>
            Render(Params(Bare + $"shading {shading}; surface s parametric \"cos(u)*sin(v)\" \"sin(u)*sin(v)\" \"cos(v)\" 0 6.2832 0 3.1416 #FFFFFF nolabel mesh 12", 4))
                .ColorBuffer.Distinct().Count();
        Assert.True(Shades("smooth") > 2 * Shades("flat"));
    }

    [Fact]
    public void TrianglesCrossingTheNearPlane_AreClipped_NotDropped()
    {
        // One huge cell pair under a camera close to the ground: both triangles reach behind
        // the camera, and every pixel of the bottom row must still see the ground.
        var c = Render(Params(Bare + "surface g explicit \"0\" -50 50 -50 50 #FFFFFF nolabel mesh 2", 3, 0.7, 1.2));
        for (int x = 0; x < W; x++) Assert.NotEqual(MathPlane3DCalculator.BackgroundColor, c.ColorBuffer[(H - 1) * W + x]);
    }

    [Fact]
    public void AFilledGridPlane_TintsTheBackground()
    {
        var c = Render(TopDown("fill xy"));
        Assert.Equal(MathPlane3DCalculator.Blend(MathPlane3DCalculator.BackgroundColor, MathPlane3DCalculator.GridFillColor), Centre(c));
        Assert.Equal(MathPlane3DCalculator.BackgroundColor, Centre(Render(TopDown("fill off"))));
    }

    // ── Space curves ─────────────────────────────────────────────────────

    [Fact]
    public void ASpaceCurve_SamplesItsFormula_AndBreaksAtGaps()
    {
        Assert.True(MathPlane3DScene.TryParse("curve h \"2*cos(t)\" \"2*sin(t)\" \"t/3\" 0 12", out var scene, out _));
        var pieces = MathPlane3DCalculator.SampleCurve(scene.Objects[0]);
        Assert.Single(pieces);
        Assert.Equal(MathPlane3DCalculator.CurveSegments + 1, pieces[0].Count);
        Assert.All(pieces[0], q => Assert.Equal(2.0, Math.Sqrt(q.X * q.X + q.Y * q.Y), 9));
        Assert.Equal(4.0, pieces[0][^1].Z, 9);

        Assert.True(MathPlane3DScene.TryParse("curve g \"t\" \"sqrt(1 - t^2)\" \"0\" -2 2", out var gap, out _));
        var g = MathPlane3DCalculator.SampleCurve(gap.Objects[0]);
        Assert.Single(g);
        Assert.All(g[0], q => Assert.InRange(q.X, -1.0, 1.0));
    }

    // ── Batch ────────────────────────────────────────────────────────────

    [Fact]
    public void TheCommandBuilder_RoundTripsASurfaceScene()
    {
        var p = Params("v1; fill xy; shading flat; surface s explicit \"sin(x) * cos(y)\" #56B4E9C0 mesh 20; "
                       + "curve h \"cos(t)\" \"sin(t)\" \"t/4\" 0 9", 7, 1.9, 0.8);
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane3D, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane3D, CenterX = 0, CenterY = 0, Zoom = 1.2, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var perr), perr);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane3D, out var ferr)!.ApplyTo(fresh);
        Assert.Null(ferr);
        Assert.True(MathPlane3DScene.TryParse(fresh.MathPlane3DScene, out var back, out _));
        Assert.True(MathPlane3DScene.TryParse(p.MathPlane3DScene, out var orig, out _));
        Assert.Equal(orig, back);
        Assert.Equal(Render(p, 1.2).ColorBuffer, Render(fresh, o.Zoom ?? 1).ColorBuffer);
    }
}
