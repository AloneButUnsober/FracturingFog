// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1336 — the sparse distance-field cache for interactive 3D camera moves
// (DistanceFieldCache; KIFS and User Bulb, CPU raymarch). Invariants:
//   • exact SDFs (sphere, box, torus — Lipschitz 1): inside a baked brick the cached
//     distance is within half a voxel diagonal of the true one (the issue's bound);
//     every surface point lies in a baked brick (no surface in an EMPTY cell); an
//     EMPTY cell never reads below the hit threshold; rays marched on the field hit
//     the sphere / box within a voxel of the analytic intersection and miss what
//     they should miss;
//   • the KIFS folds' DEs: near the surface the cached distance is within half a
//     voxel diagonal of the true DE (measured, the DE's own Lipschitz factor aside);
//   • a frame that does not march a field is the true-DE frame byte for byte (no
//     cache, the bake not ready yet, the camera too close);
//   • moving the camera or changing the lighting keeps the field (no re-bake); a DE
//     parameter re-bakes; the preview frame stays close to the true frame;
//   • the background bake completes and is then used; a throwing DE never escapes;
//     a new key cancels the old bake; over the budget a near cell reads the true DE;
//   • User Bulb's identity blit never keeps an approximate frame (else the settle frame
//     of the same view replayed it — also a #1337 bug);
//   • the live host: one settle frame after each preview, equal to the option-off frame.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1336DistanceFieldCacheTests(ITestOutputHelper output)
{
    private const int W = 128, H = 96;
    private static readonly double HalfDiag = Math.Sqrt(3) / 2;

    // ── Exact SDFs ─────────────────────────────────────────────────────────────

    public enum Shape { Sphere, Box, Torus }

    private static double Sdf(Shape s, double x, double y, double z) => s switch
    {
        Shape.Sphere => Math.Sqrt(x * x + y * y + z * z) - 1.0,
        Shape.Box => BoxSdf(x, y, z, 0.8, 0.6, 0.7),
        _ => Math.Sqrt(Math.Pow(Math.Sqrt(x * x + z * z) - 0.9, 2) + y * y) - 0.35,
    };

    private static double BoxSdf(double x, double y, double z, double bx, double by, double bz)
    {
        double qx = Math.Abs(x) - bx, qy = Math.Abs(y) - by, qz = Math.Abs(z) - bz;
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0), oz = Math.Max(qz, 0);
        return Math.Sqrt(ox * ox + oy * oy + oz * oz) + Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0);
    }

    // pixelAngle 0.004 at distance 5: level 7 for a half-size of 2 (voxel 2/128).
    private static DistanceField Bake(Func<double, double, double, double> de, double half = 2.0, double hitEps = 1e-3, long budget = 96L << 20)
    {
        var cache = new DistanceFieldCache { SynchronousBake = true, BudgetBytes = budget };
        var f = cache.Acquire(1, 0, 0, 0, half, hitEps, 0, 0, 5, 5, 0.004, de);
        Assert.NotNull(f);
        Assert.True(f!.Complete);
        return f;
    }

    [Theory]
    [InlineData(Shape.Sphere)]
    [InlineData(Shape.Box)]
    [InlineData(Shape.Torus)]
    public void Exact_Sdf_Bricks_Are_Within_Half_A_Voxel_Diagonal(Shape shape)
    {
        var f = Bake((x, y, z) => Sdf(shape, x, y, z));
        double v = f.Voxel, cell = v * DistanceFieldCache.BrickSize;
        var rng = new Random(1336);
        int inBrick = 0, surfaceSeen = 0;
        double worst = 0;
        for (int n = 0; n < 200_000; n++)
        {
            double x = rng.NextDouble() * 4 - 2, y = rng.NextDouble() * 4 - 2, z = rng.NextDouble() * 4 - 2;
            double d = Sdf(shape, x, y, z);
            int kind = f.CellKindAt(x, y, z);
            if (kind == 2)
            {
                inBrick++;
                worst = Math.Max(worst, Math.Abs(f.Sample(x, y, z) - d));
            }
            else if (kind == 0)
            {
                // EMPTY: never a hit, and a safe step: at most the true distance plus the
                // trilinear blend's overshoot, which for a distance field (Hessian ≤ 1/d)
                // is at most 3·cell²/(8·d_min), d_min the distance across the cell.
                double s = f.Sample(x, y, z);
                Assert.True(s > 1e-3, $"empty cell reads {s} at ({x}, {y}, {z})");
                double dMin = Math.Max(d - Math.Sqrt(3) * cell, cell);
                Assert.True(s <= d + 3 * cell * cell / (8 * dMin), $"empty cell oversteps: {s} vs {d}");
            }
            else if (kind == -1)
                Assert.True(f.Sample(x, y, z) > 0 && d > 0, "outside the grid must be outside the shape");
        }
        // Every surface point is in a baked brick.
        for (int n = 0; n < 20_000; n++)
        {
            // Project a random point onto the surface (a few Newton steps along the gradient).
            double x = rng.NextDouble() * 4 - 2, y = rng.NextDouble() * 4 - 2, z = rng.NextDouble() * 4 - 2;
            for (int it = 0; it < 30; it++)
            {
                double d = Sdf(shape, x, y, z), h = 1e-6;
                double gx = (Sdf(shape, x + h, y, z) - d) / h, gy = (Sdf(shape, x, y + h, z) - d) / h, gz = (Sdf(shape, x, y, z + h) - d) / h;
                double g2 = gx * gx + gy * gy + gz * gz;
                if (g2 < 1e-12) break;
                x -= d * gx / g2; y -= d * gy / g2; z -= d * gz / g2;
            }
            if (Math.Abs(Sdf(shape, x, y, z)) > 1e-9) continue;
            surfaceSeen++;
            Assert.Equal(2, f.CellKindAt(x, y, z));
        }
        output.WriteLine($"{shape}: voxel {v:G3}, bricks {f.BakedBricks}, samples in bricks {inBrick}, worst error {worst / (HalfDiag * v):F3} half-diagonals, surface points {surfaceSeen}");
        Assert.True(inBrick > 5_000 && surfaceSeen > 10_000);   // preconditions
        Assert.True(worst <= HalfDiag * v + 1e-6, $"brick error {worst} > half a voxel diagonal {HalfDiag * v}");
    }

    [Fact]
    public void Features_Smaller_Than_A_Cell_Keep_Their_Bricks()
    {
        // 300 spheres of radius 0.02 (a sixth of a cell): a cell holding one may have all
        // 8 corners the same distance from it, so only the gradient around it tells.
        var rng = new Random(1336);
        var c = new List<(double X, double Y, double Z)>();
        for (int n = 0; n < 300; n++) c.Add((rng.NextDouble() * 3 - 1.5, rng.NextDouble() * 3 - 1.5, rng.NextDouble() * 3 - 1.5));
        double De(double x, double y, double z)
        {
            double d = double.MaxValue;
            foreach (var p in c) d = Math.Min(d, Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y) + (z - p.Z) * (z - p.Z)) - 0.02);
            return d;
        }
        var f0 = Bake(De);
        // One more sphere exactly at the centre of a grid cell far from the others: its
        // cell's 8 corners all read the same distance (the cell alone shows no gradient).
        double cell = f0.Voxel * DistanceFieldCache.BrickSize;
        var box = f0.GridBox;
        (double X, double Y, double Z) centre = default;
        bool found = false;
        for (int k = 2; k < f0.GridZ - 2 && !found; k++)
            for (int j = 2; j < f0.GridY - 2 && !found; j++)
                for (int i = 2; i < f0.GridX - 2 && !found; i++)
                {
                    var q = (box.X0 + (i + 0.5) * cell, box.Y0 + (j + 0.5) * cell, box.Z0 + (k + 0.5) * cell);
                    if (c.All(p => Math.Sqrt((p.X - q.Item1) * (p.X - q.Item1) + (p.Y - q.Item2) * (p.Y - q.Item2) + (p.Z - q.Item3) * (p.Z - q.Item3)) > 6 * cell))
                    { centre = q; found = true; }
                }
        Assert.True(found);
        c.Add(centre);
        var f = Bake(De);
        Assert.Equal(box, f.GridBox);                                     // precondition: the same grid
        Assert.True(0.02 < cell / 4);                                     // precondition: sub-cell features
        int lost = 0;
        foreach (var p in c)
            for (int a = 0; a < 7; a++)
            {
                // the centre and six surface points
                double ox = a == 1 ? 0.02 : a == 2 ? -0.02 : 0, oy = a == 3 ? 0.02 : a == 4 ? -0.02 : 0, oz = a == 5 ? 0.02 : a == 6 ? -0.02 : 0;
                if (f.CellKindAt(p.X + ox, p.Y + oy, p.Z + oz) != 2) lost++;
            }
        Assert.Equal(0, lost);
    }

    [Theory]
    [InlineData(Shape.Sphere)]
    [InlineData(Shape.Box)]
    public void Rays_Marched_On_The_Field_Hit_Within_A_Voxel(Shape shape)
    {
        var f = Bake((x, y, z) => Sdf(shape, x, y, z));
        var rng = new Random(7);
        double eps = 1e-3;
        int hits = 0, misses = 0;
        for (int n = 0; n < 4000; n++)
        {
            // From (0, 0, 5) toward a random point of the z = 0 plane.
            double tx = rng.NextDouble() * 3.2 - 1.6, ty = rng.NextDouble() * 3.2 - 1.6;
            double dx = tx, dy = ty, dz = -5, l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            dx /= l; dy /= l; dz /= l;
            double exact = Intersect(shape, dx, dy, dz);
            double t = 0; bool hit = false;
            for (int s = 0; s < 400; s++)
            {
                double d = f.Sample(dx * t, dy * t, 5 + dz * t);
                if (d < eps) { hit = true; break; }
                t += d;
                if (t > 12) break;
            }
            // Grazing rays (within two voxels of the silhouette, or meeting the surface at
            // a shallow angle) may go either way.
            if (Grazes(shape, dx, dy, dz, 2 * f.Voxel)) continue;
            if (double.IsNaN(exact)) { Assert.False(hit, $"ray {n} hit nothing in the scene at t={t}"); misses++; }
            else
            {
                Assert.True(hit, $"ray {n} missed (exact t={exact})");
                Assert.True(Math.Abs(t - exact) <= f.Voxel, $"ray {n}: t {t} vs exact {exact}, voxel {f.Voxel}");
                hits++;
            }
        }
        output.WriteLine($"{shape}: {hits} hits, {misses} misses");
        Assert.True(hits > 500 && misses > 500);
    }

    // The analytic first intersection from (0, 0, 5) along d, NaN on a miss.
    private static double Intersect(Shape s, double dx, double dy, double dz) => IntersectFrom(s, 0, 0, 5, dx, dy, dz);

    private static double IntersectFrom(Shape s, double ox0, double oy0, double oz0, double dx, double dy, double dz)
    {
        if (s == Shape.Sphere)
        {
            double b = ox0 * dx + oy0 * dy + oz0 * dz, c = ox0 * ox0 + oy0 * oy0 + oz0 * oz0 - 1, disc = b * b - c;
            return disc < 0 ? double.NaN : -b - Math.Sqrt(disc);
        }
        double t0 = double.NegativeInfinity, t1 = double.PositiveInfinity;
        Span<double> o = stackalloc double[] { ox0, oy0, oz0 };
        Span<double> d = stackalloc double[] { dx, dy, dz };
        Span<double> e = stackalloc double[] { 0.8, 0.6, 0.7 };
        for (int a = 0; a < 3; a++)
        {
            if (Math.Abs(d[a]) < 1e-15) { if (Math.Abs(o[a]) > e[a]) return double.NaN; continue; }
            double ta = (-e[a] - o[a]) / d[a], tb = (e[a] - o[a]) / d[a];
            t0 = Math.Max(t0, Math.Min(ta, tb)); t1 = Math.Min(t1, Math.Max(ta, tb));
        }
        return t0 <= t1 && t1 > 0 ? t0 : double.NaN;
    }

    // True when the ray runs within `margin` of the silhouette (a parallel ray shifted
    // sideways by `margin` disagrees about hitting) or meets the surface at a shallow angle.
    private static bool Grazes(Shape s, double dx, double dy, double dz, double margin)
    {
        bool hit = !double.IsNaN(Intersect(s, dx, dy, dz));
        // Two unit vectors across the ray.
        double ax = Math.Abs(dx) < 0.9 ? 1 : 0, ay = ax == 1 ? 0 : 1;
        double ux = dy * 0 - dz * ay, uy = dz * ax - dx * 0, uz = dx * ay - dy * ax;
        double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz); ux /= ul; uy /= ul; uz /= ul;
        double vx = dy * uz - dz * uy, vy = dz * ux - dx * uz, vz = dx * uy - dy * ux;
        for (int k = 0; k < 8; k++)
        {
            double a = k * Math.PI / 4, ox = margin * (Math.Cos(a) * ux + Math.Sin(a) * vx),
                   oy = margin * (Math.Cos(a) * uy + Math.Sin(a) * vy), oz = margin * (Math.Cos(a) * uz + Math.Sin(a) * vz);
            if (!double.IsNaN(IntersectFrom(s, ox, oy, 5 + oz, dx, dy, dz)) != hit) return true;
        }
        if (!hit) return false;
        double t = Intersect(s, dx, dy, dz);
        double px = dx * t, py = dy * t, pz = 5 + dz * t, h = 1e-6, d0 = Sdf(s, px, py, pz);
        double gx = (Sdf(s, px + h, py, pz) - d0) / h, gy = (Sdf(s, px, py + h, pz) - d0) / h, gz = (Sdf(s, px, py, pz + h) - d0) / h;
        double cos = Math.Abs(gx * dx + gy * dy + gz * dz) / Math.Sqrt(gx * gx + gy * gy + gz * gz);
        return cos < 0.2;
    }

    // ── Fractal DEs (KIFS) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(KifsFoldKind.Menger)]
    [InlineData(KifsFoldKind.Octahedron)]
    [InlineData(KifsFoldKind.Sierpinski)]
    public void Kifs_Bricks_Are_Within_Half_A_Voxel_Diagonal_Near_The_Surface(KifsFoldKind fold)
    {
        double scale = KifsCalculator.ResolveScale(fold, 0);
        Func<double, double, double, double> de = (x, y, z) => KifsCalculator.ProbeDE(fold, x, y, z, scale, 1, 1, 1, 14);
        var f = Bake(de, half: 3.0, hitEps: 0.0012);
        double v = f.Voxel;
        var rng = new Random(9);
        int near = 0, within = 0;
        double worst = 0;
        for (int n = 0; n < 4_000_000 && near < 20_000; n++)
        {
            double x = rng.NextDouble() * 6 - 3, y = rng.NextDouble() * 6 - 3, z = rng.NextDouble() * 6 - 3;
            if (f.CellKindAt(x, y, z) != 2) continue;
            double d = de(x, y, z);
            if (Math.Abs(d) > v) continue;   // near the surface: within a voxel
            near++;
            double e = Math.Abs(f.Sample(x, y, z) - d);
            worst = Math.Max(worst, e);
            if (e <= HalfDiag * v) within++;
        }
        output.WriteLine($"{fold}: voxel {v:G3}, near-surface samples {near}, within half a voxel diagonal {within / (double)near:P2}, worst {worst / (HalfDiag * v):F2} half-diagonals");
        Assert.True(near > 5_000);
        Assert.True(within >= 0.99 * near, $"only {within}/{near} within half a voxel diagonal");
    }

    // ── Families ───────────────────────────────────────────────────────────────

    public enum Family { Menger, Octahedron, MandelboxRot, UserBulb }

    private static LightingFxData Fx()
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = false;
        fx.AoSamples = 5; fx.AoStrength = 1.0;
        fx.ShadowSteps = 32; fx.ShadowSoftK = 16;
        fx.SsaoSamples = 0;
        return fx;
    }

    private static IFractalCalculator Calc(Family fam, double theta = 0, Action<FractalParameters>? tweak = null, double zoom = 1.0)
    {
        var fp = new FractalParameters { Lighting = Fx(), UserBulbSource = "z^8 + c" };
        fp.KifsFold = fam switch
        {
            Family.Octahedron => KifsFoldKind.Octahedron,
            Family.MandelboxRot => KifsFoldKind.MandelboxRot,
            _ => KifsFoldKind.Menger,
        };
        fp.KifsCameraTheta += theta;
        fp.UserBulbCameraTheta += theta;
        tweak?.Invoke(fp);
        IFractalCalculator c = fam == Family.UserBulb
            ? new UserBulbCalculator(W, H) { FractalParameters = fp }
            : new KifsCalculator(W, H) { FractalParameters = fp };
        c.ColorMap = ColorPalette.BuiltIns[0];
        c.Zoom = zoom;
        return c;
    }

    private static uint[] Render(IFractalCalculator c, DistanceFieldCache? cache)
    {
        cache?.BeginRender();
        using (DistanceFieldCache.Use(cache)) c.Calculate(CancellationToken.None);
        return c.ColorBuffer.Take(W * H).ToArray();
    }

    private static double MeanError(uint[] a, uint[] b)
    {
        double e = 0;
        for (int i = 0; i < a.Length; i++)
        {
            uint p = a[i], q = b[i];
            e += Math.Max(Math.Abs((int)((p >> 16) & 255) - (int)((q >> 16) & 255)),
                 Math.Max(Math.Abs((int)((p >> 8) & 255) - (int)((q >> 8) & 255)), Math.Abs((int)(p & 255) - (int)(q & 255))));
        }
        return e / a.Length;
    }

    public static TheoryData<Family> Families()
    {
        var d = new TheoryData<Family>();
        foreach (var f in Enum.GetValues<Family>()) d.Add(f);
        return d;
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Preview_Frames_March_The_Field_And_Stay_Close(Family fam)
    {
        var cache = new DistanceFieldCache { SynchronousBake = true };
        var exact0 = Render(Calc(fam), null);
        Render(Calc(fam), cache);   // #1390: the first frame's hits pick the next frame's level
        var prev0 = Render(Calc(fam), cache);
        Assert.True(cache.LastFrameApproximate);
        var field = cache.LastField!;
        long evals = field.BakeEvaluations;
        Assert.NotEqual(exact0, prev0);   // precondition: the frame really marched the field

        // The camera moves and the lighting changes: the same field, no re-bake.
        var exact1 = Render(Calc(fam, 0.05), null);
        var prev1 = Render(Calc(fam, 0.05, fp => { var l = fp.Lighting; l.AoStrength = 0.5; fp.Lighting = l; }), cache);
        Assert.Same(field, cache.LastField);
        Assert.Equal(evals, field.BakeEvaluations);
        double err0 = MeanError(exact0, prev0), err1 = MeanError(exact1, Render(Calc(fam, 0.05), cache));
        output.WriteLine($"{fam}: level {field.Level}, bricks {field.BakedBricks}, preview error {err0:F2} / {err1:F2} levels");
        Assert.True(err0 < 40 && err1 < 40, $"preview error {err0:F1} / {err1:F1}");
        Assert.NotNull(prev1);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_Frame_Without_A_Field_Is_The_True_Frame(Family fam)
    {
        var exact = Render(Calc(fam), null);
        // The bake not ready yet (background, slowed by a gate): the true frame.
        var background = new DistanceFieldCache { SynchronousBake = false, BakeThreads = 1 };
        var first = Render(Calc(fam), background);
        Assert.False(background.LastFrameApproximate);
        Assert.Equal(exact, first);
        background.Clear();
        // The camera too close for the voxels: the true frame.
        var close = new DistanceFieldCache { SynchronousBake = true };
        var exactClose = Render(Calc(fam, zoom: 400), null);
        for (int n = 0; n < 2; n++)   // the second time the (finest) field exists already
        {
            Assert.Equal(exactClose, Render(Calc(fam, zoom: 400), close));
            Assert.False(close.LastFrameApproximate);
        }
    }

    [Fact]
    public void A_De_Parameter_Rebakes()
    {
        var cache = new DistanceFieldCache { SynchronousBake = true };
        Render(Calc(Family.Menger), cache);
        var a = cache.LastField;
        Render(Calc(Family.Menger, tweak: fp => fp.KifsIterations = 10), cache);
        Assert.NotSame(a, cache.LastField);
        Render(Calc(Family.UserBulb), cache);
        var b = cache.LastField;
        Render(Calc(Family.UserBulb, tweak: fp => fp.UserBulbIterations = 6), cache);
        Assert.NotSame(b, cache.LastField);
        Render(Calc(Family.UserBulb, tweak: fp => fp.UserBulbJuliaMode = true), cache);
        Assert.NotNull(cache.LastField);
    }

    [Fact]
    public void Level_Follows_The_View_Distance()
    {
        int far = DistanceFieldCache.LevelFor(3, 20, 0.004), mid = DistanceFieldCache.LevelFor(3, 4, 0.004), near = DistanceFieldCache.LevelFor(3, 0.5, 0.004);
        Assert.True(far <= mid && mid <= near);
        Assert.InRange(far, DistanceFieldCache.MinLevel, DistanceFieldCache.MaxLevel);
        Assert.Equal(DistanceFieldCache.MaxLevel, near);
        // The voxel of the chosen level is at most TargetVoxelPixels pixels (below the cap).
        double voxel = 3.0 / (1 << mid);
        Assert.True(voxel <= DistanceFieldCache.TargetVoxelPixels * 4 * 0.004 + 1e-12);
    }

    // ── Bake mechanics ─────────────────────────────────────────────────────────

    [Fact]
    public void Background_Bake_Completes_And_Is_Then_Used()
    {
        var cache = new DistanceFieldCache();
        Func<double, double, double, double> de = (x, y, z) => Sdf(Shape.Sphere, x, y, z);
        Assert.Null(cache.Acquire(1, 0, 0, 0, 2, 1e-3, 0, 0, 5, 5, 0.004, de));
        var sw = Stopwatch.StartNew();
        DistanceField? f = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            f = cache.Acquire(1, 0, 0, 0, 2, 1e-3, 0, 0, 5, 5, 0.004, de);
            if (f is { Complete: true }) break;
            Thread.Sleep(20);
        }
        Assert.NotNull(f);
        Assert.True(f!.Complete);
        Assert.Equal(f.BudgetedBricks, f.BakedBricks);
    }

    [Fact]
    public void A_Throwing_De_Never_Escapes_The_Background_Bake()
    {
        var cache = new DistanceFieldCache();
        Func<double, double, double, double> de = (x, y, z) => x > 0.5 ? throw new InvalidOperationException("formula") : Sdf(Shape.Sphere, x, y, z);
        for (int i = 0; i < 20; i++)
        {
            var f = cache.Acquire(1, 0, 0, 0, 2, 1e-3, 0, 0, 5, 5, 0.004, de);
            Assert.True(f is null || !f.Complete);
            Thread.Sleep(25);
        }
    }

    [Fact]
    public void A_New_Key_Cancels_The_Old_Bake()
    {
        var cache = new DistanceFieldCache { BakeThreads = 2 };
        long calls = 0;
        Func<double, double, double, double> slow = (x, y, z) => { Interlocked.Increment(ref calls); Thread.SpinWait(2000); return Sdf(Shape.Sphere, x, y, z); };
        Assert.Null(cache.Acquire(1, 0, 0, 0, 2, 1e-3, 0, 0, 5, 5, 0.004, slow));
        Thread.Sleep(200);
        cache.Acquire(2, 0, 0, 0, 2, 1e-3, 0, 0, 5, 5, 0.004, (x, y, z) => Sdf(Shape.Box, x, y, z));
        // Each bake thread stops within a row of samples (9 DE calls); give a loaded
        // machine time to get there, then the count must stand still.
        Thread.Sleep(1000);
        long after = Interlocked.Read(ref calls);
        Thread.Sleep(1000);
        Assert.True(Interlocked.Read(ref calls) - after <= 2 * 9, "the old bake kept evaluating after its key was replaced");
        cache.Clear();
    }

    [Fact]
    public void Over_The_Budget_A_Near_Cell_Reads_The_True_De()
    {
        Func<double, double, double, double> de = (x, y, z) => Sdf(Shape.Torus, x, y, z);
        var f = Bake(de, budget: 200L * 9 * 9 * 9 * sizeof(float));
        Assert.Equal(200, f.BudgetedBricks);
        Assert.True(f.NearCells > 400);
        var rng = new Random(3);
        int checkedNear = 0;
        for (int n = 0; n < 200_000 && checkedNear < 1000; n++)
        {
            double x = rng.NextDouble() * 4 - 2, y = rng.NextDouble() * 4 - 2, z = rng.NextDouble() * 4 - 2;
            if (f.CellKindAt(x, y, z) != 1) continue;
            Assert.Equal(de(x, y, z), f.Sample(x, y, z));
            checkedNear++;
        }
        Assert.True(checkedNear >= 1000);
    }

    // ── User Bulb's identity blit (#1336 / #1337) ─────────────────────────────

    // The identity blit needs acos(fwd·fwd') < 1e-9, which holds only when the dot product
    // rounds to exactly 1: an axis-aligned camera (φ = π/2, θ = 0 looks along −x).
    private static void AxisCamera(FractalParameters fp, double theta = 0)
    {
        fp.UserBulbCameraPhi = Math.PI / 2;
        fp.UserBulbCameraTheta = theta;
    }

    [Fact]
    public void User_Bulb_Never_Replays_An_Approximate_Frame()
    {
        // The same calculator across frames, as the host keeps it.
        var calc = (UserBulbCalculator)Calc(Family.UserBulb, tweak: fp => AxisCamera(fp));
        Assert.True(calc.FractalParameters.UserBulbTemporalReuse);   // precondition: the blit is on
        var exact = Render(Calc(Family.UserBulb, tweak: fp => AxisCamera(fp)), null);
        // Precondition: the blit replays a saved frame of this view (it returns before
        // marching anything, so it is far faster than the frame it replays).
        var probe = (UserBulbCalculator)Calc(Family.UserBulb, tweak: fp => AxisCamera(fp));
        var sw = Stopwatch.StartNew();
        var first = Render(probe, null);
        double full = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        Assert.Equal(first, Render(probe, null));
        double replay = sw.Elapsed.TotalMilliseconds;
        Assert.True(replay * 5 < full, $"no identity blit: {replay:F2} ms vs {full:F2} ms");
        var cache = new DistanceFieldCache { SynchronousBake = true };
        var preview = Render(calc, cache);
        Assert.True(cache.LastFrameApproximate);
        Assert.NotEqual(exact, preview);
        // The settle frame: same view, no cache.
        Assert.Equal(exact, Render(calc, null));

        // The same with #1337's temporal AO / shadows.
        var calc2 = (UserBulbCalculator)Calc(Family.UserBulb);
        var temporal = new TemporalLighting();
        uint[] Frame(double theta, bool settle)
        {
            AxisCamera(calc2.FractalParameters, theta);
            temporal.BeginRender(); temporal.Settle = settle;
            using (TemporalLighting.Use(temporal)) calc2.Calculate(CancellationToken.None);
            return calc2.ColorBuffer.Take(W * H).ToArray();
        }
        Frame(-0.01, false);
        Frame(0, false);
        Assert.True(temporal.LastFrameApproximate);   // precondition: the frame reused
        var settled = Frame(0, true);
        Assert.Equal(exact, settled);
    }

    // ── The live host ──────────────────────────────────────────────────────────

    private sealed class RecordingRenderer : IFractalRenderer
    {
        public readonly List<uint[]> Frames = new();
        public void UpdateTexture(uint[] colorBuffer, int width, int height)
        {
            lock (Frames) Frames.Add(colorBuffer.Take(width * height).ToArray());
        }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "record";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static List<uint[]> HostRun(bool on, out uint[] final)
    {
        var renderer = new RecordingRenderer();
        var fp = new FractalParameters { Lighting = Fx() };
        var state = new FractalViewState { FractalType = FractalType.Kifs, FractalParameters = fp, Zoom = 1.0 };
        using var host = new FractalRenderHost(renderer, state, W, H, ColorPalette.BuiltIns[0]) { DistanceFieldCache3D = on };
        host.DistanceField3DCache.SynchronousBake = true;
        using var done = new SemaphoreSlim(0);
        host.FrameCompleted += (_, _) => done.Release();
        host.Trigger();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no first frame");
        while (done.Wait(TimeSpan.FromSeconds(3))) { }   // the frame, its later stages and any settle
        fp.KifsCameraTheta += 0.05;
        host.Trigger();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no moving frame");
        while (done.Wait(TimeSpan.FromSeconds(3))) { }
        final = host.SnapshotFrame(out int sw, out int sh).Take(sw * sh).ToArray();
        lock (renderer.Frames) return renderer.Frames.ToList();
    }

    [Fact]
    public void Live_Host_Settles_On_The_True_Frame()
    {
        var off = HostRun(on: false, out var offFinal);
        var on = HostRun(on: true, out var onFinal);
        output.WriteLine($"uploads: off {off.Count}, on {on.Count}");
        Assert.Equal(off.Count + 2, on.Count);          // one settle frame after each preview
        Assert.NotEqual(off[^1], on[^2]);               // the moving frame marched the field
        Assert.Equal(off[^1], on[^1]);                  // the settle frame is the true frame
        Assert.Equal(offFinal, onFinal);
        Assert.Contains(on, f => !off.Any(o => o.SequenceEqual(f)));   // previews differ from every true frame
    }

    [Fact]
    public void The_Option_Defaults_Off_And_The_Menu_Forwards_It()
    {
        var renderer = new RecordingRenderer();
        using var host = new FractalRenderHost(renderer, new FractalViewState { FractalType = FractalType.Kifs }, W, H, ColorPalette.BuiltIns[0]);
        Assert.False(host.DistanceFieldCache3D);
        var vm = new FracturingFog.UI.Avalonia.ViewModels.FloatingMenuViewModel();
        bool? got = null;
        vm.DistanceFieldCache3DToggled += (_, v) => got = v;
        vm.SetDistanceFieldCache3DState(true);
        Assert.True(vm.DistanceFieldCache3D);
        Assert.Null(got);
        vm.DistanceFieldCache3D = false;
        Assert.False(got);
    }
}
