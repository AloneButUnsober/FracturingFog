// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// OpenExrReader.cs
//
// Minimal OpenEXR scanline-image decoder. Pure managed (no native libIlmImf
// dependency, no NuGet) so the Avalonia shell ships unchanged on every
// platform per CLAUDE.md cross-platform mandate.
//
// What we decode
//   * Single-part EXR (the 1.x / 2.x scanline format the overwhelming
//     majority of HDRI library exports use — Poly Haven, sIBL, Substance,
//     Blender's default EXR exporter).
//   * RGB or RGBA channel sets. We pull R, G, B in alphabetical / dataWindow
//     order (B, G, R [, A]) per spec. Alpha is read when present but
//     discarded (HDRI lighting cares about radiance, not coverage).
//   * HALF (16-bit IEEE-754 binary16) and FLOAT (32-bit float) pixel types.
//     UINT is rejected — it's only used by depth / object-id passes, not
//     scene-referred lighting.
//   * NONE (uncompressed) and ZIP / ZIPS compression (deflate, zlib-wrapped
//     per the EXR spec). PIZ / DWA / B44 / PXR24 / RLE are out of scope —
//     wavelet+Huffman / lossy / float-residual codecs add several K lines
//     of code for diminishing return. If we hit one, fail loud so the user
//     converts in Blender / oiiotool rather than rendering garbled colours.
//
// What we do NOT decode
//   * Tiled images.
//   * Deep images (multi-sample-per-pixel).
//   * Multi-part EXR 2.0.
//   * Anything beyond the 3 RGB channels (custom AOVs / cryptomatte etc.).
//
// File layout (single-part scanline, what the parser walks)
//   magic     = 0x01312f76                          (4 bytes LE)
//   version   = 2 in low byte; flags in next 3      (4 bytes LE)
//   header    = sequence of (attrName:str0, attrType:str0, attrSize:i32,
//                            attrValue:bytes)
//               terminated by a single 0x00.
//   offsets   = i64 per scanline block (one block = chunk of <chunkLines>
//                                       scanlines), N = ceil(H / chunkLines).
//   chunks    = each preceded by (scanY:i32, chunkSize:i32) header; pixel
//               data follows in channel-major / row-major layout, ordered
//               alphabetically by channel name.
//
// All multi-byte integers are little-endian per spec.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FracturingFog.Rendering.Lighting;

internal static class OpenExrReader
{
    private const uint MagicNumber = 0x01312f76;

    private enum PixelTypeT { UInt = 0, Half = 1, Float = 2 }

    private enum Compression : byte
    {
        None = 0,
        Rle = 1,
        Zips = 2,   // single-line zlib
        Zip = 3,    // 16-line zlib block
        Piz = 4,
        Pxr24 = 5,
        B44 = 6,
        B44A = 7,
        Dwaa = 8,
        Dwab = 9,
    }

    private sealed class Channel
    {
        public string Name = "";
        public PixelTypeT PixelType;
        public int XSampling;
        public int YSampling;
    }

