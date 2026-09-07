// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S1 (#398) — relight in post. The relief render can rebuild its beauty from the
// captured per-pixel lighting components (the ShadeComponents AOV) + the albedo,
// under per-layer gains, so a user retunes the lighting look without re-tracing the
// geometry (the LightCompositor operator #642, now user-reachable). Locks: relight
// off is byte-identical; relight at unit gains produces a valid image and hitFraction
// is untouched (relight never moves geometry); pushing a gain changes the render.

using FracturingFog.Batch;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefRelightTests
{
    private const int W = 96, H = 72, HW = 96, HH = 72;

    private static (uint[] albedo, float[] height) Scene()
    {
        var albedo = new uint[W * H];
        for (int i = 0; i < albedo.Length; i++) albedo[i] = 0xFFB06030u;   // warm terrain
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

    private static uint[] Render(FractalParameters p)
    {
        var (albedo, height) = Scene();
        var dst = new uint[W * H];
        HeightfieldRaymarch2D.Render(albedo, height, W, H, HW, HH, p, dst, out _);
        return dst;
    }

    [Fact]
    public void RelightOff_Is_ByteIdentical()
    {
        var off1 = Render(Base());
        var off2 = Render(Base());
        Assert.Equal(off1, off2);
        // A params object with relight flag false but gains set to non-defaults must
        // still be byte-identical to the plain render (the flag gates everything).
        var p = Base();
        p.Relief2DRelightDiffuseGain = 3.0;
        p.Relief2DRelightAmbient = 0.5;
        Assert.Equal(off1, Render(p));
    }

    [Fact]
    public void Relight_Preserves_Silhouette_And_Renders()
    {
        var (albedo, height) = Scene();

        var pOff = Base();
        var off = new uint[W * H];
        HeightfieldRaymarch2D.Render(albedo, height, W, H, HW, HH, pOff, off, out double hitOff);

        var pOn = Base();
        pOn.Relief2DRelight = true;   // unit gains
        var on = new uint[W * H];
        HeightfieldRaymarch2D.Render(albedo, height, W, H, HW, HH, pOn, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);            // relight never moves geometry
        // Some surface pixel is non-background (a real relit image, not all sky).
        bool anySurface = false;
        for (int i = 0; i < on.Length; i++) if ((on[i] & 0x00FFFFFF) != 0) { anySurface = true; break; }
        Assert.True(anySurface, "relit render should have lit surface pixels");
    }

    [Fact]
    public void Relight_Gain_Changes_The_Render()
    {
        var pLow = Base();
        pLow.Relief2DRelight = true;
        pLow.Relief2DRelightDiffuseGain = 0.5;
        var low = Render(pLow);

        var pHigh = Base();
        pHigh.Relief2DRelight = true;
        pHigh.Relief2DRelightDiffuseGain = 4.0;
        var high = Render(pHigh);

        int diff = 0;
        for (int i = 0; i < low.Length; i++) if (low[i] != high[i]) diff++;
        Assert.True(diff > 20, $"a higher diffuse relight gain should brighten the surface ({diff} px)");
    }

    [Fact]
    public void Batch_Relight_Flags_Parse_And_Arm_Relief()
    {
        string[] argv = { "app.exe", "--batch", "--x", "-0.5", "--y", "0", "--zoom", "1", "--out", "o.png",
                          "--relight-diffuse", "2.5", "--relight-ambient", "0.3" };
        Assert.True(BatchOptions.TryParse(argv, 2, out var opts, out var err), err);
        Assert.True(opts.Relight);                       // a gain implies enable
        Assert.Equal(2.5, opts.RelightDiffuse!.Value, 6);
        Assert.Equal(0.3, opts.RelightAmbient!.Value, 6);
        Assert.True(opts.ReliefRaymarch);
        Assert.True(opts.Relief);

        // Range-checked.
        Assert.False(BatchOptions.TryParse(
            new[] { "app.exe", "--batch", "--x", "-0.5", "--y", "0", "--zoom", "1", "--out", "o.png", "--relight-diffuse", "99" },
            2, out _, out var err2));
        Assert.Contains("relight-diffuse", err2);
    }
}
