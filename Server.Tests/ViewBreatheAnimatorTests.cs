// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Xunit;
using FracturingFog.Audio;
using FracturingFog.Abstractions.Animation;
using FracturingFog.ViewState;

namespace FracturingFog.Server.Tests;

// #264 / Audio-Reactive Phase 5 — view-breathe animator (zoom-pulse + shake).
public class ViewBreatheAnimatorTests
{
    private const double Eps = 1e-9;

    private sealed class FakeModSource : IAudioModulationSource
    {
        public bool IsActive { get; set; } = true;
        public long BeatCount { get; set; }
        public long DownbeatCount { get; set; }
        public AudioModulationFrame Frame { get; set; }
        public AudioModulationFrame Sample() => Frame;
        public AudioModulationFrame SampleAt(double seconds) => Frame;
    }

    // Field order: bass, lowMid, mid, highMid, high, rms, beatPulse,
    // downbeatPulse, bpmPhaseSaw, bpmPhaseSine, transient, bpm, isActive.
    private static AudioModulationFrame BassFrame(float bass, bool active = true) =>
        new(bass, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, active);

    private static AudioModulationFrame HighFrame(float high, bool active = true) =>
        new(0, 0, 0, 0, high, 0, 0, 0, 0, 0, false, 0, active);

    [Fact]
    public void ZoomPulse_Scales_Factor_By_Depth()
    {
        var view = new FractalViewState();               // Zoom 0.13 (shallow)
        var src = new FakeModSource { Frame = BassFrame(1f) };
        var anim = new ViewBreatheAnimator(src, view);   // depth 0.06, bass, smoothstep

        anim.Tick(0.05);
        Assert.Equal(1.06, view.BreatheZoomFactor, Eps); // smoothstep(1)=1 -> 1+0.06
    }

    [Fact]
    public void ZoomPulse_Identity_At_Zero_Signal()
    {
        var view = new FractalViewState();
        var src = new FakeModSource { Frame = BassFrame(0f) };
        var anim = new ViewBreatheAnimator(src, view);

        anim.Tick(0.05);
        Assert.Equal(1.0, view.BreatheZoomFactor, Eps);
    }

    [Fact]
    public void Depth_Setter_Retunes_Range_And_Clamps()
    {
        var view = new FractalViewState();
        var src = new FakeModSource { Frame = BassFrame(1f) };
        var anim = new ViewBreatheAnimator(src, view) { ZoomDepth = 0.2 };

        anim.Tick(0.05);
        Assert.Equal(1.2, view.BreatheZoomFactor, Eps);

        anim.ZoomDepth = 5.0;                            // clamps to 0.5
        Assert.Equal(0.5, anim.ZoomDepth, Eps);
    }

    [Fact]
    public void Inactive_Source_Resets_To_Identity()
    {
        var view = new FractalViewState { BreatheZoomFactor = 1.5, BreatheOffsetXFrac = 0.1 };
        var src = new FakeModSource { IsActive = false, Frame = BassFrame(1f, active: false) };
        var anim = new ViewBreatheAnimator(src, view);

        anim.Tick(0.05);
        Assert.Equal(1.0, view.BreatheZoomFactor, Eps);
        Assert.Equal(0.0, view.BreatheOffsetXFrac, Eps);
        Assert.Equal(0.0, view.BreatheOffsetYFrac, Eps);
    }

    [Fact]
    public void Deep_Zoom_Suppresses_Breathe()
    {
        var view = new FractalViewState { Zoom = 1e9 };  // past MaxZoom (1e6)
        var src = new FakeModSource { Frame = BassFrame(1f) };
        var anim = new ViewBreatheAnimator(src, view);

        anim.Tick(0.05);
        Assert.Equal(1.0, view.BreatheZoomFactor, Eps);  // identity — no wobble deep
    }

    [Fact]
    public void ZoomPulse_Disabled_Leaves_Factor_Identity()
    {
        var view = new FractalViewState();
        var src = new FakeModSource { Frame = BassFrame(1f) };
        var anim = new ViewBreatheAnimator(src, view) { ZoomPulseEnabled = false };

        anim.Tick(0.05);
        Assert.Equal(1.0, view.BreatheZoomFactor, Eps);
    }

    [Fact]
    public void Shake_Produces_Bounded_Offset()
    {
        var view = new FractalViewState();
        var src = new FakeModSource { Frame = HighFrame(1f) };
        var anim = new ViewBreatheAnimator(src, view)
        {
            ZoomPulseEnabled = false,
            ShakeEnabled = true,
            ShakeSignal = AudioSignalKind.High,
            ShakeAmount = 0.05,
        };

        anim.Tick(0.05);
        double r = System.Math.Sqrt(
            view.BreatheOffsetXFrac * view.BreatheOffsetXFrac +
            view.BreatheOffsetYFrac * view.BreatheOffsetYFrac);
        Assert.True(r > 0.0, "shake should displace the view");
        // Lissajous jitter (independent x/y phases) — bounded by amount*sqrt(2).
        Assert.True(r <= 0.05 * System.Math.Sqrt(2.0) + Eps, "shake magnitude bounded");
    }

    [Fact]
    public void Shake_Disabled_Zero_Offset()
    {
        var view = new FractalViewState();
        var src = new FakeModSource { Frame = HighFrame(1f) };
        var anim = new ViewBreatheAnimator(src, view) { ShakeEnabled = false };

        anim.Tick(0.05);
        Assert.Equal(0.0, view.BreatheOffsetXFrac, Eps);
        Assert.Equal(0.0, view.BreatheOffsetYFrac, Eps);
    }
}
