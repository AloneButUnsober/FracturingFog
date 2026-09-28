// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1027 (S2 of #1025) — the relief raymarch's hit tolerance anchor. Camera (default)
// is byte-identical; Uniform measures the tolerance at the terrain's nearest point
// and holds it, so terrain nearer than that is traced exactly as before and farther
// terrain is traced closer to the true surface. The reference for "true" is the same
// view rendered at 5× the resolution (a 5× tighter tolerance cone), sampled at the
// low-res pixel centres — independent of the anchor code.

using System;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefDetailAnchorTests
{
    private const int W = 160, H = 120, K = 5;          // K× reference (odd: pixel centres align)
    // Field = the low-res output grid, so the K× reference (whose output is finer
    // than the field) traces the very same field — no downsample on either side.
    private const int FW = W, FH = H;

    private static readonly Lazy<(uint[] Albedo, float[] Field)> s_mandel = new(() =>
    {
        var calc = new MandelbrotCalculator(FW, FH)
        {
            CenterX = -0.75, CenterY = 0.1, Zoom = 1.6, MaxIterations = 300,
            ColorMap = new MonoBandMap(),
        };
        calc.Calculate(default);
        return ((uint[])calc.ColorBuffer.Clone(), (float[])calc.SmoothBuffer.Clone());
    });

    // A lowish camera: the far terrain is about twice as far as the look-at point,
    // where the camera-anchored tolerance has grown the most.
    private static FractalParameters P(ReliefDetailAnchor anchor) => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DGpuRaymarch = false,
        Relief2DHeightScale = 0.8,
        Relief2DCameraAzimuthDeg = 20,
        Relief2DCameraElevationDeg = 30,
        Relief2DCameraFovDeg = 60,
        Relief2DGroundPlane = true,
        Relief2DSupersample = 1,
        Relief2DAutoShade = false,
        Relief2DDetailAnchor = anchor,
    };

    private static float[] Depth(int w, int h, FractalParameters p)
    {
        var (albedo, field) = s_mandel.Value;
        // Albedo only colours the hit; a flat one at the output size is enough.
        var alb = new uint[w * h];
        Array.Fill(alb, 0xFF808080u);
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h);
        HeightfieldRaymarch2D.Render(alb, field, w, h, FW, FH, p, new uint[w * h], out _, null, aov);
        return aov.Depth.Select(d => d > 0f && d < 9.9e5f ? d : float.NaN).ToArray();
    }

    // The cap for the test view. The camera and terrain box depend only on
    // sy·maxH = 0.35·HeightScale (Peak, full-size field), whatever the content.
    private static double CapDistance()
    {
        var p = P(ReliefDetailAnchor.Uniform);
        return HeightfieldRaymarch2D.BuildObliqueCamera(W, H, (double)W / H, 0.35 * p.Relief2DHeightScale, 1.0, p).ConeCapT;
    }

    [Fact]
    public void Camera_IsTheDefault_AndHasNoCap()
    {
        Assert.Equal(ReliefDetailAnchor.Camera, new FractalParameters().Relief2DDetailAnchor);
        var cam = HeightfieldRaymarch2D.BuildObliqueCamera(W, H, (double)W / H, 0.5, 1.0, P(ReliefDetailAnchor.Camera));
        Assert.Equal(0.0, cam.ConeCapT);
        Assert.Equal(cam.Eps0 + cam.PixelAngle * 7.5, cam.Tolerance(7.5));
    }

    [Fact]
    public void Uniform_CapsTheToleranceAtTheNearestTerrain_AndRaisesTheStepBudget()
    {
        var camA = HeightfieldRaymarch2D.BuildObliqueCamera(W, H, (double)W / H, 0.5, 1.0, P(ReliefDetailAnchor.Camera));
        var uni  = HeightfieldRaymarch2D.BuildObliqueCamera(W, H, (double)W / H, 0.5, 1.0, P(ReliefDetailAnchor.Uniform));
        // Same camera otherwise.
        Assert.Equal(camA.CamX, uni.CamX); Assert.Equal(camA.PixelAngle, uni.PixelAngle);
        // Cap = camera → nearest point of the terrain box (independent geometry).
        double nx = Math.Max(Math.Abs(camA.CamX) - camA.Bx, 0), nz = Math.Max(Math.Abs(camA.CamZ) - camA.Bz, 0);
        double ny = camA.CamY > camA.By ? camA.CamY - camA.By : 0;
        double near = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        Assert.True(near > 0, "test camera should sit outside the terrain box");
        Assert.Equal(near, uni.ConeCapT, 12);
        Assert.Equal(camA.Tolerance(near * 0.5), uni.Tolerance(near * 0.5));
        Assert.Equal(uni.Tolerance(near), uni.Tolerance(near * 4));
        Assert.True(uni.Tolerance(near * 4) < camA.Tolerance(near * 4));
        Assert.True(uni.MaxSteps > camA.MaxSteps);
    }

    [Fact]
    public void Uniform_WithOrtho_HasNoCap()
    {
        var p = P(ReliefDetailAnchor.Uniform);
        p.Relief2DCameraOrthographic = true;
        Assert.Equal(0.0, HeightfieldRaymarch2D.BuildObliqueCamera(W, H, (double)W / H, 0.5, 1.0, p).ConeCapT);
    }

    [Fact]
    public void Uniform_TracesNearTerrainIdentically_AndFarTerrainCloserToTheTruth()
    {
        var pc = P(ReliefDetailAnchor.Camera);
        var pt = P(ReliefDetailAnchor.Uniform);
        var camDepth = Depth(W, H, pc);
        var tgtDepth = Depth(W, H, pt);
        var reference = Depth(W * K, H * K, pc);   // 5× tighter cone everywhere
        double cap = CapDistance();

        int near = 0, far = 0;
        double errCam = 0, errTgt = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float c = camDepth[i], t = tgtDepth[i];
                float r = reference[(y * K + K / 2) * (W * K) + x * K + K / 2];
                if (!float.IsFinite(c) || !float.IsFinite(t) || !float.IsFinite(r)) continue;
                if (c < cap * 0.98)
                {
                    // Every march step stayed nearer than the cap: identical trace.
                    Assert.Equal(c, t);
                    near++;
                }
                else if (r > cap * 1.5)
                {
                    errCam += Math.Abs(c - r);
                    errTgt += Math.Abs(t - r);
                    far++;
                }
            }

        _ = near;   // usually none: the cap is the terrain's nearest point
        var fin = camDepth.Where(float.IsFinite).OrderBy(v => v).ToArray();
        Assert.True(far > W * H / 20, $"too few far pixels ({far}); cap {cap:G4}, depth p10 {fin[fin.Length/10]:G4} p50 {fin[fin.Length/2]:G4} p90 {fin[fin.Length*9/10]:G4} max {fin[^1]:G4}, finite {fin.Length}, fieldmax {s_mandel.Value.Field.Max():G4}, ref finite {reference.Count(float.IsFinite)}");
        errCam /= far; errTgt /= far;
        Assert.True(errTgt < errCam * 0.85,
            $"far depth error: uniform {errTgt:0.#####} vs camera {errCam:0.#####} over {far} px; differing {camDepth.Zip(tgtDepth, (a, b) => a != b ? 1 : 0).Sum()}, cap {cap:G4}");
    }

    // ── Batch / builder / region / dialog ───────────────────────────────────

    [Fact]
    public void Batch_ParsesTheAnchor_AndRejectsUnknown()
    {
        string[] Argv(string v) => new[] { "FracturingFog", "--batch", "--fractal", "Mandelbrot", "--x", "-0.5", "--y", "0",
                                           "--zoom", "1", "--relief-raymarch", "--relief-detail-anchor", v, "--out", "out.png" };
        Assert.True(BatchOptions.TryParse(Argv("uniform"), 2, out var o, out var e), e);
        Assert.Equal(ReliefDetailAnchor.Uniform, o.ReliefDetailAnchor);
        Assert.True(o.Relief);
        Assert.False(BatchOptions.TryParse(Argv("sky"), 2, out _, out var err));
        Assert.Contains("--relief-detail-anchor", err);
    }

    [Fact]
    public void Builder_RoundTripsUniform_AndOmitsCamera()
    {
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1,
            ReliefEnabled = true, ReliefRaymarch = true, ReliefDetailAnchor = ReliefDetailAnchor.Uniform,
        };
        var report = BatchCommandBuilder.BuildWithReport(snap);
        var argv = new[] { "FracturingFog", "--batch" }
            .Concat(report.Args.Select(a => a == "<OUTPUT.png>" ? "out.png" : a)).ToArray();
        Assert.True(BatchOptions.TryParse(argv, 2, out var o, out var e), e);
        Assert.Equal(ReliefDetailAnchor.Uniform, o.ReliefDetailAnchor);

        Assert.DoesNotContain("--relief-detail-anchor", BatchCommandBuilder.Build(new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true,
        }));
    }

    [Fact]
    public void Region_And_Clone_CarryTheAnchor()
    {
        var src = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DDetailAnchor = ReliefDetailAnchor.Uniform };
        Assert.Equal(ReliefDetailAnchor.Uniform, src.Clone().Relief2DDetailAnchor);

        string json = JsonSerializer.Serialize(Relief3DSettings.Snapshot(src));
        Assert.Contains("\"Uniform\"", json);
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<Relief3DSettings>(json)!.ApplyTo(dst);
        Assert.Equal(ReliefDetailAnchor.Uniform, dst.Relief2DDetailAnchor);

        var legacy = new FractalParameters { Relief2DDetailAnchor = ReliefDetailAnchor.Uniform };
        JsonSerializer.Deserialize<Relief3DSettings>("{\"Enabled\":true}")!.ApplyTo(legacy);
        Assert.Equal(ReliefDetailAnchor.Camera, legacy.Relief2DDetailAnchor);
    }

    [Fact]
    public void Dialog_WritesTheAnchor()
    {
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        var vm = new FractalParamsViewModel(FractalType.Mandelbrot, p);
        vm.Relief2DDetailAnchor = ReliefDetailAnchor.Uniform;
        Assert.Equal(ReliefDetailAnchor.Uniform, p.Relief2DDetailAnchor);
        Assert.Contains(ReliefDetailAnchor.Uniform, vm.Relief2DDetailAnchors.Cast<ReliefDetailAnchor>());
    }
}
