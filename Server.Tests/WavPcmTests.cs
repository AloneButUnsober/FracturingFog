// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Audio;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1432 — the in-process WAVE reader (no ffmpeg for `--audio x.wav`). Each file is
// built here byte by byte from the documented layout (RIFF / WAVE / "fmt " /
// "data", little-endian; codes 0x0001 PCM, 0x0003 float, 0xFFFE extensible with
// the code in the SubFormat's first two bytes; 8-bit offset binary), and must
// decode to the samples it was built from.
public sealed class WavPcmTests
{
    private static byte[] Wav(ushort format, ushort channels, int rate, ushort bits, byte[] data,
        bool extensible = false, byte[]? extraChunk = null)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(0); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        if (extraChunk != null)
        {
            w.Write(Encoding.ASCII.GetBytes("LIST")); w.Write(extraChunk.Length); w.Write(extraChunk);
            if ((extraChunk.Length & 1) == 1) w.Write((byte)0);   // pad byte
        }
        ushort block = (ushort)(channels * bits / 8);
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(extensible ? 40 : 16);
        w.Write(extensible ? (ushort)0xFFFE : format);
        w.Write(channels); w.Write(rate); w.Write(rate * block); w.Write(block); w.Write(bits);
        if (extensible)
        {
            w.Write((ushort)22); w.Write(bits); w.Write(3u);   // cbSize, valid bits, channel mask
            w.Write(format); w.Write(new byte[] { 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 });
        }
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data.Length); w.Write(data);
        w.Flush();
        var bytes = ms.ToArray();
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
        return bytes;
    }

    private static float[] Decode(byte[] wav, out int rate, out int ch)
    {
        Assert.True(WavPcm.TryDecode(wav, out var s, out rate, out ch, out var err), err);
        return s;
    }

    [Fact]
    public void Pcm16_Stereo()
    {
        var d = new List<byte>();
        foreach (short v in new short[] { 0, 16384, -32768, 32767 }) d.AddRange(BitConverter.GetBytes(v));
        var s = Decode(Wav(1, 2, 22050, 16, d.ToArray()), out int rate, out int ch);
        Assert.Equal(22050, rate); Assert.Equal(2, ch);
        Assert.Equal(new[] { 0f, 0.5f, -1f, 32767f / 32768f }, s);
    }

    [Fact]
    public void Pcm8_Is_Offset_Binary()
    {
        var s = Decode(Wav(1, 1, 8000, 8, new byte[] { 128, 192, 0, 255 }), out _, out _);
        Assert.Equal(new[] { 0f, 0.5f, -1f, 127f / 128f }, s);
    }

    [Fact]
    public void Pcm24_Sign_Extends()
    {
        // 0x400000 = +0.5, 0xC00000 = −0.5 (two's complement, little-endian).
        var s = Decode(Wav(1, 1, 48000, 24, new byte[] { 0, 0, 0x40, 0, 0, 0xC0 }), out _, out _);
        Assert.Equal(new[] { 0.5f, -0.5f }, s);
    }

    [Fact]
    public void Pcm32_And_Float32_And_Float64()
    {
        var i32 = BitConverter.GetBytes(int.MinValue / 4).Concat(BitConverter.GetBytes(1 << 30)).ToArray();
        Assert.Equal(new[] { -0.25f, 0.5f }, Decode(Wav(1, 1, 44100, 32, i32), out _, out _));
        var f32 = BitConverter.GetBytes(0.25f).Concat(BitConverter.GetBytes(-0.75f)).ToArray();
        Assert.Equal(new[] { 0.25f, -0.75f }, Decode(Wav(3, 1, 44100, 32, f32), out _, out _));
        var f64 = BitConverter.GetBytes(0.125).Concat(BitConverter.GetBytes(-1.0)).ToArray();
        Assert.Equal(new[] { 0.125f, -1f }, Decode(Wav(3, 1, 44100, 64, f64), out _, out _));
    }

    [Fact]
    public void Extensible_Takes_The_Code_From_The_SubFormat()
    {
        var s = Decode(Wav(1, 2, 96000, 24, new byte[] { 0, 0, 0x40, 0, 0, 0xC0 }, extensible: true), out int rate, out int ch);
        Assert.Equal(96000, rate); Assert.Equal(2, ch);
        Assert.Equal(new[] { 0.5f, -0.5f }, s);
        var f = BitConverter.GetBytes(0.25f).Concat(BitConverter.GetBytes(-0.75f)).ToArray();   // SubFormat = IEEE float
        Assert.Equal(new[] { 0.25f, -0.75f }, Decode(Wav(3, 1, 48000, 32, f, extensible: true), out _, out _));
    }

    [Fact]
    public void An_Odd_Sized_Chunk_Before_The_Format_Is_Skipped_With_Its_Pad_Byte()
    {
        var s = Decode(Wav(1, 1, 8000, 16, BitConverter.GetBytes((short)8192), extraChunk: new byte[] { 1, 2, 3 }), out _, out _);
        Assert.Equal(new[] { 0.25f }, s);
    }

    [Theory]
    [InlineData("alaw", "unsupported")]
    [InlineData("notriff", "not a RIFF WAVE")]
    [InlineData("block", "block alignment")]
    [InlineData("nodata", "no data chunk")]
    public void Refuses_What_It_Cannot_Read(string kind, string reason)
    {
        byte[] wav = kind switch
        {
            "alaw" => Wav(6, 1, 8000, 8, new byte[] { 1, 2 }),
            "notriff" => Encoding.ASCII.GetBytes("OggS0000WAVE...."),
            "block" => Corrupt(Wav(1, 2, 8000, 16, new byte[8])),
            _ => Wav(1, 1, 8000, 16, new byte[2])[..36],
        };
        Assert.False(WavPcm.TryDecode(wav, out _, out _, out _, out var err));
        Assert.Contains(reason, err);
    }

    private static byte[] Corrupt(byte[] wav)
    {
        BitConverter.GetBytes((ushort)3).CopyTo(wav, 12 + 8 + 12);   // nBlockAlign
        return wav;
    }

    // ── A click track through the offline analysis and a preset's binding ──

    [Fact]
    public void A_Click_Track_Pulses_The_Beat_Collapse_Demo_Every_Beat()
    {
        // 120 bpm: a decaying 60 Hz kick every 0.5 s, 6 s, mono 16-bit.
        const int sr = 44100; const double secs = 6.0;
        var d = new byte[(int)(sr * secs) * 2];
        for (int i = 0; i < d.Length / 2; i++)
        {
            double t = i / (double)sr, ph = t % 0.5;
            double v = ph < 0.15 ? Math.Exp(-ph * 40) * Math.Sin(2 * Math.PI * 60 * t) * 0.9 : 0;
            BitConverter.GetBytes((short)(v * 32767)).CopyTo(d, i * 2);
        }
        string dir = Path.Combine(Path.GetTempPath(), "ff-1432-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "clicks.wav");
            File.WriteAllBytes(path, Wav(1, 1, sr, 16, d));
            var baked = OfflineAudioAnalysis.AnalyzeWav(path, out var err);
            Assert.True(baked != null, err);

            var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == "Double Slit - Beat Collapse");
            var p = new FractalParameters();
            region.Params!.ApplyTo(p);
            const int fps = 20;
            var drive = BatchAudioDrive.Create(p, region.FractalType, baked!, BatchAudioDrive.Merge(region.AudioBindings, null), fps);
            var eta = new List<double>();
            for (int f = 0; f < (int)(secs * fps); f++) { drive.ApplyFrame(f); eta.Add(p.DualOrbitWhichPath); }

            // After the analyser has settled (2 s), the trace repeats with the beat:
            // autocorrelation at one beat (10 frames) beats half a beat (5 frames).
            var tail = eta.Skip(2 * fps).ToArray();
            double mean = tail.Average();
            double Ac(int lag) => Enumerable.Range(0, tail.Length - lag).Sum(i => (tail[i] - mean) * (tail[i + lag] - mean));
            Assert.True(tail.Max() - tail.Min() > 0.2, $"η swing {tail.Min():F3}..{tail.Max():F3}");
            Assert.True(Ac(10) > 0 && Ac(10) > Ac(5), $"autocorrelation beat {Ac(10):F4} vs half-beat {Ac(5):F4}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
