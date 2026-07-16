// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Linq;
using FracturingFog;
using FracturingFog.Server.Guard;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FractalTypeAllowlistTests
{
    [Theory]
    [InlineData(FractalType.UserEquation)]
    [InlineData(FractalType.Sandbox)]
    [InlineData(FractalType.UserBulb)]
    public void Blocked_UserCodeTypes_AreRefused(FractalType t)
    {
        Assert.False(FractalTypeAllowlist.IsAllowed(t));
        Assert.Contains(t, FractalTypeAllowlist.BlockedTypes);
    }

    [Theory]
    [InlineData(FractalType.Mandelbrot)]
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.BurningShip)]
    [InlineData(FractalType.Tricorn)]
    [InlineData(FractalType.Multibrot)]
    [InlineData(FractalType.Phoenix)]
    [InlineData(FractalType.Newton)]
    [InlineData(FractalType.Nova)]
    [InlineData(FractalType.BuddhaBrot)]
    [InlineData(FractalType.Nebulabrot)]
    [InlineData(FractalType.AntiBuddhabrot)]
    [InlineData(FractalType.AntiNebulabrot)]
    [InlineData(FractalType.IFS)]
    [InlineData(FractalType.LSystem)]
    [InlineData(FractalType.StrangeAttractor)]
    [InlineData(FractalType.Mandelbulb)]
    [InlineData(FractalType.TearDrop)]
    [InlineData(FractalType.Magnet1)]
    [InlineData(FractalType.Magnet2)]
    [InlineData(FractalType.Glynn)]
    [InlineData(FractalType.Logistic)]
    [InlineData(FractalType.Halley)]
    [InlineData(FractalType.Secant)]
    [InlineData(FractalType.Spider)]
    [InlineData(FractalType.Mandelbox)]
    [InlineData(FractalType.Kifs)]
    [InlineData(FractalType.QuaternionJulia)]
    [InlineData(FractalType.QuaternionMandelbrot)]
    [InlineData(FractalType.Plasma)]
    [InlineData(FractalType.Flame)]
    [InlineData(FractalType.Apollonian)]
    [InlineData(FractalType.GeneratedMandelbrotZ2)]
    [InlineData(FractalType.GeneratedMandelbrotZ3)]
    [InlineData(FractalType.GeneratedMandelbrotZ4)]
    [InlineData(FractalType.GeneratedMandelbrotZ5)]
    [InlineData(FractalType.GeneratedTricorn)]
    [InlineData(FractalType.GeneratedBurningShip)]
    [InlineData(FractalType.Kleinian)]
    [InlineData(FractalType.BicomplexMandelbrot)]
    [InlineData(FractalType.Dla)]
    public void AllowedTypes_AreAllowed(FractalType t)
    {
        Assert.True(FractalTypeAllowlist.IsAllowed(t));
    }

    [Theory]
    [InlineData("UserEquation", false)]
    [InlineData("sandbox", false)]      // case-insensitive
    [InlineData("USERBULB", false)]
    [InlineData("Mandelbrot", true)]
    [InlineData("BurningShip", true)]
    public void NameOverload_HandlesCaseAndBlocking(string name, bool allowed)
    {
        bool ok = FractalTypeAllowlist.IsAllowed(name, out _);
        Assert.Equal(allowed, ok);
    }

    [Theory]
    [InlineData("notafractal")]
    [InlineData("")]
    [InlineData("Mandelbrot42")]
    [InlineData("Mandel brot")]
    [InlineData("../etc/passwd")]
    public void NameOverload_RejectsUnknownName(string name)
    {
        Assert.False(FractalTypeAllowlist.IsAllowed(name, out _));
    }

    // Regression guard: only the three user-code types should ever sit in
    // the blocked set. If a future change accidentally blocks a built-in
    // (e.g. by adding a built-in name to the HashSet), this test fires.
    [Fact]
    public void BlockedSet_ContainsOnlyUserCodeTypes()
    {
        var expected = new[]
        {
            FractalType.UserEquation,
            FractalType.Sandbox,
            FractalType.UserBulb,
        };
        Assert.Equal(expected.Length, FractalTypeAllowlist.BlockedTypes.Count);
        foreach (var t in expected)
            Assert.Contains(t, FractalTypeAllowlist.BlockedTypes);
    }

    // Coverage assertion: every FractalType enum value is either explicitly
    // allowed or explicitly blocked — nothing falls through into an
    // undefined classification.
    [Fact]
    public void EveryEnumValue_IsExplicitlyClassified()
    {
        foreach (FractalType t in System.Enum.GetValues(typeof(FractalType)))
        {
            bool allowed = FractalTypeAllowlist.IsAllowed(t);
            bool blocked = FractalTypeAllowlist.BlockedTypes.Contains(t);
            Assert.True(allowed ^ blocked, $"{t}: allowed={allowed} blocked={blocked}");
        }
    }
}
