// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #510 — FractalParameters.Clone() omitted the Relief 3D froxel (frustum-voxel)
// volumetrics fields, so a cloned params (region / preset / scene snapshot, batch
// copy) silently dropped froxel fog + its temporal settings. Lock the round-trip.

using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S510CloneFroxelTests
{
    [Fact]
    public void Clone_Preserves_Froxel_Fields()
    {
        var p = new FractalParameters
        {
            Relief2DFroxelVolumetrics = true,
            Relief2DFroxelTemporal = true,
            Relief2DFroxelTemporalFeedback = 0.42,
            Relief2DFroxelReproject = true,
            Relief2DFroxelQuality = FroxelQuality.High,
        };

        var c = p.Clone();

        Assert.True(c.Relief2DFroxelVolumetrics);
        Assert.True(c.Relief2DFroxelTemporal);
        Assert.Equal(0.42, c.Relief2DFroxelTemporalFeedback);
        Assert.True(c.Relief2DFroxelReproject);
        Assert.Equal(FroxelQuality.High, c.Relief2DFroxelQuality);
    }

    // Defaults must clone cleanly too (no accidental flip).
    [Fact]
    public void Clone_Preserves_Froxel_Defaults()
    {
        var c = new FractalParameters().Clone();
        Assert.False(c.Relief2DFroxelVolumetrics);
        Assert.False(c.Relief2DFroxelTemporal);
        Assert.False(c.Relief2DFroxelReproject);
    }
}
