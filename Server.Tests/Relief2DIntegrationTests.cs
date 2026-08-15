using System;
using System.Linq;
using Xunit;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Server.Tests;

public class Relief2DIntegrationTests
{
    [Fact]
    public void Mandelbrot_Relief2D_Modulates_Themed_Colour()
    {
        int w = 400, h = 400;
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.745428, CenterY = 0.113009, Zoom = 120.0, MaxIterations = 800,
            ColorMap = new MonoBandMap(),
        };
        calc.Calculate(default);

        var flat = (uint[])calc.ColorBuffer.Clone();
        var lit = new uint[w * h];
        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DHeightScale = 1.8,
            Relief2DLightAzimuthDeg = 135,
            Relief2DLightElevationDeg = 22,
            Relief2DShadowStrength = 0.85,
            Relief2DStrength = 1.0,
        };
        HeightfieldRelief2D.Apply(flat, lit, calc.SmoothBuffer, w, h, p);

        // Relief must change a substantial fraction of exterior pixels (hillshade
        // + shadows), and darken some (shadows/low-lambert) below the flat colour.
        // Relative relief leaves FLAT regions neutral (the fix for global
        // darkening) and shades only slopes — so it must both DARKEN (away
        // slopes / shadows) AND BRIGHTEN (toward-light slopes + specular),
        // i.e. it is not a one-way tint. Flats staying unchanged is correct.
        int changed = 0, darkened = 0, brightened = 0, exterior = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (calc.SmoothBuffer[i] > 0) exterior++;
            if (lit[i] != flat[i]) changed++;
            int lf = (int)(flat[i] & 0xFF) + (int)((flat[i] >> 8) & 0xFF) + (int)((flat[i] >> 16) & 0xFF);
            int ll = (int)(lit[i] & 0xFF) + (int)((lit[i] >> 8) & 0xFF) + (int)((lit[i] >> 16) & 0xFF);
            if (ll < lf - 20) darkened++;
            if (ll > lf + 20) brightened++;
        }

        Assert.True(exterior > w * h / 10, $"too little exterior: {exterior}");
        Assert.True(changed > exterior / 10,
            $"relief shaded too few pixels: {changed} of {exterior} exterior");
        Assert.True(darkened > exterior / 40,
            $"relief produced no shadows/away-slope shading: darkened={darkened}/{exterior}");
        Assert.True(brightened > exterior / 40,
            $"relief is one-way (only darkens): brightened={brightened}/{exterior} darkened={darkened}");
    }

    [Fact]
    public void Relief2D_Disabled_Is_Passthrough()
    {
        int w = 64, h = 64;
        var src = new uint[w * h];
        var height = new float[w * h];
        for (int i = 0; i < w * h; i++) { src[i] = 0xFF804020u; height[i] = i % 50; }
        var dst = new uint[w * h];
        var p = new FractalParameters { Relief2DEnabled = false };
        HeightfieldRelief2D.Apply(src, dst, height, w, h, p);
        Assert.Equal(src, dst);
    }

    // #127 — a FLAT (constant-height, zero-slope) exterior is the shallow-view
    // degenerate case: RELATIVE relief nets to neutral (flats are left alone),
    // while ABSOLUTE relief shades the whole surface by its orientation to the
    // light. This is the crisp difference between the two modes.
    [Fact]
    public void Relief2D_Absolute_Shades_Flat_Surface_Relative_Leaves_It()
    {
        int w = 64, h = 64, n = w * h;
        var src = new uint[n];
        var height = new float[n];
        for (int i = 0; i < n; i++) { src[i] = 0xFF404040u; height[i] = 5.0f; } // flat plane

        var relative = new uint[n];
        var absolute = new uint[n];
        var pRel = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DStrength = 1.0,
            Relief2DLightElevationDeg = 30, Relief2DShadowStrength = 0.0,
            Relief2DAbsolute = false,
        };
        var pAbs = pRel.Clone();
        pAbs.Relief2DAbsolute = true;

        HeightfieldRelief2D.Apply(src, relative, height, w, h, pRel);
        HeightfieldRelief2D.Apply(src, absolute, height, w, h, pAbs);

        // Relative: a flat surface reads neutral → unchanged.
        Assert.Equal(src, relative);

        // Absolute: the whole flat surface tilts into the light → every pixel
        // shifts (here brightened, elevation 30 → positive lambert).
        int changed = 0, brightened = 0;
        for (int i = 0; i < n; i++)
        {
            if (absolute[i] != src[i]) changed++;
            int sf = (int)(src[i] & 0xFF);
            int la = (int)(absolute[i] & 0xFF);
            if (la > sf + 20) brightened++;
        }
        Assert.Equal(n, changed);
        Assert.Equal(n, brightened);
    }
}
