// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1012 (S5 of #1014) — stereo in the batch CLI: flags parse and validate, land
// on the lighting block, round-trip from the Command builder's live seed, and
// the batch still / zoom video actually write stereo frames.
public class BatchStereoTests
{
    // A valid view, so a failure can only come from the flag under test.
    private static readonly string[] View = { "--x", "0", "--y", "0", "--zoom", "1" };

    private static BatchOptions Parse(params string[] flags)
    {
        var argv = new[] { "FracturingFog", "--batch" }.Concat(View).Concat(flags).ToArray();
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        return opts;
    }

    private static string? ParseError(params string[] flags)
    {
        var argv = new[] { "FracturingFog", "--batch" }.Concat(View).Concat(flags).ToArray();
        return BatchOptions.TryParse(argv, startIndex: 2, out _, out var err) ? null : err;
    }

    [Fact]
    public void Flags_LandOnTheLightingBlock()
    {
        var o = Parse("--stereo", "true", "--stereo-eye-sep", "0.1", "--stereo-convergence", "0.04",
                      "--stereo-layout", "half", "--stereo-swap-eyes", "--out", "x.png");
        var fx = BatchStereo.ApplyFlags(LightingFxData.CreateDefault(), o);
        Assert.Equal((StereoMode.True, 0.1, 0.04, StereoLayout.HalfSbs, true),
            (fx.StereoMode, fx.StereoEyeSeparation, fx.StereoConvergence, fx.StereoLayout, fx.StereoSwapEyes));

        var a = Parse("--stereo", "autostereogram", "--autostereo-pattern", "texture", "--autostereo-eye-sep", "0.2",
                      "--autostereo-depth", "0.5", "--autostereo-smoothing", "0", "--autostereo-levels", "0",
                      "--autostereo-cross-eyed", "--autostereo-no-guide-dots", "--autostereo-seed", "9", "--out", "x.png");
        var ax = BatchStereo.ApplyFlags(LightingFxData.CreateDefault(), a);
        Assert.Equal((StereoMode.Autostereogram, AutostereoPattern.FractalTexture, 0.2, 0.5, 0, 0, true, false, 9),
            (ax.StereoMode, ax.StereoAutoPattern, ax.StereoAutoEyeSep, ax.StereoAutoDepthOfField,
             ax.StereoAutoBlur, ax.StereoAutoLevels, ax.StereoAutoCrossEyed, ax.StereoAutoGuideDots, ax.StereoAutoSeed));
    }

    [Fact]
    public void APairWithoutAnEyeSeparation_GetsTheDefault()
    {
        var fx = BatchStereo.ApplyFlags(LightingFxData.CreateDefault(), Parse("--stereo", "fake", "--out", "x.png"));
        Assert.Equal(0.06, fx.StereoEyeSeparation);
    }

    [Theory]
    [InlineData("--stereo", "sideways")]
    [InlineData("--stereo-layout", "quarter")]
    [InlineData("--autostereo-pattern", "stripes")]
    [InlineData("--stereo-eye-sep", "0.5")]
    [InlineData("--stereo-convergence", "0.3")]
    [InlineData("--autostereo-eye-sep", "0.01")]
    [InlineData("--autostereo-levels", "65")]
    public void BadValues_AreRejected(string flag, string value)
    {
        string? err = ParseError(flag, value, "--out", "x.png");
        Assert.NotNull(err);
        Assert.Contains(flag, err);   // rejected for this flag, not something else
    }

    // ── Command builder live seed → batch ────────────────────────────────

