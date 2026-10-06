// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-M (GPU parity plan G0.3) — every GPU-capable calculator records the
// route its last frame took (GPU, or CPU and why) instead of falling back
// silently. Checked here:
//   - the 3D gate helper decides exactly as the old inline gate did (same
//     boolean over a grid of inputs), so routing is unchanged; and the reason
//     it names is the condition that failed;
//   - real calculators report the right state and reason: CPU-only gates,
//     a 3D frame that ran on the ILGPU CPU accelerator, Coquaternion (no
//     kernel), and the 2D Mandelbrot / escape-time paths with no kernel, a
//     working stub kernel, a throwing stub kernel and an unsupported family.

using System;
using System.Linq;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using ILGPU;
using ILGPU.Runtime;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1173MGpuRouteTests
{
    // ── Gate helper ──────────────────────────────────────────────────────

    [Fact]
    public void Gate_Matches_The_Old_Inline_Gate_Over_A_Grid()
    {
        foreach (bool useGpu in new[] { false, true })
        foreach (var aov in new[] { AovView.Beauty, AovView.Normals })
        foreach (bool lowRes in new[] { false, true })
        foreach (int extra in new[] { 0, 1, 2, 3 })   // 0 plain, 1 stereo depth, 2 froxel + thin lens, 3 stereo + thin lens
        foreach (bool familyOk in new[] { true, false })
        {
            var fx = LightingFxData.CreateDefault();
            fx.UseGpuRender = useGpu;
            fx.DebugAov = aov;
            if (extra == 1) { fx.StereoMode = StereoMode.Fake; fx.StereoEyeSeparation = 0.05; }
            if (extra == 2) { fx.Froxel3D = true; fx.FogDensity = 0.05; fx.DofThinLens = true; fx.DofAperture = 0.2; fx.DofSamples = 4; }
            if (extra == 3) { fx.StereoMode = StereoMode.Fake; fx.StereoEyeSeparation = 0.05; fx.DofThinLens = true; fx.DofAperture = 0.2; fx.DofSamples = 4; }

            // #323: AOV views and stereo depth no longer force the CPU; only depth
            // needs with thin-lens DoF do (the extra == 2 and 3 rows).
            bool oldGate = fx.UseGpuRender && !lowRes && extra < 2 && familyOk;
            var route = Gpu3DRoute.Gate(in fx, lowRes, familyOk ? null : "family", "family detail");

            Assert.Equal(oldGate, route is null);
            if (route is { } r)
                Assert.Equal(useGpu ? GpuRouteState.CpuFallback : GpuRouteState.NotRequested, r.State);
        }
    }

    [Fact]
    public void Gate_Names_The_Condition_That_Failed()
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = true;
        Assert.Null(Gpu3DRoute.Gate(in fx, lowRes: false));

        var aov = fx; aov.DebugAov = AovView.Depth;
        Assert.Null(Gpu3DRoute.Gate(in aov, false));   // #323 — AOV views render on the GPU

        Assert.Equal("preview frame", Gpu3DRoute.Gate(in fx, lowRes: true)!.Value.Reason);

        var stereo = fx; stereo.StereoMode = StereoMode.Fake; stereo.StereoEyeSeparation = 0.05;
        Assert.Null(Gpu3DRoute.Gate(in stereo, false));   // #323 — GPU depth feeds stereo
        var stereoLens = stereo; stereoLens.DofThinLens = true; stereoLens.DofAperture = 0.2; stereoLens.DofSamples = 4;
        Assert.Equal("stereo + thin-lens", Gpu3DRoute.Gate(in stereoLens, false)!.Value.Reason);

        var lens = fx; lens.Froxel3D = true; lens.FogDensity = 0.05;
        lens.DofThinLens = true; lens.DofAperture = 0.2; lens.DofSamples = 4;
        Assert.Equal("froxel + thin-lens", Gpu3DRoute.Gate(in lens, false)!.Value.Reason);

        var fam = Gpu3DRoute.Gate(in fx, false, "Octahedron fold", "detail")!.Value;
        Assert.Equal(GpuRouteState.CpuFallback, fam.State);
        Assert.Equal("Octahedron fold", fam.Reason);
        Assert.Equal("detail", fam.Detail);
    }

    [Fact]
    public void Status_Tag_And_Hud_Line()
    {
        Assert.Null(GpuRoute.NotRequested.StatusTag);
        Assert.Equal("[GPU]", GpuRoute.OnGpu("Cuda GT 710").StatusTag);
        Assert.Equal("[CPU: AOV view]", GpuRoute.Cpu("AOV view", "long").StatusTag);
        Assert.Equal("gpu    CPU fallback: long", GpuRoute.Cpu("AOV view", "long").HudLine);
        Assert.Equal("gpu    on: Cuda GT 710", GpuRoute.OnGpu("Cuda GT 710").HudLine);
        Assert.Equal("gpu    off (CPU backend)", GpuRoute.NotRequested.HudLine);
    }

    // ── 3D calculators ───────────────────────────────────────────────────

    private static FractalParameters Params3D(KifsFoldKind fold = KifsFoldKind.Menger)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = true;
        return new FractalParameters { Lighting = fx, KifsFold = fold };
    }

    private static GpuRoute Run3D(Interefaces.IFractalCalculator calc, FractalParameters fp)
    {
        ((dynamic)calc).FractalParameters = fp;
        calc.ColorMap = new HsvPalette();
        calc.Zoom = 1.0;
        calc.Calculate(CancellationToken.None);
        return ((IGpuRouteSource)calc).LastGpuRoute;
    }

    [Fact]
    public void Mandelbox_Preview_Frame_Reports_The_Preview_Gate()
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = true;
        var calc = new MandelboxCalculator(32, 24) { LowResPreview = true };
        var route = Run3D(calc, new FractalParameters { Lighting = fx });
        Assert.Equal(GpuRouteState.CpuFallback, route.State);
        Assert.Equal("preview frame", route.Reason);
    }

    [Fact]
    public void Mandelbox_Cpu_Backend_Is_Not_Requested()
    {
        var route = Run3D(new MandelboxCalculator(32, 24), new FractalParameters { Lighting = LightingFxData.CreateDefault() });
        Assert.Equal(GpuRouteState.NotRequested, route.State);
    }

    [Fact]
    public void Kifs_Fold_Without_A_Kernel_Is_Named()
    {
        var route = Run3D(new KifsCalculator(32, 24), Params3D(fold: KifsFoldKind.Octahedron));
        Assert.Equal(GpuRouteState.CpuFallback, route.State);
        Assert.Equal("Octahedron fold", route.Reason);
        Assert.Contains("#1173-D", route.Detail);
    }

    [Fact]
    public void Coquaternion_Says_It_Has_No_Kernel()
    {
        var on = Run3D(new CoquaternionMandelbrotCalculator(32, 24), Params3D());
        Assert.Equal(GpuRouteState.CpuFallback, on.State);
        Assert.Equal("no GPU kernel", on.Reason);

        var off = Run3D(new CoquaternionMandelbrotCalculator(32, 24),
            new FractalParameters { Lighting = LightingFxData.CreateDefault() });
        Assert.Equal(GpuRouteState.NotRequested, off.State);
    }

    [Fact]
    public void Mandelbox_On_The_Cpu_Accelerator_Reports_The_Device()
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try
        {
            var route = Run3D(new MandelboxCalculator(32, 24), Params3D());
            Assert.Equal(GpuRouteState.Gpu, route.State);
            Assert.StartsWith("CPU ", route.Reason);   // "<AcceleratorType> <name>" of the device that ran it
        }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    // ── 2D paths ─────────────────────────────────────────────────────────

    /// <summary>Minimal IGpuKernel: "renders" every pixel in-set, or throws.</summary>
    private sealed class StubKernel : IGpuKernel
    {
        public bool Throw;
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public string BackendLabel => "Stub";
        public void SetPalette(FracturingFog.Interefaces.IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter,
            double bailout2, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst, int[]? perRowMaxIter = null,
            FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f, uint[]? colorDst = null)
        {
            if (Throw) throw new InvalidOperationException("stub dispatch failure");
            Array.Fill(iterDst, maxIter, 0, width * height);
        }
        public void Dispose() { }
    }

    private static GpuRoute RunMandelbrot(IGpuKernel? kernel, bool useGpu)
    {
        var calc = new MandelbrotCalculator(32, 24)
        {
            ColorMap = new HsvPalette(), Zoom = 1.0, MaxIterations = 64,
            UseGpuCompute = useGpu, GpuKernel = kernel,
        };
        calc.Calculate(CancellationToken.None);
        return calc.LastGpuRoute;
    }

    [Fact]
    public void Mandelbrot_Routes()
    {
        Assert.Equal(GpuRouteState.NotRequested, RunMandelbrot(null, useGpu: false).State);
        Assert.Equal("no GPU kernel", RunMandelbrot(null, useGpu: true).Reason);

        var ok = RunMandelbrot(new StubKernel(), useGpu: true);
        Assert.Equal(GpuRouteState.Gpu, ok.State);
        Assert.Equal("Stub", ok.Reason);

        var bad = RunMandelbrot(new StubKernel { Throw = true }, useGpu: true);
        Assert.Equal(GpuRouteState.CpuFallback, bad.State);
        Assert.Equal("GPU error", bad.Reason);
        Assert.Contains("stub dispatch failure", bad.Detail);
    }

    private static GpuRoute RunEscape(FractalType type, IGpuKernel? kernel)
    {
        var calc = new EscapeTimeCalculator(32, 24)
        {
            FractalType = type, ColorMap = new HsvPalette(), Zoom = 1.0, MaxIterations = 64,
            UseGpuCompute = true, GpuKernel = kernel,
        };
        calc.Calculate(CancellationToken.None);
        return calc.LastGpuRoute;
    }

    [Fact]
    public void EscapeTime_Routes()
    {
        Assert.Equal("no GPU kernel", RunEscape(FractalType.Julia, null).Reason);

        var julia = RunEscape(FractalType.Julia, new StubKernel());
        Assert.Equal(GpuRouteState.Gpu, julia.State);
        Assert.Contains("colouring on the CPU", julia.Detail);   // stub has no GPU palette

        var multi = RunEscape(FractalType.Multibrot, new StubKernel());
        Assert.Equal(GpuRouteState.CpuFallback, multi.State);
        Assert.Equal("Multibrot: no kernel", multi.Reason);
    }
}
