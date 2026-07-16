// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Cluster/ReferenceOrbitBlobCodec.cs
// D-6b — encode / decode the master-computed Mandelbrot reference orbit
// shipped with every tile of a deep-zoom job. Workers seed their per-tile
// MandelbrotCalculator with the decoded arrays so the per-tile recompute
// short-circuits in the calculator's centre-cache check.
//
// On-wire format (little-endian, fixed 44-byte header then variable
// centre-limb extension then variable array block):
//
//   offset  size  field
//   ------  ----  ------------------------------------------------------
//    0       1    magic byte 0xD6 (sniff guard against accidental misuse)
//    1       1    format version  (currently 1)
//    2       1    limbs per slot  (2 = DD; 4 = QD; 8 = OD)
//    3       1    escaped flag    (1 if the orbit terminated by escape, else 0)
//    4       4    refLen          (int32; number of orbit steps stored)
//    8       4    maxIter         (int32; iteration cap used at compute time)
//   12       8    centreX  (X0 / Hi limb)
//   20       8    centreXLo (X1 limb)
//   28       8    centreY  (X0 / Hi limb)
//   36       8    centreYLo (X1 limb)
//   ── header ends at 44 ──
//
// D-6b2 — when limbs >= 4 (QD), append 4 more centre doubles
// (cx X2, cx X3, cy X2, cy X3) in 32 bytes. When limbs == 8 (OD), append
// 8 more centre doubles (cx X4..X7, cy X4..X7) in 64 bytes.
//
//   if limbs >= 4:
//     +0   8  centreX X2
//     +8   8  centreX X3
//     +16  8  centreY X2
//     +24  8  centreY X3
//   if limbs == 8 (immediately after the QD limbs above):
//     +0   8  centreX X4 .. X7  (4 doubles)
//     +32  8  centreY X4 .. X7  (4 doubles)
//
// Then the array block, with one double[refLen+1] per array, in this
// fixed order:
//
//   limbs=2 (DD): Zr_Hi, Zi_Hi, Zr_Lo, Zi_Lo
//   limbs=4 (QD): Zr_Hi, Zi_Hi, Zr_Lo, Zi_Lo, Zr_X2, Zi_X2, Zr_X3, Zi_X3
//   limbs=8 (OD): Zr_Hi, Zi_Hi, Zr_Lo, Zi_Lo,
//                 Zr_X2, Zi_X2, Zr_X3, Zi_X3,
//                 Zr_X4, Zi_X4, Zr_X5, Zi_X5,
//                 Zr_X6, Zi_X6, Zr_X7, Zi_X7
//
// The orbit array length is refLen+1 because ComputeReferenceOrbit writes
// the post-escape Z at index refLen (mirrors the engine's storage shape;
// see MandelbrotCalculator.ComputeReferenceOrbit).
//
// D-6b2 — earlier versions of this file accepted only DD blobs and threw
// on QD/OD. Format version stays at 1; the limbs byte (already reserved
// in v1) carries the differentiation, so an existing DD-only worker
// sees `limbs=4` and refuses with a clear error message. A QD/OD-capable
// worker switches on `limbs` and seeds the calculator's QD / OD orbit
// cache via SeedReferenceOrbitQD / SeedReferenceOrbitOD.

using System;
using System.Buffers.Binary;

namespace FracturingFog.Server.Cluster;

public static class ReferenceOrbitBlobCodec
{
    public const byte MagicByte     = 0xD6;
    public const byte FormatVersion = 1;
    public const byte LimbsDD       = 2;
    public const byte LimbsQD       = 4;
    public const byte LimbsOD       = 8;
    public const int  HeaderBytes   = 44;

    /// <summary>Decoded view of a shipped orbit. Hi/Lo arrays are always
    /// populated (DD/QD/OD all carry them). X2..X3 arrays populated only
    /// when <see cref="Limbs"/> >= 4 (QD or OD); X4..X7 populated only
    /// when <see cref="Limbs"/> == 8 (OD). Centre fields follow the same
    /// rule. Unused arrays are <see cref="Array.Empty{T}"/>; unused
    /// centre limbs are zero.</summary>
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

