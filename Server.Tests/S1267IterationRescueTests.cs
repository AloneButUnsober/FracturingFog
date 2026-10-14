// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SM-1 (#1267) — a deep Mandelbrot view renders as one flat colour when its
// iteration cap is below the escape band (every pixel counts as in-set). The
// rescue climbs the cap on a probe and re-renders once pixels escape. These
// tests use a deep location from the original --regionprobe report that is
// SOLID at 300 iterations, and check the outcome by the iteration buffer.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1267IterationRescueTests
{
    // "Deep Lightning in Space" (zoom 7.58e26), from Program.cs --regionprobe.
    private static MandelbrotCalculator DeepLightning(int maxIter, int w = 64, int h = 48) => new(w, h)
    {
        CenterX = -1.4181949444785762, CenterXLo = -7.4415882477902279E-17,
        CenterY = -0.12700786443815276, CenterYLo = -2.3429499355532375E-18,
        Zoom = 7.58348E+26, MaxIterations = maxIter,
        Quality = QualityPreset.Standard,
        ColorMap = new HsvPalette(),
    };

    [Fact]
    public void Low_Cap_Deep_Frame_Is_All_InSet()
    {
        var c = DeepLightning(300);
        c.Calculate();
        Assert.True(IterationRescue.IsAllInSet(c));
        Assert.Single(c.ColorBuffer.Distinct());   // the reported symptom: one flat colour
    }

    [Fact]
    public void Rescue_Raises_The_Cap_Until_Pixels_Escape()
    {
        var c = DeepLightning(300);
        c.Calculate();
        int raised = IterationRescue.TryRescue(c, CancellationToken.None);

        Assert.True(raised > 300, $"raised = {raised}");
        Assert.Equal(raised, c.MaxIterations);
        // Independent check on the re-rendered frame: real escapes, real structure.
        Assert.Contains(c.IterationBuffer, it => it < c.MaxIterations);
        Assert.True(c.ColorBuffer.Distinct().Count() > 16);
    }

    [Fact]
    public void Probe_Result_Matches_A_Direct_Render_At_That_Cap()
    {
        var c = DeepLightning(300);
        c.Calculate();
        int found = IterationRescue.FindEscapingIterations(c, IterationRescue.Ceiling, CancellationToken.None);
        Assert.True(found > 300);

        // Not just the probe: a fresh full-size render at the found cap escapes.
        var at = DeepLightning(found); at.Calculate();
        Assert.False(IterationRescue.IsAllInSet(at));
    }

    [Fact]
    public void Frame_With_Escapes_Is_Left_Alone()
    {
        var c = DeepLightning(20000);
        c.Calculate();
        var before = (uint[])c.ColorBuffer.Clone();
        Assert.Equal(0, IterationRescue.TryRescue(c, CancellationToken.None));
        Assert.Equal(20000, c.MaxIterations);
        Assert.Equal(before, c.ColorBuffer);
    }

    [Fact]
    public void Genuinely_Interior_View_Finds_No_Escape_And_Is_Left_Alone()
    {
        // Deep inside the main cardioid: every point is in the set at any cap.
        var c = new MandelbrotCalculator(48, 48)
        {
            CenterX = -0.2, CenterY = 0.0, Zoom = 100, MaxIterations = 256,
            Quality = QualityPreset.Standard, ColorMap = new HsvPalette(),
        };
        c.Calculate();
        Assert.True(IterationRescue.IsAllInSet(c));
        Assert.Equal(0, IterationRescue.FindEscapingIterations(c, 20000, CancellationToken.None));
        Assert.Equal(0, IterationRescue.TryRescue(c, CancellationToken.None));
        Assert.Equal(256, c.MaxIterations);
    }

    [Fact]
    public void Cancelled_Climb_Returns_Zero()
    {
        var c = DeepLightning(300);
        c.Calculate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(0, IterationRescue.TryRescue(c, cts.Token));
        Assert.Equal(300, c.MaxIterations);
    }

    // ── Live view: FractalRenderHost ────────────────────────────────────────────

    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    /// <summary>Renders the deep view the way a recalled region does
    /// (PreferredIterations = its saved count) and returns the frame info and pixels.</summary>
    private static (RenderFrameInfo info, uint[] frame) HostRender(FractalViewState state, FractalRenderHost host)
    {
        RenderFrameInfo info = default;
        using var done = new ManualResetEventSlim();
        EventHandler<RenderFrameInfo> h = (_, i) => { info = i; done.Set(); };
        host.FrameCompleted += h;
        try
        {
            host.Trigger(progressive: false);
            Assert.True(done.Wait(TimeSpan.FromSeconds(120)), "no frame completed");
        }
        finally { host.FrameCompleted -= h; }
        var snap = host.SnapshotFrame(out int w, out int h2);
        return (info, snap.Take(w * h2).ToArray());
    }

    private static FractalViewState DeepLightningState(int savedIterations) => new()
    {
        FractalType = FractalType.Mandelbrot,
        CenterX = -1.4181949444785762, CenterXLo = -7.4415882477902279E-17,
        CenterY = -0.12700786443815276, CenterYLo = -2.3429499355532375E-18,
        Zoom = 7.58348E+26, Quality = QualityPreset.Standard,
        PreferredIterations = savedIterations,
    };

    [Fact]
    public void Host_Rescues_A_Region_Saved_With_Too_Few_Iterations()
    {
        var state = DeepLightningState(300);
        using var host = new FractalRenderHost(new NullRenderer(), state, 96, 64, ColorPalette.BuiltIns[0]);
        var (info, frame) = HostRender(state, host);
        Assert.True(info.Iterations > 300, $"reported {info.Iterations}");
        Assert.True(frame.Distinct().Count() > 16, "frame is still flat");

        // A re-render of the same view (theme / FX change) keeps the raised cap.
        var (again, _) = HostRender(state, host);
        Assert.Equal(info.Iterations, again.Iterations);
    }

    [Fact]
    public void Host_Leaves_A_Locked_Iteration_Count_Alone()
    {
        var state = DeepLightningState(0);
        state.IterLocked = true;
        state.LockedIterations = 300;
        using var host = new FractalRenderHost(new NullRenderer(), state, 96, 64, ColorPalette.BuiltIns[0]);
        var (info, frame) = HostRender(state, host);
        Assert.Equal(300, info.Iterations);
        Assert.True(frame.Distinct().Count() <= 2, "a locked count must render as asked");
    }

    [Fact]
    public void Per_Row_Caps_Count_As_InSet_At_Their_Own_Cap()
    {
        var c = new MandelbrotCalculator(4, 2) { MaxIterations = 100 };
        c.PerRowMaxIter = new[] { 50, 100 };
        c.IterationBuffer[0] = 50; c.IterationBuffer[1] = 50; c.IterationBuffer[2] = 50; c.IterationBuffer[3] = 50;
        c.IterationBuffer[4] = 100; c.IterationBuffer[5] = 100; c.IterationBuffer[6] = 100; c.IterationBuffer[7] = 100;
        Assert.True(IterationRescue.IsAllInSet(c));
        c.IterationBuffer[6] = 99;
        Assert.False(IterationRescue.IsAllInSet(c));
    }
}
