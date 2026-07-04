// Server.Tests/Cluster/FramesPayloadCodecTests.cs
// D-4a — round-trip tests for the FRMS frames trailer codec used by
// tile.deliver PayloadKind="frames".

using System;
using System.Collections.Generic;
using System.IO;

using FracturingFog.Server.Cluster;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class FramesPayloadCodecTests
{
    private static byte[] FakePng(int seed, int len)
    {
        var rng = new Random(seed);
        var b = new byte[len];
        rng.NextBytes(b);
        return b;
    }

    [Fact]
    public void Roundtrip_Preserves_All_Frames()
    {
        var input = new List<FramesPayloadCodec.Frame>
        {
            new(10, FakePng(1, 4096)),
            new(11, FakePng(2, 2048)),
            new(12, FakePng(3, 8192)),
        };
        byte[] packed = FramesPayloadCodec.Pack(input);
        var output = FramesPayloadCodec.Unpack(packed);
        Assert.Equal(input.Count, output.Count);
        for (int i = 0; i < input.Count; i++)
        {
            Assert.Equal(input[i].FrameIndex, output[i].FrameIndex);
            Assert.Equal(input[i].Png, output[i].Png);
        }
    }

    [Fact]
    public void Roundtrip_With_Zero_Frames()
    {
        byte[] packed = FramesPayloadCodec.Pack(Array.Empty<FramesPayloadCodec.Frame>());
        var output = FramesPayloadCodec.Unpack(packed);
        Assert.Empty(output);
    }

    [Fact]
    public void Unpack_Rejects_Bad_Magic()
    {
        byte[] bogus = new byte[12];
        bogus[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => FramesPayloadCodec.Unpack(bogus));
    }

    [Fact]
    public void Unpack_Rejects_Truncated_Payload()
    {
        var input = new List<FramesPayloadCodec.Frame> { new(0, FakePng(1, 1024)) };
        byte[] packed = FramesPayloadCodec.Pack(input);
        // Chop off last 16 bytes (mid-PNG).
        byte[] truncated = packed.AsSpan(0, packed.Length - 16).ToArray();
        Assert.Throws<InvalidDataException>(() => FramesPayloadCodec.Unpack(truncated));
    }

    [Fact]
    public void Unpack_Rejects_Trailing_Garbage()
    {
        var input = new List<FramesPayloadCodec.Frame> { new(0, FakePng(1, 64)) };
        byte[] packed = FramesPayloadCodec.Pack(input);
        // Append a stray byte.
        byte[] tampered = new byte[packed.Length + 1];
        Buffer.BlockCopy(packed, 0, tampered, 0, packed.Length);
        Assert.Throws<InvalidDataException>(() => FramesPayloadCodec.Unpack(tampered));
    }
}
