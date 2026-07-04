// Server/Cluster/FramesPayloadCodec.cs
// Pack / unpack the binary trailer for tile.deliver PayloadKind="frames"
// (D-4). One tile carries N video frames as PNGs; rather than open a
// fresh JSON-RPC call per frame we cat them into a single binary blob
// with a tiny self-describing header.
//
// Wire shape (all integers little-endian, signed int32):
//
//   [4 bytes "FRMS"]                           magic so a misrouted
//                                              base64 payload fails fast
//   [int32  version = 1]
//   [int32  frameCount]
//   repeat frameCount times:
//      [int32 frameIndex]                      global frame id in the job
//      [int32 pngLen]                          PNG byte length
//      [pngLen bytes]                          raw PNG data, no padding
//
// No per-frame SHA — the outer TileDeliverDto.Sha256 covers the whole
// trailer. Corruption shows up at the master before any frame hits disk.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace FracturingFog.Server.Cluster;

public static class FramesPayloadCodec
{
    private static readonly byte[] Magic = new byte[] { (byte)'F', (byte)'R', (byte)'M', (byte)'S' };
    public const int Version = 1;

    public readonly record struct Frame(int FrameIndex, byte[] Png);

    public static byte[] Pack(IReadOnlyList<Frame> frames)
    {
        long total = 4 + 4 + 4; // magic + version + count
        for (int i = 0; i < frames.Count; i++) total += 4 + 4 + frames[i].Png.LongLength;
        if (total > int.MaxValue)
            throw new InvalidDataException(
                $"frames payload too big for in-memory pack: {total} bytes");

        var buf = new byte[(int)total];
        int off = 0;
        Buffer.BlockCopy(Magic, 0, buf, off, 4);                      off += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off, 4), Version);
        off += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off, 4), frames.Count);
        off += 4;
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off, 4), f.FrameIndex);
            off += 4;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off, 4), f.Png.Length);
            off += 4;
            Buffer.BlockCopy(f.Png, 0, buf, off, f.Png.Length);
            off += f.Png.Length;
        }
        return buf;
    }

    public static List<Frame> Unpack(byte[] payload)
    {
        if (payload is null || payload.Length < 12)
            throw new InvalidDataException("frames payload truncated (< header)");
        if (payload[0] != Magic[0] || payload[1] != Magic[1]
            || payload[2] != Magic[2] || payload[3] != Magic[3])
            throw new InvalidDataException("frames payload magic mismatch");

        int version = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        if (version != Version)
            throw new InvalidDataException($"frames payload version {version} unsupported");

        int count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4));
        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"frames payload count {count} out of range");

        var list = new List<Frame>(count);
        int off = 12;
        for (int i = 0; i < count; i++)
        {
            if (off + 8 > payload.Length)
                throw new InvalidDataException(
                    $"frames payload truncated at frame {i}: header would run past end");
            int frameIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(off, 4));
            off += 4;
            int len = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(off, 4));
            off += 4;
            if (len < 0 || off + len > payload.Length)
                throw new InvalidDataException(
                    $"frames payload truncated at frame {i}: pngLen={len} exceeds remaining");
            var png = new byte[len];
            Buffer.BlockCopy(payload, off, png, 0, len);
            off += len;
            list.Add(new Frame(frameIndex, png));
        }
        if (off != payload.Length)
            throw new InvalidDataException(
                $"frames payload has {payload.Length - off} trailing bytes");
        return list;
    }
}
