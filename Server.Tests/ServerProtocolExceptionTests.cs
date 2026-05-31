using System.Text.Json;
using FracturingFog.Server;
using FracturingFog.Server.Protocol;
using FracturingFog.Server.Wire;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ServerProtocolExceptionTests
{
    [Theory]
    [InlineData("forbidden-fractal", "user-code fractal types refused")]
    [InlineData("unknown-region", "region 'Foo' not found")]
    [InlineData("unknown-theme", "theme 'Bar' not found")]
    [InlineData("ffmpeg-missing", "ffmpeg.exe is required for lossless encode")]
    [InlineData("limit-exceeded", "width × height exceeds host pixel cap")]
    public void Carries_Code_And_Message(string code, string message)
    {
        var ex = new ServerProtocolException(code, message);
        Assert.Equal(code, ex.Code);
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Maps_To_ErrorDto_OverWire()
    {
        // The dispatcher reflects ServerProtocolException onto an ErrorDto.
        // Exercise the same JSON shape clients see.
        var ex = new ServerProtocolException("unknown-region", "region 'Foo' not found");
        var dto = new ErrorDto { Code = ex.Code, Message = ex.Message };

        string json = JsonSerializer.Serialize(dto, JsonRpcFraming.JsonOpts);
        ErrorDto? round = JsonSerializer.Deserialize<ErrorDto>(json, JsonRpcFraming.JsonOpts);

        Assert.NotNull(round);
        Assert.Equal("unknown-region", round!.Code);
        Assert.Equal("region 'Foo' not found", round.Message);
    }

    [Fact]
    public void ErrorDto_DefaultCode_IsInternal()
    {
        // Defense-in-depth: an ErrorDto constructed without an explicit code
        // must NOT default to a permissive value. The wire contract treats
        // empty/missing codes as server-internal failures.
        var dto = new ErrorDto();
        Assert.Equal("internal", dto.Code);
    }
}
