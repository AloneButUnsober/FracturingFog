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

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public abstract class BuddhaFamilyCalculator : IFractalCalculator, IHeightFieldSource
{
    // Per-thread orbit buffer size limit. At 200K × 8 bytes × 2 arrays × 32
    // threads ≈ 100 MB — high but manageable; without the cap a 1M iteration
    // in-set render would allocate ~512 MB just for orbit scratch.
    private const int MaxOrbitCap = 200_000;

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

    /// <summary>Record orbits that DO NOT escape (in-set) when true;
    /// orbits that DO escape (classic Buddhabrot) when false.</summary>
    protected abstract bool IsInSet { get; }

    private uint[] _hitsR = Array.Empty<uint>();
    private uint[] _hitsG = Array.Empty<uint>();
    private uint[] _hitsB = Array.Empty<uint>();

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
    private sealed class MhState
    {
        public double Cx, Cy;
        public double[] OrbitR = Array.Empty<double>();
        public double[] OrbitI = Array.Empty<double>();
        public int RecLen;
        public int ClassIter;
        public int Score;
        public bool HasSeed;
    }

    public void Calculate(CancellationToken ct = default)
    {
        Array.Clear(_hitsR);
        Array.Clear(_hitsG);
        Array.Clear(_hitsB);

        int width = Width;
        int height = Height;
        int samples = FractalParameters.BuddhaSamples;
        int low = FractalParameters.BuddhaIterLow;
        int mid = FractalParameters.BuddhaIterMid;
        int high = FractalParameters.BuddhaIterHigh;
        bool hd = FractalParameters.BuddhaQualityMode == BuddhaQualityMode.HighDefinition;
        bool mh = FractalParameters.BuddhaMetropolis;
        bool progressive = FractalParameters.BuddhaProgressive;

        int maxOrbit = IsInSet ? Math.Max(high, MaxIterations) : high;
        if (maxOrbit > MaxOrbitCap) maxOrbit = MaxOrbitCap;

        double scale = (3.5 / Math.Max(width, height)) / Zoom;
        double midX = CenterX;
        double midY = CenterY;

        int threads = Math.Max(1, Environment.ProcessorCount);
        int batches = progressive ? ProgressiveBatches : 1;
        int samplesPerBatch = Math.Max(1, samples / batches);
        int perThreadPerBatch = Math.Max(1, samplesPerBatch / threads);

        var localR = new uint[threads][];
        var localG = new uint[threads][];
        var localB = new uint[threads][];
        for (int t = 0; t < threads; t++)
        {
            localR[t] = new uint[width * height];
            localG[t] = new uint[width * height];
            localB[t] = new uint[width * height];
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

        for (int batch = 0; batch < batches; batch++)
        {
            if (ct.IsCancellationRequested) break;

            Parallel.For(0, threads, new ParallelOptions { CancellationToken = ct }, t =>
            {
                if (ct.IsCancellationRequested) return;
                int seed = unchecked(Environment.TickCount * 73856093 + t * 19349663 + batch * 83492791);
                var rng = new Random(seed);

                if (mh)
                {
                    RunMhBatch(mhStates![t], rng, perThreadPerBatch,
                               localR[t], localG[t], localB[t],
                               maxOrbit, inSet, skipBulbs, hd,
                               scale, midX, midY, width, height, low, mid);
                }
                else
                {
                    RunUniformBatch(rng, perThreadPerBatch,
                                    localR[t], localG[t], localB[t],
                                    maxOrbit, inSet, skipBulbs, hd,
                                    scale, midX, midY, width, height, low, mid);
                }
            });

            if (ct.IsCancellationRequested && !progressive) break;

            // Merge locals → globals (additive), then clear locals for next batch.
            for (int t = 0; t < threads; t++)
            {
                var lR = localR[t]; var lG = localG[t]; var lB = localB[t];
                for (int i = 0; i < _hitsR.Length; i++)
                {
                    _hitsR[i] += lR[i];
                    _hitsG[i] += lG[i];
                    _hitsB[i] += lB[i];
                }
                if (progressive)
                {
                    Array.Clear(lR);
                    Array.Clear(lG);
                    Array.Clear(lB);
                }
            }

            // Composite to ColorBuffer. Progressive mode does it every batch
            // so a mid-render cancel still yields a usable image; single-pass
            // mode does it once after the only batch.
            if (FractalParameters.BuddhaColorMode == BuddhaColorMode.ColorMap)
                RenderColorMap();
            else
                RenderBands();

            UpdateHeightField();   // #139 — density → relief height
        }
    }

    /// <summary>#139 — build the Relief 3D height field from the orbit-density
    /// histogram: log-normalised total hits (0 on empty pixels = base plane).</summary>
    private void UpdateHeightField()
    {
        int n = _hitsR.Length;
        if (SmoothBuffer.Length < n) SmoothBuffer = new float[n];
        uint maxAll = 0;
        for (int i = 0; i < n; i++)
        {
            uint sum = _hitsR[i] + _hitsG[i] + _hitsB[i];
            if (sum > maxAll) maxAll = sum;
        }
        if (maxAll == 0) { Array.Clear(SmoothBuffer, 0, n); return; }
        double inv = 1.0 / Math.Log(maxAll + 1.0);
        for (int i = 0; i < n; i++)
        {
            uint sum = _hitsR[i] + _hitsG[i] + _hitsB[i];
            SmoothBuffer[i] = sum == 0 ? 0f : (float)(Math.Log(sum + 1.0) * inv);
        }
    }

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
    private static double Gaussian(Random rng)
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
        bool keep = inSet ? !escaped : escaped;
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
        for (int k = 0; k < recLen; k++)
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
    private static bool InCardioidOrBulb(double cx, double cy)
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
    private static void SplatOrbitStd(
        uint[] target, double[] orbitR, double[] orbitI, int recLen,
        double scale, double midX, double midY, int width, int height)
    {
        for (int k = 0; k < recLen; k++)
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
    /// </summary>
    private static void SplatOrbitHD(
        uint[] target, double[] orbitR, double[] orbitI, int recLen,
        double scale, double midX, double midY, int width, int height, Random rng)
    {
        for (int k = 0; k < recLen; k++)
        {
            double ozr = orbitR[k], ozi = orbitI[k];

            double fx = (ozr - midX) / scale + width * 0.5;
            double fy = (ozi - midY) / scale + height * 0.5;
            SplatBilinearOne(target, fx, fy, width, height, rng);

            // Mirror about real axis: y → -y (i.e. distance from midY flips).
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
        uint maxR = 0, maxG = 0, maxB = 0;
        for (int i = 0; i < n; i++)
        {
            if (_hitsR[i] > maxR) maxR = _hitsR[i];
            if (_hitsG[i] > maxG) maxG = _hitsG[i];
            if (_hitsB[i] > maxB) maxB = _hitsB[i];
        }

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

        for (int i = 0; i < n; i++)
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
            ColorBuffer[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
        }
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
        uint maxAll = 0;
        for (int i = 0; i < n; i++)
        {
            uint sum = _hitsR[i] + _hitsG[i] + _hitsB[i];
            if (sum > maxAll) maxAll = sum;
        }
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

        for (int i = 0; i < n; i++)
        {
            uint sum = _hitsR[i] + _hitsG[i] + _hitsB[i];
            if (sum == 0)
            {
                ColorBuffer[i] = inSetColor;
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
            ColorBuffer[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
        }
    }
}
