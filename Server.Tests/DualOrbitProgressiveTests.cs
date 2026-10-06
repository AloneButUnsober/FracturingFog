// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1146 — Dual-Orbit Escape gets the host's Wave 2.5 progressive chain
// (¼ → ½ → full) during interaction, like Mandelbrot. Driven through a real
// FractalRenderHost with a capturing renderer: the upload sequence is the
// quarter, half, then full frame, and the final frame is byte-identical to a
// single-pass render. Types outside the set keep their single full render.
public sealed class DualOrbitProgressiveTests
{
    private const int W = 320, H = 256;

    private sealed class RecordingRenderer : IFractalRenderer
    {
        public readonly List<(int W, int H)> Uploads = new();
        public uint[]? Last;
        public void UpdateTexture(uint[] colorBuffer, int width, int height)
        {
            lock (Uploads) { Uploads.Add((width, height)); Last = colorBuffer.Take(width * height).ToArray(); }
        }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "record";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static (List<(int W, int H)> uploads, uint[] final) Run(FractalType type, bool progressive, FractalParameters? fp = null)
    {
        var renderer = new RecordingRenderer();
        var state = new FractalViewState { FractalType = type, FractalParameters = fp ?? new FractalParameters(), CenterX = -0.6, CenterY = 0, Zoom = 1.0 };
        using var host = new FractalRenderHost(renderer, state, W, H, ColorPalette.BuiltIns[0]);
        using var done = new ManualResetEventSlim();
        host.FrameCompleted += (_, _) => done.Set();
        host.Trigger(progressive: progressive);
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no frame completed");
        var snap = host.SnapshotFrame(out int sw, out int sh);
        Assert.Equal((W, H), (sw, sh));
        lock (renderer.Uploads) return (renderer.Uploads.ToList(), snap.Take(sw * sh).ToArray());
    }

    [Fact]
    public void DualOrbitEscape_IsInTheAlwaysProgressiveSet_AndHasAPreviewTwin()
    {
        Assert.True(FractalRenderHost.AlwaysProgressiveAlt(FractalType.JulibrotPair));
        Assert.IsType<DualOrbitEscapeCalculator>(FractalRenderHost.CreateReliefFieldCalc(FractalType.JulibrotPair, 64, 64));
        Assert.False(FractalRenderHost.AlwaysProgressiveAlt(FractalType.Julia));
        Assert.False(FractalRenderHost.AlwaysProgressiveAlt(FractalType.Mandelbrot));
    }

    [Fact]
    public void DualOrbitEscape_UploadsQuarterHalfThenFull_FinalMatchesSinglePass()
    {
        var fp = new FractalParameters { DualOrbitField = DualOrbitField.BasinEntropy, DualOrbitEnsembleN = 8 };
        var (uploads, final) = Run(FractalType.JulibrotPair, progressive: true, fp);
        (int, int) quarter = (Math.Max(64, W / 4), Math.Max(64, H / 4)), half = (Math.Max(64, W / 2), Math.Max(64, H / 2));
        int iq = uploads.IndexOf(quarter), ih = uploads.IndexOf(half), iF = uploads.LastIndexOf((W, H));
        Assert.True(iq >= 0 && ih > iq && iF > ih, $"uploads: {string.Join(", ", uploads)}");

        var (single, singleFinal) = Run(FractalType.JulibrotPair, progressive: false, fp.Clone());
        Assert.DoesNotContain(quarter, single);
        Assert.Equal(singleFinal, final);
    }

    [Fact]
    public void OtherAltTypes_KeepTheirSingleFullRender()
    {
        var (uploads, _) = Run(FractalType.Julia, progressive: true);   // relief off: not eligible
        Assert.All(uploads, u => Assert.Equal((W, H), u));
    }

    [Fact]
    public void Mandelbrot_StillRunsItsProgressiveChain()
    {
        var (uploads, _) = Run(FractalType.Mandelbrot, progressive: true);
        Assert.Contains((Math.Max(64, W / 4), Math.Max(64, H / 4)), uploads);
    }
}
