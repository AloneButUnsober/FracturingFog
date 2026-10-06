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
// is cut into equal blocks (sized so one pixel per block is ~ProbePixels
// pixels), and a launch shades the same run of TileRun pixels from every
// block. A raymarch's cost varies wildly across the frame (sky rows are
// nearly free, rows through the fractal are not), so a row band measured at
// the top would mis-predict the next one. A comb samples every part of the
// frame evenly, so the time of one launch predicts the next. Runs stay
// contiguous, so neighbouring threads still trace neighbouring pixels.
//
// Sizing is adaptive: a small probe launch measures the per-pixel cost on
// this device for this view, then each later launch is sized to take about
// TargetLaunchSeconds (growth capped per step). Per-pixel output doesn't
// depend on how the frame is split, so tiled frames are byte-identical to a
// single launch.
//
// #1169 — the probe is tiny (64 px) and every frame above that is tiled:
// Mandelbulb on a GT 710 costs ~1 s of thread time per pixel, so the old
// 8192-px probe and the untiled small frames (even 96x72) outlasted the
// watchdog — that was the "Mandelbulb faults at launch" bug. And when a launch
// shows the device can't render this kernel in any useful time (over
// MaxSecondsPerPixel), Run gives up with TooSlow so the caller renders on the
// CPU instead of spending minutes on one frame.

using System;
using System.Diagnostics;
using System.Threading;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Outcome of <see cref="GpuTiledDispatch.Run"/>.</summary>
public enum GpuDispatchResult
{
    /// <summary>Every pixel was shaded.</summary>
    Completed,
    /// <summary>The token was cancelled between launches; the frame is incomplete.</summary>
    Cancelled,
    /// <summary>#1169 — a launch measured more than
    /// <see cref="GpuTiledDispatch.MaxSecondsPerPixel"/>; the frame was abandoned
    /// so the caller can render it on the CPU.</summary>
    TooSlow,
}

public static class GpuTiledDispatch
{
    /// <summary>Frames at or under this many pixels go out as one launch.</summary>
    public const int SingleLaunchPixels = 64;

    /// <summary>Approximate pixel count of the first (probe) launch. #1169:
    /// small, because one Mandelbulb pixel on a GT 710 costs ~1 s of thread
    /// time (software fp64 acos/atan2/pow/sin/cos at 1/24 rate), so even a
    /// few thousand pixels in one launch outlast the watchdog.</summary>
    public const int ProbePixels = 64;

    /// <summary>Wall-time target for each launch after the probe; well
    /// under the 2 s watchdog.</summary>
    public const double TargetLaunchSeconds = 0.25;

    /// <summary>Largest factor one launch's run may grow over the last.</summary>
    public const int MaxGrowth = 8;

    /// <summary>#1169 — launches that cost more than this per pixel (wall time
    /// over their pixel count) mean this device is hopeless for this kernel:
    /// 1 ms/px is ~35 min for a 1080p frame. Mandelbulb on a GT 710 costs
    /// ~3-5 ms/px; every other family there stays under ~0.1 ms/px even on
    /// the tiny, under-occupied probe launch.</summary>
    public const double MaxSecondsPerPixel = 1e-3;

    /// <summary>Consecutive over-budget launches needed for a TooSlow verdict,
    /// so one launch stalled behind another GPU client can't trip it.</summary>
    public const int TooSlowStrikes = 3;

    /// <summary>The too-slow verdict needs a launch at least this long, so a
    /// fast GPU's first-launch overhead on a tiny probe can't trip it.</summary>
    public const double MinGuardSeconds = 0.05;

    // Test-only thresholds, scoped to the thread that set them (like
    // GpuAcceleratorHost's test override): xUnit runs classes in parallel,
    // and a process-wide change would retile other tests' frames.
    [ThreadStatic] private static (int Single, int Probe, double MaxSecPerPx, double MinGuard, bool JudgeCpu)? t_testThresholds;

    /// <summary>Test-only. Override the thresholds on the calling thread
    /// (<see cref="ClearTestThresholds"/> restores the defaults). Not for
    /// production use.</summary>
    public static void SetTestThresholds(int singleLaunchPixels, int probePixels,
        double maxSecondsPerPixel = MaxSecondsPerPixel, double minGuardSeconds = MinGuardSeconds,
        bool judgeCpuAccelerator = false)
        => t_testThresholds = (singleLaunchPixels, probePixels, maxSecondsPerPixel, minGuardSeconds, judgeCpuAccelerator);

