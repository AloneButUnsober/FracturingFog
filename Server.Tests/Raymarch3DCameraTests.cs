// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/Raymarch3DCameraTests.cs
//
// #1733 (Math Plane 3D fractal bridge B3) — each 3D raymarcher's LastCamera is the camera it
// actually cast its rays with. The check is independent of the camera code: the renderer's own
// published depth (the ray distance where its march hit) is laid along this camera's ray for
// the same pixel, and the point must be on the fractal (its distance estimate within the march's
// hit epsilon). A camera off by a pan, a zoom lens or a stereo eye would put the points off
// the surface. Then the projection round trip and the frame's parameters.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FracturingFog;
using FracturingFog.Export;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class Raymarch3DCameraTests
{
    private const int W = 96, H = 64;
    // The raymarchers stop when the DE is below their hit epsilon (1e-3 .. 1.5e-3 here).
    private const double HitTolerance = 2e-3;

    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] c, int w, int h) { }
        public void Render() { }
        public void Resize(int w, int h) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static IFractalCalculator Calculator(FractalRenderHost host, FractalType t)
        => (IFractalCalculator)typeof(FractalRenderHost).GetMethod("SelectAltCalculatorByType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, new object[] { t })!;

    // The DE raymarchers (their mesh sampler is the render's DE).
    public static IEnumerable<object[]> Cases()
    {
        foreach (var t in MeshExportCapabilities.Types)
            foreach (var c in new[] { "plain", "pan", "lens", "eye" })
                yield return new object[] { t, c };
    }

    private static (Raymarch3DCamera Cam, float[] Depth, FractalParameters P) Render(IFractalCalculator calc, string kase)
    {
        calc.Resize(W, H);
        calc.CenterX = kase == "pan" ? 0.15 : 0; calc.CenterY = kase == "pan" ? -0.1 : 0;
        calc.Zoom = kase == "lens" ? 3.0 : 1.0;
        calc.MaxIterations = 64;
        calc.ColorMap = ColorPalette.BuiltIns[0];
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = StereoMode.Fake; fx.StereoEyeSeparation = 0.1;   // Fake stereo publishes depth
        var fp = new FractalParameters { Lighting = fx, UserBulbSource = "z^8 + c" };
        calc.GetType().GetProperty("FractalParameters")!.SetValue(calc, fp);
        ((IStereoEyeCamera)calc).StereoEyeOffset = kase == "eye" ? 0.08 : 0;
        calc.Calculate(default);
        var cam = ((IRaymarch3DCameraSource)calc).LastCamera;
        Assert.True(cam.HasValue, "no camera");
        var depth = ((IDepthAovSource)calc).DepthBuffer;
        Assert.NotNull(depth);
        return (cam!.Value, depth!, fp);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheRenderersOwnHits_LieOnTheFractal_AlongThisCamerasRays(FractalType type, string kase)
    {
        using var host = new FractalRenderHost(new NullRenderer(), new FractalViewState(), W, H, ColorPalette.BuiltIns[0]);
        var (cam, depth, fp) = Render(Calculator(host, type), kase);
        var de = RaymarchMeshSampler.For(type, fp)!;
        int hits = 0;
        double worst = 0, worstRoundTrip = 0;
        for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++)
            {
                float d = depth[j * W + i];
                if (!float.IsFinite(d) || d <= 0 || d > 1e6) continue;   // sky
                hits++;
                var p = cam.PointAt(i + 0.5, j + 0.5, d);
                worst = Math.Max(worst, Math.Abs(de.Evaluate(p.X, p.Y, p.Z)));
                var q = cam.Project(p)!.Value;
                worstRoundTrip = Math.Max(worstRoundTrip, Math.Max(Math.Abs(q.X - (i + 0.5)), Math.Abs(q.Y - (j + 0.5))));
                Assert.Equal(d, q.Distance, 3);
            }
        Assert.True(hits >= 50, $"{type} {kase}: only {hits} hits");
        Assert.True(worst <= HitTolerance, $"{type} {kase}: a hit point is {worst} from the surface");
        Assert.True(worstRoundTrip < 1e-6, $"{type} {kase}: projection round trip off by {worstRoundTrip} px");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheCamera_CarriesThisFramesPanLensEyeAndSize(FractalType type, string kase)
    {
        using var host = new FractalRenderHost(new NullRenderer(), new FractalViewState(), W, H, ColorPalette.BuiltIns[0]);
        var calc = Calculator(host, type);
        var (plain, _, _) = Render(calc, "plain");
        var (cam, _, _) = Render(calc, kase);
        Assert.Equal((W, H), (cam.Width, cam.Height));
        double baseFov = Math.Tan(0.5 * Math.PI / 3.0);   // 60 degrees
        Assert.Equal(baseFov, plain.FovScale, 12);
        switch (kase)
        {
            case "pan":
                Assert.Equal((0.15, 0.1), (cam.PanU, cam.PanV));
                break;
            case "lens":
                if (type == FractalType.Mandelbulb)
                {
                    // The Mandelbulb has no zoom lens: zooming moves the eye in (distance / zoom).
                    Assert.Equal(baseFov, cam.FovScale, 12);
                    Assert.Equal(Len(plain.Eye) / 3.0, Len(cam.Eye), 9);
                }
                else   // zoomed inside the distance clamp: the eye stays put and the field of view narrows
                    Assert.True(cam.FovScale < baseFov, $"{type}: zoom lens not engaged ({cam.FovScale})");
                break;
            case "eye":
                var e = (cam.Eye.X - plain.Eye.X, cam.Eye.Y - plain.Eye.Y, cam.Eye.Z - plain.Eye.Z);
                Assert.Equal(0.08 * plain.Right.X, e.Item1, 9);
                Assert.Equal(0.08 * plain.Right.Y, e.Item2, 9);
                Assert.Equal(0.08 * plain.Right.Z, e.Item3, 9);
                break;
        }
        // The basis is orthonormal (the projection relies on it).
        static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        Assert.Equal(1.0, Dot(cam.Forward, cam.Forward), 9);
        Assert.Equal(1.0, Dot(cam.Right, cam.Right), 9);
        Assert.Equal(1.0, Dot(cam.Up, cam.Up), 9);
        Assert.Equal(0.0, Dot(cam.Forward, cam.Right), 9);
        Assert.Equal(0.0, Dot(cam.Forward, cam.Up), 9);
        Assert.Equal(0.0, Dot(cam.Right, cam.Up), 9);
    }

    private static double Len((double X, double Y, double Z) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    [Fact]
    public void EveryDeRaymarcher_ExposesItsCamera()
    {
        using var host = new FractalRenderHost(new NullRenderer(), new FractalViewState(), W, H, ColorPalette.BuiltIns[0]);
        var with = Enum.GetValues<FractalType>().Where(FractalViewState.IsThreeD)
            .Where(t => Calculator(host, t) is IRaymarch3DCameraSource).ToHashSet();
        foreach (var t in MeshExportCapabilities.Types) Assert.Contains(t, with);
        Assert.Contains(FractalType.UserBulb, with);
        // Not raymarchers of this kind: Math Plane 3D has its own camera; Mirror Spheres is ray traced.
        Assert.DoesNotContain(FractalType.MathPlane3D, with);
    }

    [Fact]
    public void UserBulb_HasItsCamera_AndItRoundTrips()
    {
        using var host = new FractalRenderHost(new NullRenderer(), new FractalViewState(), W, H, ColorPalette.BuiltIns[0]);
        var (cam, _, _) = Render(Calculator(host, FractalType.UserBulb), "plain");
        for (double x = 0.5; x < W; x += 7.3)
            for (double y = 0.5; y < H; y += 5.1)
            {
                var q = cam.Project(cam.PointAt(x, y, 3.7))!.Value;
                Assert.Equal(x, q.X, 9); Assert.Equal(y, q.Y, 9); Assert.Equal(3.7, q.Distance, 9);
            }
        Assert.Null(cam.Project((cam.Eye.X - cam.Forward.X, cam.Eye.Y - cam.Forward.Y, cam.Eye.Z - cam.Forward.Z)));   // behind the eye
    }
}
