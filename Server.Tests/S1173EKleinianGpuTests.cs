// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #880 + #1173-E / GPU parity plan G3.4 — the Kleinian GPU kernel serves every
// inversion group, not just the uniform tetrahedral 4-sphere preset: the generator
// list (any count, per-sphere radius) arrives as a buffer, and the #877 rotation
// fold, #878 word / last-generator colouring and #881 under-relaxed stepping run in
// the kernel. Before this, each of those forced the CPU.
//
// The CPU KleinianCalculator is the reference. For each case the GPU frame (ILGPU
// CPU accelerator) must match the CPU frame, and the case's feature must reach the
// pixels: the GPU frame is far from the CPU frame of the baseline (tetrahedral,
// no rotation, smooth colour, plain stepping), so a kernel that ignored the
// generator list or the feature fails.

using System;
using System.Collections.Generic;
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

public sealed class S1173EKleinianGpuTests
{
    private const int W = 64, H = 48;

    private static FractalParameters Baseline(LightingFxData fx) => new() { Lighting = fx };

    /// <summary>Case name → the FractalParameters edit that turns it on.</summary>
    private static readonly Dictionary<string, Action<FractalParameters>> s_cases = new()
    {
        ["Octahedral6"] = p => p.KleinianPreset = KleinianPreset.Octahedral6,
        ["CubeCorner8"] = p => p.KleinianPreset = KleinianPreset.CubeCorner8,
        ["Necklace7"] = p => { p.KleinianPreset = KleinianPreset.NecklaceN; p.KleinianNecklaceCount = 7; },
        // Six separate spheres on a ring, each its own radius — the 5th and 6th are
        // visible, so a kernel that read only four generators fails.
        ["CustomRingMixedRadii"] = p => { p.KleinianPreset = KleinianPreset.Custom; p.KleinianCustomSpheres = Ring(6); },
        ["Rotation"] = p => { p.KleinianRotationAngle = 25.0; p.KleinianRotationAxisX = 1.0; p.KleinianRotationAxisY = 1.0; p.KleinianRotationAxisZ = 0.0; },
        ["WordLength"] = p => p.KleinianColorSource = KleinianColorSource.WordLength,
        ["LastGenerator"] = p => p.KleinianColorSource = KleinianColorSource.LastGenerator,
        ["UnderRelaxed"] = p => p.KleinianDeFactor = 0.6,
        ["RotationWordNecklace"] = p =>
        {
            p.KleinianPreset = KleinianPreset.NecklaceN; p.KleinianNecklaceCount = 9;
            p.KleinianRotationAngle = 15.0; p.KleinianRotationAxisZ = 1.0; p.KleinianRotationAxisY = 0.0;
            p.KleinianColorSource = KleinianColorSource.LastGenerator;
        },
    };

    private static List<KleinianSphereDef> Ring(int count)
    {
        double[] rr = { 0.58, 0.45, 0.55, 0.40, 0.52, 0.47 };
        var ring = new List<KleinianSphereDef>();
        for (int k = 0; k < count; k++)
        {
            double a = 2 * Math.PI * k / 6;
            ring.Add(new KleinianSphereDef(Math.Cos(a), 0.0, Math.Sin(a), rr[k]));
        }
        return ring;
    }

    public static TheoryData<string, bool> Cases()
    {
        var d = new TheoryData<string, bool>();
        foreach (var k in s_cases.Keys) d.Add(k, false);
        d.Add("Rotation", true);
        d.Add("Necklace7", true);
        return d;
    }