    private static LightingFxData RoundTrip(LightingFxData live, out IReadOnlyList<string> args)
    {
        var fp = new FractalParameters { Lighting = live };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot { Fractal = FractalType.Mandelbulb, Parameters = fp });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("Stereo", StringComparison.OrdinalIgnoreCase));
        args = report.Args;
        var flags = report.Args.Select(a => a == "<OUTPUT.png>" ? "out.png" : a).ToArray();
        var argv = new[] { "FracturingFog", "--batch" }.Concat(flags).ToArray();
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        return BatchStereo.ApplyFlags(LightingFxData.CreateDefault(), opts);
    }

    [Fact]
    public void Builder_RoundTrips_ASideBySidePair()
    {
        var live = LightingFxData.CreateDefault();
        live.StereoMode = StereoMode.Fake; live.StereoEyeSeparation = 0.08; live.StereoConvergence = -0.05;
        live.StereoMaxDisparity = 0.05; live.StereoFovDegrees = 75; live.StereoLayout = StereoLayout.HalfSbs; live.StereoSwapEyes = true;
        var back = RoundTrip(live, out _);
        Assert.Equal((live.StereoMode, live.StereoEyeSeparation, live.StereoConvergence, live.StereoMaxDisparity,
                      live.StereoFovDegrees, live.StereoLayout, live.StereoSwapEyes),
                     (back.StereoMode, back.StereoEyeSeparation, back.StereoConvergence, back.StereoMaxDisparity,
                      back.StereoFovDegrees, back.StereoLayout, back.StereoSwapEyes));
    }

    [Fact]
    public void Builder_RoundTrips_AnAutostereogram()
    {
        var live = LightingFxData.CreateDefault();
        live.StereoMode = StereoMode.Autostereogram; live.StereoAutoPattern = AutostereoPattern.ThemeDots;
        live.StereoAutoEyeSep = 0.15; live.StereoAutoDepthOfField = 0.4; live.StereoAutoBlur = 0; live.StereoAutoLevels = 0;
        live.StereoAutoCrossEyed = true; live.StereoAutoGuideDots = false; live.StereoAutoSeed = 42;
        var back = RoundTrip(live, out _);
        Assert.Equal((live.StereoMode, live.StereoAutoPattern, live.StereoAutoEyeSep, live.StereoAutoDepthOfField,
                      live.StereoAutoBlur, live.StereoAutoLevels, live.StereoAutoCrossEyed, live.StereoAutoGuideDots, live.StereoAutoSeed),
                     (back.StereoMode, back.StereoAutoPattern, back.StereoAutoEyeSep, back.StereoAutoDepthOfField,
                      back.StereoAutoBlur, back.StereoAutoLevels, back.StereoAutoCrossEyed, back.StereoAutoGuideDots, back.StereoAutoSeed));
    }

    // A live pair with no eye separation renders mono, so nothing is emitted
    // (the batch would otherwise default a separation and render a pair).
    [Fact]
    public void Builder_APairWithNoEyeSeparation_EmitsNothing()
    {
        var live = LightingFxData.CreateDefault();
        live.StereoMode = StereoMode.True;
        RoundTrip(live, out var args);
        Assert.DoesNotContain("--stereo", args);
    }

    // An older preset's zero autostereo fields render with the defaults live;
    // the seed must not emit out-of-range flags for them.
    [Fact]
    public void Builder_ZeroAutostereoFields_EmitValidFlags()
    {
        var live = default(LightingFxData);
        live.StereoMode = StereoMode.Autostereogram;
        var back = RoundTrip(live, out var args);
        Assert.Equal(StereoMode.Autostereogram, back.StereoMode);
        Assert.DoesNotContain("--autostereo-eye-sep", args);
        Assert.DoesNotContain("--autostereo-depth", args);
    }

    // ── The batch video's stereo frame plan ──────────────────────────────
    // (BatchRenderer lives in the WinExe the test project can't reference; the
    // still / video paths are exe-smoked. The plan + post step are tested here.)

    private static LightingFxData Mode(StereoMode m, StereoLayout layout = StereoLayout.FullSbs)
    {
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = m; fx.StereoEyeSeparation = 0.08; fx.StereoLayout = layout;
        return fx;
    }

    [Theory]
    [InlineData(StereoMode.True, StereoLayout.FullSbs, BatchStereoKind.True3D, 128, 48)]
    [InlineData(StereoMode.True, StereoLayout.HalfSbs, BatchStereoKind.True3D, 64, 48)]
    [InlineData(StereoMode.Fake, StereoLayout.FullSbs, BatchStereoKind.Depth3D, 128, 48)]
    [InlineData(StereoMode.Autostereogram, StereoLayout.FullSbs, BatchStereoKind.Depth3D, 64, 48)]
    public void Plan_3D_FrameSizeByMode(StereoMode m, StereoLayout layout, BatchStereoKind kind, int fw, int fh)
    {
        var plan = BatchStereo.For(FractalType.Mandelbulb, false, Mode(m, layout), 64, 48)!;
        Assert.Equal((kind, fw, fh), (plan.Kind, plan.FrameW, plan.FrameH));
    }

    [Fact]
    public void Plan_Relief_UsesTheWarp_Flat2D_HasNone()
    {
        Assert.Equal(BatchStereoKind.DepthRelief, BatchStereo.For(FractalType.Mandelbrot, true, Mode(StereoMode.True), 64, 48)!.Kind);
        Assert.Null(BatchStereo.For(FractalType.Mandelbrot, false, Mode(StereoMode.Fake), 64, 48));
        Assert.Null(BatchStereo.For(FractalType.Mandelbulb, false, LightingFxData.CreateDefault(), 64, 48));
    }

    [Fact]
    public void Finish_DepthFrame_IsTheStereoFrame_OrThePaddedMono()
    {
        const int w = 64, h = 48;
        var calc = new global::FracturingFog.MandelbulbCalculator(w, h) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0 };
        var fx = Mode(StereoMode.Fake);
        calc.FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, Lighting = fx };
        calc.Calculate(default);
        var plan = BatchStereo.For(FractalType.Mandelbulb, false, fx, w, h)!;

        var sbs = plan.Finish((uint[])calc.ColorBuffer.Clone(), w, h, calc.DepthBuffer, null, in fx);
        Assert.Equal(plan.FrameW * plan.FrameH, sbs.Length);

        var padded = plan.Finish((uint[])calc.ColorBuffer.Clone(), w, h, null, null, in fx);   // no depth came back
        Assert.Equal(plan.FrameW * plan.FrameH, padded.Length);
        Assert.Equal(calc.ColorBuffer[0], padded[0]);                 // mono at the left
        Assert.Equal(0xFF000000u, padded[w]);                          // black beside it
    }
}
