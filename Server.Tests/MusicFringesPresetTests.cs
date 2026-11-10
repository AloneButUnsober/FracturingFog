// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Audio;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1604 — music-driven double-slit presets: built-in regions carrying audio→param
// bindings (#268). Each binding is driven here through the same factory the live
// AudioModulationManager uses (AudioModulatorAnimator.TryCreate, with the target's
// animatable range) from a fake audio source, and the render must then do what the
// preset promises: the fringes vanish on the beat, the spiral-arm count follows the
// pulse, the wavelength (and with it the measured dimension) follows the bass. Batch
// does not reproduce audio bindings yet (#1432): the Command builder must say so.
public sealed class MusicFringesPresetTests
{
    private const string BeatCollapse = "Double Slit - Beat Collapse";
    private const string SpiralBeat = "Double Slit - Spiral Beat";
    private const string Breathing = "Slit Cascade - Music Breathing";

    private sealed class FakeSource : IAudioModulationSource
    {
        public bool IsActive { get; set; } = true;
        public long BeatCount { get; set; }
        public long DownbeatCount { get; set; }
        public AudioModulationFrame Frame { get; set; }
        public AudioModulationFrame Sample() => Frame;
        public AudioModulationFrame SampleAt(double seconds) => Frame;
    }

    private static AudioModulationFrame Frame(float bass = 0, float beat = 0)
        => new(bass, 0, 0, 0, 0, 0, beat, 0, 0, 0, false, 120, true);

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF000000u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static (FractalRegion Region, FractalParameters P, AudioParamBinding B) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        var b = Assert.Single(region.AudioBindings!);
        return (region, p, b);
    }

    // Drive the preset's binding to its steady state for a constant frame.
    private static void Drive(FractalRegion region, FractalParameters p, AudioParamBinding b, AudioModulationFrame f)
    {
        var d = FractalAnimatableParamsMap.For(region.FractalType).Single(x => x.ParamName == b.ParamName);
        var anim = AudioModulatorAnimator.TryCreate(p, b.ParamName, new FakeSource { Frame = f }, b.Binding, d.Cost, d.Min, d.Max);
        Assert.NotNull(anim);
        anim!.IsEnabled = true;
        for (int i = 0; i < 20; i++) anim.Tick(0.5);   // well past attack / release
    }

    private static T Render<T>(T calc, FractalRegion region) where T : IFractalCalculator
    {
        calc.CenterX = region.CenterX; calc.CenterY = region.CenterY; calc.Zoom = region.Zoom;
        calc.MaxIterations = region.Iterations; calc.ColorMap = new RampMap();
        calc.Calculate(TestContext.Current.CancellationToken);
        return calc;
    }

    [Theory]
    [InlineData(BeatCollapse)]
    [InlineData(SpiralBeat)]
    [InlineData(Breathing)]
    public void Binding_Targets_An_Animatable_Setting_Within_Its_Range(string name)
    {
        var (region, _, b) = Preset(name);
        Assert.True(b.Enabled);
        Assert.Equal(AudioBindingMode.Position, b.Binding.Mode);
        var d = FractalAnimatableParamsMap.For(region.FractalType).SingleOrDefault(x => x.ParamName == b.ParamName);
        Assert.NotNull(d);
        Assert.NotEqual(AnimatableParamKind.Complex, d!.Kind);
        Assert.InRange(b.Binding.OutMin, d.Min, d.Max);
        Assert.InRange(b.Binding.OutMax, d.Min, d.Max);
        Assert.Null(region.AnimationName);   // no timeline animation fighting the music
        Assert.Contains("audio", region.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Beat_Collapse_Flattens_The_Fringes_On_The_Beat()
    {
        var (region, p, b) = Preset(BeatCollapse);
        Assert.Equal(nameof(FractalParameters.DualOrbitWhichPath), b.ParamName);
        Assert.Equal(AudioSignalKind.BeatPulse, b.Binding.Source);
        float[] Dual() => (float[])Render(new DualOrbitEscapeCalculator(96, 64) { FractalParameters = p }, region).ColorScalarBuffer.Clone();

        Drive(region, p, b, Frame(beat: 1f));
        Assert.Equal(1.0, p.DualOrbitWhichPath, 9);
        Assert.Single(Dual().Where(v => v > 0).Distinct());             // I ≡ 1: flat
        Drive(region, p, b, Frame(beat: 0f));
        Assert.True(p.DualOrbitWhichPath < 1e-3, $"η {p.DualOrbitWhichPath}");
        Assert.True(Dual().Where(v => v > 0).Distinct().Count() > 50);   // fringes back
    }

    // Arms = how many times the phase winds round the frame (as the Spirals preset test).
    private static double Arms(FractalRegion region, FractalParameters p)
    {
        const int w = 160, h = 120;
        var s = Render(new DualOrbitEscapeCalculator(w, h) { FractalParameters = p }, region).ColorScalarBuffer;
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
        return total;
    }

    [Theory]
    [InlineData(0f, 2)]
    [InlineData(0.34f, 3)]
    [InlineData(0.67f, 4)]
    [InlineData(1f, 5)]
    public void Spiral_Beat_Steps_The_Arm_Count_With_The_Pulse(float pulse, int arms)
    {
        var (region, p, b) = Preset(SpiralBeat);
        Assert.Equal(nameof(FractalParameters.DualOrbitAngularMomentum), b.ParamName);
        Drive(region, p, b, Frame(beat: pulse));
        Assert.Equal(arms, p.DualOrbitAngularMomentum);
        Assert.Equal(arms, Arms(region, p), 0.05);
    }

    [Fact]
    public void Music_Breathing_Moves_The_Wavelength_And_The_Dimension_With_The_Bass()
    {
        var (region, p, b) = Preset(Breathing);
        Assert.Equal(nameof(FractalParameters.SlitCascadeWavelength), b.ParamName);
        Assert.Equal(AudioSignalKind.Bass, b.Binding.Source);
        Assert.True(b.Binding.ReleaseSeconds > 0, "smoothed, so it breathes rather than flickers");
        string Label() => Render(new SlitCascadeCalculator(64, 48) { FractalParameters = p }, region).LastPrecisionLabel!;

        Drive(region, p, b, Frame(bass: 0f));
        Assert.Equal(0.4, p.SlitCascadeWavelength, 9);
        string quiet = Label();
        Drive(region, p, b, Frame(bass: 1f));
        Assert.Equal(1.4, p.SlitCascadeWavelength, 9);
        string loud = Label();
        Drive(region, p, b, Frame(bass: 0.5f));
        Assert.Equal(0.9, p.SlitCascadeWavelength, 9);                 // smoothstep(½) = ½
        Assert.NotEqual(quiet, loud);                                    // the status-bar dimension moves
        var c04 = SlitCascadeCalculator.Build(new FractalParameters { SlitCascadeWavelength = 0.4 });
        var c14 = SlitCascadeCalculator.Build(new FractalParameters { SlitCascadeWavelength = 1.4 });
        Assert.NotEqual(SlitCascadeCalculator.GeneralizedDimension(c04, 1), SlitCascadeCalculator.GeneralizedDimension(c14, 1));
    }

    [Theory]
    [InlineData(BeatCollapse)]
    [InlineData(SpiralBeat)]
    [InlineData(Breathing)]
    public void Without_Audio_The_Preset_Keeps_Its_Own_Settings(string name)
    {
        var (region, p, b) = Preset(name);
        var before = p.Clone();
        var d = FractalAnimatableParamsMap.For(region.FractalType).Single(x => x.ParamName == b.ParamName);
        var anim = AudioModulatorAnimator.TryCreate(p, b.ParamName, new FakeSource { IsActive = false, Frame = AudioModulationFrame.Inactive },
            b.Binding, d.Cost, d.Min, d.Max)!;
        anim.IsEnabled = true;
        anim.Tick(0.5);
        var prop = typeof(FractalParameters).GetProperty(b.ParamName)!;
        Assert.Equal(prop.GetValue(before), prop.GetValue(p));
    }

    [Theory]
    [InlineData(BeatCollapse)]
    [InlineData(SpiralBeat)]
    [InlineData(Breathing)]
    public void Command_Builder_Reports_The_Audio_Drive_As_A_Batch_Gap(string name)
    {
        var (region, p, b) = Preset(name);
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = region.FractalType, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(region.FractalType, p)!.ToKeyValues(),
            AudioDrivenParams = region.AudioBindings!.Where(x => x.Enabled).Select(x => x.ParamName).ToArray(),
        });
        Assert.Contains(report.Gaps, g => g.Contains(b.ParamName) && g.Contains("#1432"));
    }
}
