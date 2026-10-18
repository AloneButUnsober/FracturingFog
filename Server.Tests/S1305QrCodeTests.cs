// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1305 fractal QR codes - S1 (#1396) encoder + independent decode, S2 (#1397)
// compositor, S5 (#1400) halftone.
//
// Every "it works" assertion is the independent invariant: ZXing.Net (a
// separate implementation) decodes the image back to the exact payload. The
// encoder is never asked to agree with itself.

using System;
using System.IO;
using System.Linq;
using System.Text;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Imaging.Qr;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1305QrCodeTests
{
    private const uint Black = 0xFF000000u, White = 0xFFFFFFFFu;

    /// <summary>Plain black-on-white rendering with a 4-module quiet zone.</summary>
    private static (uint[] Px, int Side) Plain(QrCode code, int m = 4)
    {
        int side = (code.Size + 2 * QrCode.QuietZone) * m;
        var px = Enumerable.Repeat(White, side * side).ToArray();
        for (int y = 0; y < code.Size; y++)
            for (int x = 0; x < code.Size; x++)
                if (code.Dark[y, x])
                    for (int dy = 0; dy < m; dy++)
                        for (int dx = 0; dx < m; dx++)
                            px[((y + QrCode.QuietZone) * m + dy) * side + (x + QrCode.QuietZone) * m + dx] = Black;
        return (px, side);
    }

    private static string Payload(int length, int seed)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~:/?#[]@!$&'()*+,;=";
        var rng = new Random(seed);
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
        return sb.ToString();
    }

    // ── S1: encoder vs independent decoder ─────────────────────────────

    public static TheoryData<int, QrErrorCorrection> AllVersions()
    {
        var d = new TheoryData<int, QrErrorCorrection>();
        for (int v = 1; v <= 40; v++)
            foreach (var e in Enum.GetValues<QrErrorCorrection>()) d.Add(v, e);
        return d;
    }

    // The symbol itself: ZXing's decoder reads our module matrix (format, version,
    // mask, block interleave, Reed-Solomon, byte mode) for every version / level
    // at full capacity. The matrix decoder skips image detection on purpose -
    // detection is probabilistic (see QrEncoder.PseudoFinderPenalty) and is
    // covered by the image tests below.
    [Theory]
    [MemberData(nameof(AllVersions))]
    public void EveryVersionAndLevel_AtFullCapacity_DecodesToThePayload(int version, QrErrorCorrection ecc)
    {
        int cap = QrEncoder.CapacityBytes(version, ecc);
        string payload = Payload(cap, version * 7 + (int)ecc);
        var code = QrEncoder.Encode(payload, ecc);
        Assert.Equal(version, code.Version);   // a full-capacity payload needs exactly this version
        Assert.Equal(payload, MatrixDecode(code));
    }

    private static string? MatrixDecode(QrCode code)
    {
        var bm = new ZXing.Common.BitMatrix(code.Size, code.Size);
        for (int y = 0; y < code.Size; y++)
            for (int x = 0; x < code.Size; x++)
                if (code.Dark[y, x]) bm[x, y] = true;
        return new ZXing.QrCode.Internal.Decoder().decode(bm, null)?.Text;
    }

    // The whole image path (detection included): what QrExport writes scans
    // through the full reader at sizes from tiny to the largest version.
    [Theory]
    [InlineData(1)] [InlineData(5)] [InlineData(9)] [InlineData(16)]
    [InlineData(21)] [InlineData(28)] [InlineData(33)] [InlineData(40)]
    public void ExportedCode_OnAPlainCanvas_ScansAtEverySize(int version)
    {
        var ecc = QrErrorCorrection.M;
        string payload = Payload(QrEncoder.CapacityBytes(version, ecc), version);
        int side = (version * 4 + 17 + 8) * 3;
        var px = Enumerable.Repeat(White, side * side).ToArray();
        var (code, layout) = QrExport.Apply(px, side, side, new QrCodeOptions { Payload = payload, ErrorCorrection = ecc });
        Assert.Equal(version, code.Version);
        var result = QrVerifier.Verify(px, side, side, payload, layout.ModulePixels);
        Assert.True(result.Ok, result.Summary);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void EveryForcedMask_Decodes(int mask)
    {
        const string payload = "https://github.com/AloneButUnsober/FracturingFog";
        var code = QrEncoder.Encode(payload, QrErrorCorrection.M, mask: mask);
        Assert.Equal(mask, code.Mask);
        var (px, side) = Plain(code);
        Assert.Equal(payload, QrVerifier.Decode(px, side, side));
    }

    [Fact]
    public void NonAsciiPayload_UsesUtf8Eci_AndDecodes()
    {
        const string payload = "Fractured fog: z² + c → ∞ — été";
        var code = QrEncoder.Encode(payload, QrErrorCorrection.Q);
        var (px, side) = Plain(code);
        Assert.Equal(payload, QrVerifier.Decode(px, side, side));
    }

    // Published worked example (ISO 18004 annex / thonky.com "HELLO WORLD" 1-M):
    // data codewords -> the 10 error-correction codewords. Independent of our tables.
    [Fact]
    public void ReedSolomon_MatchesThePublishedHelloWorldVector()
    {
        byte[] data = { 32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17 };
        byte[] expected = { 196, 35, 39, 119, 235, 215, 231, 226, 93, 23 };
        Assert.Equal(expected, ReedSolomon.ComputeRemainder(data, ReedSolomon.ComputeDivisor(10)));
    }

    [Fact]
    public void OversizePayload_IsRejectedWithAReason()
    {
        var opt = new QrCodeOptions { Payload = new string('x', 3000), ErrorCorrection = QrErrorCorrection.L };
        Assert.NotNull(QrExport.Validate(opt));
        Assert.NotNull(QrExport.Validate(new QrCodeOptions { Payload = "" }));
        Assert.Null(QrExport.Validate(new QrCodeOptions { Payload = "ok" }));
    }

    [Fact]
    public void FunctionMask_CoversFindersTimingAndFormat_ButNotData()
    {
        var code = QrEncoder.Encode("function mask", QrErrorCorrection.H);
        int n = code.Size;
        Assert.True(code.IsFunction[0, 0] && code.IsFunction[7, 7]);          // finder + separator
        Assert.True(code.IsFunction[0, n - 1] && code.IsFunction[n - 1, 0]);
        Assert.True(code.IsFunction[6, 10] && code.IsFunction[10, 6]);        // timing
        Assert.True(code.IsFunction[8, 2] && code.IsFunction[n - 8, 8]);      // format + dark module
        Assert.True(code.Dark[n - 8, 8]);
        Assert.False(code.IsFunction[n - 1, n - 1]);                          // first data module
    }

    // ── S2 / S5: fractal compositing ───────────────────────────────────

    /// <summary>A busy, colourful, full-range test image: hue swirl x fine
    /// rings x hard bands - harder than most real renders for a decoder.</summary>
    private static uint[] Busy(int w, int h)
    {
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = (x - w / 2.0) / w, v = (y - h / 2.0) / h;
                double r = Math.Sqrt(u * u + v * v), a = Math.Atan2(v, u);
                double hue = (a / (2 * Math.PI) + r * 3) % 1.0;
                double val = 0.5 + 0.5 * Math.Sin(r * 140) * Math.Cos(a * 9);
                var c = HsvToArgb(hue < 0 ? hue + 1 : hue, 0.9, Math.Clamp(val, 0, 1));
                px[y * w + x] = c;
            }
        return px;
    }

    private static uint HsvToArgb(double h, double s, double v)
    {
        double c = v * s, hp = h * 6, x = c * (1 - Math.Abs(hp % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)hp switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x),
            3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return 0xFF000000u | (uint)((r + m) * 255) << 16 | (uint)((g + m) * 255) << 8 | (uint)((b + m) * 255);
    }

    [Theory]
    [InlineData(QrStyle.Dots, QrErrorCorrection.H, false)]
    [InlineData(QrStyle.Steer, QrErrorCorrection.H, false)]
    [InlineData(QrStyle.Halftone, QrErrorCorrection.H, false)]
    [InlineData(QrStyle.Dots, QrErrorCorrection.M, false)]
    [InlineData(QrStyle.Steer, QrErrorCorrection.M, false)]
    [InlineData(QrStyle.Halftone, QrErrorCorrection.M, false)]
    [InlineData(QrStyle.Steer, QrErrorCorrection.H, true)]
    [InlineData(QrStyle.Halftone, QrErrorCorrection.Q, true)]
    public void BusyImage_EveryStyle_PassesEveryRobustnessPass(QrStyle style, QrErrorCorrection ecc, bool invert)
    {
        const int W = 900, H = 640;
        var px = Busy(W, H);
        var opt = new QrCodeOptions
        {
            Payload = "https://github.com/AloneButUnsober/FracturingFog/issues/1305",
            ErrorCorrection = ecc, Style = style, Invert = invert,
        };
        var (_, layout) = QrExport.Apply(px, W, H, opt);
        var result = QrVerifier.Verify(px, W, H, opt.Payload, layout.ModulePixels);
        Assert.True(result.Ok, result.Summary);
    }

    [Fact]
    public void Steer_KeepsHue_AndOnlyMovesLuma()
    {
        // A saturated orange steered dark stays orange-ish (R > G > B), and its
        // luma lands in the dark core band.
        uint orange = 0xFFFF8020u;
        uint d = QrCompositor.Steer(orange, dark: true, core: true);
        int r = (int)(d >> 16) & 0xFF, g = (int)(d >> 8) & 0xFF, b = (int)d & 0xFF;
        Assert.True(r > g && g >= b, $"hue lost: {d:X8}");
        Assert.InRange(QrVerifier.LumaOf(d), 0, QrCompositor.DarkCoreMax);
        Assert.InRange(QrVerifier.LumaOf(QrCompositor.Steer(orange, dark: false, core: true)), QrCompositor.LightCoreMin, 255);
        // Pure blue (luma 29) cannot be made light without losing chroma - the
        // band still holds.
        Assert.InRange(QrVerifier.LumaOf(QrCompositor.Steer(0xFF0000FFu, dark: false, core: true)), QrCompositor.LightCoreMin, 255);
    }

    [Fact]
    public void Layout_CentresTheSymbol_AndRejectsATooSmallImage()
    {
        var L = QrCompositor.Layout(800, 500, 25, 0);
        Assert.Equal(500 / 33, L.ModulePixels);
        Assert.Equal((800 - 25 * L.ModulePixels) / 2, L.OriginX);
        Assert.Throws<ArgumentException>(() => QrCompositor.Layout(30, 30, 25, 0));
        Assert.Throws<ArgumentException>(() => QrCompositor.Layout(800, 500, 25, 40));
    }

    [Fact]
    public void OutsideTheCodeArea_PixelsAreUntouched()
    {
        const int W = 900, H = 400;
        var px = Busy(W, H);
        var before = (uint[])px.Clone();
        var (_, L) = QrExport.Apply(px, W, H, new QrCodeOptions { Payload = "untouched" });
        int q = L.QuietPixels;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < L.OriginX - q; x++)
                Assert.Equal(before[y * W + x], px[y * W + x]);
    }

    // ── S3: batch flags + Command builder parity ───────────────────────

    [Fact]
    public void Builder_QrFlags_RoundTripThroughTheParser()
    {
        var qr = new QrCodeOptions
        {
            Payload = "https://example.org/a b?x=1&y=\"two\"",
            ErrorCorrection = QrErrorCorrection.Q,
            Style = QrStyle.Halftone,
            ModulePixels = 7,
            DotFraction = 0.5,
            Invert = true,
            Verify = false,
        };
        var report = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(
            new FracturingFog.Cli.BatchCommandSnapshot { Qr = qr, CenterX = -0.5, Zoom = 1 });
        var args = report.Args.Select(a => a == "<OUTPUT.png>" ? "out.png" : a).ToArray();
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args, 0, out var opts, out var err), err);
        Assert.Equal(qr, opts.BuildQrOptions());
    }

    [Fact]
    public void Builder_DefaultQr_EmitsOnlyThePayload_AndNoQrEmitsNothing()
    {
        var withQr = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(
            new FracturingFog.Cli.BatchCommandSnapshot { Qr = new QrCodeOptions { Payload = "hi" } }).Args;
        Assert.Single(withQr, a => a.StartsWith("--qr-", StringComparison.Ordinal));
        var none = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(new FracturingFog.Cli.BatchCommandSnapshot()).Args;
        Assert.DoesNotContain(none, a => a.StartsWith("--qr-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--mode", "video")]
    [InlineData("--aov-exr", null)]
    public void Parser_RejectsQrOutsideAFlatImage(string flag, string? value)
    {
        var args = new System.Collections.Generic.List<string> { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--qr-payload", "x", flag };
        if (value != null) args.Add(value);
        Assert.False(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out _, out var err));
        Assert.Contains("--qr-payload", err);
    }

    [Fact]
    public void BatchPreflight_ReportsAnImageTooSmallForTheCode()
    {
        var qr = new QrCodeOptions { Payload = new string('a', 500), ErrorCorrection = QrErrorCorrection.H };
        Assert.NotNull(QrExport.Preflight(qr, 64, 64));
        Assert.Null(QrExport.Preflight(qr, 1200, 1200));
    }

    // ── S4: Control Center QR group ────────────────────────────────────

    [Fact]
    public void ViewModel_NoPayload_NoOptions_AndExportDisabled()
    {
        var vm = new FracturingFog.UI.Avalonia.ViewModels.QrCodeViewModel
        {
            Preflight = QrExport.Preflight,
            ExportRequested = (_, _) => System.Threading.Tasks.Task.FromResult<FracturingFog.UI.Avalonia.ViewModels.QrExportOutcome?>(null),
        };
        vm.HooksAttached();
        Assert.Null(vm.Options);
        Assert.False(((System.Windows.Input.ICommand)vm.ExportCommand).CanExecute(null));
        vm.Payload = "hello";
        Assert.True(((System.Windows.Input.ICommand)vm.ExportCommand).CanExecute(null));
    }

    [Fact]
    public void ViewModel_MapsItsControls_OntoTheOptions()
    {
        var vm = new FracturingFog.UI.Avalonia.ViewModels.QrCodeViewModel
        {
            Payload = "x", StyleIndex = 2, ErrorCorrectionIndex = 1, ModulePixels = 9, DotFraction = 0.33, Invert = true,
        };
        Assert.Equal(new QrCodeOptions
        {
            Payload = "x", Style = QrStyle.Halftone, ErrorCorrection = QrErrorCorrection.M,
            ModulePixels = 9, DotFraction = 0.33, Invert = true,
        }, vm.Options);
    }

    [Fact]
    public void ViewModel_ACodeThatCannotFit_IsAYellowWarning_AndBlocksExport()
    {
        var vm = new FracturingFog.UI.Avalonia.ViewModels.QrCodeViewModel
        {
            Preflight = QrExport.Preflight,
            ExportRequested = (_, _) => System.Threading.Tasks.Task.FromResult<FracturingFog.UI.Avalonia.ViewModels.QrExportOutcome?>(null),
        };
        vm.Payload = new string('z', 600);
        vm.Size = 64;
        Assert.True(vm.MessageIsWarning);
        Assert.True(vm.HasMessage);
        Assert.False(((System.Windows.Input.ICommand)vm.ExportCommand).CanExecute(null));
        vm.Size = 2000;
        Assert.False(vm.MessageIsWarning);
        Assert.True(((System.Windows.Input.ICommand)vm.ExportCommand).CanExecute(null));
    }

    [Fact]
    public async System.Threading.Tasks.Task ViewModel_AFailedScan_IsReportedAsAWarning()
    {
        var vm = new FracturingFog.UI.Avalonia.ViewModels.QrCodeViewModel
        {
            Preflight = QrExport.Preflight,
            ExportRequested = (_, _) => System.Threading.Tasks.Task.FromResult<FracturingFog.UI.Avalonia.ViewModels.QrExportOutcome?>(
                new FracturingFog.UI.Avalonia.ViewModels.QrExportOutcome(false, "does NOT scan")),
            Payload = "p",
        };
        await System.Reactive.Threading.Tasks.TaskObservableExtensions.ToTask(vm.ExportCommand.Execute());
        Assert.Equal("does NOT scan", vm.Message);
        Assert.True(vm.MessageIsWarning);
    }

    // ── end to end: a real Mandelbrot poster, watermark on, saved PNG ──

    [Theory]
    [InlineData(QrStyle.Steer)]
    [InlineData(QrStyle.Dots)]
    [InlineData(QrStyle.Halftone)]
    public void MandelbrotPoster_WithWatermark_SavedFileScans(QrStyle style)
    {
        string path = Path.Combine(Path.GetTempPath(), "ff-qr-" + Guid.NewGuid().ToString("N") + ".png");
        var req = new PosterRequest
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.7453, CenterY = 0.1127,
            Zoom = 120,
            MaxIterations = 600,
            Width = 720, Height = 720,
            ColorMap = ColorPalette.BuiltIns[0],
            Quality = QualityPreset.Standard,
            FractalParameters = new FractalParameters(),
            Path = path,
            Format = ImageFileFormat.Png,
            Watermark = "Seahorse Valley - Classic",
            SubText = "Fracturing Fog test",
            Qr = new QrCodeOptions { Payload = "https://example.org/fractal-qr", Style = style },
        };
        try
        {
            var result = PosterRenderer.RenderToFile(req, default);
            Assert.NotNull(result.Qr);
            Assert.NotNull(result.Qr!.Verify);
            Assert.True(result.Qr.Verify!.Ok, result.Qr.Verify.Summary);

            // And independently of the report: the file on disk decodes.
            var px = QrVerifier.LoadBgra(path, out int w, out int h);
            Assert.NotNull(px);
            Assert.Equal("https://example.org/fractal-qr", QrVerifier.Decode(px!, w, h));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void NoQrOptions_PosterResultCarriesNoQr()
    {
        string path = Path.Combine(Path.GetTempPath(), "ff-qr-none-" + Guid.NewGuid().ToString("N") + ".png");
        var req = new PosterRequest
        {
            FractalType = FractalType.Mandelbrot, CenterX = -0.5, Zoom = 0.8, MaxIterations = 100,
            Width = 64, Height = 48, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard,
            FractalParameters = new FractalParameters(), Path = path, Format = ImageFileFormat.Png,
        };
        try { Assert.Null(PosterRenderer.RenderToFile(req, default).Qr); }
        finally { try { File.Delete(path); } catch { } }
    }
}
