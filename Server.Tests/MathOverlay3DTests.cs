// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/MathOverlay3DTests.cs
//
// #1734 (Math Plane 3D fractal bridge B4) — the math overlay on 3D fractal views.
//   - Asking a raymarcher for its depth publishes it and leaves the image and the user's
//     Lighting untouched.
//   - The depth test: a point beyond the frame's depth at its pixel is hidden, nearer is shown,
//     sky never hides; a synthetic depth hides exactly the part of a segment behind it.
//   - Drawing: the axes cross where the B3 camera projects the origin; an all-hiding depth
//     dims what is drawn; the bailout sphere's outline is its true silhouette.
//   - Picking returns a point on the fractal's surface (its DE ~ 0) on a real render.
//   - Read-out, the spec's text form, and the Command builder's gap.

using System;
using System.Linq;
using System.Reflection;
using FracturingFog;
using FracturingFog.Cli;
using FracturingFog.Export;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathOverlay3DTests
{
    private const int W = 96, H = 64;

    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] c, int w, int h) { }
        public void Render() { }
        public void Resize(int w, int h) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static IFractalCalculator Calc(FractalRenderHost host, FractalType t)
        => (IFractalCalculator)typeof(FractalRenderHost).GetMethod("SelectAltCalculatorByType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, new object[] { t })!;

    private static FractalParameters Render(IFractalCalculator c, bool depth)
    {
        c.Resize(W, H); c.CenterX = 0; c.CenterY = 0; c.Zoom = 1; c.MaxIterations = 64; c.ColorMap = ColorPalette.BuiltIns[0];
        var fp = new FractalParameters { Lighting = LightingFxData.CreateDefault(), UserBulbSource = "z^8 + c" };
        c.GetType().GetProperty("FractalParameters")!.SetValue(c, fp);
        ((IDepthAovSource)c).DepthRequested = depth;
        c.Calculate(default);
        return fp;
    }

    // ── Depth on request ──

    [Fact]
    public void ARequestedDepth_IsPublished_AndChangesNeitherTheImageNorTheUsersLighting()
    {
        using var host = new FractalRenderHost(new NullRenderer(), new FractalViewState(), W, H, ColorPalette.BuiltIns[0]);
        foreach (var t in MeshExportCapabilities.Types.Append(FractalType.UserBulb))
        {
            var c = Calc(host, t);
            Render(c, depth: false);
            Assert.Null(((IDepthAovSource)c).DepthBuffer);
            var mono = (uint[])c.ColorBuffer.Clone();
            var fp = Render(c, depth: true);
            Assert.True(((IDepthAovSource)c).DepthBuffer is { } d && d.Length == W * H, $"{t}: no depth on request");
            Assert.True(mono.SequenceEqual(c.ColorBuffer), $"{t}: the depth request changed the image");
            Assert.False(fp.Lighting.OverlayDepth);   // the calculator set it on its own copy only
        }
    }

    [Fact]
    public void OverlayDepth_IsNotSaved_WithLightingPresets()
    {
        var fx = LightingFxData.CreateDefault();
        fx.OverlayDepth = true;
        var back = LightingFxPresetData.FromFx(in fx).ToFx();
        Assert.False(back.OverlayDepth);
        Assert.True(ScreenSpacePost.WantsDepthOutput(in fx));
        Assert.False(ScreenSpacePost.WantsDepthOutput(LightingFxData.CreateDefault()));
    }

    // ── The depth test ──

    private static Raymarch3DCamera Camera() => new((0, 0, 6), (0, 0, -1), (1, 0, 0), (0, 1, 0), Math.Tan(Math.PI / 6), 0, 0, W, H);

    [Fact]
    public void APoint_IsHidden_ExactlyWhenItIsBeyondTheDepthAtItsPixel()
    {
        var cam = Camera();
        var depth = Enumerable.Repeat(5f, W * H).ToArray();
        var near = cam.PointAt(40.5, 30.5, 4.0);
        var far = cam.PointAt(40.5, 30.5, 6.0);
        Assert.False(MathOverlay3DRenderer.IsHidden(cam, depth, near));
        Assert.True(MathOverlay3DRenderer.IsHidden(cam, depth, far));
        Assert.False(MathOverlay3DRenderer.IsHidden(cam, null, far));   // no depth: nothing hidden
        depth[30 * W + 40] = float.PositiveInfinity;                    // sky never hides
        Assert.False(MathOverlay3DRenderer.IsHidden(cam, depth, far));
        Assert.False(MathOverlay3DRenderer.IsHidden(cam, depth, (0, 0, 10)));   // behind the eye
    }

    [Fact]
    public void ASyntheticWall_HidesExactlyTheSegmentBehindIt()
    {
        // A wall at ray distance 5 everywhere; a segment from distance 3 to 7 along the central ray
        // is hidden from distance 5 (+ the bias) on.
        var cam = Camera();
        var depth = Enumerable.Repeat(5f, W * H).ToArray();
        var a = cam.PointAt(48, 32, 3.0); var b = cam.PointAt(48, 32, 7.0);
        for (int k = 0; k <= 40; k++)
        {
            double t = k / 40.0, dist = 3.0 + 4.0 * t;
            var p = (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
            bool expect = dist > 5.0 + 1e-3 + 0.01 * 5.0;
            Assert.Equal(expect, MathOverlay3DRenderer.IsHidden(cam, depth, p));
        }
    }

    // ── Drawing ──

    private static int LitAt(uint[] px, int x, int y, int r = 1)
    {
        int n = 0;
        for (int j = y - r; j <= y + r; j++)
            for (int i = x - r; i <= x + r; i++)
                if (i >= 0 && j >= 0 && i < W && j < H && (px[j * W + i] & 0xFFFFFF) != 0) n++;
        return n;
    }

    [Fact]
    public void TheAxesCross_WhereTheCameraProjectsTheOrigin()
    {
        var cam = new Raymarch3DCamera((3, 2, 4), Norm((-3, -2, -4)), Norm((4, 0, -3)), (0, 0, 0), Math.Tan(Math.PI / 6), 0.05, -0.03, W, H);
        cam = cam with { Up = Cross(cam.Right, cam.Forward) };
        var px = new uint[W * H];
        Array.Fill(px, 0xFF000000u);
        MathOverlay3DRenderer.Draw(px, cam, null, FractalType.Mandelbulb, new FractalParameters(), null, false);
        var o = cam.Project((0, 0, 0))!.Value;
        Assert.True(LitAt(px, (int)o.X, (int)o.Y) >= 3, "nothing drawn at the projected origin");
        // A point far off the axes stays black.
        Assert.Equal(0, LitAt(px, 2, 2, 0));
    }

    [Fact]
    public void AnAllHidingDepth_DimsWhatIsDrawn()
    {
        // Hidden pieces are drawn at alpha HiddenAlpha, so a hidden line can never reach the channel
        // value a shown one does. Single lines only: the axes of a type with no orbit or sphere and no
        // point, away from their crossing, the labels and the read-out (which are never dimmed);
        // overlapping translucent layers would compound.
        const int Wb = 480, Hb = 320;
        var cam = Camera() with { Width = Wb, Height = Hb };
        var p = new FractalParameters();
        const FractalType type = FractalType.Kleinian;
        double ext = MathOverlay3DRenderer.Extent(type, p);
        var o = cam.Project((0, 0, 0))!.Value;
        var labels = new[] { (ext, 0.0, 0.0), (0.0, ext, 0.0), (0.0, 0.0, ext) }
            .Select(q => cam.Project(q)).Where(q => q != null).Select(q => q!.Value).ToArray();
        int Peak(uint[] a)
        {
            int best = 0;
            for (int y = 0; y < Hb - 40; y++)
                for (int x = 0; x < Wb; x++)
                {
                    if (Math.Abs(x - o.X) < 12 && Math.Abs(y - o.Y) < 12) continue;
                    if (labels.Any(l => x >= l.X - 2 && x <= l.X + 22 && y >= l.Y - 22 && y <= l.Y + 4)) continue;
                    uint v = a[y * Wb + x];
                    best = Math.Max(best, (int)Math.Max((v >> 16) & 0xFF, Math.Max((v >> 8) & 0xFF, v & 0xFF)));
                }
            return best;
        }
        var plain = new uint[Wb * Hb]; Array.Fill(plain, 0xFF000000u);
        MathOverlay3DRenderer.Draw(plain, cam, Enumerable.Repeat(float.PositiveInfinity, Wb * Hb).ToArray(), type, p, null, false);
        var hidden = new uint[Wb * Hb]; Array.Fill(hidden, 0xFF000000u);
        MathOverlay3DRenderer.Draw(hidden, cam, Enumerable.Repeat(0.01f, Wb * Hb).ToArray(), type, p, null, false);
        int a = Peak(plain), b = Peak(hidden);
        Assert.True(a > 150 && b > 0, $"nothing drawn ({a}, {b})");
        Assert.True(b <= MathOverlay3DRenderer.HiddenAlpha / 255.0 * a + 6, $"hidden peak {b} vs shown {a}");
        // And dashed: a hidden line covers clearly fewer pixels (dash 5 on, 4 off). Skia restarts a
        // dash at every contour, so drawing each two-pixel piece as its own contour came out solid.
        int Lit(uint[] px)
        {
            int n = 0;
            for (int y = 0; y < Hb - 40; y++)
                for (int x = 0; x < Wb; x++)
                {
                    if (Math.Abs(x - o.X) < 12 && Math.Abs(y - o.Y) < 12) continue;
                    if (labels.Any(l => x >= l.X - 2 && x <= l.X + 22 && y >= l.Y - 22 && y <= l.Y + 4)) continue;
                    uint v = px[y * Wb + x];
                    if (Math.Max((v >> 16) & 0xFF, Math.Max((v >> 8) & 0xFF, v & 0xFF)) > 60) n++;
                }
            return n;
        }
        Assert.True(Lit(hidden) < 0.8 * Lit(plain), $"hidden lines are not dashed ({Lit(hidden)} vs {Lit(plain)} lit pixels)");
    }

    [Fact]
    public void TheSphereOutline_IsItsSilhouette()
    {
        var eye = (1.0, 2.0, 5.5);
        const double r = 2.0;
        var ring = MathOverlay3DRenderer.Silhouette(eye, r)!;
        foreach (var q in ring)
        {
            Assert.Equal(r, Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z), 9);   // on the sphere
            // The line of sight touches the sphere there: (q - eye) . q = 0.
            Assert.Equal(0.0, (q.X - eye.Item1) * q.X + (q.Y - eye.Item2) * q.Y + (q.Z - eye.Item3) * q.Z, 9);
        }
        Assert.Null(MathOverlay3DRenderer.Silhouette((0.5, 0, 0), r));   // eye inside
    }

    // ── Picking ──

    [Theory]
    [InlineData(FractalType.Mandelbulb)]
    [InlineData(FractalType.Mandelbox)]
    [InlineData(FractalType.Kifs)]
    public void APick_LandsOnTheFractalsSurface(FractalType type)
    {
        using var host = new FractalRenderHost(new NullRenderer(), new FractalViewState(), W, H, ColorPalette.BuiltIns[0]);
        var c = Calc(host, type);
        var fp = Render(c, depth: true);
        var cam = ((IRaymarch3DCameraSource)c).LastCamera!.Value;
        var depth = ((IDepthAovSource)c).DepthBuffer!;
        var de = RaymarchMeshSampler.For(type, fp)!;
        int picked = 0;
        for (int y = 2; y < H; y += 7)
            for (int x = 2; x < W; x += 7)
                if (MathOverlay3DRenderer.Pick(cam, depth, x + 0.3, y + 0.7) is { } p)
                {
                    picked++;
                    Assert.True(Math.Abs(de.Evaluate(p.X, p.Y, p.Z)) <= 2e-3, $"{type}: picked point off the surface");
                }
                else Assert.False(float.IsFinite(depth[y * W + x]) && depth[y * W + x] > 0);   // only the sky picks nothing
        Assert.True(picked > 5, $"{type}: picked {picked}");
        Assert.Null(MathOverlay3DRenderer.Pick(cam, null, 10, 10));
        Assert.Null(MathOverlay3DRenderer.Pick(cam, depth, -1, 10));
    }

    // ── Read-out, spec, Command builder ──

    [Fact]
    public void TheReadout_SaysWhatTheOrbitDoes()
    {
        var p = new FractalParameters();
        var esc = MathPlane3DOrbit.Run(FractalType.Mandelbulb, p, (0.6, 0.4, 0.5));
        Assert.Contains("escapes at step", MathOverlay3DRenderer.Readout(FractalType.Mandelbulb, new MathOverlay3DSpec(0.6, 0.4, 0.5), esc, true));
        var still = MathPlane3DOrbit.Run(FractalType.Mandelbulb, p, (0, 0, 0));
        Assert.Contains("bounded for", MathOverlay3DRenderer.Readout(FractalType.Mandelbulb, new MathOverlay3DSpec(0, 0, 0), still, false));
        Assert.Contains("(w dropped)", MathOverlay3DRenderer.Readout(FractalType.QuaternionJulia, new MathOverlay3DSpec(0, 0, 0),
            MathPlane3DOrbit.Run(FractalType.QuaternionJulia, p, (0, 0, 0)), false));
        Assert.Contains("no orbit for Kleinian", MathOverlay3DRenderer.Readout(FractalType.Kleinian, new MathOverlay3DSpec(1, 2, 3), null, true));
        Assert.StartsWith("Point at the fractal", MathOverlay3DRenderer.Readout(FractalType.Mandelbulb, null, null, false));
    }

    [Theory]
    [InlineData("1,2,3", 1.0, 2.0, 3.0, 50)]
    [InlineData(" -0.5 , 0.25 ,1e-3, 12 ", -0.5, 0.25, 0.001, 12)]
    public void TheSpec_ParsesAndRoundTrips(string text, double x, double y, double z, int steps)
    {
        Assert.True(MathOverlay3DSpec.TryParse(text, out var s, out var err), err);
        Assert.Equal(new MathOverlay3DSpec(x, y, z, steps), s);
        Assert.True(MathOverlay3DSpec.TryParse(s.ToFlagValue(), out var back, out _));
        Assert.Equal(s, back);
    }

    [Theory]
    [InlineData("1,2")]
    [InlineData("1,2,x")]
    [InlineData("1,2,3,0")]
    [InlineData("1,2,NaN")]
    public void BadSpecs_AreRefused(string text) => Assert.False(MathOverlay3DSpec.TryParse(text, out _, out _));

    [Fact]
    public void APinned3DOverlay_IsReportedAsAGap_UntilBatchDrawsIt()
    {
        var with = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { Fractal = FractalType.Mandelbulb, MathOverlay3D = new MathOverlay3DSpec(0.1, 0.2, 0.3) });
        Assert.Contains(with, g => g.Contains("3D math overlay") && g.Contains("0.1,0.2,0.3") && g.Contains("#1735"));
        var without = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { Fractal = FractalType.Mandelbulb });
        Assert.DoesNotContain(without, g => g.Contains("3D math overlay"));
    }

    private static (double X, double Y, double Z) Norm((double X, double Y, double Z) v)
    { double l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z); return (v.X / l, v.Y / l, v.Z / l); }
    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) u, (double X, double Y, double Z) v)
        => (u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X);
}
