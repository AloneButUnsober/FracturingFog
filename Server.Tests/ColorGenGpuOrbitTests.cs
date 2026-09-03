// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// F16 (#603) — ColorGen orbit-accumulator inputs on the GPU (HLSL).
//
// An orbit ColorGen theme (references trapMin / stripeAvg / …) renders on the
// CPU today because the escape-only kernel doesn't compute the accumulators.
// F16 adds an orbit-accumulating colour kernel, gated behind the opt-in
// InterpretedOrbitColorMap.GpuEnabled (default off, so production is unchanged
// until on-device parity is signed off). These tests pin:
//   • opt-in behaviour — off ⇒ no GPU palette (CPU); on ⇒ body + orbit mask;
//   • the mask reflects exactly the referenced inputs;
//   • the generated kernel accumulates only the mask'd inputs and extends the
//     EvalPalette signature;
//   • (when dxc is present) the generated orbit kernel actually compiles.
//
// Numeric CPU-vs-GPU parity needs a GPU device (the --colorprobe-style gate) and
// is out of scope for a headless unit test.

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ColorGenGpuOrbitTests
{
    private static InterpretedColorMap Make(string src)
    {
        var m = InterpretedColorMap.TryCreate(src, null, out string? err);
        Assert.Null(err);
        return m!;
    }

    // Toggle GpuEnabled for one test, always restoring it.
    private static T WithGpu<T>(bool on, System.Func<T> body)
    {
        bool prev = InterpretedOrbitColorMap.GpuEnabled;
        InterpretedOrbitColorMap.GpuEnabled = on;
        try { return body(); }
        finally { InterpretedOrbitColorMap.GpuEnabled = prev; }
    }

    [Fact]
    public void GpuDisabled_OrbitTheme_AdvertisesNoGpuPalette()
    {
        WithGpu(false, () =>
        {
            var m = (InterpretedOrbitColorMap)Make("return hsv(saturate(trapMin), 0.9, 1.0);");
            Assert.Equal(GpuOrbitInputs.None, m.OrbitInputs);   // ⇒ CPU render
            Assert.Equal("", m.HlslPaletteBody);
            return 0;
        });
    }

    [Fact]
    public void GpuEnabled_OrbitTheme_ExposesBodyAndMask()
    {
        WithGpu(true, () =>
        {
            var m = (InterpretedOrbitColorMap)Make("return hsv(saturate(trapMin), 0.9, 1.0);");
            Assert.Equal(GpuOrbitInputs.TrapMin, m.OrbitInputs);
            Assert.NotEqual("", m.HlslPaletteBody);
            Assert.NotEqual("Interp_none", m.PaletteId);
            Assert.Contains("in_trapMin", m.HlslPaletteBody);   // body reads the input
            return 0;
        });
    }

    [Fact]
    public void GpuEnabled_Mask_ReflectsExactlyReferencedInputs()
    {
        WithGpu(true, () =>
        {
            var m = (InterpretedOrbitColorMap)Make(
                "let k = trapCross*2.0 + trapHexagon; return hsv(saturate(k), 0.9, 1.0);");
            Assert.Equal(GpuOrbitInputs.TrapCross | GpuOrbitInputs.TrapHexagon, m.OrbitInputs);
            return 0;
        });
    }

    [Fact]
    public void NonOrbitTheme_IsNotOrbitPalette_EvenWhenGpuEnabled()
    {
        WithGpu(true, () =>
        {
            var m = Make("return hsv(smooth*0.03, 0.85, 1.0);");
            Assert.False(m is IGpuOrbitPalette);        // base type, escape-only GPU
            Assert.IsAssignableFrom<IGpuHlslPalette>(m);
            Assert.NotEqual("", m.HlslPaletteBody);     // still GPU-capable (escape path)
            return 0;
        });
    }

    [Fact]
    public void BuildColorOrbit_AccumulatesOnlyMaskedInputs()
    {
        // trapMin only: has the trapMin accumulator, extends the signature, and
        // does NOT emit the stripe/hexagon accumulators.
        string hlsl = MandelbrotKernelSource.BuildColorOrbit(
            "", "    return float3(saturate(in_trapMin), 0.0, 0.0);", MandelbrotKernelSource.OrbTrapMin);

        Assert.Contains("float in_trapMin,", hlsl);            // signature extended
        Assert.Contains("acc_trapMin = min(acc_trapMin", hlsl); // trapMin sampled
        Assert.DoesNotContain("acc_stripeSum", hlsl);           // unmasked → absent
        Assert.DoesNotContain("acc_trapHexagon", hlsl);
        Assert.Contains("if (it > 0)", hlsl);                    // per-iteration sample gate
    }

    [Fact]
    public void BuildColorOrbit_MultipleInputs_EmitsEach()
    {
        int mask = MandelbrotKernelSource.OrbStripe | MandelbrotKernelSource.OrbCurvature;
        string hlsl = MandelbrotKernelSource.BuildColorOrbit(
            "", "    return float3(saturate(in_stripeAvg + in_curvature), 0.0, 0.0);", mask);

        Assert.Contains("acc_stripeSum", hlsl);
        Assert.Contains("acc_cvSum", hlsl);      // curvature state machine
        Assert.Contains("cvPrevSegR", hlsl);
        Assert.DoesNotContain("acc_trapMin", hlsl);
    }

    // Strong correctness gate short of a GPU: the generated orbit kernel must be
    // valid HLSL. Compiles to SPIR-V via dxc; SKIPPED (soft) when dxc isn't on
    // the box (headless CI without the Vulkan SDK).
    [Theory]
    [InlineData("return hsv(saturate(trapMin), 0.9, 1.0);")]
    [InlineData("return hsv(saturate(stripeAvg + tiaAvg), 0.9, 1.0);")]
    [InlineData("let k = trapRing + trapHyperbola + trapHexagon; return hsv(saturate(k), 0.9, 1.0);")]
    [InlineData("return hsv(saturate(curvature + lyapunov*0.2 + gaussian + expSmooth), 0.8, 1.0);")]
    public void GeneratedOrbitKernel_CompilesWithDxc(string dsl)
    {
        var map = WithGpu(true, () => (InterpretedOrbitColorMap)Make(dsl));
        Assert.NotEqual(GpuOrbitInputs.None, map.OrbitInputs);

        string hlsl = MandelbrotKernelSource.BuildColorOrbit(
            map.HlslPrelude, map.HlslPaletteBody, (int)map.OrbitInputs);

        byte[] spirv;
        try
        {
            spirv = FracturingFog.Rendering.Vulkan.DxcCompiler.CompileToSpirv(
                hlsl, MandelbrotKernelSource.EntryPoint, "cs_6_0", "-fvk-t-shift", "100", "0",
                "-fvk-u-shift", "200", "0");
        }
        catch (System.Exception ex) when (
            ex is System.IO.FileNotFoundException ||
            ex.Message.Contains("dxc", System.StringComparison.OrdinalIgnoreCase))
        {
            return; // dxc not installed — soft skip.
        }

        Assert.NotNull(spirv);
        Assert.True(spirv.Length > 0, "dxc produced an empty SPIR-V module");
    }
}
