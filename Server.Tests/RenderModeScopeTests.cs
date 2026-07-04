using System;
using System.Threading;

using FracturingFog.Render;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Scene Engine Roadmap Phase S0: the two-mode render split contract. Covers
/// the canonical policies, the ambient <see cref="RenderModeScope"/> push /
/// restore, nesting, thread isolation, and the GPU-resolve decision point.
/// </summary>
public sealed class RenderModeScopeTests
{
    [Fact]
    public void Default_Current_IsRealtime()
    {
        Assert.Same(RenderModePolicy.Realtime, RenderModeScope.Current);
        Assert.Equal(RenderMode.RealtimePreview, RenderModeScope.Current.Mode);
    }

    [Fact]
    public void RealtimePolicy_HasBudget_ParticipatesInGovernor_AllowsGpu()
    {
        var p = RenderModePolicy.Realtime;
        Assert.False(p.IsFrameLocked);
        Assert.NotNull(p.FrameTimeBudgetMs);
        Assert.True(p.ParticipatesInGovernor);
        Assert.False(p.PinDeterministicCpuPath);
        Assert.True(p.ResolveUseGpuRender(requested: true));
    }

    [Fact]
    public void OfflinePolicy_IsFrameLocked_Deterministic_NoGovernor_RefusesGpu()
    {
        var p = RenderModePolicy.Offline;
        Assert.True(p.IsFrameLocked);
        Assert.Null(p.FrameTimeBudgetMs);
        Assert.False(p.ParticipatesInGovernor);
        Assert.True(p.PinDeterministicCpuPath);
        // Deterministic pin refuses the GPU even when the caller asks for it.
        Assert.False(p.ResolveUseGpuRender(requested: true));
    }

    [Fact]
    public void OfflineFastGpu_IsFrameLocked_ButAllowsGpu()
    {
        var p = RenderModePolicy.OfflineFastGpu;
        Assert.True(p.IsFrameLocked);
        Assert.False(p.PinDeterministicCpuPath);
        Assert.True(p.ResolveUseGpuRender(requested: true));
        // Never fabricates a GPU render the caller didn't ask for.
        Assert.False(p.ResolveUseGpuRender(requested: false));
    }

    [Fact]
    public void Enter_SetsCurrent_AndRestoresOnDispose()
    {
        Assert.Same(RenderModePolicy.Realtime, RenderModeScope.Current);

        using (RenderModeScope.Enter(RenderModePolicy.Offline))
        {
            Assert.Same(RenderModePolicy.Offline, RenderModeScope.Current);
        }

        Assert.Same(RenderModePolicy.Realtime, RenderModeScope.Current);
    }

    [Fact]
    public void Scopes_Nest_AndRestoreInnerToOuter()
    {
        using (RenderModeScope.Enter(RenderModePolicy.Offline))
        {
            Assert.Same(RenderModePolicy.Offline, RenderModeScope.Current);

            using (RenderModeScope.Enter(RenderModePolicy.OfflineFastGpu))
            {
                Assert.Same(RenderModePolicy.OfflineFastGpu, RenderModeScope.Current);
            }

            // Inner scope must not leak into the outer one.
            Assert.Same(RenderModePolicy.Offline, RenderModeScope.Current);
        }

        Assert.Same(RenderModePolicy.Realtime, RenderModeScope.Current);
    }

    [Fact]
    public void Enter_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RenderModeScope.Enter(null!));
    }

    [Fact]
    public void Current_IsThreadStatic_ScopeDoesNotLeakAcrossThreads()
    {
        RenderModePolicy? observedOnOtherThread = null;

        using (RenderModeScope.Enter(RenderModePolicy.Offline))
        {
            // A different thread must still see the Realtime default — the
            // offline scope is confined to the thread that entered it. Use a
            // blocking Join (not await) so the entering thread stays inside the
            // scope the whole time; the scope is thread-affine, not async-flow.
            var other = new Thread(() => observedOnOtherThread = RenderModeScope.Current);
            other.Start();
            other.Join();

            Assert.Same(RenderModePolicy.Offline, RenderModeScope.Current);
        }

        Assert.Same(RenderModePolicy.Realtime, observedOnOtherThread);
    }
}
