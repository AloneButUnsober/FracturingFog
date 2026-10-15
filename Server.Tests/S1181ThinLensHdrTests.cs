// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1181 (part 2) — thin-lens DoF with tonemap / bloom on the GPU. The kernels'
// lens-tap loops averaged only the packed 8-bit colour; the taps wrote no HDR, so
// tonemap / bloom had nothing to work on and Gpu3DRoute kept the combination on
// the CPU ("thin-lens + tonemap"). Each tap now writes its pre-clamp HDR (NaN on a
// miss) and the loop stores the average, as the CPU ThinLensDof accumulator does.
// Kernels run on the ILGPU CPU accelerator (SetTestOverride). Checked:
//   - per pixel: the averaged HDR agrees with the averaged LDR byte wherever no tap
//     clipped (HDR - 1.5 < LDR <= HDR + 0.5: each tap truncates, the average
//     rounds), and a
//     pixel whose taps all missed stays NaN; the taps' normals are reset;
//   - every GPU family keeps a thin-lens + tonemap frame on the GPU, and the
//     tonemap changes it (it is not dropped);
//   - the GPU thin-lens + tonemap frame lands near the CPU one: much nearer than
//     the untonemapped GPU frame is (the tonemap moved it the way the CPU's did).

using System;
using System.Linq;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering.Lighting;
using ILGPU;
using ILGPU.Runtime;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1181ThinLensHdrTests(ITestOutputHelper output)
{
    private const int W = 48, H = 36;

    private static T WithCpuAccelerator<T>(Func<T> body)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try { return body(); }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    [Fact]
    public void Lens_Taps_Average_The_Hdr_Beauty()
    {
        double fov = Math.Tan(Math.PI / 6);
        var rp = new GpuRaymarchParams
        {
            Width = W, Height = H, CamZ = 2.6, FwdZ = -1, RightX = 1, UpY = 1,
            FovScale = fov, Aspect = (double)W / H, LightX = 0.577, LightY = 0.577, LightZ = 0.577,
            MaxSteps = 200, Eps = 1e-4, InSetColor = 0xFF000000u,
            DofAperture = 0.25, DofFocus = 2.6, DofSamples = 6,
        };
        var bp = new MandelbulbGpuParams { Power = 8, DEIter = 8, Bailout = 2.0, SceneRadius = 12.0 };
        var fx = LightingFxData.CreateDefault();
        var sp = GpuShadingParams.Build(in fx);

        var (color, normal, hdr) = WithCpuAccelerator(() =>
        {
            var gpu = new MandelbulbGpuCalculator();
            var c = new uint[W * H]; var d = new float[W * H];
            var n = new float[3 * W * H]; var h = new float[3 * W * H];
            Assert.True(gpu.Render(c, rp, sp, bp, null, d, default, n, h), gpu.LastError);
            return (c, n, h);
        });

        int finite = 0, nan = 0, agree = 0;
        for (int i = 0; i < W * H; i++)
        {
            Assert.True(normal[3 * i] == 0 && normal[3 * i + 1] == 0 && normal[3 * i + 2] == 0, $"pixel {i}: tap normal left behind");
            float hr = hdr[3 * i], hg = hdr[3 * i + 1], hb = hdr[3 * i + 2];
            if (float.IsNaN(hr) || float.IsNaN(hg) || float.IsNaN(hb)) { nan++; continue; }
            finite++;
            uint c = color[i];
            bool ok = true;
            foreach (var (byteV, h) in new[] { ((c >> 16) & 0xFF, hr), ((c >> 8) & 0xFF, hg), (c & 0xFF, hb) })
                if (h >= 0 && h <= 200 && !(byteV > h - 1.5001 && byteV <= h + 0.5001)) ok = false;
            if (ok) agree++;
            else if (finite - agree < 8) output.WriteLine($"  px {i % W},{i / W}: LDR {(c >> 16) & 0xFF},{(c >> 8) & 0xFF},{c & 0xFF} HDR {hr:F2},{hg:F2},{hb:F2}");
        }
        output.WriteLine($"finite HDR {finite}, NaN {nan}, LDR in (HDR - 1.5, HDR + 0.5] {agree}");
        // Before #1181 every thin-lens pixel kept the NaN the kernel cleared it to.
        Assert.True(finite > W * H / 10, $"only {finite} pixels carry an averaged HDR beauty");
        Assert.True(nan > 0, "no pixel missed with every tap: the view has no background");
        // A tap past 255 clips its byte below its HDR; at this exposure none does.
        Assert.True(agree >= finite * 0.99, $"LDR and averaged HDR disagree on {finite - agree} of {finite} pixels");
    }

    public enum Family { Mandelbulb, Mandelbox, Menger, Sierpinski, QuatJulia, QuatMandelbrot, Kleinian, Bicomplex, Coquaternion }

    private static (uint[] Color, GpuRoute Route) Render(Family fam, bool gpu, bool tonemap, bool thinLens = true)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = gpu;
        fx.SsaoSamples = 0; fx.EdgeStrength = 0; fx.BloomStrength = 0;
        fx.ToneMap = tonemap ? ToneMapOperator.Reinhard : ToneMapOperator.None;
        fx.DofThinLens = thinLens; fx.DofAperture = 0.2; fx.DofSamples = 4; fx.DofFocusDistance = 0.0;
        var fp = new FractalParameters
        {
            Lighting = fx,
            KifsFold = fam == Family.Sierpinski ? KifsFoldKind.Sierpinski : KifsFoldKind.Menger,
        };
        dynamic calc = fam switch
        {
            Family.Mandelbulb     => new MandelbulbCalculator(W, H),
            Family.Mandelbox      => new MandelboxCalculator(W, H),
            Family.Menger         => new KifsCalculator(W, H),
            Family.Sierpinski     => new KifsCalculator(W, H),
            Family.QuatJulia      => new QuatJuliaCalculator(W, H),
            Family.QuatMandelbrot => new QuatMandelbrotCalculator(W, H),
            Family.Kleinian       => new KleinianCalculator(W, H),
            Family.Coquaternion   => new CoquaternionMandelbrotCalculator(W, H),
            _                     => (object)new BicomplexMandelbrotCalculator(W, H),
        };
        calc.ColorMap = ColorPalette.BuiltIns[0];
        calc.FractalParameters = fp;
        calc.Zoom = 1.0;
        calc.Calculate(CancellationToken.None);
        return ((uint[])((uint[])calc.ColorBuffer).Clone(), (GpuRoute)calc.LastGpuRoute);
    }

    private static double MeanDiff(uint[] a, uint[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++)
            for (int sh = 0; sh <= 16; sh += 8)
                s += Math.Abs((int)((a[i] >> sh) & 0xFF) - (int)((b[i] >> sh) & 0xFF));
        return s / (3.0 * a.Length);
    }

    [Theory]
    [InlineData(Family.Mandelbulb)]
    [InlineData(Family.Mandelbox)]
    [InlineData(Family.Menger)]
    [InlineData(Family.Sierpinski)]
    [InlineData(Family.QuatJulia)]
    [InlineData(Family.QuatMandelbrot)]
    [InlineData(Family.Kleinian)]
    [InlineData(Family.Bicomplex)]
    [InlineData(Family.Coquaternion)]
    public void Thin_Lens_With_Tonemap_Stays_On_The_Gpu_And_Tonemaps_Like_The_Cpu(Family fam)
    {
        var (plain, tone, cpuTone, cpuPlain) = WithCpuAccelerator(() =>
            (Render(fam, gpu: true, tonemap: false), Render(fam, gpu: true, tonemap: true),
             Render(fam, gpu: false, tonemap: true), Render(fam, gpu: false, tonemap: false)));

        Assert.Equal(GpuRouteState.Gpu, plain.Route.State);
        Assert.True(tone.Route.State == GpuRouteState.Gpu, $"thin-lens + tonemap left the GPU ({tone.Route.Reason})");
        Assert.Equal(GpuRouteState.NotRequested, cpuTone.Route.State);

        double effect = MeanDiff(tone.Color, plain.Color);   // what the tonemap did on the GPU frame
        double drift = MeanDiff(tone.Color, cpuTone.Color);  // GPU vs CPU, both tonemapped
        double before = MeanDiff(plain.Color, cpuTone.Color); // the GPU frame had the tonemap been dropped
        double lens = MeanDiff(plain.Color, cpuPlain.Color); // the existing thin-lens GPU-vs-CPU drift, no tonemap
        output.WriteLine($"{fam}: tonemap effect {effect:F2}, thin-lens GPU vs CPU tonemapped {drift:F2} / plain {lens:F2} (GPU plain vs CPU tonemapped {before:F2})");
        Assert.True(effect > 1.0, $"{fam}: the tonemap barely changed the thin-lens GPU frame ({effect:F2})");
        Assert.True(drift < before, $"{fam}: the tonemap moved the GPU frame away from the CPU's ({before:F2} -> {drift:F2})");
        // The GPU and CPU lens taps sample the aperture differently (the CPU's tap 0 is
        // the centre ray), so thin-lens frames already differ between the two paths.
        // Tonemapping both must not widen that gap beyond the operator's own effect.
        Assert.True(drift <= lens * 1.5 + 1.0, $"{fam}: thin-lens drift tonemapped {drift:F2} vs untonemapped {lens:F2}");
    }
}
