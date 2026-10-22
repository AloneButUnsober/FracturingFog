// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #1445 — near-touching defaults, ExitGap gates, muted misses and the PhaseSpace
// launch. The physics oracles are symmetries the geometry guarantees, not the
// implementation's own output: rotating a three-disk launch by 120° must move the
// exit to the next gap with the same bounce count; mirroring a phase-space launch
// must mirror the exit gap; a reversed exit ray must retrace its way back in.
public class BilliardWadaModesTests
{
    private static BilliardScene Scene(Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters();
        tweak?.Invoke(p);
        return BilliardScene.Build(p);
    }

    [Fact]
    public void Defaults_AreNearTouching_ExitGap_Muted_FarField()
    {
        var p = new FractalParameters();
        double centreDistance = p.BilliardSeparation * Math.Sqrt(3.0);
        double ratio = centreDistance / p.BilliardDiskRadius;
        Assert.InRange(ratio, 2.0, 2.5);   // the regime where the trapped set is visible
        Assert.Equal(BilliardGateMode.ExitGap, p.BilliardGateMode);
        Assert.Equal(BilliardLaunchMode.FarField, p.BilliardLaunchMode);
        Assert.True(p.BilliardMuteMisses);

        var c = p.Clone();
        Assert.Equal(p.BilliardGateMode, c.BilliardGateMode);
        Assert.Equal(p.BilliardLaunchMode, c.BilliardLaunchMode);
        Assert.Equal(p.BilliardMuteMisses, c.BilliardMuteMisses);
    }

    [Fact]
    public void ExitGap_GateCount_FollowsTheRing()
    {
        Assert.Equal(3, Scene().GateCount);
        Assert.Equal(5, Scene(p => { p.BilliardGeometry = BilliardGeometry.Ring; p.BilliardDiskCount = 5; }).GateCount);
        // No ring gaps: NDisk and a one-disk ring fall back to compass sectors.
        Assert.Equal(7, Scene(p => { p.BilliardGeometry = BilliardGeometry.NDisk; p.BilliardGateCount = 7; }).GateCount);
        Assert.Equal(7, Scene(p => { p.BilliardGeometry = BilliardGeometry.Ring; p.BilliardDiskCount = 1; p.BilliardGateCount = 7; }).GateCount);
        Assert.Equal(6, Scene(p => p.BilliardGateMode = BilliardGateMode.Sector).GateCount);
    }

    [Fact]
    public void ExitGap_Gates_AreCentredOnTheGaps()
    {
        // ThreeDisk centres at 90°, 210°, 330°; the gap between disk k and k+1 is
        // centred at 150°, 270°, 30°. A ray heading straight out of a gap is that gate.
        var s = Scene();
        foreach (var (deg, gate) in new[] { (150.0, 0), (270.0, 1), (30.0, 2) })
        {
            double a = deg * Math.PI / 180.0;
            Assert.Equal(gate, s.Gate(Math.Cos(a), Math.Sin(a)));
        }
    }

