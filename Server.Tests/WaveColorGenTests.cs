// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;
using FracturingFog;
using FracturingFog.Cli;
using FracturingFog.ColorGen;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1593 — ColorGen `wave*` inputs for the double-slit renderers. A theme reading
// them becomes an InterpretedWaveColorMap (CPU-only, no C# export, as the billiard
// themes #633). The values are checked against references computed here or from
// the renderers' separate public helpers, not against the code under test:
// the Talbot field from TalbotCarpetCalculator.Field (a direct sum, not the
// render's multiply-up loop), the cascade address against α and f(α) worked out by
// hand for a chosen leaf, and the Pair intensity against the closed form
// I/2 = ½(1 + (1 − η)·cos κΔG) at γ = 0, ℓ = 0, using the theme's own waveDeltaG.
public sealed class WaveColorGenTests
{
    private const double Lsb = 1.0 / 255.0;

    private static InterpretedWaveColorMap Wave(string src)
    {
        var map = InterpretedColorMap.TryCreate(src, null, out var err);
        Assert.Null(err);
        return Assert.IsType<InterpretedWaveColorMap>(map);
    }

    private static (double R, double G, double B) Rgb(uint c)
        => (((c >> 16) & 255) / 255.0, ((c >> 8) & 255) / 255.0, (c & 255) / 255.0);

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF000000u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    // ── Parsing and routing ────────────────────────────────────────────────

    [Theory]
    [InlineData("return rgb(waveIntensity, 0.0, 0.0);", WaveInputs.Field)]
    [InlineData("return hsv(wavePhase, 1.0, waveLevel);", WaveInputs.Field)]
    [InlineData("return rgb(0.5 + 0.5 * cos(waveDeltaG), waveDeltaTheta + 0.5, wavePaths / 12.0);", WaveInputs.Paths)]
    [InlineData("let a = (waveAlpha - waveAlphaMin) / (waveAlphaMax - waveAlphaMin); return rgb(a, waveOnSet, 0.0);", WaveInputs.Address)]
    [InlineData("return hsv(waveLastChild / waveChildren, 1.0, waveIntensity);", WaveInputs.Address | WaveInputs.Field)]
    [InlineData("return rgb(fract(waveXi), fract(waveZeta), 0.0);", WaveInputs.Coords)]
    public void A_Wave_Input_Makes_A_Cpu_Only_Wave_Theme_That_Reads_Only_What_It_Needs(string src, WaveInputs needs)
    {
        var map = Wave(src);
        Assert.Equal(needs, map.Needs);
        Assert.Equal("", map.HlslPaletteBody);           // no GPU palette
        Assert.Equal("Interp_none", map.PaletteId);
    }

    [Fact]
    public void A_Theme_Without_Wave_Inputs_Is_Not_A_Wave_Theme()
    {
        var map = InterpretedColorMap.TryCreate("return hsv(t, 1.0, 1.0);", null, out _);
        Assert.IsNotType<InterpretedWaveColorMap>(map);
        Assert.False(map is IWaveColorMap);
    }

    [Fact]
    public void Wave_Input_Names_Are_Reserved()
    {
        Assert.Null(InterpretedColorMap.TryCreate("let waveLevel = 1.0; return rgb(waveLevel, 0.0, 0.0);", null, out var err));
        Assert.Contains("built-in", err);
    }

    [Fact]
    public void Csharp_Export_Refuses_A_Wave_Theme()
    {
        var r = ColorGenApi.Generate("return hsv(wavePhase, 1.0, 1.0);", "WaveTheme");
        Assert.False(r.Ok);
        Assert.Contains("interpreter only", r.Error);
    }

    [Fact]
    public void On_Other_Fractals_The_Wave_Inputs_Read_Zero()
    {
        var map = Wave("return rgb(waveIntensity + 0.5, wavePhase + 0.25, waveAlpha);");
        var (r, g, b) = Rgb(unchecked((uint)map.Map(10f, 0f, 10)));
        Assert.Equal(0.5, r, Lsb); Assert.Equal(0.25, g, Lsb); Assert.Equal(0.0, b, Lsb);
    }

