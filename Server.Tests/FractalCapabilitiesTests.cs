// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FractalCapabilitiesTests
{
    [Theory]
    [InlineData(FractalType.Mandelbrot)]
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.BurningShip)]
    [InlineData(FractalType.Tricorn)]
    [InlineData(FractalType.Multibrot)]
    [InlineData(FractalType.Phoenix)]
    [InlineData(FractalType.Newton)]
    [InlineData(FractalType.Nova)]
    [InlineData(FractalType.Magnet1)]
    [InlineData(FractalType.Magnet2)]
    [InlineData(FractalType.Glynn)]
    [InlineData(FractalType.Halley)]
    [InlineData(FractalType.Secant)]
    [InlineData(FractalType.Spider)]
    [InlineData(FractalType.TearDrop)]
    [InlineData(FractalType.Apollonian)]
    [InlineData(FractalType.GeneratedMandelbrotZ2)]
    [InlineData(FractalType.GeneratedTricorn)]
    [InlineData(FractalType.GeneratedBurningShip)]
    public void Zoomable2D_Families_Classify(FractalType t)
        => Assert.Equal(FractalMotionClass.Zoomable2D, FractalMotionCapabilities.MotionClass(t));

    [Theory]
    [InlineData(FractalType.Mandelbulb)]
    [InlineData(FractalType.Mandelbox)]
    [InlineData(FractalType.Kifs)]
    [InlineData(FractalType.QuaternionJulia)]
    [InlineData(FractalType.QuaternionMandelbrot)]
    [InlineData(FractalType.Kleinian)]
    [InlineData(FractalType.BicomplexMandelbrot)]
    [InlineData(FractalType.UserBulb)]
    public void Raymarch3D_Families_Classify(FractalType t)
        => Assert.Equal(FractalMotionClass.Raymarch3D, FractalMotionCapabilities.MotionClass(t));

    [Theory]
    [InlineData(FractalType.Plasma)]
    [InlineData(FractalType.AcidWarp)]
    [InlineData(FractalType.Flame)]
    [InlineData(FractalType.Dla)]
    [InlineData(FractalType.Logistic)]
    [InlineData(FractalType.IFS)]
    [InlineData(FractalType.LSystem)]
    [InlineData(FractalType.StrangeAttractor)]
    [InlineData(FractalType.BuddhaBrot)]
    [InlineData(FractalType.Nebulabrot)]
    [InlineData(FractalType.AntiBuddhabrot)]
    [InlineData(FractalType.AntiNebulabrot)]
    public void NonSpatial_Families_Classify(FractalType t)
        => Assert.Equal(FractalMotionClass.NonSpatial, FractalMotionCapabilities.MotionClass(t));

    [Theory]
    [InlineData(FractalType.UserEquation)]
    [InlineData(FractalType.Sandbox)]
    [InlineData(FractalType.UserBulb)]
    public void UserCode_Families_AreFlagged(FractalType t)
        => Assert.True(FractalMotionCapabilities.IsUserCode(t));

    [Theory]
    [InlineData(FractalType.Mandelbrot)]
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.Mandelbulb)]
    [InlineData(FractalType.Plasma)]
    public void NonUserCode_Families_NotFlagged(FractalType t)
        => Assert.False(FractalMotionCapabilities.IsUserCode(t));

    // P1 (#91): eligible for a real zoom leg iff 2D-zoomable AND not user code.
    [Theory]
    [InlineData(FractalType.Mandelbrot, true)]
    [InlineData(FractalType.Julia, true)]
    [InlineData(FractalType.Glynn, true)]
    [InlineData(FractalType.Apollonian, true)]
    // Raymarch3D — deferred to P3.
    [InlineData(FractalType.Mandelbulb, false)]
    [InlineData(FractalType.Kifs, false)]
    // NonSpatial — deferred to P4.
    [InlineData(FractalType.Plasma, false)]
    [InlineData(FractalType.Logistic, false)]
    // User-code 2D — zoomable geometry, but excluded (security).
    [InlineData(FractalType.UserEquation, false)]
    [InlineData(FractalType.Sandbox, false)]
    public void SupportsVideoZoomLeg_MatchesP1Policy(FractalType t, bool expected)
        => Assert.Equal(expected, FractalMotionCapabilities.SupportsVideoZoomLeg(t));

    // P3 (#93): eligible for a camera-fly leg iff raymarched-3D AND not user code.
    [Theory]
    [InlineData(FractalType.Mandelbulb, true)]
    [InlineData(FractalType.Mandelbox, true)]
    [InlineData(FractalType.Kifs, true)]
    [InlineData(FractalType.QuaternionJulia, true)]
    [InlineData(FractalType.QuaternionMandelbrot, true)]
    [InlineData(FractalType.Kleinian, true)]
    [InlineData(FractalType.BicomplexMandelbrot, true)]
    // UserBulb is raymarched-3D but user code — excluded.
    [InlineData(FractalType.UserBulb, false)]
    // 2D + NonSpatial are not camera legs.
    [InlineData(FractalType.Mandelbrot, false)]
    [InlineData(FractalType.Julia, false)]
    [InlineData(FractalType.Plasma, false)]
    public void SupportsVideoCameraLeg_MatchesP3Policy(FractalType t, bool expected)
        => Assert.Equal(expected, FractalMotionCapabilities.SupportsVideoCameraLeg(t));

    // SupportsVideoLeg spans 2D zoom (P1), 3D camera (P3) and non-spatial hold
    // (P4); only user-code families are out. (P4 flipped Plasma/BuddhaBrot to
    // true — they now play static-hold legs.)
    [Theory]
    [InlineData(FractalType.Mandelbrot, true)]   // 2D zoom
    [InlineData(FractalType.Glynn, true)]        // 2D zoom
    [InlineData(FractalType.Mandelbulb, true)]   // 3D camera
    [InlineData(FractalType.Kleinian, true)]     // 3D camera
    [InlineData(FractalType.Plasma, true)]       // non-spatial hold (P4)
    [InlineData(FractalType.BuddhaBrot, true)]   // non-spatial hold (P4)
    [InlineData(FractalType.UserBulb, false)]    // user code
    [InlineData(FractalType.UserEquation, false)]// user code
    public void SupportsVideoLeg_UnionOfAllMotionKinds(FractalType t, bool expected)
        => Assert.Equal(expected, FractalMotionCapabilities.SupportsVideoLeg(t));

    // Camera-leg and zoom-leg sets are disjoint (a family is never both).
    [Fact]
    public void CameraLeg_And_ZoomLeg_AreDisjoint()
    {
        foreach (FractalType t in Enum.GetValues(typeof(FractalType)))
            Assert.False(
                FractalMotionCapabilities.SupportsVideoZoomLeg(t)
                && FractalMotionCapabilities.SupportsVideoCameraLeg(t),
                $"{t} classified as both zoom and camera leg");
    }

    // P4 (#94): eligible for a static-hold leg iff non-spatial and not user code.
    [Theory]
    [InlineData(FractalType.Plasma, true)]
    [InlineData(FractalType.Flame, true)]
    [InlineData(FractalType.Dla, true)]
    [InlineData(FractalType.Logistic, true)]
    [InlineData(FractalType.IFS, true)]
    [InlineData(FractalType.LSystem, true)]
    [InlineData(FractalType.StrangeAttractor, true)]
    [InlineData(FractalType.BuddhaBrot, true)]
    [InlineData(FractalType.RandomTile, true)]
    // 2D + 3D families are not hold legs.
    [InlineData(FractalType.Mandelbrot, false)]
    [InlineData(FractalType.Mandelbulb, false)]
    public void SupportsVideoHoldLeg_MatchesP4Policy(FractalType t, bool expected)
        => Assert.Equal(expected, FractalMotionCapabilities.SupportsVideoHoldLeg(t));

    // SupportsVideoLeg now spans all three: 2D zoom, 3D camera, non-spatial hold.
    // Only user-code families are excluded outright.
    [Theory]
    [InlineData(FractalType.Mandelbrot, true)]   // 2D zoom
    [InlineData(FractalType.Mandelbulb, true)]   // 3D camera
    [InlineData(FractalType.Plasma, true)]       // hold
    [InlineData(FractalType.BuddhaBrot, true)]   // hold
    [InlineData(FractalType.UserBulb, false)]    // user code
    [InlineData(FractalType.UserEquation, false)]// user code
    [InlineData(FractalType.Sandbox, false)]     // user code
    public void SupportsVideoLeg_SpansAllThreeMotionKinds(FractalType t, bool expected)
        => Assert.Equal(expected, FractalMotionCapabilities.SupportsVideoLeg(t));

    // #806 param-sweep — the sweepable hold families (smooth + cheap).
    [Theory]
    [InlineData(FractalType.Logistic, true)]
    [InlineData(FractalType.AcidWarp, true)]
    // Non-smooth / slow hold families are NOT sweepable (stay static / Ken-Burns).
    [InlineData(FractalType.Plasma, false)]
    [InlineData(FractalType.Flame, false)]
    [InlineData(FractalType.Dla, false)]
    [InlineData(FractalType.BuddhaBrot, false)]
    // Non-hold families are never sweepable.
    [InlineData(FractalType.Mandelbrot, false)]
    [InlineData(FractalType.Mandelbulb, false)]
    public void SupportsVideoParamSweep_MatchesPolicy(FractalType t, bool expected)
        => Assert.Equal(expected, FractalMotionCapabilities.SupportsVideoParamSweep(t));

    // Sweepable ⊆ hold: a param sweep only ever applies to a static-hold family.
    [Fact]
    public void ParamSweep_IsSubsetOfHold()
    {
        foreach (FractalType t in Enum.GetValues(typeof(FractalType)))
            if (FractalMotionCapabilities.SupportsVideoParamSweep(t))
                Assert.True(FractalMotionCapabilities.SupportsVideoHoldLeg(t),
                    $"{t} is sweepable but not a hold leg");
    }

    // #806 Buddhabrot progressive accumulation — the four Buddhabrot-family types.
    [Theory]
    [InlineData(FractalType.BuddhaBrot, true)]
    [InlineData(FractalType.Nebulabrot, true)]
    [InlineData(FractalType.AntiBuddhabrot, true)]
    [InlineData(FractalType.AntiNebulabrot, true)]
    [InlineData(FractalType.Logistic, false)]
    [InlineData(FractalType.Plasma, false)]
    [InlineData(FractalType.Mandelbrot, false)]
    public void SupportsVideoBuddhaAccumulation_MatchesPolicy(FractalType t, bool expected)
        => Assert.Equal(expected, FractalMotionCapabilities.SupportsVideoBuddhaAccumulation(t));

    // Buddha-accumulation ⊆ hold, and disjoint from param-sweep (different hold
    // animation paths).
    [Fact]
    public void BuddhaAccumulation_SubsetOfHold_DisjointFromSweep()
    {
        foreach (FractalType t in Enum.GetValues(typeof(FractalType)))
        {
            if (!FractalMotionCapabilities.SupportsVideoBuddhaAccumulation(t)) continue;
            Assert.True(FractalMotionCapabilities.SupportsVideoHoldLeg(t),
                $"{t} accumulates but isn't a hold leg");
            Assert.False(FractalMotionCapabilities.SupportsVideoParamSweep(t),
                $"{t} is both param-sweep and Buddha-accumulation");
        }
    }

    // The three leg kinds partition the non-user-code families exactly (each gets
    // exactly one leg kind; user code gets none).
    [Fact]
    public void LegKinds_PartitionNonUserCodeFamilies()
    {
        foreach (FractalType t in Enum.GetValues(typeof(FractalType)))
        {
            int kinds =
                (FractalMotionCapabilities.SupportsVideoZoomLeg(t) ? 1 : 0)
                + (FractalMotionCapabilities.SupportsVideoCameraLeg(t) ? 1 : 0)
                + (FractalMotionCapabilities.SupportsVideoHoldLeg(t) ? 1 : 0);
            if (FractalMotionCapabilities.IsUserCode(t))
                Assert.Equal(0, kinds);
            else
                Assert.Equal(1, kinds);
        }
    }

    // Every enum value returns a defined motion class — a new family added to
    // the enum still yields a valid classification (defaults to NonSpatial so
    // it can never silently land a broken zoom leg).
    [Fact]
    public void EveryEnumValue_ReturnsDefinedMotionClass()
    {
        foreach (FractalType t in Enum.GetValues(typeof(FractalType)))
        {
            var mc = FractalMotionCapabilities.MotionClass(t);
            Assert.True(Enum.IsDefined(typeof(FractalMotionClass), mc), $"{t} → {mc}");
        }
    }
}
