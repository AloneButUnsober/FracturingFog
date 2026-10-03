// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1067 (froxel F1) — FroxelCamera decouples the froxel volume from the Relief 3D
// camera. The relief path must stay byte-identical: the golden hash below was
// captured by running this exact fixture against the pre-refactor
// FroxelCameraVolume / FroxelGpuUniforms (main @ 24490b9e), so it is an
// independent oracle, not the refactored code checking itself.

using System;

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FroxelCameraTests
{
    private const int W = 40, H = 30;

    private static HeightfieldRaymarch2D.ReliefCamera Relief(double camX, double camZ)
    {
        // A camera above and behind a 2×0.6×2 slab, looking at the origin.
        double cy = 1.8;
        double fl = Math.Sqrt(camX * camX + cy * cy + camZ * camZ);
        double fx = -camX / fl, fy = -cy / fl, fz = -camZ / fl;
        double rl = Math.Sqrt(fz * fz + fx * fx);
        double rx = -fz / rl, rz = fx / rl;
        double ux = -rz * fy, uy = rz * fx - rx * fz, uz = rx * fy;
        return new HeightfieldRaymarch2D.ReliefCamera(camX, cy, camZ, fx, fy, fz, rx, rz, ux, uy, uz,
            Math.Tan(25 * Math.PI / 180), false, 0, 2.0, 0.6, 2.0, 1e-3, 1e-3, 256, true, 3.0, 3.0);
    }

    private static LightingFxData Fx()
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.35;
        fx.VolumeAnisotropy = 0.3;
        fx.VolumeNoiseAmount = 0.4;
        fx.VolumeNoiseScale = 1.7;
        fx.VolumeNoiseOctaves = 3;
        fx.VolumeLightMask = 0x7;
        fx.Light2.Intensity = 0.6;
        fx.Light2.Type = LightType.Point;
        fx.Light2.PosX = 0.5; fx.Light2.PosY = 1.0; fx.Light2.PosZ = -0.4; fx.Light2.Range = 4;
        return fx;
    }

    private static (uint[] Beauty, float[] Depth, float[] Hdr) Buffers()
    {
        var b = new uint[W * H]; var d = new float[W * H]; var hdr = new float[W * H * 3];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                b[i] = 0xFF000000u | (uint)(x * 6) << 16 | (uint)(y * 8) << 8 | (uint)((x + y) * 3);
                d[i] = y < 4 ? 1e6f : 1.5f + 0.15f * x + 0.05f * y;       // top rows = sky
                hdr[i * 3] = x * 6; hdr[i * 3 + 1] = y * 8; hdr[i * 3 + 2] = y < 4 ? float.NaN : (x + y) * 3;
            }
        return (b, d, hdr);
    }

    // FNV-1a over everything the relief froxel path produces.
    private static ulong Fingerprint()
    {
        ulong h = 1469598103934665603UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        void MixD(double v) => Mix((ulong)BitConverter.DoubleToInt64Bits(v));

        var fx = Fx();
        var (beauty, depth, hdr) = Buffers();

        // Single frame, every quality.
        foreach (var q in new[] { FroxelQuality.Low, FroxelQuality.Balanced, FroxelQuality.High })
            foreach (uint p in FroxelCameraVolume.Apply(beauty, depth, W, H, Relief(1.2, 3.5), in fx, null, false, 0, q))
                Mix(p);

        // Temporal + reprojection over a moving camera, with an HDR plane.
        var hist = new FroxelHistory();
        for (int f = 0; f < 3; f++)
        {
            var hdrF = (float[])hdr.Clone();
            foreach (uint p in FroxelCameraVolume.Apply(beauty, depth, W, H, Relief(1.2 + 0.1 * f, 3.5 - 0.05 * f),
                         in fx, hist, true, 0.8, FroxelQuality.Balanced, hdrF, reproject: true))
                Mix(p);
            foreach (float v in hdrF) Mix((ulong)BitConverter.SingleToInt32Bits(v));
        }

        // GPU uniforms (grid + medium + reprojection basis).
        var u = FroxelGpuUniforms.Build(Relief(1.2, 3.5), in fx, FroxelQuality.Balanced);
        MixD(u.Grid.Near); MixD(u.Grid.Far); Mix((ulong)u.Grid.DimZ);
        MixD(u.Medium.WorldExtent); MixD(u.Medium.ViewDx); MixD(u.Medium.ViewDy); MixD(u.Medium.ViewDz);
        MixD(u.Camera.PosX); MixD(u.Camera.Rx); MixD(u.Camera.Ry); MixD(u.Camera.Uy); MixD(u.Camera.Fz);
        return h;
    }

    [Fact]
    public void ReliefFroxel_IsByteIdenticalToThePreRefactorPath()
        => Assert.Equal(GoldenFingerprint, Fingerprint());

    [Fact]
    public void LookAt_BuildsAnOrthonormalRightHandedBasis_TowardTheTarget()
    {
        var c = FroxelCamera.LookAt((2, 1.5, 3), (0, 0, 0), 50 * Math.PI / 180, 0.1, 12, 1.4);
        double Dot(double ax, double ay, double az, double bx, double by, double bz) => ax * bx + ay * by + az * bz;

        Assert.Equal(1.0, Dot(c.Fx, c.Fy, c.Fz, c.Fx, c.Fy, c.Fz), 9);
        Assert.Equal(1.0, Dot(c.Rx, c.Ry, c.Rz, c.Rx, c.Ry, c.Rz), 9);
        Assert.Equal(1.0, Dot(c.Ux, c.Uy, c.Uz, c.Ux, c.Uy, c.Uz), 9);
        Assert.Equal(0.0, Dot(c.Fx, c.Fy, c.Fz, c.Rx, c.Ry, c.Rz), 9);
        Assert.Equal(0.0, Dot(c.Fx, c.Fy, c.Fz, c.Ux, c.Uy, c.Uz), 9);
        Assert.Equal(0.0, Dot(c.Rx, c.Ry, c.Rz, c.Ux, c.Uy, c.Uz), 9);
        Assert.Equal(0.0, c.Ry, 12);                          // horizontal right vector
        Assert.True(c.Uy > 0);                                // up is up
        double len = Math.Sqrt(2 * 2 + 1.5 * 1.5 + 3 * 3);   // forward points at the target
        Assert.Equal(-2 / len, c.Fx, 9); Assert.Equal(-1.5 / len, c.Fy, 9); Assert.Equal(-3 / len, c.Fz, 9);
        // right × up = −forward  ⇔  right-handed with forward = −Z_cam
        Assert.Equal(-c.Fx, c.Ry * c.Uz - c.Rz * c.Uy, 9);
        Assert.Equal((0.1, 12.0, 1.4), (c.Near, c.Far, c.Extent));
        Assert.Equal(Math.Tan(25 * Math.PI / 180), c.TanHalf, 12);
    }

    // Captured from main @ 24490b9e (pre-#1067) with this fixture.
    private const ulong GoldenFingerprint = 7690144804423798110UL;
}
