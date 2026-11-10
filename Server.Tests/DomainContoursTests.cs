// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1598 — contour lines over the Domain views. Oracles: the direct amplitude sums
// (SlitCascadeCalculator.Amplitude, TalbotCarpetCalculator.Field — not the render
// loops' recurrences) give each pixel's phase band and modulus octave; a pixel must
// be drawn as a line exactly where its band differs from its right or lower
// neighbour. Round a vortex, the phase winding of the direct sum (the charge q)
// fixes how many lines a closed path must cross: n·|q|.
public sealed class DomainContoursTests
{
    private static FractalParameters Vortex(DomainContourMode mode, int n = 8, int depth = 3, double lambda = 0.6, double width = 0.2) => new()
    {
        SlitCascadeView = SlitCascadeView.Domain, SlitCascadeChildren = 5, SlitCascadeDepth = depth, SlitCascadeCharge = 1,
        SlitCascadeWavelength = lambda, SlitCascadeWidth = width,
        SlitCascadeDomainContours = mode, SlitCascadeContourCount = n,
    };

    private static uint[] RenderSlit(FractalParameters p, int w, int h, double zoom = 1.0)
    {
        var calc = new SlitCascadeCalculator(w, h) { Zoom = zoom, MaxIterations = 400, ColorMap = new HsvPalette(), FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        return (uint[])calc.ColorBuffer.Clone();
    }

    private static uint[] RenderTalbot(FractalParameters p, int w, int h, double cy = 0.5)
    {
        var calc = new TalbotCarpetCalculator(w, h) { CenterY = cy, Zoom = 1.9, MaxIterations = 400, ColorMap = new HsvPalette(), FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        return (uint[])calc.ColorBuffer.Clone();
    }

    private static float L(uint argb) => PerceptualRamp.RgbToOkLab((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb).L;

    // Expected line mask from per-pixel bands (null = no value: never a line, never compared).
    private static bool[] ExpectedLines(int?[] band, int w, int h)
    {
        var m = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (band[i] is not int b) continue;
                m[i] = (x + 1 < w && band[i + 1] is int r && r != b) || (y + 1 < h && band[i + w] is int d && d != b);
            }
        return m;
    }

    // Compare rendered lines (pixel changed from the Off render, and darker) with the
    // oracle's, over pixels bright enough for 8-bit colour to show the darkening.
    private static void AssertLinesMatch(uint[] off, uint[] on, bool[] expected, double[] k, string what)
    {
        int lines = 0, mismatches = 0, checkedPx = 0;
        for (int i = 0; i < off.Length; i++)
        {
            if (on[i] != off[i]) Assert.True(L(on[i]) < L(off[i]), $"{what}: pixel {i} changed but is not darker");
            if (k[i] < 0.25) continue;
            checkedPx++;
            bool got = on[i] != off[i];
            if (got) lines++;
            if (got != expected[i]) mismatches++;
        }
        Assert.True(lines > 50, $"{what}: only {lines} line pixels");
        // Float side buffers vs the double oracle can disagree only on a pixel that
        // sits within rounding of a level.
        Assert.True(mismatches <= Math.Max(3, checkedPx / 500), $"{what}: {mismatches} of {checkedPx} pixels disagree with the oracle ({lines} lines)");
    }

    [Fact]
    public void Off_IsByteIdentical_AndLinesOnlyDarkenPixels()
    {
        var plain = RenderSlit(Vortex(DomainContourMode.Off), 96, 64);
        var offOtherCount = RenderSlit(Vortex(DomainContourMode.Off, n: 3), 96, 64);
        Assert.Equal(plain, offOtherCount);
        // The default parameters are Off.
        var def = new FractalParameters();
        Assert.Equal(DomainContourMode.Off, def.SlitCascadeDomainContours);
        Assert.Equal(DomainContourMode.Off, def.TalbotDomainContours);
        Assert.Equal(DomainContourMode.Off, def.DualOrbitInterferenceContours);
        // Lines never touch the other views.
        var inten = Vortex(DomainContourMode.Both); inten.SlitCascadeView = SlitCascadeView.Intensity;
        var intenOff = Vortex(DomainContourMode.Off); intenOff.SlitCascadeView = SlitCascadeView.Intensity;
        Assert.Equal(RenderSlit(intenOff, 64, 40), RenderSlit(inten, 64, 40));
        var tal = new FractalParameters { TalbotView = TalbotView.Phase, TalbotDomainContours = DomainContourMode.Both };
        Assert.Equal(RenderTalbot(new FractalParameters { TalbotView = TalbotView.Phase }, 64, 36), RenderTalbot(tal, 64, 36));
    }

    [Theory]
    [InlineData(DomainContourMode.Phase)]
    [InlineData(DomainContourMode.Modulus)]
    [InlineData(DomainContourMode.Both)]
    public void SlitCascade_LinesSitAtThePhaseAndModulusLevelsOfTheDirectSum(DomainContourMode mode)
    {
        const int w = 160, h = 108, n = 8;
        var p = Vortex(mode, n);
        var off = RenderSlit(Vortex(DomainContourMode.Off, n), w, h);
        var on = RenderSlit(p, w, h);

        var c = SlitCascadeCalculator.Build(p);
        double sumSqrt = c.P.Sum(Math.Sqrt), logI0 = 2 * c.Depth * Math.Log10(sumSqrt), pitch = 4.0 / w;
        var phaseBand = new int?[w * h]; var modBand = new int?[w * h]; var k = new double[w * h];
        for (int i = 0; i < w * h; i++)
        {
            var a = SlitCascadeCalculator.Amplitude(c, (i % w - w * 0.5) * pitch, (i / w - h * 0.5) * pitch);
            double m2 = a.Real * a.Real + a.Imaginary * a.Imaginary;
            if (m2 <= 0) continue;
            double ki = Math.Clamp(1 + (Math.Log10(m2) - logI0) / p.SlitCascadeDynamicRange, 0, 1);
            k[i] = ki;
            double t = a.Phase / (2 * Math.PI); t -= Math.Floor(t);
            phaseBand[i] = Math.Min(n - 1, (int)Math.Floor(t * n));
            // |A| relative to the peak amplitude Σ√p^D crosses a power of 2.
            if (ki > 0) modBand[i] = (int)Math.Floor(Math.Log2(Math.Sqrt(m2) / Math.Pow(sumSqrt, c.Depth)));
        }
        var exp = new bool[w * h];
        if (DomainContours.DrawsPhase(mode)) exp = ExpectedLines(phaseBand, w, h);
        if (DomainContours.DrawsModulus(mode))
        {
            var m = ExpectedLines(modBand, w, h);
            for (int i = 0; i < exp.Length; i++) exp[i] |= m[i];
        }
        AssertLinesMatch(off, on, exp, k, $"slit {mode}");
    }

    [Theory]
    [InlineData(DomainContourMode.Phase)]
    [InlineData(DomainContourMode.Modulus)]
    public void TalbotCarpet_LinesSitAtThePhaseAndModulusLevelsOfTheDirectSum(DomainContourMode mode)
    {
        const int w = 160, h = 90, n = 6;
        var p = new FractalParameters { TalbotView = TalbotView.Domain, TalbotModes = 24, TalbotDomainContours = mode, TalbotContourCount = n };
        // Keep pixel rows off ζ = 0 and ½ exactly: there the field is real, Im ψ is
        // rounding noise, and the phase flips between 0 and ½ at random (in the oracle
        // as much as in the render).
        double pitch = 4.0 / w / 1.9, cy = 0.5 + 0.37 * pitch;
        var off = RenderTalbot(new FractalParameters { TalbotView = TalbotView.Domain, TalbotModes = 24 }, w, h, cy);
        var on = RenderTalbot(p, w, h, cy);

        var cf = TalbotCarpetCalculator.Coefficients(p);
        double mean = cf.Sum(v => v.Real * v.Real + v.Imaginary * v.Imaginary), sumAbs = cf.Sum(v => v.Magnitude);
        double top = 2 * Math.Log10(sumAbs) - Math.Log10(mean);
        var band = new int?[w * h]; var k = new double[w * h];
        for (int i = 0; i < w * h; i++)
        {
            var f = TalbotCarpetCalculator.Field(cf, (i % w - w * 0.5) * pitch, cy + (i / w - h * 0.5) * pitch);
            double m2 = f.Real * f.Real + f.Imaginary * f.Imaginary;
            if (m2 <= 0) continue;
            double ki = Math.Clamp((Math.Log10(m2) - Math.Log10(mean) + p.TalbotDynamicRange) / (p.TalbotDynamicRange + top), 0, 1);
            k[i] = ki;
            if (mode == DomainContourMode.Phase)
            {
                double t = f.Phase / (2 * Math.PI); t -= Math.Floor(t);
                band[i] = Math.Min(n - 1, (int)Math.Floor(t * n));
            }
            else if (ki > 0)
                band[i] = (int)Math.Floor(Math.Log2(Math.Sqrt(m2 / mean)));   // |ψ| against the rms amplitude
        }
        AssertLinesMatch(off, on, ExpectedLines(band, w, h), k, $"talbot {mode}");
    }

    // A 4-connected closed square path of pixels round (cx, cy) at half-side r.
    private static List<int> SquarePath(int cx, int cy, int r, int w)
    {
        var path = new List<int>();
        for (int x = cx - r; x < cx + r; x++) path.Add((cy - r) * w + x);
        for (int y = cy - r; y < cy + r; y++) path.Add(y * w + cx + r);
        for (int x = cx + r; x > cx - r; x--) path.Add((cy + r) * w + x);
        for (int y = cy + r; y > cy - r; y--) path.Add(y * w + cx - r);
        return path;
    }

    // Equal shares (λ huge, no envelope): the core vortex carries charge D·ℓ (one
    // per level). Round it, n·|q| phase lines must be crossed — counted as runs of
    // line pixels along a closed path, with q measured from the direct sum.
    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 6)]
    [InlineData(3, 4)]
    public void LinesRoundTheVortex_Number_nTimesTheCharge(int depth, int n)
    {
        const int w = 129, h = 129;
        const double zoom = 6.0;
        var p = Vortex(DomainContourMode.Phase, n, depth, lambda: 1e6, width: 0);
        p.SlitCascadeDynamicRange = 30;   // keep the dim core bright enough to show the lines
        var offP = Vortex(DomainContourMode.Off, n, depth, 1e6, 0);
        offP.SlitCascadeDynamicRange = 30;
        var off = RenderSlit(offP, w, h, zoom);
        var on = RenderSlit(p, w, h, zoom);

        var c = SlitCascadeCalculator.Build(p);
        Assert.All(c.P.Where(v => v > 0), v => Assert.Equal(c.P.Max(), v, 9));   // equal shares
        double pitch = 4.0 / w / zoom;
        int cx = w / 2, cy = h / 2;
        // x index i ↔ u = (i − w/2)·pitch, so the core u = 0 sits at pixel w/2 (= 64.5 → between 64 and 65).
        foreach (int r in new[] { 10, 16, 22 })
        {
            var path = SquarePath(cx, cy, r, w);
            double wind = 0, prev = double.NaN;
            foreach (int i in path.Append(path[0]))
            {
                var a = SlitCascadeCalculator.Amplitude(c, (i % w - w * 0.5) * pitch, (i / w - h * 0.5) * pitch);
                double ph = a.Phase;
                if (!double.IsNaN(prev)) { double d = ph - prev; d -= 2 * Math.PI * Math.Round(d / (2 * Math.PI)); wind += d; }
                prev = ph;
            }
            int charge = (int)Math.Round(wind / (2 * Math.PI));
            Assert.Equal(depth, Math.Abs(charge));   // the oracle sees the D·ℓ core (ℓ = 1, N = 5)

            var flags = path.Select(i => on[i] != off[i]).ToArray();
            int runs = 0;
            for (int j = 0; j < flags.Length; j++)
                if (flags[j] && !flags[(j + flags.Length - 1) % flags.Length]) runs++;
            Assert.Equal(n * Math.Abs(charge), runs);
        }
    }

