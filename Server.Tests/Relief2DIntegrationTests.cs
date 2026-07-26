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
        int changed = 0, darkened = 0, exterior = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (calc.SmoothBuffer[i] > 0) exterior++;
            if (lit[i] != flat[i]) changed++;
            int lf = (int)(flat[i] & 0xFF) + (int)((flat[i] >> 8) & 0xFF) + (int)((flat[i] >> 16) & 0xFF);
            int ll = (int)(lit[i] & 0xFF) + (int)((lit[i] >> 8) & 0xFF) + (int)((lit[i] >> 16) & 0xFF);
            if (ll < lf - 20) darkened++;
        }

        Assert.True(exterior > w * h / 10, $"too little exterior: {exterior}");
        Assert.True(changed > exterior / 2,
            $"relief changed too few pixels: {changed} of {exterior} exterior");
        Assert.True(darkened > exterior / 20,
            $"relief produced no shading/shadows: darkened={darkened} of {exterior}");
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
}
