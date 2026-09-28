// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1013 (S6 of #1014) — animated autostereograms: centre-anchored colouring
// keeps a depth change local, and an AutostereoSequence holds the pattern and
// smooths the depth across a video's frames.
public class AutostereoAnimationTests
{
    private const int W = 600, H = 6, E = 120;
    private static readonly uint[] Palette16 =
        Enumerable.Range(0, 16).Select(i => 0xFF000000u | (uint)(i * 0x0F0F0F)).ToArray();
    private static AutostereoOptions Opts() => new() { EyeSeparationPx = E, DotColors = Palette16, Seed = 3 };

    // A raised block at columns [x0, x0 + 40) on a flat background.
    private static float[] Block(int x0)
    {
        var z = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = x0; x < x0 + 40 && x < W; x++) z[y * W + x] = 0.8f;
        return z;
    }

    // Independent locality invariant: when the depth changes only at columns
    // >= a, right of the centre, every pixel more than a far-plane separation
    // left of a keeps its colour (its chains' centre-most members are unchanged).
    // The paper's right-to-left colouring repaints the whole row left of the change.
    [Fact]
    public void ADepthChange_RightOfCentre_LeavesEverythingToItsLeft()
    {
        var before = Autostereogram.Render(Block(420), W, H, Opts());
        var after  = Autostereogram.Render(Block(440), W, H, Opts());   // block moved right
        int sFar = Autostereogram.FarSeparation(Opts());
        int limit = 420 - sFar;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < limit; x++)
                Assert.True(before[y * W + x] == after[y * W + x], $"pixel ({x},{y}) left of the change repainted");
    }

    [Fact]
    public void ADepthChange_LeftOfCentre_LeavesEverythingToItsRight()
    {
        var before = Autostereogram.Render(Block(120), W, H, Opts());
        var after  = Autostereogram.Render(Block(100), W, H, Opts());   // block moved left
        int sFar = Autostereogram.FarSeparation(Opts());
        int start = 160 + sFar;
        for (int y = 0; y < H; y++)
            for (int x = start; x < W; x++)
                Assert.True(before[y * W + x] == after[y * W + x], $"pixel ({x},{y}) right of the change repainted");
    }

    // ── AutostereoSequence ───────────────────────────────────────────────

    private static LightingFxData Fx(AutostereoPattern pattern = AutostereoPattern.FractalTexture, double temporal = 0.5)
    {
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = StereoMode.Autostereogram;
        fx.StereoAutoPattern = pattern;
        fx.StereoAutoGuideDots = false;
        fx.StereoAutoBlur = 0;
        fx.StereoAutoLevels = 0;
        fx.StereoAutoTemporal = temporal;
        return fx;
    }

    private static (uint[] Mono, float[] Ray) Frame(uint tint, float dist)
    {
        var mono = new uint[W * H];
        var ray = new float[W * H];
        for (int i = 0; i < mono.Length; i++)
        {
            int x = i % W;
            mono[i] = 0xFF000000u | tint ^ (uint)(x * 2654435761u >> 8) & 0xFFFFFFu;
            ray[i] = x >= 200 && x < 400 ? dist : float.PositiveInfinity;
        }
        return (mono, ray);
    }

    // In a video the pattern comes from the first frame: a new frame with a
    // different image but the same depth draws the same stereogram.
    [Theory]
    [InlineData(AutostereoPattern.FractalTexture)]
    [InlineData(AutostereoPattern.ThemeDots)]
    public void Sequence_HoldsThePatternAcrossFrames(AutostereoPattern pattern)
    {
        var fx = Fx(pattern, temporal: 0.0);
        var seq = new AutostereoSequence();
        var (m1, r) = Frame(0x112233, 2f);
        var (m2, _) = Frame(0x998877, 2f);
        var first = seq.Frame(m1, r, W, H, in fx, temporal: true);
        var second = seq.Frame(m2, r, W, H, in fx, temporal: true);
        Assert.Equal(first, second);
        Assert.Equal(2, seq.FramesSinceReset);

        // A still (not a sequence) takes the pattern from its own frame.
        var still = new AutostereoSequence().Frame(m2, r, W, H, in fx, temporal: false);
        Assert.NotEqual(first, still);
    }

    // The depth blend is exactly the exponential moving average, applied before
    // the depth levels / cross-eyed steps.
    [Fact]
    public void Sequence_BlendsTheDepth_ByTheTemporalSetting()
    {
        var fx = Fx(AutostereoPattern.RandomDots, temporal: 0.5);
        var (m, r1) = Frame(0x112233, 3f);
        var (_, r2) = Frame(0x112233, 1f);
        var seq = new AutostereoSequence();
        seq.Frame(m, r1, W, H, in fx, temporal: true);
        var blended = seq.Frame(m, r2, W, H, in fx, temporal: true);

        var (opts, depthOpts) = Autostereogram.OptionsFromLighting(m, r2, W, H, in fx, null, null);
        var z1 = Autostereogram.PrepareDepthContinuous(r1, W, H, depthOpts);
        var z2 = Autostereogram.PrepareDepthContinuous(r2, W, H, depthOpts);
        var z = z1.Zip(z2, (a, b) => 0.5f * a + 0.5f * b).ToArray();
        Autostereogram.FinishDepth(z, depthOpts);
        Assert.Equal(Autostereogram.Render(z, W, H, opts), blended);

        // Temporal 0 = each frame stands alone.
        var none = Fx(AutostereoPattern.RandomDots, temporal: 0.0);
        var seq0 = new AutostereoSequence();
        seq0.Frame(m, r1, W, H, in none, temporal: true);
        Assert.Equal(Autostereogram.FromLighting(m, r2, W, H, in none), seq0.Frame(m, r2, W, H, in none, temporal: true));
    }

    [Fact]
    public void Sequence_StartsOver_WhenThePatternSettingsChange()
    {
        var fx = Fx();
        var seq = new AutostereoSequence();
        var (m, r) = Frame(0x112233, 2f);
        seq.Frame(m, r, W, H, in fx, temporal: true);
        seq.Frame(m, r, W, H, in fx, temporal: true);
        fx.StereoAutoEyeSep = 0.2;
        seq.Frame(m, r, W, H, in fx, temporal: true);
        Assert.Equal(1, seq.FramesSinceReset);
    }

    // End to end on a real moving fractal: across an orbiting Mandelbulb, the
    // held-pattern sequence repaints far fewer pixels per frame than stateless
    // frames (which re-cut the fractal texture every frame).
    [Fact]
    public void OrbitingMandelbulb_Sequence_FlickersLess()
    {
        const int w = 240, h = 160;
        var fx = Fx(AutostereoPattern.FractalTexture, temporal: 0.5);
        (uint[], float[]) Render(double theta)
        {
            var c = new global::FracturingFog.MandelbulbCalculator(w, h) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0 };
            c.FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, BulbCameraTheta = theta, Lighting = fx };
            c.Calculate(default);
            return ((uint[])c.ColorBuffer.Clone(), c.DepthBuffer!);
        }
        var frames = Enumerable.Range(0, 4).Select(i => Render(0.785 + i * 0.02)).ToArray();

        double Changed(Func<int, uint[]> make)
        {
            int changed = 0, total = 0;
            var prev = make(0);
            for (int i = 1; i < frames.Length; i++)
            {
                var cur = make(i);
                changed += prev.Zip(cur, (a, b) => a != b ? 1 : 0).Sum();
                total += cur.Length;
                prev = cur;
            }
            return changed / (double)total;
        }

        double stateless = Changed(i => Autostereogram.FromLighting(frames[i].Item1, frames[i].Item2, w, h, in fx));
        var seq = new AutostereoSequence();
        var held = Enumerable.Range(0, frames.Length).Select(i => seq.Frame(frames[i].Item1, frames[i].Item2, w, h, in fx, temporal: true)).ToArray();
        double sequence = Changed(i => held[i]);
        Assert.True(sequence < stateless * 0.8, $"sequence {sequence:P1} vs stateless {stateless:P1} pixels changed per frame");
    }

    // ── Batch parity ─────────────────────────────────────────────────────

    [Fact]
    public void TemporalFlag_RoundTripsThroughTheBuilder()
    {
        var live = LightingFxData.CreateDefault();
        live.StereoMode = StereoMode.Autostereogram;
        live.StereoAutoTemporal = 0.8;
        var fp = new FractalParameters { Lighting = live };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot { Fractal = FractalType.Mandelbulb, Parameters = fp });
        Assert.Contains("--autostereo-temporal", report.Args);
        var argv = new[] { "FracturingFog", "--batch" }
            .Concat(report.Args.Select(a => a == "<OUTPUT.png>" ? "out.png" : a)).ToArray();
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        Assert.Equal(0.8, BatchStereo.ApplyFlags(LightingFxData.CreateDefault(), opts).StereoAutoTemporal);

        var bad = new[] { "FracturingFog", "--batch", "--x", "0", "--y", "0", "--zoom", "1",
                          "--autostereo-temporal", "0.95", "--out", "x.png" };
        Assert.False(BatchOptions.TryParse(bad, startIndex: 2, out _, out var badErr));
        Assert.Contains("--autostereo-temporal", badErr);
    }
}
