// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1330 — FractalRenderHost renders deep CPU frames of the main view on the
// calculator's anchored lattice and reuses the pixels across pans. Through a
// real host (null renderer), Standard quality, a deep view:
//   • after a pan the frame reused pixels, and it is byte-identical to a fresh
//     lattice render at the panned offset from the first frame's anchor;
//   • it shows the same picture as an ordinary render at the new view centre;
//   • the calculator carries the VIEW centre again after the frame (status,
//     regions, exports and the preview mirrors read it);
//   • High quality (MSAA + TAA) doesn't take the lattice; PanPixelReuse off
//     neither.

using System;
using System.Linq;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // PanPixelReuse is static
public sealed class S1330PanReuseHostTests(ITestOutputHelper output)
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

    private const int W = 160, H = 120;
    private const double Zoom = 1e15;
    private static readonly OD Cx = new(-1.1726999042772253, 8.9529605787776783E-17, 0, 0, 0, 0, 0, 0);
    private static readonly OD Cy = new(-0.2968356710071185, -2.3536240906562374E-18, 0, 0, 0, 0, 0, 0);

    private static void SetCentre(FractalViewState s, OD x, OD y)
    {
        s.CenterX = x.X0; s.CenterXLo = x.X1; s.CenterX2 = x.X2; s.CenterX3 = x.X3;
        s.CenterX4 = x.X4; s.CenterX5 = x.X5; s.CenterX6 = x.X6; s.CenterX7 = x.X7;
        s.CenterY = y.X0; s.CenterYLo = y.X1; s.CenterY2 = y.X2; s.CenterY3 = y.X3;
        s.CenterY4 = y.X4; s.CenterY5 = y.X5; s.CenterY6 = y.X6; s.CenterY7 = y.X7;
    }

    private static void Frame(FractalRenderHost host)
    {
        var done = new ManualResetEventSlim(false);   // not disposed: a late event may still Set it
        EventHandler<RenderFrameInfo> h = (_, _) => done.Set();
        host.FrameCompleted += h;
        try
        {
            host.Trigger(progressive: false);
            Assert.True(done.Wait(TimeSpan.FromSeconds(120)), "no frame");
        }
        finally { host.FrameCompleted -= h; }
    }

    private static FractalViewState State(QualityPreset q)
    {
        var s = new FractalViewState { FractalType = FractalType.Mandelbrot, Zoom = Zoom, Quality = q, PreferredIterations = 3000 };
        SetCentre(s, Cx, Cy);
        return s;
    }

    private static MandelbrotCalculator Reference(int ox, int oy, bool lattice, OD cx, OD cy, IColorMap palette) => new(W, H)
    {
        Quality = QualityPreset.Standard, MaxIterations = 3000, Zoom = Zoom, ColorMap = palette,
        CenterX = cx.X0, CenterXLo = cx.X1, CenterY = cy.X0, CenterYLo = cy.X1,
        ImageWidth = lattice ? W : 0, ImageHeight = lattice ? H : 0,
        SubRectOffsetX = ox, SubRectOffsetY = oy, LatticeAligned = lattice,
    };

    [Fact]
    public void A_Deep_Pan_Reuses_Pixels_And_Matches_A_Fresh_Lattice_Render()
    {
        bool saved = FractalRenderHost.PanPixelReuse;
        FractalRenderHost.PanPixelReuse = true;
        try
        {
            var state = State(QualityPreset.Standard);
            using var host = new FractalRenderHost(new NullRenderer(), state, W, H, ColorPalette.BuiltIns[0]);
            Frame(host);
            Assert.False(host.LastFrameReusedPanPixels, "the first frame has nothing to reuse");
            double scale = (3.5 / Math.Max(W, H)) / Zoom;
            // Two pans, the second with a sub-pixel remainder (snapped to the lattice).
            var steps = new[] { (7.0, -3.0), (-4.4, 9.3) };
            double px = 0, py = 0;
            foreach (var (sx, sy) in steps)
            {
                px += sx; py += sy;
                SetCentre(state, OD.FromCenterOffset(Cx, px, scale), OD.FromCenterOffset(Cy, py, scale));
                Frame(host);
                Assert.True(host.LastFrameReusedPanPixels, $"pan ({sx},{sy}) didn't reuse pixels");
                var calc = host.Mandelbrot;
                Assert.Equal(state.CenterX, calc.CenterX);       // the view centre is back
                Assert.Equal(state.CenterXLo, calc.CenterXLo);
                Assert.Equal(0, calc.SubRectOffsetX);
                Assert.False(calc.LatticeAligned);

                int ox = (int)Math.Round(px), oy = (int)Math.Round(py);
                var fresh = Reference(ox, oy, lattice: true, Cx, Cy, calc.ColorMap);
                fresh.LatticeMaxOffset = Math.Max(W, H);
                fresh.Calculate(CancellationToken.None);
                int n = W * H;
                int bad = Enumerable.Range(0, n).Count(i => calc.ColorBuffer[i] != fresh.ColorBuffer[i]
                                                            || calc.IterationBuffer[i] != fresh.IterationBuffer[i]);
                Assert.True(bad == 0, $"pan to ({px},{py}): {bad} pixels differ from a fresh lattice render");

                // Independent: an ordinary render at the snapped centre shows the same picture.
                var normal = Reference(0, 0, lattice: false, OD.FromCenterOffset(Cx, ox, scale), OD.FromCenterOffset(Cy, oy, scale), calc.ColorMap);
                normal.Calculate(CancellationToken.None);
                int differ = Enumerable.Range(0, n).Count(i => calc.IterationBuffer[i] != normal.IterationBuffer[i]);
                int distinct = normal.IterationBuffer.Take(n).Distinct().Count();
                output.WriteLine($"pan to ({px:F1},{py:F1}) → offset ({ox},{oy}): reused, {differ}/{n} differ from a normal render, {distinct} distinct");
                Assert.True(distinct > 20, "precondition: a busy frame");
                Assert.True(differ <= n / 200, $"{differ}/{n} pixels differ from a normal render");
            }
        }
        finally { FractalRenderHost.PanPixelReuse = saved; }
    }

    [Fact]
    public void Msaa_Quality_And_The_Switch_Keep_The_Normal_Path()
    {
        bool saved = FractalRenderHost.PanPixelReuse;
        try
        {
            foreach (var (q, on) in new[] { (QualityPreset.High, true), (QualityPreset.Standard, false) })
            {
                FractalRenderHost.PanPixelReuse = on;
                var state = State(q);
                using var host = new FractalRenderHost(new NullRenderer(), state, W, H, ColorPalette.BuiltIns[0]);
                Frame(host);
                double scale = (3.5 / Math.Max(W, H)) / Zoom;
                SetCentre(state, OD.FromCenterOffset(Cx, 5, scale), Cy);
                Frame(host);
                Assert.False(host.LastFrameReusedPanPixels, $"{q.Name}, switch {on}: reused pixels");
            }
        }
        finally { FractalRenderHost.PanPixelReuse = saved; }
    }
}
