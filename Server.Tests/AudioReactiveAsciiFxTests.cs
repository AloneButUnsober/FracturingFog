// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Xunit;
using FracturingFog.Audio;
using FracturingFog.Imaging;

namespace FracturingFog.Server.Tests;

// #261 / Audio-Reactive Phase 2 — the pure AsciiFx audio mapper.
public class AudioReactiveAsciiFxTests
{
    private const double Eps = 1e-9;

    private static AudioModulationFrame Frame(
        float bass = 0, float rms = 0, float beat = 0, bool transient = false, double bpm = 0,
        bool active = true) =>
        new(bass, 0, 0, 0, 0, rms, beat, 0, 0, 0, transient, bpm, active);

    [Fact]
    public void Inactive_Frame_Is_NoOp()
    {
        var fx = new AsciiFxSettings();
        AudioReactiveAsciiFx.Apply(fx, Frame(bass: 1, rms: 1, bpm: 120, active: false));
        Assert.False(fx.Breathe);
        Assert.False(fx.Bloom);
        Assert.False(fx.HueCycle);
        Assert.False(fx.AnyEnabled);
    }

    [Fact]
    public void Bass_Drives_Breathe_Depth()
    {
        var fx = new AsciiFxSettings();
        AudioReactiveAsciiFx.Apply(fx, Frame(bass: 0f));
        Assert.True(fx.Breathe);
        Assert.Equal(0.15, fx.BreatheGammaAmp, Eps);

        var fx2 = new AsciiFxSettings();
        AudioReactiveAsciiFx.Apply(fx2, Frame(bass: 1f));
        Assert.Equal(0.75, fx2.BreatheGammaAmp, Eps);
    }

    [Fact]
    public void Rms_Drives_Bloom_Strength()
    {
        var fx = new AsciiFxSettings();
        AudioReactiveAsciiFx.Apply(fx, Frame(rms: 1f));
        Assert.True(fx.Bloom);
        Assert.Equal(1.5, fx.BloomStrength, Eps);
    }

    [Fact]
    public void Transient_Fires_Glitch_Otherwise_Off()
    {
        var on = new AsciiFxSettings();
        AudioReactiveAsciiFx.Apply(on, Frame(transient: true));
        Assert.True(on.Glitch);

        var off = new AsciiFxSettings();
        AudioReactiveAsciiFx.Apply(off, Frame(transient: false));
        Assert.False(off.Glitch);
    }

    [Fact]
    public void Bpm_Syncs_Hue_And_RampScroll()
    {
        var fx = new AsciiFxSettings();
        AudioReactiveAsciiFx.Apply(fx, Frame(bpm: 120));
        Assert.True(fx.HueCycle);
        Assert.Equal(180.0, fx.HueCycleDegPerSec, Eps);   // 120 * 1.5
        Assert.True(fx.RampScroll);
        Assert.Equal(4.0, fx.RampScrollSpeed, Eps);        // 120 / 30
    }

    [Fact]
    public void No_Bpm_Leaves_Hue_And_RampScroll_Untouched()
    {
        var fx = new AsciiFxSettings();
        AudioReactiveAsciiFx.Apply(fx, Frame(bpm: 0));
        Assert.False(fx.HueCycle);
        Assert.False(fx.RampScroll);
    }

    [Fact]
    public void Matrix_Rain_Surges_Only_When_Preenabled()
    {
        // Not enabled -> stays off, no surge assignment path.
        var off = new AsciiFxSettings();
        AudioReactiveAsciiFx.Apply(off, Frame(beat: 1f));
        Assert.False(off.MatrixRain);

        // Pre-enabled -> speed scales with beat envelope.
        var on = new AsciiFxSettings { MatrixRain = true };
        AudioReactiveAsciiFx.Apply(on, Frame(beat: 1f));
        Assert.Equal(30.0, on.MatrixRainSpeed, Eps);       // 8 + 22*1
    }

    [Fact]
    public void Null_Settings_Does_Not_Throw()
    {
        AudioReactiveAsciiFx.Apply(null!, Frame(bass: 1f));  // no exception
    }
}
