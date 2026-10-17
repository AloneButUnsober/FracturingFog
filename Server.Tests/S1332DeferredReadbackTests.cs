// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1332 — a GPU-coloured SP frame leaves its iteration / smooth / final-z outputs
// on the GPU (IGpuKernel.RunDeferred) and the calculator reads each back the
// first time something asks (Calculators.Gpu.GpuDeferredAux). Checked:
//   • calculator protocol, on a recording fake kernel (no GPU): nothing comes back
//     until a buffer is read; each read pulls only its own output; the distance
//     fill pulls iterations + final z; the values are the eager frame's; a resize
//     drops the pending frame; a GPU → CPU path change leaves the CPU frame's
//     values; the switch off and a CPU palette stay eager;
//   • on the Vulkan device (skipped without one), both buffer placements: deferred
//     == eager bit for bit (Mandelbrot views / sizes / palettes, the escape-time
//     families) and, independently, agrees with the CPU calculator (the eager and
//     deferred paths share the copy code); a pending frame survives another calculator's dispatch (flushed
//     into its own arrays) and its own next frame supersedes it; readers on another
//     thread during a run of frames;
//   • the live host (Vulkan): a default frame (equalisation and relief off) reads
//     nothing back; the Adaptive slider's first tick builds its CDF from the
//     deferred frame and matches an eager build; relief on still gets its field.
// The D3D11 kernel (and its GPU any-escaped count) is covered by --gpudeferprobe.

