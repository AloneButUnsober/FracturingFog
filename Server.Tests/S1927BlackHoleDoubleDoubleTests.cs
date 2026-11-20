// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FracturingFog;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Scattering;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1927 part 2 — double-double (DD) black hole trace for views past double precision.
// Independent checks:
//   • where double is accurate, the DD trace gives the double trace's fates and values;
//   • DD resolves the single-hole shadow edge (L/E = 4m, extremal Reissner-Nordström) to 2e-16,
//     below double's own spacing of numbers near 4 (8.9e-16; the double trace already calls
//     b* + 1e-14 captured);
//   • past x10^10 (where the double references disagree, part 1), all-DD frames at two DD
//     tolerances agree with each other (converged), and the calculator's edge fix-up (double
//     pass + DD retrace of the fate edges) reproduces the all-DD frame pixel for pixel.
public class S1927BlackHoleDoubleDoubleTests
{
    [Fact]
    public void DD_Trace_Matches_The_Double_Trace_Where_Double_Is_Accurate()
    {
        var scene = BlackHoleShadowCalculator.Scene.Build(new FractalParameters { BlackHoleTolerance = 1e-10 });
        Span<double> s = stackalloc double[6];
        int compared = 0;
        foreach (var (u, v) in new[] { (9.0, 2.0), (2.0, 1.0), (-5.5, 0.3), (0.2, 6.0), (-3.0, -2.5), (6.0, -0.5) })
        {
            var (o, sys) = scene.Trace(u, v, s, channels: false);
            var d = MajumdarPapapetrouDD.Trace(scene, u, v, 1e-10, 20000);
            Assert.Equal(o.Fate, d.Fate);
            Assert.Equal(o.Body, d.Body);
            if (o.Fate == ScatterFate.Escaped)
            {
                Assert.InRange(Math.Abs(d.Delay - sys.Delay), 0.0, 1e-6 * Math.Max(1.0, sys.Delay));
                double pm = Math.Sqrt(s[3] * s[3] + s[4] * s[4] + s[5] * s[5]);
                double dm = Math.Sqrt(d.Px * d.Px + d.Py * d.Py + d.Pz * d.Pz);
                double cos = (s[3] * d.Px + s[4] * d.Py + s[5] * d.Pz) / (pm * dm);
                Assert.InRange(cos, 1 - 1e-9, 1 + 1e-12);
                compared++;
            }
        }
        Assert.True(compared >= 2);
    }

    [Fact]
    public void DD_Square_Root_Has_DD_Accuracy()
    {
        foreach (double a in new[] { 2.0, 3.0, 1e-20, 12345.678 })
        {
            DD r = MajumdarPapapetrouDD.Sqrt(a);
            DD back = r * r - a;
            Assert.InRange(Math.Abs((double)back), 0.0, 1e-30 * a);
        }
    }

    [Fact]
    public void DD_Resolves_The_Single_Hole_Shadow_Edge_Far_Below_Double()
    {
        const double D = 1000.0;
        var scene = BlackHoleShadowCalculator.Scene.Build(new FractalParameters
        {
            BlackHoleLayout = BlackHoleLayout.Single, BlackHoleMass1 = 1.0, BlackHoleCameraDistance = D,
        });
        // The ray through plane offset b starts at (D, 0, b), distance r0 = sqrt(D² + b²), with
        // momentum U(r0)² along -x, so L/E = b U(r0)², U(r) = 1 + 1/r. The extremal RN edge is
        // L/E = 4m: solve b U(sqrt(D² + b²))² = 4 by fixed-point iteration, in DD.
        DD edge = 4.0;
        for (int k = 0; k < 8; k++)
        {
            DD r0 = MajumdarPapapetrouDD.Sqrt((DD)(D * D) + edge * edge);
            DD ur = (DD)1.0 + (DD)1.0 / r0;
            edge = (DD)4.0 / (ur * ur);
        }
        // Tolerance 1e-20 (Dormand-Prince 5(4) needs ~3-4e5 steps per ray there; tighter hits the cap).
        foreach (double off in new[] { 1e-12, 1e-14, 2e-16 })
        {
            var inside = MajumdarPapapetrouDD.Trace(scene, edge - off, 0.0, 1e-20, 2_000_000);
            var outside = MajumdarPapapetrouDD.Trace(scene, edge + off, 0.0, 1e-20, 2_000_000);
            Assert.True(inside.Fate == ScatterFate.Captured, $"b* - {off:E0} should fall in");
            Assert.True(outside.Fate == ScatterFate.Escaped, $"b* + {off:E0} should escape");
        }
    }