    /// <summary>Parse a single-part scanline EXR from <paramref name="stream"/>.
    /// Returns null on any unsupported feature or malformed input.</summary>
    public static HdriImage? Parse(Stream stream)
    {
        try
        {
            using var br = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (br.ReadUInt32() != MagicNumber) return null;
            uint version = br.ReadUInt32();
            int versionNumber = (int)(version & 0xFF);
            if (versionNumber != 2) return null;
            uint flags = version & 0xFFFFFF00u;
            // Bit 9 (0x200) = tiled. Bit 11 (0x800) = multi-part.
            // Bit 12 (0x1000) = non-image / deep. Reject any of those.
            if ((flags & (0x200u | 0x800u | 0x1000u)) != 0) return null;

            // ── header ────────────────────────────────────────────────
            int dwXmin = 0, dwYmin = 0, dwXmax = 0, dwYmax = 0;
            bool gotDataWindow = false;
            Compression comp = Compression.None;
            bool gotCompression = false;
            var channels = new List<Channel>();
            bool gotChannels = false;

            while (true)
            {
                string? name = ReadNullString(br, 256);
                if (name == null) return null;
                if (name.Length == 0) break; // end-of-header sentinel
                string? type = ReadNullString(br, 256);
                if (type == null) return null;
                int size = br.ReadInt32();
                if (size < 0 || size > (1 << 24)) return null;
                long endPos = br.BaseStream.Position + size;

                switch (name)
                {
                    case "dataWindow" when type == "box2i" && size == 16:
                        dwXmin = br.ReadInt32();
                        dwYmin = br.ReadInt32();
                        dwXmax = br.ReadInt32();
                        dwYmax = br.ReadInt32();
                        gotDataWindow = true;
                        break;
                    case "compression" when type == "compression" && size == 1:
                        comp = (Compression)br.ReadByte();
                        gotCompression = true;
                        break;
                    case "channels" when type == "chlist":
                        if (!ReadChannelList(br, size, channels)) return null;
                        gotChannels = true;
                        break;
                    default:
                        br.BaseStream.Seek(size, SeekOrigin.Current);
                        break;
                }

                if (br.BaseStream.Position != endPos)
                    br.BaseStream.Seek(endPos, SeekOrigin.Begin);
            }

            if (!gotDataWindow || !gotCompression || !gotChannels) return null;

            int width = dwXmax - dwXmin + 1;
            int height = dwYmax - dwYmin + 1;
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;

            // Locate R / G / B channels by name (case-sensitive per spec).
            // Spec stores channels alphabetically — they will appear in chunk
            // data in name order, so the offset of each channel inside a row
            // depends on its alphabetical position in the channel list.
            int rIdx = -1, gIdx = -1, bIdx = -1;
            for (int i = 0; i < channels.Count; i++)
            {
                if (channels[i].Name == "R") rIdx = i;
                else if (channels[i].Name == "G") gIdx = i;
                else if (channels[i].Name == "B") bIdx = i;
            }
            if (rIdx < 0 || gIdx < 0 || bIdx < 0) return null;

            foreach (var ch in channels)
            {
                if (ch.PixelType != PixelTypeT.Half && ch.PixelType != PixelTypeT.Float) return null;
                if (ch.XSampling != 1 || ch.YSampling != 1) return null;
            }

            // ── offset table ─────────────────────────────────────────
            int chunkLines = comp switch
            {
                Compression.None => 1,
                Compression.Rle  => 1,
                Compression.Zips => 1,
                Compression.Zip  => 16,
                _ => -1,
            };
            if (chunkLines < 0) return null; // PIZ / Pxr24 / B44 / DWA unsupported
            if (comp == Compression.Rle) return null; // RLE is rare for HDRI; skip

            int chunkCount = (height + chunkLines - 1) / chunkLines;
            var offsets = new long[chunkCount];
            for (int i = 0; i < chunkCount; i++) offsets[i] = br.ReadInt64();

            // ── compute per-channel byte size + per-row bytes ────────
            int channelCount = channels.Count;
            int[] chBytesPerSample = new int[channelCount];
            for (int i = 0; i < channelCount; i++)
                chBytesPerSample[i] = channels[i].PixelType == PixelTypeT.Half ? 2 : 4;

            int rowBytes = 0;
            int[] chRowBytes = new int[channelCount];
            for (int i = 0; i < channelCount; i++)
            {
                chRowBytes[i] = width * chBytesPerSample[i];
                rowBytes += chRowBytes[i];
            }

            var outBuf = new float[width * height * 3];

            // ── walk chunks ──────────────────────────────────────────
            byte[] decoded = Array.Empty<byte>();
            for (int ci = 0; ci < chunkCount; ci++)
            {
                br.BaseStream.Position = offsets[ci];
                int scanY = br.ReadInt32();   // first y of chunk (data-window-relative on load)
                int chunkSize = br.ReadInt32();
                if (chunkSize < 0 || chunkSize > (rowBytes * chunkLines * 4)) return null;

                int linesInChunk = Math.Min(chunkLines, dwYmax - scanY + 1);
                if (linesInChunk <= 0) return null;
                int decodedSize = rowBytes * linesInChunk;

                if (decoded.Length < decodedSize) decoded = new byte[decodedSize];
                if (comp == Compression.None)
                {
                    if (chunkSize != decodedSize) return null;
                    int read = br.Read(decoded, 0, decodedSize);
                    if (read != decodedSize) return null;
                }
                else // Zip / Zips
                {
                    var compressedBytes = br.ReadBytes(chunkSize);
                    if (compressedBytes.Length != chunkSize) return null;
                    if (!InflateExr(compressedBytes, decoded, decodedSize)) return null;
                }

                // EXR ZIP/ZIPS apply a per-byte predictor + interleave
                // unscramble. NONE skips both.
                if (comp == Compression.Zip || comp == Compression.Zips)
                {
                    Predictor(decoded, decodedSize);
                    Interleave(decoded, decodedSize);
                }

                // Decoded layout: channel-major within each scanline. For
                // line L (0..linesInChunk-1):
                //   bytes [L*rowBytes .. L*rowBytes + rowBytes)
                //     ch0 row: width * chBytes[0] bytes
                //     ch1 row: width * chBytes[1] bytes
                //     ...
                // EXR stores channels in alphabetical order; B < G < R so
                // channel 0 of each row is B, channel 1 is G, channel 2 is R.
                for (int li = 0; li < linesInChunk; li++)
                {
                    int yAbs = scanY + li - dwYmin;
                    if ((uint)yAbs >= (uint)height) continue;
                    int lineBase = li * rowBytes;
                    // Walk channels in order, accumulate per-channel base.
                    int chBase = lineBase;
                    int rBase = 0, gBase = 0, bBase = 0;
                    int rType = 0, gType = 0, bType = 0;
                    for (int i = 0; i < channelCount; i++)
                    {
                        if (i == rIdx) { rBase = chBase; rType = (int)channels[i].PixelType; }
                        else if (i == gIdx) { gBase = chBase; gType = (int)channels[i].PixelType; }
                        else if (i == bIdx) { bBase = chBase; bType = (int)channels[i].PixelType; }
                        chBase += chRowBytes[i];
                    }
                    int dstRow = yAbs * width * 3;
                    for (int x = 0; x < width; x++)
                    {
                        float R = ReadSample(decoded, rBase, x, rType);
                        float G = ReadSample(decoded, gBase, x, gType);
                        float B = ReadSample(decoded, bBase, x, bType);
                        int dst = dstRow + x * 3;
                        outBuf[dst + 0] = R;
                        outBuf[dst + 1] = G;
                        outBuf[dst + 2] = B;
                    }
                }
            }

            return new HdriImage(width, height, outBuf);
        }
        catch
        {
            return null;
        }
    }

