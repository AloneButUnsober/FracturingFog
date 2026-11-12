// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/MathPlane3DBridgeDemoTests.cs
//
// #1736 (Math Plane 3D fractal bridge B6) — the fractal demos. The generic demo tests
// (MathPlane3DPresetTests: parse, headless render, --param, Command builder, guide) cover them
// like every other demo; these check each demo's stated numbers. The orbit is rebuilt by the
// scene's own orbit object, and the numbers are worked by hand from each family's step (see the
// demo text), so a wrong idea text or a moved start point fails here.

using System;
using System.Linq;
using FracturingFog.Calculators;
using FracturingFog.Help;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlane3DBridgeDemoTests
{
    private static (MathPlane3DOrbitPath Orbit, MathPlaneObject? Fractal) Load(string name)
    {
        var d = MathPlane3DPresets.Find(name) ?? throw new Xunit.Sdk.XunitException("no demo " + name);
        Assert.True(MathPlane3DScene.TryParse(d.Scene, out var s, out var e), e?.ToString());
        var orbit = s.Objects.Single(o => o.Kind == MathPlaneObjectKind.Curve && o.CurveKind == MathPlaneCurveKind.Fractal);
        var fractal = s.Objects.SingleOrDefault(o => o.Kind == MathPlaneObjectKind.Surface && o.CurveKind == MathPlaneCurveKind.Fractal);
        var path = MathPlane3DOrbit.Build(orbit, out var err);
        Assert.True(path != null, err);
        if (fractal != null)
        {
            // The fractal object and the orbit are the same type with the same settings, so they line up.
            Assert.Equal(fractal.Sources, orbit.Sources);
            Assert.NotNull(MathPlane3DFractalMesher.Build(fractal with { Mesh = 16 }, out var merr));
            Assert.Null(merr);
        }
        return (path!, fractal);
    }

    /// <summary>A point in the fractal's own axes (y up), as the scene shows it (z up).</summary>
    private static (double X, double Y, double Z) Scene(double x, double y, double z) => (x, -z, y);

    private static void Near((double X, double Y, double Z) want, (double X, double Y, double Z) got, double tol = 1e-12)
    {
        Assert.Equal(want.X, got.X, tol);
        Assert.Equal(want.Y, got.Y, tol);
        Assert.Equal(want.Z, got.Z, tol);
    }

    private static double Len((double X, double Y, double Z) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    [Fact]
    public void TheMandelbulbOrbit_StaysNear_ThenEscapesAtStep4()
    {
        var (o, _) = Load("Mandelbulb: an escaping orbit");
        Assert.True(o.Escaped);
        Assert.Equal(5, o.Points.Count);   // z0 .. z4
        Assert.All(o.Points.Take(4), p => Assert.InRange(Len(p), 0, 1.25));
        Assert.InRange(Len(o.Points[4]), 4.85, 4.95);
    }

    [Fact]
    public void TheMandelboxOrbit_ScalesFirst_ThenFoldsAndScales()
    {
        var (o, f) = Load("Mandelbox: fold, then scale");
        Assert.Null(f);   // the s = 2 block would hide the orbit (see the idea text)
        Assert.Equal(4, o.Points.Count);   // steps 3
        // The fold box and the unit sphere the text names are in the scene.
        var d = MathPlane3DPresets.Find("Mandelbox: fold, then scale")!;
        Assert.Contains("polyline box -1 -1 -1 1 -1 -1", d.Scene);
        Assert.Contains("implicit \"x^2 + y^2 + z^2 - 1\"", d.Scene);
        var p = new FractalParameters();
        Assert.Equal((2.0, 1.0, 0.5), (p.MandelboxScale, p.MandelboxFixedRadius, p.MandelboxMinRadius));
        Near(Scene(0.9, 0.6, -0.3), o.Points[0]);
        // Step 1: inside the box (|each| <= 1), |z|^2 = 1.26 >= the fixed radius^2 1: no fold; 2 z + c.
        Near(Scene(2.7, 1.8, -0.9), o.Points[1]);
        // Step 2: box fold x 2.7 -> 2 - 2.7 = -0.7, y 1.8 -> 0.2; |z|^2 = 1.34: no sphere fold; 2 z + c.
        Near(Scene(-0.5, 1.0, -2.1), o.Points[2]);
    }

    [Fact]
    public void TheKifsStep_FoldsAcrossXPlusY_ThenScalesAboutTheCorner()
    {
        var (o, _) = Load("KIFS fold step beside the mesh");
        Assert.Equal(3, o.Points.Count);   // steps 2
        Near(Scene(-0.8, 0.3, 0.6), o.Points[0]);
        // x + y = -0.5 < 0: (x, y) -> (-y, -x) = (-0.3, 0.8); x + z = 0.3, y + z = 1.4: no more folds.
        // Then 2 z - (1, 1, 1).
        Near(Scene(-1.6, 0.6, 0.2), o.Points[1]);
    }

    [Fact]
    public void TheQuaternionJuliaOrbit_EscapesAtStep7()
    {
        var (o, f) = Load("Quaternion Julia slice and its orbit");
        Assert.True(o.Escaped);
        Assert.Equal(8, o.Points.Count);   // z0 .. z7
        var p = new FractalParameters();
        Assert.Equal((-0.2, 0.4, -0.4, -0.4), (p.QJuliaCX, p.QJuliaCY, p.QJuliaCZ, p.QJuliaCW));   // the c the text names
        Assert.Equal(0.0, p.QJuliaSliceW);
        Assert.Single(f!.Sources!);   // the defaults: no settings
    }

    [Fact]
    public void TheHelpText_NamesTheFractalDemos()
    {
        foreach (var word in new[] { "Mandelbulb: an escaping orbit", "Mandelbox: fold, then scale",
                                     "KIFS fold step beside the mesh", "Quaternion Julia slice and its orbit" })
            Assert.Contains(word, HelpTextBundle.MathPlane3DText);
    }
}
