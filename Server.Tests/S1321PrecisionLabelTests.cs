// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1321 — the status bar / perf HUD read "DD" for every deep Mandelbrot frame:
// the host collapsed MandelbrotCalculator to IsHighPrecisionActive ? "DD" : "SP",
// though past 1e25 the reference orbit is QD and past 1e50 OD. The calculator
// now reports the tier of the reference orbit it actually built (SP / DD-PT /
// QD-PT / OD-PT), for the plain and the orbit-aware (trap / stripe) paths, and
// the host passes it through FrameCompleted.

using System;
using System.Threading;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1321PrecisionLabelTests
{
    // A point well outside the set: every pixel escapes fast at any depth, so
    // the test is about the precision path, not iteration cost.
    private const double Cx = 0.4, Cy = 0.4;

    private static MandelbrotCalculator Calc(double zoom, IColorMap map) => new(32, 24)
    {
        Quality = QualityPreset.Extreme,
        CenterX = Cx, CenterY = Cy, Zoom = zoom, MaxIterations = 500, ColorMap = map,
    };

    [Theory]
    [InlineData(1e3, "SP")]
    [InlineData(1e20, "DD-PT")]    // under the 1e25 QD threshold
    [InlineData(1e40, "QD-PT")]    // between 1e25 and the 1e50 OD threshold
    [InlineData(1e60, "OD-PT")]
    [InlineData(1e90, "OD-PT")]
    public void Label_Names_The_Tier_The_Frame_Ran(double zoom, string expected)
    {
        var plain = Calc(zoom, new HsvPalette());
        plain.Calculate(CancellationToken.None);
        Assert.Equal(expected, plain.LastPrecisionLabel);

        // The orbit-aware path (trap / stripe themes) builds its own reference orbit.
        var trap = Calc(zoom, new OrbitTrapPointMap());
        trap.Calculate(CancellationToken.None);
        Assert.Equal(expected, trap.LastPrecisionLabel);
    }

    [Fact]
    public void Label_Follows_The_View_Back_Up()
    {
        var c = Calc(1e60, new HsvPalette());
        c.Calculate(CancellationToken.None);
        Assert.Equal("OD-PT", c.LastPrecisionLabel);
        c.Zoom = 10;
        c.Calculate(CancellationToken.None);
        Assert.Equal("SP", c.LastPrecisionLabel);   // no stale deep label
    }

    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    [Fact]
    public void Status_Bar_Label_Reads_OD_At_1e60()
    {
        var state = new FractalViewState
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = Cx, CenterY = Cy, Zoom = 1e60, Quality = QualityPreset.Extreme,
            PreferredIterations = 500,
        };
        using var host = new FractalRenderHost(new NullRenderer(), state, 64, 48, ColorPalette.BuiltIns[0]);
        string? label = null;
        var done = new ManualResetEventSlim(false);   // not disposed: a late event may still Set it
        host.FrameCompleted += (_, i) => { label = i.PrecisionLabel; done.Set(); };
        host.Trigger(progressive: false);
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no frame");
        Assert.Equal("OD-PT", label);
    }
}