    private static float ReadSample(byte[] buf, int chRowBase, int x, int pixelType)
    {
        if (pixelType == (int)PixelTypeT.Half)
        {
            int idx = chRowBase + x * 2;
            ushort raw = (ushort)(buf[idx] | (buf[idx + 1] << 8));
            return HalfToFloat(raw);
        }
        else // Float
        {
            int idx = chRowBase + x * 4;
            uint raw = (uint)(buf[idx] | (buf[idx + 1] << 8) | (buf[idx + 2] << 16) | (buf[idx + 3] << 24));
            return BitConverter.Int32BitsToSingle((int)raw);
        }
    }

    /// <summary>IEEE-754 binary16 → binary32. Inlined so the hot pixel loop
    /// doesn't pay the BitConverter.UInt16BitsToHalf round-trip cost.</summary>
    private static float HalfToFloat(ushort h)
    {
        uint sign = (uint)(h & 0x8000) << 16;
        uint exp = (uint)(h & 0x7C00) >> 10;
        uint mant = (uint)(h & 0x03FF);
        if (exp == 0)
        {
            if (mant == 0) return BitConverter.Int32BitsToSingle((int)sign);
            // Subnormal — normalise by shifting until top mantissa bit is set.
            int shift = 0;
            while ((mant & 0x0400) == 0) { mant <<= 1; shift++; }
            mant &= 0x03FF;
            uint outExp = (uint)(127 - 15 - shift + 1) << 23;
            return BitConverter.Int32BitsToSingle((int)(sign | outExp | (mant << 13)));
        }
        if (exp == 0x1F)
        {
            // Inf / NaN. Preserve mantissa bit pattern; reset exponent to 0xFF.
            uint outExp = 0xFFu << 23;
            return BitConverter.Int32BitsToSingle((int)(sign | outExp | (mant << 13)));
        }
        uint newExp = (exp + (127 - 15)) << 23;
        return BitConverter.Int32BitsToSingle((int)(sign | newExp | (mant << 13)));
    }

