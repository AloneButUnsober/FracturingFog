// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S3 (#400) — in-camera exposure. A camera-stage exposure (stops) applied to the
// relief beauty in linear light, independent of the S2 output view transform (the
// camera exposes the scene; the view transform still tonemaps afterwards). Locks:
// the ViewTransformOps.ApplyExposureOnly operator (0 EV byte-identical, +EV brightens,
// -EV darkens, no tonemap) + the relief render wiring (EV 0 byte-identical, +EV
// brightens the surface, silhouette unmoved) + the batch flag parse/range-check.

using FracturingFog.Batch;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class CameraExposureTests
{
    private const int W = 96, H = 72, HW = 96, HH = 72;

    private static (uint[] albedo, float[] height) Scene()
    {
        var albedo = new uint[W * H];
        for (int i = 0; i < albedo.Length; i++) albedo[i] = 0xFF808080u;   // mid grey
        var height = new float[HW * HH];
        for (int y = 0; y < HH; y++)
        for (int x = 0; x < HW; x++)
        {
            double u = (x + 0.5) / HW - 0.5, v = (y + 0.5) / HH - 0.5;
            double r = System.Math.Sqrt(u * u + v * v) / 0.5;
            height[y * HW + x] = r >= 1.0 ? 0f : (float)(0.5 * (1.0 + System.Math.Cos(System.Math.PI * r)));
        }
        return (albedo, height);
    }

    private static FractalParameters Base() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,
    };

    // ── the operator ────────────────────────────────────────────────────────
    [Fact]
    public void ApplyExposureOnly_ZeroEv_Is_ByteIdentical()
    {
        var a = new uint[] { 0xFF203040u, 0xFFAABBCCu, 0xFF000000u, 0xFFFFFFFFu };
        var b = (uint[])a.Clone();
        ViewTransformOps.ApplyExposureOnly(b, b.Length, 0f);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ApplyExposureOnly_Brightens_And_Darkens()
    {
        uint mid = 0xFF808080u;
        var up = new[] { mid }; ViewTransformOps.ApplyExposureOnly(up, 1, +1f);
        var dn = new[] { mid }; ViewTransformOps.ApplyExposureOnly(dn, 1, -1f);
        Assert.True((up[0] & 0xFF) > 0x80, "+1 EV should brighten the mid grey");
        Assert.True((dn[0] & 0xFF) < 0x80, "-1 EV should darken the mid grey");
        Assert.Equal(0xFFu, (up[0] >> 24) & 0xFF);   // alpha preserved
    }

    // ── the relief wiring ─────────────────────────────────────────────────────
    private static uint[] Render(FractalParameters p)
    {
        var (albedo, height) = Scene();
        var dst = new uint[W * H];
        HeightfieldRaymarch2D.Render(albedo, height, W, H, HW, HH, p, dst, out _);
        return dst;
    }

    [Fact]
    public void CameraExposure_Zero_Is_ByteIdentical()
    {
        Assert.Equal(Render(Base()), Render(Base()));   // determinism baseline
        var p = Base();
        p.Relief2DCameraExposureEv = 0.0;
        Assert.Equal(Render(Base()), Render(p));         // 0 EV path == no exposure
    }

    [Fact]
    public void CameraExposure_Brightens_Surface_Without_Moving_Silhouette()
    {
        var (albedo, height) = Scene();
        var off = new uint[W * H];
        HeightfieldRaymarch2D.Render(albedo, height, W, H, HW, HH, Base(), off, out double hitOff);

        var p = Base();
        p.Relief2DCameraExposureEv = 2.0;
        var on = new uint[W * H];
        HeightfieldRaymarch2D.Render(albedo, height, W, H, HW, HH, p, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);   // exposure never moves geometry
        int brighter = 0;
        for (int i = 0; i < off.Length; i++)
            if ((on[i] & 0xFF) > (off[i] & 0xFF)) brighter++;
        Assert.True(brighter > 50, $"+2 EV should brighten the lit surface ({brighter} px)");
    }

    // ── the batch surface ─────────────────────────────────────────────────────
    [Fact]
    public void Batch_CameraExposure_Parses_Arms_Relief_And_RangeChecks()
    {
        string[] argv = { "app.exe", "--batch", "--x", "-0.5", "--y", "0", "--zoom", "1", "--out", "o.png",
                          "--camera-exposure", "-1.5" };
        Assert.True(BatchOptions.TryParse(argv, 2, out var opts, out var err), err);
        Assert.Equal(-1.5, opts.CameraExposureEv!.Value, 6);
        Assert.True(opts.ReliefRaymarch);
        Assert.True(opts.Relief);

        Assert.False(BatchOptions.TryParse(
            new[] { "app.exe", "--batch", "--x", "-0.5", "--y", "0", "--zoom", "1", "--out", "o.png", "--camera-exposure", "99" },
            2, out _, out var err2));
        Assert.Contains("camera-exposure", err2);
    }
}
