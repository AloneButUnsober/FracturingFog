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
        var ct = TestContext.Current.CancellationToken;
        var sent = new MessageEnvelope
        {
            Kind = "request",
            Id = "abc-123",
            Method = "render.image",
            Params = JsonSerializer.SerializeToElement(new { width = 1920, height = 1080 }, JsonRpcFraming.JsonOpts),
        };

        using var ms = new MemoryStream();
        await JsonRpcFraming.WriteAsync(ms, sent, ct: ct);
        ms.Position = 0;

        var got = await JsonRpcFraming.ReadAsync(ms, ct: ct);
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
        var ct = TestContext.Current.CancellationToken;
        using var ms = new MemoryStream();
        for (int i = 0; i < 5; i++)
        {
            await JsonRpcFraming.WriteAsync(ms, new MessageEnvelope
            {
                Kind = "request",
                Id = i.ToString(),
                Method = "ping",
            }, ct: ct);
        }
        ms.Position = 0;

        for (int i = 0; i < 5; i++)
        {
            var got = await JsonRpcFraming.ReadAsync(ms, ct: ct);
            Assert.NotNull(got);
            Assert.Equal(i.ToString(), got!.Id);
        }
        Assert.Null(await JsonRpcFraming.ReadAsync(ms, ct: ct)); // EOF
    }

    [Fact]
    public async Task Read_RejectsFrameLargerThanCap()
    {
        var ct = TestContext.Current.CancellationToken;
        // Hand-craft a 4-byte little-endian header that claims a 1 GB body.
        byte[] header = { 0x00, 0x00, 0x00, 0x40 }; // 0x4000_0000 = 1 GiB
        using var ms = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await JsonRpcFraming.ReadAsync(ms, maxFrameBytes: 1 * 1024 * 1024, ct: ct));
    }

    [Fact]
    public async Task Write_RejectsBodyOverCap()
    {
        var ct = TestContext.Current.CancellationToken;
        var env = new MessageEnvelope
        {
            Kind = "request",
            Id = "x",
            Method = "echo",
            Params = JsonSerializer.SerializeToElement(new string('A', 10_000)),
        };
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<System.InvalidOperationException>(async () =>
            await JsonRpcFraming.WriteAsync(ms, env, maxFrameBytes: 256, ct: ct));
    }

    // ── D-3 binary trailer ─────────────────────────────────────────────

    [Fact]
    public async Task BinaryTrailer_RoundTrip_PreservesBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] payload = new byte[64 * 1024];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

        var sent = new MessageEnvelope
        {
            Kind   = "request",
            Id     = "bin-1",
            Method = "tile.deliver",
            Params = JsonSerializer.SerializeToElement(new { tileId = 7 }, JsonRpcFraming.JsonOpts),
            Binary = payload,
        };

        using var ms = new MemoryStream();
        await JsonRpcFraming.WriteAsync(ms, sent, ct: ct);
        ms.Position = 0;

        var got = await JsonRpcFraming.ReadAsync(ms, ct: ct);
        Assert.NotNull(got);
        Assert.Equal("bin-1", got!.Id);
        Assert.Equal(payload.Length, got.BinaryLength);
        Assert.NotNull(got.Binary);
        Assert.Equal(payload, got.Binary);
    }

    [Fact]
    public async Task BinaryTrailer_BackToBackFrames_DoNotSnagFollowingReader()
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] trailerA = new byte[1024];
        for (int i = 0; i < trailerA.Length; i++) trailerA[i] = (byte)'A';
        byte[] trailerB = new byte[2048];
        for (int i = 0; i < trailerB.Length; i++) trailerB[i] = (byte)'B';

        using var ms = new MemoryStream();
        await JsonRpcFraming.WriteAsync(ms, new MessageEnvelope
        {
            Kind = "request", Id = "1", Method = "a", Binary = trailerA,
        }, ct: ct);
        await JsonRpcFraming.WriteAsync(ms, new MessageEnvelope
        {
            Kind = "request", Id = "2", Method = "b",
        }, ct: ct);  // JSON-only between two binary-bearing frames
        await JsonRpcFraming.WriteAsync(ms, new MessageEnvelope
        {
            Kind = "request", Id = "3", Method = "c", Binary = trailerB,
        }, ct: ct);
        ms.Position = 0;

        var f1 = await JsonRpcFraming.ReadAsync(ms, ct: ct);
        Assert.Equal("1", f1!.Id);
        Assert.Equal(trailerA, f1.Binary);

        var f2 = await JsonRpcFraming.ReadAsync(ms, ct: ct);
        Assert.Equal("2", f2!.Id);
        Assert.Null(f2.Binary);
        Assert.Equal(0, f2.BinaryLength);

        var f3 = await JsonRpcFraming.ReadAsync(ms, ct: ct);
        Assert.Equal("3", f3!.Id);
        Assert.Equal(trailerB, f3.Binary);

        Assert.Null(await JsonRpcFraming.ReadAsync(ms, ct: ct));
    }

    [Fact]
    public async Task BinaryTrailer_RejectedWhenTrailerExceedsCap()
    {
        var ct = TestContext.Current.CancellationToken;
        var env = new MessageEnvelope
        {
            Kind   = "request",
            Id     = "big",
            Method = "x",
            Binary = new byte[2048],
        };
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<System.InvalidOperationException>(async () =>
            await JsonRpcFraming.WriteAsync(ms, env, maxFrameBytes: 1024, ct: ct));
    }
}
