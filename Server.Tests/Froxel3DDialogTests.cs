// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1071 (froxel F5) — Lighting & FX dialog controls for the 3D froxel fog.

using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class Froxel3DDialogTests
{
    [Fact]
    public void Controls_WriteTheLiveLighting_AndShowOnlyFor3D()
    {
        var p = new FractalParameters();
        var vm = new FractalParamsViewModel(FractalType.Mandelbulb, p);
        int fired = 0;
        vm.ParamChanged += () => fired++;

        vm.Froxel3D = true;
        vm.Froxel3DQuality = FroxelQuality.High;
        vm.Froxel3DShadowSteps = 500;          // clamped

        Assert.True(p.Lighting.Froxel3D);
        Assert.Equal(FroxelQuality.High, p.Lighting.Froxel3DQuality);
        Assert.Equal(128, p.Lighting.Froxel3DShadowSteps);
        Assert.Equal(3, fired);
        Assert.True(vm.Stage2PostFxApplies);    // the 3D-only section is shown
        Assert.False(new FractalParamsViewModel(FractalType.Mandelbrot, new FractalParameters()).Stage2PostFxApplies);
    }
}
