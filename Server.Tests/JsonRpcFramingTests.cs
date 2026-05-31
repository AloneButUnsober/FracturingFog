using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FracturingFog.Server.Wire;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class JsonRpcFramingTests
{
    [Fact]
    public async Task RoundTrip_PreservesEnvelopeFields()
    {
        var sent = new MessageEnvelope
        {
            Kind = "request",
            Id = "abc-123",
            Method = "render.image",
            Params = JsonSerializer.SerializeToElement(new { width = 1920, height = 1080 }, JsonRpcFraming.JsonOpts),
        };

        using var ms = new MemoryStream();
        await JsonRpcFraming.WriteAsync(ms, sent);
        ms.Position = 0;

        var got = await JsonRpcFraming.ReadAsync(ms);
        Assert.NotNull(got);
        Assert.Equal("request", got!.Kind);
        Assert.Equal("abc-123", got.Id);
        Assert.Equal("render.image", got.Method);
        Assert.NotNull(got.Params);
        Assert.Equal(1920, got.Params!.Value.GetProperty("width").GetInt32());
        Assert.Equal(1080, got.Params!.Value.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task RoundTrip_MultipleFramesBackToBack()
    {
        using var ms = new MemoryStream();
        for (int i = 0; i < 5; i++)
        {
            await JsonRpcFraming.WriteAsync(ms, new MessageEnvelope
            {
                Kind = "request",
                Id = i.ToString(),
                Method = "ping",
            });
        }
        ms.Position = 0;

        for (int i = 0; i < 5; i++)
        {
            var got = await JsonRpcFraming.ReadAsync(ms);
            Assert.NotNull(got);
            Assert.Equal(i.ToString(), got!.Id);
        }
        Assert.Null(await JsonRpcFraming.ReadAsync(ms)); // EOF
    }

    [Fact]
    public async Task Read_RejectsFrameLargerThanCap()
    {
        // Hand-craft a 4-byte little-endian header that claims a 1 GB body.
        byte[] header = { 0x00, 0x00, 0x00, 0x40 }; // 0x4000_0000 = 1 GiB
        using var ms = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await JsonRpcFraming.ReadAsync(ms, maxFrameBytes: 1 * 1024 * 1024));
    }

    [Fact]
    public async Task Write_RejectsBodyOverCap()
    {
        var env = new MessageEnvelope
        {
            Kind = "request",
            Id = "x",
            Method = "echo",
            Params = JsonSerializer.SerializeToElement(new string('A', 10_000)),
        };
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<System.InvalidOperationException>(async () =>
            await JsonRpcFraming.WriteAsync(ms, env, maxFrameBytes: 256));
    }
}
