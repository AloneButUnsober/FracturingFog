// FlameRenderer.cs
//
// Apophysis-style flame fractal. Chaos game over a small set of FlameMaps —
// each map = affine pre-transform + one non-linear "variation" + a colour
// index. Sample 8 M points (default), accumulate hit + colour into two
// per-pixel histograms, then tone-map through log-density + gamma + the
// active IColorMap.
//
// Roadmap slice plan (FRACTAL_EXPANSION_ROADMAP D.4):
//   Slice 1 (this commit): core chaos game, hit-only histogram, basic log
//                          tone-map. Linear variation fully wired; other
//                          variations recognised by the enum but fall
//                          through to identity until slice 2 lands.
//   Slice 2:               variation library — sinusoidal, spherical,
//                          swirl, polar, heart, disc, julia.
//   Slice 3:               per-map colour histogram, gamma tone-map,
//                          vibrancy blend, 6 built-in presets, batch CLI
//                          flags, math help tab.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class FlameRenderer : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 0; // unused

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    private uint[] _hits = Array.Empty<uint>();
    private float[] _colorSum = Array.Empty<float>();

    public FlameRenderer(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        _hits = new uint[n];
        _colorSum = new float[n];
    }

    public void Calculate(CancellationToken ct = default)
    {
        Array.Clear(_hits);
        Array.Clear(_colorSum);

        var maps = FractalParameters.FlameMaps
            ?? (FlamePresets.All.TryGetValue(FractalParameters.FlamePresetName, out var preset)
                ? preset
                : FlamePresets.All["Sierpinski Variation"]);

        if (maps.Count == 0) return;

        // Cumulative weight CDF for sampler.
        var cum = new double[maps.Count];
        double sum = 0;
        for (int i = 0; i < maps.Count; i++)
        {
            sum += maps[i].Weight;
            cum[i] = sum;
        }
        if (sum <= 0) return;

        // Single-pass attractor-fit pass — same trick IFSCalculator uses.
        ComputeAttractorBBox(maps, cum, sum, out double minX, out double maxX, out double minY, out double maxY);

        double spanX = Math.Max(1e-9, maxX - minX);
        double spanY = Math.Max(1e-9, maxY - minY);
        double worldSpan = Math.Max(spanX, spanY);
        double mapFit = 3.0 / worldSpan;
        double mx = (minX + maxX) * 0.5;
        double my = (minY + maxY) * 0.5;
        double pixelScale = (3.5 / Math.Max(Width, Height)) / Zoom;

        int width = Width;
        int height = Height;
        int iterations = Math.Max(1, FractalParameters.FlameIterations);
        double centerX = CenterX;
        double centerY = CenterY;

        int threadCount = Math.Max(1, Environment.ProcessorCount);
        int perThread = iterations / threadCount;

        var localHits = new uint[threadCount][];
        var localColor = new float[threadCount][];
        for (int t = 0; t < threadCount; t++)
        {
            localHits[t]  = new uint[width * height];
            localColor[t] = new float[width * height];
        }

        Parallel.For(0, threadCount, new ParallelOptions { CancellationToken = ct }, t =>
        {
            if (ct.IsCancellationRequested) return;
            // Deterministic seed per thread. Two renders of the same
            // (preset, iter) reproduce the exact same chaos-game realization
            // so when the user nudges Gamma / Vibrancy the only visible
            // change is the tone-map response — RNG variance no longer
            // masks the slider effect with per-pixel noise.
            var rng = new Random(unchecked(t * 19349663 + 0x5A3C9F));
            var lh = localHits[t];
            var lc = localColor[t];

            double x = 0, y = 0;
            double cPoint = 0.0;

            // Warm-up — settle onto the attractor and let the colour
            // recurrence converge to its limit cycle before recording hits.
            for (int i = 0; i < 50; i++)
            {
                int idx = PickMap(rng, cum, sum);
                Step(maps[idx], ref x, ref y);
                cPoint = (cPoint + maps[idx].ColorIndex) * 0.5;
            }

            for (int i = 0; i < perThread; i++)
            {
                int idx = PickMap(rng, cum, sum);
                Step(maps[idx], ref x, ref y);
                // Per-Apophysis convention: average the picked map's
                // colour index into a running per-point colour. The 0.5
                // weighting makes the colour state a geometric blend of
                // every map traversed up to this point.
                cPoint = (cPoint + maps[idx].ColorIndex) * 0.5;

                double worldX = (x - mx) * mapFit;
                double worldY = -(y - my) * mapFit;

                int ix = (int)((worldX - centerX) / pixelScale + width * 0.5);
                int iy = (int)((worldY - centerY) / pixelScale + height * 0.5);
                if ((uint)ix < (uint)width && (uint)iy < (uint)height)
                {
                    int p = iy * width + ix;
                    lh[p]++;
                    lc[p] += (float)cPoint;
                }
            }
        });

        for (int t = 0; t < threadCount; t++)
        {
            var lh = localHits[t];
            var lc = localColor[t];
            for (int i = 0; i < _hits.Length; i++)
            {
                _hits[i] += lh[i];
                _colorSum[i] += lc[i];
            }
        }

        // ── Tone-map ──
        // Apophysis-style gamma + vibrancy over a log-density histogram.
        //   alpha   = log(hit + 1) / log(maxHit + 1)              ∈ [0, 1]
        //   alphaG  = alpha ^ (1 / gamma)                          gamma-boost
        //   bright  = vibrancy · alphaG + (1 − vibrancy) · alpha   blend
        //   colour  = palette.Map(avgColorIndex)                   sample
        //   out     = colour · bright                              modulate
        // True-max normalization (no auto-exposure clip). Keeps the bulk
        // of pixels in the gamma-active range α < 1 — auto-exposure
        // pushed too many α values near 1 where γ has no visible effect.
        uint maxHit = 0;
        for (int i = 0; i < _hits.Length; i++) if (_hits[i] > maxHit) maxHit = _hits[i];
        double invLogMax = maxHit > 1 ? 1.0 / Math.Log(maxHit + 1) : 1.0;

        double gamma = Math.Max(0.1, FractalParameters.FlameGamma);
        double invGamma = 1.0 / gamma;
        double vib = Math.Clamp(FractalParameters.FlameVibrancy, 0.0, 1.0);

        ColorMap.MaxIterations = 256;
        for (int i = 0; i < _hits.Length; i++)
        {
            uint h = _hits[i];
            if (h == 0)
            {
                ColorBuffer[i] = ColorMap.InSetColor;
                continue;
            }
            double alpha = Math.Log(h + 1) * invLogMax;
            double alphaG = Math.Pow(alpha, invGamma);
            double bright = vib * alphaG + (1.0 - vib) * alpha;

            double avgColor = Math.Clamp(_colorSum[i] / h, 0.0, 1.0);
            uint packed = (uint)ColorMap.Map((float)(avgColor * 256.0), 0f, 256);

            // Modulate RGB by brightness, preserve alpha.
            uint a = packed & 0xFF000000u;
            uint r = (packed >> 16) & 0xFFu;
            uint g = (packed >> 8)  & 0xFFu;
            uint b =  packed        & 0xFFu;

            byte rO = (byte)Math.Clamp((int)(r * bright + 0.5), 0, 255);
            byte gO = (byte)Math.Clamp((int)(g * bright + 0.5), 0, 255);
            byte bO = (byte)Math.Clamp((int)(b * bright + 0.5), 0, 255);
            ColorBuffer[i] = a | ((uint)rO << 16) | ((uint)gO << 8) | bO;
        }
    }

    private static void ComputeAttractorBBox(
        List<FlameMap> maps, double[] cum, double sum,
        out double minX, out double maxX, out double minY, out double maxY)
    {
        var rng = new Random(42);
        double x = 0, y = 0;
        for (int i = 0; i < 200; i++)
        {
            int idx = PickMap(rng, cum, sum);
            Step(maps[idx], ref x, ref y);
        }
        minX = maxX = x;
        minY = maxY = y;
        for (int i = 0; i < 30_000; i++)
        {
            int idx = PickMap(rng, cum, sum);
            Step(maps[idx], ref x, ref y);
            // Variations like spherical can punt a point to ±∞ when r ≈ 0.
            // Skip those samples in the bbox pass — they would explode the
            // attractor fit and squash the rest of the set to a single pixel.
            if (double.IsNaN(x) || double.IsNaN(y) ||
                double.IsInfinity(x) || double.IsInfinity(y))
            {
                x = 0; y = 0;
                continue;
            }
            if (x < minX) minX = x; else if (x > maxX) maxX = x;
            if (y < minY) minY = y; else if (y > maxY) maxY = y;
        }
    }

    private static int PickMap(Random rng, double[] cum, double sum)
    {
        double r = rng.NextDouble() * sum;
        for (int i = 0; i < cum.Length; i++)
            if (r <= cum[i]) return i;
        return cum.Length - 1;
    }

    private static void Step(FlameMap m, ref double x, ref double y)
    {
        // Affine pre-transform — identical to AffineMap.
        double px = m.A * x + m.B * y + m.E;
        double py = m.C * x + m.D * y + m.F;

        // Primary variation.
        ApplyVariation(m.Variation, m.VariationAmount, px, py, out double vx, out double vy);

        // Optional secondary variation — Apophysis blends N variations
        // additively. Skip the call entirely when its weight is zero so
        // single-variation maps stay branch-cheap.
        if (m.VariationAmount2 != 0.0)
        {
            ApplyVariation(m.Variation2, m.VariationAmount2, px, py, out double vx2, out double vy2);
            vx += vx2;
            vy += vy2;
        }

        // Post-affine — second linear transform applied after the variation
        // sum (Apophysis convention). The identity post (Pa=Pd=1, rest 0)
        // is no-op visually; non-trivial posts let a preset rotate, mirror
        // or scale the variation output without changing the pre-affine.
        x = m.Pa * vx + m.Pb * vy + m.Pe;
        y = m.Pc * vx + m.Pd * vy + m.Pf;
    }

    /// <summary>Apply a single non-linear variation. Eight Apophysis stock
    /// variations — the most-used core — are wired here. Formulae follow
    /// the canonical Draves &amp; Reckase "Fractal Flame Algorithm" paper
    /// (2003). Each variation operates on the affine-transformed point;
    /// <paramref name="amount"/> is a linear weight on the result, identical
    /// to Apophysis' per-variation Weight field.</summary>
    private static void ApplyVariation(FlameVariation v, double amount,
        double x, double y, out double ox, out double oy)
    {
        switch (v)
        {
            case FlameVariation.Sinusoidal:
                ox = amount * Math.Sin(x);
                oy = amount * Math.Sin(y);
                return;

            case FlameVariation.Spherical:
            {
                // r² = x² + y². Singular at the origin — when the
                // chaos game lands inside a tiny safety bubble we
                // collapse the output to the origin instead of NaN.
                double r2 = x * x + y * y;
                if (r2 < 1e-20) { ox = 0; oy = 0; return; }
                double inv = amount / r2;
                ox = inv * x;
                oy = inv * y;
                return;
            }

            case FlameVariation.Swirl:
            {
                double r2 = x * x + y * y;
                double s = Math.Sin(r2);
                double c = Math.Cos(r2);
                ox = amount * (x * s - y * c);
                oy = amount * (x * c + y * s);
                return;
            }

            case FlameVariation.Polar:
            {
                double theta = Math.Atan2(x, y);
                double r = Math.Sqrt(x * x + y * y);
                ox = amount * (theta / Math.PI);
                oy = amount * (r - 1.0);
                return;
            }

            case FlameVariation.Heart:
            {
                double r = Math.Sqrt(x * x + y * y);
                double theta = Math.Atan2(x, y);
                double tr = theta * r;
                ox =  amount * r * Math.Sin(tr);
                oy = -amount * r * Math.Cos(tr);
                return;
            }

            case FlameVariation.Disc:
            {
                double theta = Math.Atan2(x, y);
                double r = Math.Sqrt(x * x + y * y);
                double piR = Math.PI * r;
                double k = amount * theta / Math.PI;
                ox = k * Math.Sin(piR);
                oy = k * Math.Cos(piR);
                return;
            }

            case FlameVariation.Julia:
            {
                // r = √|z|, φ = ½ arg(z) + nπ (n random ∈ {0,1}).
                // The two-branch random pick is what gives Apophysis'
                // julia variation its characteristic split filaments;
                // a single deterministic branch would only paint half.
                double r = Math.Sqrt(Math.Sqrt(x * x + y * y));
                double theta = Math.Atan2(y, x) * 0.5;
                if ((_juliaBranchRng.Value!.Next() & 1) == 1)
                    theta += Math.PI;
                ox = amount * r * Math.Cos(theta);
                oy = amount * r * Math.Sin(theta);
                return;
            }

            // Wave 5.11 — next 10 Apophysis stock variations.

            case FlameVariation.Horseshoe:
            {
                double r = Math.Sqrt(x * x + y * y);
                if (r < 1e-12) { ox = 0; oy = 0; return; }
                ox = amount * (x - y) * (x + y) / r;
                oy = amount * 2.0 * x * y / r;
                return;
            }

            case FlameVariation.Spiral:
            {
                double r = Math.Sqrt(x * x + y * y);
                if (r < 1e-12) { ox = 0; oy = 0; return; }
                double theta = Math.Atan2(x, y);
                ox = amount * (Math.Cos(theta) + Math.Sin(r)) / r;
                oy = amount * (Math.Sin(theta) - Math.Cos(r)) / r;
                return;
            }

            case FlameVariation.Hyperbolic:
            {
                double r = Math.Sqrt(x * x + y * y);
                if (r < 1e-12) { ox = 0; oy = 0; return; }
                double theta = Math.Atan2(x, y);
                ox = amount * Math.Sin(theta) / r;
                oy = amount * r * Math.Cos(theta);
                return;
            }

            case FlameVariation.Diamond:
            {
                double r = Math.Sqrt(x * x + y * y);
                double theta = Math.Atan2(x, y);
                ox = amount * Math.Sin(theta) * Math.Cos(r);
                oy = amount * Math.Cos(theta) * Math.Sin(r);
                return;
            }

            case FlameVariation.Ex:
            {
                double r = Math.Sqrt(x * x + y * y);
                double theta = Math.Atan2(x, y);
                double p0 = Math.Sin(theta + r); double p03 = p0 * p0 * p0;
                double p1 = Math.Cos(theta - r); double p13 = p1 * p1 * p1;
                ox = amount * r * (p03 + p13);
                oy = amount * r * (p03 - p13);
                return;
            }

            case FlameVariation.Bent:
            {
                // Quadrant-piecewise scale. Apophysis bent stretches negative
                // X by 2× and compresses negative Y by ½, leaving the +X+Y
                // quadrant identity.
                double rx = x < 0 ? 2.0 * x : x;
                double ry = y < 0 ? 0.5 * y : y;
                ox = amount * rx;
                oy = amount * ry;
                return;
            }

            case FlameVariation.Fisheye:
            {
                double r = Math.Sqrt(x * x + y * y);
                double k = 2.0 * amount / (r + 1.0);
                // Apophysis fisheye swaps (x, y) deliberately — bend-and-flip.
                ox = k * y;
                oy = k * x;
                return;
            }

            case FlameVariation.Exponential:
            {
                double e = Math.Exp(x - 1.0);
                double piY = Math.PI * y;
                ox = amount * e * Math.Cos(piY);
                oy = amount * e * Math.Sin(piY);
                return;
            }

            case FlameVariation.Power:
            {
                double r = Math.Sqrt(x * x + y * y);
                if (r < 1e-12) { ox = 0; oy = 0; return; }
                double theta = Math.Atan2(x, y);
                double rp = Math.Pow(r, Math.Sin(theta));
                ox = amount * rp * Math.Cos(theta);
                oy = amount * rp * Math.Sin(theta);
                return;
            }

            case FlameVariation.Cosine:
            {
                double piX = Math.PI * x;
                ox =  amount * Math.Cos(piX) * Math.Cosh(y);
                oy = -amount * Math.Sin(piX) * Math.Sinh(y);
                return;
            }

            case FlameVariation.Linear:
            default:
                ox = amount * x;
                oy = amount * y;
                return;
        }
    }

    /// <summary>Thread-local RNG for variations whose definition includes a
    /// per-step coin flip (currently <see cref="FlameVariation.Julia"/>).
    /// Per-thread isolation avoids contention on a shared Random and
    /// keeps the chaos-game's main RNG (used for map selection) free of
    /// extra calls that would shift sample sequences across slices.</summary>
    private static readonly ThreadLocal<Random> _juliaBranchRng =
        new(() => new Random(unchecked((int)(Environment.TickCount * 2654435761u) + Environment.CurrentManagedThreadId)));
}
