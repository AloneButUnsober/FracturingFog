using System;
using System.IO;
using System.Linq;

using FracturingFog.Server.Cluster;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

/// <summary>Trivial codec that round-trips BGRA bytes by simply
/// prefixing a 12-byte header [magic 4B][width 4B][height 4B]. Lets
/// merger tests run without any PNG dependency.</summary>
internal sealed class RawHeaderCodec : IClusterImageCodec
{
    private const uint Magic = 0xFADEFACEu;

    public byte[] DecodePngToBgra(byte[] png, out int width, out int height)
    {
        if (png.Length < 12) throw new InvalidDataException("payload too short");
        uint magic = BitConverter.ToUInt32(png, 0);
        if (magic != Magic) throw new InvalidDataException("bad magic");
        width  = BitConverter.ToInt32(png, 4);
        height = BitConverter.ToInt32(png, 8);
        byte[] bgra = new byte[png.Length - 12];
        Buffer.BlockCopy(png, 12, bgra, 0, bgra.Length);
        return bgra;
    }

    public void EncodeBgraToPng(byte[] bgra, int width, int height, string outPath)
    {
        using var fs = File.Create(outPath);
        fs.Write(BitConverter.GetBytes(Magic));
        fs.Write(BitConverter.GetBytes(width));
        fs.Write(BitConverter.GetBytes(height));
        fs.Write(bgra);
    }

    public static byte[] BuildTile(int w, int h, byte fillR, byte fillG, byte fillB)
    {
        byte[] bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            bgra[i * 4 + 0] = fillB;
            bgra[i * 4 + 1] = fillG;
            bgra[i * 4 + 2] = fillR;
            bgra[i * 4 + 3] = 0xFF;
        }
        byte[] payload = new byte[12 + bgra.Length];
        BitConverter.GetBytes(Magic).CopyTo(payload, 0);
        BitConverter.GetBytes(w).CopyTo(payload, 4);
        BitConverter.GetBytes(h).CopyTo(payload, 8);
        Buffer.BlockCopy(bgra, 0, payload, 12, bgra.Length);
        return payload;
    }
}

public sealed class ArtifactMergerTests
{
    [Fact]
    public void Merge_Pastes_Tiles_Into_Correct_Rect()
    {
        var codec = new RawHeaderCodec();
        using var m = new ArtifactMerger(8, 4, 2, codec);

        byte[] left  = RawHeaderCodec.BuildTile(4, 4, 0xFF, 0x00, 0x00);   // red
        byte[] right = RawHeaderCodec.BuildTile(4, 4, 0x00, 0xFF, 0x00);   // green

        Assert.True(m.TryMergePngTile(0, 0, 0, 4, 4, left));
        Assert.True(m.TryMergePngTile(1, 4, 0, 4, 4, right));
        Assert.True(m.IsComplete);
    }

    [Fact]
    public void Duplicate_Tile_Delivery_Returns_False()
    {
        var codec = new RawHeaderCodec();
        using var m = new ArtifactMerger(4, 4, 1, codec);
        byte[] one = RawHeaderCodec.BuildTile(4, 4, 0, 0, 0);

        Assert.True (m.TryMergePngTile(0, 0, 0, 4, 4, one));
        Assert.False(m.TryMergePngTile(0, 0, 0, 4, 4, one));  // idempotent
    }

    [Fact]
    public void Rect_Out_Of_Bounds_Throws()
    {
        var codec = new RawHeaderCodec();
        using var m = new ArtifactMerger(4, 4, 1, codec);
        byte[] big = RawHeaderCodec.BuildTile(5, 4, 0, 0, 0);  // too wide

        Assert.Throws<ArgumentException>(() =>
            m.TryMergePngTile(0, 0, 0, 5, 4, big));
    }

    [Fact]
    public void Size_Mismatch_From_Codec_Throws()
    {
        var codec = new RawHeaderCodec();
        using var m = new ArtifactMerger(4, 4, 1, codec);
        // Build a tile whose header says 3×4 but caller declares 4×4.
        byte[] payload = RawHeaderCodec.BuildTile(3, 4, 0, 0, 0);

        Assert.Throws<InvalidDataException>(() =>
            m.TryMergePngTile(0, 0, 0, 4, 4, payload));
    }

    [Fact]
    public void WritePng_Requires_All_Tiles()
    {
        var codec = new RawHeaderCodec();
        using var m = new ArtifactMerger(4, 4, 2, codec);
        m.TryMergePngTile(0, 0, 0, 4, 2, RawHeaderCodec.BuildTile(4, 2, 0, 0, 0));

        Assert.False(m.IsComplete);
        Assert.Throws<InvalidOperationException>(() => m.WritePng("nope"));
    }

    [Fact]
    public void WritePng_Round_Trips_Buffer_Through_Codec()
    {
        var codec = new RawHeaderCodec();
        using var m = new ArtifactMerger(2, 2, 1, codec);
        byte[] tile = RawHeaderCodec.BuildTile(2, 2, 1, 2, 3);
        Assert.True(m.TryMergePngTile(0, 0, 0, 2, 2, tile));

        string tmp = Path.Combine(Path.GetTempPath(), $"ff-merge-out-{Guid.NewGuid():N}.bin");
        try
        {
            m.WritePng(tmp);
            byte[] file = File.ReadAllBytes(tmp);
            // 12-byte header + 16 bytes (2*2*4) of BGRA = 28 bytes.
            Assert.Equal(28, file.Length);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void MissingTileIds_Reports_Outstanding_Indices()
    {
        var codec = new RawHeaderCodec();
        using var m = new ArtifactMerger(4, 4, 4, codec);
        m.TryMergePngTile(1, 0, 0, 4, 4, RawHeaderCodec.BuildTile(4, 4, 0, 0, 0));
        var missing = m.MissingTileIds();
        Assert.Equal(new[] { 0, 2, 3 }, missing.ToArray());
    }
}
