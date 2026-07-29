using System;
using Xunit;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Server.Tests;

// #102 Phase 2 — oblique heightfield raymarch. These lock in that the render
// produces a real 3D view (surface hits + ray-miss sky = a silhouette) and
// that the shared volumetric fog stack (LightingFxData) reaches the 2D fractal.
public class Relief2DRaymarchTests
{
    private static (uint[] albedo, float[] height) Mandelbrot(int w, int h)
    {
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, MaxIterations = 400,
            ColorMap = new MonoBandMap(),
        };
        calc.Calculate(default);
        return ((uint[])calc.ColorBuffer.Clone(), (float[])calc.SmoothBuffer.Clone());
    }

    [Fact]
    public void Raymarch_Produces_Surface_And_Sky_Silhouette()
    {
        int w = 320, h = 240;
        var (albedo, height) = Mandelbrot(w, h);

        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            // Isolate the terrain-vs-sky silhouette: the #132 ground plane would
            // otherwise fill the ray-miss region with floor. Frame-fill (#128)
            // legitimately raises terrain coverage, so the upper bound is generous.
            Relief2DGroundPlane = false,
        };
        var dst = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, p, dst, out double hitFrac);

        // A genuine 3D view has BOTH a lit surface and ray-miss sky.
        Assert.InRange(hitFrac, 0.10, 0.97);
    }

    [Fact]
    public void Volumetric_Fog_Alters_The_Image()
    {
        int w = 320, h = 240;
        var (albedo, height) = Mandelbrot(w, h);

        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraElevationDeg = 45,
        };
        var noFog = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, p, noFog);

        var pf = p.Clone();
        var fx = pf.Lighting;
        fx.FogDensity = 0.9;
        fx.VolumeSteps = 24;
        pf.Lighting = fx;
        var fog = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, pf, fog);

        int changed = 0;
        for (int i = 0; i < w * h; i++) if (fog[i] != noFog[i]) changed++;
        Assert.True(changed > w * h / 20,
            $"volumetric fog changed too few pixels: {changed} of {w * h}");
    }

    [Fact]
    public void Isolate_Culls_Background_And_Writes_Transparent()
    {
        int w = 320, h = 240;
        var (albedo, height) = Mandelbrot(w, h);

        var baseP = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraElevationDeg = 50,
            Relief2DGroundPlane = false,
        };
        var full = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, baseP, full, out double fullFrac);

        var iso = baseP.Clone();
        iso.Relief2DIsolate = true;
        iso.Relief2DIsolateByDetail = true;
        iso.Relief2DDetailThreshold = 0.6;
        var cut = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, iso, cut, out double isoFrac);

        int transparent = 0;
        for (int i = 0; i < w * h; i++) if (((cut[i] >> 24) & 0xFF) == 0) transparent++;

        // Isolation removes background (fewer surface hits), keeps some object,
        // and drops the background to transparent alpha.
        Assert.True(isoFrac < fullFrac, $"isolate did not cull: {isoFrac} vs {fullFrac}");
        Assert.True(isoFrac > 0.02, $"isolate culled everything: {isoFrac}");
        Assert.True(transparent > w * h / 10, $"too few transparent px: {transparent}");
    }

    [Fact]
    public void Dead_Flat_Field_Is_Passthrough()
    {
        int w = 64, h = 64;
        var albedo = new uint[w * h];
        var height = new float[w * h];   // all zero = all interior
        for (int i = 0; i < w * h; i++) albedo[i] = 0xFF334455u;
        var dst = new uint[w * h];
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        HeightfieldRaymarch2D.Render(albedo, height, w, h, p, dst, out double hitFrac);
        Assert.Equal(albedo, dst);
        Assert.Equal(0.0, hitFrac);
    }
}
