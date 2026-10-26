// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1390 parts 1 + 4 — the distance-field cache (#1336) for the other CPU 3D
// families, and the voxel level from the hit distances. Measured
// (--distancefieldprobe), the field pays on Mandelbulb (3x) and Mandelbox (4-5x);
// on Quaternion Julia / Mandelbrot and Bicomplex it is no faster (their DE costs
// what a field sample does), on Coquaternion and the Julibrot volume slower, and on
// Kleinian slower and wrong (its limit set is detail at every scale: most of it
// thinner than a voxel, so rays pass through). Those six march their DE. The cache
// itself (brick accuracy on exact SDFs and KIFS, EMPTY cells, the bake) is S1336's;
// here, each wired family:
//   • a preview frame marches the field (the frame differs from the true one) and
//     stays close to it; at the lattice points of its bricks the field equals the
//     family's DE (the field really is this family's DE);
//   • moving the camera or changing the lighting keeps the field (no re-bake); a DE
//     parameter (the iteration depth) re-bakes;
//   • a frame that marches no field is the true frame byte for byte (no cache, the
//     bake not ready yet, the camera too close for the voxels, thin-lens DoF);
//   • the live host settles on the true frame after a preview (Mandelbulb);
//   • the other six never march a field;
//   • part 4: a frame's hit distances (a sparse sample) set the next frame's view
//     distance (their nearer quartile) for that fractal only, and so its level;
//     Mandelbulb, Mandelbox and KIFS report them.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Calculators;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1390DistanceFieldFamiliesTests(ITestOutputHelper output)
{
    private const int W = 128, H = 96;
    private static readonly double HalfDiag = Math.Sqrt(3) / 2;

    public enum Family { Mandelbulb, Mandelbox, QuatJulia, QuatMandelbrot, Kleinian, Bicomplex, Coquaternion, JulibrotVolume }

    private static readonly Family[] Wired = { Family.Mandelbulb, Family.Mandelbox };

    public static TheoryData<Family> Families()
    {
        var d = new TheoryData<Family>();
        foreach (var f in Wired) d.Add(f);
        return d;
    }

    public static TheoryData<Family> Unwired()
    {
        var d = new TheoryData<Family>();
        foreach (var f in Enum.GetValues<Family>()) if (Array.IndexOf(Wired, f) < 0) d.Add(f);
        return d;
    }

    private static LightingFxData Fx()
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = false;
        fx.AoSamples = 5; fx.AoStrength = 1.0;
        fx.ShadowSteps = 32; fx.ShadowSoftK = 16;
        fx.SsaoSamples = 0;
        return fx;
    }

    // The camera theta and the DE depth of each family.
    private static void Theta(Family f, FractalParameters p, double d)
    {
        switch (f)
        {
            case Family.Mandelbulb: p.BulbCameraTheta += d; break;
            case Family.Mandelbox: p.MandelboxCameraTheta += d; break;
            case Family.QuatJulia: p.QJuliaCameraTheta += d; break;
            case Family.QuatMandelbrot: p.QMandelCameraTheta += d; break;
            case Family.Kleinian: p.KleinianCameraTheta += d; break;
            case Family.Bicomplex: p.BicomplexCameraTheta += d; break;
            case Family.Coquaternion: p.CoquaternionCameraTheta += d; break;
            default: p.DualOrbitVolumeCameraTheta += d; break;
        }
    }

    private static void FewerIterations(Family f, FractalParameters p)
    {
        switch (f)
        {
            case Family.Mandelbulb: p.BulbIterations -= 2; break;
            case Family.Mandelbox: p.MandelboxIterations -= 2; break;
            case Family.QuatJulia: p.QJuliaIterations -= 2; break;
            case Family.QuatMandelbrot: p.QMandelIterations -= 2; break;
            case Family.Kleinian: p.KleinianIterations -= 2; break;
            case Family.Bicomplex: p.BicomplexIterations -= 2; break;
            case Family.Coquaternion: p.CoquaternionIterations -= 2; break;
            default: p.DualOrbitVolumeIterations -= 2; break;
        }
    }

    private static IFractalCalculator Calc(Family fam, double theta = 0, Action<FractalParameters>? tweak = null, double zoom = 1.0)
    {
        var fp = new FractalParameters { Lighting = Fx() };
        Theta(fam, fp, theta);
        tweak?.Invoke(fp);
        IFractalCalculator c = fam switch
        {
            Family.Mandelbulb => new MandelbulbCalculator(W, H) { FractalParameters = fp },
            Family.Mandelbox => new MandelboxCalculator(W, H) { FractalParameters = fp },
            Family.QuatJulia => new QuatJuliaCalculator(W, H) { FractalParameters = fp },
            Family.QuatMandelbrot => new QuatMandelbrotCalculator(W, H) { FractalParameters = fp },
            Family.Kleinian => new KleinianCalculator(W, H) { FractalParameters = fp },
            Family.Bicomplex => new BicomplexMandelbrotCalculator(W, H) { FractalParameters = fp },
            Family.Coquaternion => new CoquaternionMandelbrotCalculator(W, H) { FractalParameters = fp },
            _ => new DualOrbitVolumeCalculator(W, H) { FractalParameters = fp },
        };
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

    [Theory]
    [MemberData(nameof(Families))]
    public void Preview_Frames_March_The_Field_And_Stay_Close(Family fam)
    {
        var cache = new DistanceFieldCache { SynchronousBake = true };
        var exact0 = Render(Calc(fam), null);
        Render(Calc(fam), cache);   // the first frame's hits pick the level of the next (part 4)
        var prev0 = Render(Calc(fam), cache);
        Assert.True(cache.LastFrameApproximate, $"{fam}: the frame marched no field");
        var field = cache.LastField!;
        long evals = field.BakeEvaluations;
        Assert.NotEqual(exact0, prev0);   // precondition: the frame really marched the field
        Assert.Contains(exact0, p => p != exact0[0]);   // precondition: a real frame, not one colour

        // The camera moves and the lighting changes: the same field, no re-bake.
        var exact1 = Render(Calc(fam, 0.05), null);
        Render(Calc(fam, 0.05, fp => { var l = fp.Lighting; l.AoStrength = 0.5; fp.Lighting = l; }), cache);
        Assert.Same(field, cache.LastField);
        Assert.Equal(evals, field.BakeEvaluations);
        double err0 = MeanError(exact0, prev0), err1 = MeanError(exact1, Render(Calc(fam, 0.05), cache));
        output.WriteLine($"{fam}: level {field.Level}, voxel {field.Voxel:G3}, grid {field.GridX}x{field.GridY}x{field.GridZ}, " +
                         $"bricks {field.BakedBricks}, preview error {err0:F2} / {err1:F2} levels");
        Assert.True(err0 < 40 && err1 < 40, $"{fam}: preview error {err0:F1} / {err1:F1}");

        // A DE parameter re-bakes.
        Render(Calc(fam, tweak: fp => FewerIterations(fam, fp)), cache);
        Assert.NotSame(field, cache.LastField);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void The_Field_Is_The_Familys_De_At_Its_Samples(Family fam)
    {
        // A brick stores the DE at its (BrickSize+1)^3 lattice points (as floats), so
        // at a lattice point inside a baked brick the field IS the family's DE — the
        // check that the field was baked from this family, whatever its DE's quality.
        var cache = new DistanceFieldCache { SynchronousBake = true };
        Render(Calc(fam), cache);
        var f = cache.LastField!;
        Assert.True(f.Complete);
        const int B = DistanceFieldCache.BrickSize;
        double v = f.Voxel, cell = v * B;
        var (x0, y0, z0, _, _, _) = f.GridBox;
        var rng = new Random(11);
        int checkedPoints = 0, bricks = 0;
        for (int n = 0; n < 200_000 && checkedPoints < 4_000; n++)
        {
            int ci = rng.Next(f.GridX), cj = rng.Next(f.GridY), ck = rng.Next(f.GridZ);
            if (f.CellKindAt(x0 + (ci + 0.5) * cell, y0 + (cj + 0.5) * cell, z0 + (ck + 0.5) * cell) != 2) continue;
            bricks++;
            for (int k = 0; k < 4; k++)
            {
                double x = x0 + ci * cell + rng.Next(1, B) * v, y = y0 + cj * cell + rng.Next(1, B) * v, z = z0 + ck * cell + rng.Next(1, B) * v;
                double d = f.TrueDistance(x, y, z), s = f.Sample(x, y, z);
                Assert.True(Math.Abs(s - (float)d) <= 1e-5 * (1 + Math.Abs(d)), $"{fam}: field {s} vs DE {d} at ({x}, {y}, {z})");
                checkedPoints++;
            }
        }
        output.WriteLine($"{fam}: voxel {v:G3}, {checkedPoints} lattice points in {bricks} bricks equal the DE");
        Assert.True(checkedPoints >= 1_000, $"{fam}: only {checkedPoints} lattice points in bricks");
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_Frame_Without_A_Field_Is_The_True_Frame(Family fam)
    {
        var exact = Render(Calc(fam), null);
        // The bake not ready yet (background): the true frame.
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
        // Thin-lens DoF marches the DE.
        void Dof(FractalParameters fp) { var l = fp.Lighting; l.DofThinLens = true; l.DofAperture = 0.05; l.DofSamples = 4; fp.Lighting = l; }
        var exactDof = Render(Calc(fam, tweak: Dof), null);
        var dofCache = new DistanceFieldCache { SynchronousBake = true };
        Assert.Equal(exactDof, Render(Calc(fam, tweak: Dof), dofCache));
        Assert.False(dofCache.LastFrameApproximate);
    }

    [Theory]
    [MemberData(nameof(Unwired))]
    public void The_Other_Families_March_Their_De(Family fam)
    {
        var exact = Render(Calc(fam), null);
        var cache = new DistanceFieldCache { SynchronousBake = true };
        Assert.Equal(exact, Render(Calc(fam), cache));
        Assert.False(cache.LastFrameApproximate);
        Assert.Null(cache.LastField);
    }

    // ── The wrapper the families march through ────────────────────────────────

    private readonly struct Sphere : IDistanceEstimator
    {
        public double Evaluate(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z) - 1.0;
    }

    [Fact]
    public void The_Wrapper_Reads_The_Field_While_It_Holds_One_And_The_De_Otherwise()
    {
        var de = new Sphere();
        var cache = new DistanceFieldCache { SynchronousBake = true };
        FieldOrDe<Sphere> with, without;
        using (DistanceFieldCache.Use(cache))
        {
            with = DistanceFieldCache.For(in de, 2.0, 1e-3, 0, 0, 5, 5, 0.004);
            without = DistanceFieldCache.For(in de, 2.0, 1e-3, 0, 0, 5, 5, 0.004, usable: false);
        }
        var field = with.Field;
        Assert.NotNull(field);
        Assert.Null(without.Field);
        var rng = new Random(5);
        int differ = 0;
        for (int i = 0; i < 2000; i++)
        {
            double x = rng.NextDouble() * 4 - 2, y = rng.NextDouble() * 4 - 2, z = rng.NextDouble() * 4 - 2;
            Assert.Equal(field!.Sample(x, y, z), with.Evaluate(x, y, z));
            Assert.Equal(de.Evaluate(x, y, z), without.Evaluate(x, y, z));
            if (with.Evaluate(x, y, z) != de.Evaluate(x, y, z)) differ++;
        }
        Assert.True(differ > 1000, "precondition: the field is not the DE bit for bit");
        // The normals' step: half a voxel on the field (smooth preview normals), else the family's own.
        Assert.Equal(0.5 * field!.Voxel, with.NormalStep(1e-6));
        Assert.Equal(field.Voxel, with.NormalStep(field.Voxel));
        Assert.Equal(1e-6, without.NormalStep(1e-6));
        // No cache installed: the DE.
        Assert.Null(DistanceFieldCache.For(in de, 2.0, 1e-3, 0, 0, 5, 5, 0.004).Field);
    }

    // ── Part 4: the level from the hit distances ──────────────────────────────

    [Fact]
    public void Hits_Set_The_Next_Frames_View_Distance_For_That_Fractal()
    {
        Func<double, double, double, double> sphere = (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - 1.0;
        const double half = 2.0, pixelAngle = 0.002;
        var cache = new DistanceFieldCache { SynchronousBake = true };
        cache.BeginRender();
        var far = cache.Acquire(7, 0, 0, 0, half, 1e-3, 0, 0, 10, 10, pixelAngle, sphere);
        Assert.Equal(DistanceFieldCache.LevelFor(half, 10, pixelAngle), far?.Level ?? DistanceFieldCache.LevelFor(half, 10, pixelAngle));
        Assert.Equal(0, cache.ViewDistanceHint(7));
        // Hits at 1.5 .. 9: the nearer quartile is the hint.
        var hits = Enumerable.Range(0, 40).Select(i => 1.5 + i * 0.2).ToArray();
        foreach (double t in hits.Reverse()) cache.ObserveHit(t);
        cache.ObserveHit(double.NaN); cache.ObserveHit(-1); cache.ObserveHit(double.PositiveInfinity);   // ignored
        cache.BeginRender();
        double expected = hits.OrderBy(t => t).ElementAt(hits.Length / 4);
        Assert.Equal(expected, cache.ViewDistanceHint(7));
        Assert.Equal(0, cache.ViewDistanceHint(8));   // another fractal has none
        var near = cache.Acquire(7, 0, 0, 0, half, 1e-3, 0, 0, 10, 10, pixelAngle, sphere);
        Assert.NotNull(near);
        Assert.Equal(DistanceFieldCache.LevelFor(half, expected, pixelAngle), near!.Level);
        Assert.True(near.Level > DistanceFieldCache.LevelFor(half, 10, pixelAngle), "precondition: the hint picks a finer level");

        // Fewer than the minimum: the hint stays; a frame of another fractal replaces it.
        for (int i = 0; i < 5; i++) cache.ObserveHit(9.0);
        cache.BeginRender();
        Assert.Equal(expected, cache.ViewDistanceHint(7));
        cache.Acquire(8, 0, 0, 0, half, 1e-3, 0, 0, 10, 10, pixelAngle, sphere);
        for (int i = 0; i < 20; i++) cache.ObserveHit(3.0);
        cache.BeginRender();
        Assert.Equal(0, cache.ViewDistanceHint(7));
        Assert.Equal(3.0, cache.ViewDistanceHint(8));
    }

    [Theory]
    [InlineData("Mandelbulb")]
    [InlineData("Mandelbox")]
    [InlineData("KIFS")]
    public void Families_Report_Their_Hits(string fam)
    {
        // KIFS at 2x the size: its default sponge leaves only 9 sampled hits at 128x96.
        IFractalCalculator c = fam == "KIFS"
            ? new KifsCalculator(2 * W, 2 * H) { FractalParameters = new FractalParameters { Lighting = Fx() }, ColorMap = ColorPalette.BuiltIns[0] }
            : Calc(fam == "Mandelbulb" ? Family.Mandelbulb : Family.Mandelbox);
        var cache = new DistanceFieldCache { SynchronousBake = true };
        cache.BeginRender();
        using (DistanceFieldCache.Use(cache)) c.Calculate(CancellationToken.None);
        long key = cache.LastField!.DeKey;
        cache.BeginRender();   // closes the frame: its hits become the hint
        double hint = cache.ViewDistanceHint(key);
        output.WriteLine($"{fam}: {cache.LastFrameHitSamples} hit samples, view-distance hint {hint:G4}");
        Assert.True(hint > 0 && double.IsFinite(hint), $"{fam}: no hits reported ({hint})");
        if (fam == "Mandelbulb")
        {
            // The bulb lies within radius 1.5 of the origin the camera looks at, and the
            // nearer quartile of its front-surface hits is nearer than the origin.
            double camDist = new FractalParameters().BulbCameraDistance;
            Assert.InRange(hint, camDist - 1.5, camDist);
        }
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
        var state = new FractalViewState { FractalType = FractalType.Mandelbulb, FractalParameters = fp, Zoom = 1.0 };
        using var host = new FractalRenderHost(renderer, state, W, H, ColorPalette.BuiltIns[0]) { DistanceFieldCache3D = on };
        host.DistanceField3DCache.SynchronousBake = true;
        using var done = new SemaphoreSlim(0);
        host.FrameCompleted += (_, _) => done.Release();
        host.Trigger();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no first frame");
        while (done.Wait(TimeSpan.FromSeconds(3))) { }   // the frame, its later stages and any settle
        fp.BulbCameraTheta += 0.05;
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
    }
}
