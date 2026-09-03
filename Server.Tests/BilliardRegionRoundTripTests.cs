using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #627 A3 — guards the store-schema round-trip for the chaotic-billiard geometry.
// The editor store-schema trap (#611/#613): per-item settings silently reset
// unless present at BOTH the capture (Snapshot) and apply (ApplyTo) sites of the
// RegionFractalParams schema. This asserts every billiard field survives a
// save -> recall cycle.
public class BilliardRegionRoundTripTests
{
    [Fact]
    public void All_Billiard_Fields_Survive_Snapshot_And_Apply()
    {
        var src = new FractalParameters
        {
            BilliardGeometry = BilliardGeometry.Ring,
            BilliardDiskCount = 9,
            BilliardDiskRadius = 0.37,
            BilliardSeparation = 1.35,
            BilliardMaxBounces = 512,
            BilliardGateCount = 8,
            BilliardSeed = 42,
        };

        var snap = RegionFractalParams.Snapshot(FractalType.ChaoticBilliard, src);
        Assert.NotNull(snap);

        // Apply onto a fresh (default) params — recall must overwrite defaults.
        var dst = new FractalParameters();
        snap!.ApplyTo(dst);

        Assert.Equal(BilliardGeometry.Ring, dst.BilliardGeometry);
        Assert.Equal(9, dst.BilliardDiskCount);
        Assert.Equal(0.37, dst.BilliardDiskRadius, 6);
        Assert.Equal(1.35, dst.BilliardSeparation, 6);
        Assert.Equal(512, dst.BilliardMaxBounces);
        Assert.Equal(8, dst.BilliardGateCount);
        Assert.Equal(42, dst.BilliardSeed);
    }

    [Fact]
    public void Clone_Preserves_Billiard_Fields()
    {
        var src = new FractalParameters
        {
            BilliardGeometry = BilliardGeometry.NDisk,
            BilliardDiskCount = 12,
            BilliardSeed = 7,
        };
        var clone = src.Clone();
        Assert.Equal(BilliardGeometry.NDisk, clone.BilliardGeometry);
        Assert.Equal(12, clone.BilliardDiskCount);
        Assert.Equal(7, clone.BilliardSeed);
    }
}
