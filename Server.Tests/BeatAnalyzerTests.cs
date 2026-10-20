// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using FracturingFog.Audio;
using Xunit;

namespace FracturingFog.Server.Tests;

// The live analyzer must actually raise Beat / Downbeat on a clearly rhythmic
// signal. BeatPulse / DownbeatPulse / BpmPhaseSaw / BpmPhaseSine all derive from
// those events (and the BPM estimated on them), so an analyzer that never fires
// leaves all four flat at 0 while the band / RMS signals still move.
public sealed class BeatAnalyzerTests
{
    private const int Rate = 44100;

    // Mono click track: a short decaying noise burst every `period` seconds over
    // a quiet noise floor (the floor keeps flux variance non-degenerate).
    private static float[] ClickTrack(double seconds, double period)
    {
        var rng = new Random(1234);
        int n = (int)(seconds * Rate);
        var buf = new float[n];
        int clickLen = Rate / 50; // 20 ms
        for (int i = 0; i < n; i++)
            buf[i] = (float)((rng.NextDouble() * 2 - 1) * 0.002);
        for (double t = 0.5; t < seconds; t += period)
        {
            int start = (int)(t * Rate);
            for (int k = 0; k < clickLen && start + k < n; k++)
            {
                double env = Math.Exp(-k / (clickLen / 4.0));
                buf[start + k] += (float)((rng.NextDouble() * 2 - 1) * 0.9 * env);
            }
        }
        return buf;
    }

    private static void Feed(BeatAnalyzer a, float[] mono)
    {
        const int Chunk = 1024;
        for (int pos = 0; pos < mono.Length; pos += Chunk)
            a.ProcessSamples(mono.AsSpan(pos, Math.Min(Chunk, mono.Length - pos)));
    }

    [Fact]
    public void Click_Track_Raises_Beats_Downbeats_And_Bpm()
    {
        var a = new BeatAnalyzer(Rate, channels: 1);
        int beats = 0, downbeats = 0;
        a.Beat += (_, _) => beats++;
        a.Downbeat += (_, _) => downbeats++;

        Feed(a, ClickTrack(seconds: 12.0, period: 0.5)); // 120 BPM, ~23 clicks

        Assert.True(beats >= 15, $"expected most clicks detected as beats, got {beats}");
        Assert.True(downbeats >= 3, $"expected a downbeat every 4 beats, got {downbeats}");
        Assert.InRange(a.EstimatedBpm, 110.0, 130.0);
    }

    [Fact]
    public void Click_Track_Raises_Beats_After_Format_Reset()
    {
        var a = new BeatAnalyzer(Rate, channels: 2);
        a.EnsureFormat(Rate, 1); // resets the refractory clock to its sentinel
        int beats = 0;
        a.Beat += (_, _) => beats++;

        Feed(a, ClickTrack(seconds: 6.0, period: 0.5));

        Assert.True(beats >= 6, $"expected beats after a format reset, got {beats}");
    }
}
