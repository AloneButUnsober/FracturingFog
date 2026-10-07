// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1112 / GPU parity plan G2.5 — every User Bulb DE the CPU uses, on the GPU,
// except the scalar KIFS and non-escaping DEs. The sandbox kernel gains the Vec3
// numerical-Jacobian DE (Julia included; twin of UserBulbCalculator.UserBulbDE) and
// the exact full-derivative quaternion q²+c DE (twin of UserBulbQuatExactDE). Before,
// those rendered on the CPU: Vec3 numerical / Julia always (the slowest User Bulb
// renders, #1104), and the exact quaternion DE whenever DE mode was Analytic.
//
// The CPU frame is the reference (ILGPU CPU accelerator). Each case must run on the
// GPU route and match the CPU frame; and its DE must reach the kernel: the GPU frame
// is far from the CPU frame drawn with the OTHER DE for the same source.

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

public sealed class S1112UserBulbGpuDeTests
{
    private const int W = 64, H = 48;

    private static (uint[] color, GpuRoute route) Render(bool gpu, string source, bool quat,
        UserBulbDEModeKind mode, bool julia)
    {
        var fx = LightingFxData.CreateDefault();
        fx.ShadowSteps = 16; fx.ShadowLightMask = 0x1; fx.AoSamples = 3; fx.AoStrength = 0.5;
        var fp = new FractalParameters
        {
            UserBulbSource = source,
            UserBulbAxisMode = quat ? UserBulbAxisModeKind.Quat : UserBulbAxisModeKind.Vec3,
            UserBulbCompiler = UserBulbCompilerKind.Sandbox,
            UserBulbBackend = gpu ? UserBulbBackendKind.GPU : UserBulbBackendKind.CPU,
            UserBulbDEMode = mode,
            UserBulbJuliaMode = julia,
            UserBulbIterations = 8,
            UserBulbMaxSteps = 96,
            UserBulbTemporalReuse = false,
            Lighting = fx,
        };
        var calc = new UserBulbCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[39], FractalParameters = fp };
        calc.Calculate(CancellationToken.None);
        return ((uint[])calc.ColorBuffer.Clone(), calc.LastGpuRoute);
    }

    private static double MeanDrift(uint[] a, uint[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            for (int s = 0; s < 24; s += 8)
                sum += Math.Abs((int)((a[i] >> s) & 0xFF) - (int)((b[i] >> s) & 0xFF));
        return sum / (a.Length * 3.0);
    }

    // (label, source, quat, DE mode, julia; the contrast CPU frame: source, DE mode, julia)
    public static TheoryData<string, string, bool, UserBulbDEModeKind, bool, string, UserBulbDEModeKind, bool> Cases() => new()
    {
        // Analytic vs numerical DE on the same source — different surfaces.
        { "vec numerical (Auto rejects analytic)", "z^8 + c", false, UserBulbDEModeKind.Auto, false, "z^8 + c", UserBulbDEModeKind.Analytic, false },
        { "vec numerical (explicit)", "z^8 + c", false, UserBulbDEModeKind.Numerical, false, "z^8 + c", UserBulbDEModeKind.Analytic, false },
        // No analytic form exists; the sin term must reach the kernel's Step.
        { "vec transcendental", "z^4 + sin(z)*0.5 + c", false, UserBulbDEModeKind.Numerical, false, "z^4 + c", UserBulbDEModeKind.Numerical, false },
        { "vec Julia", "z^8 + c", false, UserBulbDEModeKind.Numerical, true, "z^8 + c", UserBulbDEModeKind.Numerical, false },
        // The exact q²+c DE vs the numerical Jacobian.
        { "quat exact", "qpow(z, 2) + c", true, UserBulbDEModeKind.Analytic, false, "qpow(z, 2) + c", UserBulbDEModeKind.Numerical, false },
        { "quat Julia exact", "qpow(z, 2) + c", true, UserBulbDEModeKind.Analytic, true, "qpow(z, 2) + c", UserBulbDEModeKind.Numerical, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Gpu_De_Matches_The_Cpu(string label, string source, bool quat, UserBulbDEModeKind mode, bool julia,
        string contrastSource, UserBulbDEModeKind contrastMode, bool contrastJulia)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try
        {
            var g = Render(true, source, quat, mode, julia);
            Assert.True(g.route.State == GpuRouteState.Gpu, $"{label}: not on the GPU ({g.route.Reason}: {g.route.Detail})");
            double same = MeanDrift(g.color, Render(false, source, quat, mode, julia).color);
            Assert.True(same < 1.0, $"{label}: GPU vs CPU mean drift {same:F2} (bound 1.0)");

            // The case's DE reached the kernel: the contrasting CPU frame is far away.
            var other = Render(false, contrastSource, quat, contrastMode, contrastJulia).color;
            double vsOther = MeanDrift(g.color, other);
            Assert.True(vsOther > Math.Max(1.0, 8 * same),
                $"{label}: the GPU frame is as close to the contrasting CPU frame ({vsOther:F2}) as to its own ({same:F2})");
        }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    [Fact]
    public void Scalar_Kifs_And_NonEscaping_Stay_On_The_Cpu_And_Say_So()
    {
        foreach (var (mode, kifs, reason) in new[]
        {
            (UserBulbDEModeKind.NonEscaping, 0.0, "non-escaping DE"),
            (UserBulbDEModeKind.Numerical, 2.0, "scalar KIFS DE"),
        })
        {
            var fp = new FractalParameters
            {
                UserBulbSource = "z^8 + c", UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                UserBulbBackend = UserBulbBackendKind.GPU, UserBulbDEMode = mode, UserBulbKifsScale = kifs,
                Lighting = LightingFxData.CreateDefault(),
            };
            var calc = new UserBulbCalculator(32, 24) { ColorMap = ColorPalette.BuiltIns[39], FractalParameters = fp };
            calc.Calculate(CancellationToken.None);
            Assert.Equal(GpuRouteState.CpuFallback, calc.LastGpuRoute.State);
            Assert.Equal(reason, calc.LastGpuRoute.Reason);
        }
    }
}
