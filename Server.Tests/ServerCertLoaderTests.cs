using FracturingFog.Server.Tls;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ServerCertLoaderTests
{
    [Theory]
    [InlineData("AB CD EF 12", "ABCDEF12")]
    [InlineData("ab-cd-ef-12", "ABCDEF12")]
    [InlineData("AB:CD:EF:12", "ABCDEF12")]
    [InlineData("abcdef12", "ABCDEF12")]
    [InlineData("  AB CD-EF:12 ", "ABCDEF12")]
    public void NormalizeThumbprint_StripsSeparatorsAndUppercases(string input, string expected)
    {
        Assert.Equal(expected, ServerCertLoader.NormalizeThumbprint(input));
    }

    [Fact]
    public void NormalizeThumbprint_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", ServerCertLoader.NormalizeThumbprint(null));
        Assert.Equal("", ServerCertLoader.NormalizeThumbprint(""));
    }
}