    public static void ClearTestThresholds() => t_testThresholds = null;

    /// <summary>Launches used by the last <see cref="Run"/> on this thread
    /// (diagnostics and tests).</summary>
    [ThreadStatic] private static int t_lastLaunchCount;
    public static int LastLaunchCount => t_lastLaunchCount;

    /// <summary>Wall seconds per pixel of the last launch measured by
    /// <see cref="Run"/> on this thread (0 = none measured).</summary>
    [ThreadStatic] private static double t_lastSecondsPerPixel;
    public static double LastSecondsPerPixel => t_lastSecondsPerPixel;

    // One frame at a time per accelerator. Frames from several threads would
    // otherwise interleave their launches on the shared stream, so a tiny probe
    // launch would wait behind another frame's 0.25 s launches and its wall time
    // would read as "too slow". The GPU is the bottleneck either way, so this
    // costs no throughput.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, SemaphoreSlim> s_gates = new();

    /// <summary>Shade the frame <paramref name="r"/> describes through one or
    /// more calls to <paramref name="launch"/> (thread count, per-launch
    /// params), synchronising after each.</summary>
    public static GpuDispatchResult Run(Accelerator acc, GpuRaymarchParams r, Action<int, GpuRaymarchParams> launch, CancellationToken ct = default)
    {
        var gate = s_gates.GetValue(acc, _ => new SemaphoreSlim(1, 1));
        try { gate.Wait(ct); }
        catch (OperationCanceledException) { t_lastLaunchCount = 0; return GpuDispatchResult.Cancelled; }
        try { return RunCore(acc, r, launch, ct); }
        finally { gate.Release(); }
    }

    private static GpuDispatchResult RunCore(Accelerator acc, GpuRaymarchParams r, Action<int, GpuRaymarchParams> launch, CancellationToken ct)
    {
        int total = r.Width * r.Height;
        t_lastLaunchCount = 0;
        t_lastSecondsPerPixel = 0;
        var (singleLaunchPixels, probePixels, maxSecPerPx, minGuard, judgeCpu) =
            t_testThresholds ?? (SingleLaunchPixels, ProbePixels, MaxSecondsPerPixel, MinGuardSeconds, false);
        if (total <= singleLaunchPixels)
        {
            r.TileBlock = r.TileStart = r.TileRun = 0;
            launch(total, r);
            acc.Synchronize();
            t_lastLaunchCount = 1;
            return GpuDispatchResult.Completed;
        }

        // Comb: `block`-pixel blocks, so a run of one pixel per block is about
        // probePixels pixels — the first launch.
        int block = (int)Math.Max(1, (total + (long)probePixels - 1) / Math.Max(1, probePixels));
        int blocks = (total + block - 1) / block;
        int run = 1;
        int strikes = 0;
        var sw = new Stopwatch();
        for (int start = 0; start < block;)
        {
            if (ct.IsCancellationRequested) return GpuDispatchResult.Cancelled;
            run = Math.Min(run, block - start);
            r.TileBlock = block; r.TileStart = start; r.TileRun = run;
            sw.Restart();
            launch(blocks * run, r);
            acc.Synchronize();
            double seconds = sw.Elapsed.TotalSeconds;
            t_lastLaunchCount++;
            start += run;

            t_lastSecondsPerPixel = seconds / ((double)blocks * run);
            // The ILGPU CPU accelerator (test override only) is emulation; never judge it.
            strikes = seconds >= minGuard && t_lastSecondsPerPixel > maxSecPerPx ? strikes + 1 : 0;
            if (strikes >= TooSlowStrikes && start < block
                && (judgeCpu || acc.AcceleratorType != AcceleratorType.CPU))
                return GpuDispatchResult.TooSlow;

            // Seconds per pixel of run; size the next launch to the target.
            double perRun = seconds / run;
            long next = perRun > 0 ? (long)(TargetLaunchSeconds / perRun) : (long)run * MaxGrowth;
            run = (int)Math.Clamp(next, 1, (long)run * MaxGrowth);
        }
        return GpuDispatchResult.Completed;
    }

    /// <summary>LastError text for a <see cref="GpuDispatchResult.TooSlow"/>
    /// frame. Gpu3DRoute keys the "GPU too slow" route on its prefix.</summary>
    public static string TooSlowMessage(string family)
        => $"GPU too slow for {family} on this device (~{t_lastSecondsPerPixel * 1e3:0.###} ms per pixel); rendering on the CPU";
}
