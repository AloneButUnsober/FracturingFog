// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BuddhaFamilyCalculator.cs
//
// Shared Monte Carlo core for Buddhabrot, Nebulabrot, AntiBuddhabrot, and
// AntiNebulabrot. Each variant differs only in:
//   • IsInSet — record orbits that escape (false) or stay bounded (true).
// Output composition is controlled by FractalParameters.BuddhaColorMode:
//   • NebulabrotBands — three iteration bands (Low/Mid/High) feed R/G/B,
//     log-normalised per channel. Classic Nebulabrot look.
//   • ColorMap        — sum hits across bands into one buffer, log-normalise,
//     route through the active IColorMap.
//
// Quality is controlled by FractalParameters.BuddhaQualityMode:
//   • Standard       — nearest-pixel splat, per-channel log norm. Fast,
//     classic look.
//   • HighDefinition — stochastic bilinear splat (subpixel AA), real-axis
//     mirror sampling (free 2× effective samples), joint-channel norm,
//     low-hit noise-floor reject (kills speckle background lift). Slower
//     but markedly smoother and cleaner background.
//
// Sampling strategy (independent toggles in FractalParameters):
//   • BuddhaMetropolis — Metropolis-Hastings importance sampling. Per-thread
//     MH chain mutates a seed c value; accept/reject by viewport-hit score
//     ratio. Concentrates samples on c values that contribute to the visible
//     image. Big quality gain when zoomed in. Chain state persists across
//     progressive batches.
//   • BuddhaProgressive — split the sample budget into chunks; merge +
//     composite to the output buffer between chunks. Cancel mid-render still
//     produces a usable partial image.
//
// Always-on optimisations:
//   • Cardioid + period-2 bulb early reject for escape mode (those c values
//     never escape so iterating them is wasted CPU). Disabled for in-set
//     mode where bulb interior IS the target.
//   • In-set band classifier uses mean |z|² across orbit instead of last
//     sample — last-sample classifier was near-random for bounded orbits.
//   • Orbit buffer hard-cap at 200K to prevent per-thread allocation blow-up
//     when MaxIterations is set very high in in-set mode.
//
// Uniform sampling (#838 / #1218): one algorithm on both paths — the GPU kernel
// (BuddhaKernelSource, D3D11 + Vulkan) when GPU compute is on, otherwise its CPU
// twin BuddhaUniformSampler (counter-based stream, float orbits, run in parallel).
// Both add into the same hit histograms and render the same image for a seed, so
// the GPU switch, --batch and the CPU core count do not change the picture.
// Composite / Recolor / relief are unchanged. Metropolis-Hastings (including the
// zoom-compensation auto-enable), the Dual Buddhabrot samplers and zooms past
// MaxGpuBuddhaZoom (float orbits) use the double-precision System.Random sampler.
//
// #1218 — no sampler draws z0 = 0: every kept orbit starts there, so it piled one
// hit per orbit on a single pixel (~250x the next hottest) and the log
// normalisation divided the whole image by it — the near-black default
// Buddhabrot. Escaping orbits shorter than BuddhaMinIter are not drawn either:
// the fast escapers are most of the samples and wash the |c| <= 2 disc (the red
// disc behind the default Nebulabrot).

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using GpuRoute = FracturingFog.Render.GpuRoute;

namespace FracturingFog;

