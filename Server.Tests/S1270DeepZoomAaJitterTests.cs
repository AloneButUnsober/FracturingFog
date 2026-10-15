// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1270 (SM-9) — sub-pixel anti-aliasing jitter at deep zoom. The MSAA / TAA
// passes used to add the jitter to the double CenterX only, which rounds away
// past ~1e10, so every "jittered" sample was the centre sample again. The jitter
// now goes through every centre limb (CentreJitter). These tests check:
//   • the shift actually applied equals j·scale at every precision tier, measured
//     by subtracting the OD centres (not by re-reading the formula);
//   • at a real deep view a jitter of one whole pixel shifts the rendered image
//     by exactly one column — the geometric meaning of a jitter — where the old
//     double-only jitter leaves the image unchanged;
//   • the centre is restored bit-exactly, and the TAA fingerprint sees a pan that
//     only moves a tail limb.

using System;
using System.Threading;
using FracturingFog;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1270DeepZoomAaJitterTests
{
    private const int W = 96, H = 64;

    // The --saprobe deep view (multi-limb centre, zoom 1e15): past the double's
    // precision, so a sub-pixel offset added to CenterX alone rounds away.
    private static MandelbrotCalculator DeepCalc() => new(W, H)
    {
        Quality = QualityPreset.Extreme,
        CenterX = -1.1726999042772253, CenterXLo = 8.9529605787776783E-17,
        CenterY = -0.2968356710071185, CenterYLo = -2.3536240906562374E-18,
        Zoom = 1e15, MaxIterations = 4096, ColorMap = new HsvPalette(),
    };

    private static double Scale(MandelbrotCalculator c) => (3.5 / Math.Max(c.Width, c.Height)) / c.Zoom;

    [Theory]
    [InlineData(1e3)]
    [InlineData(1e15)]
    [InlineData(1e35)]   // QD tier
    [InlineData(1e70)]   // OD tier
    [InlineData(1e100)]  // the Extreme zoom cap
    public void Applied_Shift_Equals_Jitter_Times_Scale_At_Every_Tier(double zoom)
    {
        var c = DeepCalc();
        c.Zoom = zoom;
        // A centre with every limb populated, as deep navigation leaves it.
        OD cx = new OD(-1.1726999042772253) + 8.9529605787776783E-17 + 3.1e-33 + 7.7e-50 + 1.3e-66 + 4.4e-83 + 2.9e-99;
        OD cy = new OD(-0.2968356710071185) - 2.3536240906562374E-18 + 5.5e-35 - 6.1e-51 + 2.2e-68 - 8.8e-84 + 1.7e-101;
        c.CenterX = cx.X0; c.CenterXLo = cx.X1; c.CenterX2 = cx.X2; c.CenterX3 = cx.X3;
        c.CenterX4 = cx.X4; c.CenterX5 = cx.X5; c.CenterX6 = cx.X6; c.CenterX7 = cx.X7;
        c.CenterY = cy.X0; c.CenterYLo = cy.X1; c.CenterY2 = cy.X2; c.CenterY3 = cy.X3;
        c.CenterY4 = cy.X4; c.CenterY5 = cy.X5; c.CenterY6 = cy.X6; c.CenterY7 = cy.X7;

        double scale = Scale(c);
        Assert.True(CentreJitter.IsResolvable(c, scale));
        var orig = CentreLimbs.Capture(c);
        foreach (var (jx, jy) in new[] { (0.5, -0.25), (-0.37, 0.41), (1.0, 0.0) })
        {
            CentreJitter.Apply(c, in orig, jx, jy, scale);
            var moved = CentreLimbs.Capture(c);
            double dx = (double)(moved.X - orig.X), dy = (double)(moved.Y - orig.Y);
            Assert.Equal(jx * scale, dx, Math.Abs(jx * scale) * 1e-9);
            Assert.Equal(jy * scale, dy, Math.Abs(jy * scale) * 1e-9 + 1e-300);
            orig.Restore(c);
            Assert.Equal(orig, CentreLimbs.Capture(c));   // bit-exact restore
        }
    }

    [Fact]
    public void One_Pixel_Jitter_Shifts_The_Deep_Image_By_One_Column()
    {
        var c = DeepCalc();
        double scale = Scale(c);
        // Precondition: this view is past the double floor — the old jitter
        // (added to CenterX alone) is lost entirely.
        Assert.Equal(c.CenterX, c.CenterX + 1.0 * scale);

        c.Calculate(CancellationToken.None);
        var baseImg = (uint[])c.ColorBuffer.Clone();

        // Precondition: the image has structure along x, so a one-column shift
        // is detectable (neighbouring columns mostly differ).
        int structured = 0, cells = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W - 1; x++, cells++)
                if (baseImg[y * W + x] != baseImg[y * W + x + 1]) structured++;
        Assert.True(structured > cells / 4, $"view too flat: {structured}/{cells} neighbour pairs differ");

        var orig = CentreLimbs.Capture(c);
        CentreJitter.Apply(c, in orig, 1.0, 0.0, scale);
        c.Calculate(CancellationToken.None);
        var shifted = (uint[])c.ColorBuffer.Clone();
        orig.Restore(c);

        // shifted[col x] must be what base saw at col x + 1. Allow a sliver of
        // perturbation-tolerance disagreement (the reference orbit is rebuilt at
        // the moved centre).
        int match = 0, n = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W - 1; x++, n++)
                if (shifted[y * W + x] == baseImg[y * W + x + 1]) match++;
        Assert.True(match >= n * 0.98, $"only {match}/{n} pixels match the one-column shift");
        // …and it is NOT the unshifted image (which is what the old jitter gave).
        int same = 0;
        for (int i = 0; i < W * H; i++) if (shifted[i] == baseImg[i]) same++;
        Assert.True(same < W * H * 0.9, $"{same}/{W * H} pixels unchanged — the jitter had no effect");
    }

    [Fact]
    public void Fingerprint_Sees_A_Tail_Limb_Only_Pan()
    {
        var c = DeepCalc();
        var a = CentreLimbs.Capture(c);
        c.CenterXLo += 1e-30;   // a deep pan: the double head does not change
        Assert.NotEqual(a, CentreLimbs.Capture(c));
        Assert.Equal(a.X0, c.CenterX);
    }

    [Fact]
    public void Jitter_Below_The_OD_Floor_Is_Not_Resolvable()
    {
        var c = DeepCalc();
        Assert.True(CentreJitter.IsResolvable(c, Scale(c)));
        c.Zoom = 1e130;
        Assert.False(CentreJitter.IsResolvable(c, Scale(c)));
    }
}
