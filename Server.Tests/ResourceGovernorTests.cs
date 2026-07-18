// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Render;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Scene Engine Roadmap Phase S1: the adaptive resource governor (the 90%
/// cap). Covers the soft-target throttle, the hard-cap breach flag, the
/// unconditional cache-shed watermark, the offline (non-participating)
/// freeze, hysteresis-band hold, and slow recovery.
/// </summary>
public sealed class ResourceGovernorTests
{
    private static ResourceSample S(double cpu, double mem) => new(cpu, mem);

    [Fact]
    public void Idle_HoldsFullQuality_NoThrottle_NoShed_NoBreach()
    {
        var g = new ResourceGovernor();
        var d = g.Evaluate(S(cpu: 10, mem: 0.20), participatesInGovernor: true);

        Assert.Equal(1.0, d.QualityScale);
        Assert.False(d.ThrottleActive);
        Assert.False(d.ShedCaches);
        Assert.False(d.HardCapBreached);
    }

    [Fact]
    public void SustainedCpuPressure_RatchetsQualityDown_ClampedAtFloor()
    {
        var cfg = new ResourceGovernorConfig();
        var g = new ResourceGovernor(cfg);

        double prev = 1.0;
        for (int i = 0; i < 3; i++)
        {
            var d = g.Evaluate(S(cpu: 95, mem: 0.30), participatesInGovernor: true);
            Assert.True(d.ThrottleActive);
            Assert.True(d.QualityScale < prev);       // steps down each tick
            prev = d.QualityScale;
        }

        // Drive far past the floor — never dips below it.
        for (int i = 0; i < 50; i++)
            g.Evaluate(S(cpu: 99, mem: 0.30), participatesInGovernor: true);

        Assert.Equal(cfg.QualityFloor, g.QualityScale, precision: 6);
    }

    [Fact]
    public void Offline_DoesNotThrottle_EvenUnderCpuPressure()
    {
        var g = new ResourceGovernor();

        for (int i = 0; i < 10; i++)
        {
            var d = g.Evaluate(S(cpu: 99, mem: 0.30), participatesInGovernor: false);
            Assert.Equal(1.0, d.QualityScale);        // quality frozen full
            Assert.False(d.ThrottleActive);
            Assert.True(d.HardCapBreached);           // breach flag still raised
        }
    }

    [Fact]
    public void MemoryOverSoftWatermark_ShedsCaches_RegardlessOfParticipation()
    {
        var g = new ResourceGovernor();

        var on = g.Evaluate(S(cpu: 10, mem: 0.85), participatesInGovernor: true);
        Assert.True(on.ShedCaches);

        var off = g.Evaluate(S(cpu: 10, mem: 0.85), participatesInGovernor: false);
        Assert.True(off.ShedCaches);                  // shed is unconditional
    }

    [Fact]
    public void HardCapBreached_WhenCpuOrMemoryAtCeiling()
    {
        var g = new ResourceGovernor();

        Assert.True(g.Evaluate(S(cpu: 90, mem: 0.30), participatesInGovernor: true).HardCapBreached);
        Assert.True(g.Evaluate(S(cpu: 10, mem: 0.90), participatesInGovernor: true).HardCapBreached);
        Assert.False(g.Evaluate(S(cpu: 89, mem: 0.89), participatesInGovernor: true).HardCapBreached);
    }

    [Fact]
    public void HysteresisBand_HoldsQuality_NeitherDownNorUp()
    {
        var g = new ResourceGovernor();

        // Push it down first.
        for (int i = 0; i < 4; i++)
            g.Evaluate(S(cpu: 95, mem: 0.30), participatesInGovernor: true);
        double throttled = g.QualityScale;
        Assert.True(throttled < 1.0);

        // 80% CPU is below the 85% soft target but above the 75% recover
        // threshold — the band. Quality must not move, and recovery must not
        // accumulate.
        for (int i = 0; i < 20; i++)
            g.Evaluate(S(cpu: 80, mem: 0.30), participatesInGovernor: true);

        Assert.Equal(throttled, g.QualityScale, precision: 6);
    }

    [Fact]
    public void Recovery_IsSlow_RequiresSustainedCalm_ThenStepsUp()
    {
        var cfg = new ResourceGovernorConfig();
        var g = new ResourceGovernor(cfg);

        for (int i = 0; i < 4; i++)
            g.Evaluate(S(cpu: 95, mem: 0.30), participatesInGovernor: true);
        double throttled = g.QualityScale;

        // Calm, but fewer than RecoverHoldTicks — no step-up yet.
        for (int i = 0; i < cfg.RecoverHoldTicks - 1; i++)
            g.Evaluate(S(cpu: 20, mem: 0.20), participatesInGovernor: true);
        Assert.Equal(throttled, g.QualityScale, precision: 6);

        // One more calm tick crosses the hold threshold → single step up.
        g.Evaluate(S(cpu: 20, mem: 0.20), participatesInGovernor: true);
        Assert.Equal(throttled + cfg.StepUp, g.QualityScale, precision: 6);
    }

    [Fact]
    public void Recovery_NeverExceedsFullQuality()
    {
        var g = new ResourceGovernor();

        // Long sustained calm from a full-quality start stays pinned at 1.0.
        for (int i = 0; i < 200; i++)
            g.Evaluate(S(cpu: 5, mem: 0.10), participatesInGovernor: true);

        Assert.Equal(1.0, g.QualityScale, precision: 6);
    }

    [Fact]
    public void Reset_RestoresFullQuality()
    {
        var g = new ResourceGovernor();
        for (int i = 0; i < 5; i++)
            g.Evaluate(S(cpu: 99, mem: 0.30), participatesInGovernor: true);
        Assert.True(g.QualityScale < 1.0);

        g.Reset();
        Assert.Equal(1.0, g.QualityScale);
    }
}
