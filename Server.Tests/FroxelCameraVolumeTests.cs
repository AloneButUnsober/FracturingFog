// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S6 render wiring (3D-Rendering-Roadmap.md, #389 / #408): the
// camera-frustum froxel mapping (FroxelCameraVolume) + the world-depth composite
// (FroxelVolumePass.CompositeWorldDepth). Pure/deterministic, so tested headless.

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class FroxelCameraVolumeTests
    {
        [Fact]
        public void CompositeWorldDepth_EmptyMedium_LeavesBeautyUnchanged()
        {
            var grid = new FroxelGrid(4, 4, 16, near: 1.0, far: 10.0);
            var pass = new FroxelVolumePass(grid);
            pass.Populate(new FroxelMedium { BaseDensity = 0.0, Extinction = 1.0, WorldExtent = 1.0 });

            int w = 4, h = 4;
            var beauty = new uint[w * h];
            var depth = new float[w * h];
            for (int i = 0; i < w * h; i++) { beauty[i] = 0xFF7788AAu; depth[i] = 5.0f; }

            var outb = pass.CompositeWorldDepth(beauty, depth, w, h);
            Assert.Equal(beauty, outb);
        }

        [Fact]
        public void CompositeWorldDepth_FarPixelMoreAttenuatedThanNear()
        {
            // Extinction only (no in-scatter) → a far pixel keeps less of its beauty.
            var grid = new FroxelGrid(4, 4, 32, near: 1.0, far: 20.0);
            var pass = new FroxelVolumePass(grid);
            pass.Populate(new FroxelMedium
            {
                BaseDensity = 0.4, Extinction = 1.0, LightIntensity = 0.0, WorldExtent = 1.0,
            });

            int w = 2, h = 1;
            var beauty = new uint[] { 0xFFFFFFFFu, 0xFFFFFFFFu };
            var depth = new float[] { 1.0f, 20.0f };   // near, far
            var outb = pass.CompositeWorldDepth(beauty, depth, w, h);

            int near = (int)(outb[0] & 0xFF);
            int far = (int)(outb[1] & 0xFF);
            Assert.True(near > far, $"near {near} should stay brighter than far {far}");
            Assert.True(far < 255, "far pixel should be attenuated below full white");
        }

        [Fact]
        public void CompositeWorldDepth_SkyMissDepthClampsToFullColumn()
        {
            // A sky-miss sentinel depth (1e6, well past Far) must clamp to the last
            // integrated slice — the full column in front, not an out-of-range read.
            var grid = new FroxelGrid(4, 4, 16, near: 1.0, far: 10.0);
            var pass = new FroxelVolumePass(grid);
            pass.Populate(new FroxelMedium
            {
                BaseDensity = 0.5, Extinction = 1.0, LightIntensity = 0.0, WorldExtent = 1.0,
            });

            var beauty = new uint[] { 0xFFFFFFFFu };
            var atFar = pass.CompositeWorldDepth(beauty, new float[] { 10.0f }, 1, 1);
            var atMiss = pass.CompositeWorldDepth(beauty, new float[] { 1e6f }, 1, 1);
            Assert.Equal(atFar[0] & 0xFF, atMiss[0] & 0xFF);   // both use the last slice
        }

        [Fact]
        public void BuildGrid_FramesSceneWithValidNearFar()
        {
            var p = ReliefParams();
            var cam = HeightfieldRaymarch2D.BuildObliqueCamera(320, 240, 320.0 / 240, sy: 0.35, maxH: 1.0, p);
            var grid = FroxelCameraVolume.BuildGrid(in cam);
            Assert.True(grid.Near > 0);
            Assert.True(grid.Far > grid.Near);
            Assert.Equal(FroxelCameraVolume.DimX, grid.DimX);
            Assert.Equal(FroxelCameraVolume.DimZ, grid.DimZ);
        }

        [Fact]
        public void BuildMedium_MapsFogKnobs()
        {
            var p = ReliefParams();
            var cam = HeightfieldRaymarch2D.BuildObliqueCamera(320, 240, 320.0 / 240, sy: 0.35, maxH: 1.0, p);
            var fx = LightingFxData.CreateDefault();
            fx.FogDensity = 0.7;
            fx.Light1.Intensity = 1.2;
            fx.Light1.Color = 0xFF8899AAu;
            var m = FroxelCameraVolume.BuildMedium(in cam, in fx);
            Assert.Equal(0.7, m.BaseDensity, 6);
            Assert.Equal(1.0, m.Extinction, 6);
            // S6 multi-light (#408): the key light is now Lights[0].
            Assert.NotNull(m.Lights);
            Assert.Equal(1.2, m.Lights![0].Intensity, 6);
            Assert.Equal(0xFF8899AAu, m.Lights[0].Color);
        }

        [Fact]
        public void Apply_WithFog_ChangesBuffer()
        {
            var p = ReliefParams();
            var cam = HeightfieldRaymarch2D.BuildObliqueCamera(320, 240, 320.0 / 240, sy: 0.35, maxH: 1.0, p);
            var fx = LightingFxData.CreateDefault();
            fx.FogDensity = 0.6;
            fx.Light1.Intensity = 1.0;

            int w = 8, h = 8;
            var beauty = new uint[w * h];
            var depth = new float[w * h];
            for (int i = 0; i < w * h; i++) { beauty[i] = 0xFF404040u; depth[i] = 3.0f; }

            var outb = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fx);
            bool anyChanged = false;
            for (int i = 0; i < w * h; i++) if (outb[i] != beauty[i]) { anyChanged = true; break; }
            Assert.True(anyChanged, "fog composite should change the buffer");
        }

        // ── S6 multi-light froxel (#408) ──────────────────────────────────

        private static FroxelLight WhiteDir(double intensity) => new()
        {
            Type = 0, Color = 0xFFFFFFFFu, Intensity = intensity,
            Lx = 0, Ly = 1, Lz = 0,
        };

        private static uint InScatterOnlyByte(FroxelMedium m)
        {
            // Composite over a black beauty at a far depth → the read-out byte is the
            // integrated in-scatter alone (transmittance times 0 = 0).
            var grid = new FroxelGrid(2, 2, 24, near: 1.0, far: 12.0);
            var pass = new FroxelVolumePass(grid);
            pass.Populate(m);
            var outb = pass.CompositeWorldDepth(new uint[] { 0xFF000000u }, new float[] { 12.0f }, 1, 1);
            return outb[0] & 0xFF;
        }

        [Fact]
        public void MultiLight_SumsBrighterThanSingle()
        {
            var baseM = new FroxelMedium
            {
                BaseDensity = 0.3, Extinction = 1.0, WorldExtent = 1.0,
                ViewDx = 0, ViewDy = 0, ViewDz = 1, Anisotropy = 0.0,
            };
            uint one = InScatterOnlyByte(baseM with { Lights = new[] { WhiteDir(1.0) } });
            uint three = InScatterOnlyByte(baseM with { Lights = new[] { WhiteDir(1.0), WhiteDir(1.0), WhiteDir(1.0) } });
            Assert.True(three > one, $"three lights ({three}) should out-scatter one ({one})");
        }

        [Fact]
        public void MultiLight_NullLights_MatchesEquivalentSingleArray()
        {
            // The legacy scalar key light (Lights == null) must be byte-identical to a
            // one-element Lights array with the same values (the unified populate path).
            var legacy = new FroxelMedium
            {
                BaseDensity = 0.4, Extinction = 1.0, WorldExtent = 1.0,
                ViewDx = 0, ViewDy = 0, ViewDz = 1, Anisotropy = 0.0,
                LightColor = 0xFF8090A0u, LightIntensity = 1.3, Lx = 0, Ly = 1, Lz = 0,
            };
            var arrayForm = legacy with
            {
                Lights = new[]
                {
                    new FroxelLight { Type = 0, Color = 0xFF8090A0u, Intensity = 1.3, Lx = 0, Ly = 1, Lz = 0 },
                },
            };
            Assert.Equal(InScatterOnlyByte(legacy), InScatterOnlyByte(arrayForm));
        }

        [Fact]
        public void MultiLight_PointDiffersFromDirectional()
        {
            // A point light resolves direction + inverse-square/range falloff per froxel,
            // so it must not read identically to a directional light of the same energy.
            var baseM = new FroxelMedium
            {
                BaseDensity = 0.3, Extinction = 1.0, WorldExtent = 1.0,
                ViewDx = 0, ViewDy = 0, ViewDz = 1, Anisotropy = 0.0,
            };
            uint dir = InScatterOnlyByte(baseM with { Lights = new[] { WhiteDir(1.0) } });
            var point = new FroxelLight
            {
                Type = 1, Color = 0xFFFFFFFFu, Intensity = 1.0,
                Lx = 0, Ly = 1, Lz = 0, PosX = 0, PosY = 5, PosZ = 3, Range = 20.0,
            };
            uint pt = InScatterOnlyByte(baseM with { Lights = new[] { point } });
            Assert.NotEqual(dir, pt);
        }

        [Fact]
        public void BuildMedium_PopulatesThreeLights()
        {
            var p = ReliefParams();
            var cam = HeightfieldRaymarch2D.BuildObliqueCamera(320, 240, 320.0 / 240, sy: 0.35, maxH: 1.0, p);
            var fx = LightingFxData.CreateDefault();
            fx.FogDensity = 0.5;
            fx.Light1.Intensity = 1.0;
            fx.Light2.Intensity = 0.6;
            fx.Light3.Type = LightType.Spot;
            fx.Light3.Intensity = 0.8;
            var m = FroxelCameraVolume.BuildMedium(in cam, in fx);
            Assert.NotNull(m.Lights);
            Assert.Equal(3, m.Lights!.Length);
            Assert.Equal((int)LightType.Spot, m.Lights[2].Type);
        }

        // ── S12 froxel-in-HDR (#655/#652): compose the fog into the float HDR beauty ──

        [Fact]
        public void CompositeWorldDepthHdr_MatchesByteComposite_ForInRangeValues()
        {
            // The HDR composite uses the SAME arithmetic as the byte path
            // (out = beauty·tr + inscatter·255). So a float HDR plane seeded with the
            // exact byte channel values must land within rounding of the byte result.
            var grid = new FroxelGrid(4, 4, 24, near: 1.0, far: 20.0);
            var pass = new FroxelVolumePass(grid);
            pass.Populate(new FroxelMedium
            {
                BaseDensity = 0.4, Extinction = 1.0, WorldExtent = 1.0,
                ViewDx = 0, ViewDy = 0, ViewDz = 1, Anisotropy = 0.0,
                Lights = new[] { WhiteDir(1.0) },
            });

            int w = 3, h = 1;
            var beauty = new uint[] { 0xFF406080u, 0xFF406080u, 0xFF406080u };
            var depth = new float[] { 3.0f, 9.0f, 18.0f };
            var byteOut = pass.CompositeWorldDepth(beauty, depth, w, h);

            var hdr = new float[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                uint p = beauty[i];
                hdr[i * 3]     = (p >> 16) & 0xFF;   // r = 0x40
                hdr[i * 3 + 1] = (p >> 8) & 0xFF;    // g = 0x60
                hdr[i * 3 + 2] = p & 0xFF;           // b = 0x80
            }
            pass.CompositeWorldDepthHdr(hdr, depth, w, h);

            for (int i = 0; i < w * h; i++)
            {
                int br = (int)((byteOut[i] >> 16) & 0xFF);
                int bg = (int)((byteOut[i] >> 8) & 0xFF);
                int bb = (int)(byteOut[i] & 0xFF);
                // Byte path clamps to [0,255] + rounds; HDR is unclamped float → within 1.
                Assert.True(System.Math.Abs(hdr[i * 3]     - br) <= 1.0f, $"r px{i}: hdr {hdr[i*3]} vs byte {br}");
                Assert.True(System.Math.Abs(hdr[i * 3 + 1] - bg) <= 1.0f, $"g px{i}: hdr {hdr[i*3+1]} vs byte {bg}");
                Assert.True(System.Math.Abs(hdr[i * 3 + 2] - bb) <= 1.0f, $"b px{i}: hdr {hdr[i*3+2]} vs byte {bb}");
            }
        }

        [Fact]
        public void CompositeWorldDepthHdr_LeavesNaNSkyUntouched()
        {
            // Sky / ray-miss pixels are NaN in the HDR plane; the HDR composite must NOT
            // touch them (the view transform decodes them from the fog-carrying 8-bit
            // fallback instead), so they stay NaN.
            var grid = new FroxelGrid(4, 4, 16, near: 1.0, far: 10.0);
            var pass = new FroxelVolumePass(grid);
            pass.Populate(new FroxelMedium
            {
                BaseDensity = 0.6, Extinction = 1.0, WorldExtent = 1.0,
                Lights = new[] { WhiteDir(1.0) },
            });

            int w = 2, h = 1;
            var hdr = new float[w * h * 3];
            // px0 = terrain (64,64,64); px1 = sky (NaN).
            hdr[0] = 64; hdr[1] = 64; hdr[2] = 64;
            hdr[3] = float.NaN; hdr[4] = float.NaN; hdr[5] = float.NaN;
            var depth = new float[] { 5.0f, 1e6f };

            pass.CompositeWorldDepthHdr(hdr, depth, w, h);

            Assert.False(float.IsNaN(hdr[0]), "terrain pixel should be composited");
            Assert.NotEqual(64f, hdr[0]);
            Assert.True(float.IsNaN(hdr[3]) && float.IsNaN(hdr[4]) && float.IsNaN(hdr[5]),
                "sky pixel must stay NaN (handled by the 8-bit fallback)");
        }

        [Fact]
        public void Apply_WithHdr_ComposesByteAndHdrTogether()
        {
            var p = ReliefParams();
            var cam = HeightfieldRaymarch2D.BuildObliqueCamera(320, 240, 320.0 / 240, sy: 0.35, maxH: 1.0, p);
            var fx = LightingFxData.CreateDefault();
            fx.FogDensity = 0.6;
            fx.Light1.Intensity = 1.0;

            int w = 8, h = 8;
            var beauty = new uint[w * h];
            var depth = new float[w * h];
            var hdr = new float[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                beauty[i] = 0xFF404040u;
                depth[i] = 3.0f;
                hdr[i * 3] = 64; hdr[i * 3 + 1] = 64; hdr[i * 3 + 2] = 64;
            }
            var hdrBefore = (float[])hdr.Clone();

            var outb = FroxelCameraVolume.Apply(beauty, depth, w, h, in cam, in fx,
                null, false, 0.0, FroxelQuality.Balanced, hdr);

            bool byteChanged = false, hdrChanged = false;
            for (int i = 0; i < w * h; i++) if (outb[i] != beauty[i]) { byteChanged = true; break; }
            for (int i = 0; i < hdr.Length; i++) if (hdr[i] != hdrBefore[i]) { hdrChanged = true; break; }
            Assert.True(byteChanged, "byte beauty should get fog");
            Assert.True(hdrChanged, "HDR beauty should get fog in the same Apply call");
        }

        private static FractalParameters ReliefParams() => new()
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
        };
    }
}
