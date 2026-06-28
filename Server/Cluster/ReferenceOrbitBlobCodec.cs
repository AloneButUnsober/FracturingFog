// Server/Cluster/ReferenceOrbitBlobCodec.cs
// D-6b — encode / decode the master-computed Mandelbrot reference orbit
// shipped with every tile of a deep-zoom job. Workers seed their per-tile
// MandelbrotCalculator with the decoded arrays so the per-tile recompute
// short-circuits in the calculator's centre-cache check.
//
// On-wire format (little-endian, fixed header + two double[] arrays):
//
//   offset  size  field
//   ------  ----  ------------------------------------------------------
//    0       1    magic byte 0xD6 (sniff guard against accidental misuse)
//    1       1    format version  (currently 1)
//    2       1    limbs per slot  (2 = DD; 4 = QD; 8 = OD — only DD in v1)
//    3       1    escaped flag    (1 if the orbit terminated by escape, else 0)
//    4       4    refLen          (int32; number of orbit steps stored)
//    8       4    maxIter         (int32; iteration cap used at compute time)
//   12       8    centreX  (double — Hi limb)
//   20       8    centreXLo
//   28       8    centreY
//   36       8    centreYLo
//   44      8·(refLen+1)  Zr Hi limb array
//   ..      8·(refLen+1)  Zi Hi limb array
//   ..      8·(refLen+1)  Zr Lo limb array
//   ..      8·(refLen+1)  Zi Lo limb array
//
// The orbit array length is refLen+1 because ComputeReferenceOrbit writes
// the post-escape Z at index refLen (mirrors the engine's storage shape;
// see MandelbrotCalculator.ComputeReferenceOrbit).
//
// v1 ships DD precision only (limbs=2). QD/OD shipping lands when the
// engine seam gains QD/OD orbit seed support — until then the master
// refuses to attach a blob for zoom > QDZoomThreshold and the tile falls
// back to per-tile compute.

using System;
using System.Buffers.Binary;

namespace FracturingFog.Server.Cluster;

public static class ReferenceOrbitBlobCodec
{
    public const byte MagicByte     = 0xD6;
    public const byte FormatVersion = 1;
    public const byte LimbsDD       = 2;
    public const int  HeaderBytes   = 44;

    /// <summary>Decoded view of a shipped orbit. Arrays are sized
    /// <c>RefLen + 1</c>; the slot at index RefLen carries the
    /// post-escape Z value (or the terminal Z when the orbit ran to
    /// the iteration cap without escaping).</summary>
    public sealed class DecodedOrbit
    {
        public required byte    Limbs       { get; init; }
        public required int     RefLen      { get; init; }
        public required int     MaxIter     { get; init; }
        public required bool    Escaped     { get; init; }
        public required double  CentreX     { get; init; }
        public required double  CentreXLo   { get; init; }
        public required double  CentreY     { get; init; }
        public required double  CentreYLo   { get; init; }
        public required double[] RefZr      { get; init; }
        public required double[] RefZi      { get; init; }
        public required double[] RefZrLo    { get; init; }
        public required double[] RefZiLo    { get; init; }
    }

    /// <summary>Encode a DD-precision orbit. Arrays must be sized at
    /// least <c>refLen + 1</c>; only the first refLen+1 doubles of each
    /// are written.</summary>
    public static byte[] EncodeDD(
        int refLen, int maxIter, bool escaped,
        double centreX, double centreXLo, double centreY, double centreYLo,
        double[] refZr, double[] refZi, double[] refZrLo, double[] refZiLo)
    {
        if (refLen < 0) throw new ArgumentOutOfRangeException(nameof(refLen));
        int slots = refLen + 1;
        if (refZr.Length < slots || refZi.Length < slots
            || refZrLo.Length < slots || refZiLo.Length < slots)
            throw new ArgumentException(
                $"orbit arrays shorter than refLen+1 (need {slots})");

        int doubleBytes = slots * 8;
        byte[] buf = new byte[HeaderBytes + 4 * doubleBytes];
        var s = buf.AsSpan();

        s[0] = MagicByte;
        s[1] = FormatVersion;
        s[2] = LimbsDD;
        s[3] = (byte)(escaped ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(4, 4), refLen);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(8, 4), maxIter);
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(12, 8), centreX);
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(20, 8), centreXLo);
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(28, 8), centreY);
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(36, 8), centreYLo);

        int off = HeaderBytes;
        WriteArray(s, ref off, refZr, slots);
        WriteArray(s, ref off, refZi, slots);
        WriteArray(s, ref off, refZrLo, slots);
        WriteArray(s, ref off, refZiLo, slots);
        return buf;
    }

    public static DecodedOrbit Decode(byte[] blob)
    {
        if (blob == null) throw new ArgumentNullException(nameof(blob));
        if (blob.Length < HeaderBytes)
            throw new InvalidOperationException(
                $"orbit blob too small: {blob.Length} < {HeaderBytes}");
        var s = blob.AsSpan();

        if (s[0] != MagicByte)
            throw new InvalidOperationException(
                $"orbit blob magic mismatch: got 0x{s[0]:X2}, want 0x{MagicByte:X2}");
        if (s[1] != FormatVersion)
            throw new InvalidOperationException(
                $"orbit blob format version unsupported: {s[1]}");
        byte limbs = s[2];
        if (limbs != LimbsDD)
            throw new InvalidOperationException(
                $"orbit blob limbs={limbs} not supported (v1 = DD only)");
        bool escaped = s[3] != 0;
        int refLen  = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(4, 4));
        int maxIter = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(8, 4));
        if (refLen < 0 || maxIter < 0)
            throw new InvalidOperationException(
                $"orbit blob has negative refLen/maxIter ({refLen}/{maxIter})");

        double cx  = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(12, 8));
        double cxL = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(20, 8));
        double cy  = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(28, 8));
        double cyL = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(36, 8));

        int slots = refLen + 1;
        int expectedBytes = HeaderBytes + 4 * slots * 8;
        if (blob.Length < expectedBytes)
            throw new InvalidOperationException(
                $"orbit blob truncated: have {blob.Length}, need {expectedBytes}");

        int off = HeaderBytes;
        double[] zr   = ReadArray(s, ref off, slots);
        double[] zi   = ReadArray(s, ref off, slots);
        double[] zrLo = ReadArray(s, ref off, slots);
        double[] ziLo = ReadArray(s, ref off, slots);

        return new DecodedOrbit
        {
            Limbs     = limbs,
            RefLen    = refLen,
            MaxIter   = maxIter,
            Escaped   = escaped,
            CentreX   = cx,
            CentreXLo = cxL,
            CentreY   = cy,
            CentreYLo = cyL,
            RefZr     = zr,
            RefZi     = zi,
            RefZrLo   = zrLo,
            RefZiLo   = ziLo,
        };
    }

    private static void WriteArray(Span<byte> dst, ref int off, double[] src, int count)
    {
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(dst.Slice(off, 8), src[i]);
            off += 8;
        }
    }

    private static double[] ReadArray(Span<byte> src, ref int off, int count)
    {
        var arr = new double[count];
        for (int i = 0; i < count; i++)
        {
            arr[i] = BinaryPrimitives.ReadDoubleLittleEndian(src.Slice(off, 8));
            off += 8;
        }
        return arr;
    }
}
