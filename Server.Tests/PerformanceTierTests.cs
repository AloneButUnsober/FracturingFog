using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using FracturingFog.Render;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Scene Engine Roadmap Phase S2: the hardware-tier profiles. Covers default
/// tier selection from the hardware probe, the monotonic ordering of the three
/// baselines, and the governor-quality fold (Resolve) — identity at full
/// quality, proportional throttle of the continuous knobs, floor clamps, the
/// no-boost-past-baseline clamp, and structural-knob invariance.
/// </summary>
public sealed class PerformanceTierTests
{
    private static HardwareProfile Hw(int cores, bool discrete) => new(cores, discrete);

    // ── DefaultTier ──────────────────────────────────────────────────────────

    [Fact]
    public void DefaultTier_IntegratedGpu_FewCores_IsPotato()
        => Assert.Equal(PerformanceTier.Potato,
                        PerformanceTierProfile.DefaultTier(Hw(cores: 4, discrete: false)));

    [Fact]
    public void DefaultTier_IntegratedGpu_ManyCores_IsBalanced()
        => Assert.Equal(PerformanceTier.Balanced,
                        PerformanceTierProfile.DefaultTier(Hw(cores: 16, discrete: false)));

    [Fact]
    public void DefaultTier_DiscreteGpu_FewCores_IsBalanced_NotWow()
        => Assert.Equal(PerformanceTier.Balanced,
                        PerformanceTierProfile.DefaultTier(Hw(cores: 4, discrete: true)));

    [Fact]
    public void DefaultTier_DiscreteGpu_HealthyCores_IsWow()
        => Assert.Equal(PerformanceTier.Wow,
                        PerformanceTierProfile.DefaultTier(Hw(cores: 12, discrete: true)));

    // ── Baseline ordering ────────────────────────────────────────────────────

    [Fact]
    public void Baselines_AreMonotonic_PotatoToWow()
    {
        var p = PerformanceTierProfile.Baseline(PerformanceTier.Potato);
        var b = PerformanceTierProfile.Baseline(PerformanceTier.Balanced);
        var w = PerformanceTierProfile.Baseline(PerformanceTier.Wow);

        Assert.True(p.PreviewResolutionScale < b.PreviewResolutionScale);
        Assert.True(b.PreviewResolutionScale < w.PreviewResolutionScale);

        Assert.True(p.VolumeSteps < b.VolumeSteps);
        Assert.True(b.VolumeSteps < w.VolumeSteps);

        Assert.True(p.AnimatedParamCeiling < b.AnimatedParamCeiling);
        Assert.True(b.AnimatedParamCeiling < w.AnimatedParamCeiling);

        Assert.True(p.AaSamples < b.AaSamples);
        Assert.True(b.AaSamples < w.AaSamples);
    }

    [Fact]
    public void Wow_Baseline_RunsFullResHighTier_NoCpuFallback()
    {
        var w = PerformanceTierProfile.Baseline(PerformanceTier.Wow);
        Assert.Equal(1.00, w.PreviewResolutionScale, precision: 6);
        Assert.Equal(QualityTier.High, w.QualityTier);
        Assert.False(w.AllowCpuFallback);
    }

    [Fact]
    public void Potato_Baseline_HalfRes_DraftTier_ToleratesCpu()
    {
        var p = PerformanceTierProfile.Baseline(PerformanceTier.Potato);
        Assert.Equal(0.50, p.PreviewResolutionScale, precision: 6);
        Assert.Equal(QualityTier.Draft, p.QualityTier);
        Assert.True(p.AllowCpuFallback);
    }

    // ── Resolve — the governor fold ──────────────────────────────────────────

    [Fact]
    public void Resolve_AtFullQuality_EqualsBaseline()
    {
        foreach (var tier in new[] { PerformanceTier.Potato, PerformanceTier.Balanced, PerformanceTier.Wow })
        {
            var baseline = PerformanceTierProfile.Baseline(tier);
            var resolved = PerformanceTierProfile.Resolve(baseline, qualityScale: 1.0);
            Assert.Equal(baseline, resolved);
        }
    }

    [Fact]
    public void Resolve_UnderThrottle_ReducesContinuousKnobs()
    {
        var baseline = PerformanceTierProfile.Baseline(PerformanceTier.Wow);
        var resolved = PerformanceTierProfile.Resolve(baseline, qualityScale: 0.5);

        Assert.True(resolved.PreviewResolutionScale < baseline.PreviewResolutionScale);
        Assert.True(resolved.VolumeSteps < baseline.VolumeSteps);
        Assert.True(resolved.AaSamples < baseline.AaSamples);
        Assert.True(resolved.AnimatedParamCeiling < baseline.AnimatedParamCeiling);
    }

    [Fact]
    public void Resolve_PreservesStructuralKnobs_UnderThrottle()
    {
        var baseline = PerformanceTierProfile.Baseline(PerformanceTier.Wow);
        var resolved = PerformanceTierProfile.Resolve(baseline, qualityScale: 0.25);

        // Precision tier / GPU gate / CPU-fallback are structural — throttling
        // must not silently change zoom limits or the render path.
        Assert.Equal(baseline.QualityTier, resolved.QualityTier);
        Assert.Equal(baseline.AllowGpuRender, resolved.AllowGpuRender);
        Assert.Equal(baseline.AllowCpuFallback, resolved.AllowCpuFallback);
    }

    [Fact]
    public void Resolve_AtZeroQuality_ClampsEveryKnobToItsFloor()
    {
        var baseline = PerformanceTierProfile.Baseline(PerformanceTier.Wow);
        var resolved = PerformanceTierProfile.Resolve(baseline, qualityScale: 0.0);

        Assert.Equal(PerformanceTierProfile.MinPreviewScale, resolved.PreviewResolutionScale, precision: 6);
        Assert.Equal(PerformanceTierProfile.MinVolumeSteps, resolved.VolumeSteps);
        Assert.Equal(1, resolved.AaSamples);
        Assert.Equal(1, resolved.AnimatedParamCeiling);
    }

    [Fact]
    public void Resolve_NeverBoostsPastBaseline_WhenQualityExceedsOne()
    {
        var baseline = PerformanceTierProfile.Baseline(PerformanceTier.Balanced);
        var resolved = PerformanceTierProfile.Resolve(baseline, qualityScale: 2.0);
        Assert.Equal(baseline, resolved);
    }
}
