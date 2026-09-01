// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S6 (#408 / #468) — the offline --batch video/slideshow loop can now carry Relief
// 3D, and the froxel temporal-reprojection seam is reachable from the CLI. These
// lock the CLI grammar + the shared BuildFractalParameters mapping: relief flags
// flow into FractalParameters, and --relief-froxel-temporal / -feedback set the
// temporal knobs (and imply froxel + relief + raymarch).

using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S6VideoReliefBatchTests
{
    private static BatchOptions Parse(params string[] tail)
    {
        var argv = new string[tail.Length + 2];
        argv[0] = "FracturingFog"; argv[1] = "--batch";
        System.Array.Copy(tail, 0, argv, 2, tail.Length);
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        return opts;
    }

    [Fact]
    public void FroxelTemporal_Flag_Implies_Froxel_Relief_Raymarch()
    {
        var opts = Parse("--fractal", "Mandelbrot", "--x", "-0.5", "--y", "0", "--zoom", "1",
                         "--mode", "video", "--seconds", "1", "--fps", "10",
                         "--relief-froxel-temporal", "--out", "outdir");
        Assert.True(opts.ReliefFroxelTemporal);
        Assert.True(opts.ReliefFroxel);
        Assert.True(opts.ReliefRaymarch);
        Assert.True(opts.Relief);
    }

    [Fact]
    public void FroxelFeedback_Parses_And_Implies_Temporal()
    {
        var opts = Parse("--fractal", "Mandelbrot", "--x", "-0.5", "--y", "0", "--zoom", "1",
                         "--mode", "video", "--seconds", "1", "--fps", "10",
                         "--relief-froxel-feedback", "0.75", "--out", "outdir");
        Assert.Equal(0.75, opts.ReliefFroxelFeedback!.Value, 6);
        Assert.True(opts.ReliefFroxelTemporal);
        Assert.True(opts.ReliefFroxel);
    }

    [Fact]
    public void FroxelFeedback_OutOfRange_Rejected()
    {
        string[] argv =
        {
            "FracturingFog", "--batch", "--fractal", "Mandelbrot",
            "--x", "-0.5", "--y", "0", "--zoom", "1",
            "--relief-froxel-feedback", "1.5", "--out", "outdir",
        };
        Assert.False(BatchOptions.TryParse(argv, startIndex: 2, out _, out var err));
        Assert.Contains("relief-froxel-feedback", err);
    }

    [Fact]
    public void Slideshow_Mode_Accepts_Relief_Flags()
    {
        // S6 (#408) — the offline slideshow loop (both video-zoom legs and the
        // still cross-fade loop) now carries Relief 3D via the same flags as
        // video. Lock that slideshow mode parses them (does not reject relief).
        var opts = Parse("--mode", "slideshow", "--seconds", "2", "--fps", "8",
                         "--relief", "--relief-raymarch", "--relief-froxel-temporal",
                         "--out", "outdir");
        Assert.Equal(BatchMode.Slideshow, opts.Mode);
        Assert.True(opts.Relief);
        Assert.True(opts.ReliefRaymarch);
        Assert.True(opts.ReliefFroxelTemporal);
    }

    // NOTE: BatchRenderer.BuildFractalParameters lives in the WinExe assembly (not
    // referenced by this test project), so the opts→FractalParameters mapping is
    // exercised via the headless CLI (a relief video frame differs from a flat one)
    // rather than a direct call here. These tests lock the CLI grammar.
}
