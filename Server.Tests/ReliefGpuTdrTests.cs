// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1044 — the relief GPU dispatch with volumetric fog ran for seconds on a slow GPU
// (GeForce GT 710) and tripped the ~2 s OS watchdog (TDR): DXGI_ERROR_DEVICE_REMOVED,
// unhandled → the app died. The dispatch is now tiled so a tile's predicted time
// stays near the target, and a lost device falls back to the CPU trace. (Tiling on
// the real GPU was checked on the GT 710: a 960×540 fog frame, ~3 s of GPU work,
// rendered in 23 tiles of ~125 ms with no watchdog reset.)

using System;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefGpuTdrTests
{
    private const double Gt710NsPerUnit = 3.3;   // measured on the GT 710 (Real height + fog)

    private static ReliefUniforms Uniforms(int w, int h, bool fog)
    {
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DTrueHeight = true };
        var fx = p.Lighting;
        fx.ShadowSteps = 24; fx.ShadowLightMask = 0x1; fx.AoSamples = 5;   // the auto-shade defaults
        if (fog)
        {
            fx.FogDensity = 0.5; fx.VolumeSteps = 32;
            fx.Light2.Intensity = 0.7; fx.Light3.Intensity = 0.5; fx.ShadowLightMask = 0x7;
        }
        return ReliefUniforms.Build(w, h, w, h, 0.35, (double)w / h, 0.1, 1.0, p, in fx);
    }

    private static double PredictedTileMs(int rows, int cols, double cost, double ns) => rows * cols * cost * ns * 1e-6;

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void FogFrame_TilesStayNearTheTarget_AtGt710Speed(int w, int h)
    {
        var u = Uniforms(w, h, fog: true);
        double cost = ReliefGpuTiling.CostPerPixel(in u);
        double wholeFrameMs = (double)w * h * cost * Gt710NsPerUnit * 1e-6;
        Assert.True(wholeFrameMs > 2000, $"precondition: one dispatch would take {wholeFrameMs:0} ms (over the watchdog)");

        var (rows, cols) = ReliefGpuTiling.TileSize(w, h, cost, Gt710NsPerUnit);
        Assert.Equal(0, rows % 8); Assert.Equal(0, cols % 8);
        Assert.InRange(rows, 8, h + 7); Assert.InRange(cols, 8, w + 7);
        double tileMs = PredictedTileMs(rows, cols, cost, Gt710NsPerUnit);
        Assert.True(tileMs <= ReliefGpuTiling.BandTargetMs * 1.01, $"tile {rows}×{cols} predicted {tileMs:0} ms");
    }

    // The cost model reads the uniforms, so switching fog on shrinks the tiles in
    // the same frame (a speed learned from a fog-free frame cannot size a fog frame
    // into one watchdog-length dispatch).
    [Fact]
    public void SwitchingFogOn_ShrinksTheTiles_InTheSameFrame()
    {
        var plain = Uniforms(1920, 1080, fog: false);
        var fog = Uniforms(1920, 1080, fog: true);
        double cPlain = ReliefGpuTiling.CostPerPixel(in plain), cFog = ReliefGpuTiling.CostPerPixel(in fog);
        Assert.True(cFog > cPlain * 5, $"fog cost {cFog:0} vs plain {cPlain:0}");
        var a = ReliefGpuTiling.TileSize(1920, 1080, cPlain, Gt710NsPerUnit);
        var b = ReliefGpuTiling.TileSize(1920, 1080, cFog, Gt710NsPerUnit);
        Assert.True(b.Rows * b.Cols < a.Rows * a.Cols, $"fog tile {b} not smaller than plain tile {a}");
    }

    [Fact]
    public void ExtremeCost_FallsBackToColumnTiles()
    {
        var (rows, cols) = ReliefGpuTiling.TileSize(3840, 2160, costPerPixel: 200_000, nsPerUnit: Gt710NsPerUnit);
        Assert.Equal(8, rows);
        Assert.True(cols < 3840 && cols % 8 == 0, $"cols {cols}");
        Assert.True(PredictedTileMs(rows, cols, 200_000, Gt710NsPerUnit) <= ReliefGpuTiling.BandTargetMs * 1.01);
    }

    // ── device loss ──────────────────────────────────────────────────────────

    private sealed class LostDeviceKernel : IReliefRaymarchKernel
    {
        public int Calls;
        public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst,
                        float[]? aovNormalXyz = null, float[]? aovDepth = null)
        {
            Calls++;
            Array.Fill(dst, 0xFFFF00FFu);   // a partial GPU write before the device died
            throw new GpuDeviceLostException("simulated DXGI_ERROR_DEVICE_REMOVED");
        }
        public void Dispose() { }
    }

    [Fact]
    public void LostDevice_FallsBackToTheCpuTrace_AndIsNotRetried()
    {
        int w = 160, h = 90;
        var c = new MandelbrotCalculator(w, h) { CenterX = -0.5, CenterY = 0, Zoom = 1, MaxIterations = 200, ColorMap = new MonoBandMap() };
        c.Calculate(default);
        var p = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DGpuRaymarch = true, Relief2DSupersample = 1,
            Relief2DTrueHeight = true,
        };
        var cpu = new uint[w * h];
        HeightfieldRaymarch2D.Render(c.ColorBuffer, c.SmoothBuffer, w, h, p, cpu);   // no kernel → CPU

        var kernel = new LostDeviceKernel();
        var viaLost = new uint[w * h];
        var ex = Record.Exception(() => HeightfieldRaymarch2D.Render(c.ColorBuffer, c.SmoothBuffer, w, h, p, viaLost, kernel));
        Assert.Null(ex);
        Assert.Equal(cpu, viaLost);
        Assert.True(HeightfieldRaymarch2D.IsGpuReliefLost(kernel));

        var again = new uint[w * h];
        HeightfieldRaymarch2D.Render(c.ColorBuffer, c.SmoothBuffer, w, h, p, again, kernel);
        Assert.Equal(1, kernel.Calls);   // the lost kernel is not dispatched again
        Assert.Equal(cpu, again);
        Assert.False(HeightfieldRaymarch2D.IsGpuReliefLost(new LostDeviceKernel()));   // per kernel, not global
    }
}
