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
    [InlineData(FractalType.Apollonian)]
    [InlineData(FractalType.Kleinian)]
    [InlineData(FractalType.BicomplexMandelbrot)]
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

    [Fact]
    public void NameOverload_RejectsUnknownName()
    {
        Assert.False(FractalTypeAllowlist.IsAllowed("notafractal", out _));
    }
}