        // QD extension (limbs >= 4). Zero / empty when limbs == 2.
        public double CentreX2 { get; init; }
        public double CentreX3 { get; init; }
        public double CentreY2 { get; init; }
        public double CentreY3 { get; init; }
        public double[] RefZrX2 { get; init; } = Array.Empty<double>();
        public double[] RefZiX2 { get; init; } = Array.Empty<double>();
        public double[] RefZrX3 { get; init; } = Array.Empty<double>();
        public double[] RefZiX3 { get; init; } = Array.Empty<double>();

        // OD extension (limbs == 8). Zero / empty when limbs in {2, 4}.
        public double CentreX4 { get; init; }
        public double CentreX5 { get; init; }
        public double CentreX6 { get; init; }
        public double CentreX7 { get; init; }
        public double CentreY4 { get; init; }
        public double CentreY5 { get; init; }
        public double CentreY6 { get; init; }
        public double CentreY7 { get; init; }
        public double[] RefZrX4 { get; init; } = Array.Empty<double>();
        public double[] RefZiX4 { get; init; } = Array.Empty<double>();
        public double[] RefZrX5 { get; init; } = Array.Empty<double>();
        public double[] RefZiX5 { get; init; } = Array.Empty<double>();
        public double[] RefZrX6 { get; init; } = Array.Empty<double>();
        public double[] RefZiX6 { get; init; } = Array.Empty<double>();
        public double[] RefZrX7 { get; init; } = Array.Empty<double>();
        public double[] RefZiX7 { get; init; } = Array.Empty<double>();

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
        RequireSlots(slots, refZr, refZi, refZrLo, refZiLo);

        int doubleBytes = slots * 8;
        byte[] buf = new byte[HeaderBytes + 4 * doubleBytes];
        var s = buf.AsSpan();

        WriteHeader(s, LimbsDD, escaped, refLen, maxIter,
            centreX, centreXLo, centreY, centreYLo);

