// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1738 — a partial Cam3D* set (say --param Cam3DDistance=30 alone) used to be dropped
// silently: the camera block applied only with all of distance, theta and phi. Each key now
// applies on its own and a missing one keeps the family's current value. Checked through the
// region snapshot (the keys the region and the batch write) and on rendered pixels.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class Cam3DPartialKeysTests
{
    // Every family the camera block routes (FractalRegion.ApplyTo).
    public static IEnumerable<object[]> Families() => new[]
    {
        FractalType.Mandelbulb, FractalType.Mandelbox, FractalType.Kifs, FractalType.QuaternionJulia,
        FractalType.QuaternionMandelbrot, FractalType.Kleinian, FractalType.MirrorSpheres, FractalType.MathPlane3D,
        FractalType.BicomplexMandelbrot, FractalType.Coquaternion, FractalType.Julibrot,
    }.Select(t => new object[] { t });

    private static KeyValuePair<string, string> Kv(string k, double v)
        => new(k, v.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The camera as the region snapshot reads it back (independent of the apply code).</summary>
    private static (double D, double T, double P) Camera(FractalType t, FractalParameters p)
    {
        var s = RegionFractalParams.Snapshot(t, p)!;
        return (s.Cam3DDistance!.Value, s.Cam3DTheta!.Value, s.Cam3DPhi!.Value);
    }

    private static FractalParameters Apply(FractalType t, params KeyValuePair<string, string>[] kv)
    {
        var p = new FractalParameters();
        var rp = RegionFractalParams.FromKeyValues(kv, t, out var err);   // as --param: family from --fractal
        Assert.Null(err);
        rp!.ApplyTo(p);
        return p;
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void EveryKeyAppliesOnItsOwn_AndAMissingOneKeepsTheCurrentValue(FractalType t)
    {
        var def = Camera(t, new FractalParameters());
        // A camera already set (as a region would), unlike any family's default, so a missing key must
        // keep THIS family's current value, not a default or another family's.
        var start = (D: def.D + 0.75, T: def.T - 0.15, P: def.P + 0.1);
        double d = def.D + 1.25, th = def.T + 0.3, ph = def.P - 0.2;
        // Every non-empty subset of distance, theta and phi.
        for (int mask = 1; mask < 8; mask++)
        {
            var p = Apply(t, Kv("Cam3DDistance", start.D), Kv("Cam3DTheta", start.T), Kv("Cam3DPhi", start.P));
            Assert.Equal(start, Camera(t, p));
            var kv = new List<KeyValuePair<string, string>>();
            if ((mask & 1) != 0) kv.Add(Kv("Cam3DDistance", d));
            if ((mask & 2) != 0) kv.Add(Kv("Cam3DTheta", th));
            if ((mask & 4) != 0) kv.Add(Kv("Cam3DPhi", ph));
            RegionFractalParams.FromKeyValues(kv, t, out var err)!.ApplyTo(p);
            Assert.Null(err);
            Assert.Equal(((mask & 1) != 0 ? d : start.D, (mask & 2) != 0 ? th : start.T, (mask & 4) != 0 ? ph : start.P), Camera(t, p));
        }
    }

    [Fact]
    public void AMissingKey_KeepsTheValueAlreadySet_NotTheDefault()
    {
        // A region (or an earlier --param) set the whole camera; a later lone theta changes only theta.
        var p = new FractalParameters();
        RegionFractalParams.FromKeyValues(new[] { Kv("Cam3DDistance", 5.5), Kv("Cam3DTheta", 0.1), Kv("Cam3DPhi", 0.7) },
            FractalType.Mandelbox, out _)!.ApplyTo(p);
        RegionFractalParams.FromKeyValues(new[] { Kv("Cam3DTheta", 1.9) }, FractalType.Mandelbox, out _)!.ApplyTo(p);
        Assert.Equal((5.5, 1.9, 0.7), Camera(FractalType.Mandelbox, p));
    }

    [Fact]
    public void ALoneSliceW_Applies()
    {
        var p = Apply(FractalType.QuaternionJulia, Kv("Cam3DSliceW", 0.35));
        Assert.Equal(0.35, p.QJuliaSliceW);
        Assert.Equal(Camera(FractalType.QuaternionJulia, new FractalParameters()), Camera(FractalType.QuaternionJulia, p));
    }

    [Fact]
    public void NoCameraKeys_LeaveTheCameraAlone()
    {
        var p = new FractalParameters { BulbCameraDistance = 7.25 };
        RegionFractalParams.FromKeyValues(new[] { new KeyValuePair<string, string>("BulbPower", "6") },
            FractalType.Mandelbulb, out _)!.ApplyTo(p);
        Assert.Equal(7.25, p.BulbCameraDistance);
    }

    private static uint[] Render(FractalType t, FractalParameters fp)
        => PosterRenderer.RenderDisplayPixels(new PosterRequest
        {
            FractalType = t, Width = 96, Height = 64, CenterX = 0, CenterY = 0, Zoom = 1, MaxIterations = 64,
            ColorMap = ColorPalette.BuiltIns[0], FractalParameters = fp,
        }, CancellationToken.None, out _, out _);

    [Theory]
    [InlineData(FractalType.Mandelbulb, 3.2)]
    [InlineData(FractalType.MathPlane3D, 14.0)]
    public void ALoneDistance_MovesTheRenderedCamera_AsTheFullSetDoes(FractalType t, double d)
    {
        var def = Camera(t, new FractalParameters());
        var lone = Render(t, Apply(t, Kv("Cam3DDistance", d)));
        var full = Render(t, Apply(t, Kv("Cam3DDistance", d), Kv("Cam3DTheta", def.T), Kv("Cam3DPhi", def.P)));
        var none = Render(t, new FractalParameters());
        Assert.Equal(full, lone);
        Assert.NotEqual(none, lone);
    }
}