    // ── Talbot Carpet ──────────────────────────────────────────────────────

    private static TalbotCarpetCalculator Talbot(IColorMap map, TalbotView view, int w = 80, int h = 60, double cx = 0.3, double cy = 0.37, double zoom = 1.3)
    {
        var calc = new TalbotCarpetCalculator(w, h)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = 400, ColorMap = map,
            FractalParameters = new FractalParameters { TalbotView = view },
        };
        calc.Calculate(TestContext.Current.CancellationToken);
        return calc;
    }

    [Fact]
    public void Talbot_Intensity_And_Phase_Match_The_Direct_Field_Sum()
    {
        const int w = 80, h = 60; const double cx = 0.3, cy = 0.37, zoom = 1.3;
        var calc = Talbot(Wave("return rgb(waveIntensity, wavePhase, 0.0);"), TalbotView.Intensity, w, h, cx, cy, zoom);
        var c = TalbotCarpetCalculator.Coefficients(new FractalParameters());
        double bound = Math.Pow(c.Sum(v => v.Magnitude), 2);
        double pitch = 4.0 / w / zoom;
        int checkedPhase = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double xi = cx + (x - w * 0.5) * pitch, zeta = cy + (y - h * 0.5) * pitch;
                Complex psi = TalbotCarpetCalculator.Field(c, xi, zeta);
                var (r, g, _) = Rgb(calc.ColorBuffer[y * w + x]);
                Assert.Equal(Math.Min(1.0, psi.Magnitude * psi.Magnitude / bound), r, 0.6 * Lsb + 1e-9);
                double turns = psi.Phase / (2 * Math.PI); turns -= Math.Floor(turns);
                if (psi.Magnitude < 1e-3 || turns < 2 * Lsb || turns > 1 - 2 * Lsb) continue;   // phase undefined / wraps
                Assert.Equal(turns, g, 0.6 * Lsb + 1e-9);
                checkedPhase++;
            }
        Assert.True(checkedPhase > w * h / 2, $"{checkedPhase} phase pixels");
    }

    [Theory]
    [InlineData(TalbotView.Intensity)]
    [InlineData(TalbotView.Phase)]
    public void Talbot_Level_Is_What_The_View_Shows_And_The_Relief_Height_Is_Unchanged(TalbotView view)
    {
        string src = view == TalbotView.Phase ? "return rgb(wavePhase, 0.0, 0.0);" : "return rgb(waveLevel, 0.0, 0.0);";
        var plain = Talbot(new RampMap(), view);
        var wave = Talbot(Wave(src), view);
        Assert.Equal(plain.SmoothBuffer, wave.SmoothBuffer);   // height field untouched
        for (int i = 0; i < plain.SmoothBuffer.Length; i++)
        {
            double v = plain.SmoothBuffer[i] / 400.0;
            if (view == TalbotView.Phase && (v < 2 * Lsb || v > 1 - 2 * Lsb)) continue;
            Assert.Equal(v, Rgb(wave.ColorBuffer[i]).R, 0.6 * Lsb + 1e-6);
        }
    }

    [Fact]
    public void Talbot_Coordinates_Are_Xi_And_Zeta()
    {
        const int w = 80, h = 60; const double cx = 0.3, cy = 0.37, zoom = 1.3;
        var calc = Talbot(Wave("return rgb(fract(waveXi), fract(waveZeta), 0.0);"), TalbotView.Intensity, w, h, cx, cy, zoom);
        double pitch = 4.0 / w / zoom;
        foreach (var (x, y) in new[] { (0, 0), (17, 41), (79, 59), (40, 30) })
        {
            double xi = cx + (x - w * 0.5) * pitch, zeta = cy + (y - h * 0.5) * pitch;
            var (r, g, _) = Rgb(calc.ColorBuffer[y * w + x]);
            Assert.Equal(xi - Math.Floor(xi), r, 0.6 * Lsb);
            Assert.Equal(zeta - Math.Floor(zeta), g, 0.6 * Lsb);
        }
    }

    [Fact]
    public void Dark_Pixels_Reach_The_Theme_And_Self_Painting_Views_Ignore_It()
    {
        // Every pixel goes to the theme, including those below the dynamic range.
        var white = Talbot(Wave("return rgb(1.0, 1.0, 1.0 + 0.0 * waveLevel);"), TalbotView.Intensity);
        Assert.All(white.ColorBuffer, c => Assert.Equal(0xFFFFFFFFu, c));
        // The Domain view paints its own phase wheel: a wave theme changes nothing.
        var plain = Talbot(new RampMap(), TalbotView.Domain);
        var wave = Talbot(Wave("return rgb(waveLevel, 0.0, 0.0);"), TalbotView.Domain);
        Assert.Equal(plain.ColorBuffer, wave.ColorBuffer);
    }

    // ── Slit Cascade ───────────────────────────────────────────────────────

    [Fact]
    public void Cascade_Address_Gives_First_And_Last_Child_And_Hand_Worked_Alpha()
    {
        var fp = new FractalParameters { SlitCascadeDepth = 2 };
        var c = SlitCascadeCalculator.Build(fp);
        Assert.False(SlitCascadeCalculator.ChildrenOverlap(c));
        int n = c.P.Length;
        Assert.True(n >= 2);
        var counts = new int[n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (c.P[i] <= 0 || c.P[j] <= 0) continue;
                // The centre of child j inside child i.
                double x = c.Bx[i] + c.Ratio * c.Bx[j], y = c.By[i] + c.Ratio * c.By[j];
                var s = SlitCascadeCalculator.WaveAt(c, null, x, y, counts, WaveInputs.Address, 0, 6, -1, -2);
                Assert.Equal(1.0, s.OnSet);
                Assert.Equal(i, s.FirstChild); Assert.Equal(j, s.LastChild);
                Assert.Equal(n, s.Children);
                Assert.Equal((Math.Log(c.P[i]) + Math.Log(c.P[j])) / (2 * Math.Log(c.Ratio)), s.Alpha, 12);
                // f(α): log of the leaves with the same counts — 2 when i ≠ j, 1 when i = j.
                Assert.Equal(i == j ? 0.0 : Math.Log(2) / (2 * Math.Log(1 / c.Ratio)), s.FAlpha, 12);
                Assert.Equal(-1, s.AlphaMin); Assert.Equal(-2, s.AlphaMax);   // passed through
                Assert.Equal(0.0, s.Intensity);   // the far field was not asked for
            }
        var off = SlitCascadeCalculator.WaveAt(c, null, 5.0, 5.0, counts, WaveInputs.Address, 0, 6, 0, 1);
        Assert.Equal(0.0, off.OnSet);
        Assert.Equal(-1, off.FirstChild); Assert.Equal(-1, off.LastChild);
    }

    [Fact]
    public void Cascade_Far_Field_Peaks_At_One_On_Axis_And_Matches_The_Amplitude()
    {
        var fp = new FractalParameters();
        var c = SlitCascadeCalculator.Build(fp);
        double sumSqrt = c.P.Sum(Math.Sqrt);
        double logI0 = 2.0 * c.Depth * Math.Log10(sumSqrt);
        var counts = new int[c.P.Length];
        var axis = SlitCascadeCalculator.WaveAt(c, null, 0, 0, counts, WaveInputs.Field, logI0, 6, 0, 1);
        Assert.Equal(1.0, axis.Intensity, 9);
        Assert.Equal(1.0, axis.Level, 9);
        foreach (var (u, v) in new[] { (0.7, 0.2), (2.3, -1.1), (-0.4, 3.0) })
        {
            var a = SlitCascadeCalculator.Amplitude(c, u, v);
            double i = a.Magnitude * a.Magnitude / Math.Pow(sumSqrt, 2 * c.Depth);
            var s = SlitCascadeCalculator.WaveAt(c, null, u, v, counts, WaveInputs.Field, logI0, 6, 0, 1);
            Assert.Equal(i, s.Intensity, 9);
            Assert.Equal(Math.Clamp(1 + Math.Log10(i) / 6, 0, 1), s.Level, 9);
            double turns = a.Phase / (2 * Math.PI); turns -= Math.Floor(turns);
            Assert.Equal(turns, s.Phase, 9);
            Assert.Equal(0.0, s.OnSet);   // the address was not asked for
        }
    }

    [Fact]
    public void Cascade_Render_Alpha_Agrees_With_The_Aperture_Mass()
    {
        // Aperture view: t = 1 + log10(m)/decades with m = Π (pᵢ/p_max)^countᵢ, so
        // ln m = D·ln r·α − D·ln p_max: α follows from the plain view's height alone.
        const int w = 96, h = 72, maxIt = 400; const double decades = 6.0;
        var fp = new FractalParameters { SlitCascadeView = SlitCascadeView.Aperture, SlitCascadeDynamicRange = decades };
        var c = SlitCascadeCalculator.Build(fp);
        double pMax = c.P.Max();
        SlitCascadeCalculator Run(IColorMap map)
        {
            var k = new SlitCascadeCalculator(w, h) { CenterX = 0.03, CenterY = -0.02, Zoom = 1.0, MaxIterations = maxIt, ColorMap = map, FractalParameters = fp };
            k.Calculate(TestContext.Current.CancellationToken);
            return k;
        }
        var plain = Run(new RampMap());
        // α scaled into a channel over a range wide enough for every leaf.
        var wave = Run(Wave("return rgb(waveOnSet, (waveAlpha - 0.0) / 4.0, 0.0);"));
        Assert.Equal(plain.SmoothBuffer, wave.SmoothBuffer);
        int on = 0;
        for (int i = 0; i < w * h; i++)
        {
            var (r, g, _) = Rgb(wave.ColorBuffer[i]);
            double t = plain.SmoothBuffer[i] / maxIt;
            if (t <= 0) continue;   // off the set, or below the dynamic range
            Assert.Equal(1.0, r);   // a lit aperture pixel lies on the set
            double lnM = (t - 1) * decades * Math.Log(10);
            double alpha = (lnM + c.Depth * Math.Log(pMax)) / (c.Depth * Math.Log(c.Ratio));
            Assert.Equal(alpha / 4.0, g, 0.6 * Lsb + 1e-4);
            on++;
        }
        Assert.True(on > 100, $"{on} pixels on the set");
    }

    // ── Julibrot Pair ──────────────────────────────────────────────────────

    private static DualOrbitEscapeCalculator Pair(IColorMap map, Action<FractalParameters>? tweak = null, int w = 96, int h = 64)
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.PathInterference,
            DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitInterferenceK = 20.0,
        };
        tweak?.Invoke(p);
        var c = new DualOrbitEscapeCalculator(w, h) { CenterX = -0.6, CenterY = 0.05, Zoom = 1.0, MaxIterations = 400, FractalParameters = p, ColorMap = map };
        c.Calculate();
        return c;
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    public void Pair_Intensity_Is_The_Two_Path_Closed_Form_Of_Its_Delta_G(double eta)
    {
        // γ = 0, ℓ = 0, N = 2: I/2 = ½(1 + (1 − η)·cos κΔG). Blue = 1 marks a live pixel.
        string k = (1 - eta).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var calc = Pair(Wave($"return rgb(waveIntensity, 0.5 + 0.5 * {k} * cos(waveDeltaG), 1.0);"),
            p => p.DualOrbitWhichPath = eta);
        int live = 0;
        foreach (uint col in calc.ColorBuffer)
        {
            var (r, g, b) = Rgb(col);
            if (b < 1) continue;
            Assert.Equal(g, r, 1.01 * Lsb);
            live++;
        }
        Assert.True(live > 500, $"{live} live pixels");
    }

    [Fact]
    public void Pair_Phase_And_Level_Match_The_Plain_Fields()
    {
        var phasePlain = Pair(new RampMap(), p => p.DualOrbitField = DualOrbitField.PathInterferencePhase);
        var phaseWave = Pair(Wave("return rgb(wavePhase, waveLevel, 1.0);"), p => p.DualOrbitField = DualOrbitField.PathInterferencePhase);
        var intenPlain = Pair(new RampMap());
        Assert.Equal(phasePlain.ColorScalarBuffer, phaseWave.ColorScalarBuffer);   // height field untouched
        int n = 0;
        for (int i = 0; i < phaseWave.ColorBuffer.Length; i++)
        {
            var (r, g, b) = Rgb(phaseWave.ColorBuffer[i]);
            if (b < 1) continue;
            double ph = phasePlain.ColorScalarBuffer[i] / 400.0, lv = intenPlain.ColorScalarBuffer[i] / 400.0;
            Assert.Equal(lv, g, 0.6 * Lsb + 1e-6);
            if (ph < 2 * Lsb || ph > 1 - 2 * Lsb) continue;
            Assert.Equal(ph, r, 0.6 * Lsb + 1e-6);
            n++;
        }
        Assert.True(n > 500, $"{n} pixels");
    }

    [Theory]
    [InlineData(DualOrbitPathLayout.Grating)]
    [InlineData(DualOrbitPathLayout.Ray)]
    public void Pair_Settings_Reach_The_Theme_And_N_Paths_Match_The_Plain_Field(DualOrbitPathLayout layout)
    {
        void Tweak(FractalParameters p)
        {
            p.DualOrbitInterferencePaths = 6; p.DualOrbitPathLayout = layout;
            p.DualOrbitWhichPath = 0.4; p.DualOrbitAngularMomentum = 3;
        }
        var settings = Pair(Wave("return rgb(wavePaths / 12.0, waveWhichPath, waveAngularMomentum / 10.0 + 0.0 * waveIntensity);"), Tweak);
        var plain = Pair(new RampMap(), Tweak);
        var level = Pair(Wave("return rgb(waveIntensity, waveDeltaTheta + 0.5, 1.0);"), Tweak);
        // Which-path law, independent of the shared field code: I_η = 1 + (1 − η)(I₀ − 1), I = N·waveIntensity.
        var coherent = Pair(Wave("return rgb(waveIntensity, 0.0, 1.0);"), p => { Tweak(p); p.DualOrbitWhichPath = 0.0; });
        int n = 0;
        for (int i = 0; i < plain.ColorBuffer.Length; i++)
        {
            var (r, g, b) = Rgb(level.ColorBuffer[i]);
            if (b < 1) continue;
            Assert.Equal(plain.ColorScalarBuffer[i] / 400.0, r, 0.6 * Lsb + 1e-6);
            Assert.InRange(g, 0.0, 1.0);
            double i0 = 6.0 * Rgb(coherent.ColorBuffer[i]).R;
            Assert.Equal((1.0 + 0.6 * (i0 - 1.0)) / 6.0, r, 1.2 * Lsb);
            var (sr, sg, sb) = Rgb(settings.ColorBuffer[i]);
            Assert.Equal(0.5, sr, 0.6 * Lsb); Assert.Equal(0.4, sg, 0.6 * Lsb); Assert.Equal(0.3, sb, 0.6 * Lsb);
            n++;
        }
        Assert.True(n > 500, $"{n} live pixels");
    }

    // ── Command builder (#1684) ────────────────────────────────────────────

    [Fact]
    public void Command_Builder_Reports_A_Session_ColorGen_Theme_As_A_Batch_Gap()
    {
        const string name = "Wave test theme 1593";
        SessionColorGenThemes.Add(name);
        Assert.True(SessionColorGenThemes.Contains(name));
        Assert.False(SessionColorGenThemes.Contains("HSV"));
        var p = new FractalParameters();
        var gaps = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { ThemeName = name, ThemeIsColorGen = true, Parameters = p });
        Assert.Contains(gaps, g => g.Contains(name) && g.Contains("#1684"));
        var none = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { ThemeName = "Fire", Parameters = p });
        Assert.DoesNotContain(none, g => g.Contains("#1684"));
    }
}