    private static string? ReadNullString(BinaryReader br, int maxLen)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < maxLen; i++)
        {
            int b = br.BaseStream.ReadByte();
            if (b < 0) return null;
            if (b == 0) return sb.ToString();
            sb.Append((char)b);
        }
        return null;
    }

    private static bool ReadChannelList(BinaryReader br, int size, List<Channel> channels)
    {
        long end = br.BaseStream.Position + size;
        while (br.BaseStream.Position < end)
        {
            // Each channel: name\0, pixelType(i32), pLinear(u8), reserved(3),
            // xSampling(i32), ySampling(i32). Terminator = empty name (single \0).
            int peek = br.BaseStream.ReadByte();
            if (peek < 0) return false;
            if (peek == 0) return true;
            var sb = new StringBuilder();
            sb.Append((char)peek);
            while (true)
            {
                int b = br.BaseStream.ReadByte();
                if (b < 0) return false;
                if (b == 0) break;
                sb.Append((char)b);
                if (sb.Length > 256) return false;
            }
            if (br.BaseStream.Position + 16 > end) return false;
            int pt = br.ReadInt32();
            br.BaseStream.Seek(4, SeekOrigin.Current); // pLinear + reserved
            int xs = br.ReadInt32();
            int ys = br.ReadInt32();
            channels.Add(new Channel
            {
                Name = sb.ToString(),
                PixelType = (PixelTypeT)pt,
                XSampling = xs,
                YSampling = ys,
            });
        }
        return br.BaseStream.Position == end;
    }

    /// <summary>EXR ZIP-block predictor. Per the spec: each byte after the
    /// first becomes byte[i-1] + byte[i] - 128, mod 256. Reverses the
    /// encoder's delta step so adjacent low-bit values compress better
    /// downstream.</summary>
    private static void Predictor(byte[] buf, int count)
    {
        int t1 = 1;
        while (t1 < count)
        {
            int d = (int)buf[t1 - 1] + (int)buf[t1] - 128;
            buf[t1] = (byte)d;
            t1++;
        }
    }

    /// <summary>EXR ZIP-block interleave-unscramble. Encoder splits the byte
    /// stream into evens + odds (concatenated) so the predictor sees runs of
    /// related bytes (low-byte of a HALF, then high-byte of a HALF, etc.).
    /// We reverse via a scratch swap.</summary>
    private static void Interleave(byte[] buf, int count)
    {
        var tmp = new byte[count];
        Array.Copy(buf, 0, tmp, 0, count);
        int t1 = 0;
        int t2 = (count + 1) / 2;
        int s = 0;
        while (true)
        {
            if (s < count) buf[s++] = tmp[t1++]; else break;
            if (s < count) buf[s++] = tmp[t2++]; else break;
        }
    }

    /// <summary>Inflate an EXR ZIP / ZIPS block. The spec wraps deflate in a
    /// 2-byte zlib header + 4-byte adler32 trailer. <see cref="DeflateStream"/>
    /// only handles raw deflate, so we strip the zlib header before passing
    /// the payload in. The trailer is unused (we trust the predictor /
    /// interleave to surface corruption).</summary>
    private static bool InflateExr(byte[] src, byte[] dst, int dstCount)
    {
        if (src.Length < 6) return false; // need at least 2 hdr + 4 trailer
        // zlib header: byte 0 = CMF (0x78 typical), byte 1 = FLG with check.
        // (CMF * 256 + FLG) % 31 == 0 per RFC 1950.
        int hdr = (src[0] << 8) | src[1];
        if ((hdr % 31) != 0) return false;
        // Strip 2-byte zlib hdr and 4-byte adler32 trailer for DeflateStream.
        int payload = src.Length - 6;
        if (payload <= 0) return false;
        using var ms = new MemoryStream(src, 2, payload, writable: false);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        int total = 0;
        while (total < dstCount)
        {
            int n = ds.Read(dst, total, dstCount - total);
            if (n <= 0) break;
            total += n;
        }
        return total == dstCount;
    }
}
