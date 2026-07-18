// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Guard/RequestLimits.cs
// Bounds the server rejects requests outside of. Image width × height cap
// covers 32K posters. Video seconds cap matches the local BatchOptions
// limit (already enforced for headless --batch). Queue depth is the
// number of accepted-but-running renders; excess conns hit busy.

namespace FracturingFog.Server.Guard;

public sealed class RequestLimits
{
    public int MinWidth  { get; init; } = 16;
    public int MaxWidth  { get; init; } = 32768;
    public int MinHeight { get; init; } = 16;
    public int MaxHeight { get; init; } = 32768;

    /// <summary>Total pixel ceiling (width × height). 32K × 32K hits
    /// 4 GB of BGRA alone; the calculator scratch doubles that. 64 M
    /// (≈ 8K × 8K) keeps the worst-case render under ~768 MB on the host.</summary>
    public long MaxPixels { get; init; } = 64L * 1024L * 1024L;

    public double MinVideoSeconds { get; init; } = 0.5;
    public double MaxVideoSeconds { get; init; } = 600.0;

    public int MinVideoFps { get; init; } = 1;
    public int MaxVideoFps { get; init; } = 240;

    /// <summary>Per-frame iteration ceiling. Per-pixel cost is O(iterations)
    /// in the inner Mandelbrot loop. 1 M is already deep-zoom territory; a
    /// remote request asking for 10^9 iterations would burn the worker for
    /// minutes per pixel and is the closest thing the protocol has to a
    /// cheap DoS knob short of the dimension caps. Override per-deployment
    /// via <c>RequestLimits</c> at engine wiring time.</summary>
    public int MaxIterations { get; init; } = 1_000_000;

    /// <summary>Aggregate budget for video mode: width × height × seconds × fps.
    /// Independent of MaxPixels (single-frame) and MaxVideoSeconds (duration);
    /// stops a request whose components each pass their individual caps but
    /// whose product is still days of CPU. 16 B frame-pixels ≈ 8K poster ×
    /// 60 fps × 60 s; raise for deployments with batch-render queues that
    /// genuinely need it.</summary>
    public long MaxVideoFramePixels { get; init; } = 16_000_000_000L;

    public int MaxQueueDepth { get; init; } = 1;

    public static readonly RequestLimits Default = new();
}
