// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// GpuTiledDispatch.cs — #1170: split a 3D raymarch frame into several kernel
// launches so no single launch outlasts the Windows GPU watchdog (TDR, 2 s by
// default).
//
// One launch per frame was fine on a fast card, but on a slow one (GeForce
// GT 710, fp64 at 1/24 rate) a 1080p Mandelbox frame is ~25 s of GPU work.
// The driver resets the device mid-launch, ILGPU reports "unspecified launch
// failure", and the CUDA context is poisoned for the process (#1164 latches
// GPU 3D off for the session when that happens).
//
// Each launch covers a *comb* of the frame rather than a row band: the frame
// is cut into TileBlock-pixel blocks, and a launch shades the same run of
// TileRun pixels from every block. A raymarch's cost varies wildly across
// the frame (sky rows are nearly free, rows through the fractal are not), so
// a row band measured at the top would mis-predict the next one. A comb
// samples every part of the frame evenly, so the time of one launch predicts
// the next. Runs stay contiguous, so neighbouring threads still trace
// neighbouring pixels.
//
// Sizing is adaptive: a small probe launch measures the per-pixel cost on
// this device for this view, then each later launch is sized to take about
// TargetLaunchSeconds (growth capped per step). Small frames skip all of this
// and go out as one launch, exactly as before. Per-pixel output doesn't
// depend on how the frame is split, so tiled frames are byte-identical to a
// single launch.

using System;
using System.Diagnostics;
using System.Threading;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

public static class GpuTiledDispatch
{
    /// <summary>Pixels per block of the comb. Each launch shades the same
    /// run of pixels from every block.</summary>
    public const int TileBlock = 1024;

    /// <summary>Frames at or under this many pixels go out as one launch.</summary>
    public const int SingleLaunchPixels = 16384;

    /// <summary>Approximate pixel count of the first (probe) launch.</summary>
    public const int ProbePixels = 8192;

    /// <summary>Wall-time target for each launch after the probe; well
    /// under the 2 s watchdog.</summary>
    public const double TargetLaunchSeconds = 0.25;

    /// <summary>Largest factor one launch's run may grow over the last.</summary>
    public const int MaxGrowth = 8;

    // Test-only thresholds, scoped to the thread that set them (like
    // GpuAcceleratorHost's test override): xUnit runs classes in parallel,
    // and a process-wide change would retile other tests' frames.
    [ThreadStatic] private static (int Single, int Probe)? t_testThresholds;

    /// <summary>Test-only. Override <see cref="SingleLaunchPixels"/> and
    /// <see cref="ProbePixels"/> on the calling thread (<see cref="ClearTestThresholds"/> restores the
    /// defaults). Not for production use.</summary>
    public static void SetTestThresholds(int singleLaunchPixels, int probePixels)
        => t_testThresholds = (singleLaunchPixels, probePixels);

    public static void ClearTestThresholds() => t_testThresholds = null;

    /// <summary>Launches used by the last <see cref="Run"/> on this thread
    /// (diagnostics and tests).</summary>
    [ThreadStatic] private static int t_lastLaunchCount;
    public static int LastLaunchCount => t_lastLaunchCount;

    /// <summary>Shade the frame <paramref name="r"/> describes through one or
    /// more calls to <paramref name="launch"/> (thread count, per-launch
    /// params), synchronising after each. Returns false if
    /// <paramref name="ct"/> was cancelled between launches; the frame is
    /// then incomplete.</summary>
    public static bool Run(Accelerator acc, GpuRaymarchParams r, Action<int, GpuRaymarchParams> launch, CancellationToken ct = default)
    {
        int total = r.Width * r.Height;
        t_lastLaunchCount = 0;
        var (singleLaunchPixels, probePixels) = t_testThresholds ?? (SingleLaunchPixels, ProbePixels);
        if (total <= singleLaunchPixels)
        {
            r.TileBlock = r.TileStart = r.TileRun = 0;
            launch(total, r);
            acc.Synchronize();
            t_lastLaunchCount = 1;
            return true;
        }

        int blocks = (total + TileBlock - 1) / TileBlock;
        int run = (int)Math.Clamp((long)TileBlock * probePixels / total, 1, TileBlock);
        var sw = new Stopwatch();
        for (int start = 0; start < TileBlock;)
        {
            if (ct.IsCancellationRequested) return false;
            run = Math.Min(run, TileBlock - start);
            r.TileBlock = TileBlock; r.TileStart = start; r.TileRun = run;
            sw.Restart();
            launch(blocks * run, r);
            acc.Synchronize();
            double seconds = sw.Elapsed.TotalSeconds;
            t_lastLaunchCount++;
            start += run;

            // Seconds per pixel of run; size the next launch to the target.
            double perRun = seconds / run;
            long next = perRun > 0 ? (long)(TargetLaunchSeconds / perRun) : (long)run * MaxGrowth;
            run = (int)Math.Clamp(next, 1, (long)run * MaxGrowth);
        }
        return true;
    }
}