public abstract class BuddhaFamilyCalculator : IFractalCalculator, IHeightFieldSource, ISupportsCheapRecolor,
    FracturingFog.Render.IGpuRouteSource
{
    // Per-thread orbit buffer size limit. At 200K × 8 bytes × 2 arrays × 32
    // threads ≈ 100 MB — high but manageable; without the cap a 1M iteration
    // in-set render would allocate ~512 MB just for orbit scratch.
    protected const int MaxOrbitCap = 200_000;

    // #837 — zoom-detail compensation thresholds. Below the threshold zoom the
    // compensation is a no-op (byte-identical to before the feature); at/above
    // it the effective sample budget scales with zoom, capped at the factor.
    private const double BuddhaZoomCompThreshold = 1.2;
    private const double BuddhaZoomCompMaxSampleFactor = 8.0;

    // #1224 — BuddhaSamples is the budget for a 640×480 frame. A larger window
    // spreads a fixed budget over more pixels (0.4 hits/px at 2560×1440 against
    // 4.6 at 640×480: dim and grainy), so with BuddhaScaleSamplesWithWindow the
    // budget grows with the pixel count. Never scaled down: frames up to 640×480
    // sample exactly BuddhaSamples, as before.
    public const int SampleReferencePixels = 640 * 480;

    // #1224 — zoom compensation used to turn Metropolis on past zoom 1.2. On
    // the shared uniform sampler (cycle detection, all cores) uniform sampling is
    // far cheaper for less noise at those zooms: 640×480, zoom 6, Buddhabrot
    // Metropolis 12.8 s at block-histogram seed noise 0.18 against uniform 8M
    // samples 76 ms at 0.15; AntiBuddhabrot 150 s against 414 ms at 0.08. (One
    // Metropolis step re-draws the chain's whole orbit, and the chains are
    // correlated, so cutting their steps raised the noise nearly as fast as it
    // cut the time.) So zoom compensation now only raises the sample count, and
    // turns Metropolis on past the zoom where the float uniform sampler stops
    // (MaxGpuBuddhaZoom). Without the shared sampler (Dual) it is on past 1.2,
    // as before. Uniform still wins at zoom 80 for Buddhabrot (32M samples
    // 256 ms, noise 0.23, against Metropolis 14 s, 0.19); AntiBuddhabrot there is
    // cleaner on Metropolis but takes 20 s.
    //
    // Uniform samples are cheap, so their zoom boost goes to ×64 (Metropolis
    // keeps ×8); with window scaling the window × zoom factor is held to ×64 so
    // a large zoomed live frame stays bounded (500K → at most 32M samples,
    // ~0.3 s Buddhabrot on 12 cores).
    private const double BuddhaZoomCompUniformMaxFactor = 64.0;
    protected virtual double AutoMetropolisZoom =>
        SupportsGpuSampling ? MaxGpuBuddhaZoom : BuddhaZoomCompThreshold;

    /// <summary>#1224 — samples the last Calculate ran (uniform samples or
    /// Metropolis steps), after window and zoom scaling.</summary>
    public int LastEffectiveSamples { get; private set; }

    /// <summary>#1224 — true when the last Calculate sampled with Metropolis
    /// (ticked, or turned on by zoom compensation).</summary>
    public bool LastCalculateUsedMetropolis { get; private set; }

    /// <summary>#1224 — the sample budget for a frame: BuddhaSamples, scaled up
    /// with the window (when enabled) and with zoom (zoom compensation, which
    /// also turns Metropolis on past <paramref name="autoMetropolisZoom"/>).</summary>
    public static int EffectiveSamples(FractalParameters p, int width, int height, double zoom,
                                       double autoMetropolisZoom, out bool metropolis)
    {
        bool zoomComp = p.BuddhaZoomCompensation && zoom > BuddhaZoomCompThreshold;
        metropolis = p.BuddhaMetropolis || (zoomComp && zoom > autoMetropolisZoom);
        double s = Math.Max(1, p.BuddhaSamples);
        if (metropolis)
        {
            // Metropolis already deposits thousands of hits per pixel; its noise is
            // the chains' correlation, not the pixel count, so the window does not
            // scale it (it would only multiply a multi-second frame).
            if (zoomComp) s *= Math.Clamp(zoom, 1.0, BuddhaZoomCompMaxSampleFactor);
        }
        else
        {
            double windowFactor = p.BuddhaScaleSamplesWithWindow
                ? Math.Max(1.0, (double)width * height / SampleReferencePixels) : 1.0;
            s *= windowFactor;
            if (zoomComp)
                s *= Math.Clamp(zoom, 1.0, Math.Max(1.0, BuddhaZoomCompUniformMaxFactor / windowFactor));
        }
        return (int)Math.Clamp(s, 1, int.MaxValue);
    }

    // Progressive batch count. 8 gives ~12.5% increments — frequent enough for
    // perceived live preview, infrequent enough that composite overhead stays
    // negligible relative to sampling.
    private const int ProgressiveBatches = 8;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // #139 — Relief 3D height field. The orbit-density histogram IS the natural
    // relief: dense orbit-traced regions rise, empty background is the base
    // plane. Log-normalised so the height reads as terrain, not spikes.
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; } = -0.5;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 50_000;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <summary>#806 — override the progressive batch count for this render (null
    /// = the default <see cref="ProgressiveBatches"/>). The video-slideshow
    /// Buddhabrot accumulation leg raises it so the "developing" progression has
    /// more, finer steps. Only takes effect when BuddhaProgressive is on.</summary>
    public int? ProgressiveBatchesOverride { get; set; }

    /// <summary>#806 — invoked after each progressive batch is composited into
    /// <see cref="ColorBuffer"/>, as (completedBatch, totalBatches). Lets the
    /// video Buddhabrot hold present + pace the accumulation as it builds. Runs on
    /// the Calculate() thread, between batches; null = no callback (the normal
    /// interactive path). Never fired in single-pass (non-progressive) mode.</summary>
    public Action<int, int>? OnBatchComposited { get; set; }

    /// <summary>#838 — sample on <see cref="GpuKernel"/> when it supports the
    /// Buddhabrot pass. Set by the host with the 2D GPU compute switch.</summary>
    public bool UseGpuCompute { get; set; }

    /// <summary>#838 — the GPU compute kernel (shared with the 2D calculators).</summary>
    public FracturingFog.Rendering.IGpuKernel? GpuKernel { get; set; }

    // #838 / #1218 — FF_GPU_BUDDHA=1 opts in; a per-thread override for tests.
    private static readonly bool s_gpuBuddhaEnv =
        Environment.GetEnvironmentVariable("FF_GPU_BUDDHA") is "1" or "true" or "on" or "yes";
    [ThreadStatic] private static bool? t_gpuBuddha;

    /// <summary>#838 / #1218 — sample uniform Buddhabrot renders on the GPU kernel
    /// (still needs <see cref="UseGpuCompute"/>). Off by default: the GPU and CPU run
    /// one shared sampler and render the same image, and the CPU's run — cycle
    /// detection included — is the faster one on the hardware this was measured on
    /// (GT 710, 640x480 defaults: Buddhabrot 66 vs 195 ms, AntiBuddhabrot 125 vs
    /// 1214 ms). <c>FF_GPU_BUDDHA=1</c> opts in (a card with far more compute than the
    /// CPU). Setting it overrides for the calling thread only (tests).</summary>
    public static bool UseGpuBuddha
    {
        get => t_gpuBuddha ?? s_gpuBuddhaEnv;
        set => t_gpuBuddha = value;
    }

    // #1218 — test hook (thread-static, like the other calculator test knobs).
    [ThreadStatic] private static bool t_legacyUniform;

    /// <summary>#1218 — false routes uniform renders to the double-precision
    /// System.Random sampler instead of the shared one (BuddhaUniformSampler / the GPU
    /// kernel). Tests use it to compare against that sampler (the Dual Buddhabrot's Z
    /// channel is its hit total). Per thread; default true.</summary>
    public static bool UseSharedUniformSampler
    {
        get => !t_legacyUniform;
        set => t_legacyUniform = !value;
    }

    /// <summary>#838 — the GPU orbit is float: past this zoom the viewport pixel
    /// approaches float resolution of z (|z| ≤ 2), so the GPU leaves sampling to the
    /// CPU's double orbit.</summary>
    public const double MaxGpuBuddhaZoom = 100.0;

    /// <summary>#838 — true when the last Calculate sampled on the GPU.</summary>
    public bool LastCalculateUsedGpu { get; private set; }

    /// <summary>#1173-M / #838 — the GPU route of the last Calculate.</summary>
    public GpuRoute LastGpuRoute { get; private set; }

    /// <summary>#838 — false for a sampler the GPU kernel does not implement (Dual
    /// Buddhabrot's two-orbit samplers).</summary>
    protected virtual bool SupportsGpuSampling => true;

    /// <summary>Record orbits that DO NOT escape (in-set) when true;
    /// orbits that DO escape (classic Buddhabrot) when false.</summary>
    protected abstract bool IsInSet { get; }

    /// <summary>#836 — the composite this fractal TYPE mandates, independent of
    /// the shared <see cref="FractalParameters.BuddhaColorMode"/> UI toggle.
    /// Buddhabrot / AntiBuddhabrot force single-channel <c>ColorMap</c>;
    /// Nebulabrot / AntiNebulabrot force RGB <c>NebulabrotBands</c>. Null =
    /// honour the param (no type forces it). Without this the four types
    /// collapsed to two — Buddhabrot rendered identically to Nebulabrot because
    /// nothing tied the type to a composite, so both used the default
    /// NebulabrotBands (#65).</summary>
    protected virtual BuddhaColorMode? ForcedColorMode => null;

    private uint[] _hitsR = Array.Empty<uint>();
    private uint[] _hitsG = Array.Empty<uint>();
    private uint[] _hitsB = Array.Empty<uint>();

    /// <summary>#1124 — the three accumulated hit histograms (classic: the Low /
    /// Mid / High iteration bands; Dual Buddhabrot: the Z / CB / CE outcome
    /// channels). Retained after Calculate for Recolor / diagnostics.</summary>
    public uint[] HitsR => _hitsR;
    public uint[] HitsG => _hitsG;
    public uint[] HitsB => _hitsB;

    protected BuddhaFamilyCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        SmoothBuffer = new float[n];
        _hitsR = new uint[n];
        _hitsG = new uint[n];
        _hitsB = new uint[n];
    }

    /// <summary>Per-thread Metropolis-Hastings chain state. Persists across
    /// progressive batches so the chain keeps exploring without restart.</summary>
    protected sealed class MhState
    {
        public double Cx, Cy;
        public double[] OrbitR = Array.Empty<double>();
        public double[] OrbitI = Array.Empty<double>();
        public int RecLen;
        public int ClassIter;
        public int Score;
        public bool HasSeed;
    }

    /// <summary>#1124 — a subclass may skip the sample pass when nothing that
    /// affects sampling changed since the last completed Calculate (colour-only
    /// edit): Calculate then only re-composites. Default false = classic types
    /// always re-sample.</summary>
    protected virtual bool CanReuseSamples() => false;

    /// <summary>#1124 — called at the end of Calculate; <paramref name="completed"/>
    /// is false when the sample pass was cancelled (cache must not be trusted).</summary>
    protected virtual void OnSamplingFinished(bool completed) { }

    /// <summary>#1124 — called once per Calculate before any batch, so a subclass
    /// can allocate per-thread sampler state.</summary>
    protected virtual void PrepareSampling(int threads, int maxOrbit, bool mh) { }

    /// <summary>True when the last Calculate re-composited cached samples instead
    /// of re-sampling (diagnostics / tests).</summary>
    public bool LastCalculateReusedSamples { get; private set; }

    public void Calculate(CancellationToken ct = default)
    {
        LastCalculateUsedGpu = false;
        LastCalculateReusedSamples = CanReuseSamples();
        if (LastCalculateReusedSamples)
        {
            Composite();
            LastGpuRoute = UseGpuCompute ? GpuRoute.Cpu("cached samples", "re-composited cached samples") : GpuRoute.NotRequested;
            return;
        }

        Array.Clear(_hitsR);
        Array.Clear(_hitsG);
        Array.Clear(_hitsB);

        int width = Width;
        int height = Height;
        int samples;
        int low = FractalParameters.BuddhaIterLow;
        int mid = FractalParameters.BuddhaIterMid;
        int high = FractalParameters.BuddhaIterHigh;
        bool hd = FractalParameters.BuddhaQualityMode == BuddhaQualityMode.HighDefinition;
        bool mh;
        bool progressive = FractalParameters.BuddhaProgressive;
        int minIter = Math.Max(0, FractalParameters.BuddhaMinIter);   // #1218

        // #837 — zoom-detail compensation. A zoomed viewport catches far fewer
        // of the fixed-domain sample orbits, so coverage collapses and the frame
        // reads dark/grainy (sparse hits, not under-normalisation). Past the
        // threshold, scale the effective sample budget with zoom (capped) and
        // (past AutoMetropolisZoom) turn on Metropolis-Hastings, which
        // concentrates samples on c values whose orbits reach the visible
        // pixels. #1224 — the budget also grows with the window.
        samples = EffectiveSamples(FractalParameters, width, height, Zoom, AutoMetropolisZoom, out mh);
        LastEffectiveSamples = samples;
        LastCalculateUsedMetropolis = mh;

        int maxOrbit = IsInSet ? Math.Max(high, MaxIterations) : high;
        if (maxOrbit > MaxOrbitCap) maxOrbit = MaxOrbitCap;

        double scale = (3.5 / Math.Max(width, height)) / Zoom;
        double midX = CenterX;
        double midY = CenterY;

        int threads = Math.Max(1, Environment.ProcessorCount);
        int batches = progressive ? Math.Max(1, ProgressiveBatchesOverride ?? ProgressiveBatches) : 1;
        int samplesPerBatch = Math.Max(1, samples / batches);
        int perThreadPerBatch = Math.Max(1, samplesPerBatch / threads);

        // #838 / #1218 — the shared uniform sampler (GPU kernel, or its CPU twin)
        // when the render is uniform and shallow enough for float orbits.
        bool shared = UseSharedUniformSampler && !mh && SupportsGpuSampling && Zoom <= MaxGpuBuddhaZoom;
        var gpu = GpuSamplingKernel(mh);
        _minIter = minIter;

        // Per-thread CPU histograms, allocated on first CPU batch (a GPU render
        // that never falls back never needs them).
        uint[][]? localR = null, localG = null, localB = null;
        void EnsureLocals()
        {
            if (localR != null) return;
            localR = new uint[threads][];
            localG = new uint[threads][];
            localB = new uint[threads][];
            for (int t = 0; t < threads; t++)
            {
                localR[t] = new uint[width * height];
                localG[t] = new uint[width * height];
                localB[t] = new uint[width * height];
            }
        }

        // Per-thread MH state (when MH disabled, never touched).
        MhState[]? mhStates = null;
        if (mh)
        {
            mhStates = new MhState[threads];
            for (int t = 0; t < threads; t++)
            {
                mhStates[t] = new MhState
                {
                    OrbitR = new double[maxOrbit],
                    OrbitI = new double[maxOrbit],
                };
            }
        }

        bool inSet = IsInSet;
        bool skipBulbs = !inSet;
        PrepareSampling(threads, maxOrbit, mh);

        // #193 — deterministic seed. Derive per-(thread, batch) seeds from the
        // user-set BuddhaSeed instead of the wall clock so identical params
        // render an identical image. Was Environment.TickCount, which made
        // every re-sample a different random realization — so any setting
        // change (which forces a full re-sample on this alt calculator) made
        // the fractal appear to 'morph'.
        int baseSeed = FractalParameters.BuddhaSeed;

        for (int batch = 0; batch < batches; batch++)
        {
            if (ct.IsCancellationRequested) break;

            if (shared)
            {
                var sb = new FracturingFog.Rendering.GpuBuddhaBatch(
                    width, height, scale, midX, midY, maxOrbit, inSet, hd, low, mid,
                    unchecked((uint)baseSeed), batch, samplesPerBatch, minIter);
                bool done = false;
                if (gpu != null)
                {
                    try
                    {
                        gpu.RunBuddhaBatch(sb, _hitsR, _hitsG, _hitsB);
                        LastCalculateUsedGpu = true;
                        done = true;
                    }
                    catch (Exception ex)
                    {
                        // A failed batch adds nothing (the GPU result is read back only
                        // on success): the CPU twin runs it and the rest — same image.
                        Console.Error.WriteLine($"[Buddhabrot] GPU sampling failed, CPU fallback: {ex.Message}");
                        LastGpuRoute = GpuRoute.Cpu("GPU error", $"GPU Buddhabrot sampling failed: {ex.Message}");
                        gpu = null;
                        LastCalculateUsedGpu = false;
                    }
                }
                if (!done && !RunSharedOnCpu(sb, ct)) break;
                Composite();
                if (progressive) OnBatchComposited?.Invoke(batch + 1, batches);
                continue;
            }
            EnsureLocals();

            Parallel.For(0, threads, new ParallelOptions { CancellationToken = ct }, t =>
            {
                if (ct.IsCancellationRequested) return;
                int seed = unchecked(baseSeed * 73856093 + t * 19349663 + batch * 83492791);
                var rng = new Random(seed);

                SampleBatch(t, batch, mh ? mhStates![t] : null, rng, perThreadPerBatch,
                            localR![t], localG![t], localB![t],
                            maxOrbit, inSet, skipBulbs, hd,
                            scale, midX, midY, width, height, low, mid);
            });

            if (ct.IsCancellationRequested && !progressive) break;

            // Merge locals → globals (additive), then clear locals for next batch.
            // Split by pixel block, not by thread, so the merge runs in parallel
            // (it was serial: threads × pixels adds, ~0.5 s a frame at 2560×1440).
            bool clearLocals = progressive;
            ForPixelBlocks(_hitsR.Length, (from, to) =>
            {
                for (int t = 0; t < threads; t++)
                {
                    var lR = localR![t]; var lG = localG![t]; var lB = localB![t];
                    for (int i = from; i < to; i++)
                    {
                        _hitsR[i] += lR[i];
                        _hitsG[i] += lG[i];
                        _hitsB[i] += lB[i];
                    }
                    if (clearLocals)
                    {
                        Array.Clear(lR, from, to - from);
                        Array.Clear(lG, from, to - from);
                        Array.Clear(lB, from, to - from);
                    }
                }
            });

            // Composite to ColorBuffer. Progressive mode does it every batch
            // so a mid-render cancel still yields a usable image; single-pass
            // mode does it once after the only batch.
            Composite();

            // #806 — let the video Buddhabrot hold present + pace the growing
            // accumulation. Progressive only; skipped in single-pass.
            if (progressive)
                OnBatchComposited?.Invoke(batch + 1, batches);
        }
        OnSamplingFinished(!ct.IsCancellationRequested);
    }

    /// <summary>#1218 — the shared uniform sampler on the CPU: the batch split into
    /// chunks across threads, all adding into the hit arrays with Interlocked, as
    /// the GPU does. Every sample owns its random stream and the sums are integer, so
    /// the result does not depend on how the chunks are scheduled. Per-thread
    /// full-frame histograms cost O(threads × pixels) to allocate and merge — most of
    /// the frame at large windows (2560×1440: 548 MB, 327 ms of a 25 ms sampling
    /// job) — while a frame takes only a few million hits. False when cancelled (the
    /// arrays may then hold part of the batch).</summary>
    private bool RunSharedOnCpu(FracturingFog.Rendering.GpuBuddhaBatch b, CancellationToken ct)
    {
        const int chunk = 8192;
        int chunks = (b.Samples + chunk - 1) / chunk;
        var hitsR = _hitsR; var hitsG = _hitsG; var hitsB = _hitsB;
        try
        {
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, ci =>
            {
                int first = ci * chunk;
                FracturingFog.Rendering.BuddhaUniformSampler.Run(b, hitsR, hitsG, hitsB,
                    first, Math.Min(chunk, b.Samples - first), atomic: true);
            });
        }
        catch (OperationCanceledException) { return false; }
        return true;
    }

    // #1218 — the minimum escape count of the render in progress (legacy sampler).
    private int _minIter;

    /// <summary>#838 — the kernel to sample this render on, or null for the CPU
    /// (recording the reason in <see cref="LastGpuRoute"/>).</summary>
    private FracturingFog.Rendering.IGpuKernel? GpuSamplingKernel(bool metropolis)
    {
        var k = GpuKernel;
        if (!UseGpuCompute) { LastGpuRoute = GpuRoute.NotRequested; return null; }
        string? why =
              k == null ? "the active renderer has no GPU compute kernel"
            : !k.SupportsBuddhabrot ? $"{k.BackendLabel} has no Buddhabrot kernel"
            : !UseGpuBuddha ? "the CPU sampler renders the same image and is faster on most hardware; FF_GPU_BUDDHA=1 opts in to the GPU"
            : !SupportsGpuSampling ? "this Buddhabrot variant's sampler runs on the CPU only"
            : metropolis ? "Metropolis sampling runs on the CPU only"
            : Zoom > MaxGpuBuddhaZoom ? $"past the GPU Buddhabrot zoom limit ({MaxGpuBuddhaZoom:0}; float orbits)"
            : null;
        if (why != null)
        {
            string reason = k == null ? "no GPU kernel"
                : !k.SupportsBuddhabrot ? "no Buddhabrot kernel"
                : !UseGpuBuddha ? "CPU faster"
                : !SupportsGpuSampling ? "CPU-only sampler"
                : metropolis ? "Metropolis sampling"
                : $"zoom > {MaxGpuBuddhaZoom:0}";
            LastGpuRoute = GpuRoute.Cpu(reason, why);
            return null;
        }
        LastGpuRoute = GpuRoute.OnGpu(k!.BackendLabel, $"{k.BackendLabel}: Buddhabrot sampling on the GPU, compositing on the CPU");
        return k;
    }

    /// <summary>#1124 — one thread's share of one batch. Classic: Metropolis-
    /// Hastings when <paramref name="mhState"/> is non-null, else uniform.
    /// Dual Buddhabrot overrides it with its two-orbit samplers.</summary>
    protected virtual void SampleBatch(
        int threadIndex, int batch, MhState? mhState, Random rng, int sampleCount,
        uint[] tR, uint[] tG, uint[] tB,
        int maxOrbit, bool inSet, bool skipBulbs, bool hd,
        double scale, double midX, double midY, int width, int height,
        int low, int mid)
    {
        if (mhState != null)
            RunMhBatch(mhState, rng, sampleCount, tR, tG, tB,
                       maxOrbit, inSet, skipBulbs, hd,
                       scale, midX, midY, width, height, low, mid);
        else
            RunUniformBatch(rng, sampleCount, tR, tG, tB,
                            maxOrbit, inSet, skipBulbs, hd,
                            scale, midX, midY, width, height, low, mid);
    }

    /// <summary>#194 — recomposite <see cref="ColorBuffer"/> from the retained
    /// hit histograms using the current <see cref="ColorMap"/> and parameters,
    /// WITHOUT re-sampling. The Monte Carlo sample pass is the dominant cost, so
    /// a colour-theme change routes here instead of a full Calculate(). The
    /// _hits* buffers persist after Calculate (cleared only at the next
    /// Calculate), so this is a pure composite over cached data.</summary>
    public void Recolor() => Composite();

    /// <summary>Composite the accumulated hit histograms into ColorBuffer per
    /// the active BuddhaColorMode, then refresh the relief height field. Shared
    /// by Calculate() (per batch) and Recolor().</summary>
    protected virtual void Composite()
    {
        // #836 — the type's mandated composite wins over the shared param so
        // Buddhabrot ≠ Nebulabrot (and Anti pair) regardless of the UI toggle.
        var mode = ForcedColorMode ?? FractalParameters.BuddhaColorMode;
        if (mode == BuddhaColorMode.ColorMap)
            RenderColorMap();
        else
            RenderBands();

        UpdateHeightField();   // #139 — density → relief height
    }

    /// <summary>#139 — build the Relief 3D height field from the orbit-density
    /// histogram: log-normalised total hits (0 on empty pixels = base plane).</summary>
    protected void UpdateHeightField()
    {
        int n = _hitsR.Length;
        if (SmoothBuffer.Length < n) SmoothBuffer = new float[n];
        uint maxAll = MaxTotalHits();
        if (maxAll == 0) { Array.Clear(SmoothBuffer, 0, n); return; }
        double inv = 1.0 / Math.Log(maxAll + 1.0);
        var smooth = SmoothBuffer;
        ForPixelBlocks(n, (from, to) =>
        {
            for (int i = from; i < to; i++)
            {
                uint sum = _hitsR[i] + _hitsG[i] + _hitsB[i];
                smooth[i] = sum == 0 ? 0f : (float)(Math.Log(sum + 1.0) * inv);
            }
        });
    }

    // The composite is per pixel and was single-threaded: at 2560×1440 it took
    // more time than sampling the default 500K orbits. Every pixel is written
    // independently, so splitting it into blocks gives the same image.
    private const int PixelBlock = 1 << 15;

    protected static void ForPixelBlocks(int n, Action<int, int> body)
    {
        int blocks = (n + PixelBlock - 1) / PixelBlock;
        if (blocks <= 1) { body(0, n); return; }
        Parallel.For(0, blocks, k => body(k * PixelBlock, Math.Min(n, (k + 1) * PixelBlock)));
    }

    /// <summary>Largest per-band hit counts (R, G, B) and largest band total.</summary>
    protected (uint R, uint G, uint B, uint Total) MaxHits()
    {
        uint mR = 0, mG = 0, mB = 0, mT = 0;
        var gate = new object();
        ForPixelBlocks(_hitsR.Length, (from, to) =>
        {
            uint r = 0, g = 0, b = 0, t = 0;
            for (int i = from; i < to; i++)
            {
                uint hR = _hitsR[i], hG = _hitsG[i], hB = _hitsB[i];
                if (hR > r) r = hR;
                if (hG > g) g = hG;
                if (hB > b) b = hB;
                uint sum = hR + hG + hB;
                if (sum > t) t = sum;
            }
            lock (gate)
            {
                if (r > mR) mR = r;
                if (g > mG) mG = g;
                if (b > mB) mB = b;
                if (t > mT) mT = t;
            }
        });
        return (mR, mG, mB, mT);
    }

    private uint MaxTotalHits() => MaxHits().Total;

    // ── Uniform sampling path (classic Buddhabrot) ────────────────────────

    private void RunUniformBatch(
        Random rng, int sampleCount,
        uint[] tR, uint[] tG, uint[] tB,
        int maxOrbit, bool inSet, bool skipBulbs, bool hd,
        double scale, double midX, double midY, int width, int height,
        int low, int mid)
    {
        var orbitR = new double[maxOrbit];
        var orbitI = new double[maxOrbit];

        for (int s = 0; s < sampleCount; s++)
        {
            double cx = -2.5 + rng.NextDouble() * 4.0;
            double cy = -1.5 + rng.NextDouble() * 3.0;

            if (!IterateOrbit(cx, cy, orbitR, orbitI, maxOrbit, inSet, skipBulbs,
                              scale, midX, midY, width, height,
                              out int recLen, out int classIter, out _))
                continue;

            uint[] target = (classIter < low) ? tR
                          : (classIter < mid) ? tG
                          : tB;

            if (hd)
                SplatOrbitHD(target, orbitR, orbitI, recLen, scale, midX, midY, width, height, rng);
            else
                SplatOrbitStd(target, orbitR, orbitI, recLen, scale, midX, midY, width, height);
        }
    }

    // ── Metropolis-Hastings sampling path ─────────────────────────────────

    private void RunMhBatch(
        MhState st, Random rng, int sampleCount,
        uint[] tR, uint[] tG, uint[] tB,
        int maxOrbit, bool inSet, bool skipBulbs, bool hd,
        double scale, double midX, double midY, int width, int height,
        int low, int mid)
    {
        // Scratch buffer for proposed orbits; on accept we copy into the
        // persistent seed buffer.
        var propR = new double[maxOrbit];
        var propI = new double[maxOrbit];

        // Warm-up: random-restart until we find a c with non-zero viewport
        // score. Capped so we don't spin forever in a deeply zoomed empty
        // region; if warm-up fails we fall back to uniform splatting this
        // batch (chain stays unseeded for the next batch retry).
        if (!st.HasSeed)
        {
            const int warmupCap = 4096;
            for (int w = 0; w < warmupCap; w++)
            {
                double cx = -2.5 + rng.NextDouble() * 4.0;
                double cy = -1.5 + rng.NextDouble() * 3.0;
                if (!IterateOrbit(cx, cy, propR, propI, maxOrbit, inSet, skipBulbs,
                                  scale, midX, midY, width, height,
                                  out int recLen, out int classIter, out int score))
                    continue;
                if (score == 0) continue;

                st.Cx = cx; st.Cy = cy;
                Array.Copy(propR, st.OrbitR, recLen);
                Array.Copy(propI, st.OrbitI, recLen);
                st.RecLen = recLen;
                st.ClassIter = classIter;
                st.Score = score;
                st.HasSeed = true;
                break;
            }
            if (!st.HasSeed)
            {
                // Fall back to uniform for this batch — better than zero work.
                RunUniformBatch(rng, sampleCount, tR, tG, tB,
                                maxOrbit, inSet, skipBulbs, hd,
                                scale, midX, midY, width, height, low, mid);
                return;
            }
        }

        for (int s = 0; s < sampleCount; s++)
        {
            // 50/50 small / large mutation. Galloway's canonical mix:
            // small refines local detail, large explores new regions and
            // helps escape from low-quality local optima.
            double ncx, ncy;
            if (rng.NextDouble() < 0.5)
            {
                // Small Gaussian mutation, σ ≈ 0.001 in c-space.
                double sigma = 0.0001 + rng.NextDouble() * 0.001;
                ncx = st.Cx + Gaussian(rng) * sigma;
                ncy = st.Cy + Gaussian(rng) * sigma;
            }
            else
            {
                // Large uniform mutation — full domain restart.
                ncx = -2.5 + rng.NextDouble() * 4.0;
                ncy = -1.5 + rng.NextDouble() * 3.0;
            }

            bool keptProp = IterateOrbit(ncx, ncy, propR, propI, maxOrbit, inSet, skipBulbs,
                                          scale, midX, midY, width, height,
                                          out int propLen, out int propClass, out int propScore);

            // Acceptance: probability = min(1, propScore / seedScore).
            // Sampled c values with no viewport contribution are auto-rejected.
            bool accept = keptProp
                && propScore > 0
                && (st.Score == 0
                    || rng.NextDouble() < (double)propScore / st.Score);

            if (accept)
            {
                Array.Copy(propR, st.OrbitR, propLen);
                Array.Copy(propI, st.OrbitI, propLen);
                st.Cx = ncx; st.Cy = ncy;
                st.RecLen = propLen;
                st.ClassIter = propClass;
                st.Score = propScore;
            }

            // Splat the current seed orbit. Re-splatting on rejection is what
            // gives MH its "weight by acceptance" — high-scoring regions get
            // re-splatted repeatedly so their structure builds density.
            uint[] target = (st.ClassIter < low) ? tR
                          : (st.ClassIter < mid) ? tG
                          : tB;
            if (hd)
                SplatOrbitHD(target, st.OrbitR, st.OrbitI, st.RecLen, scale, midX, midY, width, height, rng);
            else
                SplatOrbitStd(target, st.OrbitR, st.OrbitI, st.RecLen, scale, midX, midY, width, height);
        }
    }

    /// <summary>Standard-normal sample via Box-Muller. One transcendental
    /// per call — fine for the once-per-MH-step rate; not hot enough to
    /// warrant Marsaglia polar.</summary>
    protected static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // ── Core iteration / classification ───────────────────────────────────

    /// <summary>Iterate z² + c from c = (cx, cy); fill orbitR/I; classify into
    /// a band; compute viewport-hit score. Returns true if the orbit matches
    /// the keep predicate (escape for Buddhabrot, in-set for Anti-*).</summary>
    private bool IterateOrbit(
        double cx, double cy, double[] orbitR, double[] orbitI, int maxOrbit,
        bool inSet, bool skipBulbs,
        double scale, double midX, double midY, int width, int height,
        out int recLen, out int classIter, out int score)
    {
        recLen = 0; classIter = 0; score = 0;
        if (skipBulbs && InCardioidOrBulb(cx, cy)) return false;

        double zr = 0, zi = 0;
        double sumZ2 = 0;
        int iter;
        for (iter = 0; iter < maxOrbit; iter++)
        {
            orbitR[iter] = zr;
            orbitI[iter] = zi;
            double zr2 = zr * zr, zi2 = zi * zi;
            sumZ2 += zr2 + zi2;
            if (zr2 + zi2 > 4.0) break;
            double newZr = zr2 - zi2 + cx;
            zi = 2.0 * zr * zi + cy;
            zr = newZr;
        }

        bool escaped = iter < maxOrbit;
        bool keep = inSet ? !escaped : escaped && iter >= _minIter;   // #1218 — no fast escapers
        if (!keep) return false;

        if (escaped) classIter = iter;
        else
        {
            // In-set orbits: classify by mean |z|² over the orbit.
            // For bounded c values, mean is heavily peaked near 0 (deep
            // interior fills most of the cardioid). A linear map dumps
            // ~95% of orbits into the high band → single-channel blowout.
            // Square the deep-end normalisation (cube-root the high end)
            // to spread the histogram more evenly across the three bands.
            double mean = sumZ2 / Math.Max(1, iter);
            double normMean = Math.Min(1.0, mean * 0.25);   // [0, 1], 0 = deep
            // Inverse with shaped curve: small mean (deep) → low classIter,
            // big mean (near edge) → high classIter. Power < 1 stretches
            // small values; this puts deep orbits into the LOW band, edge
            // orbits into the HIGH band — opposite of the previous linear
            // map but gives non-degenerate three-band coverage.
            double frac = Math.Pow(normMean, 0.5);
            classIter = (int)(frac * maxOrbit);
        }

        recLen = escaped ? iter : maxOrbit;

        // Score = orbit points landing inside the rendered viewport. Used by
        // MH acceptance; cheap to compute alongside since we already have the
        // orbit. Free for the uniform path since it's also returned (callers
        // discard it).
        int s = 0;
        double invScale = 1.0 / scale;
        double cxOff = width * 0.5 - midX * invScale;
        double cyOff = height * 0.5 - midY * invScale;
        for (int k = 1; k < recLen; k++)   // #1218 — z0 is not drawn, so it does not score
        {
            int ix = (int)(orbitR[k] * invScale + cxOff);
            int iy = (int)(orbitI[k] * invScale + cyOff);
            if ((uint)ix < (uint)width && (uint)iy < (uint)height) s++;
        }
        score = s;
        return true;
    }

    /// <summary>True when (cx, cy) is inside the main cardioid or the
    /// period-2 bulb. These regions of the parameter plane are provably part
    /// of the Mandelbrot set; their orbits never escape.</summary>
    protected static bool InCardioidOrBulb(double cx, double cy)
    {
        // Period-2 bulb: (cx + 1)² + cy² < 1/16
        double dx = cx + 1.0;
        if (dx * dx + cy * cy < 0.0625) return true;
        // Main cardioid test: q = (cx - 1/4)² + cy²; in set iff q·(q + cx - 1/4) < cy²/4
        double xq = cx - 0.25;
        double q = xq * xq + cy * cy;
        return q * (q + xq) < 0.25 * cy * cy;
    }

    // ── Splat helpers ─────────────────────────────────────────────────────

    /// <summary>Classic nearest-pixel splat. Splats the orbit to the target
    /// hit buffer using integer pixel snapping.</summary>
    protected static void SplatOrbitStd(
        uint[] target, double[] orbitR, double[] orbitI, int recLen,
        double scale, double midX, double midY, int width, int height, int from = 1)
    {
        // #1218 — from 1: z0 = 0 is every orbit's, not drawn (Dual Buddhabrot passes
        // its own start, unchanged).
        for (int k = from; k < recLen; k++)
        {
            double ozr = orbitR[k], ozi = orbitI[k];
            int ix = (int)((ozr - midX) / scale + width * 0.5);
            int iy = (int)((ozi - midY) / scale + height * 0.5);
            if ((uint)ix < (uint)width && (uint)iy < (uint)height)
                target[iy * width + ix]++;
        }
    }

    /// <summary>HD splat. Two improvements over Std:
    ///   1. Stochastic bilinear splatting: the fractional pixel coordinate
    ///      picks one of the 4 surrounding cells with probability matching
    ///      the bilinear weight. Smooths grain into anti-aliased detail
    ///      while keeping the buffer in uint (cheap).
    ///   2. Real-axis mirror duplication: every orbit point (ozr, ozi) is
    ///      mirrored to (ozr, -ozi) and splatted there too. Mandelbrot is
    ///      symmetric about the real axis, so this is free 2× effective
    ///      sample count at the cost of one extra pixel write per step.
    ///      Only valid when the deposited orbit family is itself real-axis
    ///      symmetric — <paramref name="mirror"/> = false turns it off (#1124:
    ///      Dual Buddhabrot c channels with an off-axis c-seed).
    /// </summary>
    protected static void SplatOrbitHD(
        uint[] target, double[] orbitR, double[] orbitI, int recLen,
        double scale, double midX, double midY, int width, int height, Random rng,
        bool mirror = true, int from = 1)
    {
        for (int k = from; k < recLen; k++)   // #1218 — see SplatOrbitStd
        {
            double ozr = orbitR[k], ozi = orbitI[k];

            double fx = (ozr - midX) / scale + width * 0.5;
            double fy = (ozi - midY) / scale + height * 0.5;
            SplatBilinearOne(target, fx, fy, width, height, rng);

            // Mirror about real axis: y → -y (i.e. distance from midY flips).
            if (!mirror) continue;
            double fyMirror = (-ozi - midY) / scale + height * 0.5;
            SplatBilinearOne(target, fx, fyMirror, width, height, rng);
        }
    }

    /// <summary>Stochastic bilinear splat to one cell, chosen with
    /// probability matching the fractional pixel offset.</summary>
    private static void SplatBilinearOne(
        uint[] target, double fx, double fy, int width, int height, Random rng)
    {
        int x0 = (int)Math.Floor(fx);
        int y0 = (int)Math.Floor(fy);
        double dx = fx - x0;
        double dy = fy - y0;
        int xi = rng.NextDouble() < dx ? x0 + 1 : x0;
        int yi = rng.NextDouble() < dy ? y0 + 1 : y0;
        if ((uint)xi < (uint)width && (uint)yi < (uint)height)
            target[yi * width + xi]++;
    }

    // ── Composite passes ──────────────────────────────────────────────────

    private void RenderBands()
    {
        int n = _hitsR.Length;
        var (maxR, maxG, maxB, _) = MaxHits();

        bool hd = FractalParameters.BuddhaQualityMode == BuddhaQualityMode.HighDefinition;

        // Joint normalisation (HD): all three channels divide by the same
        // joint max so weak bands stay proportionally dim instead of being
        // boosted to full intensity by per-channel norm. Standard mode keeps
        // the classic per-channel norm for the historical Nebulabrot look.
        double invR, invG, invB;
        if (hd)
        {
            uint jointMax = Math.Max(maxR, Math.Max(maxG, maxB));
            double invJ = jointMax > 1 ? 1.0 / Math.Log(jointMax + 1) : 1.0;
            invR = invG = invB = invJ;
        }
        else
        {
            invR = maxR > 1 ? 1.0 / Math.Log(maxR + 1) : 1.0;
            invG = maxG > 1 ? 1.0 / Math.Log(maxG + 1) : 1.0;
            invB = maxB > 1 ? 1.0 / Math.Log(maxB + 1) : 1.0;
        }

        // Noise-floor reject (HD): pixels with very low hit counts are noise
        // from the random sampling — stochastic bilinear splat scatters
        // single-hit speckle widely. Without this gate the log scale lifts
        // 1- or 2-hit pixels to ~5-10% intensity, flooding the background
        // with channel color. Hits at or below the floor are forced black.
        // Floor scales with the sample budget so it tracks expected signal
        // density (more samples → higher noise threshold, but also higher
        // signal so good pixels are unaffected).
        int floor = 0;
        if (hd)
        {
            int samples = FractalParameters.BuddhaSamples;
            floor = Math.Max(2, samples / 2_000_000); // 1 per 2M samples, min 2
        }

        var colors = ColorBuffer;
        ForPixelBlocks(n, (from, to) =>
        {
            for (int i = from; i < to; i++)
            {
                uint hR = _hitsR[i], hG = _hitsG[i], hB = _hitsB[i];
                if (hd)
                {
                    if (hR <= floor) hR = 0;
                    if (hG <= floor) hG = 0;
                    if (hB <= floor) hB = 0;
                }
                double r = Math.Log(hR + 1) * invR;
                double g = Math.Log(hG + 1) * invG;
                double b = Math.Log(hB + 1) * invB;
                byte R = (byte)Math.Clamp(r * 255, 0, 255);
                byte G = (byte)Math.Clamp(g * 255, 0, 255);
                byte B = (byte)Math.Clamp(b * 255, 0, 255);
                colors[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        });
    }

    private void RenderColorMap()
    {
        // Sum all three bands → single histogram. Hot pixels (high hit count)
        // are the fractal; cold/empty pixels are background.
        //
        // Two-stage colouring:
        //   1. Pick the theme colour at smooth = (1-norm)·iters so hot pixels
        //      land in the outer-escape band and cold pixels in the interior
        //      band — preserves the theme's gradient across the fractal.
        //   2. Alpha-blend toward InSetColor by (1-norm)² so faint single-hit
        //      pixels fade into the background instead of carrying a fully
        //      saturated theme colour. The square fades sparse hits hard
        //      while leaving genuine fractal density at near-full saturation.
        int n = _hitsR.Length;
        uint maxAll = MaxTotalHits();
        if (maxAll == 0)
        {
            Array.Clear(ColorBuffer);
            return;
        }

        double inv = 1.0 / Math.Log(maxAll + 1.0);
        int iters = MaxIterations;
        var cm = ColorMap;
        cm.MaxIterations = iters;
        uint inSetColor = cm.InSetColor;
        byte bgR = (byte)((inSetColor >> 16) & 0xFF);
        byte bgG = (byte)((inSetColor >>  8) & 0xFF);
        byte bgB = (byte)(inSetColor & 0xFF);

        var colors = ColorBuffer;
        ForPixelBlocks(n, (from, to) =>
        {
            for (int i = from; i < to; i++)
            {
                uint sum = _hitsR[i] + _hitsG[i] + _hitsB[i];
                if (sum == 0)
                {
                    colors[i] = inSetColor;
                    continue;
                }
                double norm = Math.Log(sum + 1.0) * inv;     // 0..1, hot ≈ 1
                float smooth = (float)((1.0 - norm) * iters);
                uint argb = unchecked((uint)cm.Map(smooth, 0f, iters));
                byte fR = (byte)((argb >> 16) & 0xFF);
                byte fG = (byte)((argb >>  8) & 0xFF);
                byte fB = (byte)(argb & 0xFF);

                double a = norm * norm;                       // density alpha
                double oneMa = 1.0 - a;
                byte R = (byte)(fR * a + bgR * oneMa);
                byte G = (byte)(fG * a + bgG * oneMa);
                byte B = (byte)(fB * a + bgB * oneMa);
                colors[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        });
    }
}
