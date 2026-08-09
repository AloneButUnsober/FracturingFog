// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Offline analysis pass (Audio-Reactive Phase 7 / #266): runs the same
    /// <see cref="BeatAnalyzer"/> the live capture uses over a decoded PCM buffer
    /// and bakes the result into a deterministic, seekable
    /// <see cref="OfflineAudioModulationSource"/>.
    ///
    /// The analyzer is fed one hop-sized block at a time so the current audio time
    /// is known when each analysis window lands: band/RMS + BPM are snapshotted at
    /// each block boundary, and beat / downbeat events are stamped with the
    /// block-end audio time (rather than the live source's wall-clock
    /// <c>DateTime.UtcNow</c>, which is meaningless offline). The whole pass is a
    /// pure function of the samples, so the same file yields the same timeline.
    /// </summary>
    public static class OfflineAudioAnalysis
    {
        // Block size in mono frames: one analyzer hop (~23 ms at 44.1 kHz), so one
        // analysis window lands per block and beat times are accurate to a block.
        private const int BlockFrames = 1024;

        // f32le PCM the decode targets. Matches the analyzer's native format.
        private const int DecodeSampleRate = 44100;
        private const int DecodeChannels = 2;

        /// <summary>
        /// Bake a timeline from an interleaved float PCM buffer. Deterministic and
        /// ffmpeg-free — the unit-testable core.
        /// </summary>
        /// <param name="interleaved">Interleaved float32 samples in [-1, 1].</param>
        /// <param name="sampleRate">Samples per second per channel.</param>
        /// <param name="channels">Channel count (>= 1).</param>
        /// <param name="sensitivity">Onset sensitivity (0..1) — same knob as live.</param>
        /// <param name="bandWeights">Optional per-band flux weights (5 values).</param>
        public static OfflineAudioModulationSource AnalyzePcm(
            ReadOnlySpan<float> interleaved, int sampleRate, int channels,
            float sensitivity = 0.5f, float[]? bandWeights = null)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

            var analyzer = new BeatAnalyzer(sampleRate, channels) { Sensitivity = sensitivity };
            if (bandWeights != null) analyzer.SetBandWeights(bandWeights);

            var times = new List<double>();
            var bands = new List<BandEnergy>();
            var bpms = new List<double>();
            var beatT = new List<double>();
            var beatS = new List<float>();
            var downT = new List<double>();
            var downS = new List<float>();

            // Beat/downbeat handlers stamp the event at the block-end time that is
            // current when ProcessSamples raises them.
            double curSec = 0.0;
            void OnBeat(object? _, BeatEventArgs e) { beatT.Add(curSec); beatS.Add(Clamp01((float)e.Strength)); }
            void OnDown(object? _, BeatEventArgs e) { downT.Add(curSec); downS.Add(Clamp01((float)e.Strength)); }
            analyzer.Beat += OnBeat;
            analyzer.Downbeat += OnDown;

            try
            {
                int totalFrames = interleaved.Length / channels;
                for (int f = 0; f < totalFrames; f += BlockFrames)
                {
                    int fr = System.Math.Min(BlockFrames, totalFrames - f);
                    curSec = (f + fr) / (double)sampleRate;
                    analyzer.ProcessSamples(interleaved.Slice(f * channels, fr * channels));
                    times.Add(curSec);
                    bands.Add(analyzer.CurrentEnergy);
                    bpms.Add(analyzer.EstimatedBpm);
                }
            }
            finally
            {
                analyzer.Beat -= OnBeat;
                analyzer.Downbeat -= OnDown;
            }

            return new OfflineAudioModulationSource(
                times.ToArray(), bands.ToArray(), bpms.ToArray(),
                beatT.ToArray(), beatS.ToArray(), downT.ToArray(), downS.ToArray());
        }

        /// <summary>
        /// Decode <paramref name="audioPath"/> to f32le PCM with ffmpeg and bake a
        /// timeline. Returns null when the file is missing, ffmpeg is unavailable /
        /// fails, or the decode is empty. The caller resolves the ffmpeg binary
        /// (this project does not reference the encoder) and passes it in.
        /// </summary>
        public static OfflineAudioModulationSource? AnalyzeFile(
            string audioPath, string ffmpegExe,
            float sensitivity = 0.5f, float[]? bandWeights = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)) return null;
            if (string.IsNullOrWhiteSpace(ffmpegExe) || !File.Exists(ffmpegExe)) return null;

            byte[]? pcm = DecodePcm(audioPath, ffmpegExe, ct);
            if (pcm == null || pcm.Length < sizeof(float) * DecodeChannels) return null;

            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(pcm);
            return AnalyzePcm(samples, DecodeSampleRate, DecodeChannels, sensitivity, bandWeights);
        }

        // Shell ffmpeg to stream raw interleaved f32le stereo @ 44.1 kHz on stdout.
        private static byte[]? DecodePcm(string audioPath, string ffmpegExe, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(audioPath);
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
            psi.ArgumentList.Add("-acodec"); psi.ArgumentList.Add("pcm_f32le");
            psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(DecodeChannels.ToString());
            psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(DecodeSampleRate.ToString());
            psi.ArgumentList.Add("pipe:1");

            try
            {
                using var proc = new Process { StartInfo = psi };
                if (!proc.Start()) return null;

                // Drain stderr on a background thread so a chatty ffmpeg can't
                // deadlock the stdout pipe.
                var errThread = new Thread(() => { try { proc.StandardError.ReadToEnd(); } catch { } })
                { IsBackground = true };
                errThread.Start();

                using var ms = new MemoryStream();
                using (var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } }))
                {
                    proc.StandardOutput.BaseStream.CopyTo(ms, 1 << 16);
                    proc.WaitForExit();
                }
                errThread.Join(2000);

                if (ct.IsCancellationRequested) return null;
                if (proc.ExitCode != 0) return null;
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
