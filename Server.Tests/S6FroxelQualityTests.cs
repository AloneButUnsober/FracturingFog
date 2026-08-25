// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S6 — froxel resolution / quality controls (3D-Rendering-Roadmap.md,
// #389 / #408). FroxelQuality scales the camera-frustum froxel grid dims (X×Y×Z).
// Contract: Balanced == the historical const dims (24×24×48) so a Balanced scene is
// byte-identical to a pre-knob render; Low/High scale down/up; both the CPU post-pass
// (FroxelCameraVolume.Apply) and the GPU uniforms (FroxelGpuUniforms.Build) read the
// dims off the SAME grid, so they scale in lock-step; and the knob survives a batch +
// builder round-trip (a non-Balanced quality implies froxel).

using System;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S6FroxelQualityTests
{
    // A small oblique relief camera to frame a froxel grid over.
    private static HeightfieldRaymarch2D.ReliefCamera Cam()
        => HeightfieldRaymarch2D.BuildObliqueCamera(8, 8, 1.0, sy: 1.0, maxH: 1.0, new FractalParameters());

    private static LightingFxData FogFx()
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.6;
        fx.Light1.Intensity = 1.0;
        return fx;
    }

    // ── Dims mapping ─────────────────────────────────────────────────────────

    // Balanced maps to the legacy const dims — the byte-identical anchor.
    [Fact]
    public void Dims_Balanced_Is_Legacy_Const()
    {
        var (x, y, z) = FroxelCameraVolume.Dims(FroxelQuality.Balanced);
        Assert.Equal(FroxelCameraVolume.DimX, x);
        Assert.Equal(FroxelCameraVolume.DimY, y);
        Assert.Equal(FroxelCameraVolume.DimZ, z);
    }

    // Low < Balanced < High in total cell count (monotone quality).
    [Fact]
    public void Dims_Are_Monotone_In_CellCount()
    {
        long Cells(FroxelQuality q) { var (x, y, z) = FroxelCameraVolume.Dims(q); return (long)x * y * z; }
        Assert.True(Cells(FroxelQuality.Low) < Cells(FroxelQuality.Balanced));
        Assert.True(Cells(FroxelQuality.Balanced) < Cells(FroxelQuality.High));
    }

    // ── BuildGrid honours quality; near/far unchanged ────────────────────────

    [Fact]
    public void BuildGrid_Scales_Dims_But_Not_Depth_Bracket()
    {
        var cam = Cam();
        var bal = FroxelCameraVolume.BuildGrid(in cam);                      // parameterless == Balanced
        var balQ = FroxelCameraVolume.BuildGrid(in cam, FroxelQuality.Balanced);
        var hi = FroxelCameraVolume.BuildGrid(in cam, FroxelQuality.High);

        // Parameterless overload == explicit Balanced (byte-identical legacy path).
        Assert.Equal(bal.DimX, balQ.DimX);
        Assert.Equal(bal.DimZ, balQ.DimZ);

        var (hx, hy, hz) = FroxelCameraVolume.Dims(FroxelQuality.High);
        Assert.Equal(hx, hi.DimX);
        Assert.Equal(hy, hi.DimY);
        Assert.Equal(hz, hi.DimZ);

        // Only the resolution scales — the near/far bracket is quality-independent.
        Assert.Equal(bal.Near, hi.Near, 12);
        Assert.Equal(bal.Far, hi.Far, 12);
    }

    // ── CPU Apply: Balanced byte-identical, quality changes output ───────────

    [Fact]
    public void Apply_Balanced_Is_ByteIdentical_To_Default()
    {
        var cam = Cam();
        var fx = FogFx();
        int w = 8, h = 8, n = w * h;
        var beauty = new uint[n];
        var depth = new float[n];
        for (int i = 0; i < n; i++) { beauty[i] = 0xFF204060u; depth[i] = 2.0f + i * 0.1f; }

        var def = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fx);
        var bal = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fx,
            null, false, 0.0, FroxelQuality.Balanced);

        Assert.Equal(def, bal);   // exact byte-for-byte
    }

    [Fact]
    public void Apply_Quality_Changes_Output()
    {
        var cam = Cam();
        var fx = FogFx();
        int w = 8, h = 8, n = w * h;
        var beauty = new uint[n];
        var depth = new float[n];
        for (int i = 0; i < n; i++) { beauty[i] = 0xFF204060u; depth[i] = 2.0f + i * 0.1f; }

        var lo = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fx, null, false, 0.0, FroxelQuality.Low);
        var hi = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fx, null, false, 0.0, FroxelQuality.High);

        Assert.NotEqual(lo, hi);
    }

    // ── GPU uniforms scale in lock-step with the CPU grid ────────────────────

    [Fact]
    public void GpuUniforms_Build_Scales_Grid_With_Quality()
    {
        var cam = Cam();
        var fx = FogFx();
        var bal = FroxelGpuUniforms.Build(in cam, in fx);                    // == Balanced
        var hi = FroxelGpuUniforms.Build(in cam, in fx, FroxelQuality.High);

        Assert.Equal(FroxelCameraVolume.DimZ, bal.Grid.DimZ);
        var (_, _, hz) = FroxelCameraVolume.Dims(FroxelQuality.High);
        Assert.Equal(hz, hi.Grid.DimZ);
        // Same scene → same medium density regardless of resolution.
        Assert.Equal(bal.Medium.BaseDensity, hi.Medium.BaseDensity, 12);
    }

    // ── Batch parse + validation ─────────────────────────────────────────────

    [Fact]
    public void Batch_Quality_Flag_Parses_And_Forces_Froxel()
    {
        string[] argv =
        {
            "FracturingFog", "--batch", "--fractal", "Mandelbrot",
            "--x", "-0.5", "--y", "0", "--zoom", "1",
            "--relief-froxel-quality", "High", "--out", "out.png",
        };
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        Assert.Equal(FroxelQuality.High, opts.ReliefFroxelQuality);
        Assert.True(opts.ReliefFroxel);   // quality implies froxel
        Assert.True(opts.Relief);
        Assert.True(opts.ReliefRaymarch);
    }

    [Fact]
    public void Batch_Quality_Unknown_Rejected()
    {
        string[] argv =
        {
            "FracturingFog", "--batch", "--fractal", "Mandelbrot",
            "--x", "-0.5", "--y", "0", "--zoom", "1",
            "--relief-froxel-quality", "Ultra", "--out", "out.png",
        };
        Assert.False(BatchOptions.TryParse(argv, startIndex: 2, out _, out var err));
        Assert.Contains("relief-froxel-quality", err);
    }

    // ── Builder emit round-trip ─────────────────────────────────────────────

    [Fact]
    public void Builder_Emits_Quality_When_NonBalanced_RoundTrip()
    {
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0, Zoom = 1,
            ReliefEnabled = true,
            ReliefRaymarch = true,
            ReliefFroxel = true,
            ReliefFroxelQuality = FroxelQuality.High,
        };
        string cmd = BatchCommandBuilder.Build(snap);
        Assert.Contains("--relief-froxel-quality", cmd);
        Assert.DoesNotContain("--relief-froxel ", cmd + " ");   // not the bare flag

        var argv = Tokenize(cmd);
        for (int i = 0; i < argv.Length; i++) if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        Assert.Equal(FroxelQuality.High, opts.ReliefFroxelQuality);
    }

    [Fact]
    public void Builder_Emits_Bare_Froxel_When_Balanced()
    {
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0, Zoom = 1,
            ReliefEnabled = true,
            ReliefRaymarch = true,
            ReliefFroxel = true,
            ReliefFroxelQuality = FroxelQuality.Balanced,
        };
        string cmd = BatchCommandBuilder.Build(snap);
        Assert.Contains("--relief-froxel", cmd);
        Assert.DoesNotContain("--relief-froxel-quality", cmd);
    }

    private static string[] Tokenize(string cmd)
    {
        var list = new System.Collections.Generic.List<string>();
        int i = 0;
        while (i < cmd.Length)
        {
            while (i < cmd.Length && char.IsWhiteSpace(cmd[i])) i++;
            if (i >= cmd.Length) break;
            if (cmd[i] == '"')
            {
                i++;
                int start = i;
                while (i < cmd.Length && cmd[i] != '"') i++;
                list.Add(cmd.Substring(start, i - start));
                i++;
            }
            else
            {
                int start = i;
                while (i < cmd.Length && !char.IsWhiteSpace(cmd[i])) i++;
                list.Add(cmd.Substring(start, i - start));
            }
        }
        return list.ToArray();
    }
}