using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1332DeferredReadbackTests(ITestOutputHelper output) : IDisposable
{
    private const int AllPending = 15;   // iterations | smooth | final z | distance (8)

    public void Dispose() => GpuDeferredReadback.Reset();

    private static bool Same<T>(T[] a, T[] b) where T : unmanaged
        => a.Length == b.Length && MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));

    private static IColorMap GpuPalette(int i = 0)
        => ColorPalette.BuiltIns.Where(m => m is IGpuHlslPalette).Skip(i).First();

    // ── Calculator protocol on a recording fake kernel ─────────────────────────

    /// <summary>A CPU stand-in for a deferring GPU kernel: "renders" a deterministic
    /// pattern keyed by the view into its own buffers, hands them out eagerly or on
    /// ReadDeferred, and counts every output it copies.</summary>
    private sealed class FakeKernel : IGpuKernel
    {
        public int Reads, IterReads, SmoothReads, FinalZReads, DeferredRuns, EagerRuns;
        private bool _palette;
        private int[]? _gi; private float[]? _gs, _gzr, _gzi, _gdr, _gdi;
        private int[]? _defIter; private float[]? _defS, _defZr, _defZi, _defDr, _defDi;
        private GpuAuxOutputs _pending; private int _n;

        public bool HasGpuPalette => _palette;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public string BackendLabel => "Fake";
        public bool SupportsDeferredReadback => true;
        public void SetPalette(IGpuHlslPalette? palette) => _palette = palette != null;
        public void Dispose() { }

        private void Render(int w, int h, double cx, int maxIter, uint[]? colorDst)
        {
            int n = w * h;
            _n = n;
            _gi = new int[n]; _gs = new float[n]; _gzr = new float[n]; _gzi = new float[n]; _gdr = new float[n]; _gdi = new float[n];
            uint seed = (uint)(cx * 1e6) ^ (uint)maxIter;
            for (int i = 0; i < n; i++)
            {
                uint hsh = (uint)i * 2654435761u ^ seed;
                int it = (int)(hsh % (uint)(maxIter + 1));
                _gi[i] = it;
                if (it < maxIter)
                {
                    _gs[i] = it + 0.25f;
                    _gzr[i] = 600f + (i % 97); _gzi[i] = -300f + (i % 31);
                    _gdr[i] = 2f + (i % 13); _gdi[i] = 1f + (i % 7);
                }
                if (colorDst != null) colorDst[i] = 0xFF000000u | (hsh & 0xFFFFFFu);
            }
        }

        private void Copy(GpuAuxOutputs parts, int[] it, float[] s, float[] zr, float[] zi, float[] dr, float[] di)
        {
            if ((parts & GpuAuxOutputs.Iterations) != 0) { Array.Copy(_gi!, it, _n); IterReads++; Reads++; }
            if ((parts & GpuAuxOutputs.Smooth) != 0) { Array.Copy(_gs!, s, _n); SmoothReads++; Reads++; }
            if ((parts & GpuAuxOutputs.FinalZ) != 0)
            {
                Array.Copy(_gzr!, zr, _n); Array.Copy(_gzi!, zi, _n); Array.Copy(_gdr!, dr, _n); Array.Copy(_gdi!, di, _n);
                FinalZReads++; Reads++;
            }
        }

        private void Flush(int[] next)
        {
            if (_pending == GpuAuxOutputs.None) return;
            if (!ReferenceEquals(next, _defIter)) Copy(_pending, _defIter!, _defS!, _defZr!, _defZi!, _defDr!, _defDi!);
            _pending = GpuAuxOutputs.None; _defIter = null;
        }

        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter,
            double bailout2, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst, int[]? perRowMaxIter = null,
            FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f,
            uint[]? colorDst = null, float[]? trapDst = null, GpuDomainWarp warp = default)
        {
            Flush(iterDst);
            EagerRuns++;
            Render(width, height, centerX, maxIter, _palette ? colorDst : null);
            Copy(GpuAuxOutputs.All, iterDst, smoothDst, finalZrDst, finalZiDst, finalDrDst, finalDiDst);
            Reads -= 3; IterReads--; SmoothReads--; FinalZReads--;   // eager copies aren't deferred reads
        }

        public GpuAuxOutputs RunDeferred(int width, int height, double centerX, double centerY, double scale, int maxIter,
            double bailout2, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst, int[]? perRowMaxIter = null,
            FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f,
            uint[]? colorDst = null, float[]? trapDst = null, GpuDomainWarp warp = default)
        {
            Flush(iterDst);
            DeferredRuns++;
            Render(width, height, centerX, maxIter, _palette ? colorDst : null);
            _defIter = iterDst; _defS = smoothDst; _defZr = finalZrDst; _defZi = finalZiDst; _defDr = finalDrDst; _defDi = finalDiDst;
            _pending = GpuAuxOutputs.All;
            return GpuAuxOutputs.All;
        }

        public void ReadDeferred(int[] iterDst, GpuAuxOutputs outputs)
        {
            if (!ReferenceEquals(iterDst, _defIter)) return;
            var parts = outputs & _pending;
            if (parts == GpuAuxOutputs.None) return;
            Copy(parts, _defIter!, _defS!, _defZr!, _defZi!, _defDr!, _defDi!);
            _pending &= ~parts;
        }
    }

    private static MandelbrotCalculator FakeCalc(FakeKernel k, int w = 40, int h = 30, double cx = -0.6, IColorMap? map = null) => new(w, h)
    {
        CenterX = cx, CenterY = 0, Zoom = 1, MaxIterations = 200,
        ColorMap = map ?? GpuPalette(), UseGpuCompute = true, GpuKernel = k,
    };

    private static MandelbrotCalculator EagerTwin(FakeKernel k, double cx = -0.6)
    {
        GpuDeferredReadback.Enabled = false;
        var e = FakeCalc(k, cx: cx);
        e.Calculate();
        GpuDeferredReadback.Enabled = true;
        return e;
    }

    [Fact]
    public void Nothing_Comes_Back_Until_A_Buffer_Is_Read_And_Then_Only_That_Output()
    {
        var k = new FakeKernel();
        var eager = EagerTwin(k);
        var lazy = FakeCalc(k);
        lazy.Calculate();
        Assert.Equal(1, k.DeferredRuns);
        Assert.Equal(AllPending, lazy.GpuAuxPending);
        Assert.Equal(0, k.Reads);
        Assert.True(Same(eager.ColorBuffer, lazy.ColorBuffer));   // colour is eager
        Assert.Equal(0, k.Reads);

        Assert.True(Same(eager.SmoothBuffer, lazy.SmoothBuffer));
        Assert.Equal((1, 0, 1, 0), (k.SmoothReads, k.IterReads, k.Reads, k.FinalZReads));
        Assert.Equal(AllPending & ~2, lazy.GpuAuxPending);

        // Distance needs the iterations and the final z / dz; the fill is the eager formula.
        Assert.True(Same(eager.DistanceBuffer, lazy.DistanceBuffer));
        Assert.Equal((1, 1, 1, 3), (k.SmoothReads, k.IterReads, k.FinalZReads, k.Reads));
        Assert.Equal(0, lazy.GpuAuxPending);
        Assert.True(eager.DistanceBuffer.Any(d => d != 0f));   // precondition: a real fill

        Assert.True(Same(eager.IterationBuffer, lazy.IterationBuffer));
        Assert.True(Same(eager.FinalZrBuffer, lazy.FinalZrBuffer) && Same(eager.FinalDiBuffer, lazy.FinalDiBuffer));
        Assert.Equal(3, k.Reads);   // already on the CPU: no second copy
    }

    [Fact]
    public void Resize_Drops_The_Pending_Frame()
    {
        var k = new FakeKernel();
        var lazy = FakeCalc(k);
        lazy.Calculate();
        lazy.Resize(20, 10);
        Assert.Equal(0, lazy.GpuAuxPending);
        _ = lazy.IterationBuffer; _ = lazy.DistanceBuffer;
        Assert.Equal(0, k.Reads);
    }

    [Fact]
    public void A_Cpu_Frame_After_A_Deferred_One_Holds_The_Cpu_Values()
    {
        var k = new FakeKernel();
        var lazy = FakeCalc(k);
        lazy.Calculate();
        lazy.UseGpuCompute = false;
        lazy.Calculate();
        var cpu = new MandelbrotCalculator(40, 30) { CenterX = -0.6, CenterY = 0, Zoom = 1, MaxIterations = 200, ColorMap = GpuPalette() };
        cpu.Calculate();
        Assert.True(Same(cpu.IterationBuffer, lazy.IterationBuffer));
        Assert.True(Same(cpu.SmoothBuffer, lazy.SmoothBuffer));
        Assert.True(Same(cpu.DistanceBuffer, lazy.DistanceBuffer));
        Assert.True(Same(cpu.FinalZrBuffer, lazy.FinalZrBuffer));
        Assert.True(Same(cpu.ColorBuffer, lazy.ColorBuffer));
        Assert.Equal(0, lazy.GpuAuxPending);
    }

    [Fact]
    public void A_Frame_Pending_Across_Another_Calculators_Dispatch_Keeps_Its_Values()
    {
        var k = new FakeKernel();
        var eager = EagerTwin(k, cx: -0.6);
        var lazy = FakeCalc(k, cx: -0.6);
        lazy.Calculate();
        var other = FakeCalc(k, cx: 0.3);
        other.Calculate();   // the kernel flushes lazy's frame into lazy's arrays first
        Assert.True(Same(eager.IterationBuffer, lazy.IterationBuffer));
        Assert.True(Same(eager.DistanceBuffer, lazy.DistanceBuffer));
        Assert.True(Same(eager.FinalZiBuffer, lazy.FinalZiBuffer));
        var otherEager = EagerTwin(k, cx: 0.3);
        Assert.True(Same(otherEager.SmoothBuffer, other.SmoothBuffer));
    }

    [Fact]
    public void Switch_Off_And_A_Cpu_Palette_Stay_Eager()
    {
        var k = new FakeKernel();
        GpuDeferredReadback.Enabled = false;
        var off = FakeCalc(k);
        off.Calculate();
        Assert.Equal((0, 1, 0), (k.DeferredRuns, k.EagerRuns, off.GpuAuxPending));

        GpuDeferredReadback.Enabled = true;
        var cpuPalette = ColorPalette.BuiltIns.First(m => m is not IGpuHlslPalette && m is not IOrbitAwareColorMap);
        var cpuColoured = FakeCalc(k, map: cpuPalette);
        cpuColoured.Calculate();   // the colour pass needs every output on the CPU
        Assert.Equal((0, 2, 0), (k.DeferredRuns, k.EagerRuns, cpuColoured.GpuAuxPending));
    }

    [Fact]
    public void Escape_Time_Families_Defer_The_Same_Way()
    {
        var k = new FakeKernel();
        EscapeTimeCalculator Make() => new(40, 30)
        {
            FractalType = FractalType.Julia, FractalParameters = new FractalParameters { JuliaC = new Complex(-0.8, 0.156) },
            ColorMap = GpuPalette(), MaxIterations = 200, Zoom = 1, UseGpuCompute = true, GpuKernel = k,
        };
        GpuDeferredReadback.Enabled = false;
        var eager = Make(); eager.Calculate();
        GpuDeferredReadback.Enabled = true;
        var lazy = Make(); lazy.Calculate();
        Assert.Equal(AllPending, lazy.GpuAuxPending);
        Assert.Equal(0, k.Reads);
        Assert.True(Same(eager.ColorBuffer, lazy.ColorBuffer));
        Assert.True(Same(eager.DistanceBuffer, lazy.DistanceBuffer));
        Assert.True(Same(eager.IterationBuffer, lazy.IterationBuffer) && Same(eager.SmoothBuffer, lazy.SmoothBuffer));
        Assert.Equal(3, k.Reads);
    }

    // ── Vulkan ─────────────────────────────────────────────────────────────────

    private static VulkanComputeKernel Kernel(VulkanBufferPlacement? placement = null)
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext(placement);
        if (k is null) Assert.Skip("no Vulkan device on this host");
        Assert.True(k.SupportsDeferredReadback);
        return k;
    }

    private static void AssertSameFrame(string label, MandelbrotCalculator e, MandelbrotCalculator l)
    {
        Assert.True(Same(e.ColorBuffer, l.ColorBuffer), label + ": colour");
        Assert.True(Same(e.IterationBuffer, l.IterationBuffer), label + ": iterations");
        Assert.True(Same(e.SmoothBuffer, l.SmoothBuffer), label + ": smooth");
        Assert.True(Same(e.DistanceBuffer, l.DistanceBuffer), label + ": distance");
        Assert.True(Same(e.FinalZrBuffer, l.FinalZrBuffer) && Same(e.FinalZiBuffer, l.FinalZiBuffer)
                    && Same(e.FinalDrBuffer, l.FinalDrBuffer) && Same(e.FinalDiBuffer, l.FinalDiBuffer), label + ": final z / dz");
    }

    /// <summary>Independent anchor (eager == lazy shares the readback code, so it
    /// can't see a wrong copy): the deferred frame's outputs agree with the CPU
    /// calculator's where the escape iteration does — smooth to fp32 noise, distance
    /// and final z to a relative 1e-2 (medians).</summary>
    private static void AssertMatchesCpu(string label, MandelbrotCalculator gpu, int maxIter)
    {
        var cpu = new MandelbrotCalculator(gpu.Width, gpu.Height)
        {
            CenterX = gpu.CenterX, CenterY = gpu.CenterY, Zoom = gpu.Zoom, MaxIterations = maxIter, ColorMap = gpu.ColorMap,
        };
        cpu.Calculate();
        int n = gpu.Width * gpu.Height;
        var shared = Enumerable.Range(0, n).Where(i => gpu.IterationBuffer[i] == cpu.IterationBuffer[i]).ToArray();
        Assert.True(shared.Length > n / 2, $"{label}: only {shared.Length}/{n} pixels share their iteration with the CPU");
        var esc = shared.Where(i => cpu.IterationBuffer[i] < maxIter).ToArray();
        if (esc.Length < 32) return;   // an all-in-set view: nothing more to compare
        static double Median(System.Collections.Generic.IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }
        double sm = Median(esc.Select(i => (double)Math.Abs(gpu.SmoothBuffer[i] - cpu.SmoothBuffer[i])));
        double de = Median(esc.Select(i => Math.Abs(gpu.DistanceBuffer[i] - cpu.DistanceBuffer[i]) / Math.Max(1e-12, Math.Abs(cpu.DistanceBuffer[i]))));
        double zr = Median(esc.Select(i => Math.Abs(gpu.FinalZrBuffer[i] - cpu.FinalZrBuffer[i]) / Math.Max(1.0, Math.Abs(cpu.FinalZrBuffer[i]))));
        Assert.True(sm < 0.02, $"{label}: median |smooth GPU − CPU| {sm:E2}");
        Assert.True(de < 1e-2, $"{label}: median relative distance GPU vs CPU {de:E2}");
        Assert.True(zr < 1e-2, $"{label}: median relative final zr GPU vs CPU {zr:E2}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Vulkan_Deferred_Equals_Eager_Bit_For_Bit(bool deviceOutputs)
    {
        using var k = Kernel(deviceOutputs ? VulkanBufferPlacement.DeviceOutputs : null);
        var views = new (double Cx, double Cy, double Zoom, int Iter)[]
        {
            (-0.6, 0.0, 1.0, 256), (-0.7453, 0.1127, 300.0, 1000), (-0.1225611668766536, 0.7448617666197442, 175.0, 400),
        };
        int cases = 0;
        foreach (var v in views)
        foreach (var (w, h) in new[] { (96, 64), (77, 41) })
        foreach (int pal in new[] { 0, 1 })
        {
            MandelbrotCalculator Make() => new(w, h)
            {
                CenterX = v.Cx, CenterY = v.Cy, Zoom = v.Zoom, MaxIterations = v.Iter,
                ColorMap = GpuPalette(pal), UseGpuCompute = true, GpuKernel = k,
            };
            GpuDeferredReadback.Enabled = false;
            var eager = Make(); eager.Calculate();
            GpuDeferredReadback.Enabled = true;
            var lazy = Make(); lazy.Calculate();
            string label = $"{v} {w}x{h} palette {pal}";
            Assert.True(lazy.LastFrameUsedGpuCompute, label);
            Assert.Equal(AllPending, lazy.GpuAuxPending);
            Assert.Null(lazy.GpuFrameAnyEscaped);   // Vulkan doesn't count: the check reads the iterations
            Assert.Equal(IterationRescue.IsAllInSet(eager), IterationRescue.IsAllInSet(lazy));
            AssertSameFrame(label, eager, lazy);
            if (pal == 0) AssertMatchesCpu(label, lazy, v.Iter);
            cases++;
        }
        Assert.Equal(12, cases);

        foreach (var (type, p) in new (FractalType, FractalParameters)[]
        {
            (FractalType.Julia, new FractalParameters { JuliaC = new Complex(-0.8, 0.156) }),
            (FractalType.BurningShip, new FractalParameters()),
            (FractalType.Multibrot, new FractalParameters { MultibrotExponent = 4 }),
            (FractalType.Phoenix, new FractalParameters { PhoenixP = new Complex(-0.5, 0) }),
        })
        {
            EscapeTimeCalculator Make() => new(96, 64)
            {
                FractalType = type, FractalParameters = p, ColorMap = GpuPalette(),
                MaxIterations = 300, Zoom = 1, UseGpuCompute = true, GpuKernel = k,
            };
            GpuDeferredReadback.Enabled = false;
            var eager = Make(); eager.Calculate();
            GpuDeferredReadback.Enabled = true;
            var lazy = Make(); lazy.Calculate();
            Assert.Equal(AllPending, lazy.GpuAuxPending);
            Assert.True(Same(eager.ColorBuffer, lazy.ColorBuffer), $"{type}: colour");
            Assert.True(Same(eager.IterationBuffer, lazy.IterationBuffer) && Same(eager.SmoothBuffer, lazy.SmoothBuffer)
                        && Same(eager.DistanceBuffer, lazy.DistanceBuffer) && Same(eager.FinalDrBuffer, lazy.FinalDrBuffer),
                $"{type}: outputs");
        }
    }

    [Fact]
    public void Vulkan_Flush_And_Supersede()
    {
        using var k = Kernel();
        MandelbrotCalculator Make(int w, int h, double cx, double cy, double zoom, int iter) => new(w, h)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = iter,
            ColorMap = GpuPalette(), UseGpuCompute = true, GpuKernel = k,
        };
        GpuDeferredReadback.Enabled = false;
        var eagerA = Make(96, 64, -0.7453, 0.1127, 300, 800); eagerA.Calculate();
        var eagerB2 = Make(50, 40, -1.25, 0.02, 20, 600); eagerB2.Calculate();
        GpuDeferredReadback.Enabled = true;

        var a = Make(96, 64, -0.7453, 0.1127, 300, 800); a.Calculate();
        var b = Make(50, 40, 0.3, 0.5, 3, 400); b.Calculate();   // flushes a's frame into a's arrays
        Assert.Equal(AllPending, a.GpuAuxPending);
        AssertSameFrame("flushed", eagerA, a);

        b.CenterX = -1.25; b.CenterY = 0.02; b.Zoom = 20; b.MaxIterations = 600;
        b.Calculate();   // supersedes b's own pending frame
        AssertSameFrame("superseded", eagerB2, b);
    }

    [Fact]
    public void Vulkan_Readers_On_Another_Thread_During_Frames()
    {
        using var k = Kernel();
        var calc = new MandelbrotCalculator(96, 64) { ColorMap = GpuPalette(), UseGpuCompute = true, GpuKernel = k, MaxIterations = 500 };
        var views = new[] { (-0.6, 0.0, 1.0), (-0.7453, 0.1127, 300.0), (0.28, 0.01, 50.0) };
        using var stop = new CancellationTokenSource();
        Exception? readerError = null;
        long reads = 0;
        var reader = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    _ = calc.IterationBuffer[17]; _ = calc.SmoothBuffer[5]; _ = calc.DistanceBuffer[33]; _ = calc.FinalDiBuffer[9];
                    Interlocked.Increment(ref reads);
                }
            }
            catch (Exception ex) { readerError = ex; }
        });
        reader.Start();
        for (int f = 0; f < 30; f++)
        {
            var v = views[f % views.Length];
            calc.CenterX = v.Item1; calc.CenterY = v.Item2; calc.Zoom = v.Item3;
            calc.Calculate();
        }
        stop.Cancel();
        reader.Join();
        Assert.Null(readerError);
        Assert.True(Interlocked.Read(ref reads) > 0);

        // The last frame, read now, is still the eager frame's.
        GpuDeferredReadback.Enabled = false;
        var last = views[29 % views.Length];
        var eager = new MandelbrotCalculator(96, 64)
        {
            ColorMap = GpuPalette(), UseGpuCompute = true, GpuKernel = k, MaxIterations = 500,
            CenterX = last.Item1, CenterY = last.Item2, Zoom = last.Item3,
        };
        eager.Calculate();
        AssertSameFrame("after concurrent reads", eager, calc);
    }

    // ── Live host ──────────────────────────────────────────────────────────────

    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static void HostFrame(FractalRenderHost host)
    {
        using var done = new ManualResetEventSlim();
        EventHandler<FracturingFog.Render.RenderFrameInfo> h = (_, _) => done.Set();
        host.FrameCompleted += h;
        try
        {
            host.Trigger(progressive: false);
            Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no frame completed");
        }
        finally { host.FrameCompleted -= h; }
    }

    [Fact]
    public void Live_Host_Default_Frame_Reads_Nothing_Back_And_Adaptive_Builds_From_It()
    {
        using var k = Kernel();
        var state = new FractalViewState { FractalType = FractalType.Mandelbrot, CenterX = -0.7453, CenterY = 0.1127, Zoom = 300 };
        using var host = new FractalRenderHost(new NullRenderer(), state, 96, 64, GpuPalette());
        host.GpuKernelFactory = (_, _) => k;
        host.UseGpuCompute = true;
        Assert.True(host.Mandelbrot.UseGpuCompute);

        HostFrame(host);
        var calc = host.Mandelbrot;
        Assert.True(calc.LastFrameUsedGpuCompute);
        Assert.Equal(0, state.HistogramEq);
        Assert.False(state.FractalParameters.Relief2DEnabled);
        // The whole post-frame tail read nothing back but the iterations, which the
        // solid-frame check scans because Vulkan doesn't count escapes on the GPU (the
        // D3D11 kernel does: --gpudeferprobe).
        Assert.False(state.IterLocked);
        Assert.Null(calc.GpuFrameAnyEscaped);
        Assert.Equal(AllPending & ~1, calc.GpuAuxPending);

        // The Adaptive slider's first tick builds the CDF from the deferred frame.
        state.HistogramEq = 40;
        host.RepaintWithAdaptive();
        Assert.Equal(0, calc.GpuAuxPending & (1 | 2));   // it read the iterations / smooth
        var adaptive = (uint[])calc.ColorBuffer.Clone();

        // Independent: the same frame computed eagerly, equalised directly.
        GpuDeferredReadback.Enabled = false;
        calc.Calculate();
        Assert.Equal(0, calc.GpuAuxPending);
        Assert.True(calc.BuildHistogramCdf(out var cdf, out int bins, out int src));
        calc.ApplyHistogramEqualizationWithCdf(cdf!, bins, src, 0.4);
        Assert.True(Same(calc.ColorBuffer, adaptive));
        Assert.False(Same(calc.ColorBuffer, adaptive.Select(c => c ^ 1u).ToArray()));
        output.WriteLine($"adaptive frame: {adaptive.Distinct().Count()} colours");
    }

    [Fact]
    public void Live_Host_Relief_Still_Gets_Its_Field()
    {
        using var k = Kernel();
        var state = new FractalViewState { FractalType = FractalType.Mandelbrot, CenterX = -0.7453, CenterY = 0.1127, Zoom = 300 };
        state.FractalParameters.Relief2DEnabled = true;
        using var host = new FractalRenderHost(new NullRenderer(), state, 96, 64, GpuPalette());
        host.GpuKernelFactory = (_, _) => k;
        host.UseGpuCompute = true;
        HostFrame(host);
        Assert.True(host.Mandelbrot.LastFrameUsedGpuCompute);
        Assert.Equal(0, host.Mandelbrot.GpuAuxPending & 2);   // relief read the smooth field back
    }
}
