// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S8 GPU 3D lights slice 4 (#488) — UserBulb GPU + retire force-CPU. #488 taught the
// then single-light UserBulb GPU shade to resolve a point / spot Light1 and lifted the
// last !HasPositionalLight force-CPU guard. Since #1173-A the UserBulb GPU kernel is
// the family kernels' shading (all three lights, positional included); see
// S1173AUserBulbGpuShadingTests for the GPU-vs-CPU parity.
//
// On-device output is validated by user smoke; the executable lock is that with
// UserBulbBackend.GPU a point light beside the surface produces a different image
// than the same light far away — the positional resolve reaching the pixels
// (through the ILGPU CPU accelerator in CI when no GPU device is present, or the
// CPU shade fallback, either of which honours the position).

using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8Gpu3DPositionalUserBulbTests
{
    private static uint[] Render(double lightPos)
    {
        var fx = LightingFxData.CreateDefault();
        var l = fx.Light1;
        l.Type = LightType.Point;
        l.Intensity = 3.0;
        l.Range = 0.0;                       // pure inverse-square
        l.PosX = lightPos; l.PosY = lightPos; l.PosZ = lightPos;
        fx.Light1 = l;

        var fp = new FractalParameters
        {
            UserBulbSource = "z^8 + c",
            UserBulbCompiler = UserBulbCompilerKind.Sandbox,
            UserBulbBackend = UserBulbBackendKind.GPU,   // request the GPU shade
            UserBulbIterations = 6,
            UserBulbMaxSteps = 48,
            Lighting = fx,
        };

        var calc = new UserBulbCalculator(64, 48)
        {
            ColorMap = ColorPalette.BuiltIns[0],
            FractalParameters = fp,
        };
        calc.Calculate(System.Threading.CancellationToken.None);
        return (uint[])calc.ColorBuffer.Clone();
    }

    [Fact]
    public void PointLight_Position_Changes_The_Image()
    {
        uint[] near = Render(2.0);
        uint[] far  = Render(60.0);

        Assert.Equal(near.Length, far.Length);
        int diff = 0;
        for (int i = 0; i < near.Length; i++)
            if (near[i] != far[i]) diff++;
        Assert.True(diff > 0,
            "moving a point light must change the UserBulb image — the positional resolve did not reach the pixels");
    }

    [Fact]
    public void PointLight_Render_Is_Deterministic()
    {
        Assert.Equal(Render(2.0), Render(2.0));
    }
}
