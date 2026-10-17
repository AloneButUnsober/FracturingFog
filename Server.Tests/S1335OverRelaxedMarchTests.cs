// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1335 — over-relaxed sphere tracing (FractalParameters.MarchRelaxation, the
// shared SphereRelax) on the DE raymarchers' primary rays, CPU and GPU.
//   • SphereRelax against an exact signed distance field (spheres, a plane, a
//     box, a torus), whose first intersection is known: a relaxed march never
//     passes the true surface (the plain march cannot either) and ends on it to
//     within the hit epsilon on the same rays. On the exact field it saves no
//     steps (a true distance leaves nothing to over-relax); on a conservative one
//     (half the distance, as fractal DEs underestimate) it takes far fewer. ω = 1
//     is the plain loop bit for bit. Without the back-off a relaxed march passes
//     the surface (the mutation the back-off exists for).
//   • Every family that honours it (CPU): with a step cap high enough that neither
//     march runs out, ω = 1.5 finds the plain march's surface — the same hit / miss
//     pixels and the same hit distance (published depth) to within a few hit
//     epsilons, bar a handful of silhouette pixels (these DEs are not strict
//     distance bounds). Precondition: the frame hits enough pixels, and ω reached
//     the march (the step-based colour moved).
//   • The GPU kernels (ILGPU CPU accelerator) relax in lockstep with the CPU: their
//     ω = 1.5 frame agrees with the CPU ω = 1.5 frame as closely as at ω = 1.
//   • Region snapshot / --param / Command builder carry it for those families only.
//   • Frame time, plain vs relaxed (reported).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using FracturingFog.Batch;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using ILGPU;
using ILGPU.Runtime;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1335OverRelaxedMarchTests(ITestOutputHelper output)
{
    // ── SphereRelax on exact SDFs ────────────────────────────────────────────

    private static double Sdf(double x, double y, double z)
    {
        double s1 = Math.Sqrt((x - 0.6) * (x - 0.6) + y * y + z * z) - 0.5;
        double s2 = Math.Sqrt((x + 0.7) * (x + 0.7) + (y - 0.2) * (y - 0.2) + (z + 0.3) * (z + 0.3)) - 0.35;
        double plane = y + 0.9;
        double qx = Math.Abs(x) - 0.25, qy = Math.Abs(y - 0.6) - 0.15, qz = Math.Abs(z - 0.5) - 0.25;
        double box = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2) + Math.Pow(Math.Max(qz, 0), 2))
                   + Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0);
        double tx = Math.Sqrt((x + 0.1) * (x + 0.1) + (z + 0.9) * (z + 0.9)) - 0.4;
        double torus = Math.Sqrt(tx * tx + (y + 0.4) * (y + 0.4)) - 0.12;
        return Math.Min(Math.Min(Math.Min(s1, s2), Math.Min(plane, box)), torus);
    }

    /// <summary>The first t where Sdf ≤ 0 along the ray, by fine stepping + bisection.</summary>
    private static double TrueHit(double ox, double oy, double oz, double dx, double dy, double dz, double tMax)
    {
        const double h = 1e-3;
        double prev = 0;
        for (double t = h; t <= tMax; t += h)
        {
            if (Sdf(ox + dx * t, oy + dy * t, oz + dz * t) <= 0)
            {
                double lo = prev, hi = t;
                for (int k = 0; k < 60; k++) { double m = 0.5 * (lo + hi); if (Sdf(ox + dx * m, oy + dy * m, oz + dz * m) <= 0) hi = m; else lo = m; }
                return hi;
            }
            prev = t;
        }
        return double.PositiveInfinity;
    }

    private static (bool Hit, double T, int Steps) March(double ox, double oy, double oz, double dx, double dy, double dz,
                                                        double omega, double eps, double tMax, bool noBackOff = false, double fieldScale = 1.0)
    {
        var relax = new SphereRelax(omega);
        double px = ox, py = oy, pz = oz, t = 0;
        for (int step = 0; step < 100_000; step++)
        {
            double d = Sdf(px, py, pz) * fieldScale;
            double back = noBackOff ? 0 : relax.BackOff(d);
            if (back > 0) { px -= dx * back; py -= dy * back; pz -= dz * back; t -= back; continue; }
            if (d < eps) return (true, t, step + 1);
            if (t > tMax) return (false, t, step + 1);
            double s = relax.Step(d);
            px += dx * s; py += dy * s; pz += dz * s; t += s;
        }
        return (false, t, 100_000);
    }

    private static IEnumerable<(double ox, double oy, double oz, double dx, double dy, double dz)> Rays()
    {
        var rng = new Random(1335);
        for (int i = 0; i < 4000; i++)
        {
            double th = rng.NextDouble() * 2 * Math.PI, ph = 0.3 + rng.NextDouble() * 1.2;
            double ox = 3 * Math.Sin(ph) * Math.Cos(th), oy = 3 * Math.Cos(ph), oz = 3 * Math.Sin(ph) * Math.Sin(th);
            // aim near the origin with a spread, so many rays graze or just miss
            double ax = (rng.NextDouble() - 0.5) * 2.4 - ox, ay = (rng.NextDouble() - 0.5) * 2.4 - oy, az = (rng.NextDouble() - 0.5) * 2.4 - oz;
            double l = Math.Sqrt(ax * ax + ay * ay + az * az);
            yield return (ox, oy, oz, ax / l, ay / l, az / l);
        }
    }

    [Theory]
    [InlineData(1.3, 1.0)]
    [InlineData(1.6, 1.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(1.3, 0.5)]
    [InlineData(1.6, 0.5)]
    [InlineData(2.0, 0.5)]
    public void Relaxed_March_Never_Passes_The_Surface_And_Saves_Steps_On_A_Conservative_Field(double omega, double fieldScale)
    {
        const double Eps = 1e-3, TMax = 8;
        long plainSteps = 0, relaxedSteps = 0;
        int hits = 0, far = 0;
        foreach (var (ox, oy, oz, dx, dy, dz) in Rays())
        {
            double truth = TrueHit(ox, oy, oz, dx, dy, dz, TMax + 1);
            var p = March(ox, oy, oz, dx, dy, dz, 1.0, Eps, TMax, fieldScale: fieldScale);
            var r = March(ox, oy, oz, dx, dy, dz, omega, Eps, TMax, fieldScale: fieldScale);
            plainSteps += p.Steps; relaxedSteps += r.Steps;
            // never past the first intersection (an exact SDF: plain cannot either)
            Assert.True(p.T <= truth + 1e-9 && r.T <= truth + 1e-9, $"ω {omega}: t {r.T} past the surface at {truth}");
            Assert.Equal(p.Hit, r.Hit);
            if (!r.Hit) continue;
            hits++;
            // ends within the hit epsilon of the surface, where the plain march does
            // (a grazing ray can stop near a tangent it never crosses — both do)
            Assert.True(Sdf(ox + dx * r.T, oy + dy * r.T, oz + dz * r.T) * fieldScale < Eps);
            if (Math.Abs(r.T - p.T) > 0.01) far++;
        }
        output.WriteLine($"ω {omega}, field × {fieldScale}: {hits} hits of 4000 rays, {far} > 0.01 from the plain hit, steps {relaxedSteps} vs plain {plainSteps} ({100.0 * relaxedSteps / plainSteps:F1}%)");
        Assert.True(hits > 1000, "precondition: too few hits");
        Assert.True(far <= hits / 200, $"ω {omega}: {far} hits away from the plain march's");
        // the exact field leaves nothing to gain (no step bound: it may cost a few %);
        // the conservative one is where relaxation pays
        if (fieldScale < 1.0)
            Assert.True(relaxedSteps < (omega >= 2.0 ? 0.95 : 0.85) * plainSteps, $"ω {omega}: {relaxedSteps} steps vs plain {plainSteps}");   // ω 2 overshoots often
        else
            Assert.True(relaxedSteps < 1.25 * plainSteps, $"ω {omega}: {relaxedSteps} steps vs plain {plainSteps} on the exact field");
    }

    [Fact]
    public void Omega_One_Is_The_Plain_Loop_Bit_For_Bit()
    {
        foreach (var (ox, oy, oz, dx, dy, dz) in Rays().Take(500))
        {
            var r = March(ox, oy, oz, dx, dy, dz, 1.0, 1e-3, 8);
            // the pre-#1335 loop
            double px = ox, py = oy, pz = oz, t = 0; int steps = 0; bool hit = false;
            for (int step = 0; step < 100_000; step++)
            {
                double d = Sdf(px, py, pz);
                steps = step + 1;
                if (d < 1e-3) { hit = true; break; }
                if (t > 8) break;
                px += dx * d; py += dy * d; pz += dz * d; t += d;
            }
            Assert.Equal(hit, r.Hit);
            Assert.Equal(BitConverter.DoubleToInt64Bits(t), BitConverter.DoubleToInt64Bits(r.T));
            Assert.Equal(steps, r.Steps);
        }
        Assert.Equal(1.0, SphereRelax.Clamp(0.5));
        Assert.Equal(1.0, SphereRelax.Clamp(double.NaN));
        Assert.Equal(SphereRelax.MaxOmega, SphereRelax.Clamp(9));
    }

    [Fact]
    public void Without_The_Back_Off_A_Relaxed_March_Passes_The_Surface()
    {
        int passed = 0;
        foreach (var (ox, oy, oz, dx, dy, dz) in Rays())
        {
            double truth = TrueHit(ox, oy, oz, dx, dy, dz, 9);
            var r = March(ox, oy, oz, dx, dy, dz, 1.6, 1e-3, 8, noBackOff: true);
            if (r.T > truth + 1e-6) passed++;
        }
        Assert.True(passed > 100, $"only {passed} rays passed the surface without the back-off");
    }

    // ── The calculators ──────────────────────────────────────────────────────

    private const int W = 160, H = 120;

    private sealed class NullRenderer : FracturingFog.IFractalRenderer
    {
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static readonly FractalType[] Families = Enum.GetValues<FractalType>()
        .Where(MarchRelaxationCapabilities.UsesMarchRelaxation).ToArray();

    public static TheoryData<FractalType> FamilyData()
    {
        var d = new TheoryData<FractalType>();
        foreach (var t in Families) d.Add(t);
        return d;
    }

    private sealed record Frame(uint[] Color, float[] Depth, double Ms, GpuRoute Route);

    private static readonly string[] StepCaps =
    {
        nameof(FractalParameters.BulbMaxSteps), nameof(FractalParameters.MandelboxMaxSteps),
        nameof(FractalParameters.KifsMaxSteps), nameof(FractalParameters.QJuliaMaxSteps),
        nameof(FractalParameters.QMandelMaxSteps), nameof(FractalParameters.BicomplexMaxSteps),
        nameof(FractalParameters.CoquaternionMaxSteps), nameof(FractalParameters.DualOrbitVolumeMaxSteps),
    };

    private static Frame Render(FractalType type, double omega, bool gpu = false, int stepCap = 0, int w = W, double zoom = 1.0)
    {
        using var host = new FractalRenderHost(new NullRenderer(), new FractalViewState(), w, w * 3 / 4, ColorPalette.BuiltIns[0]);
        var select = typeof(FractalRenderHost).GetMethod("SelectAltCalculatorByType", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var c = (IFractalCalculator)select.Invoke(host, new object[] { type })!;
        c.Resize(w, w * 3 / 4);
        c.CenterX = 0; c.CenterY = 0; c.Zoom = zoom; c.MaxIterations = 64;
        c.ColorMap = ColorPalette.BuiltIns[0];
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = StereoMode.Fake;          // publishes the hit distance (IDepthAovSource)
        fx.StereoEyeSeparation = 0.02;
        fx.UseGpuRender = gpu;
        var fp = new FractalParameters { Lighting = fx, MarchRelaxation = omega };
        if (stepCap > 0)
            foreach (var name in StepCaps) typeof(FractalParameters).GetProperty(name)!.SetValue(fp, stepCap);
        c.GetType().GetProperty("FractalParameters")!.SetValue(c, fp);
        var sw = Stopwatch.StartNew();
        c.Calculate(CancellationToken.None);
        sw.Stop();
        var depth = ((IDepthAovSource)c).DepthBuffer ?? throw new InvalidOperationException($"{type}: no depth");
        var route = c.GetType().GetProperty("LastGpuRoute")?.GetValue(c) is GpuRoute r ? r : default;
        return new Frame((uint[])c.ColorBuffer.Clone(), (float[])depth.Clone(), sw.Elapsed.TotalMilliseconds, route);
    }

    private sealed record Diff(int HitA, int HitB, int HitMiss, int Both, double P50, double P99, double Max, int ColourChanged);

    private static Diff Compare(Frame a, Frame b)
    {
        int n = a.Depth.Length, hitA = 0, hitB = 0, hm = 0, cc = 0;
        var dt = new List<double>();
        for (int i = 0; i < n; i++)
        {
            bool ha = float.IsFinite(a.Depth[i]), hb = float.IsFinite(b.Depth[i]);
            if (ha) hitA++;
            if (hb) hitB++;
            if (ha && hb) dt.Add(Math.Abs(a.Depth[i] - b.Depth[i]));
            else if (ha != hb) hm++;
            if (a.Color[i] != b.Color[i]) cc++;
        }
        dt.Sort();
        double P(double q) => dt.Count == 0 ? 0 : dt[(int)Math.Min(dt.Count - 1, q * dt.Count)];
        return new Diff(hitA, hitB, hm, dt.Count, P(0.5), P(0.99), P(1.0), cc);
    }

    [Theory]
    [MemberData(nameof(FamilyData))]
    public void Relaxed_Frame_Finds_The_Plain_Surface(FractalType type)
    {
        var plain = Render(type, 1.0, stepCap: 2048);
        var relaxed = Render(type, 1.5, stepCap: 2048);
        var d = Compare(plain, relaxed);
        int n = W * H;
        output.WriteLine($"{type}: hits {d.HitA}/{d.HitB}, hit≠miss {d.HitMiss}, |Δt| median {d.P50:E2} p99 {d.P99:E2} max {d.Max:E2}, colour changed {d.ColourChanged}");
        Assert.True(d.HitA > n / 20, $"{type}: precondition — only {d.HitA} hit pixels");
        Assert.True(d.ColourChanged > 0, $"{type}: ω 1.5 left the frame unchanged (did it reach the march?)");
        // the same pixels hit (bar a few silhouette pixels: these DEs are not strict bounds)
        Assert.True(d.HitMiss <= Math.Max(4, n / 1000), $"{type}: {d.HitMiss} pixels hit in one march only");
        // the same surface: a hit epsilon or two typically, grazing faces a little more
        Assert.True(d.P50 < 3e-3, $"{type}: |Δt| median {d.P50}");
        Assert.True(d.P99 < 0.03, $"{type}: |Δt| p99 {d.P99}");
    }

    private static T OnCpuAccelerator<T>(Func<T> body)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(dv => dv.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try { return body(); }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    // Julibrot is CPU-only (no GPU kernel).
    public static TheoryData<FractalType> GpuFamilyData()
    {
        var d = new TheoryData<FractalType>();
        foreach (var t in Families.Where(t => t != FractalType.Julibrot)) d.Add(t);
        return d;
    }

    [Theory]
    [MemberData(nameof(GpuFamilyData))]
    public void Gpu_Kernel_Relaxes_In_Lockstep_With_The_Cpu(FractalType type)
    {
        var (g1, g15) = OnCpuAccelerator(() => (Render(type, 1.0, gpu: true), Render(type, 1.5, gpu: true)));
        Assert.Equal(GpuRouteState.Gpu, g15.Route.State);
        var c1 = Render(type, 1.0);
        var c15 = Render(type, 1.5);
        var plainGap = Compare(c1, g1);
        var relaxedGap = Compare(c15, g15);
        var gpuChange = Compare(g1, g15);
        output.WriteLine($"{type}: CPU↔GPU ω1 hit≠miss {plainGap.HitMiss} p99 {plainGap.P99:E2}; ω1.5 hit≠miss {relaxedGap.HitMiss} p99 {relaxedGap.P99:E2}; GPU ω1→1.5 colour changed {gpuChange.ColourChanged}");
        Assert.True(gpuChange.ColourChanged > 0, $"{type}: the GPU kernel ignored ω");
        Assert.True(relaxedGap.HitMiss <= plainGap.HitMiss + Math.Max(4, W * H / 1000), $"{type}: CPU/GPU hit≠miss {relaxedGap.HitMiss} relaxed vs {plainGap.HitMiss} plain");
        Assert.True(relaxedGap.P99 <= Math.Max(2 * plainGap.P99, 1e-3), $"{type}: CPU/GPU |Δt| p99 {relaxedGap.P99} relaxed vs {plainGap.P99} plain");
    }

    [Fact]
    public void Frame_Time_Plain_Vs_Relaxed()
    {
        foreach (double zoom in new[] { 1.0, 4.0 })
            foreach (var type in Families)
            {
                Render(type, 1.0, w: 480, zoom: zoom);
                double Best(double om) => Enumerable.Range(0, 3).Min(_ => Render(type, om, w: 480, zoom: zoom).Ms);
                double t1 = Best(1.0), t13 = Best(1.3), t16 = Best(1.6);
                output.WriteLine($"zoom {zoom} {type,-22} plain {t1,6:F0} ms | ω 1.3 {100 * t13 / t1,5:F1}% | ω 1.6 {100 * t16 / t1,5:F1}%");
            }
    }

    // ── Region snapshot / --param / Command builder ─────────────────────────

    [Fact]
    public void Region_Snapshot_Carries_It_For_The_Relaxed_Families_Only()
    {
        foreach (var type in Enum.GetValues<FractalType>())
        {
            var p = new FractalParameters { MarchRelaxation = 1.4 };
            var snap = RegionFractalParams.Snapshot(type, p);
            bool owns = RegionFractalParams.FamilyProperties(type).Any(pi => pi.Name == nameof(FractalParameters.MarchRelaxation));
            if (MarchRelaxationCapabilities.UsesMarchRelaxation(type))
            {
                Assert.Equal(1.4, snap?.MarchRelaxation);
                Assert.True(owns, $"{type}: recall would not reset MarchRelaxation");
                Assert.Null(RegionFractalParams.Snapshot(type, new FractalParameters())?.MarchRelaxation);
            }
            else
            {
                Assert.Null(snap?.MarchRelaxation);
                Assert.False(owns, $"{type} claims MarchRelaxation");
            }
        }
        Assert.False(MarchRelaxationCapabilities.UsesMarchRelaxation(FractalType.UserBulb));
        Assert.False(MarchRelaxationCapabilities.UsesMarchRelaxation(FractalType.Kleinian));
    }

    [Fact]
    public void Live_View_Round_Trips_Through_The_Command_Builder_And_Param()
    {
        var p = new FractalParameters { MarchRelaxation = 1.45 };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbox, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.Mandelbox, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        Assert.Contains(report.Args, a => a.StartsWith("MarchRelaxation=", StringComparison.Ordinal));
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.Mandelbox, out var perr)!.ApplyTo(fresh);
        Assert.Null(perr);
        Assert.Equal(1.45, fresh.MarchRelaxation);
        // a lone --param, as typed
        var lone = RegionFractalParams.FromKeyValues(new[] { new KeyValuePair<string, string>("MarchRelaxation", "1.3") }, FractalType.Mandelbulb, out perr)!;
        var q = new FractalParameters();
        lone.ApplyTo(q);
        Assert.Equal(1.3, q.MarchRelaxation);
        Assert.Equal(1.3, q.Clone().MarchRelaxation);
    }
}