        int off = HeaderBytes;
        WriteArray(s, ref off, refZr, slots);
        WriteArray(s, ref off, refZi, slots);
        WriteArray(s, ref off, refZrLo, slots);
        WriteArray(s, ref off, refZiLo, slots);
        return buf;
    }

    /// <summary>D-6b2 — encode a QD-precision orbit (limbs=4). All four
    /// X-coord centre limbs and all four Y-coord centre limbs are
    /// shipped, plus 8 limb arrays. Arrays must be sized at least
    /// <c>refLen + 1</c>.</summary>
    public static byte[] EncodeQD(
        int refLen, int maxIter, bool escaped,
        double centreX,  double centreXLo, double centreX2, double centreX3,
        double centreY,  double centreYLo, double centreY2, double centreY3,
        double[] refZr, double[] refZi, double[] refZrLo, double[] refZiLo,
        double[] refZrX2, double[] refZiX2, double[] refZrX3, double[] refZiX3)
    {
        if (refLen < 0) throw new ArgumentOutOfRangeException(nameof(refLen));
        int slots = refLen + 1;
        RequireSlots(slots, refZr, refZi, refZrLo, refZiLo,
            refZrX2, refZiX2, refZrX3, refZiX3);

        const int qdExtBytes = 4 * 8;  // 4 centre doubles
        int doubleBytes = slots * 8;
        byte[] buf = new byte[HeaderBytes + qdExtBytes + 8 * doubleBytes];
        var s = buf.AsSpan();

        WriteHeader(s, LimbsQD, escaped, refLen, maxIter,
            centreX, centreXLo, centreY, centreYLo);

        int off = HeaderBytes;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreX2); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreX3); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreY2); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreY3); off += 8;

        WriteArray(s, ref off, refZr,   slots);
        WriteArray(s, ref off, refZi,   slots);
        WriteArray(s, ref off, refZrLo, slots);
        WriteArray(s, ref off, refZiLo, slots);
        WriteArray(s, ref off, refZrX2, slots);
        WriteArray(s, ref off, refZiX2, slots);
        WriteArray(s, ref off, refZrX3, slots);
        WriteArray(s, ref off, refZiX3, slots);
        return buf;
    }

    /// <summary>D-6b2 — encode an OD-precision orbit (limbs=8). All
    /// eight X-coord centre limbs and all eight Y-coord centre limbs
    /// are shipped, plus 16 limb arrays. Arrays must be sized at least
    /// <c>refLen + 1</c>.</summary>
    public static byte[] EncodeOD(
        int refLen, int maxIter, bool escaped,
        double centreX,  double centreXLo, double centreX2, double centreX3,
        double centreX4, double centreX5,  double centreX6, double centreX7,
        double centreY,  double centreYLo, double centreY2, double centreY3,
        double centreY4, double centreY5,  double centreY6, double centreY7,
        double[] refZr, double[] refZi, double[] refZrLo, double[] refZiLo,
        double[] refZrX2, double[] refZiX2, double[] refZrX3, double[] refZiX3,
        double[] refZrX4, double[] refZiX4, double[] refZrX5, double[] refZiX5,
        double[] refZrX6, double[] refZiX6, double[] refZrX7, double[] refZiX7)
    {
        if (refLen < 0) throw new ArgumentOutOfRangeException(nameof(refLen));
        int slots = refLen + 1;
        RequireSlots(slots, refZr, refZi, refZrLo, refZiLo,
            refZrX2, refZiX2, refZrX3, refZiX3,
            refZrX4, refZiX4, refZrX5, refZiX5,
            refZrX6, refZiX6, refZrX7, refZiX7);

        const int qdExtBytes = 4 * 8;
        const int odExtBytes = 8 * 8;  // 4 X-coord + 4 Y-coord
        int doubleBytes = slots * 8;
        byte[] buf = new byte[HeaderBytes + qdExtBytes + odExtBytes + 16 * doubleBytes];
        var s = buf.AsSpan();

        WriteHeader(s, LimbsOD, escaped, refLen, maxIter,
            centreX, centreXLo, centreY, centreYLo);

        int off = HeaderBytes;
        // QD extension limbs (X2, X3 for X then Y).
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreX2); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreX3); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreY2); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreY3); off += 8;
        // OD extension limbs (X4..X7 for X then Y).
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreX4); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreX5); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreX6); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreX7); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreY4); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreY5); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreY6); off += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(off, 8), centreY7); off += 8;

        WriteArray(s, ref off, refZr,   slots);
        WriteArray(s, ref off, refZi,   slots);
        WriteArray(s, ref off, refZrLo, slots);
        WriteArray(s, ref off, refZiLo, slots);
        WriteArray(s, ref off, refZrX2, slots);
        WriteArray(s, ref off, refZiX2, slots);
        WriteArray(s, ref off, refZrX3, slots);
        WriteArray(s, ref off, refZiX3, slots);
        WriteArray(s, ref off, refZrX4, slots);
        WriteArray(s, ref off, refZiX4, slots);
        WriteArray(s, ref off, refZrX5, slots);
        WriteArray(s, ref off, refZiX5, slots);
        WriteArray(s, ref off, refZrX6, slots);
        WriteArray(s, ref off, refZiX6, slots);
        WriteArray(s, ref off, refZrX7, slots);
        WriteArray(s, ref off, refZiX7, slots);
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
        if (limbs != LimbsDD && limbs != LimbsQD && limbs != LimbsOD)
            throw new InvalidOperationException(
                $"orbit blob limbs={limbs} not supported (must be 2 / 4 / 8)");
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
        int arrayCount = 2 * limbs;          // 4 / 8 / 16
        int extBytes   = limbs >= LimbsQD ? 4 * 8 : 0;  // QD adds 4 centre doubles
        if (limbs == LimbsOD) extBytes += 8 * 8;        // OD adds 8 more centre doubles
        int expectedBytes = HeaderBytes + extBytes + arrayCount * slots * 8;
        if (blob.Length < expectedBytes)
            throw new InvalidOperationException(
                $"orbit blob truncated: have {blob.Length}, need {expectedBytes}");

        int off = HeaderBytes;
        double cx2 = 0, cx3 = 0, cy2 = 0, cy3 = 0;
        double cx4 = 0, cx5 = 0, cx6 = 0, cx7 = 0;
        double cy4 = 0, cy5 = 0, cy6 = 0, cy7 = 0;
        if (limbs >= LimbsQD)
        {
            cx2 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
            cx3 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
            cy2 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
            cy3 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
        }
        if (limbs == LimbsOD)
        {
            cx4 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
            cx5 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
            cx6 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
            cx7 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
            cy4 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
            cy5 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
            cy6 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
            cy7 = BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(off, 8)); off += 8;
        }

        double[] zr   = ReadArray(s, ref off, slots);
        double[] zi   = ReadArray(s, ref off, slots);
        double[] zrLo = ReadArray(s, ref off, slots);
        double[] ziLo = ReadArray(s, ref off, slots);

        double[] zrX2 = Array.Empty<double>(), ziX2 = Array.Empty<double>();
        double[] zrX3 = Array.Empty<double>(), ziX3 = Array.Empty<double>();
        double[] zrX4 = Array.Empty<double>(), ziX4 = Array.Empty<double>();
        double[] zrX5 = Array.Empty<double>(), ziX5 = Array.Empty<double>();
        double[] zrX6 = Array.Empty<double>(), ziX6 = Array.Empty<double>();
        double[] zrX7 = Array.Empty<double>(), ziX7 = Array.Empty<double>();
        if (limbs >= LimbsQD)
        {
            zrX2 = ReadArray(s, ref off, slots);
            ziX2 = ReadArray(s, ref off, slots);
            zrX3 = ReadArray(s, ref off, slots);
            ziX3 = ReadArray(s, ref off, slots);
        }
        if (limbs == LimbsOD)
        {
            zrX4 = ReadArray(s, ref off, slots);
            ziX4 = ReadArray(s, ref off, slots);
            zrX5 = ReadArray(s, ref off, slots);
            ziX5 = ReadArray(s, ref off, slots);
            zrX6 = ReadArray(s, ref off, slots);
            ziX6 = ReadArray(s, ref off, slots);
            zrX7 = ReadArray(s, ref off, slots);
            ziX7 = ReadArray(s, ref off, slots);
        }

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
            CentreX2 = cx2, CentreX3 = cx3,
            CentreY2 = cy2, CentreY3 = cy3,
            CentreX4 = cx4, CentreX5 = cx5, CentreX6 = cx6, CentreX7 = cx7,
            CentreY4 = cy4, CentreY5 = cy5, CentreY6 = cy6, CentreY7 = cy7,
            RefZr     = zr,
            RefZi     = zi,
            RefZrLo   = zrLo,
            RefZiLo   = ziLo,
            RefZrX2 = zrX2, RefZiX2 = ziX2,
            RefZrX3 = zrX3, RefZiX3 = ziX3,
            RefZrX4 = zrX4, RefZiX4 = ziX4,
            RefZrX5 = zrX5, RefZiX5 = ziX5,
            RefZrX6 = zrX6, RefZiX6 = ziX6,
            RefZrX7 = zrX7, RefZiX7 = ziX7,
        };
    }

    private static void WriteHeader(
        Span<byte> s, byte limbs, bool escaped, int refLen, int maxIter,
        double centreX, double centreXLo, double centreY, double centreYLo)
    {
        s[0] = MagicByte;
        s[1] = FormatVersion;
        s[2] = limbs;
        s[3] = (byte)(escaped ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(4, 4), refLen);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(8, 4), maxIter);
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(12, 8), centreX);
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(20, 8), centreXLo);
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(28, 8), centreY);
        BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(36, 8), centreYLo);
    }

    private static void RequireSlots(int slots, params double[][] arrays)
    {
        foreach (var a in arrays)
            if (a.Length < slots)
                throw new ArgumentException(
                    $"orbit arrays shorter than refLen+1 (need {slots})");
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
