using System;
using System.Linq;
using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

public class TricornReliefProbeTests
{
    private static (int distinct, double maxNormalMag) Render(FractalType type)
    {
        int w = 256, h = 256;
        var calc = new EscapeTimeCalculator(w, h)
        {
            FractalType = type,
            UseGpuCompute = false,               // force CPU SIMD path
            CenterX = type == FractalType.Tricorn ? -0.2 : -0.5,
            CenterY = 0, Zoom = 0.6, MaxIterations = 400,
            ColorMap = new MarbleReliefMap(),
            FractalParameters = new FractalParameters(),
        };
        calc.Resize(w, h);
        calc.Calculate();

        int distinct = calc.ColorBuffer.Where(c => (c & 0xFFFFFF) != 0).Distinct().Count();
        double maxMag = 0;
        for (int i = 0; i < w * h; i++)
        {
            double nx = calc.NormalXBuffer[i], ny = calc.NormalYBuffer[i];
            double m = Math.Sqrt(nx * nx + ny * ny);
            if (m > maxMag) maxMag = m;
        }
        return (distinct, maxMag);
    }

    [Fact]
    public void Tricorn_Vs_Mandelbrot_Relief()
    {
        var mb = Render(FractalType.Mandelbrot);
        var tc = Render(FractalType.Tricorn);
        // Surface both numbers; assert Mandelbrot is the known-good reference so
        // the harness itself is valid, then report Tricorn.
        Assert.True(mb.distinct > 20 && mb.maxNormalMag > 0.1,
            $"Mandelbrot ref weak: distinct={mb.distinct} maxNormal={mb.maxNormalMag:0.000}");
        // Tricorn's surface normals (from the "as if Mandelbrot" derivative)
        // are as strong as Mandelbrot's — proves 3D Relief themes DO shade
        // Tricorn on the CPU path (regression guard for the flat-3D report).
        Assert.True(tc.distinct > 20 && tc.maxNormalMag > 0.5,
            $"Tricorn 3D relief weak/flat: distinct={tc.distinct} maxNormal={tc.maxNormalMag:0.000} " +
            $"(Mandelbrot ref: distinct={mb.distinct} maxNormal={mb.maxNormalMag:0.000})");
    }
}
