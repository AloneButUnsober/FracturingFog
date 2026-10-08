// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

using FracturingFog.Abstractions.Explore;
using FracturingFog.Explore;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

// Interesting-location finder S7 (#1191): the R² Newton finders for Multibrot,
// Burning Ship, Tricorn and User Equation. Fixtures are solved by hand
// (period-2 nuclei and M(2, p) points), and every nucleus is re-checked by
// rendering it through the family's real calculator.
public sealed class GeneralFinderTests
{
    private readonly ITestOutputHelper _out;
    public GeneralFinderTests(ITestOutputHelper output) => _out = output;

    private static readonly double S3 = Math.Sqrt(3) / 2;

    private static void Near(double ex, double ey, V2 got, double tol = 1e-12)
        => Assert.True(Math.Abs(got.X - ex) <= tol && Math.Abs(got.Y - ey) <= tol,
                       $"expected ({ex:R}, {ey:R}), got ({got.X:R}, {got.Y:R})");

    private static GeneralNucleus Nucleus(IOrbitMap map, double x, double y, int p, double reach = 0.1)
    {
        var r = GeneralFinder.FindNucleus(map, new V2(x, y), p, reach);
        Assert.True(r.HasValue, $"{map.Name}: no period-{p} nucleus near ({x}, {y})");
        return r!.Value;
    }

    // ── Hand-solved nuclei ───────────────────────────────────────────────

    [Fact]
    public void Generic_Z2_ReproducesMandelbrotNuclei()
    {
        var m = new MultibrotMap(2);
        Near(-1, 0, Nucleus(m, -0.95, 0.03, 2).Point);
        Near(-1.7548776662466927, 0, Nucleus(m, -1.75, 0.01, 3).Point);
        Near(-0.12256116687665362, 0.74486176661974424, Nucleus(m, -0.12, 0.74, 3).Point);
        Assert.Equal(0, Nucleus(m, -0.95, 0.03, 2).CycleRadius, 9);   // superattracting
    }

    [Theory]
    [InlineData(3, 0, 1)]          // c³ + c = 0
    [InlineData(3, 0, -1)]
    [InlineData(4, -1, 0)]         // c⁴ + c = 0
    [InlineData(4, 0.5, 0.8660254037844386)]
    [InlineData(4, 0.5, -0.8660254037844386)]
    public void Multibrot_Period2(int d, double x, double y)
        => Near(x, y, Nucleus(new MultibrotMap(d), x + 0.03, y - 0.02, 2).Point);

    [Theory]
    [InlineData(-1, 0)]            // conj(c)² + c = 0  ⇒  c³-like roots e^{iπ}, e^{±iπ/3}
    [InlineData(0.5, 0.8660254037844386)]
    [InlineData(0.5, -0.8660254037844386)]
    public void Tricorn_Period2(double x, double y)
        => Near(x, y, Nucleus(new TricornMap(), x + 0.03, y - 0.02, 2).Point);

    [Fact]
    public void BurningShip_Period2_AndItsAsymmetry()
    {
        var bs = new BurningShipMap();
        // z₂ = (x² − y² + x, 2|x||y| + y) = 0  ⇒  (−1, 0) or (½, −√3/2).
        Near(-1, 0, Nucleus(bs, -0.97, 0.02, 2).Point);
        Near(0.5, -S3, Nucleus(bs, 0.52, -0.85, 2).Point);
        // The mirror point (½, +√3/2) is not a nucleus (the ship is not symmetric).
        Assert.Null(GeneralFinder.FindNucleus(bs, new V2(0.5, S3), 2, 0.02));
    }

    [Fact]
    public void LowerPeriod_IsRejected()
    {
        // A period-4 search at the period-2 nucleus must not report period 4.
        var r = GeneralFinder.FindNucleus(new MultibrotMap(2), new V2(-1.0000001, 0), 4, 1e-4);
        Assert.Null(r);
    }

    // ── User Equation ────────────────────────────────────────────────────

    private static UserEquationMap User(string src, string? seed = null)
        => new(SandboxExpression.Parse(src), seed == null ? null : SandboxExpression.Parse(seed), 0, src);

    [Fact]
    public void UserEquation_MatchesTheAnalyticMaps()
    {
        Near(-0.12256116687665362, 0.74486176661974424, Nucleus(User("z^2 + c"), -0.12, 0.74, 3).Point, 1e-9);
        Near(0, 1, Nucleus(User("z^3 + c"), 0.03, 0.98, 2).Point, 1e-9);
        Near(0.5, -S3, Nucleus(User("(abs(re(z)) + i*abs(im(z)))^2 + c"), 0.52, -0.85, 2).Point, 1e-9);
        // z₀ = c only shifts the orbit by one step: the same nuclei.
        Near(-1.7548776662466927, 0, Nucleus(User("z^2 + c", "c"), -1.75, 0.01, 3).Point, 1e-9);
    }

    [Fact]
    public void UserEquation_NonAutonomous_IsDetected()
    {
        Assert.True(UserEquationMap.IsAutonomous(SandboxExpression.Parse("z^2 + c")));
        Assert.False(UserEquationMap.IsAutonomous(SandboxExpression.Parse("z^2 + c + 0.001*n")));
        Assert.False(UserEquationMap.IsAutonomous(SandboxExpression.Parse("z^2 + c + 0.5*prev")));
    }

