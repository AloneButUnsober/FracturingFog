// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// WavPcm.cs (#1432)
//
// A small reader for uncompressed RIFF WAVE files, so `--batch --audio x.wav`
// (and OfflineAudioAnalysis.AnalyzeWav) work without ffmpeg. Layout and codes
// follow the WAVE format as documented by Microsoft (WAVEFORMATEX and
// WAVEFORMATEXTENSIBLE, Microsoft Learn) and P. Kabal, "Wave File
// Specifications" (McGill MMSP Lab): a little-endian "RIFF" chunk of form type
// "WAVE" holding a "fmt " chunk (wFormatTag, nChannels, nSamplesPerSec,
// nAvgBytesPerSec, nBlockAlign, wBitsPerSample, then for EXTENSIBLE cbSize,
// wValidBitsPerSample, dwChannelMask and a SubFormat GUID whose first two bytes
// are the format code) and a "data" chunk; a pad byte follows an odd-sized
// chunk. Format codes: 0x0001 PCM (8-bit samples are offset binary, wider ones
// two's complement), 0x0003 IEEE float, 0xFFFE EXTENSIBLE.
// See Docs/Resources-Bibliography.md#wave-file-format.
//
// Supported: PCM 8 / 16 / 24 / 32-bit, IEEE float 32 / 64-bit, any channel
// count, plain or EXTENSIBLE. Anything else (A-law, ADPCM, MP3-in-WAV, …) is
// refused with a reason, so the caller can fall back to ffmpeg.

using System;
using System.Buffers.Binary;
using System.IO;

namespace FracturingFog.Audio
{
    public static class WavPcm
    {
        public const ushort FormatPcm = 0x0001, FormatFloat = 0x0003, FormatExtensible = 0xFFFE;

        /// <summary>Read <paramref name="path"/> as interleaved float samples in [-1, 1].</summary>
        public static bool TryRead(string path, out float[] samples, out int sampleRate, out int channels, out string? error)
        {
            samples = Array.Empty<float>(); sampleRate = 0; channels = 0; error = null;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { error = ex.Message; return false; }
            return TryDecode(bytes, out samples, out sampleRate, out channels, out error);
        }

        /// <summary>Decode a whole WAVE file held in memory (see <see cref="TryRead"/>).</summary>
        public static bool TryDecode(ReadOnlySpan<byte> b, out float[] samples, out int sampleRate, out int channels, out string? error)
        {
            samples = Array.Empty<float>(); sampleRate = 0; channels = 0; error = null;
            if (b.Length < 12 || !Id(b, 0, "RIFF") || !Id(b, 8, "WAVE")) { error = "not a RIFF WAVE file"; return false; }
            int pos = 12;
            ushort format = 0, bits = 0, block = 0; bool haveFmt = false;
            while (pos + 8 <= b.Length)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(b[(pos + 4)..]);
                int body = pos + 8;
                long end = body + (long)size;
                if (Id(b, pos, "fmt "))
                {
                    if (size < 16 || end > b.Length) { error = "truncated fmt chunk"; return false; }
                    format = BinaryPrimitives.ReadUInt16LittleEndian(b[body..]);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(b[(body + 2)..]);
                    sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[(body + 4)..]);
                    block = BinaryPrimitives.ReadUInt16LittleEndian(b[(body + 12)..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(b[(body + 14)..]);
                    if (format == FormatExtensible)
                    {
                        if (size < 40) { error = "truncated WAVE_FORMAT_EXTENSIBLE fmt chunk"; return false; }
                        format = BinaryPrimitives.ReadUInt16LittleEndian(b[(body + 24)..]);   // SubFormat's first two bytes
                    }
                    haveFmt = true;
                }
                else if (Id(b, pos, "data"))
                {
                    if (!haveFmt) { error = "data chunk before fmt chunk"; return false; }
                    long avail = Math.Min(end, b.Length) - body;   // tolerate a truncated last chunk
                    return Convert(b.Slice(body, (int)Math.Max(0, avail)), format, bits, block, channels, sampleRate,
                        out samples, out error);
                }
                pos = (int)Math.Min(int.MaxValue, end + (size & 1));   // pad byte after an odd-sized chunk
            }
            error = haveFmt ? "no data chunk" : "no fmt chunk";
            return false;
        }

        private static bool Convert(ReadOnlySpan<byte> d, ushort format, int bits, int block, int channels, int rate,
            out float[] samples, out string? error)
        {
            samples = Array.Empty<float>(); error = null;
            if (channels < 1 || rate < 1) { error = "bad channel count or sample rate"; return false; }
            int bytesPer = bits / 8;
            bool pcm = format == FormatPcm && bits is 8 or 16 or 24 or 32;
            bool flt = format == FormatFloat && bits is 32 or 64;
            if (!pcm && !flt) { error = $"unsupported WAVE encoding (format 0x{format:X4}, {bits}-bit); convert to PCM or float"; return false; }
            if (block != bytesPer * channels) { error = "block alignment does not match the sample size"; return false; }
            int n = d.Length / bytesPer / channels * channels;
            samples = new float[n];
            for (int i = 0; i < n; i++)
            {
                var s = d.Slice(i * bytesPer, bytesPer);
                samples[i] = (format, bits) switch
                {
                    (FormatPcm, 8) => (s[0] - 128) / 128f,                                   // offset binary
                    (FormatPcm, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                    (FormatPcm, 24) => ((s[0] | (s[1] << 8) | ((sbyte)s[2] << 16))) / 8388608f,
                    (FormatPcm, _) => (float)(BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648.0),
                    (_, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                    _ => (float)BinaryPrimitives.ReadDoubleLittleEndian(s),
                };
            }
            return true;
        }

        private static bool Id(ReadOnlySpan<byte> b, int at, string id)
            => at + 4 <= b.Length && b[at] == id[0] && b[at + 1] == id[1] && b[at + 2] == id[2] && b[at + 3] == id[3];
    }
}
