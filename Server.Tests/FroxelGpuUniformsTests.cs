// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap S6 (#389 / #408) — the GPU-froxel-compute seam. FroxelGpuUniforms is the
// backend-agnostic bundle a GPU compute kernel (FroxelGpuKernel, Windows-only) reads
// to reproduce the pure-CPU froxel pass. These headless tests prove the uniforms
// carry EXACTLY the FroxelGrid + FroxelMedium that FroxelCameraVolume.Apply builds
// (so GPU == CPU by construction); the on-device GPU==CPU parity is proven by the
// --froxelgpu WARP gate (FroxelGpuProbe), which cannot run in this cross-platform
// test assembly (no Rendering.D3D reference).

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class FroxelGpuUniformsTests
    {
        private static HeightfieldRaymarch2D.ReliefCamera Cam()
        {
            var p = new FractalParameters
            {
                Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DFroxelVolumetrics = true,
                Relief2DHeightScale = 1.4,
                Relief2DCameraAzimuthDeg = 25, Relief2DCameraElevationDeg = 45, Relief2DCameraFovDeg = 55,
            };
            return HeightfieldRaymarch2D.BuildObliqueCamera(200, 150, 200.0 / 150, sy: 0.35, maxH: 1.0, p);
        }

        private static LightingFxData Fx()
        {
            var fx = LightingFxData.CreateDefault();
            fx.FogDensity = 0.6;
            fx.VolumeAnisotropy = 0.3;
            fx.VolumeNoiseAmount = 0.2;
            fx.VolumeNoiseScale = 0.5;
            fx.VolumeNoiseOctaves = 3;
            fx.Light2.Type = LightType.Point;
            fx.Light2.Intensity = 0.8;
            fx.Light2.PosX = 0.4; fx.Light2.PosY = 1.1; fx.Light2.PosZ = 0.3;
            fx.Light2.Range = 4.0;
            fx.Light3.Type = LightType.Spot;
            fx.Light3.Intensity = 0.6;
            fx.Light3.SpotInnerDeg = 30.0; fx.Light3.SpotOuterDeg = 60.0;
            return fx;
        }

        [Fact]
        public void Build_GridMatchesFroxelCameraVolume()
        {
            var cam = Cam();
            var fx = Fx();
            var u = FroxelGpuUniforms.Build(in cam, in fx);
            var g = FroxelCameraVolume.BuildGrid(in cam);

            Assert.Equal(g.DimX, u.Grid.DimX);
            Assert.Equal(g.DimY, u.Grid.DimY);
            Assert.Equal(g.DimZ, u.Grid.DimZ);
            Assert.Equal(g.Near, u.Grid.Near, 12);
            Assert.Equal(g.Far, u.Grid.Far, 12);
        }

        [Fact]
        public void Build_MediumMatchesFroxelCameraVolume()
        {
            var cam = Cam();
            var fx = Fx();
            var u = FroxelGpuUniforms.Build(in cam, in fx);
            var m = FroxelCameraVolume.BuildMedium(in cam, in fx);

            Assert.Equal(m.BaseDensity, u.Medium.BaseDensity, 12);
            Assert.Equal(m.Extinction, u.Medium.Extinction, 12);
            Assert.Equal(m.Anisotropy, u.Medium.Anisotropy, 12);
            Assert.Equal(m.NoiseAmount, u.Medium.NoiseAmount, 12);
            Assert.Equal(m.NoiseScale, u.Medium.NoiseScale, 12);
            Assert.Equal(m.NoiseOctaves, u.Medium.NoiseOctaves);
            Assert.Equal(m.WorldExtent, u.Medium.WorldExtent, 12);
            Assert.Equal(m.ViewDx, u.Medium.ViewDx, 12);
            Assert.Equal(m.ViewDy, u.Medium.ViewDy, 12);
            Assert.Equal(m.ViewDz, u.Medium.ViewDz, 12);
        }

        [Fact]
        public void Build_CarriesAllThreeLights()
        {
            var cam = Cam();
            var fx = Fx();
            var u = FroxelGpuUniforms.Build(in cam, in fx);

            Assert.NotNull(u.Medium.Lights);
            Assert.Equal(3, u.Medium.Lights!.Length);
            // Types map through (directional / point / spot).
            Assert.Equal((int)LightType.Directional, u.Medium.Lights[0].Type);
            Assert.Equal((int)LightType.Point, u.Medium.Lights[1].Type);
            Assert.Equal((int)LightType.Spot, u.Medium.Lights[2].Type);
            // Point light carries its world position + range.
            Assert.Equal(0.4, u.Medium.Lights[1].PosX, 12);
            Assert.Equal(4.0, u.Medium.Lights[1].Range, 12);
        }

        [Fact]
        public void Build_FogLightMaskZeroesDroppedLightIntensity()
        {
            var cam = Cam();
            var fx = Fx();
            fx.Light1.Intensity = 1.0;
            fx.VolumeLightMask = 0x5;   // drop Light2 (bit 0x2) from the fog
            var u = FroxelGpuUniforms.Build(in cam, in fx);

            Assert.True(u.Medium.Lights![0].Intensity > 0, "Light1 fogs");
            Assert.Equal(0.0, u.Medium.Lights[1].Intensity, 12);   // Light2 dropped
            Assert.True(u.Medium.Lights[2].Intensity > 0, "Light3 fogs");
        }
    }
}
