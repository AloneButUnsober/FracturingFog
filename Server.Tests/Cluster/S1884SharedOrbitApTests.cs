// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1884 (epic #1428, C6c) — the server / cluster path past 1e100. Checked:
//   • the master's tier-3 orbit (ComputeReferenceOrbitAPPublic, the full image's period
//     tolerance) is the orbit a tile's own calculator builds (every limb of every Z_n, the
//     period data), at 1e150 (dense 340-digit centre near M(4,1)) and 1e320 (past double);
//   • with a periodic reference (the period-2 nucleus c = −1: Z_2 = c² + c = 0 exactly,
//     dZ_2/dc = 2c + 1 = −1; deep minibrots' nuclei don't show their period at the zoom's
//     precision, the rounding amplified past the pixel): at the nucleus the master ships
//     the period (#1614) and a tile seeded with it renders as its own without building;
//     just off it, where |Z_2| lies between the full image's pixel and a 1-pixel image's,
//     the master detects no period, as the tile doesn't (the tolerance is the full image's
//     pixel, not the master's own 1×1 frame's);
//   • the version-2 blob round-trips it (SharedOrbitAp + ReferenceOrbitBlobCodec): every
//     field; it carries format version 2, which a version-1 decoder refuses; a truncated
//     blob is refused;
//   • a tile seeded with it renders byte-identically to the tile building its own orbit,
//     without building one (the orbit generation is the seed's); a seed for another centre
//     is not used (the tile rebuilds, same pixels);
//   • the planner ships it past 1e110 at any depth (log zoom past double);
//   • the server engine (HostFractalRenderEngine) renders a request's exact centre and log
//     zoom as the poster path does (it dropped both before), and a seeded tile request as
//     an unseeded one, the seed reaching the calculator (a poisoned seed changes the tile);
//     a video frame carries them too.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Server.Cluster;
using FracturingFog.Server.Protocol;
using FracturingFog.ServerHost;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class S1884SharedOrbitApTests
{
    private const string Mx = "-0.1010963638456221610257854457386225654638054428262534838769311776607808407404705842748212198105167790334045319085567411939715461442609118823557039076803016157584181434354544823068106664029419378267411891607819526448995396995334466823393126699009817557150425206538866991861344333961600610562775385144259609560703568221643279956133036926714760";
    private const string My = "0.9562865108091415007710960577299774358098333365105291700343143215005246590657167325269784107873398072043444724926469284366752406567465722656200815719741551313831228054884443810571677870203738395055477279309548311953190385170024877917670113105649462054553485859930615004427397447762045980085973915977603864276670744042253335354323566859404712";
    private const int W = 48, H = 27;

    private static ExactCentre Centre()
    {
        Assert.True(ExactCentre.TryParse(Mx, My, out var c));
        return c;
    }

    private static int MaxIt(int zl) => 1000 + 12 * zl;   // as S1880: escapes at ~8 per decade here

    // A tile of the W×H image: its sub-rect geometry, the image's centre and zoom.
    private static MandelbrotCalculator Tile(int zl, int offX, int offY, int tw, int th)
    {
        var c = Centre(); var d = c.ToDeep();
        var calc = new MandelbrotCalculator(tw, th)
        {
            ImageWidth = W, ImageHeight = H, SubRectOffsetX = offX, SubRectOffsetY = offY,
            MaxIterations = MaxIt(zl), ColorMap = new HsvPalette(), Quality = QualityPreset.Extreme,
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.SetZoomLog10(zl);
        calc.SetDepth(c);
        return calc;
    }

    private static MandelbrotCalculator.OrbitAP Master(int zl)
    {
        var c = Centre();
        var ap = MandelbrotCalculator.ComputeReferenceOrbitAPPublic(c.ToDeep(), c, zl, W, H, MaxIt(zl));
        Assert.NotNull(ap);
        return ap!;
    }

    private static void AssertSameOrbit(MandelbrotCalculator.OrbitAP a, MandelbrotCalculator.OrbitAP b)
    {
        Assert.Equal(a.Od.RefLen, b.Od.RefLen);
        Assert.Equal(a.Od.MaxIter, b.Od.MaxIter);
        Assert.Equal(a.Od.Escaped, b.Od.Escaped);
        Assert.Equal(a.Exact, b.Exact);
        Assert.Equal(a.Precision, b.Precision);
        Assert.Equal(a.Period, b.Period);
        Assert.Equal(a.PeriodZ2, b.PeriodZ2);
        Assert.Equal((a.PeriodZ2Fx.M, a.PeriodZ2Fx.E), (b.PeriodZ2Fx.M, b.PeriodZ2Fx.E));
        Assert.Equal(a.MaxUsefulZoomLog10, b.MaxUsefulZoomLog10);
        double[][] A(MandelbrotCalculator.OrbitOD o) => new[] { o.Zr, o.Zi, o.ZrLo, o.ZiLo, o.ZrX2, o.ZiX2, o.ZrX3, o.ZiX3,
            o.ZrX4, o.ZiX4, o.ZrX5, o.ZiX5, o.ZrX6, o.ZiX6, o.ZrX7, o.ZiX7 };
        var aa = A(a.Od); var bb = A(b.Od);
        for (int k = 0; k < 16; k++)
            Assert.True(aa[k].AsSpan(0, a.Od.RefLen + 1).SequenceEqual(bb[k].AsSpan(0, b.Od.RefLen + 1)), $"limb array {k}");
    }

    [Theory]
    [InlineData(150)]
    [InlineData(320)]
    public void The_Masters_Orbit_Is_The_Tiles_Own(int zl)
    {
        var master = Master(zl);
        var tile = Tile(zl, 16, 9, 16, 9);
        tile.BuildReferenceOrbit();
        Assert.Equal(3, tile.ReferenceOrbitTier);
        AssertSameOrbit(master, tile.ExportReferenceOrbitAP()!);
        Assert.True(master.Od.RefLen > 100, "precondition: a real orbit");
    }

    private static MandelbrotCalculator PeriodicTile(ExactCentre c, int zl)
    {
        var d = c.ToDeep();
        var calc = new MandelbrotCalculator(16, 9)
        {
            ImageWidth = W, ImageHeight = H, SubRectOffsetX = 16, SubRectOffsetY = 9,
            MaxIterations = MaxIt(zl), ColorMap = new HsvPalette(), Quality = QualityPreset.Extreme,
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.SetZoomLog10(zl);
        calc.SetDepth(c);
        return calc;
    }

    [Fact]
    public void A_Periodic_Reference_Ships_Its_Period_At_The_Images_Tolerance()
    {
        const int zl = 152, n = 2;
        int prec = BigFloat.PrecisionForZoom(zl) + 64;
        var nc = BigFloat.FromDouble(-1, prec);   // dZ_2/dc = −1 there
        var zero = BigFloat.Zero(prec);

        // At the nucleus: the period, shipped and used.
        var at = new ExactCentre(nc, zero);
        var master = MandelbrotCalculator.ComputeReferenceOrbitAPPublic(at.ToDeep(), at, zl, W, H, MaxIt(zl))!;
        Assert.Equal(n, master.Period);
        var own = PeriodicTile(at, zl);
        own.BuildReferenceOrbit();
        AssertSameOrbit(master, own.ExportReferenceOrbitAP()!);
        var seeded = PeriodicTile(at, zl);
        seeded.SeedReferenceOrbitAP(SharedOrbitAp.ToOrbit(ReferenceOrbitBlobCodec.Decode(SharedOrbitAp.Encode(master)))!);
        int gen = seeded.ReferenceOrbitGeneration;
        seeded.Calculate(CancellationToken.None);
        var ownFrame = PeriodicTile(at, zl);
        ownFrame.Calculate(CancellationToken.None);
        Assert.Equal(gen, seeded.ReferenceOrbitGeneration);
        Assert.Equal(ownFrame.IterationBuffer, seeded.IterationBuffer);

        // Just off it: |Z_2| ≈ |dZ_2/dc|·δ = δ = 3.5 / (7·zoom), between the W-wide image's
        // pixel (3.5 / (48·zoom)) and a 1-pixel image's (3.5 / zoom).
        var delta = BigFloat.Parse($"5e-{zl + 1}", prec);
        var near = new ExactCentre(BigFloat.Add(nc, delta, prec), zero);
        var onePixel = MandelbrotCalculator.ComputeReferenceOrbitAPPublic(near.ToDeep(), near, zl, 1, 1, MaxIt(zl))!;
        Assert.Equal(n, onePixel.Period);   // precondition: the tolerance decides here
        var nearMaster = MandelbrotCalculator.ComputeReferenceOrbitAPPublic(near.ToDeep(), near, zl, W, H, MaxIt(zl))!;
        var nearOwn = PeriodicTile(near, zl);
        nearOwn.BuildReferenceOrbit();
        Assert.Equal(0, nearMaster.Period);
        AssertSameOrbit(nearMaster, nearOwn.ExportReferenceOrbitAP()!);
    }

    [Fact]
    public void The_Version_2_Blob_Round_Trips_The_Orbit()
    {
        var master = Master(150);
        byte[] blob = SharedOrbitAp.Encode(master);
        Assert.Equal(ReferenceOrbitBlobCodec.MagicByte, blob[0]);
        Assert.Equal(ReferenceOrbitBlobCodec.FormatVersionAp, blob[1]);
        Assert.NotEqual(ReferenceOrbitBlobCodec.FormatVersion, blob[1]);   // a v1 decoder refuses it
        var d = ReferenceOrbitBlobCodec.Decode(blob);
        Assert.NotNull(d.Ap);
        AssertSameOrbit(master, SharedOrbitAp.ToOrbit(d)!);
        // A period FloatExp survives too (synthetic: this orbit has none).
        var withFx = new MandelbrotCalculator.OrbitAP
        {
            Od = master.Od, Exact = master.Exact, Precision = master.Precision, Period = 7, PeriodZ2 = 1e-300,
            PeriodZ2Fx = FloatExp.FromLog10(-700), MaxUsefulZoomLog10 = 151.5,
        };
        AssertSameOrbit(withFx, SharedOrbitAp.ToOrbit(ReferenceOrbitBlobCodec.Decode(SharedOrbitAp.Encode(withFx)))!);
        // Truncated anywhere past the header: refused.
        foreach (int cut in new[] { 60, 150, 200, blob.Length - 8 })
            Assert.Throws<InvalidOperationException>(() => ReferenceOrbitBlobCodec.Decode(blob.AsSpan(0, cut).ToArray()));
        // The old (OD) blobs still decode, with no AP extension.
        var o = master.Od;
        var od = ReferenceOrbitBlobCodec.Decode(ReferenceOrbitBlobCodec.EncodeOD(o.RefLen, o.MaxIter, o.Escaped,
            o.CentreX, o.CentreXLo, o.CentreX2, o.CentreX3, o.CentreX4, o.CentreX5, o.CentreX6, o.CentreX7,
            o.CentreY, o.CentreYLo, o.CentreY2, o.CentreY3, o.CentreY4, o.CentreY5, o.CentreY6, o.CentreY7,
            o.Zr, o.Zi, o.ZrLo, o.ZiLo, o.ZrX2, o.ZiX2, o.ZrX3, o.ZiX3, o.ZrX4, o.ZiX4, o.ZrX5, o.ZiX5, o.ZrX6, o.ZiX6, o.ZrX7, o.ZiX7));
        Assert.Null(od.Ap);
        Assert.Null(SharedOrbitAp.ToOrbit(od));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(320)]
    public void A_Seeded_Tile_Renders_As_Its_Own_Without_Building(int zl)
    {
        var own = Tile(zl, 16, 9, 16, 9);
        own.Calculate(CancellationToken.None);

        var seeded = Tile(zl, 16, 9, 16, 9);
        seeded.SeedReferenceOrbitAP(SharedOrbitAp.ToOrbit(ReferenceOrbitBlobCodec.Decode(SharedOrbitAp.Encode(Master(zl))))!);
        int gen = seeded.ReferenceOrbitGeneration;
        seeded.Calculate(CancellationToken.None);
        Assert.Equal(gen, seeded.ReferenceOrbitGeneration);   // no orbit was built
        Assert.Equal(own.IterationBuffer, seeded.IterationBuffer);
        Assert.Equal(own.ColorBuffer, seeded.ColorBuffer);
        Assert.Contains(own.IterationBuffer, n => n < own.MaxIterations);

        // A seed for another centre (one pixel off) is not used: the tile builds its own.
        var c = Centre();
        var off = c.TranslatePixels(1, 0, Math.Pow(10, -zl), -zl, c.Precision + 64);
        var other = MandelbrotCalculator.ComputeReferenceOrbitAPPublic(off.ToDeep(), off, zl, W, H, MaxIt(zl))!;
        var wrong = Tile(zl, 16, 9, 16, 9);
        wrong.SeedReferenceOrbitAP(other);
        int g2 = wrong.ReferenceOrbitGeneration;
        wrong.Calculate(CancellationToken.None);
        Assert.NotEqual(g2, wrong.ReferenceOrbitGeneration);
        Assert.Equal(own.IterationBuffer, wrong.IterationBuffer);
    }

    [Fact]
    public void The_Planner_Ships_It_Past_1e110_At_Any_Depth()
    {
        var r = new RenderRequestDto { FractalType = "Mandelbrot", Width = W, Height = H, CenterX = -0.1, CenterY = 0.95 };
        foreach (double zl in new[] { 112.0, 150.0, 320.0, 5000.0 })
        {
            r.SetZoomLog10(zl);
            Assert.True(TilePlanner.QualifiesForSharedReferenceOrbit(r), $"1e{zl}");
        }
        r.SetZoomLog10(5);
        Assert.False(TilePlanner.QualifiesForSharedReferenceOrbit(r));
        r.SetZoomLog10(60);
        Assert.True(TilePlanner.QualifiesForSharedReferenceOrbit(r));   // OD blob, unchanged
    }

    // ── the server engine ────────────────────────────────────────────────────

    private sealed class Log : ISessionLog
    {
        public readonly List<string> Lines = new();
        public void Info(string line) { lock (Lines) Lines.Add(line); }
        public void Warn(string line) { lock (Lines) Lines.Add("WARN " + line); }
        public void Err(string line) { lock (Lines) Lines.Add("ERR " + line); }
    }

    private static RenderRequestDto Request(int zl, bool exact = true)
    {
        var c = Centre(); var d = c.ToDeep();
        var r = new RenderRequestDto
        {
            FractalType = "Mandelbrot", Width = W, Height = H, Iterations = MaxIt(zl), SuppressDecorations = true,
            ThemeName = "HSV", QualityName = "Standard",
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        r.SetZoomLog10(zl);
        if (exact) r.SetCentreExact(c);
        return r;
    }

    private static uint[] Pixels(string path)
    {
        using var bmp = SKBitmap.Decode(path);
        Assert.NotNull(bmp);
        return bmp.Pixels.Select(p => (uint)p).ToArray();
    }

    private static string Engine(RenderRequestDto r, Log log, string dir)
        => new HostFractalRenderEngine().RenderAsync(r, dir, log, CancellationToken.None).GetAwaiter().GetResult().FilePath;

    [Theory]
    [InlineData(150)]
    [InlineData(320)]
    public void The_Engine_Renders_The_Exact_Centre_And_Log_Zoom(int zl)
    {
        string dir = Directory.CreateTempSubdirectory("ff1884-").FullName;
        try
        {
            var log = new Log();
            var got = Pixels(Engine(Request(zl), log, dir));
            // The poster path with the same view (what --batch renders, C10).
            var c = Centre(); var d = c.ToDeep();
            var want = new PosterRequest
            {
                FractalType = FractalType.Mandelbrot, Width = W, Height = H,
                CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
                CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
                CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
                CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
                CenterExact = c, Zoom = ZoomLogFrom(zl), ZoomLog10 = zl, MaxIterations = MaxIt(zl),
                ColorMap = ColorPalette.GetPaletteByName("HSV"), Quality = QualityPreset.Standard,
                FractalParameters = new FractalParameters(), Path = Path.Combine(dir, "want.png"), Format = ImageFileFormat.Png,
                Watermark = "", SubText = "",
            };
            PosterRenderer.RenderToFile(want, CancellationToken.None);
            Assert.Equal(Pixels(want.Path), got);
            Assert.True(got.Distinct().Count() > 4, "precondition: structure in the frame");
            // Without the exact centre the frame differs (the OD-rounded centre).
            var noExact = Pixels(Engine(Request(zl, exact: false), log, dir));
            Assert.NotEqual(got, noExact);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static double ZoomLogFrom(double zl) => FracturingFog.ViewState.ZoomLog.FromLog10(zl);

    // The same orbit with every Z_n from n = 10 on moved by 1e-3 (a stale or wrong seed).
    private static MandelbrotCalculator.OrbitAP Poison(MandelbrotCalculator.OrbitAP a)
    {
        var o = a.Od;
        var zr = (double[])o.Zr.Clone();
        for (int k = 10; k <= o.RefLen; k++) zr[k] += 1e-3;
        var od = new MandelbrotCalculator.OrbitOD
        {
            CentreX = o.CentreX, CentreXLo = o.CentreXLo, CentreX2 = o.CentreX2, CentreX3 = o.CentreX3,
            CentreX4 = o.CentreX4, CentreX5 = o.CentreX5, CentreX6 = o.CentreX6, CentreX7 = o.CentreX7,
            CentreY = o.CentreY, CentreYLo = o.CentreYLo, CentreY2 = o.CentreY2, CentreY3 = o.CentreY3,
            CentreY4 = o.CentreY4, CentreY5 = o.CentreY5, CentreY6 = o.CentreY6, CentreY7 = o.CentreY7,
            MaxIter = o.MaxIter, RefLen = o.RefLen, Escaped = o.Escaped,
            Zr = zr, Zi = o.Zi, ZrLo = o.ZrLo, ZiLo = o.ZiLo, ZrX2 = o.ZrX2, ZiX2 = o.ZiX2, ZrX3 = o.ZrX3, ZiX3 = o.ZiX3,
            ZrX4 = o.ZrX4, ZiX4 = o.ZiX4, ZrX5 = o.ZrX5, ZiX5 = o.ZiX5, ZrX6 = o.ZrX6, ZiX6 = o.ZiX6, ZrX7 = o.ZrX7, ZiX7 = o.ZiX7,
        };
        return new MandelbrotCalculator.OrbitAP
        {
            Od = od, Exact = a.Exact, Precision = a.Precision, Period = a.Period, PeriodZ2 = a.PeriodZ2,
            PeriodZ2Fx = a.PeriodZ2Fx, MaxUsefulZoomLog10 = a.MaxUsefulZoomLog10,
        };
    }

    [Fact]
    public void A_Seeded_Tile_Request_Renders_As_An_Unseeded_One()
    {
        string dir = Directory.CreateTempSubdirectory("ff1884-").FullName;
        try
        {
            const int zl = 150;
            RenderRequestDto TileReq()
            {
                var r = Request(zl);
                r.Width = 16; r.Height = 9; r.ImageWidth = W; r.ImageHeight = H; r.SubRectOffsetX = 16; r.SubRectOffsetY = 9;
                return r;
            }
            var log = new Log();
            var plain = Pixels(Engine(TileReq(), log, dir));
            var seededReq = TileReq();
            seededReq.RefOrbitBlobBase64 = Convert.ToBase64String(SharedOrbitAp.Encode(Master(zl)));
            seededReq.RefOrbitMaxIter = MaxIt(zl);
            var seeded = Pixels(Engine(seededReq, log, dir));
            Assert.Contains(log.Lines, l => l.StartsWith("seeded AP ref orbit"));
            Assert.Equal(plain, seeded);
            // The seed reaches the calculator: a poisoned one (same centre, Z_n moved) changes the tile.
            var poisonedReq = TileReq();
            poisonedReq.RefOrbitBlobBase64 = Convert.ToBase64String(SharedOrbitAp.Encode(Poison(Master(zl))));
            poisonedReq.RefOrbitMaxIter = MaxIt(zl);
            Assert.NotEqual(plain, Pixels(Engine(poisonedReq, log, dir)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_Video_Frame_Carries_The_Exact_Centre_And_Log_Zoom()
    {
        const int zl = 320;
        var r = Request(zl);
        var cm = ColorPalette.GetPaletteByName("HSV");
        var d = Centre().ToDeep();
        uint[] Frame(RenderRequestDto? q, double l) => HostFractalRenderEngine.RenderOneFrame(FractalType.Mandelbrot, W, H,
            d.Re.X0, d.Re.X1, d.Re.X2, d.Re.X3, d.Im.X0, d.Im.X1, d.Im.X2, d.Im.X3,
            ZoomLogFrom(l), MaxIt(zl), cm, QualityPreset.Standard, CancellationToken.None, q, l);
        var got = Frame(r, zl);
        // The view rendered directly.
        var c = Tile(zl, 0, 0, W, H);
        c.ImageWidth = 0; c.ImageHeight = 0; c.ColorMap = cm; c.Quality = QualityPreset.Standard;
        c.Calculate(CancellationToken.None);
        Assert.Equal(c.ColorBuffer.Take(W * H), got);
        Assert.NotEqual(got, Frame(null, double.NaN));   // the pre-#1884 frame: saturated zoom, Hi limbs
    }
}