    private static LightingFxData Fx(bool gpu, bool features)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = gpu;
        if (features)
        {
            fx.ShadowSteps = 24; fx.ShadowLightMask = 0x1;
            fx.AoSamples = 4; fx.AoStrength = 0.6;
            fx.ReflectionStrength = 0.4; fx.Metallic = 0.3;
            fx.FogDensity = 0.08; fx.VolumeSteps = 24;
        }
        return fx;
    }

    private static (uint[] color, GpuRoute route) Render(string? kase, LightingFxData fx)
    {
        var fp = Baseline(fx);
        if (kase != null) s_cases[kase](fp);
        var c = new KleinianCalculator(W, H)
        {
            ColorMap = ColorPalette.BuiltIns[39],   // HsvPhong3D — bright across the whole smooth range (word values are small)
            Zoom = 1.0,
            FractalParameters = fp,
        };
        c.Calculate(CancellationToken.None);
        return ((uint[])c.ColorBuffer.Clone(), c.LastGpuRoute);
    }

    private static double MeanDrift(uint[] a, uint[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            for (int s = 0; s < 24; s += 8)
                sum += Math.Abs((int)((a[i] >> s) & 0xFF) - (int)((b[i] >> s) & 0xFF));
        return sum / (a.Length * 3.0);
    }

    private static T OnCpuAccelerator<T>(Func<T> body)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try { return body(); }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Gpu_Kleinian_Matches_The_Cpu(string kase, bool features)
    {
        OnCpuAccelerator(() =>
        {
            string label = $"{kase}{(features ? " + shadows/AO/reflections/fog" : "")}";
            var g = Render(kase, Fx(gpu: true, features));
            Assert.Equal(GpuRouteState.Gpu, g.route.State);
            double same = MeanDrift(g.color, Render(kase, Fx(gpu: false, features)).color);
            Assert.True(same < 1.0, $"{label}: GPU vs CPU mean drift {same:F2} (bound 1.0)");

            double vsBaseline = MeanDrift(g.color, Render(null, Fx(gpu: false, features)).color);
            Assert.True(vsBaseline > Math.Max(0.5, 8 * same),
                $"{label}: the GPU frame is as close to the baseline tetrahedral frame ({vsBaseline:F2}) as to its own case ({same:F2})");
            return 0;
        });
    }

    [Fact]
    public void Gpu_Reads_Every_Generator_Not_Just_Four()
    {
        // The pre-#880 kernel held exactly four spheres. The six-sphere ring on the GPU
        // must be far from the CPU's first-four-spheres ring, not just near its own.
        OnCpuAccelerator(() =>
        {
            var g = Render("CustomRingMixedRadii", Fx(gpu: true, features: false));
            Assert.Equal(GpuRouteState.Gpu, g.route.State);
            double same = MeanDrift(g.color, Render("CustomRingMixedRadii", Fx(gpu: false, features: false)).color);
            var four = Baseline(Fx(gpu: false, features: false));
            four.KleinianPreset = KleinianPreset.Custom;
            four.KleinianCustomSpheres = Ring(4);
            var c = new KleinianCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[39], Zoom = 1.0, FractalParameters = four };
            c.Calculate(CancellationToken.None);
            double vsFour = MeanDrift(g.color, c.ColorBuffer);
            Assert.True(vsFour > Math.Max(0.5, 8 * same),
                $"the six-sphere GPU frame is as close to the four-sphere CPU frame ({vsFour:F2}) as to its own ({same:F2})");
            return 0;
        });
    }

    [Fact]
    public void Baseline_Tetrahedral_Still_Matches()
    {
        // The pre-#880 GPU case (uniform 4-sphere group) through the new buffer path.
        OnCpuAccelerator(() =>
        {
            var g = Render(null, Fx(gpu: true, features: false));
            Assert.Equal(GpuRouteState.Gpu, g.route.State);
            double same = MeanDrift(g.color, Render(null, Fx(gpu: false, features: false)).color);
            Assert.True(same < 1.5, $"tetrahedral GPU vs CPU drift {same:F2}");
            return 0;
        });
    }

    [Fact]
    public void Packed_Generators_And_Colour_Source_Codes()
    {
        var gens = KleinianGroup.FromPreset(KleinianPreset.NecklaceN, 1.0, 5, 16).ToArray();
        double[] buf = KleinianGpuCalculator.PackGenerators(gens);
        Assert.Equal(4 * gens.Length, buf.Length);
        for (int k = 0; k < gens.Length; k++)
        {
            Assert.Equal(gens[k].Cx, buf[4 * k]); Assert.Equal(gens[k].Cy, buf[4 * k + 1]);
            Assert.Equal(gens[k].Cz, buf[4 * k + 2]); Assert.Equal(gens[k].R, buf[4 * k + 3]);
        }
        // The kernel branches on these integer codes.
        Assert.Equal(0, (int)KleinianColorSource.Smooth);
        Assert.Equal(1, (int)KleinianColorSource.WordLength);
        Assert.Equal(2, (int)KleinianColorSource.LastGenerator);
    }
}