    private static sbyte[] AllDD(BlackHoleShadowCalculator.Scene scene, double cx, double zoom, int w, int h, double tol)
    {
        var f = new sbyte[w * h];
        double pitch = BlackHoleShadowCalculator.PixelPitch(w, h, zoom);
        Parallel.For(0, w * h, i =>
        {
            int x = i % w, y = i / w;
            var u = DD.FromCenterOffset(cx, x + 0.5 - w * 0.5, pitch);
            var v = -DD.FromCenterOffset(0.0, y + 0.5 - h * 0.5, pitch);
            var o = MajumdarPapapetrouDD.Trace(scene, u, v, tol, 400000);
            f[i] = (sbyte)(o.Fate == ScatterFate.Captured ? o.Body : o.Fate == ScatterFate.Escaped ? scene.EscapedGate : -1);
        });
        return f;
    }

    [Theory]
    [InlineData(1e11)]
    [InlineData(1e12)]
    public void Edge_Fixup_Reproduces_The_Converged_All_DD_Frame_Past_Double(double zoomFactor)
    {
        const int W = 48, H = 27;
        const double c = -7.806216050537152;   // the part 1 calibration's bisected boundary
        double zoom = BlackHoleShadowCalculator.DefaultZoom * zoomFactor;
        double pitch = BlackHoleShadowCalculator.PixelPitch(W, H, zoom);
        Assert.True(BlackHoleShadowCalculator.Scene.PastDoublePrecision(pitch));
        var scene = BlackHoleShadowCalculator.Scene.Build(new FractalParameters(), zoom, pitch);

        double tol = BlackHoleShadowCalculator.DdTolerance(pitch);
        var reference = AllDD(scene, c, zoom, W, H, tol);
        var tighter = AllDD(scene, c, zoom, W, H, tol / 100);
        Assert.Equal(tighter, reference);                       // converged
        Assert.Contains(reference, f => f != reference[0]);      // an edge is in view

        var calc = new BlackHoleShadowCalculator(W, H) { FractalParameters = new FractalParameters(), ColorMap = new HsvPalette(), CenterX = c, Zoom = zoom };
        var sw = Stopwatch.StartNew();
        calc.Calculate(TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine($"x{zoomFactor:E0}: fix-up retraced {calc.LastDdPixels} of {W * H} px in DD, {sw.ElapsedMilliseconds} ms; label '{calc.LastPrecisionLabel}'");
        Assert.Equal(reference, calc.FateBuffer);
        Assert.True(calc.LastDdPixels > 0 && calc.LastDdPixels < W * H);
        Assert.StartsWith("DD edges:", calc.LastPrecisionLabel);

        var off = new BlackHoleShadowCalculator(W, H) { FractalParameters = new FractalParameters { BlackHoleDdEdges = false }, ColorMap = new HsvPalette(), CenterX = c, Zoom = zoom };
        off.Calculate(TestContext.Current.CancellationToken);
        Assert.Equal(0, off.LastDdPixels);
        Assert.Equal("past fp64: edges approximate", off.LastPrecisionLabel);
    }

    [Fact]
    public void No_Fixup_Where_Double_Suffices_Or_For_Channels()
    {
        var shallow = new BlackHoleShadowCalculator(40, 24) { FractalParameters = new FractalParameters(), ColorMap = new HsvPalette() };
        shallow.Calculate(TestContext.Current.CancellationToken);
        Assert.Equal(0, shallow.LastDdPixels);
        var channel = new BlackHoleShadowCalculator(24, 14)
        {
            FractalParameters = new FractalParameters { BlackHoleChannel = BlackHoleChannel.Steps, BlackHoleMaxSteps = 3000 },
            ColorMap = new HsvPalette(), CenterX = -7.806216050537152, Zoom = BlackHoleShadowCalculator.DefaultZoom * 1e12,
        };
        channel.Calculate(TestContext.Current.CancellationToken);
        Assert.Equal(0, channel.LastDdPixels);
    }

    [Fact]
    public void DD_Edges_Setting_Round_Trips()
    {
        var p = new FractalParameters { BlackHoleDdEdges = false };
        var q = new FractalParameters();
        RegionFractalParams.Snapshot(FractalType.BlackHoleShadow, p)!.ApplyTo(q);
        Assert.False(q.BlackHoleDdEdges);
        Assert.False(p.Clone().BlackHoleDdEdges);
        Assert.True(new FractalParameters().BlackHoleDdEdges);
    }
}
