// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// TalbotCarpetCalculator.cs
//
// Fractal Talbot carpet (#1570, double-slit epic #1565 S6; design:
// Docs/Technical/DoubleSlit-Fractal-RnD.md §7). A periodic grating lit by a
// plane wave images itself at multiples of the Talbot distance z_T = 2d²/λ;
// in between, the (transverse, propagation) intensity is a "quantum carpet",
// and at irrational z/z_T the transverse intensity is a fractal (Berry &
// Klein, J. Mod. Opt. 43, 1996; Berry, Marzoli & Schleich, Physics World 14,
// 2001). Its quantum twin is a uniform state in a box (Berry, J. Phys. A 29,
// 1996).
//
// Paraxially, a grating with Fourier coefficients cₙ propagated a distance z is
//
//     ψ(x, z) = Σₙ cₙ · e^{2πi·n·x/d} · e^{−iπ·λ·z·n²/d²}.
//
// In grating periods ξ = x/d and Talbot distances ζ = z/z_T the propagation
// phase is exactly 2π·n²·ζ, so the carpet has no free scale:
//
//     ψ(ξ, ζ) = Σ_{|n|≤M} cₙ · e^{2πi·n·ξ} · e^{−2πi·n²·ζ}.
//
// ζ = 1 gives the grating back (n² is an integer); ζ = ½ gives it shifted by
// half a period (n² ≡ n mod 2); rational ζ gives finite superpositions of
// shifted copies (fractional Talbot). The view's x axis is ξ, its y axis ζ
// (propagation runs down the screen).
//
// Profiles (cₙ): Ronchi — a square wave of duty a, cₙ = a·sinc(π·n·a);
// δ-comb — cₙ = 1 (Berry & Klein's idealisation, the sharpest carpet); and
// SlitCascade — one period of the #1569 cascade aperture on the Line layout
// (period 2, the level-0 square [−1, 1]), cₙ = r^D·A(n/2) with A the
// cascade's far-field amplitude. M (TalbotModes) truncates the sum: it is
// how sharp the grating edges are, and so how fine the carpet's fractal
// detail can get.
//
// Cost: each row has one ζ, so bₙ = cₙ·e^{−2πi·n²·ζ} is built once per row and
// the pixel sum steps e^{2πi·n·ξ} by complex multiplication — O(M) per pixel
// with no trig in the inner loop.
//
// Domain view (#1592): hue = arg ψ on the colour-blind-safe Phase Wheel,
// brightness = the log-intensity scale below (Wegert-style phase portrait; the
// theme is not used). The SmoothBuffer keeps the intensity for Relief.
//
// Profile overlay (#1601): with TalbotSliceOverlay on, the transverse intensity
// I(ξ) = |ψ(ξ, ζ₀)|² at ζ₀ = TalbotSliceZeta is drawn over the carpet as a white curve
// with a black halo (achromatic: readable over any theme and any colour vision),
// rising from a dashed line at the row of ζ₀, its height I / max over one period
// (so the scale does not jump as the view pans). Each column is the direct sum
// Field(). The status bar shows the curve's box-counting graph dimension, measured on
// one period sampled at S points over box sizes 1/8 down to 1/(2M) (finer than that
// the truncated sum is smooth). Berry & Klein: at irrational ζ the transverse profile
// is a fractal of graph dimension 3/2 (as summarised by Rosu, Murguia & Ludu, Physica
// A 392, 2013, §2; Berry's box, the same sum, gives D + ½ = 3/2 too) — the finite-M
// estimate approaches it from below. At ζ = 0, ½, 1 the Ronchi profile is the (shifted) square wave,
// dimension 1. The overlay is drawn into the ColorBuffer only (Relief heights and the
// SmoothBuffer are untouched), in every view; in white light the curve is the 550 nm
// intensity.
//
// Scale: by Parseval the mean intensity over a period is Σ|cₙ|² at every ζ
// (propagation conserves energy), and |ψ|² ≤ (Σ|cₙ|)². The log of I/mean is
// mapped from −TalbotDynamicRange decades (dark) up to that bound (white), so
// the scale is the same everywhere in the carpet and under pan / animation.

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class TalbotCarpetCalculator : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    /// <summary>Log intensity (or phase) mapped onto [0, MaxIterations]; 0 = dark.</summary>
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; }
    public double CenterY { get; set; } = 0.5;
    public double Zoom { get; set; } = 1.9;
    public int MaxIterations { get; set; } = 400;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    public const int MaxModes = 4096;

    public TalbotCarpetCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
    }

    /// <summary>The grating's Fourier coefficients c₋M..c_M (index n + M).</summary>
    public static Complex[] Coefficients(FractalParameters p)
    {
        int m = Math.Clamp(p.TalbotModes, 1, MaxModes);
        var c = new Complex[2 * m + 1];
        switch (p.TalbotProfile)
        {
            case TalbotProfile.DeltaComb:
                for (int n = -m; n <= m; n++) c[n + m] = Complex.One;
                break;
            case TalbotProfile.SlitCascade:
            {
                var lineParams = p.Clone();
                lineParams.SlitCascadeLayout = SlitCascadeLayout.Line;   // one period of a 1D grating
                var cascade = SlitCascadeCalculator.Build(lineParams);
                double leaf = Math.Pow(cascade.Ratio, cascade.Depth);
                for (int n = -m; n <= m; n++) c[n + m] = leaf * SlitCascadeCalculator.Amplitude(cascade, n / 2.0, 0.0);
                break;
            }
            default:
            {
                double a = Math.Clamp(double.IsFinite(p.TalbotDuty) ? p.TalbotDuty : 0.3, 1e-6, 1.0);
                for (int n = -m; n <= m; n++)
                {
                    double t = Math.PI * n * a;
                    c[n + m] = a * (n == 0 ? 1.0 : Math.Sin(t) / t);
                }
                break;
            }
        }
        return c;
    }

    /// <summary>ψ(ξ, ζ) by the direct sum (header) — the reference the row
    /// recurrence must match.</summary>
    public static Complex Field(Complex[] c, double xi, double zeta)
    {
        int m = (c.Length - 1) / 2;
        Complex sum = Complex.Zero;
        for (int n = -m; n <= m; n++)
        {
            double ph = 2.0 * Math.PI * (n * xi - (double)n * n * zeta);
            sum += c[n + m] * new Complex(Math.Cos(ph), Math.Sin(ph));
        }
        return sum;
    }

    public void Calculate(CancellationToken ct)
    {
        int width = Width, height = Height;
        if (width < 1 || height < 1) return;
        var fp = FractalParameters;
        var c = Coefficients(fp);
        int m = (c.Length - 1) / 2;
        double sumAbs = 0.0, mean = 0.0;
        foreach (var v in c) { sumAbs += v.Magnitude; mean += v.Real * v.Real + v.Imaginary * v.Imaginary; }
        double logMean = Math.Log10(Math.Max(mean, 1e-300));
        double top = Math.Max(1e-6, 2.0 * Math.Log10(Math.Max(sumAbs, 1e-300)) - logMean);   // bound over mean, decades
        bool phase = fp.TalbotView == TalbotView.Phase;
        bool domain = fp.TalbotView == TalbotView.Domain;   // #1592
        double decades = Math.Clamp(double.IsFinite(fp.TalbotDynamicRange) ? fp.TalbotDynamicRange : 2.0, 0.25, 30.0);

        double pitch = (4.0 / Math.Max(1, width)) / Math.Max(1e-300, Zoom);
        double cx = CenterX, cy = CenterY;
        int maxIt = Math.Max(16, MaxIterations);
        var cm = ColorMap;
        cm.MaxIterations = maxIt;
        uint inSet = cm.InSetColor;
        float[] smooth = SmoothBuffer; uint[] color = ColorBuffer;
        // #1598 — contour lines over the Domain view: side buffers, then DomainContours.
        var contours = domain ? fp.TalbotDomainContours : DomainContourMode.Off;
        if (contours != DomainContourMode.Off) DomainContours.Ensure(ref _ctTurns, ref _ctK, ref _ctLog2, width * height);
        float[] ctTurns = _ctTurns, ctK = _ctK, ctLog2 = _ctLog2;
        double log2Ref = 0.5 * Math.Log2(Math.Max(mean, 1e-300));   // modulus reference: the rms amplitude (Parseval)
        // #1597 — white light: ζ is measured in Talbot distances of 550 nm; z_T = 2d²/λ, so
        // wavelength λ_k sees ζ_k = ζ·r_k (r_k = nm/550). The grating, hence cₙ, is fixed.
        var wlMode = fp.TalbotView == TalbotView.Intensity ? fp.TalbotWhiteLight : WhiteLightMode.Off;
        WhiteLight? wl = wlMode != WhiteLightMode.Off ? new WhiteLight(fp.WhiteLightSamples) : null;
        // #1599 — Detections: |ψ|² is the detection probability.
        bool detections = fp.TalbotView == TalbotView.Detections;
        if (detections && _detWeights.Length != width * height) { _detWeights = new double[width * height]; DetectionCounts = new int[width * height]; }
        double[] detW = _detWeights;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            double zeta = cy + (y - height * 0.5) * pitch;
            if (wl != null) { WhiteLightRow(c, wl, wlMode, zeta, y, width, cx, pitch, logMean, decades, top, maxIt, inSet, smooth, color); return; }
            // bₙ = cₙ·e^{−2πi·n²·ζ}; n² mod a period keeps the angle small at large ζ·n².
            var b = new Complex[c.Length];
            double zf = zeta - Math.Floor(zeta);
            for (int n = -m; n <= m; n++)
            {
                double ang = (double)n * n * zf;
                ang -= Math.Floor(ang);
                double ph = -2.0 * Math.PI * ang;
                b[n + m] = c[n + m] * new Complex(Math.Cos(ph), Math.Sin(ph));
            }
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                double xi = cx + (x - width * 0.5) * pitch;
                double xf = xi - Math.Floor(xi);
                double a0 = 2.0 * Math.PI * xf;
                var w = new Complex(Math.Cos(a0), Math.Sin(a0));
                // Σ bₙ wⁿ from n = −M: start at w^{−M} and multiply up.
                double am = -2.0 * Math.PI * ((m * xf) - Math.Floor(m * xf));
                var wn = new Complex(Math.Cos(am), Math.Sin(am));
                double re = 0.0, im = 0.0;
                for (int k = 0; k < b.Length; k++)
                {
                    var bk = b[k];
                    re += bk.Real * wn.Real - bk.Imaginary * wn.Imaginary;
                    im += bk.Real * wn.Imaginary + bk.Imaginary * wn.Real;
                    wn *= w;
                }
                if (detections) { detW[row + x] = re * re + im * im; continue; }
                if (domain)
                {
                    double m2 = re * re + im * im;
                    double ti = m2 > 0.0 ? Math.Clamp((Math.Log10(m2) - logMean + decades) / (decades + top), 0.0, 1.0) : 0.0;
                    int di = row + x;
                    smooth[di] = (float)(ti * maxIt);
                    color[di] = PhaseWheelBlueAmberMap.ColorAt(Math.Atan2(im, re) / (2.0 * Math.PI), ti);
                    if (contours != DomainContourMode.Off)
                    {
                        ctTurns[di] = m2 > 0.0 ? (float)(Math.Atan2(im, re) / (2.0 * Math.PI)) : float.NaN;
                        ctK[di] = (float)ti;
                        ctLog2[di] = ti > 0.0 ? (float)(0.5 * Math.Log2(m2) - log2Ref) : float.NaN;
                    }
                    continue;
                }
                double t;
                if (phase)
                {
                    t = Math.Atan2(im, re) / (2.0 * Math.PI);
                    t -= Math.Floor(t);
                    t = Math.Max(t, 1e-9);   // phase 0 is a colour, not "dark"
                }
                else
                {
                    double mag2 = re * re + im * im;
                    t = mag2 > 0.0 ? (Math.Log10(mag2) - logMean + decades) / (decades + top) : 0.0;
                }
                int idx = row + x;
                if (!(t > 0.0)) { smooth[idx] = 0f; color[idx] = inSet; continue; }
                float s = (float)(Math.Min(1.0, t) * maxIt);
                // #1591 — phase 1 ≡ 0: a value rounding up to maxIt would read as "in the set".
                if (phase && s >= maxIt) s = (float)(1e-9 * maxIt);
                smooth[idx] = s;
                color[idx] = (uint)cm.Map(s, 0f, maxIt);
            }
        });
        if (contours != DomainContourMode.Off)
            DomainContours.Apply(color, ctTurns, ctK, ctLog2, width, height, contours, fp.TalbotContourCount, ct);
        if (detections)
        {
            DetectionBuildUp.Sample(detW, fp.DetectionCount, fp.DetectionSeed, DetectionCounts, ct);
            DetectionBuildUp.Paint(DetectionCounts, width, height, fp.DetectionDotRadius, fp.DetectionGain, color, smooth, maxIt, ct);
        }
        // #1601 — transverse-profile overlay and its graph dimension.
        if (fp.TalbotSliceOverlay)
        {
            double z0 = double.IsFinite(fp.TalbotSliceZeta) ? fp.TalbotSliceZeta : 0.0;
            var period = PeriodProfile(c, z0, ProfileSamples(m), ct);
            double peak = 0.0;
            foreach (double v in period) peak = Math.Max(peak, v);
            SliceMax = peak;
            SliceDimension = GraphDimension(period, m);
            if (SliceProfile.Length != width) SliceProfile = new double[width];
            var prof = SliceProfile;
            Parallel.For(0, width, new ParallelOptions { CancellationToken = ct }, x =>
            {
                var f = Field(c, cx + (x - width * 0.5) * pitch, z0);
                prof[x] = f.Real * f.Real + f.Imaginary * f.Imaginary;
            });
            SliceBaseRow = (z0 - cy) / pitch + height * 0.5;
            DrawSlice(color, width, height, prof, peak, SliceBaseRow);
            LastPrecisionLabel = FormattableString.Invariant(
                $"slice ζ0 {z0:G6}  graph dim {(double.IsNaN(SliceDimension) ? "n/a" : SliceDimension.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))} (box count, 1/8..1/{2 * m})");
        }
        else LastPrecisionLabel = null;
    }

    /// <summary>Status-bar text: the slice's graph dimension when the overlay is on (#1601).</summary>
    public string? LastPrecisionLabel { get; private set; }
    /// <summary>#1601 — the last overlay's per-column intensity |ψ(ξ_x, ζ₀)|².</summary>
    public double[] SliceProfile { get; private set; } = Array.Empty<double>();
    /// <summary>#1601 — the brightest value over one period (the curve's full height).</summary>
    public double SliceMax { get; private set; }
    /// <summary>#1601 — the screen row of ζ₀ (may lie off screen).</summary>
    public double SliceBaseRow { get; private set; }
    /// <summary>#1601 — box-counting graph dimension of the last slice (NaN = too few scales).</summary>
    public double SliceDimension { get; private set; } = double.NaN;

    /// <summary>Samples per period for the dimension estimate: a power of two, about
    /// 16 per mode, 256..8192.</summary>
    public static int ProfileSamples(int modes)
    {
        int s = 256;
        while (s < 16 * modes && s < 8192) s <<= 1;
        return s;
    }

    /// <summary>|ψ(j/S, ζ)|² for j = 0..S−1: one grating period, by the direct sum.</summary>
    public static double[] PeriodProfile(Complex[] c, double zeta, int samples, CancellationToken ct = default)
    {
        var y = new double[samples];
        Parallel.For(0, samples, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var f = Field(c, (double)j / samples, zeta);
            y[j] = f.Real * f.Real + f.Imaginary * f.Imaginary;
        });
        return y;
    }

    /// <summary>Box-counting dimension of the graph of one period of a profile (#1601).
    /// The values are scaled into [0, 1] and the period into [0, 1); for box size
    /// ε = k/S the count is Σ over columns of ⌊range/ε⌋ + 1 (range = max − min of the
    /// samples in the column and the first sample of the next, wrapping round), and the
    /// dimension is the least-squares slope of ln N against ln(1/ε) over ε = 1/8 down to
    /// 1/(2M) (finer, the M-mode sum is smooth). NaN with fewer than three scales.</summary>
    public static double GraphDimension(double[] y, int modes)
    {
        if (y.Length >= 16 && y.Max() - y.Min() <= 0.0) return 1.0;   // a flat line
        var (eps, boxes) = BoxCounts(y, modes);
        int count = eps.Length;
        if (count < 3) return double.NaN;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < count; i++)
        {
            double lx = Math.Log(1.0 / eps[i]), ly = Math.Log(boxes[i]);
            sx += lx; sy += ly; sxx += lx * lx; sxy += lx * ly;
        }
        return (count * sxy - sx * sy) / (count * sxx - sx * sx);
    }

    /// <summary>The box counts N(ε) behind <see cref="GraphDimension"/>, coarsest first;
    /// empty for a flat profile.</summary>
    public static (double[] Eps, double[] Boxes) BoxCounts(double[] y, int modes)
    {
        int s = y.Length;
        var eps = new System.Collections.Generic.List<double>();
        var boxes = new System.Collections.Generic.List<double>();
        if (s < 16) return (eps.ToArray(), boxes.ToArray());
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (double v in y) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        double span = hi - lo;
        if (!(span > 0.0)) return (eps.ToArray(), boxes.ToArray());
        int kMin = Math.Max(2, (int)Math.Ceiling(s / (2.0 * Math.Max(1, modes))));
        for (int k = s / 8; k >= kMin; k /= 2)
        {
            double e = (double)k / s, n = 0.0;
            for (int start = 0; start < s; start += k)
            {
                double a = double.MaxValue, b = double.MinValue;
                for (int j = start; j <= start + k; j++)   // shares its last sample with the next column (wraps)
                {
                    double v = (y[j % s] - lo) / span;
                    a = Math.Min(a, v); b = Math.Max(b, v);
                }
                n += Math.Floor((b - a) / e) + 1.0;
            }
            eps.Add(e); boxes.Add(n);
        }
        return (eps.ToArray(), boxes.ToArray());
    }

    // #1601 — the curve (white core, black halo) and a dashed marker at the row of ζ₀.
    // Height = I / peak × a quarter of the view; line width grows with resolution so
    // supersampled exports keep it visible.
    private static void DrawSlice(uint[] color, int w, int h, double[] prof, double peak, double baseRow)
    {
        if (w < 2 || h < 8) return;
        int band = Math.Max(8, h / 4);
        int thick = Math.Max(1, (int)Math.Round(h / 400.0));
        var mask = new byte[w * h];   // 1 = halo, 2 = core
        void Mark(int x0, int y0, int y1, int r, byte v)
        {
            if (y0 > y1) (y0, y1) = (y1, y0);
            for (int x = Math.Max(0, x0 - r); x <= Math.Min(w - 1, x0 + r); x++)
                for (int y = Math.Max(0, y0 - r); y <= Math.Min(h - 1, y1 + r); y++)
                {
                    int i = y * w + x;
                    if (mask[i] < v) mask[i] = v;
                }
        }
        int markerRow = (int)Math.Floor(baseRow);
        bool markerOn = markerRow >= 0 && markerRow < h;
        double baseY = Math.Clamp(baseRow, band, h - 1);
        double inv = peak > 0.0 ? 1.0 / peak : 0.0;
        int Row(int x) => (int)Math.Round(Math.Clamp(baseY - Math.Min(1.0, prof[x] * inv) * band, 0, h - 1));
        for (byte pass = 1; pass <= 2; pass++)
        {
            int r = pass == 1 ? thick : thick - 1;
            int prev = Row(0);
            for (int x = 0; x < w; x++)
            {
                int cur = Row(x);
                Mark(x, prev, cur, r, pass);
                prev = cur;
            }
        }
        uint white = 0xFFFFFFFFu, black = 0xFF000000u;
        if (markerOn)
            for (int x = 0; x < w; x++)
                for (int t = 0; t < thick; t++)
                {
                    int y = markerRow + t;
                    if (y >= h) break;
                    int i = y * w + x;
                    if (mask[i] == 0) color[i] = ((x / (6 * thick)) & 1) == 0 ? white : black;
                }
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] == 2) color[i] = white;
            else if (mask[i] == 1) color[i] = black;
    }

    // #1597 — one row of the white-light carpet: K per-wavelength coefficient sets
    // bₙ,k = cₙ·e^{−2πi n² ζ r_k}, each summed with the same recurrence as the
    // monochrome loop; brightness on the Intensity view's log scale.
    private static void WhiteLightRow(Complex[] c, WhiteLight wl, WhiteLightMode mode, double zeta, int y, int width,
        double cx, double pitch, double logMean, double decades, double top, int maxIt, uint inSet, float[] smooth, uint[] color)
    {
        int m = (c.Length - 1) / 2, K = wl.Count;
        var b = new Complex[K][];
        for (int k = 0; k < K; k++)
        {
            double zk = zeta * wl.Ratio[k];
            double zf = zk - Math.Floor(zk);
            b[k] = new Complex[c.Length];
            for (int n = -m; n <= m; n++)
            {
                double ang = (double)n * n * zf;
                ang -= Math.Floor(ang);
                double ph = -2.0 * Math.PI * ang;
                b[k][n + m] = c[n + m] * new Complex(Math.Cos(ph), Math.Sin(ph));
            }
        }
        var inten = new double[K];
        int row = y * width;
        for (int x = 0; x < width; x++)
        {
            double xi = cx + (x - width * 0.5) * pitch;
            double xf = xi - Math.Floor(xi);
            double a0 = 2.0 * Math.PI * xf;
            var w = new Complex(Math.Cos(a0), Math.Sin(a0));
            double am = -2.0 * Math.PI * ((m * xf) - Math.Floor(m * xf));
            var w0 = new Complex(Math.Cos(am), Math.Sin(am));
            for (int k = 0; k < K; k++)
            {
                var wn = w0;
                double re = 0.0, im = 0.0;
                var bk = b[k];
                for (int j = 0; j < bk.Length; j++)
                {
                    re += bk[j].Real * wn.Real - bk[j].Imaginary * wn.Imaginary;
                    im += bk[j].Real * wn.Imaginary + bk[j].Imaginary * wn.Real;
                    wn *= w;
                }
                inten[k] = re * re + im * im;
            }
            double ieff = wl.EffectiveIntensity(inten);
            double t = ieff > 0.0 ? (Math.Log10(ieff) - logMean + decades) / (decades + top) : 0.0;
            int idx = row + x;
            if (!(t > 0.0)) { smooth[idx] = 0f; color[idx] = inSet; continue; }
            smooth[idx] = (float)(Math.Min(1.0, t) * maxIt);
            color[idx] = wl.Colour(inten, t, mode);
        }
    }

    // #1599 — detection weights (|ψ|² per pixel) and the last build-up's per-pixel counts.
    private double[] _detWeights = Array.Empty<double>();
    /// <summary>Detections view: how many detections landed on each pixel in the last render (#1599).</summary>
    public int[] DetectionCounts { get; private set; } = Array.Empty<int>();

    private float[] _ctTurns = Array.Empty<float>(), _ctK = Array.Empty<float>(), _ctLog2 = Array.Empty<float>();   // #1598
}
