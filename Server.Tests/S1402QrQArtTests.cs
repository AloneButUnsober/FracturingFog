// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1402 (S7 of #1305) — QArt-style data shaping. The independent invariant is
// ZXing (a separate implementation) reading the shaped symbol back to the
// exact payload; the shaping claim is that the code's own pattern then agrees
// with the target image far better than an unshaped code does.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Imaging.Qr;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1402QrQArtTests
{
    private const string Url = "https://example.org/qart";

    private static string? MatrixDecode(QrCode code)
    {
        var bm = new ZXing.Common.BitMatrix(code.Size, code.Size);
        for (int y = 0; y < code.Size; y++)
            for (int x = 0; x < code.Size; x++)
                if (code.Dark[y, x]) bm[x, y] = true;
        return new ZXing.QrCode.Internal.Decoder().decode(bm, null)?.Text;
    }

    // A smooth target: dark disc in the middle, weight = distance from its edge.
    private static Func<int, int, (bool, double)> Disc(int size) => (x, y) =>
    {
        double cx = size / 2.0, r = Math.Sqrt((x - cx) * (x - cx) + (y - cx) * (y - cx));
        double edge = size * 0.3;
        return (r < edge, Math.Abs(r - edge));
    };

    [Theory]
    [InlineData(QrErrorCorrection.L, 1)]
    [InlineData(QrErrorCorrection.M, 5)]
    [InlineData(QrErrorCorrection.Q, 8)]
    [InlineData(QrErrorCorrection.H, 12)]
    [InlineData(QrErrorCorrection.L, 25)]
    public void ShapedCode_DecodesToThePayload_AndFollowsTheImage(QrErrorCorrection ecc, int minVersion)
    {
        int version = QrEncoder.ChooseVersion(Url.Length, ecc, minVersion);
        int size = version * 4 + 17;
        for (int mask = 0; mask < 8; mask += 3)
        {
            var code = QrQArt.ShapeText(Url, ecc, minVersion, mask, Disc(size), out var stats);
            Assert.Equal(Url, MatrixDecode(code));
            Assert.Equal(mask, code.Mask);
            Assert.True(stats.Controlled > 0);
            Assert.True(stats.AgreementAfter > stats.AgreementBefore, stats.Summary);
        }
    }

    [Fact]
    public void WithRoom_TheCodeFollowsTheImageClosely()
    {
        int size = QrEncoder.ChooseVersion(Url.Length, QrErrorCorrection.L, 8) * 4 + 17;
        QrQArt.ShapeText(Url, QrErrorCorrection.L, 8, 2, Disc(size), out var s);
        Assert.True(s.AgreementBefore < 0.6, s.Summary);   // unshaped: about chance
        Assert.True(s.AgreementAfter > 0.85, s.Summary);
    }

    [Fact]
    public void LowerErrorCorrection_AndABiggerVersion_ShapeMore()
    {
        int Controlled(QrErrorCorrection e, int v)
        {
            int size = QrEncoder.ChooseVersion(Url.Length, e, v) * 4 + 17;
            QrQArt.ShapeText(Url, e, v, 0, Disc(size), out var s);
            return s.Controlled;
        }
        Assert.True(Controlled(QrErrorCorrection.L, 6) > Controlled(QrErrorCorrection.H, 6));
        Assert.True(Controlled(QrErrorCorrection.M, 10) > Controlled(QrErrorCorrection.M, 3));
    }

    [Fact]
    public void TheHeaviestModules_AreSetExactly()
    {
        const int v = 8;
        int size = v * 4 + 17;
        var target = Disc(size);
        var code = QrQArt.ShapeText(Url, QrErrorCorrection.L, v, 2, target, out var stats);
        // The top-weighted SHAPEABLE modules (not function patterns, not the
        // payload's own data bits) are claimed first; the first few dozen are
        // independent, so they must all match.
        var payloadModules = PayloadModules(v, QrErrorCorrection.L);
        var heavy = Enumerable.Range(0, size * size)
            .Where(i => !code.IsFunction[i / size, i % size] && !payloadModules.Contains(i))
            .OrderByDescending(i => target(i % size, i / size).Item2).ThenBy(i => i)
            .Take(48);
        foreach (int i in heavy)
            Assert.Equal(target(i % size, i / size).Item1, code.Dark[i / size, i % size]);
    }

    // Modules holding the header / payload / terminator bits: fixed, never shaped.
    private static System.Collections.Generic.HashSet<int> PayloadModules(int version, QrErrorCorrection ecc)
    {
        var (_, fixedBits) = QrEncoder.BuildShapeableData(System.Text.Encoding.UTF8.GetBytes(Url), version, ecc);
        var map = QrEncoder.MapBlocks(version, ecc);
        var bits = QrEncoder.CodewordBitModules(version);
        var set = new System.Collections.Generic.HashSet<int>();
        for (int b = 0; b < fixedBits; b++) set.Add(bits[map.DataToCodeword[b >> 3] * 8 + (b & 7)]);
        return set;
    }

    [Fact]
    public void NonAsciiPayload_StillDecodes()
    {
        const string text = "z² + c → ∞";
        var code = QrQArt.ShapeText(text, QrErrorCorrection.M, 4, 1, Disc(33), out _);
        Assert.Equal(text, MatrixDecode(code));
    }

    // ── end to end: shaped export of a real render ─────────────────────

    [Theory]
    [InlineData(QrStyle.Steer)]
    [InlineData(QrStyle.Halftone)]
    public void ShapedPoster_SavedFileScans_AndTheCodeFollowsTheFractal(QrStyle style)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff-qart-" + Guid.NewGuid().ToString("N") + ".png");
        var req = new PosterRequest
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.7453, CenterY = 0.1127, Zoom = 120, MaxIterations = 500,
            Width = 900, Height = 900,
            ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard,
            FractalParameters = new FractalParameters(),
            Path = path, Format = ImageFileFormat.Png,
            Qr = new QrCodeOptions { Payload = Url, ErrorCorrection = QrErrorCorrection.L, Style = style, Shape = true, MinVersion = 8 },
        };
        try
        {
            var result = PosterRenderer.RenderToFile(req, default);
            Assert.True(result.Qr!.Verify!.Ok, result.Qr.Verify.Summary);
            var shape = result.Qr.Shape!;
            Assert.True(shape.AgreementAfter > 0.75 && shape.AgreementBefore < 0.6, shape.Summary);
            var px = QrVerifier.LoadBgra(path, out int w, out int h);
            Assert.Equal(Url, QrVerifier.Decode(px!, w, h));
        }
        finally { try { System.IO.File.Delete(path); } catch { } }
    }

    [Fact]
    public void Unshaped_ReportsNoShapeStats_AndMinVersionIsHonoured()
    {
        const int side = 600;
        var px = Enumerable.Repeat(0xFFFFFFFFu, side * side).ToArray();
        var r = QrExport.Apply(px, side, side, new QrCodeOptions { Payload = Url, MinVersion = 7 });
        Assert.Null(r.Shape);
        Assert.Equal(7, r.Code.Version);
        Assert.NotNull(QrExport.Validate(new QrCodeOptions { Payload = Url, MinVersion = 41 }));
    }

    // ── batch + builder + Control Center parity ────────────────────────

    [Fact]
    public void Builder_ShapeAndVersion_RoundTrip()
    {
        var qr = new QrCodeOptions { Payload = Url, Shape = true, MinVersion = 9 };
        var args = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(new FracturingFog.Cli.BatchCommandSnapshot { Qr = qr })
            .Args.Select(a => a == "<OUTPUT.png>" ? "o.png" : a).ToArray();
        Assert.Contains("--qr-shape", args);
        Assert.Contains("--qr-version", args);
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args, 0, out var opts, out var err), err);
        Assert.Equal(qr, opts.BuildQrOptions());
    }

    [Fact]
    public void ViewModel_ShapeAndVersion_ReachTheOptions_AndVersionClearsTheMask()
    {
        var vm = new FracturingFog.UI.Avalonia.ViewModels.QrCodeViewModel { Payload = Url, Mask = 4 };
        vm.Shape = true;
        Assert.Equal(4, vm.Mask);
        vm.MinVersion = 10;
        Assert.Equal(-1, vm.Mask);
        Assert.True(vm.Options!.Shape);
        Assert.Equal(10, vm.Options.MinVersion);
    }
}
