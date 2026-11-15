// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1318: --gpugateprobe. The D3D11 deep perturbation frame releases the shared D3D
// gate between its row bands, so a present from another thread gets in after at most
// the bands already queued instead of after the whole frame. On the default hardware
// adapter (skipped without one or without fp64), a 1920x1080 seahorse frame at zoom
// 1e5 and 3000 iterations, in 24-row bands, runs on one thread while another thread
// "presents" every few milliseconds: under the gate it clobbers the compute state (as a
// present or another kernel may), queues one GPU packet of its own and waits for the
// GPU to reach it (the time a real present's Render waits). Per mode it prints the
// frame time without presents, the band count and time, and the presenter's median and
// worst wait:
//   • held: the frame's thread holds the gate around the whole call (the gate is
//     re-entrant, so the bands can't release it) — the behaviour before #1318;
//   • lookahead 0 / 1 / 3 / auto: the gate released between bands, with that many
//     bands queued ahead (auto, the default: enough for ~32 ms of GPU work).
// Checked: every mode's frame (with the presenter running, after a frame of another
// view dirtied the buffers) is byte-identical to a quiet held frame, and with the gate
// released the worst wait is under the whole frame.
// Exit 0 = pass or SKIP; 1 = a check failed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FracturingFog.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class GpuGateProbe
    {
        private const int W = 1920, H = 1080, N = W * H, MaxIter = 3000, BandRows = 24;

        // MandelbrotKernelSource is linked into both backend assemblies; the D3D kernel bands with its own copy.
        private static readonly System.Reflection.FieldInfo RowsOverride = typeof(MandelbrotGpuKernel).Assembly
            .GetType("FracturingFog.Rendering.MandelbrotKernelSource", throwOnError: true)!.GetField("PerturbBandRowsOverride")!;

        private sealed class Frame
        {
            public readonly int[] It = new int[N];
            public readonly float[] Sm = new float[N], Zr = new float[N], Zi = new float[N], Dr = new float[N], Di = new float[N];
            public bool Same(Frame o) => It.AsSpan().SequenceEqual(o.It) && Sm.AsSpan().SequenceEqual(o.Sm)
                && Zr.AsSpan().SequenceEqual(o.Zr) && Zi.AsSpan().SequenceEqual(o.Zi) && Dr.AsSpan().SequenceEqual(o.Dr) && Di.AsSpan().SequenceEqual(o.Di);
        }

        public static int Run()
        {
            if (Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS") == null)
                Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? ctx);
            if (hr.Failure || device == null || ctx == null) { Console.WriteLine($"gpugateprobe SKIP: no hardware device (0x{hr.Code:X8})."); return 0; }
            int failures = 0;
            var gate = new object();
            try
            {
                using var k = new MandelbrotGpuKernel(device, ctx, gate);
                if (!k.SupportsPerturbation) { Console.WriteLine("gpugateprobe SKIP: no fp64."); return 0; }
                RowsOverride.SetValue(null, BandRows);
                var (zr, zi, len) = Orbit(-0.743643887037151, 0.13182590420533, MaxIter);
                double scale = 3.5 / (W * 1e5);
                void Render(Frame f) => k.RunPerturb(W, H, scale, MaxIter, 512.0 * 512.0, -W / 2, -H / 2, zr, zi, len,
                    f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di);

                // The presenter's own GPU work: clear a small UAV, then an event query.
                using var buf = device.CreateBuffer(new BufferDescription
                {
                    ByteWidth = 4096, BindFlags = BindFlags.UnorderedAccess, Usage = ResourceUsage.Default,
                    MiscFlags = ResourceOptionFlags.BufferAllowRawViews,
                });
                using var uav = device.CreateUnorderedAccessView(buf, new UnorderedAccessViewDescription
                {
                    Format = Vortice.DXGI.Format.R32_Typeless, ViewDimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = 1024, Flags = BufferUnorderedAccessViewFlags.Raw },
                });
                using var done = device.CreateQuery(new QueryDescription(QueryType.Event));
                double Present()
                {
                    long t0 = Stopwatch.GetTimestamp();
                    lock (gate)
                    {
                        // Clobber the compute state, as a present or another kernel may.
                        ctx.CSSetShader(null);
                        for (uint i = 0; i < 16; i++) ctx.CSSetShaderResource(i, null);
                        for (uint i = 0; i < 6; i++) ctx.CSUnsetUnorderedAccessView(i);
                        ctx.CSSetConstantBuffer(0, null);
                        ctx.ClearUnorderedAccessView(uav, new Vortice.Mathematics.Int4(1, 2, 3, 4));
                        ctx.End(done);
                        ctx.Flush();
                    }
                    while (true)
                    {
                        lock (gate) { if (ctx.GetData(done, IntPtr.Zero, 0, AsyncGetDataFlags.None).Code == 0) break; }
                        Thread.Yield();
                    }
                    return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                }

                var reference = new Frame();
                lock (gate) Render(reference);   // warm-up (compile), held
                lock (gate) Render(reference);
                Console.WriteLine($"gpugateprobe: {W}x{H}, zoom 1e5, {MaxIter} iterations, {k.LastPerturbBandCount} bands of {BandRows} rows");

                foreach (var (name, held, lookahead) in new[] { ("held (before #1318)", true, 3), ("lookahead 0", false, 0), ("lookahead 1", false, 1), ("lookahead 3", false, 3), ("auto (default)", false, -1) })
                {
                    MandelbrotGpuKernel.PerturbBandLookahead = lookahead;
                    var f = new Frame();
                    double quiet = Enumerable.Range(0, 3).Min(_ =>
                    {
                        long t = Stopwatch.GetTimestamp();
                        if (held) lock (gate) Render(f); else Render(f);
                        return Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                    });

                    // Dirty every output buffer with another view, so bands that wrote nothing
                    // can't pass on the quiet frame's leftovers.
                    var dirty = new Frame();
                    k.RunPerturb(W, H, scale * 7.3, MaxIter, 512.0 * 512.0, -W / 2, -H / 2, zr, zi, len,
                        dirty.It, dirty.Sm, dirty.Zr, dirty.Zi, dirty.Dr, dirty.Di);
                    if (dirty.Same(reference)) { Console.Error.WriteLine("  FAIL precondition: the dirty view equals the reference"); failures++; }

                    var waits = new List<double>();
                    var busy = new Frame();
                    using var stop = new ManualResetEventSlim();
                    var presenter = new Thread(() =>
                    {
                        while (!stop.IsSet) { waits.Add(Present()); Thread.Sleep(4); }
                    }) { IsBackground = true };
                    presenter.Start();
                    long tb = Stopwatch.GetTimestamp();
                    if (held) lock (gate) Render(busy); else Render(busy);
                    double busyMs = Stopwatch.GetElapsedTime(tb).TotalMilliseconds;
                    stop.Set();
                    presenter.Join();

                    var sorted = waits.OrderBy(x => x).ToArray();
                    double med = sorted.Length > 0 ? sorted[sorted.Length / 2] : double.NaN, worst = sorted.Length > 0 ? sorted[^1] : double.NaN;
                    bool same = busy.Same(reference) && f.Same(reference);
                    if (!same) failures++;
                    bool latencyOk = held || worst < quiet * 0.75;
                    if (!latencyOk) failures++;
                    Console.WriteLine($"  {name,-20}: frame {quiet,7:F1} ms ({quiet / k.LastPerturbBandCount,5:F1} ms/band), with presents {busyMs,7:F1} ms; " +
                                      $"{sorted.Length,3} presents, wait median {med,6:F1} ms, worst {worst,6:F1} ms; " +
                                      $"lookahead used {k.LastPerturbLookahead}, {(same ? "== reference" : "DIFFERS")}{(latencyOk ? "" : ", worst wait not under the frame")}");
                }
            }
            finally
            {
                RowsOverride.SetValue(null, 0);
                MandelbrotGpuKernel.PerturbBandLookahead = -1;
                ctx.Dispose(); device.Dispose();
            }
            Console.WriteLine(failures == 0 ? "gpugateprobe OK" : $"gpugateprobe FAIL: {failures} check(s)");
            return failures == 0 ? 0 : 1;
        }

        private static (double[] Zr, double[] Zi, int Len) Orbit(double cx, double cy, int maxIter)
        {
            var zr = new double[maxIter + 1]; var zi = new double[maxIter + 1];
            double x = 0, y = 0; int len = maxIter;
            for (int n = 0; n < maxIter; n++)
            {
                zr[n] = x; zi[n] = y;
                if (x * x + y * y >= 512.0 * 512.0) { len = n; break; }
                double nx = x * x - y * y + cx; y = 2 * x * y + cy; x = nx;
            }
            return (zr, zi, len);
        }
    }
}