    [Fact]
    public void ThreeDisk_ExitBasins_HaveThreeFoldRotationSymmetry()
    {
        var s = Scene();
        const int n = 160;
        int compared = 0, agree = 0, chaotic = 0;
        var gates = new HashSet<int>();
        double rot = 2.0 * Math.PI / 3.0;
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            double b = -1.8 + 3.6 * (i + 0.5) / n;
            double phi = rot * (j + 0.5) / n;
            var o1 = s.LaunchFarField(b, phi);
            if (o1.Bounces == 0) continue;   // misses carry no scattering
            var o2 = s.LaunchFarField(b, phi + rot);
            compared++;
            if (o1.Bounces >= 3) chaotic++;
            gates.Add(o1.GateId);
            int expect = o1.GateId < 0 ? -1 : (o1.GateId + 1) % 3;
            if (o2.GateId == expect && o2.Bounces == o1.Bounces) agree++;
        }
        // Preconditions: the sample really scatters into all three gaps, with
        // multi-bounce (chaotic) rays present — otherwise the check is vacuous.
        Assert.True(compared > n * n / 4, $"only {compared} rays hit a mirror");
        Assert.Contains(0, gates); Assert.Contains(1, gates); Assert.Contains(2, gates);
        Assert.True(chaotic > compared / 50, $"only {chaotic} of {compared} rays bounced 3+ times");
        // Rounding flips a few rays on the fractal boundary; the rest must map exactly.
        Assert.True(agree >= compared * 0.99, $"rotation symmetry held for {agree}/{compared}");
    }

    [Fact]
    public void PhaseSpace_ExitBasins_HaveMirrorSymmetry()
    {
        // Disk 0 sits on the +y axis. Mirroring x → -x maps surface angle s → π - s
        // and flips the outgoing angle's sign; the gaps at 150° and 30° swap and the
        // gap at 270° stays, so gate g → 2 - g.
        var s = Scene(p => p.BilliardLaunchMode = BilliardLaunchMode.PhaseSpace);
        const int n = 160;
        int compared = 0, agree = 0, chaotic = 0;
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            double ang = -Math.PI + 2.0 * Math.PI * (i + 0.5) / n;
            double sinA = -0.98 + 1.96 * (j + 0.5) / n;
            var o1 = s.LaunchPhaseSpace(ang, sinA);
            Assert.True(o1.Valid);
            if (o1.Bounces == 0) continue;
            var o2 = s.LaunchPhaseSpace(Math.PI - ang, -sinA);
            compared++;
            if (o1.Bounces >= 3) chaotic++;
            int expect = o1.GateId < 0 ? -1 : 2 - o1.GateId;
            if (o2.GateId == expect && o2.Bounces == o1.Bounces) agree++;
        }
        Assert.True(compared > n * n / 10, $"only {compared} launches reached another disk");
        Assert.True(chaotic > compared / 50, $"only {chaotic} of {compared} launches bounced 3+ times");
        Assert.True(agree >= compared * 0.99, $"mirror symmetry held for {agree}/{compared}");
    }

    [Fact]
    public void ReversedExitRay_RetracesTheIncomingLine()
    {
        var s = Scene();
        int checkedRays = 0;
        for (int i = 0; i < 400; i++)
        {
            double b = -1.6 + 3.2 * (i + 0.5) / 400;
            double phi = 0.37;
            var fwd = s.LaunchFarField(b, phi);
            if (fwd.GateId < 0 || fwd.Bounces == 0 || fwd.Bounces > 10) continue;
            var back = s.Trace(fwd.EndX, fwd.EndY, -fwd.EndDx, -fwd.EndDy);
            Assert.Equal(fwd.Bounces, back.Bounces);
            // Leaves along the reversed incoming direction ...
            Assert.Equal(-Math.Cos(phi), back.EndDx, 6);
            Assert.Equal(-Math.Sin(phi), back.EndDy, 6);
            // ... on the incoming line (signed offset b from the origin).
            double perp = -Math.Sin(phi) * back.EndX + Math.Cos(phi) * back.EndY;
            Assert.Equal(b, perp, 6);
            checkedRays++;
        }
        Assert.True(checkedRays > 50, $"only {checkedRays} rays scattered and escaped");
    }

    private static ChaoticBilliardCalculator Render(Action<FractalParameters>? tweak = null, int w = 128, int h = 72)
    {
        var p = new FractalParameters();
        tweak?.Invoke(p);
        var calc = new ChaoticBilliardCalculator(w, h) { FractalParameters = p, Zoom = 1.0 };
        calc.Calculate(default);
        return calc;
    }

    // Fraction of launched pixels whose ray survives 5+ bounces: the long-lived
    // chaotic transients near the trapped set, i.e. how much of the frame is fractal
    // rather than first-hit shadow. Traced on the render's own plane mapping at zoom 1.
    private static double DeepScatter(Action<FractalParameters>? tweak, int w = 256, int h = 144)
    {
        var s = Scene(tweak);
        double pitch = 4.0 / w;
        int deep = 0, launched = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var o = s.Launch((x - w * 0.5) * pitch, (y - h * 0.5) * pitch);
            if (!o.Valid) continue;
            launched++;
            if (o.Bounces >= 5) deep++;
        }
        return deep / (double)Math.Max(1, launched);
    }

    [Fact]
    public void MuteMisses_DarkensOnlyRaysThatNeverHit()
    {
        var on = Render();
        var off = Render(p => p.BilliardMuteMisses = false);
        int misses = 0;
        for (int i = 0; i < on.ColorBuffer.Length; i++)
        {
            if (on.SmoothBuffer[i] == 0f)
            {
                misses++;
                Assert.Equal(ChaoticBilliardCalculator.Mute(off.ColorBuffer[i]), on.ColorBuffer[i]);
            }
            else
            {
                Assert.Equal(off.ColorBuffer[i], on.ColorBuffer[i]);
            }
        }
        Assert.True(misses > 0, "default view has no direct misses to mute");
        uint m = ChaoticBilliardCalculator.Mute(0xFFFFFFFF);
        Assert.True((m & 0xFF) < 0x40, "muted colour is not dark");
    }

    [Fact]
    public void PhaseSpace_PaintsOutsideTheSection_AndScattersDeeperThanFarField()
    {
        var ps = Render(p => p.BilliardLaunchMode = BilliardLaunchMode.PhaseSpace, 128, 128);
        int w = 128, outside = 0;
        double pitch = 4.0 / w;
        for (int y = 0; y < 128; y++)
        {
            bool expectOutside = Math.Abs((y - 64) * pitch) >= 1.0;
            for (int x = 0; x < w; x++)
            {
                bool isOutside = ps.ColorBuffer[y * w + x] == ChaoticBilliardCalculator.OutsideSectionColor;
                if (expectOutside) { Assert.True(isOutside); outside++; }
            }
        }
        Assert.True(outside > 0);

        // Measured 11.1 % vs 3.1 % at 320x180.
        double dPs = DeepScatter(p => p.BilliardLaunchMode = BilliardLaunchMode.PhaseSpace);
        double dFf = DeepScatter(null);
        Assert.True(dPs > 2 * dFf, $"phase space {dPs:P2} vs far field {dFf:P2} pixels with 5+ bounces");
    }

    [Fact]
    public void NearTouchingDefaults_ScatterFarDeeperThanThe627Defaults()
    {
        // Measured 3.1 % vs 0.12 % at 320x180.
        double now = DeepScatter(null);
        double old = DeepScatter(p => p.BilliardDiskRadius = 0.4);
        Assert.True(now > 5 * old, $"new defaults {now:P2} vs #627 {old:P2} pixels with 5+ bounces");
    }

    [Fact]
    public void LegacySnapshot_KeepsThe627Look()
    {
        // A region saved before #1445: all seven #627 fields, none of the new ones.
        const string json = "{\"BilliardGeometry\":0,\"BilliardDiskCount\":5,\"BilliardDiskRadius\":0.4," +
                            "\"BilliardSeparation\":1.0,\"BilliardMaxBounces\":256,\"BilliardGateCount\":6,\"BilliardSeed\":1}";
        var legacy = JsonSerializer.Deserialize<RegionFractalParams>(json)!;
        var p = new FractalParameters();
        RegionFractalParams.ResetFamilyToDefaults(FractalType.ChaoticBilliard, p);
        legacy.ApplyTo(p);
        Assert.Equal(BilliardGateMode.Sector, p.BilliardGateMode);
        Assert.False(p.BilliardMuteMisses);
        Assert.Equal(BilliardLaunchMode.FarField, p.BilliardLaunchMode);
        Assert.Equal(0.4, p.BilliardDiskRadius);

        // A partial overlay (hand-typed --param) is not mistaken for a legacy snapshot.
        var partial = JsonSerializer.Deserialize<RegionFractalParams>("{\"BilliardGeometry\":2}")!;
        var q = new FractalParameters();
        partial.ApplyTo(q);
        Assert.Equal(BilliardGateMode.ExitGap, q.BilliardGateMode);
        Assert.True(q.BilliardMuteMisses);
    }

    [Fact]
    public void NewModes_RoundTripThroughTheRegionSnapshot()
    {
        var src = new FractalParameters
        {
            BilliardLaunchMode = BilliardLaunchMode.PhaseSpace,
            BilliardGateMode = BilliardGateMode.Sector,
            BilliardMuteMisses = false,
        };
        var snap = RegionFractalParams.Snapshot(FractalType.ChaoticBilliard, src)!;
        var json = JsonSerializer.Serialize(snap);
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(json)!.ApplyTo(dst);
        Assert.Equal(BilliardLaunchMode.PhaseSpace, dst.BilliardLaunchMode);
        Assert.Equal(BilliardGateMode.Sector, dst.BilliardGateMode);
        Assert.False(dst.BilliardMuteMisses);
    }
}
