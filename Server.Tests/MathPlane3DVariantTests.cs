// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/MathPlane3DVariantTests.cs
//
// #1741 — enum names in --param (KifsFold=Dodecahedron as well as KifsFold=3), and the Math
// Plane 3D section's sub-type dropdown for fractal / orbit objects.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlane3DVariantTests
{
    private static KeyValuePair<string, string> Kv(string k, string v) => new(k, v);

    private static IReadOnlyList<KeyValuePair<string, string>> Snap(string key, string value, FractalType family)
    {
        var rp = RegionFractalParams.FromKeyValues(new[] { Kv(key, value) }, family, out var err);
        Assert.True(rp != null, err);
        var p = new FractalParameters();
        rp!.ApplyTo(p);
        return new[] { Kv(key, typeof(FractalParameters).GetProperty(key)!.GetValue(p)!.ToString()!) };
    }

    // ── --param ──

    public static IEnumerable<object[]> EnumKeys()
        => RegionFractalParams.KeyNames.Where(k => RegionFractalParams.EnumTypeOf(k) != null).Select(k => new object[] { k });

    [Fact]
    public void EveryEnumKey_IsFound()
    {
        var keys = EnumKeys().Select(o => (string)o[0]).ToList();
        Assert.Contains("KifsFold", keys);
        Assert.Contains("KleinianPreset", keys);
        Assert.Contains("BicomplexSliceAxis", keys);
        Assert.Contains("BilliardGeometry", keys);
        Assert.True(keys.Count >= 17, string.Join(", ", keys));
        // A whole-number key that is not an enum is not one.
        Assert.Null(RegionFractalParams.EnumTypeOf("KifsIterations"));
        Assert.Null(RegionFractalParams.EnumTypeOf("BulbPower"));
    }

    [Theory]
    [MemberData(nameof(EnumKeys))]
    public void AnEnumKey_TakesItsMembersNames_AsWellAsTheirNumbers(string key)
    {
        var enumType = RegionFractalParams.EnumTypeOf(key)!;
        foreach (var name in Enum.GetNames(enumType))
        {
            int number = Convert.ToInt32(Enum.Parse(enumType, name));
            var byNumber = RegionFractalParams.FromKeyValues(new[] { Kv(key, number.ToString()) }, null, out var e1);
            Assert.True(byNumber != null, e1);
            foreach (var spelling in new[] { name, name.ToLowerInvariant(), name.ToUpperInvariant(), " " + name + " " })
            {
                var byName = RegionFractalParams.FromKeyValues(new[] { Kv(key, spelling) }, null, out var e2);
                Assert.True(byName != null, $"{key}={spelling}: {e2}");
                Assert.Equal(byNumber!.ToKeyValues(), byName!.ToKeyValues());
            }
        }
    }

    [Fact]
    public void ABadName_ListsTheNames_AndCombinationsAreRefused()
    {
        Assert.Null(RegionFractalParams.FromKeyValues(new[] { Kv("KifsFold", "Dodeca") }, null, out var err));
        Assert.Contains("KifsFold", err);
        Assert.Contains("Menger, Sierpinski, Octahedron, Dodecahedron, MandelboxRot", err);
        Assert.Null(RegionFractalParams.FromKeyValues(new[] { Kv("KifsFold", "Menger,Sierpinski") }, null, out _));
        // A non-enum whole-number key still wants a number.
        Assert.Null(RegionFractalParams.FromKeyValues(new[] { Kv("KifsIterations", "ten") }, null, out var err2));
        Assert.Contains("whole number", err2);
    }

    [Fact]
    public void ANameApplies_AsItsNumberDoes()
    {
        Assert.Equal(Snap("KifsFold", "3", FractalType.Kifs), Snap("KifsFold", "Dodecahedron", FractalType.Kifs));
        Assert.Equal("Dodecahedron", Snap("KifsFold", "dodecahedron", FractalType.Kifs)[0].Value);
        Assert.Equal("CubeCorner8", Snap("KleinianPreset", "cubecorner8", FractalType.Kleinian)[0].Value);
    }

    // ── The variant table ──

    [Fact]
    public void TheVariantTable_AgreesWithTheParser()
    {
        Assert.Equal(new[] { FractalType.Kifs, FractalType.Kleinian, FractalType.BicomplexMandelbrot },
                     MathPlane3DScene.FractalVariants.Select(v => v.Type));
        foreach (var v in MathPlane3DScene.FractalVariants)
        {
            Assert.Equal(v.Enum, RegionFractalParams.EnumTypeOf(v.Key));
            Assert.Contains(v.Default, v.Names);
            Assert.Equal(typeof(FractalParameters).GetProperty(v.Key)!.GetValue(new FractalParameters())!.ToString(), v.Default);
            Assert.All(v.Names, n => Assert.True(Enum.IsDefined(v.Enum, Enum.Parse(v.Enum, n))));
        }
        Assert.DoesNotContain("Custom", MathPlane3DScene.VariantOf(FractalType.Kleinian)!.Names);
        Assert.Null(MathPlane3DScene.VariantOf(FractalType.Mandelbulb));
    }

    private static MathPlaneObject Obj(string stmt)
    {
        Assert.True(MathPlane3DScene.TryParse("v1; " + stmt, out var s, out var err), err?.ToString());
        return s.Objects.Single();
    }

    [Theory]
    [InlineData("fractal F Kifs", "Menger")]
    [InlineData("fractal F Kifs KifsFold=3", "Dodecahedron")]
    [InlineData("fractal F Kifs KifsFold=octahedron", "Octahedron")]
    [InlineData("orbit o Kifs 0 0 0 KifsFold=1", "Sierpinski")]
    [InlineData("fractal F Kleinian", "Tetrahedral")]
    [InlineData("fractal F BicomplexMandelbrot BicomplexSliceAxis=2", "I")]
    public void VariantValue_ReadsANameOrANumber_ElseTheDefault(string stmt, string want)
        => Assert.Equal(want, MathPlane3DScene.VariantValue(Obj(stmt)));

    [Fact]
    public void WithVariant_ReplacesInPlace_AndKeepsTheOtherSettings()
    {
        var a = MathPlane3DScene.WithVariant(Obj("fractal F Kifs KifsIterations=10 KifsFold=3 KifsScale=2"), "Sierpinski");
        Assert.Equal(new[] { "Kifs", "KifsIterations=10", "KifsFold=Sierpinski", "KifsScale=2" }, a.Expressions);
        var b = MathPlane3DScene.WithVariant(Obj("fractal F Kifs KifsIterations=10"), "Octahedron");
        Assert.Equal(new[] { "Kifs", "KifsIterations=10", "KifsFold=Octahedron" }, b.Expressions);
        var c = Obj("fractal F Mandelbulb BulbPower=6");
        Assert.Same(c, MathPlane3DScene.WithVariant(c, "Menger"));
    }

    [Fact]
    public void AFoldByName_MeshesAsTheSameFoldByNumber()
    {
        var byName = MathPlane3DFractalMesher.Build(Obj("fractal F Kifs KifsFold=Dodecahedron mesh 24"), out var e1)!;
        var byNumber = MathPlane3DFractalMesher.Build(Obj("fractal F Kifs KifsFold=3 mesh 24"), out var e2)!;
        Assert.Null(e1); Assert.Null(e2);
        Assert.Equal(byNumber.Triangles, byName.Triangles);
        Assert.Equal(byNumber.Vertices, byName.Vertices);
        var menger = MathPlane3DFractalMesher.Build(Obj("fractal F Kifs mesh 24"), out _)!;
        Assert.NotEqual(menger.Triangles.Length, byName.Triangles.Length);
    }

    // ── Control Center ──

    [Fact]
    public void TheDropdown_ShowsForTypesWithSubTypes_AndWritesTheSetting()
    {
        var view = new FractalViewState { FractalType = FractalType.MathPlane3D, Zoom = 1 };
        view.FractalParameters.MathPlane3DScene = "v1";
        var vm = new MathPlane3DSectionViewModel(view, () => { }, () => { });
        vm.AddFractalCommand.Execute().Subscribe();
        Assert.False(vm.HasVariant);                          // Mandelbulb
        vm.SelectedFractalType = "Kifs";
        Assert.True(vm.HasVariant);
        Assert.Equal(new[] { "Menger", "Sierpinski", "Octahedron", "Dodecahedron", "MandelboxRot" }, vm.VariantChoices);
        Assert.Equal("Menger", vm.SelectedVariant);
        vm.SelectedFractalSettings = "KifsIterations=10";
        vm.SelectedVariant = "Dodecahedron";
        Assert.Null(vm.EditError);
        Assert.Contains("fractal S1 Kifs KifsIterations=10 KifsFold=Dodecahedron", view.FractalParameters.MathPlane3DScene);
        Assert.Equal("KifsIterations=10 KifsFold=Dodecahedron", vm.SelectedFractalSettings);
        Assert.Equal("Dodecahedron", vm.SelectedVariant);

        vm.SelectedFractalType = "Kleinian";
        Assert.DoesNotContain("Custom", vm.VariantChoices);
        Assert.Equal("Tetrahedral", vm.SelectedVariant);
        vm.SelectedFractalType = "Mandelbox";
        Assert.False(vm.HasVariant);

        // Orbits get it too.
        vm.AddOrbitCommand.Execute().Subscribe();
        vm.SelectedFractalType = "Kifs";
        vm.SelectedVariant = "Sierpinski";
        Assert.Contains("orbit o1 Kifs 0.6 0.4 0.5 KifsFold=Sierpinski", view.FractalParameters.MathPlane3DScene);
    }
}
