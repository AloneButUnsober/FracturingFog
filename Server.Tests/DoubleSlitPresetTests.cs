// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Linq;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Help;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1573 (double-slit epic #1565 S7) — the presets. Each is checked against what its
// description promises, by an invariant that does not trust the preset's own numbers
// (a phase winding, a dark core, a flat field), then its bound animation must exist,
// target the type and drive a setting the type can animate. The guide and the help
// text must name every preset.
public sealed class DoubleSlitPresetTests
{
    private const string WhichPath = "Double Slit - Which-Path Collapse";
    private const string Spirals = "Double Slit - Angular Momentum Spirals";
    private const string Rays = "Double Slit - Opposite Winding Rays";
    private const string Ring = "Slit Cascade - Ring Diffraction";
    private const string Dimension = "Slit Cascade - Probability Sets Dimension";
    private const string Vortex = "Slit Cascade - Vortex Core";
    private const string Ronchi = "Talbot Carpet - Ronchi Grating";
    private const string Comb = "Talbot Carpet - Fractal Comb";
    private const string CascadeGrating = "Talbot Carpet - Cascade Grating";

    private static readonly string[] All = { WhichPath, Spirals, Rays, Ring, Dimension, Vortex, Ronchi, Comb, CascadeGrating };
    private static readonly string[] Animations =
        { "Double slit: which-path collapse", "Double slit: angular momentum sweep", "Slit cascade: wavelength breathing" };

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF000000u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(RegionType.BuiltIn, region.RegionType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static T Render<T>(T calc, FractalRegion region) where T : IFractalCalculator
    {
        calc.CenterX = region.CenterX; calc.CenterY = region.CenterY; calc.Zoom = region.Zoom;
        calc.MaxIterations = region.Iterations; calc.ColorMap = new RampMap();
        calc.Calculate(TestContext.Current.CancellationToken);
        return calc;
    }

    private static float[] RenderDual(FractalRegion region, FractalParameters p, int w = 96, int h = 64)
        => (float[])Render(new DualOrbitEscapeCalculator(w, h) { FractalParameters = p }, region).ColorScalarBuffer.Clone();

    [Fact]
    public void Every_Preset_Renders_Something()
    {
        foreach (var name in All)
        {
            var (region, p) = Preset(name);
            float[] s = region.FractalType switch
            {
                FractalType.JulibrotPair => RenderDual(region, p),
                // Aperture views need pixels finer than a leaf (r^D); 320 wide is about 1 leaf per pixel.
                FractalType.SlitCascade => Render(new SlitCascadeCalculator(320, 180) { FractalParameters = p }, region).SmoothBuffer,
                FractalType.TalbotCarpet => Render(new TalbotCarpetCalculator(96, 54) { FractalParameters = p }, region).SmoothBuffer,
                _ => throw new InvalidOperationException($"{name}: unexpected type {region.FractalType}"),
            };
            int classes = s.Where(v => v > 0).Distinct().Count();
            if (p.SlitCascadeView == SlitCascadeView.HolderAlpha && region.FractalType == FractalType.SlitCascade)
            {
                // The ring's six shares take two values (x = ±0.7 and ±0.35), so α depends only
                // on how many levels chose a bright child: exactly Depth + 1 classes.
                Assert.Equal(2, SlitCascadeCalculator.Build(p).P.Select(q => Math.Round(q, 12)).Distinct().Count());
                Assert.Equal(p.SlitCascadeDepth + 1, classes);
            }
            else Assert.True(classes > 10, $"{name} renders flat");
        }
    }

    [Fact]
    public void WhichPath_Collapse_Fringes_Vanish_When_Watched()
    {
        var (region, p) = Preset(WhichPath);
        Assert.Equal(DualOrbitField.PathInterference, p.DualOrbitField);
        Assert.True(RenderDual(region, p).Where(v => v > 0).Distinct().Count() > 50);
        p.DualOrbitWhichPath = 1.0;
        Assert.Single(RenderDual(region, p).Where(v => v > 0).Distinct());   // I ≡ 1: one flat value
    }

    // The phase winds ℓ = 3 times round the frame: three spiral arms.
    [Fact]
    public void Spirals_Have_Three_Arms()
    {
        var (region, p) = Preset(Spirals);
        Assert.Equal(DualOrbitField.PathInterferencePhase, p.DualOrbitField);
        const int w = 160, h = 120;
        var s = RenderDual(region, p, w, h);
        var loop = Enumerable.Range(2, w - 4).Select(x => 2 * w + x)
            .Concat(Enumerable.Range(2, h - 4).Select(y => y * w + w - 3))
            .Concat(Enumerable.Range(2, w - 4).Reverse().Select(x => (h - 3) * w + x))
            .Concat(Enumerable.Range(2, h - 4).Reverse().Select(y => y * w + 2)).ToArray();
        double total = 0;
        for (int k = 0; k < loop.Length; k++)
        {
            double d = (s[loop[(k + 1) % loop.Length]] - s[loop[k]]) / region.Iterations;
            total += d - Math.Round(d);
        }
        Assert.Equal(3, total, 0.05);
    }

    [Fact]
    public void Rays_Use_Opposite_Winding()
    {
        var (_, p) = Preset(Rays);
        Assert.Equal(DualOrbitWindingMode.Opposite, p.DualOrbitWindingMode);
        Assert.NotEqual(0, p.DualOrbitAngularMomentum);
        Assert.Equal(DualOrbitField.PathInterference, p.DualOrbitField);
    }

    [Fact]
    public void Ring_Diffraction_Peaks_At_The_Centre()
    {
        var (region, p) = Preset(Ring);
        Assert.Equal(SlitCascadeLayout.Ring, p.SlitCascadeLayout);
        var c = Render(new SlitCascadeCalculator(64, 48) { FractalParameters = p }, region);
        Assert.Equal((float)region.Iterations, c.SmoothBuffer[24 * 64 + 32]);   // u = 0 is the brightest point
    }

    // The status bar's D1 is below D0: the shares are unequal, so the measure is multifractal.
    [Fact]
    public void Dimension_Preset_Is_Multifractal()
    {
        var (region, p) = Preset(Dimension);
        Assert.Equal(SlitCascadeView.HolderAlpha, p.SlitCascadeView);
        var calc = Render(new SlitCascadeCalculator(32, 24) { FractalParameters = p }, region);
        var cas = SlitCascadeCalculator.Build(p);
        double d0 = SlitCascadeCalculator.GeneralizedDimension(cas, 0), d1 = SlitCascadeCalculator.GeneralizedDimension(cas, 1);
        Assert.True(d0 - d1 > 0.2, $"D0 {d0} vs D1 {d1}");
        Assert.Contains("D1 ", calc.LastPrecisionLabel);
        Assert.DoesNotContain("overlap", calc.LastPrecisionLabel);
    }

    // Equal shares, charge 1 on 5 slits, 3 levels: a dark core of total charge 3.
    // Rendered with the default theme (HSV), the α classes must be different colours:
    // at 400 iterations they sat on multiples of HSV's 50-iteration turn, all red.
    [Fact]
    public void Dimension_Preset_Classes_Are_Distinct_Colours_In_The_Default_Theme()
    {
        var (region, p) = Preset(Dimension);
        var calc = new SlitCascadeCalculator(320, 180) { FractalParameters = p, ColorMap = new HsvPalette(),
            CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = region.Iterations };
        calc.Calculate(TestContext.Current.CancellationToken);
        int colours = calc.ColorBuffer.Where((c, i) => calc.SmoothBuffer[i] > 0).Distinct().Count();
        Assert.Equal(p.SlitCascadeDepth + 1, colours);
    }

    [Fact]
    public void Vortex_Core_Is_Dark_With_Charge_Three()
    {
        var (_, p) = Preset(Vortex);
        var c = SlitCascadeCalculator.Build(p);
        Assert.True(c.P.Max() - c.P.Min() < 1e-9);
        Assert.True(SlitCascadeCalculator.Amplitude(c, 0, 0).Magnitude < 1e-9);
        double total = 0, prev = SlitCascadeCalculator.Amplitude(c, 0.05, 0).Phase;
        for (int k = 1; k <= 720; k++)
        {
            double a = 2 * Math.PI * k / 720;
            double ph = SlitCascadeCalculator.Amplitude(c, 0.05 * Math.Cos(a), 0.05 * Math.Sin(a)).Phase;
            double d = (ph - prev) / (2 * Math.PI);
            total += d - Math.Round(d); prev = ph;
        }
        Assert.Equal(3, total, 0.02);
    }

    // Talbot presets: one Talbot distance tall at 16 iterations, and the grating
    // images itself at ζ = 1.
    [Theory]
    [InlineData(Ronchi, TalbotProfile.Ronchi)]
    [InlineData(Comb, TalbotProfile.DeltaComb)]
    [InlineData(CascadeGrating, TalbotProfile.SlitCascade)]
    public void Talbot_Presets_Show_One_Talbot_Distance(string name, TalbotProfile profile)
    {
        var (region, p) = Preset(name);
        Assert.Equal(FractalType.TalbotCarpet, region.FractalType);
        Assert.Equal(profile, p.TalbotProfile);
        Assert.Equal(16, region.Iterations);
        Assert.Equal(0.5, region.CenterY);
        var c = TalbotCarpetCalculator.Coefficients(p);
        Assert.Equal(TalbotCarpetCalculator.Field(c, 0.2, 0).Magnitude, TalbotCarpetCalculator.Field(c, 0.2, 1).Magnitude, 9);
    }

    [Fact]
    public void Bound_Animations_Exist_And_Drive_Animatable_Settings()
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        foreach (var name in All)
        {
            var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
            if (region.AnimationName is null) continue;
            var anim = lib.GetByName(region.AnimationName);
            Assert.NotNull(anim);
            Assert.Contains(region.FractalType, anim!.TargetFractalTypes);
            var animatable = FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(region.FractalType).Select(d => d.ParamName).ToHashSet();
            Assert.All(anim.Tracks, t => Assert.Contains(t.ParamName, animatable));
            Assert.NotEmpty(anim.ToAnimators(new FractalParameters()));
        }
        Assert.All(Animations, a => Assert.NotNull(lib.GetByName(a)));
    }

    [Fact]
    public void Guide_And_Help_Name_Every_Preset()
    {
        string guide = File.ReadAllText(Path.Combine(RepoRoot(), "Docs", "User", "DoubleSlit-Guide.md"));
        Assert.All(All, n => Assert.Contains(n, guide));
        Assert.All(Animations, a => Assert.Contains(a, guide));
        string help = HelpTextBundle.MathDoubleSlitText + HelpTextBundle.MathSlitCascadeText + HelpTextBundle.MathTalbotCarpetText;
        Assert.All(All, n => Assert.Contains(n, help));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
