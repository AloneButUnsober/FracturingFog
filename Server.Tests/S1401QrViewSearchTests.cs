// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1401 (S6 of #1305) — fractal-native QR view search. The search must
// (a) never make the match worse than the start view, (b) keep the fractal
// (detail floor), (c) be deterministic for a seed, and (d) still produce an
// image an independent decoder (ZXing) reads back to the payload.

using System;
using System.Linq;
using System.Threading.Tasks;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Imaging.Qr;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1401QrViewSearchTests
{
    private const string Url = "https://example.org/fractal-qr";

    private static PosterRequest Mandel(int size = 400) => new()
    {
        FractalType = FractalType.Mandelbrot,
        CenterX = -0.7453, CenterY = 0.1127, Zoom = 120, MaxIterations = 500,
        Width = size, Height = size,
        ColorMap = ColorPalette.BuiltIns[0],
        Quality = QualityPreset.Standard,
        FractalParameters = new FractalParameters(),
        Format = ImageFileFormat.Png,
    };

    private static readonly QrSearchOptions Quick = QrSearchOptions.Default with { Tries = 8, Levels = 2, Keep = 2 };

    [Fact]
    public void Spec_RoundTrips_AndRejectsBadKeys()
    {
        var o = QrSearchOptions.Default with { Seed = 7, Tries = 30, Levels = 5, Keep = 4, MinZoomFactor = 0.25, MaxZoomFactor = 16, ProbeModulePixels = 2, MinDetail = 0.6 };
        Assert.True(QrSearchOptions.TryParseSpec(QrSearchOptions.FormatSpec(o), out var back, out var err), err);
        Assert.Equal(o, back);
        Assert.True(QrSearchOptions.TryParseSpec("", out var d, out _));
        Assert.Equal(QrSearchOptions.Default, d);
        Assert.False(QrSearchOptions.TryParseSpec("bogus=1", out _, out _));
        Assert.False(QrSearchOptions.TryParseSpec("tries=0", out _, out _));
        Assert.False(QrSearchOptions.TryParseSpec("zmin=2", out _, out _));
    }

    // An image that IS mask m's pattern matches mask m perfectly and costs less
    // than under any other mask.
    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(6)]
    public void Score_AnImageDrawnFromAMask_PicksThatMask(int mask)
    {
        var qr = new QrCodeOptions { Payload = Url };
        var codes = QrViewSearch.AllMasks(qr);
        const int m = 6;
        int side = (codes[0].Size + 8) * m;
        var layout = QrCompositor.Layout(side, side, codes[0].Size, 0);
        var px = Enumerable.Repeat(0xFFFFFFFFu, side * side).ToArray();
        for (int y = 0; y < codes[mask].Size; y++)
            for (int x = 0; x < codes[mask].Size; x++)
                if (codes[mask].Dark[y, x])
                    for (int dy = 0; dy < layout.ModulePixels; dy++)
                        for (int dx = 0; dx < layout.ModulePixels; dx++)
                            px[(layout.OriginY + y * layout.ModulePixels + dy) * side + layout.OriginX + x * layout.ModulePixels + dx] = 0xFF000000u;
        var s = QrViewSearch.Score(px, side, side, codes, layout, side, side, invert: false);
        Assert.Equal(mask, s.Mask);
        Assert.Equal(0.0, s.Mismatch);
    }

    [Fact]
    public void Search_NeverWorseThanTheStart_KeepsDetail_AndIsDeterministic()
    {
        var qr = new QrCodeOptions { Payload = Url };
        var a = QrViewSearch.Run(Mandel(), qr, Quick with { Seed = 3 });
        var b = QrViewSearch.Run(Mandel(), qr, Quick with { Seed = 3 });
        Assert.True(a.BestScore.Cost <= a.StartScore.Cost);
        Assert.True(a.BestScore.Detail >= a.StartScore.Detail * Quick.MinDetail - 1e-12);
        Assert.Equal(a.Best, b.Best);
        Assert.Equal(a.BestScore, b.BestScore);
        // start + level 1 (8 around one parent) + level 2 (4 around each of 2 kept)
        Assert.Equal(1 + 8 + 2 * 4, a.Probes);
    }

    [Fact]
    public void FoundView_WithItsMask_RendersAFileThatScans()
    {
        var qr = new QrCodeOptions { Payload = Url };
        var found = QrViewSearch.Run(Mandel(), qr, Quick with { Seed = 5 });
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff-qrsearch-" + Guid.NewGuid().ToString("N") + ".png");
        var req = QrViewSearch.WithView(Mandel(720), found.Best) with
        {
            Path = path,
            Qr = qr with { Mask = found.BestScore.Mask },
        };
        try
        {
            var result = PosterRenderer.RenderToFile(req, default);
            Assert.True(result.Qr!.Verify!.Ok, result.Qr.Verify.Summary);
            var px = QrVerifier.LoadBgra(path, out int w, out int h);
            Assert.Equal(Url, QrVerifier.Decode(px!, w, h));
        }
        finally { try { System.IO.File.Delete(path); } catch { } }
    }

    [Fact]
    public void PreferredMask_IsUsed_WhenItScans()
    {
        const int side = 400;
        for (int mask = 0; mask < 8; mask++)
        {
            var px = Enumerable.Repeat(0xFFFFFFFFu, side * side).ToArray();
            var (code, layout) = QrExport.Apply(px, side, side, new QrCodeOptions { Payload = Url, Mask = mask });
            if (QrVerifier.Verify(px, side, side, Url, layout.ModulePixels).Ok) Assert.Equal(mask, code.Mask);
        }
        Assert.NotNull(QrExport.Validate(new QrCodeOptions { Payload = Url, Mask = 8 }));
    }

    // ── batch + builder parity ─────────────────────────────────────────

    [Fact]
    public void Builder_EmitsTheMask_AndItRoundTrips()
    {
        var qr = new QrCodeOptions { Payload = Url, Mask = 5 };
        var args = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(new FracturingFog.Cli.BatchCommandSnapshot { Qr = qr })
            .Args.Select(a => a == "<OUTPUT.png>" ? "o.png" : a).ToArray();
        Assert.Contains("--qr-mask", args);
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args, 0, out var opts, out var err), err);
        Assert.Equal(qr, opts.BuildQrOptions());
    }

    [Fact]
    public void Parser_SearchAndExploreConflict()
    {
        var args = new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--qr-payload", "x",
                           "--qr-search", "seed=2", "--explore", "seed=1" };
        Assert.False(FracturingFog.Batch.BatchOptions.TryParse(args, 0, out _, out var err));
        Assert.Contains("--qr-search", err);
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args.Take(12).ToArray(), 0, out var o, out err), err);
        Assert.Equal(2, o.QrSearch!.Seed);
    }

    // ── Control Center ─────────────────────────────────────────────────

    [Fact]
    public async Task ViewModel_FindView_NavigatesAndKeepsTheMask_UntilTheCodeChanges()
    {
        FracturingFog.Abstractions.Explore.ExploreView? went = null;
        var target = new FracturingFog.Abstractions.Explore.ExploreView(new FracturingFog.FFMath.DeepComplex(-0.5, 0.25), 40);
        var vm = new FracturingFog.UI.Avalonia.ViewModels.QrCodeViewModel
        {
            Preflight = QrExport.Preflight,
            SearchRequested = (_, _) => Task.FromResult<FracturingFog.UI.Avalonia.ViewModels.QrSearchOutcome?>(
                new FracturingFog.UI.Avalonia.ViewModels.QrSearchOutcome(target, 6, "Found.")),
            NavigateRequested = v => went = v,
            Payload = Url,
        };
        await System.Reactive.Threading.Tasks.TaskObservableExtensions.ToTask(vm.FindViewCommand.Execute());
        Assert.Equal(target, went);
        Assert.Equal(6, vm.Mask);
        Assert.Equal(6, vm.Options!.Mask);
        vm.StyleIndex = 1;                 // style does not change the code
        Assert.Equal(6, vm.Mask);
        vm.Payload = Url + "/x";           // a new code: the match no longer applies
        Assert.Equal(-1, vm.Mask);
    }
}