    // The dual-orbit domain field: lines where its own phase / intensity fields
    // (PathInterferencePhase, PathInterference) change band.
    // #1602: with N paths the field is I/N and |A|² = I = N·(I/N).
    [Theory]
    [InlineData(DomainContourMode.Phase, 2)]
    [InlineData(DomainContourMode.Modulus, 2)]
    [InlineData(DomainContourMode.Phase, 4)]
    [InlineData(DomainContourMode.Modulus, 4)]
    [InlineData(DomainContourMode.Modulus, 7)]
    public void JulibrotPair_LinesFollowThePhaseAndIntensityFields(DomainContourMode mode, int paths)
    {
        const int w = 160, h = 108, it = 400, n = 8;
        DualOrbitEscapeCalculator Run(DualOrbitField f, DomainContourMode m)
        {
            var calc = new DualOrbitEscapeCalculator(w, h)
            {
                CenterX = -0.6, CenterY = 0.0, Zoom = 0.6, MaxIterations = it, ColorMap = new HsvPalette(),
                FractalParameters = new FractalParameters
                {
                    DualOrbitField = f, DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitInterferenceK = 8, DualOrbitAngularMomentum = 3,
                    DualOrbitInterferenceContours = m, DualOrbitInterferenceContourCount = n,
                    DualOrbitInterferencePaths = paths,
                },
            };
            calc.Calculate();
            return calc;
        }
        var off = (uint[])Run(DualOrbitField.PathInterferenceDomain, DomainContourMode.Off).ColorBuffer.Clone();
        var on = (uint[])Run(DualOrbitField.PathInterferenceDomain, mode).ColorBuffer.Clone();
        var phase = Run(DualOrbitField.PathInterferencePhase, DomainContourMode.Off).ColorScalarBuffer;
        var inten = Run(DualOrbitField.PathInterference, DomainContourMode.Off).ColorScalarBuffer;
        var band = new int?[w * h]; var k = new double[w * h];
        for (int i = 0; i < w * h; i++)
        {
            if (!(inten[i] > 0)) continue;
            k[i] = inten[i] / (double)it;
            band[i] = mode == DomainContourMode.Phase
                ? DomainContours.PhaseBand(phase[i] / (double)it, n)
                : (int)Math.Floor(0.5 * Math.Log2(paths * inten[i] / it));   // |A| = √I for N unit paths
        }
        AssertLinesMatch(off, on, ExpectedLines(band, w, h), k, $"dual {mode} N={paths}");
    }

