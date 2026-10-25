// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1344: --adaptiveaaprobe [threshold tolerance]. Adaptive anti-aliasing against
// full supersampling, on the CPU (no GPU kernel), for a set of Mandelbrot views at
// 2×2 and 4×4: the fraction of pixel-samples adaptive computes, both wall times,
// and the colour error of the adaptive image against the full one (mean and 99th
// percentile of the per-pixel largest channel difference, and the share of pixels
// off by more than 4 levels). Also checks, on every view, that unmarked pixels are
// exactly the one-sample render and that threshold −1 / tolerance 0 (every pixel,
// every sample) is the full image byte for byte.
// #1382: then the same for the ten 3D raymarchers (CPU march, default lighting) at
// their home views.
// Exit 0 = the checks pass; 1 = one failed.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog
{
    internal static class AdaptiveAaProbe
    {
        private sealed record View(string Name, double Cx, double Cy, double Zoom, int MaxIter, Func<IColorMap> Theme);

        private static readonly View[] Views =
        [
            new("whole set", -0.5, 0.0, 1.0, 500, () => new HsvPalette()),
            new("seahorse 1e3", -0.7436, 0.1318, 1e3, 1000, () => new HsvPalette()),
            new("elephant 1e2", 0.2850, 0.0110, 1e2, 800, () => new HsvPalette()),
            new("minibrot 1e4", -1.7497, 0.0, 1e4, 1500, () => new HsvPalette()),
            new("spiral DD 1e14", -0.743643887037151, 0.13182590420533, 1e14, 3000, () => new HsvPalette()),
        ];

        public static int Run(string[] args)
        {
            double thr = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : AdaptiveAa.DefaultThreshold;
            double tol = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : AdaptiveAa.DefaultTolerance;
            const int W = 640, H = 360;
            int failures = 0;
            Console.WriteLine($"adaptiveaaprobe {W}x{H}, CPU, threshold {thr}, tolerance {tol}");
            foreach (int n in new[] { 4, 16 })
            {
                var quality = n == 4 ? QualityPreset.High : QualityPreset.Ultra;
                foreach (var v in Views)
                {
                    MandelbrotCalculator Calc() => new(W, H)
                    {
                        CenterX = v.Cx, CenterY = v.Cy, Zoom = v.Zoom, MaxIterations = v.MaxIter,
                        Quality = quality, ColorMap = v.Theme(),
                    };
                    var one = Calc(); one.Calculate();
                    var single = (uint[])one.ColorBuffer.Clone();

                    var full = Calc(); full.Calculate();
                    long t0 = Stopwatch.GetTimestamp();
                    Supersampling.AccumulateMandelbrot(full, n, AdaptiveAa.Off, CancellationToken.None);
                    double fullMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

                    var ad = Calc(); ad.Calculate();
                    var mask = new byte[W * H];
                    Supersampling.BuildMask(ad.ColorBuffer, ad.IterationBuffer, ad.MaxIterations, W, H, thr, mask);
                    t0 = Stopwatch.GetTimestamp();
                    var st = Supersampling.AccumulateMandelbrot(ad, n, new AdaptiveAa(true, thr, tol), CancellationToken.None);
                    double adMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

                    var every = Calc(); every.Calculate();
                    Supersampling.AccumulateMandelbrot(every, n, new AdaptiveAa(true, -1, 0), CancellationToken.None);

                    var err = new int[W * H];
                    for (int i = 0; i < err.Length; i++) err[i] = MaxDiff(ad.ColorBuffer[i], full.ColorBuffer[i]);
                    var sorted = err.OrderBy(e => e).ToArray();
                    double mean = err.Average();
                    int p99 = sorted[(int)(sorted.Length * 0.99)];
                    double over4 = err.Count(e => e > 4) * 100.0 / err.Length;

                    bool unmarkedOk = Enumerable.Range(0, W * H).All(i => mask[i] != 0 || ad.ColorBuffer[i] == single[i]);
                    bool everyOk = every.ColorBuffer.SequenceEqual(full.ColorBuffer);
                    if (!unmarkedOk) { failures++; Console.Error.WriteLine($"  FAIL {v.Name}: an unmarked pixel is not the one-sample colour"); }
                    if (!everyOk) { failures++; Console.Error.WriteLine($"  FAIL {v.Name}: every pixel / every sample differs from full"); }

                    Console.WriteLine($"  {n,2}x {v.Name,-15}: marked {st.Marked * 100.0 / st.Pixels,5:F1}%, samples {st.PixelSamples * 100.0 / st.FullPixelSamples,5:F1}% of full; " +
                        $"{fullMs,7:F0} -> {adMs,6:F0} ms ({fullMs / adMs:F1}x); error mean {mean:F2}, p99 {p99}, >4 levels {over4:F2}%");
                }
            }
            failures += Run3D(W, H, thr, tol);
            Console.WriteLine(failures == 0 ? "adaptiveaaprobe OK" : $"adaptiveaaprobe FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        // #1382 — the 3D raymarchers.
        private static int Run3D(int w, int h, double thr, double tol)
        {
            int failures = 0;
            Console.WriteLine($"  3D raymarchers {w}x{h}, CPU march, default lighting:");
            var families = new (string Name, Func<IFractalCalculator> Make)[]
            {
                ("Mandelbulb", () => new MandelbulbCalculator(w, h)), ("Mandelbox", () => new MandelboxCalculator(w, h)),
                ("KIFS", () => new KifsCalculator(w, h)), ("Quat Julia", () => new QuatJuliaCalculator(w, h)),
                ("Quat Mandelbrot", () => new QuatMandelbrotCalculator(w, h)), ("Kleinian", () => new KleinianCalculator(w, h)),
                ("Bicomplex", () => new BicomplexMandelbrotCalculator(w, h)), ("Coquaternion", () => new CoquaternionMandelbrotCalculator(w, h)),
                ("Julibrot volume", () => new DualOrbitVolumeCalculator(w, h)), ("User Bulb", () => new UserBulbCalculator(w, h)),
            };
            foreach (int n in new[] { 4, 16 })
                foreach (var (name, make) in families)
                {
                    IFractalCalculator Calc(bool adaptive, double t, double tl)
                    {
                        var fx = LightingFxData.CreateDefault();
                        fx.UseGpuRender = false;
                        var c = make();
                        c.ColorMap = ColorPalette.BuiltIns[0];
                        ((dynamic)c).FractalParameters = new FractalParameters
                        {
                            Lighting = fx, UserBulbSource = "z^8 + c",
                            AdaptiveAa = adaptive, AdaptiveAaThreshold = t, AdaptiveAaTolerance = tl,
                        };
                        c.Calculate(CancellationToken.None);
                        return c;
                    }
                    var one = Calc(true, thr, tol);
                    var single = one.ColorBuffer.Take(w * h).ToArray();
                    var full = Calc(false, thr, tol);
                    long t0 = Stopwatch.GetTimestamp();
                    Supersampling.AccumulateAlt(full, n, CancellationToken.None, histogramEq: 0);
                    double fullMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    var ad = Calc(true, thr, tol);
                    var mask = new byte[w * h];
                    Supersampling.BuildMask(single, null, 0, w, h, thr, mask);
                    t0 = Stopwatch.GetTimestamp();
                    var st = Supersampling.AccumulateAlt(ad, n, CancellationToken.None, histogramEq: 0);
                    double adMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    var err = new int[w * h];
                    for (int i = 0; i < err.Length; i++) err[i] = MaxDiff(ad.ColorBuffer[i], full.ColorBuffer[i]);
                    var sorted = err.OrderBy(e => e).ToArray();
                    bool unmarkedOk = Enumerable.Range(0, w * h).All(i => mask[i] != 0 || ad.ColorBuffer[i] == single[i]);
                    if (!unmarkedOk) { failures++; Console.Error.WriteLine($"  FAIL {name}: an unmarked pixel is not the one-sample colour"); }
                    Console.WriteLine($"  {n,2}x {name,-15}: marked {st.Marked * 100.0 / st.Pixels,5:F1}%, samples {st.PixelSamples * 100.0 / st.FullPixelSamples,5:F1}% of full; " +
                        $"{fullMs,7:F0} -> {adMs,6:F0} ms ({fullMs / adMs:F1}x); error mean {err.Average():F2}, p99 {sorted[(int)(sorted.Length * 0.99)]}");
                }
            return failures;
        }

        private static int MaxDiff(uint a, uint b)
        {
            int m = 0;
            for (int s = 0; s < 32; s += 8) m = Math.Max(m, Math.Abs((int)((a >> s) & 0xFF) - (int)((b >> s) & 0xFF)));
            return m;
        }
    }
}
