// Server.Tests/Cluster/ReferenceOrbitBlobTests.cs
// D-6b — codec round-trip, planner attach, and a calculator-level
// pixel-parity check that a shared-orbit sub-rect render matches the
// corresponding window of a full-image render.

using System;
using System.Threading;

using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class ReferenceOrbitBlobTests
{
    [Fact]
    public void EncodeDecode_RoundTrip_DD()
    {
        int refLen = 17;
        int slots  = refLen + 1;
        var zr = new double[slots];
        var zi = new double[slots];
        var zrLo = new double[slots];
        var ziLo = new double[slots];
        for (int i = 0; i < slots; i++)
        {
            zr[i]   = i * 0.5;
            zi[i]   = -i * 0.25;
            zrLo[i] = i * 1e-17;
            ziLo[i] = -i * 1e-17;
        }

        byte[] blob = ReferenceOrbitBlobCodec.EncodeDD(
            refLen: refLen, maxIter: 1000, escaped: true,
            centreX: -0.75, centreXLo: 1e-20, centreY: 0.1, centreYLo: -1e-21,
            refZr: zr, refZi: zi, refZrLo: zrLo, refZiLo: ziLo);

        Assert.Equal(ReferenceOrbitBlobCodec.MagicByte, blob[0]);
        Assert.Equal(ReferenceOrbitBlobCodec.FormatVersion, blob[1]);
        Assert.Equal(ReferenceOrbitBlobCodec.LimbsDD, blob[2]);

        var decoded = ReferenceOrbitBlobCodec.Decode(blob);
        Assert.Equal(refLen, decoded.RefLen);
        Assert.Equal(1000, decoded.MaxIter);
        Assert.True(decoded.Escaped);
        Assert.Equal(-0.75, decoded.CentreX);
        Assert.Equal(1e-20, decoded.CentreXLo);
        Assert.Equal(0.1,   decoded.CentreY);
        Assert.Equal(-1e-21, decoded.CentreYLo);
        for (int i = 0; i < slots; i++)
        {
            Assert.Equal(zr[i],   decoded.RefZr[i]);
            Assert.Equal(zi[i],   decoded.RefZi[i]);
            Assert.Equal(zrLo[i], decoded.RefZrLo[i]);
            Assert.Equal(ziLo[i], decoded.RefZiLo[i]);
        }
    }

    [Fact]
    public void Decode_BadMagic_Throws()
    {
        byte[] blob = new byte[ReferenceOrbitBlobCodec.HeaderBytes];
        blob[0] = 0x42;  // wrong magic
        var ex = Assert.Throws<InvalidOperationException>(() => ReferenceOrbitBlobCodec.Decode(blob));
        Assert.Contains("magic", ex.Message);
    }

    [Fact]
    public void Decode_Truncated_Throws()
    {
        // Header says refLen=10 (11 slots × 4 arrays × 8 bytes = 352 of payload)
        // but blob carries only the header → truncated.
        int refLen = 10;
        var orbit = MandelbrotCalculator.ComputeReferenceOrbitDDPublic(-0.5, 0, 0, 0, refLen);
        byte[] full = ReferenceOrbitBlobCodec.EncodeDD(
            orbit.RefLen, orbit.MaxIter, orbit.Escaped,
            orbit.CentreX, orbit.CentreXLo, orbit.CentreY, orbit.CentreYLo,
            orbit.Zr, orbit.Zi, orbit.ZrLo, orbit.ZiLo);
        byte[] truncated = new byte[ReferenceOrbitBlobCodec.HeaderBytes];
        Array.Copy(full, truncated, ReferenceOrbitBlobCodec.HeaderBytes);
        var ex = Assert.Throws<InvalidOperationException>(() => ReferenceOrbitBlobCodec.Decode(truncated));
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public void QualifiesForSharedReferenceOrbit_Gates()
    {
        // Low-zoom Mandelbrot — refused (perturbation not engaged).
        Assert.False(TilePlanner.QualifiesForSharedReferenceOrbit(new RenderRequestDto
        {
            FractalType = "Mandelbrot", Zoom = 1.0,
        }));
        // High enough to engage — accepted.
        Assert.True(TilePlanner.QualifiesForSharedReferenceOrbit(new RenderRequestDto
        {
            FractalType = "Mandelbrot", Zoom = 1e10,
        }));
        // Beyond DD precision ceiling — refused (v1 ships DD only).
        Assert.False(TilePlanner.QualifiesForSharedReferenceOrbit(new RenderRequestDto
        {
            FractalType = "Mandelbrot", Zoom = 1e26,
        }));
        // Wrong fractal type — refused.
        Assert.False(TilePlanner.QualifiesForSharedReferenceOrbit(new RenderRequestDto
        {
            FractalType = "Julia", Zoom = 1e10,
        }));
    }

    [Fact]
    public void AttachSharedReferenceOrbit_RewritesTilesIntoImageFrame()
    {
        var req = new RenderRequestDto
        {
            FractalType = "Mandelbrot",
            CenterX = -0.7436, CenterY = 0.1318,
            Zoom = 1e9, Iterations = 500,
            Width = 256, Height = 128,
        };
        var plan = TilePlanner.PlanImage(req, tilePixelsHint: 128);
        Assert.Equal(2, plan.TileCount);    // 256/128 = 2 cols, 128/128 = 1 row

        var orbit = MandelbrotCalculator.ComputeReferenceOrbitDDPublic(
            req.CenterX!.Value, req.CenterXLo, req.CenterY!.Value, req.CenterYLo,
            req.Iterations!.Value);
        byte[] blob = ReferenceOrbitBlobCodec.EncodeDD(
            orbit.RefLen, orbit.MaxIter, orbit.Escaped,
            orbit.CentreX, orbit.CentreXLo, orbit.CentreY, orbit.CentreYLo,
            orbit.Zr, orbit.Zi, orbit.ZrLo, orbit.ZiLo);

        TilePlanner.AttachSharedReferenceOrbit(plan, req, blob, orbit.MaxIter);

        foreach (var t in plan.Tiles)
        {
            // Image-frame fields set.
            Assert.Equal(req.CenterX, t.Render.CenterX);
            Assert.Equal(req.CenterY, t.Render.CenterY);
            Assert.Equal(req.Zoom,    t.Render.Zoom);
            Assert.Equal(256, t.Render.ImageWidth);
            Assert.Equal(128, t.Render.ImageHeight);
            Assert.Equal(t.OffsetX, t.Render.SubRectOffsetX);
            Assert.Equal(t.OffsetY, t.Render.SubRectOffsetY);
            // Tile output dims still tile-local.
            Assert.True(t.Render.Width  <= 128);
            Assert.True(t.Render.Height <= 128);
            // Blob attached.
            Assert.False(string.IsNullOrEmpty(t.Render.RefOrbitBlobBase64));
            Assert.Equal(orbit.MaxIter, t.Render.RefOrbitMaxIter);
        }
    }

    [Fact]
    public void Calculator_SubRect_With_SeededOrbit_Matches_FullRender_Pixel_For_Pixel()
    {
        // Pixel-parity guard for the engine seam: render a 64×64 full
        // image, then render four 32×32 sub-rect tiles seeded with the
        // master-computed orbit. Each tile's pixels must equal the
        // corresponding window of the full render. Any drift here means
        // the dc-origin shift in ComputeRowPTScalar is wrong — exactly
        // the kind of bug the test exists to catch.
        const int imgW = 64, imgH = 64;
        const double cx = -0.7436, cy = 0.1318;
        const double zoom = 1e9;
        const int maxIter = 400;

        var theme = ColorPalette.GetPaletteByName("HSV");

        // Full reference render.
        var full = new MandelbrotCalculator(imgW, imgH)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom,
            MaxIterations = maxIter,
            ColorMap = theme,
            Quality = QualityPreset.Standard,
        };
        full.Calculate(CancellationToken.None);
        uint[] fullBuf = (uint[])full.ColorBuffer.Clone();

        // One pre-computed orbit shared by every tile.
        var orbit = MandelbrotCalculator.ComputeReferenceOrbitDDPublic(cx, 0, cy, 0, maxIter);

        const int tileW = 32, tileH = 32;
        for (int row = 0; row < 2; row++)
        {
            for (int col = 0; col < 2; col++)
            {
                int offX = col * tileW;
                int offY = row * tileH;

                var tile = new MandelbrotCalculator(tileW, tileH)
                {
                    CenterX = cx, CenterY = cy, Zoom = zoom,
                    MaxIterations = maxIter,
                    ColorMap = theme,
                    Quality = QualityPreset.Standard,
                    ImageWidth     = imgW,
                    ImageHeight    = imgH,
                    SubRectOffsetX = offX,
                    SubRectOffsetY = offY,
                };
                tile.SeedReferenceOrbitDD(orbit);
                tile.Calculate(CancellationToken.None);

                for (int y = 0; y < tileH; y++)
                {
                    for (int x = 0; x < tileW; x++)
                    {
                        uint fullPix = fullBuf[(offY + y) * imgW + (offX + x)];
                        uint tilePix = tile.ColorBuffer[y * tileW + x];
                        if (fullPix != tilePix)
                        {
                            Assert.Fail(
                                $"pixel drift tile ({col},{row}) at ({x},{y}) " +
                                $"image=({offX + x},{offY + y}): " +
                                $"full=0x{fullPix:X8} tile=0x{tilePix:X8}");
                        }
                    }
                }
            }
        }
    }
}
