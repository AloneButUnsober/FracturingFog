// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Numerics;
using Xunit;
using FracturingFog.Audio;
using FracturingFog.Abstractions.Animation;

namespace FracturingFog.Server.Tests;

// #263 / Audio-Reactive Phase 4a — the audio->param animator + reflection factory.
public class AudioModulatorAnimatorTests
{
    private const double Eps = 1e-9;

    // Fake pull source with a settable frame + activity flag.
    private sealed class FakeModSource : IAudioModulationSource
    {
        public bool IsActive { get; set; } = true;
        public long BeatCount { get; set; }
        public long DownbeatCount { get; set; }
        public AudioModulationFrame Frame { get; set; }
        public AudioModulationFrame Sample() => Frame;
        public AudioModulationFrame SampleAt(double seconds) => Frame;
    }

    private sealed class Target
    {
        public double Scalar { get; set; }
        public int Count { get; set; }
        public Complex C { get; set; }
        public double ReadOnly { get; } = 1.0;
    }

    private static AudioModulationFrame RmsFrame(float rms, bool active = true) =>
        new(0, 0, 0, 0, 0, rms, 0, 0, 0, 0, false, 0, active);

    [Fact]
    public void Tick_Writes_Evaluated_Value_To_Double_Param()
    {
        var target = new Target();
        var src = new FakeModSource { Frame = RmsFrame(0.5f) };
        var binding = new AudioModulationBinding { Source = AudioSignalKind.Rms, OutMin = 0, OutMax = 10 };
        var anim = AudioModulatorAnimator.TryCreate(target, nameof(Target.Scalar), src, binding);

        Assert.NotNull(anim);
        anim!.Tick(0.05);
        Assert.Equal(5.0, target.Scalar, Eps);   // 0.5 -> [0,10]
    }

    [Fact]
    public void Tick_Is_NoOp_When_Source_Inactive()
    {
        var target = new Target { Scalar = 42.0 };
        var src = new FakeModSource { IsActive = false, Frame = RmsFrame(1f, active: false) };
        var binding = new AudioModulationBinding { Source = AudioSignalKind.Rms, OutMin = 0, OutMax = 10 };
        var anim = AudioModulatorAnimator.TryCreate(target, nameof(Target.Scalar), src, binding);

        anim!.Tick(0.05);
        Assert.Equal(42.0, target.Scalar, Eps);  // untouched
    }

    [Fact]
    public void Int_Param_Rounds()
    {
        var target = new Target();
        var src = new FakeModSource { Frame = RmsFrame(0.5f) };
        // 0.5 -> [0,7] = 3.5 -> rounds to 4.
        var binding = new AudioModulationBinding { Source = AudioSignalKind.Rms, OutMin = 0, OutMax = 7 };
        var anim = AudioModulatorAnimator.TryCreate(target, nameof(Target.Count), src, binding);

        anim!.Tick(0.05);
        Assert.Equal(4, target.Count);
    }

    [Fact]
    public void TryCreate_Returns_Null_For_Unsupported_Or_Missing()
    {
        var target = new Target();
        var src = new FakeModSource();
        var binding = new AudioModulationBinding();

        Assert.Null(AudioModulatorAnimator.TryCreate(target, "Nope", src, binding));         // missing
        Assert.Null(AudioModulatorAnimator.TryCreate(target, nameof(Target.ReadOnly), src, binding)); // read-only
        Assert.Null(AudioModulatorAnimator.TryCreate(target, nameof(Target.C), src, binding));        // Complex (out of scope)
    }

    [Fact]
    public void Cost_And_Binding_Are_Exposed()
    {
        var target = new Target();
        var src = new FakeModSource();
        var binding = new AudioModulationBinding();
        var anim = AudioModulatorAnimator.TryCreate(
            target, nameof(Target.Scalar), src, binding, AnimatableParamCost.Expensive);

        Assert.Equal(AnimatableParamCost.Expensive, anim!.Cost);
        Assert.Same(binding, anim.Binding);
    }
}