    // ── Minibrot search + independent render check ───────────────────────

    private static PosterRequest Template(FractalType t, FractalParameters fp) => new()
    {
        FractalType = t,
        ColorMap = ColorPalette.BuiltIns[0],
        Quality = QualityPreset.Standard,
        FractalParameters = fp,
        Path = "unused.png",
        Format = ImageFileFormat.Png,
    };

    [Theory]
    [InlineData(FractalType.Multibrot, -0.42, 0.62, 3e3)]
    [InlineData(FractalType.GeneratedMandelbrotZ3, -0.42, 0.62, 3e3)]
    [InlineData(FractalType.Tricorn, -1.7451, 0.0086, 1000)]
    [InlineData(FractalType.GeneratedTricorn, -1.7451, 0.0086, 1000)]
    [InlineData(FractalType.BurningShip, -1.755, -0.03, 300)]
    [InlineData(FractalType.GeneratedBurningShip, -1.755, -0.03, 300)]
    [InlineData(FractalType.UserEquation, -0.75, 0.12, 400)]
    public void FoundMinibrots_RenderInSet_InTheRealCalculator(FractalType t, double x, double y, double zoom)
    {
        var fp = new FractalParameters { MultibrotExponent = 3 };
        IOrbitMap map;
        if (t == FractalType.UserEquation)
        {
            fp.UserEquationSource = "z^2 + c";
            map = User("z^2 + c");
        }
        else map = OrbitMaps.For(t, fp)!;

        double radius = PeriodDetector.ViewDiskRadius(zoom, 1920, 1080);
        var g = GeneralFinder.FindMinibrot(map, new V2(x, y), radius);
        _out.WriteLine($"{t}: {g.Status} p={g.Period} at ({g.Point.X:R}, {g.Point.Y:R}) body {g.BodyRadius:G3} zoom {g.Zoom:G3} rho {g.CycleRadius:G3} attempts {g.Attempts}");
        Assert.True(g.Found);
        Assert.True((g.Point - new V2(x, y)).Length <= radius);
        Assert.True(g.Zoom > 0);

        // Independent check: the family's own calculator renders the nucleus
        // (and the 3×3 pixels around it, at the frame zoom) as in-set.
        var view = new ExploreView(new DeepComplex(g.Point.X, g.Point.Y), g.Zoom);
        var f = ExploreProbe.Render(Template(t, fp) with { MaxIterations = 0 }, view, 65, default);
        Assert.NotNull(f);
        Assert.NotNull(f!.Inside);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                Assert.True(f.Inside![(32 + dy) * 65 + 32 + dx], $"{t}: pixel ({dx},{dy}) by the nucleus escapes");
        // And the frame shows the minibrot's surroundings: some pixels escape.
        Assert.True(f.Inside!.AsSpan().Contains(false), $"{t}: frame zoom shows no exterior");
    }

    [Fact]
    public void BodyRadius_OfTheMainCardioid()
    {
        double r = GeneralFinder.BodyRadius(new MultibrotMap(2), new V2(0, 0), 1, 1);
        _out.WriteLine($"cardioid body radius {r}");
        Assert.InRange(r, 0.65, 0.8);   // median edge distance; the cardioid spans 0.25 (cusp) .. 0.75
    }

    // ── Misiurewicz ──────────────────────────────────────────────────────

    [Fact]
    public void Misiurewicz_I_InTheGenericZ2()
    {
        // c = i: 0 → i → −1+i → −i → −1+i: M(2, 2), multiplier |4·z₂·z₃| = 4√2.
        var r = GeneralFinder.FindNearMisiurewicz(new MultibrotMap(2), new V2(0.01, 0.99), 0.05);
        Assert.True(r.Found);
        Near(0, 1, r.Point);
        Assert.Equal((2, 2), (r.Preperiod, r.Period));
        Assert.Equal(4 * Math.Sqrt(2), r.Multiplier, 9);
        Assert.NotNull(r.TurnDegrees);
    }

    [Fact]
    public void Misiurewicz_Multibrot3()
    {
        // M(2, 1) of z³ + c: z₂ = ω z₁ with ω = e^{2πi/3}  ⇒  c² = ω − 1.
        var w = System.Numerics.Complex.FromPolarCoordinates(1, 2 * Math.PI / 3);
        var c = System.Numerics.Complex.Sqrt(w - 1);
        var r = GeneralFinder.FindNearMisiurewicz(new MultibrotMap(3), new V2(c.Real + 0.004, c.Imaginary - 0.003), 0.02);
        Assert.True(r.Found);
        Near(c.Real, c.Imaginary, r.Point);
        Assert.Equal((2, 1), (r.Preperiod, r.Period));
    }

    [Theory]
    [InlineData("BurningShip")]
    [InlineData("Tricorn")]
    public void Misiurewicz_TipAtMinusTwo(string family)
    {
        // c = −2: −2 → 2 → 2: M(2, 1), multiplier 4 for both folds.
        IOrbitMap map = family == "Tricorn" ? new TricornMap() : new BurningShipMap();
        var r = GeneralFinder.FindNearMisiurewicz(map, new V2(-1.99, 0.004), 0.05);
        Assert.True(r.Found, family);
        Near(-2, 0, r.Point);
        Assert.Equal((2, 1), (r.Preperiod, r.Period));
        Assert.Equal(4, r.Multiplier, 9);
    }
}
