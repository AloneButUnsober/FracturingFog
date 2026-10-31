// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1592 — brightness + phase (domain) colouring for the three wave views.
// Oracle: invert the rendered colour. The Phase Wheel is L = (0.64 + 0.18 cos φ)·k,
// b = 0.13 sin φ·√k in OkLab, so from a pixel's OkLab (L, b) and the brightness k
// computed independently from the amplitude, φ = atan2(b / (0.13√k), (L/k − 0.64) / 0.18)
// must equal arg A of the direct sum — the hue really is the phase, the lightness
// really is the intensity.
public sealed class DomainColouringTests
{
    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF000000u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    // Phase recovered from a rendered colour and a known brightness k (turns, [0, 1)).
    private static double RecoverPhase(uint argb, double k)
    {
        var (L, _, b) = PerceptualRamp.RgbToOkLab((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        double c = (L / k - PhaseWheelBlueAmberMap.LMid) / PhaseWheelBlueAmberMap.LAmp;
        double s = b / (PhaseWheelBlueAmberMap.BAmp * Math.Sqrt(k));
        double t = Math.Atan2(s, c) / (2 * Math.PI);
        return t - Math.Floor(t);
    }

    private static double TurnDiff(double a, double b) { double d = Math.Abs(a - b) % 1.0; return Math.Min(d, 1 - d); }

    private static void AssertHueIsPhase(uint[] colors, Func<int, (double phaseTurns, double k)?> oracle, int n)
    {
        int checkedPx = 0;
        for (int i = 0; i < n; i += 7)
        {
            if (oracle(i) is not { } o || o.k < 0.4) continue;   // 8-bit colour can't carry phase in near-black pixels
            double got = RecoverPhase(colors[i], o.k);
            Assert.True(TurnDiff(got, o.phaseTurns) < 0.03, $"pixel {i}: hue says {got:F3} turns, phase is {o.phaseTurns:F3} (k {o.k:F2})");
            checkedPx++;
        }
        Assert.True(checkedPx > 20, $"only {checkedPx} bright pixels checked");
    }

    private static FractalParameters Vortex(SlitCascadeView view) => new()
    {
        SlitCascadeView = view, SlitCascadeChildren = 5, SlitCascadeDepth = 3, SlitCascadeCharge = 1,
    };

    [Fact]
    public void SlitCascade_HueIsThePhase_BrightnessIsTheIntensity()
    {
        const int w = 96, h = 64, it = 400;
        var p = Vortex(SlitCascadeView.Domain);
        var calc = new SlitCascadeCalculator(w, h) { Zoom = 1.0, MaxIterations = it, ColorMap = new RampMap(), FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        var c = SlitCascadeCalculator.Build(p);
        double sumSqrt = c.P.Sum(Math.Sqrt), logI0 = 2 * c.Depth * Math.Log10(sumSqrt), pitch = 4.0 / w;
        AssertHueIsPhase(calc.ColorBuffer, i =>
        {
            var a = SlitCascadeCalculator.Amplitude(c, (i % w - w * 0.5) * pitch, (i / w - h * 0.5) * pitch);
            double m2 = a.Real * a.Real + a.Imaginary * a.Imaginary;
            if (m2 <= 0) return null;
            double k = Math.Clamp(1 + (Math.Log10(m2) - logI0) / p.SlitCascadeDynamicRange, 0, 1);
            double ph = a.Phase / (2 * Math.PI);
            return (ph - Math.Floor(ph), k);
        }, w * h);
    }

    [Fact]
    public void TalbotCarpet_HueIsThePhase_BrightnessIsTheIntensity()
    {
        const int w = 96, h = 54, it = 400;
        var p = new FractalParameters { TalbotView = TalbotView.Domain, TalbotModes = 24 };
        var calc = new TalbotCarpetCalculator(w, h) { CenterY = 0.5, Zoom = 1.9, MaxIterations = it, ColorMap = new RampMap(), FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        var cf = TalbotCarpetCalculator.Coefficients(p);
        double mean = cf.Sum(v => v.Real * v.Real + v.Imaginary * v.Imaginary), sumAbs = cf.Sum(v => v.Magnitude);
        double top = 2 * Math.Log10(sumAbs) - Math.Log10(mean), pitch = 4.0 / w / 1.9;
        AssertHueIsPhase(calc.ColorBuffer, i =>
        {
            var f = TalbotCarpetCalculator.Field(cf, (i % w - w * 0.5) * pitch, 0.5 + (i / w - h * 0.5) * pitch);
            double m2 = f.Real * f.Real + f.Imaginary * f.Imaginary;
            if (m2 <= 0) return null;
            double k = Math.Clamp((Math.Log10(m2) - Math.Log10(mean) + p.TalbotDynamicRange) / (p.TalbotDynamicRange + top), 0, 1);
            double ph = f.Phase / (2 * Math.PI);
            return (ph - Math.Floor(ph), k);
        }, w * h);
    }

    // Domain ignores the theme, and its height is exactly the Intensity view's
    // (so Relief and SSAO see the same surface in both views).
    [Fact]
    public void Domain_IgnoresTheTheme_AndKeepsTheIntensityHeight()
    {
        SlitCascadeCalculator Slit(SlitCascadeView v, IColorMap cm)
        {
            var calc = new SlitCascadeCalculator(64, 40) { Zoom = 1.0, ColorMap = cm, FractalParameters = Vortex(v) };
            calc.Calculate(CancellationToken.None);
            return calc;
        }
        Assert.Equal(Slit(SlitCascadeView.Domain, new RampMap()).ColorBuffer, Slit(SlitCascadeView.Domain, new HsvPalette()).ColorBuffer);
        Assert.Equal(Slit(SlitCascadeView.Intensity, new RampMap()).SmoothBuffer, Slit(SlitCascadeView.Domain, new RampMap()).SmoothBuffer);

        TalbotCarpetCalculator Carpet(TalbotView v, IColorMap cm)
        {
            var calc = new TalbotCarpetCalculator(64, 36) { CenterY = 0.5, Zoom = 1.9, ColorMap = cm, FractalParameters = new FractalParameters { TalbotView = v } };
            calc.Calculate(CancellationToken.None);
            return calc;
        }
        Assert.Equal(Carpet(TalbotView.Domain, new RampMap()).ColorBuffer, Carpet(TalbotView.Domain, new HsvPalette()).ColorBuffer);
        Assert.Equal(Carpet(TalbotView.Intensity, new RampMap()).SmoothBuffer, Carpet(TalbotView.Domain, new RampMap()).SmoothBuffer);
    }

    // The dual-orbit domain field agrees with the existing phase and intensity
    // fields: hue = the PathInterferencePhase value, brightness = PathInterference.
    [Fact]
    public void JulibrotPair_DomainField_IsPhaseHueOverIntensity()
    {
        const int w = 96, h = 64, it = 400;
        DualOrbitEscapeCalculator Run(DualOrbitField f, double eta = 0)
        {
            var c = new DualOrbitEscapeCalculator(w, h)
            {
                CenterX = -0.6, CenterY = 0.0, Zoom = 0.6, MaxIterations = it, ColorMap = new RampMap(),
                FractalParameters = new FractalParameters { DualOrbitField = f, DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitInterferenceK = 2, DualOrbitAngularMomentum = 3, DualOrbitWhichPath = eta },
            };
            c.Calculate();
            return c;
        }
        var dom = Run(DualOrbitField.PathInterferenceDomain);
        var phase = Run(DualOrbitField.PathInterferencePhase).ColorScalarBuffer;
        var inten = Run(DualOrbitField.PathInterference).ColorScalarBuffer;
        Assert.Equal(inten, dom.ColorScalarBuffer);   // the height is the intensity field, exactly
        AssertHueIsPhase(dom.ColorBuffer, i => inten[i] > 0 ? (phase[i] / it, inten[i] / it) : null, w * h);
        // Which-path η = 1: no fringes, so one brightness everywhere (I/2 = ½); the hue still turns.
        var flat = Run(DualOrbitField.PathInterferenceDomain, 1.0);
        var lightness = flat.ColorScalarBuffer.Where(v => v > 0).Distinct().ToArray();
        Assert.Single(lightness);
        Assert.True(flat.ColorBuffer.Distinct().Count() > 20, "the phase should still sweep the wheel");
    }
}
