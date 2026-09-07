// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S6 (#408) — sub-cell froxel temporal reprojection under continuous camera motion.
// The base temporal blend (FroxelHistory.BlendAndStore) reuses the previous frame's
// SAME cell, which ghosts once the camera moves. BlendAndStoreReproject resamples the
// stored history in WORLD space: each current cell's world position (from the current
// camera basis + grid) maps into the previous frame's froxel coordinates and the
// history is trilinearly sampled there. These lock: the first frame passes through +
// seeds; a STATIC camera reprojects ~to the same cell (so it approximates the same-cell
// blend); a moving camera shifts the sampled history noticeably more; and the whole
// thing is deterministic (a twin / --batch requirement).

using System;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FroxelReprojectTests
{
    private const int Nx = 4, Ny = 4, Nz = 8;
    private static int Cells => Nx * Ny * Nz;

    private static FroxelGrid Grid() => new FroxelGrid(Nx, Ny, Nz, 1.0, 16.0);

    // Camera looking down +Z from the origin: right = +X, up = +Y, forward = +Z.
    private static FroxelHistory.CamBasis Cam(double px, double py, double pz) =>
        new FroxelHistory.CamBasis(px, py, pz, 1, 0, 0, 0, 1, 0, 0, 0, 1);

    private static (double[] r, double[] g, double[] b, double[] e) Field(Func<int, double> f)
    {
        var r = new double[Cells]; var g = new double[Cells];
        var b = new double[Cells]; var e = new double[Cells];
        for (int i = 0; i < Cells; i++) { r[i] = f(i); g[i] = f(i) * 0.5; b[i] = f(i) * 0.25; e[i] = f(i) * 0.1; }
        return (r, g, b, e);
    }

    [Fact]
    public void FirstFrame_PassesThrough_And_Seeds()
    {
        var h = new FroxelHistory();
        var (r, g, b, e) = Field(i => i + 1);
        var r0 = (double[])r.Clone();

        // No previous camera yet → the current values must pass through unchanged.
        h.BlendAndStoreReproject(r, g, b, e, Nx, Ny, Nz, Grid(), Cam(0, 0, 0), 1.0, 0.9, 1L);
        Assert.Equal(r0, r);

        // A second static-camera frame now finds a seeded history → it blends (the new
        // current is pulled toward the stored r0), so at least some cell changes.
        var (r2, g2, b2, e2) = Field(i => 0.0);
        h.BlendAndStoreReproject(r2, g2, b2, e2, Nx, Ny, Nz, Grid(), Cam(0, 0, 0), 1.0, 0.9, 1L);
        bool anyBlended = false;
        for (int i = 0; i < Cells; i++) if (r2[i] > 1e-9) { anyBlended = true; break; }
        Assert.True(anyBlended, "second frame should blend toward the seeded history");
    }

    [Fact]
    public void StaticCamera_Reproject_Approximates_SameCell_Blend()
    {
        // Two independent histories seeded identically; blend an identical second frame,
        // one via same-cell, one via reproject with an UNMOVED camera. The reprojected
        // result should track the same-cell result closely (a static camera maps each
        // cell ~back to itself — only the exponential depth re-quantisation differs).
        var seed = Field(i => 10.0 + i);
        var hSame = new FroxelHistory();
        var hRepro = new FroxelHistory();

        // Seed both with the same first frame.
        var s1 = (double[])seed.r.Clone(); var s2 = (double[])seed.g.Clone();
        var s3 = (double[])seed.b.Clone(); var s4 = (double[])seed.e.Clone();
        hSame.BlendAndStore(s1, s2, s3, s4, Cells, 1L, 0.9);
        var q1 = (double[])seed.r.Clone(); var q2 = (double[])seed.g.Clone();
        var q3 = (double[])seed.b.Clone(); var q4 = (double[])seed.e.Clone();
        hRepro.BlendAndStoreReproject(q1, q2, q3, q4, Nx, Ny, Nz, Grid(), Cam(0, 0, 0), 1.0, 0.9, 1L);

        // Second frame (zeros), same feedback.
        var a = new double[Cells]; var b = new double[Cells]; var c = new double[Cells]; var d = new double[Cells];
        hSame.BlendAndStore(a, b, c, d, Cells, 1L, 0.9);
        var p = new double[Cells]; var q = new double[Cells]; var r = new double[Cells]; var s = new double[Cells];
        hRepro.BlendAndStoreReproject(p, q, r, s, Nx, Ny, Nz, Grid(), Cam(0, 0, 0), 1.0, 0.9, 1L);

        double maxDiff = 0;
        for (int i = 0; i < Cells; i++) maxDiff = Math.Max(maxDiff, Math.Abs(a[i] - p[i]));
        // Same-cell value ≈ 0.9·seed (seed up to ~74). A static reprojection differs only
        // by depth re-quantisation → well under one seed step.
        Assert.True(maxDiff < 2.0, $"static-camera reprojection should track same-cell (maxDiff {maxDiff:F3})");
    }

    [Fact]
    public void MovingCamera_Shifts_Sampled_History()
    {
        // Seed a strong lateral gradient (varies with cx), then blend a zero frame with
        // the camera translated by roughly two cell widths in +X. The reprojected
        // history is fetched from shifted cells, so the result differs from a static
        // reprojection by clearly more than the static depth-requantisation noise.
        var seed = Field(i =>
        {
            int cx = (i / Nz) % Nx;       // column index from (cy*nx+cx)*nz+z
            return 100.0 * cx;
        });

        double[] StaticResult()
        {
            var h = new FroxelHistory();
            var s = ((double[])seed.r.Clone(), (double[])seed.g.Clone(), (double[])seed.b.Clone(), (double[])seed.e.Clone());
            h.BlendAndStoreReproject(s.Item1, s.Item2, s.Item3, s.Item4, Nx, Ny, Nz, Grid(), Cam(0, 0, 0), 1.0, 0.9, 1L);
            var z = (new double[Cells], new double[Cells], new double[Cells], new double[Cells]);
            h.BlendAndStoreReproject(z.Item1, z.Item2, z.Item3, z.Item4, Nx, Ny, Nz, Grid(), Cam(0, 0, 0), 1.0, 0.9, 1L);
            return z.Item1;
        }

        double[] MovedResult()
        {
            var h = new FroxelHistory();
            var s = ((double[])seed.r.Clone(), (double[])seed.g.Clone(), (double[])seed.b.Clone(), (double[])seed.e.Clone());
            h.BlendAndStoreReproject(s.Item1, s.Item2, s.Item3, s.Item4, Nx, Ny, Nz, Grid(), Cam(0, 0, 0), 1.0, 0.9, 1L);
            var z = (new double[Cells], new double[Cells], new double[Cells], new double[Cells]);
            // extent 1.0, Nx 4 → one cell ≈ 0.5 world units laterally; move +1.0 (~2 cells).
            h.BlendAndStoreReproject(z.Item1, z.Item2, z.Item3, z.Item4, Nx, Ny, Nz, Grid(), Cam(1.0, 0, 0), 1.0, 0.9, 1L);
            return z.Item1;
        }

        var stat = StaticResult();
        var moved = MovedResult();
        double diff = 0;
        for (int i = 0; i < Cells; i++) diff = Math.Max(diff, Math.Abs(stat[i] - moved[i]));
        Assert.True(diff > 5.0, $"a moving camera should resample shifted history cells (maxDiff {diff:F3})");
    }

    [Fact]
    public void Reproject_Is_Deterministic()
    {
        double[] Run()
        {
            var h = new FroxelHistory();
            var s = Field(i => 5.0 + i);
            h.BlendAndStoreReproject(s.r, s.g, s.b, s.e, Nx, Ny, Nz, Grid(), Cam(0, 0, 0), 1.0, 0.9, 1L);
            var (r, g, b, e) = Field(i => 1.0);
            h.BlendAndStoreReproject(r, g, b, e, Nx, Ny, Nz, Grid(), Cam(0.3, 0.1, 0.2), 1.0, 0.9, 1L);
            return r;
        }
        Assert.Equal(Run(), Run());
    }
}
