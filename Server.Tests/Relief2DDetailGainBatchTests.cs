// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #518 — the filament detail knobs flow through the batch grammar, the command
// builder round-trips them, and they reach the relief pre-pass (a non-default gain
// changes the render; the defaults leave it byte-identical).

using System;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class Relief2DDetailGainBatchTests
{
    // A synthetic slab-with-boundary-ridge field for the render integration checks.
    private static float[] RidgeField(int w, int h)
    {
        var f = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // A broad low mound (slab) + a thin tall ridge band (filament).
                double cx = (x / (w - 1.0)) * 2 - 1;
                double slab = 0.6 * Math.Max(0.0, 1.0 - cx * cx);
                double ridge = Math.Abs(x - w / 2) <= 1 ? 0.4 : 0.0;
                f[y * w + x] = (float)(slab + ridge);
            }
        return f;
    }

    private static FractalParameters ReliefP() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DGpuRaymarch = false,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,
    };

    // ── Batch parse + validation ────────────────────────────────────────────

    [Fact]
    public void Batch_Detail_Flags_Parse_And_Force_Relief()
    {
        string[] argv =
        {
            "FracturingFog", "--batch", "--fractal", "Mandelbrot",
            "--x", "-0.5", "--y", "0", "--zoom", "1",
            "--relief-detail-gain", "2.5", "--relief-detail-radius", "4",
            "--relief-height-gamma", "1.8", "--out", "out.png",
        };
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        Assert.Equal(2.5, opts.ReliefDetailGain!.Value, 6);
        Assert.Equal(4, opts.ReliefDetailRadius!.Value);
        Assert.Equal(1.8, opts.ReliefHeightGamma!.Value, 6);
        Assert.True(opts.Relief);
    }

    [Theory]
    [InlineData("--relief-detail-gain", "9")]
    [InlineData("--relief-height-gamma", "0.01")]
    [InlineData("--relief-detail-radius", "999")]
    public void Batch_OutOfRange_Rejected(string flag, string value)
    {
        string[] argv =
        {
            "FracturingFog", "--batch", "--fractal", "Mandelbrot",
            "--x", "-0.5", "--y", "0", "--zoom", "1",
            flag, value, "--out", "out.png",
        };
        Assert.False(BatchOptions.TryParse(argv, startIndex: 2, out _, out var err));
        Assert.Contains(flag.TrimStart('-'), err);
    }

    // ── Builder emit round-trip ─────────────────────────────────────────────

    [Fact]
    public void Builder_Emits_Detail_Knobs_When_NonDefault()
    {
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0, Zoom = 1,
            ReliefEnabled = true, ReliefRaymarch = true,
            ReliefDetailGain = 3.0, ReliefDetailRadius = 6, ReliefHeightGamma = 2.0,
        };
        string cmd = BatchCommandBuilder.Build(snap);
        Assert.Contains("--relief-detail-gain", cmd);
        Assert.Contains("--relief-detail-radius", cmd);
        Assert.Contains("--relief-height-gamma", cmd);
    }

    [Fact]
    public void Builder_Omits_Detail_Knobs_At_Default()
    {
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0, Zoom = 1,
            ReliefEnabled = true, ReliefRaymarch = true,
            // gain 1 / radius 0 / gamma 1 = defaults.
        };
        string cmd = BatchCommandBuilder.Build(snap);
        Assert.DoesNotContain("--relief-detail-gain", cmd);
        Assert.DoesNotContain("--relief-detail-radius", cmd);
        Assert.DoesNotContain("--relief-height-gamma", cmd);
    }

    // ── Render integration ──────────────────────────────────────────────────

    [Fact]
    public void Render_Defaults_ByteIdentical_To_Explicit_Off()
    {
        int w = 96, h = 72, fw = 96, fh = 72;
        var field = RidgeField(fw, fh);
        var albedo = new uint[w * h];
        for (int i = 0; i < albedo.Length; i++) albedo[i] = 0xFF335577u;

        var pDefault = ReliefP();                                 // gain 1 / gamma 1 (defaults)
        var pExplicit = ReliefP();
        pExplicit.Relief2DDetailGain = 1.0;
        pExplicit.Relief2DHeightGamma = 1.0;
        pExplicit.Relief2DDetailRadius = 0;

        var a = new uint[w * h];
        var b = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, field, w, h, fw, fh, pDefault, a, out _);
        HeightfieldRaymarch2D.Render(albedo, field, w, h, fw, fh, pExplicit, b, out _);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Render_DetailGain_Changes_Output()
    {
        int w = 96, h = 72, fw = 96, fh = 72;
        var field = RidgeField(fw, fh);
        var albedo = new uint[w * h];
        for (int i = 0; i < albedo.Length; i++) albedo[i] = 0xFF335577u;

        var pOff = ReliefP();
        var pOn = ReliefP();
        pOn.Relief2DDetailGain = 4.0;

        var a = new uint[w * h];
        var b = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, field, w, h, fw, fh, pOff, a, out _);
        HeightfieldRaymarch2D.Render(albedo, field, w, h, fw, fh, pOn, b, out _);
        Assert.False(System.Linq.Enumerable.SequenceEqual(a, b),
            "detail gain did not reach the relief pre-pass");
    }
}
