// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1599 — the particle build-up views. Oracles: the direct amplitude sums
// (SlitCascadeCalculator.Amplitude, TalbotCarpetCalculator.Field) and the
// dual-orbit PathInterference field give each pixel's probability; at large N the
// detection histogram must match it (χ² per degree of freedom ≈ 1 on pixel blocks).
public sealed class DetectionBuildUpTests
{
    private static SlitCascadeCalculator Slit(FractalParameters p, int w, int h)
    {
        var c = new SlitCascadeCalculator(w, h) { Zoom = 1.0, MaxIterations = 400, ColorMap = new HsvPalette(), FractalParameters = p };
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static TalbotCarpetCalculator Talbot(FractalParameters p, int w, int h)
    {
        var c = new TalbotCarpetCalculator(w, h) { CenterY = 0.5, Zoom = 1.9, MaxIterations = 400, ColorMap = new HsvPalette(), FractalParameters = p };
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static FractalParameters SlitP(int n, int seed = 1, int radius = 1) => new()
    {
        SlitCascadeView = SlitCascadeView.Detections, SlitCascadeChildren = 5, SlitCascadeDepth = 3, SlitCascadeCharge = 1,
        DetectionCount = n, DetectionSeed = seed, DetectionDotRadius = radius,
    };

    // χ² of the counts against N·w/Σw, summed over b×b pixel blocks with expected ≥ 20.
    private static (double chi2PerDof, int dof) BlockChi2(int[] counts, double[] weights, int w, int h, int b)
    {
        double total = weights.Sum(), n = counts.Sum(x => (long)x);
        double chi2 = 0; int bins = 0;
        for (int by = 0; by < h; by += b)
            for (int bx = 0; bx < w; bx += b)
            {
                double obs = 0, exp = 0;
                for (int y = by; y < Math.Min(h, by + b); y++)
                    for (int x = bx; x < Math.Min(w, bx + b); x++) { obs += counts[y * w + x]; exp += weights[y * w + x]; }
                exp *= n / total;
                if (exp < 20) continue;
                chi2 += (obs - exp) * (obs - exp) / exp;
                bins++;
            }
        return (chi2 / (bins - 1), bins - 1);
    }

    private static void AssertConverges(int[] counts, double[] weights, int w, int h, int n, string what)
    {
        Assert.Equal(n, counts.Sum(x => (long)x));
        for (int i = 0; i < weights.Length; i++)
            if (weights[i] <= 0) Assert.Equal(0, counts[i]);   // no detections where the probability is zero
        var (c2, dof) = BlockChi2(counts, weights, w, h, 4);
        Assert.True(dof >= 40, $"{what}: only {dof} blocks");
        // E[χ²/dof] = 1, sd = √(2/dof) ≈ 0.2 at 50 blocks: a wrong distribution gives ≫ 2.
        Assert.True(c2 > 0.4 && c2 < 1.9, $"{what}: χ²/dof = {c2:F2} over {dof} dof");
    }

    [Fact]
    public void SlitCascade_HistogramConvergesToTheIntensity()
    {
        const int w = 64, h = 48, n = 2_000_000;
        var p = SlitP(n);
        var calc = Slit(p, w, h);
        var c = SlitCascadeCalculator.Build(p);
        double pitch = 4.0 / w;
        var wts = new double[w * h];
        for (int i = 0; i < wts.Length; i++)
        {
            var a = SlitCascadeCalculator.Amplitude(c, (i % w - w * 0.5) * pitch, (i / w - h * 0.5) * pitch);
            wts[i] = a.Real * a.Real + a.Imaginary * a.Imaginary;
        }
        AssertConverges(calc.DetectionCounts, wts, w, h, n, "slit");
        // Sanity: a wrong distribution (uniform) fails the same test.
        var (bad, _) = BlockChi2(calc.DetectionCounts, Enumerable.Repeat(1.0, w * h).ToArray(), w, h, 4);
        Assert.True(bad > 10, $"uniform should not fit, χ²/dof {bad:F1}");
    }

    [Fact]
    public void TalbotCarpet_HistogramConvergesToTheIntensity()
    {
        const int w = 64, h = 48, n = 2_000_000;
        var p = new FractalParameters { TalbotView = TalbotView.Detections, TalbotModes = 16, DetectionCount = n };
        var calc = Talbot(p, w, h);
        var cf = TalbotCarpetCalculator.Coefficients(p);
        double pitch = 4.0 / w / 1.9;
        var wts = new double[w * h];
        for (int i = 0; i < wts.Length; i++)
        {
            var f = TalbotCarpetCalculator.Field(cf, (i % w - w * 0.5) * pitch, 0.5 + (i / w - h * 0.5) * pitch);
            wts[i] = f.Real * f.Real + f.Imaginary * f.Imaginary;
        }
        AssertConverges(calc.DetectionCounts, wts, w, h, n, "talbot");
    }

    [Fact]
    public void JulibrotPair_HistogramConvergesToTheInterferenceField()
    {
        const int w = 64, h = 48, it = 400, n = 2_000_000;
        DualOrbitEscapeCalculator Run(DualOrbitField f)
        {
            var c = new DualOrbitEscapeCalculator(w, h)
            {
                CenterX = -0.6, CenterY = 0.0, Zoom = 0.6, MaxIterations = it, ColorMap = new HsvPalette(),
                FractalParameters = new FractalParameters
                {
                    DualOrbitField = f, DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitInterferenceK = 4,
                    DetectionCount = n,
                },
            };
            c.Calculate();
            return c;
        }
        var det = Run(DualOrbitField.PathInterferenceDetections);
        var inten = Run(DualOrbitField.PathInterference);
        // The intensity field is the probability; it is also the detection view's height.
        Assert.Equal(inten.ColorScalarBuffer, det.ColorScalarBuffer);
        // The interior and dead pairs have intensity 0, so they get no detections.
        var wts = inten.ColorScalarBuffer.Select(v => (double)v).ToArray();
        Assert.Contains(wts, v => v <= 0);
        AssertConverges(det.DetectionCounts, wts, w, h, n, "dual");
    }

    [Fact]
    public void SameSeed_SameImage_AndMoreDetectionsOnlyAddDots()
    {
        const int w = 96, h = 64;
        var a = Slit(SlitP(20000, seed: 7), w, h);
        var b = Slit(SlitP(20000, seed: 7), w, h);
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
        Assert.Equal(a.DetectionCounts, b.DetectionCounts);
        var other = Slit(SlitP(20000, seed: 8), w, h);
        Assert.NotEqual(a.DetectionCounts, other.DetectionCounts);
        // Prefix property, across a chunk boundary and inside one.
        foreach (int few in new[] { 100, DetectionBuildUp.ChunkSize, DetectionBuildUp.ChunkSize + 1, 9999 })
        {
            var small = (int[])Slit(SlitP(few, seed: 7), w, h).DetectionCounts.Clone();
            Assert.Equal(few, small.Sum());
            for (int i = 0; i < small.Length; i++) Assert.True(small[i] <= a.DetectionCounts[i], $"N={few}: pixel {i} lost a dot");
        }
        // Zero detections = a black screen.
        Assert.All(Slit(SlitP(0), w, h).ColorBuffer, c => Assert.Equal(0xFF000000u, c));
    }

    // Exposure: radius 0 shows each pixel's own count; radius r sums the disk.
    [Fact]
    public void Painting_IsTheExposureOfTheDotsWithinTheRadius()
    {
        const int w = 64, h = 40;
        var p = SlitP(30000, radius: 0); p.DetectionGain = 0.3;
        var calc = Slit(p, w, h);
        for (int i = 0; i < w * h; i++)
            Assert.Equal(DetectionBuildUp.Grey(1 - Math.Exp(-0.3 * calc.DetectionCounts[i])), calc.ColorBuffer[i]);
        var p2 = SlitP(30000, radius: 2); p2.DetectionGain = 0.3;
        var calc2 = Slit(p2, w, h);
        var cnt = calc2.DetectionCounts;
        for (int y = 2; y < h - 2; y += 5)
            for (int x = 2; x < w - 2; x += 5)
            {
                long s = 0;
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) if (dx * dx + dy * dy <= 4) s += cnt[(y + dy) * w + x + dx];
                Assert.Equal(DetectionBuildUp.Grey(1 - Math.Exp(-0.3 * s)), calc2.ColorBuffer[y * w + x]);
            }
        // Height = exposure, below MaxIterations (never read as "in the set").
        Assert.DoesNotContain(calc2.SmoothBuffer, v => v >= 400);
    }

    // Achromatic dots: OkLab a = b = 0, so the view reads the same for every colour
    // vision (Machado simulation leaves a grey grey), and brightness rises with dots.
    [Fact]
    public void Dots_AreGrey_AndBrighterWithMoreDetections()
    {
        float prevL = -1;
        foreach (double v in new[] { 0.05, 0.2, 0.4, 0.6, 0.8, 0.99 })
        {
            uint c = DetectionBuildUp.Grey(v);
            byte r = (byte)(c >> 16), g = (byte)(c >> 8), b = (byte)c;
            var (L, a, bb) = PerceptualRamp.RgbToOkLab(r, g, b);
            Assert.True(Math.Abs(a) < 0.01 && Math.Abs(bb) < 0.01, $"v {v}: not grey");
            Assert.True(L > prevL); prevL = L;
            foreach (var t in new[] { CvdType.Deutan, CvdType.Protan })
            {
                var s = CvdSimulation.Simulate(r, g, b, t);
                Assert.True(PerceptualRamp.DeltaEOk(r, g, b, s.Item1, s.Item2, s.Item3) < 0.02, $"{t} changes grey {v}");
            }
        }
    }

    [Fact]
    public void Settings_RoundTripThroughTheCommandBuilder_AndAnimate()
    {
        void Check(FractalType type, FractalParameters p)
        {
            p.DetectionCount = 123456; p.DetectionSeed = -42; p.DetectionDotRadius = 3; p.DetectionGain = 1.25;
            var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            {
                Fractal = type, Parameters = p, FamilyParams = RegionFractalParams.Snapshot(type, p)!.ToKeyValues(),
            });
            Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
            var fresh = new FractalParameters();
            RegionFractalParams.FromKeyValues(o.Params, type, out _)!.ApplyTo(fresh);
            Assert.Equal(123456, fresh.DetectionCount);
            Assert.Equal(-42, fresh.DetectionSeed);
            Assert.Equal(3, fresh.DetectionDotRadius);
            Assert.Equal(1.25, fresh.DetectionGain);
            Assert.Contains(FractalAnimatableParamsMap.For(type), d => d.ParamName == "DetectionCount");
        }
        Check(FractalType.SlitCascade, new FractalParameters { SlitCascadeView = SlitCascadeView.Detections });
        Check(FractalType.TalbotCarpet, new FractalParameters { TalbotView = TalbotView.Detections });
        Check(FractalType.JulibrotPair, new FractalParameters { DualOrbitField = DualOrbitField.PathInterferenceDetections });
    }
}
