// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1597 — white-light (spectral) colouring. Oracles: the monochrome render (one
// wavelength must reproduce it exactly), Parseval (every wavelength carries the same
// power across a grating period, so the summed intensity's row mean is Σ|cₙ|²), the
// ζ = 0 row of the carpet (the grating itself, the same for every wavelength → pure
// white), CIE 1931 table values for the colour-matching fit, and the Machado CVD
// simulation for the blue → amber mapping.
public sealed class WhiteLightTests
{
    private static SlitCascadeCalculator Slit(FractalParameters p, int w = 64, int h = 40)
    {
        var c = new SlitCascadeCalculator(w, h) { Zoom = 0.5, MaxIterations = 400, ColorMap = new HsvPalette(), FractalParameters = p };
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static TalbotCarpetCalculator Talbot(FractalParameters p, int w, int h, double cx, double cy, double zoom, int maxIt = 400)
    {
        var c = new TalbotCarpetCalculator(w, h) { CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIt, ColorMap = new HsvPalette(), FractalParameters = p };
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static (float L, float a, float b) Lab(uint c) => PerceptualRamp.RgbToOkLab((byte)(c >> 16), (byte)(c >> 8), (byte)c);

    [Theory]
    [InlineData(WhiteLightMode.TrueSpectrum)]
    [InlineData(WhiteLightMode.BlueAmber)]
    public void OneWavelength_ReproducesTheMonochromeBrightness(WhiteLightMode mode)
    {
        var mono = Slit(new FractalParameters { SlitCascadeChildren = 6, SlitCascadeDepth = 3 });
        var white = Slit(new FractalParameters { SlitCascadeChildren = 6, SlitCascadeDepth = 3, SlitCascadeWhiteLight = mode, WhiteLightSamples = 1 });
        Assert.Equal(mono.SmoothBuffer, white.SmoothBuffer);

        var tm = Talbot(new FractalParameters { TalbotModes = 16 }, 64, 36, 0, 0.37, 1.9);
        var tw = Talbot(new FractalParameters { TalbotModes = 16, TalbotWhiteLight = mode, WhiteLightSamples = 1 }, 64, 36, 0, 0.37, 1.9);
        Assert.Equal(tm.SmoothBuffer, tw.SmoothBuffer);
        // One wavelength has no spread: the colour is grey (white light of one line).
        Assert.All(tw.ColorBuffer, c => { var (_, a, b) = Lab(c); Assert.True(Math.Abs(a) < 0.01 && Math.Abs(b) < 0.01); });
    }

    // The rendered brightness is the direct spectral sum: ȳ-weighted intensities of
    // the per-wavelength patterns, Slit Cascade I_k(s) = |A_k(s/r_k)|²/r_k² with the
    // cascade rebuilt at λ·r_k, Talbot |ψ(ξ, ζ·r_k)|² — evaluated with the static
    // direct sums, not the render loops.
    [Fact]
    public void Brightness_IsTheDirectSpectralSum()
    {
        const int K = 5, w = 48, h = 32, maxIt = 1_000_000;
        var nm = Enumerable.Range(0, K).Select(i => 380.0 + (i + 0.5) * 340.0 / K).ToArray();
        var yb = nm.Select(WhiteLight.YBar).ToArray();
        double ys = yb.Sum();

        var p = new FractalParameters { SlitCascadeChildren = 5, SlitCascadeDepth = 3, SlitCascadeDynamicRange = 30,
            SlitCascadeWhiteLight = WhiteLightMode.TrueSpectrum, WhiteLightSamples = K };
        var s = new SlitCascadeCalculator(w, h) { Zoom = 0.5, MaxIterations = maxIt, ColorMap = new HsvPalette(), FractalParameters = p };
        s.Calculate(CancellationToken.None);
        var c0 = SlitCascadeCalculator.Build(p);
        double logI0 = 2 * c0.Depth * Math.Log10(c0.P.Sum(Math.Sqrt)), pitch = 4.0 / w / 0.5;
        var cs = nm.Select(n => { var q = p.Clone(); q.SlitCascadeWavelength = p.SlitCascadeWavelength * n / 550.0; return SlitCascadeCalculator.Build(q); }).ToArray();
        for (int i = 0; i < w * h; i += 5)
        {
            double sx = (i % w - w * 0.5) * pitch, sy = (i / w - h * 0.5) * pitch, ie = 0;
            for (int k = 0; k < K; k++)
            {
                double r = nm[k] / 550.0;
                var a = SlitCascadeCalculator.Amplitude(cs[k], sx / r, sy / r);
                ie += yb[k] / ys * (a.Real * a.Real + a.Imaginary * a.Imaginary) / (r * r);
            }
            double t = Math.Clamp(1 + (Math.Log10(ie) - logI0) / 30, 0, 1);
            Assert.True(Math.Abs(s.SmoothBuffer[i] / (double)maxIt - t) < 1e-5, $"slit pixel {i}: {s.SmoothBuffer[i] / maxIt} vs {t}");
        }

        var tp = new FractalParameters { TalbotModes = 12, TalbotDynamicRange = 30, TalbotWhiteLight = WhiteLightMode.BlueAmber, WhiteLightSamples = K };
        var tc = Talbot(tp, w, h, 0.1, 0.43, 1.9, maxIt);
        var cf = TalbotCarpetCalculator.Coefficients(tp);
        double mean = cf.Sum(v => v.Real * v.Real + v.Imaginary * v.Imaginary), top = 2 * Math.Log10(cf.Sum(v => v.Magnitude)) - Math.Log10(mean);
        double tpitch = 4.0 / w / 1.9;
        for (int i = 0; i < w * h; i += 5)
        {
            double xi = 0.1 + (i % w - w * 0.5) * tpitch, zeta = 0.43 + (i / w - h * 0.5) * tpitch, ie = 0;
            for (int k = 0; k < K; k++)
            {
                var f = TalbotCarpetCalculator.Field(cf, xi, zeta * nm[k] / 550.0);
                ie += yb[k] / ys * (f.Real * f.Real + f.Imaginary * f.Imaginary);
            }
            double t = Math.Clamp((Math.Log10(ie) - Math.Log10(mean) + 30) / (30 + top), 0, 1);
            Assert.True(Math.Abs(tc.SmoothBuffer[i] / (double)maxIt - t) < 1e-5, $"talbot pixel {i}: {tc.SmoothBuffer[i] / maxIt} vs {t}");
        }
    }

    // Parseval: across one grating period every wavelength's intensity averages to
    // Σ|cₙ|², so the photopic-weighted sum does too, at every distance ζ.
    [Fact]
    public void TalbotCarpet_EnergyIsConservedAcrossTheSpectrumSum()
    {
        const int w = 64, h = 6, maxIt = 1_000_000;
        var p = new FractalParameters { TalbotModes = 8, TalbotWhiteLight = WhiteLightMode.TrueSpectrum, WhiteLightSamples = 24, TalbotDynamicRange = 30 };
        // 64 pixels span exactly one period ξ ∈ [0, 1): pitch = 4 / w / zoom = 1/64.
        var calc = Talbot(p, w, h, 0.5, 0.31, 4.0, maxIt);
        var cf = TalbotCarpetCalculator.Coefficients(p);
        double mean = cf.Sum(v => v.Real * v.Real + v.Imaginary * v.Imaginary), sumAbs = cf.Sum(v => v.Magnitude);
        double logMean = Math.Log10(mean), top = 2 * Math.Log10(sumAbs) - logMean, dec = 30;
        for (int y = 0; y < h; y++)
        {
            double rowMean = 0;
            for (int x = 0; x < w; x++)
            {
                double t = calc.SmoothBuffer[y * w + x] / (double)maxIt;
                Assert.True(t > 0 && t < 1, "brightness must not clip for this test");
                rowMean += Math.Pow(10, t * (dec + top) + logMean - dec);
            }
            rowMean /= w;
            Assert.True(Math.Abs(rowMean / mean - 1) < 1e-4, $"row {y}: mean intensity {rowMean} vs Σ|c|² {mean}");
        }
    }

    // ζ = 0 is the grating itself for every wavelength: equal intensity at every
    // wavelength → exactly white (grey) in both mappings; away from it, colour.
    [Theory]
    [InlineData(WhiteLightMode.TrueSpectrum)]
    [InlineData(WhiteLightMode.BlueAmber)]
    public void TalbotCarpet_TheGratingPlaneIsWhite_AndTheCarpetIsColoured(WhiteLightMode mode)
    {
        const int w = 96, h = 64;
        var p = new FractalParameters { TalbotModes = 16, TalbotWhiteLight = mode };
        var calc = Talbot(p, w, h, 0.0, 0.0, 1.9);   // row h/2 sits exactly on ζ = 0
        int row = h / 2, grey = 0;
        for (int x = 0; x < w; x++)
        {
            var (L, a, b) = Lab(calc.ColorBuffer[row * w + x]);
            if (L < 0.2) continue;
            Assert.True(Math.Abs(a) < 0.01 && Math.Abs(b) < 0.01, $"ζ = 0, x = {x}: a {a:F3} b {b:F3}");
            grey++;
        }
        Assert.True(grey > w / 3);
        int coloured = calc.ColorBuffer.Count(c => { var (_, a, b) = Lab(c); return Math.Sqrt(a * a + b * b) > 0.05; });
        Assert.True(coloured > w * h / 10, $"only {coloured} coloured pixels");
    }

    [Fact]
    public void BlueAmber_HasNoRedGreenAxis_AndSortsByWavelength()
    {
        var t = Talbot(new FractalParameters { TalbotModes = 16, TalbotWhiteLight = WhiteLightMode.BlueAmber }, 96, 64, 0, 0.3, 1.9);
        Assert.All(t.ColorBuffer, c => Assert.True(Math.Abs(Lab(c).a) < 0.01, $"a = {Lab(c).a}"));
        var s = Slit(new FractalParameters { SlitCascadeWhiteLight = WhiteLightMode.BlueAmber });
        Assert.All(s.ColorBuffer, c => Assert.True(Math.Abs(Lab(c).a) < 0.01));

        var wl = new WhiteLight(24);
        double[] Spectrum(Func<double, double> f) => wl.Nm.Select(f).ToArray();
        var red = Spectrum(nm => nm > 600 ? 1.0 : 0.1);
        var blue = Spectrum(nm => nm < 480 ? 1.0 : 0.1);
        Assert.True(wl.Chroma(red, WhiteLightMode.BlueAmber).b > 0.03, "long wavelengths → amber");
        Assert.True(wl.Chroma(blue, WhiteLightMode.BlueAmber).b < -0.03, "short wavelengths → blue");
        Assert.True(wl.Chroma(red, WhiteLightMode.TrueSpectrum).a > 0.03, "true spectrum: long wavelengths → red");
        Assert.True(wl.Chroma(blue, WhiteLightMode.TrueSpectrum).b < -0.03, "true spectrum: short wavelengths → blue");
        var flat = Spectrum(_ => 1.0);
        Assert.Equal(0.0, wl.Chroma(flat, WhiteLightMode.TrueSpectrum).a, 6);
        Assert.Equal(0.0, wl.Chroma(flat, WhiteLightMode.BlueAmber).b, 6);
    }

    // Red/green colour blindness (Machado 2009): long- vs short-heavy light stays
    // clearly apart in BlueAmber for deutan and protan viewers and keeps nearly all of
    // its difference, while a red- vs green-heavy pair in the true spectrum loses far
    // more — why the second mapping exists.
    [Fact]
    public void BlueAmber_StaysDistinguishable_UnderRedGreenColourBlindness()
    {
        var wl = new WhiteLight(24);
        double[] S(Func<double, double> f) => wl.Nm.Select(f).ToArray();
        uint C(double[] s, WhiteLightMode m) => wl.Colour(s, 1.0, m);
        (byte, byte, byte) Rgb(uint c) => ((byte)(c >> 16), (byte)(c >> 8), (byte)c);
        double De(uint x, uint y, CvdType? t)
        {
            var (r1, g1, b1) = Rgb(x); var (r2, g2, b2) = Rgb(y);
            if (t is { } tt) { (r1, g1, b1) = CvdSimulation.Simulate(r1, g1, b1, tt); (r2, g2, b2) = CvdSimulation.Simulate(r2, g2, b2, tt); }
            return PerceptualRamp.DeltaEOk(r1, g1, b1, r2, g2, b2);
        }
        var longS = S(nm => nm > 600 ? 1.0 : 0.1); var shortS = S(nm => nm < 480 ? 1.0 : 0.1);
        var greenS = S(nm => nm is > 500 and < 560 ? 1.0 : 0.1);
        foreach (var t in new[] { CvdType.Deutan, CvdType.Protan })
        {
            uint la = C(longS, WhiteLightMode.BlueAmber), sa = C(shortS, WhiteLightMode.BlueAmber);
            double ba = De(la, sa, t), baKept = ba / De(la, sa, null);
            Assert.True(ba >= 0.15, $"{t}: BlueAmber long vs short ΔE {ba:F3}");
            Assert.True(baKept >= 0.9, $"{t}: BlueAmber kept only {baKept:P0}");
            uint lt = C(longS, WhiteLightMode.TrueSpectrum), gt = C(greenS, WhiteLightMode.TrueSpectrum);
            double rgKept = De(lt, gt, t) / De(lt, gt, null);
            Assert.True(rgKept < baKept - 0.2, $"{t}: true-spectrum red vs green kept {rgKept:P0}, BlueAmber {baKept:P0}");
        }
    }

    // Wyman–Sloan–Shirley fit against CIE 1931 2° table values (the fit's stated error
    // is a few percent of the peak).
    [Theory]
    [InlineData(555.0, 'y', 1.0002)]
    [InlineData(600.0, 'x', 1.0622)]
    [InlineData(600.0, 'y', 0.6310)]
    [InlineData(450.0, 'x', 0.3362)]
    [InlineData(450.0, 'z', 1.7721)]
    public void ColourMatchingFit_AgreesWithTheCie1931Table(double nm, char bar, double table)
    {
        double v = bar switch { 'x' => WhiteLight.XBar(nm), 'y' => WhiteLight.YBar(nm), _ => WhiteLight.ZBar(nm) };
        Assert.True(Math.Abs(v - table) < 0.06, $"{bar}̄({nm}) = {v:F4}, table {table}");
    }

    [Fact]
    public void Settings_RoundTripThroughTheCommandBuilder()
    {
        void Check(FractalType type, FractalParameters p, Action<FractalParameters> a)
        {
            var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            {
                Fractal = type, Parameters = p, FamilyParams = RegionFractalParams.Snapshot(type, p)!.ToKeyValues(),
            });
            Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
            var fresh = new FractalParameters();
            RegionFractalParams.FromKeyValues(o.Params, type, out _)!.ApplyTo(fresh);
            a(fresh);
        }
        Check(FractalType.SlitCascade, new FractalParameters { SlitCascadeWhiteLight = WhiteLightMode.BlueAmber, WhiteLightSamples = 9 },
            f => { Assert.Equal(WhiteLightMode.BlueAmber, f.SlitCascadeWhiteLight); Assert.Equal(9, f.WhiteLightSamples); });
        Check(FractalType.TalbotCarpet, new FractalParameters { TalbotWhiteLight = WhiteLightMode.TrueSpectrum, WhiteLightSamples = 31 },
            f => { Assert.Equal(WhiteLightMode.TrueSpectrum, f.TalbotWhiteLight); Assert.Equal(31, f.WhiteLightSamples); });
    }
}
