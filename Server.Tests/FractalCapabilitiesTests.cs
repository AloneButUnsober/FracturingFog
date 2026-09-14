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

    // SupportsVideoLeg = 2D zoom (P1) OR 3D camera (P3); NonSpatial + user code out.
    [Theory]
    [InlineData(FractalType.Mandelbrot, true)]   // 2D zoom
    [InlineData(FractalType.Glynn, true)]        // 2D zoom
    [InlineData(FractalType.Mandelbulb, true)]   // 3D camera
    [InlineData(FractalType.Kleinian, true)]     // 3D camera
    [InlineData(FractalType.UserBulb, false)]    // user code
    [InlineData(FractalType.UserEquation, false)]// user code
    [InlineData(FractalType.Plasma, false)]      // NonSpatial (P4)
    [InlineData(FractalType.BuddhaBrot, false)]  // NonSpatial (P4)
    public void SupportsVideoLeg_UnionOfP1AndP3(FractalType t, bool expected)
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
