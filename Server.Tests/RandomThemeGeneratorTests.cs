// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #434 slice 1 — the headless random colour-theme generator. Proves determinism
// (seed reproducibility), Kind-gating (only the relevant sections populate), and
// that artful/experimental ranges + valid stop geometry hold.

using System.Linq;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RandomThemeGeneratorTests
{
    [Fact]
    public void Deterministic_ForSameSeedKindMode()
    {
        var a = RandomThemeGenerator.Generate(ColorThemeKindDef.Phong3D, 12345, experimental: false);
        var b = RandomThemeGenerator.Generate(ColorThemeKindDef.Phong3D, 12345, experimental: false);

        Assert.Equal(a.Kind, b.Kind);
        Assert.Equal(a.Stops.Count, b.Stops.Count);
        for (int i = 0; i < a.Stops.Count; i++)
        {
            Assert.Equal(a.Stops[i].Position, b.Stops[i].Position);
            Assert.Equal((a.Stops[i].R, a.Stops[i].G, a.Stops[i].B),
                         (b.Stops[i].R, b.Stops[i].G, b.Stops[i].B));
        }
        Assert.Equal(a.Steepness, b.Steepness);
        Assert.Equal(a.KeyLight!.DiffR, b.KeyLight!.DiffR);
        Assert.Equal(a.PaletteGamma, b.PaletteGamma);
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentStops()
    {
        var a = RandomThemeGenerator.Generate(ColorThemeKindDef.Gradient, 1);
        var b = RandomThemeGenerator.Generate(ColorThemeKindDef.Gradient, 2);
        // Extremely unlikely to match across the whole stop list.
        bool identical = a.Stops.Count == b.Stops.Count &&
            a.Stops.Zip(b.Stops).All(p => p.First.R == p.Second.R && p.First.G == p.Second.G && p.First.B == p.Second.B);
        Assert.False(identical);
    }

    [Fact]
    public void Artful_HasFiveStops_Wild_ThreeToEight()
    {
        var artful = RandomThemeGenerator.Generate(ColorThemeKindDef.Gradient, 7, experimental: false);
        Assert.Equal(5, artful.Stops.Count);

        for (int seed = 0; seed < 40; seed++)
        {
            var wild = RandomThemeGenerator.Generate(ColorThemeKindDef.Gradient, seed, experimental: true);
            Assert.InRange(wild.Stops.Count, 3, 8);
        }
    }

    [Fact]
    public void Stops_AreValid_SpanZeroToOne()
    {
        var def = RandomThemeGenerator.Generate(ColorThemeKindDef.Cycling, 99);
        Assert.True(def.Stops.Count >= 2);
        Assert.Equal(0f, def.Stops.First().Position, 5);
        Assert.Equal(1f, def.Stops.Last().Position, 5);
        // strictly increasing positions
        for (int i = 1; i < def.Stops.Count; i++)
            Assert.True(def.Stops[i].Position > def.Stops[i - 1].Position);
    }

    [Fact]
    public void Gradient_HasNoLightRig_And_NeutralPostFx()
    {
        var def = RandomThemeGenerator.Generate(ColorThemeKindDef.Gradient, 3);
        Assert.Null(def.KeyLight);
        Assert.Null(def.FillLight);
        Assert.Null(def.RimLight);
        // Post-FX left neutral so a slideshow theme never hijacks global grading.
        Assert.Null(def.Brightness);
        Assert.Null(def.Contrast);
        Assert.Null(def.Adaptive);
        // In-set is always populated.
        Assert.NotNull(def.InSetColor);
    }

    [Fact]
    public void Phong3D_PopulatesLights_InUnitRange()
    {
        var def = RandomThemeGenerator.Generate(ColorThemeKindDef.Phong3D, 555);
        Assert.NotNull(def.KeyLight);
        Assert.NotNull(def.FillLight);
        foreach (var l in new[] { def.KeyLight!, def.FillLight! })
        {
            Assert.InRange(l.DiffR, 0f, 1f);
            Assert.InRange(l.SpecR, 0f, 1f);
            Assert.InRange(l.Shininess, 1f, 512f);
        }
    }

    [Fact]
    public void Pbr3D_HasMaterialBands_LastBandCatchAll()
    {
        var def = RandomThemeGenerator.Generate(ColorThemeKindDef.Pbr3D, 22);
        Assert.NotEmpty(def.MaterialBands);
        Assert.Equal(1f, def.MaterialBands.Last().UpperT, 5);
    }

    [Fact]
    public void OrbitTrap_SetsTrapFields()
    {
        var def = RandomThemeGenerator.Generate(ColorThemeKindDef.OrbitTrap, 8);
        Assert.True(def.TrapScale > 0f);
        Assert.True(def.TrapPower > 0f);
    }

    [Theory]
    [InlineData(0, 255, 0, 0)]      // red
    [InlineData(120, 0, 255, 0)]    // green
    [InlineData(240, 0, 0, 255)]    // blue
    public void HsvToRgb_PrimaryHues(double h, byte r, byte g, byte b)
    {
        var (gr, gg, gb) = RandomThemeGenerator.HsvToRgb(h, 1.0, 1.0);
        Assert.Equal((r, g, b), (gr, gg, gb));
    }

    // ── #434 slice 4 — capability-driven Kind selection ─────────────────────

    [Fact]
    public void KindsFor_None_IsGradientAndCyclingOnly()
    {
        var kinds = RandomThemeGenerator.KindsFor(FractalCapabilities.None);
        Assert.Equal(
            new[] { ColorThemeKindDef.Gradient, ColorThemeKindDef.Cycling },
            kinds);
    }

    [Fact]
    public void KindsFor_Orbit_AddsOrbitTrap_NotThreeD()
    {
        var kinds = RandomThemeGenerator.KindsFor(FractalCapabilities.SuppliesOrbit);
        Assert.Contains(ColorThemeKindDef.OrbitTrap, kinds);
        Assert.DoesNotContain(ColorThemeKindDef.Phong3D, kinds);
        Assert.DoesNotContain(ColorThemeKindDef.Pbr3D, kinds);
    }

    [Fact]
    public void KindsFor_Normals_AddsPhongAndPbr_NotOrbitTrap()
    {
        var kinds = RandomThemeGenerator.KindsFor(FractalCapabilities.SuppliesNormals);
        Assert.Contains(ColorThemeKindDef.Phong3D, kinds);
        Assert.Contains(ColorThemeKindDef.Pbr3D, kinds);
        Assert.DoesNotContain(ColorThemeKindDef.OrbitTrap, kinds);
    }

    [Fact]
    public void KindsFor_OrbitAndNormals_HasAllFive()
    {
        var kinds = RandomThemeGenerator.KindsFor(
            FractalCapabilities.SuppliesOrbit | FractalCapabilities.SuppliesNormals);
        Assert.Contains(ColorThemeKindDef.Gradient, kinds);
        Assert.Contains(ColorThemeKindDef.Cycling, kinds);
        Assert.Contains(ColorThemeKindDef.OrbitTrap, kinds);
        Assert.Contains(ColorThemeKindDef.Phong3D, kinds);
        Assert.Contains(ColorThemeKindDef.Pbr3D, kinds);
    }

    [Fact]
    public void KindsFor_Mandelbrot_IncludesOrbitTrapAndThreeD()
    {
        // Mandelbrot runs the full pipeline (orbit + normals) → the richest set.
        var kinds = RandomThemeGenerator.KindsFor(FractalCapabilityMap.For(FractalType.Mandelbrot));
        Assert.Contains(ColorThemeKindDef.OrbitTrap, kinds);
        Assert.Contains(ColorThemeKindDef.Phong3D, kinds);
    }

    [Fact]
    public void KindsFor_HistogramType_IsGradientCyclingOnly()
    {
        // A histogram/point-cloud family (no orbit, no normals) → safe 2D kinds.
        var kinds = RandomThemeGenerator.KindsFor(FractalCapabilityMap.For(FractalType.BuddhaBrot));
        Assert.DoesNotContain(ColorThemeKindDef.OrbitTrap, kinds);
        Assert.DoesNotContain(ColorThemeKindDef.Phong3D, kinds);
        Assert.DoesNotContain(ColorThemeKindDef.Pbr3D, kinds);
    }
}
