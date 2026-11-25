// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// OdeGpuFrame.cs (#2049, physics epic #2048)
//
// One frame of an ODE family on the GPU template, shared by every family (Hénon-Heiles first,
// PR #2054, then three-body and the rest): the route (GPU render on, not a preview frame,
// the family's GPU / CPU / Auto selection), the kernel choice (OdeTraceBackends), Auto's
// measurement (OdeGpuRoute), and the split of the frame into pixels the GPU integrates and
// pixels the CPU labels at no cost (forbidden launches, launches already past the exit).
// The family supplies three delegates: the launch of a pixel (or "not a GPU item"), the store
// of a GPU outcome, and the CPU trace of a pixel (the trivial pixels, and Auto's timing).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Render;

namespace FracturingFog.Calculators.Gpu;

internal static class OdeGpuFrame
{
    /// <summary>The launch state of pixel <c>idx</c> into <c>state</c>; false = the CPU labels it (no integration).</summary>
    public delegate bool LaunchFn(int idx, Span<double> state);
    /// <summary>Store the GPU outcome of item <c>item</c> (pixel <c>idx</c>) from <c>r</c>.</summary>
    public delegate void StoreFn(int idx, OdeResult r, int item);
    /// <summary>Trace and store pixel <c>idx</c> on the CPU.</summary>
    public delegate void CpuPixelFn(int idx);

    public sealed record Outcome(bool OnGpu, GpuRoute Route, string? Backend, OdeGpuRoute.Measurement? Measurement);

    /// <summary>Run the frame on the GPU when the route allows. Returns OnGpu false when the caller
    /// must trace the frame on the CPU (Route says why).</summary>
    public static Outcome TryRun(in FracturingFog.Rendering.Lighting.LightingFxData fx, bool previewChild,
        OdeGpuRoute.Mode mode, string envName, OdeJobTemplate t, int width, int height, byte[]? mask,
        LaunchFn launch, StoreFn store, CpuPixelFn cpuPixel, CancellationToken ct)
    {
        var gate = Gpu3DRoute.Gate(in fx, lowRes: false,
            previewChild ? "preview frame" : null, previewChild ? "drag-preview frames trace on the CPU" : null);
        if (gate is { } g) return new Outcome(false, g, null, null);
        if (mode == OdeGpuRoute.Mode.Cpu)
            return new Outcome(false, GpuRoute.Cpu("CPU forced", $"{envName} selects the CPU trace"), null, null);
        var kernel = OdeTraceBackends.KernelFor(OdeTraceBackends.Selection, needDouble: !t.Single);
        if (kernel == null)
            return new Outcome(false, GpuRoute.Cpu("no GPU backend", "no D3D11 / Vulkan ODE kernel on this machine"), null, null);
        int pixels = width * height;
        var known = mode == OdeGpuRoute.Mode.Auto ? OdeGpuRoute.Known(kernel, t.Family.Key, t.Single, pixels) : null;
        if (known is { GpuFaster: false })
            return new Outcome(false, GpuRoute.Cpu("CPU faster", known.Summary), null, known);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int dim = t.Family.Dimension;
        var items = new List<int>(pixels);
        var trivial = new List<int>();
        Span<double> probe = stackalloc double[dim];
        for (int idx = 0; idx < pixels; idx++)
        {
            if (mask != null && mask[idx] == 0) continue;
            if (launch(idx, probe)) items.Add(idx); else trivial.Add(idx);
        }
        var job = new OdeJob
        {
            Family = t.Family, FamilyParams = t.FamilyParams, RelTol = t.RelTol, AbsTol = t.AbsTol,
            InitialStep = t.InitialStep, MaxSteps = t.MaxSteps, Count = items.Count, Single = t.Single,
            Launches = (first, n, dst) =>
            {
                Span<double> s = stackalloc double[dim];
                for (int i = 0; i < n; i++)
                {
                    launch(items[first + i], s);
                    for (int k = 0; k < dim; k++) dst[i * dim + k] = s[k];
                }
            },
        };
        var r = kernel.Trace(job, ct);
        double gpuMs = sw.Elapsed.TotalMilliseconds;
        var route = Gpu3DRoute.AfterRender(r != null, kernel.LastError);
        if (r == null) return new Outcome(false, route, null, null);
        Parallel.For(0, items.Count, i => store(items[i], r, i));
        Parallel.ForEach(trivial, idx => cpuPixel(idx));
        string backend = $"{kernel.Backend} ({kernel.DeviceName}){(t.Single ? ", single" : "")}";

        OdeGpuRoute.Measurement? m = known;
        if (known == null && mode == OdeGpuRoute.Mode.Auto && mask == null && !ct.IsCancellationRequested)
        {
            // Auto's first GPU frame here: time the CPU on every 8th row of the same frame
            // (writing the same values the GPU wrote, so the frame is unchanged).
            int rows = 0, stride = OdeGpuRoute.MeasureRowStride;
            var cpu = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, (height + stride - 1) / stride, new ParallelOptions { CancellationToken = ct }, rr =>
            {
                int y = rr * stride;
                for (int x = 0; x < width; x++) cpuPixel(y * width + x);
                Interlocked.Increment(ref rows);
            });
            // The rows the CPU just traced are re-stored from the GPU so the frame stays one path's output.
            Parallel.For(0, items.Count, i => { if (items[i] / width % stride == 0) store(items[i], r, i); });
            m = new OdeGpuRoute.Measurement(cpu.Elapsed.TotalMilliseconds * 1e6 / Math.Max(1, rows * width),
                gpuMs * 1e6 / Math.Max(1, pixels), pixels);
            OdeGpuRoute.Record(kernel, t.Family.Key, t.Single, m);
        }
        return new Outcome(true, route, backend, m);
    }
}

/// <summary>#2049 — the per-frame job settings of a family (everything but the items).</summary>
public sealed record OdeJobTemplate(OdeFamily Family, double[] FamilyParams, double RelTol, double AbsTol,
    double InitialStep, int MaxSteps, bool Single);
