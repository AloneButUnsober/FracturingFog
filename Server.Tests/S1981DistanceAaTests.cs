// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1981 part 2 — the adaptive AA mask's distance-estimate skip (idea: pauldelbrot,
// fractalforums.org topic 5693).
//   • the exterior bound is a lower bound: every point of the disc it promises (centre ± bound,
//     sampled on a polar grid) escapes under direct iteration written out here, for Mandelbrot and a
//     connected Julia set (the Douady rabbit), with the pixel mapping checked against the frame;
//   • samples saved on the seahorse view at 4×4, and the result's error against full supersampling
//     next to the neighbour test's own;
//   • off: byte-identical (frame and stats), whatever the minimum distance;
//   • the rotation-domain distance feeds the bound (a quarter of it); Newton has none without it;
//   • the snapshot, --param and the Command builder carry both settings.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1981DistanceAaTests(ITestOutputHelper output)
{
    private const int W = 160, H = 120;

    private static MandelbrotCalculator Mandel(double cx, double cy, double zoom, int maxIt, QualityPreset q) => new(W, H)
    {
        CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIt, Quality = q, ColorMap = new HsvPalette(),
    };

    private static bool Escapes(Complex z, Complex c, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (z.Real * z.Real + z.Imaginary * z.Imaginary > 1e6) return true;
            z = z * z + c;
        }
        return false;
    }

    // Every pixel with a bound past minPx: the disc of that radius (in pixels) round its centre escapes.
    private (int Checked, int Bad, int Agree) CheckBound(float[] bound, int[] iter, int maxIt, double cx, double cy, double scale,
                                                        Func<Complex, int, bool> escapes, int stride)
    {
        int chk = 0, bad = 0, agree = 0, tot = 0;
        for (int y = 0; y < H; y += 3)
            for (int x = 0; x < W; x += 3)
            {
                int i = y * W + x;
                var p = new Complex(cx + (x - W * 0.5) * scale, cy + (y - H * 0.5) * scale);
                tot++;
                if (escapes(p, maxIt) == (iter[i] < maxIt)) agree++;
            }
        for (int i = 0; i < bound.Length; i += stride)
        {
            if (!(bound[i] > AdaptiveAa.DefaultDistanceMinPx)) continue;
            int x = i % W, y = i / W;
            var p = new Complex(cx + (x - W * 0.5) * scale, cy + (y - H * 0.5) * scale);
            double r = bound[i] * scale;
            chk++;
            bool ok = true;
            for (int k = 1; k <= 4 && ok; k++)
                for (int a = 0; a < 16 && ok; a++)
                    if (!escapes(p + Complex.FromPolarCoordinates(r * k / 4.0, 2 * Math.PI * (a + 0.5 * k) / 16), 20000)) ok = false;
            if (!ok) bad++;
        }
        output.WriteLine($"  mapping agrees on {agree}/{tot} pixels");
        Assert.True(agree >= tot * 0.97, $"precondition: pixel mapping ({agree}/{tot})");
        return (chk, bad, agree);
    }

    [Fact]
    public void Exterior_Bound_Is_A_Lower_Bound_Mandelbrot_And_Julia()
    {
        var m = Mandel(-0.7436, 0.1318, 300, 800, QualityPreset.Standard);
        m.Calculate();
        var b = AdaptiveAaDistance.Exterior(m.IterationBuffer, m.MaxIterations, m.DistanceBuffer, m.DistancePixelScale,
                                            m.FinalZrBuffer, m.FinalZiBuffer, 2);
        double scale = Supersampling.PixelScale(m);
        var (chk, bad, _) = CheckBound(b, m.IterationBuffer, m.MaxIterations, m.CenterX, m.CenterY, scale,
                                       (c, n) => Escapes(0, c, n), 7);
        output.WriteLine($"Mandelbrot seahorse: {chk} pixels with bound > 0.707 px checked, {bad} with a non-escaping point");
        Assert.True(chk >= 200, $"precondition ({chk})");
        Assert.Equal(0, bad);

        var rabbit = new Complex(-0.122561, 0.744862);
        var j = new EscapeTimeCalculator(W, H)
        {
            FractalType = FractalType.Julia, MaxIterations = 1000, Zoom = 1.2, ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters { JuliaC = rabbit },
        };
        j.Calculate();
        var bj = j.AdaptiveAaBoundaryDistancePx()!;
        double sj = (3.5 / Math.Max(W, H)) / j.Zoom;
        (chk, bad, _) = CheckBound(bj, j.IterationBuffer, j.MaxIterations, j.CenterX, j.CenterY, sj,
                                   (z, n) => Escapes(z, rabbit, n), 5);
        output.WriteLine($"Julia rabbit: {chk} checked, {bad} bad");
        Assert.True(chk >= 200, $"precondition ({chk})");
        Assert.Equal(0, bad);
    }

    // The bound itself on hand-made buffers: in-set (at the cap) gives 0 whatever the stale distance;
    // a deep escape (G → 0) gives D / 2; an early one, D (1 − e^{−2G}) / (4G).
    [Fact]
    public void Exterior_Bound_Formula()
    {
        var b = AdaptiveAaDistance.Exterior(new[] { 100, 60, 1 }, 100, new[] { 5f, 4f, 3f }, 0.5,
                                            new[] { 3f, 3f, 3f }, new[] { 4f, 4f, 4f }, 2);
        Assert.Equal(0f, b[0]);
        Assert.Equal(4.0 / 2 / 0.5, b[1], 5);
        double g = Math.Log(5) / 2;
        Assert.Equal(3 * (1 - Math.Exp(-2 * g)) / (4 * g) / 0.5, b[2], 5);
    }

    private static uint[] Run(Func<MandelbrotCalculator> make, AdaptiveAa aa, out SupersampleStats st)
    {
        var c = make();
        c.Calculate();
        st = Supersampling.AccumulateMandelbrot(c, 16, aa, CancellationToken.None);
        return (uint[])c.ColorBuffer.Clone();
    }

    private static double MeanErr(uint[] a, uint[] b)
    {
        long s = 0;
        for (int i = 0; i < a.Length; i++)
            for (int k = 0; k < 3; k++) s += Math.Abs((int)((a[i] >> (8 * k)) & 0xFF) - (int)((b[i] >> (8 * k)) & 0xFF));
        return (double)s / (3.0 * a.Length);
    }

    [Fact]
    public void Saves_Samples_On_The_Seahorse_And_Off_Is_Byte_Identical()
    {
        Func<MandelbrotCalculator> make = () => Mandel(-0.7436, 0.1318, 300, 800, QualityPreset.Ultra);
        var full = Run(make, AdaptiveAa.Off, out var stFull);
        var neigh = Run(make, new AdaptiveAa(true, 12, 1.5), out var stN);
        var de = Run(make, new AdaptiveAa(true, 12, 1.5, true), out var stD);
        double eN = MeanErr(neigh, full), eD = MeanErr(de, full);
        output.WriteLine($"full {stFull.PixelSamples}; neighbour {stN.PixelSamples} ({stN.Marked} marked, err {eN:F3}); " +
                         $"+distance {stD.PixelSamples} ({stD.Marked} marked, {stD.DistanceSkipped} skipped, err {eD:F3})");
        Assert.True(stD.DistanceSkipped > 0, "the distance test cleared marked pixels");
        Assert.Equal(stN.Marked - stD.DistanceSkipped, stD.Marked);
        Assert.True(stD.PixelSamples < stN.PixelSamples, "fewer samples");
        Assert.True(eD < eN + 1.0, $"error {eD} vs {eN}");

        // Off (the default) — the mask and the frame as before, whatever the minimum.
        var off = Run(make, new AdaptiveAa(true, 12, 1.5, false, 0.2), out var stOff);
        Assert.Equal(neigh, off);
        Assert.Equal(stN.PixelSamples, stOff.PixelSamples);
        Assert.Equal(0, stOff.DistanceSkipped);
        Assert.False(new FractalParameters().AdaptiveAaDistanceSkip);
        Assert.Equal(AdaptiveAa.DefaultDistanceMinPx, new FractalParameters().AdaptiveAaDistanceMinPx);
        // Cleared pixels keep sample 0's colour.
        var s0 = make(); s0.Calculate();
        int keptS0 = 0;
        for (int i = 0; i < de.Length; i++) if (de[i] == s0.ColorBuffer[i] && neigh[i] != s0.ColorBuffer[i]) keptS0++;
        Assert.True(keptS0 > 0);
    }

    // Samples saved at 4×4 on more views (reported; the alt path through AccumulateAlt).
    [Fact]
    public void Samples_Saved_Across_Views()
    {
        foreach (var (name, cx, cy, zoom, it) in new[] { ("whole set", -0.6, 0.0, 1.0, 500), ("seahorse", -0.7436, 0.1318, 300.0, 800),
                                                          ("elephant", 0.2925, 0.0149, 120.0, 1000) })
        {
            Func<MandelbrotCalculator> make = () => Mandel(cx, cy, zoom, it, QualityPreset.Ultra);
            var full = Run(make, AdaptiveAa.Off, out _);
            var n = Run(make, new AdaptiveAa(true, 12, 1.5), out var a);
            var d = Run(make, new AdaptiveAa(true, 12, 1.5, true), out var b);
            output.WriteLine($"Mandelbrot {name}: samples {a.PixelSamples} -> {b.PixelSamples} ({100.0 * (a.PixelSamples - b.PixelSamples) / a.PixelSamples:F1}% fewer), " +
                             $"{b.DistanceSkipped}/{a.Marked} marked cleared; err vs full {MeanErr(n, full):F3} -> {MeanErr(d, full):F3}");
            Assert.True(b.PixelSamples <= a.PixelSamples);
        }
        foreach (var (name, c) in new[] { ("rabbit", new Complex(-0.122561, 0.744862)), ("dendrite i", Complex.ImaginaryOne) })
        {
            EscapeTimeCalculator Make(bool skip)
            {
                var j = new EscapeTimeCalculator(W, H)
                {
                    FractalType = FractalType.Julia, MaxIterations = 1000, Zoom = 1.2, ColorMap = new HsvPalette(),
                    FractalParameters = new FractalParameters { JuliaC = c, AdaptiveAa = true, AdaptiveAaDistanceSkip = skip },
                };
                j.Calculate();
                return j;
            }
            var a = Supersampling.AccumulateAlt(Make(false), 16, CancellationToken.None, 0);
            var b = Supersampling.AccumulateAlt(Make(true), 16, CancellationToken.None, 0);
            output.WriteLine($"Julia {name}: samples {a.PixelSamples} -> {b.PixelSamples} ({100.0 * (a.PixelSamples - b.PixelSamples) / a.PixelSamples:F1}% fewer), {b.DistanceSkipped}/{a.Marked} cleared");
            Assert.True(b.DistanceSkipped > 0 && b.PixelSamples < a.PixelSamples, name);
        }
    }

    [Fact]
    public void Rotation_Distance_Feeds_The_Bound()
    {
        var j = S1981RotationDomainTests.Julia(S1981RotationDomainTests.SiegelC, on: true);
        var b = j.AdaptiveAaBoundaryDistancePx()!;
        int inSet = 0, fed = 0;
        for (int i = 0; i < b.Length; i++)
        {
            if (j.IterationBuffer[i] < j.MaxIterations) continue;
            inSet++;
            if (b[i] > 0) fed++;
        }
        output.WriteLine($"Siegel: {fed}/{inSet} in-set pixels carry a rotation bound; max {b.Max():F2} px");
        Assert.True(fed >= inSet * 0.9, $"{fed}/{inSet}");
        var off = S1981RotationDomainTests.Julia(S1981RotationDomainTests.SiegelC, on: false).AdaptiveAaBoundaryDistancePx()!;
        for (int i = 0; i < off.Length; i++) if (j.IterationBuffer[i] >= j.MaxIterations) Assert.Equal(0f, off[i]);

        var nova = new NewtonCalculator(32, 24) { FractalType = FractalType.Nova, MaxIterations = 200, ColorMap = ColorPalette.BuiltIns[0] };
        nova.Calculate(CancellationToken.None);
        Assert.Null(nova.AdaptiveAaBoundaryDistancePx());
    }

    [Fact]
    public void Carried_By_The_Snapshot_Param_And_Command_Builder()
    {
        foreach (var t in new[] { FractalType.Mandelbrot, FractalType.Julia, FractalType.Multibrot, FractalType.Nova })
        {
            Assert.Null(RegionFractalParams.Snapshot(t, new FractalParameters())?.AdaptiveAaDistanceSkip);
            var p = new FractalParameters { AdaptiveAa = true, AdaptiveAaDistanceSkip = true, AdaptiveAaDistanceMinPx = 1.5 };
            var snap = RegionFractalParams.Snapshot(t, p)!;
            var cmdSnap = new FracturingFog.Cli.BatchCommandSnapshot { Fractal = t, Parameters = p, FamilyParams = snap.ToKeyValues() };
            Assert.Contains("AdaptiveAaDistanceSkip=", FracturingFog.Cli.BatchCommandBuilder.Build(cmdSnap));
            var args = new List<string> { "--fractal", t.ToString(), "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
            foreach (var kv in cmdSnap.FamilyParams) { args.Add(FracturingFog.Batch.BatchFlags.Param); args.Add(kv.Key + "=" + kv.Value); }
            Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out var opts, out var err), err);
            var overlay = RegionFractalParams.FromKeyValues(opts.Params, t, out var error);
            Assert.True(overlay != null, error);
            var fresh = new FractalParameters();
            overlay!.ApplyTo(fresh);
            Assert.True(fresh.AdaptiveAaDistanceSkip, t.ToString());
            Assert.Equal(1.5, fresh.AdaptiveAaDistanceMinPx);
            Assert.Equal(AdaptiveAa.From(p), AdaptiveAa.From(fresh));
        }
    }
}
