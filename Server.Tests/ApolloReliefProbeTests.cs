using System.Collections.Generic;
using System.Linq;
using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

public class ApolloReliefProbeTests
{
    private static int CenterWindowDistinct(uint[] buf, int w, int h)
    {
        var window = new HashSet<uint>();
        for (int y = h/2 - 20; y < h/2 + 20; y++)
            for (int x = w/2 - 20; x < w/2 + 20; x++)
                window.Add(buf[y * w + x]);
        return window.Count;
    }

    [Fact]
    public void Apollonian_With_Relief3D_Theme_Shades_Domes()
    {
        int w = 256, h = 256;
        var calc = new ApollonianCalculator(w, h)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0,
            ColorMap = new MarbleReliefMap(),
            FractalParameters = new FractalParameters { ApollonianRelief = 1.0 },
        };
        calc.Calculate();

        int distinct = calc.ColorBuffer.Where(c => (c & 0xFFFFFF) != 0).Distinct().Count();
        int centerDistinct = CenterWindowDistinct(calc.ColorBuffer, w, h);

        // Flat 2D theme for comparison.
        var flat = new ApollonianCalculator(w, h)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0, ColorMap = new HsvPalette(),
        };
        flat.Calculate();
        int flatCenter = CenterWindowDistinct(flat.ColorBuffer, w, h);

        // Intra-disk colour variance proves per-pixel dome shading; the flat 2D
        // theme (HSV, ignores normals) stays near-uniform per disk.
        Assert.True(distinct > 20 && centerDistinct > flatCenter + 5,
            $"relief distinct(all)={distinct} center={centerDistinct} vs flatCenter={flatCenter}");
    }
}
