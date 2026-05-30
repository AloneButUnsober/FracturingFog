// Server/Wire/JsonRpcFraming.cs
// Length-prefixed UTF-8 JSON over an arbitrary Stream (typically
// SslStream). Frame = [4-byte little-endian length][UTF-8 JSON body].
// Body cap defaults to 256 MB — enough for an inline 16K poster PNG.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.Server.Wire;

public static class JsonRpcFraming
{
    public const int DefaultMaxFrameBytes = 256 * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(
        Stream stream, MessageEnvelope envelope,
        int maxFrameBytes = DefaultMaxFrameBytes,
        CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(envelope, JsonOpts);
        byte[] body = Encoding.UTF8.GetBytes(json);
        if (body.Length > maxFrameBytes)
            throw new InvalidOperationException(
                $"frame body {body.Length:N0} exceeds cap {maxFrameBytes:N0}");

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<MessageEnvelope?> ReadAsync(
        Stream stream,
        int maxFrameBytes = DefaultMaxFrameBytes,
        CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        if (!await ReadFullyAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len < 0 || len > maxFrameBytes)
            throw new InvalidDataException(
                $"frame length {len:N0} outside [0, {maxFrameBytes:N0}]");
        if (len == 0)
            return new MessageEnvelope();

        byte[] body = new byte[len];
        if (!await ReadFullyAsync(stream, body, ct).ConfigureAwait(false))
            throw new EndOfStreamException("stream closed mid-frame");

        return JsonSerializer.Deserialize<MessageEnvelope>(body, JsonOpts);
    }

    private static async Task<bool> ReadFullyAsync(
        Stream stream, Memory<byte> dst, CancellationToken ct)
    {
        int read = 0;
        while (read < dst.Length)
        {
            int n = await stream.ReadAsync(dst.Slice(read), ct).ConfigureAwait(false);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException();
            read += n;
        }
        return true;
    }
}
