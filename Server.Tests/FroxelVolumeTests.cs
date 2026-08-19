// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S6 (3D-Rendering-Roadmap.md, parent #389) — the froxel grid +
// front-to-back scattering integration. Contract: the depth distribution is
// exponential (near-dense) and invertible; an empty column is fully transparent;
// a uniform medium's far transmittance is exp(-extinction·distance) and its
// in-scatter accumulates monotonically front-to-back.

using System;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FroxelVolumeTests
{
    // Exponential depth slices: endpoints pinned to near/far, monotone increasing,
    // near-dense (front slices thinner than back slices).
    [Fact]
    public void Grid_Depth_Is_Exponential_And_Monotone()
    {
        var g = new FroxelGrid(4, 4, 8, near: 1.0, far: 100.0);
        Assert.Equal(1.0, g.SliceDepth(0), 9);
        Assert.Equal(100.0, g.SliceDepth(8), 6);

        double prev = -1;
        for (int z = 0; z <= 8; z++)
        {
            double d = g.SliceDepth(z);
            Assert.True(d > prev, $"depth not monotone at slice {z}");
            prev = d;
        }
        Assert.True(g.SliceThickness(0) < g.SliceThickness(7), "front slices should be thinner (near-dense)");
    }

    // DepthToSlice inverts SliceDepth and clamps outside [near, far].
    [Fact]
    public void Grid_DepthToSlice_Inverts_And_Clamps()
    {
        var g = new FroxelGrid(4, 4, 16, 2.0, 200.0);
        for (int z = 1; z < 16; z++)
        {
            double slice = g.DepthToSlice(g.SliceDepth(z));
            Assert.Equal(z, slice, 5);
        }
        Assert.Equal(0.0, g.DepthToSlice(1.0), 9);     // below near
        Assert.Equal(16.0, g.DepthToSlice(500.0), 9);  // beyond far
    }

    // An empty column (no scatter, no extinction) is fully transparent with no
    // in-scatter at every slice.
    [Fact]
    public void Empty_Column_Is_Transparent()
    {
        int n = 8;
        var (sr, sg, sb, ext, th) = Column(n, 0, 0, 0, 0, 1.0);
        var (ir, ig, ib, tr) = Out(n);
        FroxelIntegrator.IntegrateColumn(sr, sg, sb, ext, th, n, ir, ig, ib, tr);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(0.0, ir[i], 12);
            Assert.Equal(1.0, tr[i], 12);
        }
    }

    // A uniform absorbing medium: transmittance falls monotonically and the far
    // value equals exp(-extinction · total-distance).
    [Fact]
    public void Uniform_Medium_Transmittance_Matches_BeerLambert()
    {
        int n = 32;
        double ext = 0.05, thick = 1.0;
        var (sr, sg, sb, e, th) = Column(n, 0, 0, 0, ext, thick);
        var (ir, ig, ib, tr) = Out(n);
        FroxelIntegrator.IntegrateColumn(sr, sg, sb, e, th, n, ir, ig, ib, tr);

        double prev = 2.0;
        foreach (var t in tr) { Assert.True(t <= prev + 1e-12 && t >= 0 && t <= 1); prev = t; }
        Assert.Equal(Math.Exp(-ext * thick * n), tr[n - 1], 6);
    }

    // Uniform positive in-scatter accumulates monotonically front-to-back and
    // stays bounded (transmittance-weighted).
    [Fact]
    public void Uniform_Scatter_Accumulates_Monotonically()
    {
        int n = 16;
        var (sr, sg, sb, e, th) = Column(n, 0.5, 0.5, 0.5, 0.1, 1.0);
        var (ir, ig, ib, tr) = Out(n);
        FroxelIntegrator.IntegrateColumn(sr, sg, sb, e, th, n, ir, ig, ib, tr);

        double prev = -1;
        for (int i = 0; i < n; i++)
        {
            Assert.True(ir[i] >= prev - 1e-12, $"in-scatter not monotone at {i}");
            prev = ir[i];
        }
        Assert.True(ir[n - 1] > 0.0);
    }

    // Sampling: integer slices return stored values, fractions interpolate,
    // out-of-range clamps.
    [Fact]
    public void Sample_Interpolates_And_Clamps()
    {
        int n = 4;
        var ir = new[] { 0.0, 1.0, 2.0, 3.0 };
        var ig = new[] { 0.0, 1.0, 2.0, 3.0 };
        var ib = new[] { 0.0, 1.0, 2.0, 3.0 };
        var tr = new[] { 0.9, 0.7, 0.5, 0.3 };

        var mid = FroxelIntegrator.Sample(ir, ig, ib, tr, n, 1.5);
        Assert.Equal(1.5, mid.inR, 9);
        Assert.Equal(0.6, mid.trans, 9);

        var below = FroxelIntegrator.Sample(ir, ig, ib, tr, n, -1.0);
        Assert.Equal((0.0, 1.0), (below.inR, below.trans));

        var beyond = FroxelIntegrator.Sample(ir, ig, ib, tr, n, 99.0);
        Assert.Equal((3.0, 0.3), (beyond.inR, beyond.trans));
    }

    private static (double[] sr, double[] sg, double[] sb, double[] ext, double[] th) Column(
        int n, double s, double sg, double sb, double ext, double thick)
    {
        var R = new double[n]; var G = new double[n]; var B = new double[n];
        var E = new double[n]; var T = new double[n];
        for (int i = 0; i < n; i++) { R[i] = s; G[i] = sg; B[i] = sb; E[i] = ext; T[i] = thick; }
        return (R, G, B, E, T);
    }

    private static (double[] ir, double[] ig, double[] ib, double[] tr) Out(int n)
        => (new double[n], new double[n], new double[n], new double[n]);
}
