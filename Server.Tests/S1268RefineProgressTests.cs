// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SM-3 (#1268) — FrameCompleted fires on the first full-res sample, while the
// TAA continuations keep refining the same view, so the status bar read as
// finished too early. The host now raises RefineProgress after each refinement
// upload (not done while another sample is queued, done once it settles).
// FrameCompleted stays once per view.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1268RefineProgressTests
{
    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static (int frames, List<RefineProgressInfo> refine) Run(QualityPreset quality, double zoom, TimeSpan settle)
    {
        var state = new FractalViewState
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.7453, CenterY = 0.1127, Zoom = zoom, Quality = quality,
        };
        using var host = new FractalRenderHost(new NullRenderer(), state, 64, 48, ColorPalette.BuiltIns[0]);
        int frames = 0;
        var refine = new List<RefineProgressInfo>();
        using var done = new ManualResetEventSlim(false);
        host.FrameCompleted += (_, _) => Interlocked.Increment(ref frames);
        host.RefineProgress += (_, e) => { lock (refine) refine.Add(e); if (e.Done) done.Set(); };
        host.Trigger(progressive: false);
        done.Wait(settle);
        // Let any stray late event land before reading.
        Thread.Sleep(200);
        lock (refine) return (frames, refine.ToList());
    }

    [Fact]
    public void Taa_View_Reports_Refining_Then_Done_Once_And_FrameCompleted_Stays_Single()
    {
        var q = QualityPreset.High;   // TaaMaxSamples = 8
        var (frames, refine) = Run(q, 1e3, TimeSpan.FromSeconds(60));

        Assert.Equal(1, frames);
        Assert.NotEmpty(refine);
        Assert.Single(refine, r => r.Done);
        var last = refine[^1];
        Assert.True(last.Done, "the last progress event must be the settle");
        Assert.Equal(q.TaaMaxSamples, last.Sample);
        Assert.All(refine, r => Assert.Equal(q.TaaMaxSamples, r.Total));
        Assert.All(refine.Take(refine.Count - 1), r => Assert.False(r.Done));
        // One event per sample in the image: 1 (the FrameCompleted frame) .. N.
        Assert.Equal(Enumerable.Range(1, q.TaaMaxSamples), refine.Select(r => r.Sample));
    }

    [Fact]
    public void View_Without_Taa_Raises_No_Refine_Progress()
    {
        var (frames, refine) = Run(QualityPreset.Standard, 1e3, TimeSpan.FromSeconds(2));
        Assert.Equal(1, frames);
        Assert.Empty(refine);
    }
}