    // Colour-blind check (Machado 2009 simulation, as #1591): a line pixel stays
    // clearly darker than the wheel colour it sits on for normal, deutan and protan
    // vision, at every phase.
    [Theory]
    [InlineData(null)]
    [InlineData(CvdType.Deutan)]
    [InlineData(CvdType.Protan)]
    public void LinesStayVisible_UnderColourVisionDeficiency(CvdType? type)
    {
        for (int j = 0; j < 24; j++)
        {
            double t = j / 24.0;
            uint bg = PhaseWheelBlueAmberMap.ColorAt(t, 1.0), line = PhaseWheelBlueAmberMap.ColorAt(t, DomainContours.LineDim);
            (byte r, byte g, byte b) Sim(uint c)
            {
                var rgb = ((byte)(c >> 16), (byte)(c >> 8), (byte)c);
                return type is { } tt ? CvdSimulation.Simulate(rgb.Item1, rgb.Item2, rgb.Item3, tt) : rgb;
            }
            var a = Sim(bg); var l = Sim(line);
            double de = PerceptualRamp.DeltaEOk(a.r, a.g, a.b, l.r, l.g, l.b);
            Assert.True(de >= 0.2, $"{type} phase {t:F3}: line ΔE_OK {de:F3}");
        }
    }

    [Fact]
    public void Settings_RoundTripThroughTheCommandBuilder()
    {
        void Check(FractalType type, FractalParameters p, Action<FractalParameters> assert)
        {
            var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            {
                Fractal = type, Parameters = p, FamilyParams = RegionFractalParams.Snapshot(type, p)!.ToKeyValues(),
            });
            Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
            var fresh = new FractalParameters();
            RegionFractalParams.FromKeyValues(o.Params, type, out _)!.ApplyTo(fresh);
            assert(fresh);
        }
        Check(FractalType.SlitCascade, new FractalParameters { SlitCascadeView = SlitCascadeView.Domain, SlitCascadeDomainContours = DomainContourMode.Both, SlitCascadeContourCount = 12 },
            f => { Assert.Equal(DomainContourMode.Both, f.SlitCascadeDomainContours); Assert.Equal(12, f.SlitCascadeContourCount); });
        Check(FractalType.TalbotCarpet, new FractalParameters { TalbotView = TalbotView.Domain, TalbotDomainContours = DomainContourMode.Modulus, TalbotContourCount = 5 },
            f => { Assert.Equal(DomainContourMode.Modulus, f.TalbotDomainContours); Assert.Equal(5, f.TalbotContourCount); });
        Check(FractalType.JulibrotPair, new FractalParameters { DualOrbitField = DualOrbitField.PathInterferenceDomain, DualOrbitInterferenceContours = DomainContourMode.Phase, DualOrbitInterferenceContourCount = 16 },
            f => { Assert.Equal(DomainContourMode.Phase, f.DualOrbitInterferenceContours); Assert.Equal(16, f.DualOrbitInterferenceContourCount); });
    }
}
