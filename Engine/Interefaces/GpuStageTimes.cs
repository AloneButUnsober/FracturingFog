// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Interefaces/GpuStageTimes.cs
//
// #1329 — per-stage GPU time from GPU timestamp queries (D3D11
// D3D11_QUERY_TIMESTAMP, Vulkan vkCmdWriteTimestamp). Until now every GPU
// figure was a CPU Stopwatch around submit + wait, mixing queue latency, the
// shader and the copy-back. The backends write a Begin stamp at the start of
// each GPU packet (a command buffer, or a D3D11 band) and a stamp after each
// stage; this file turns the stamps into stage times. Idle time between
// packets (the host waiting, the next band being recorded) is not counted:
// each packet's first segment starts at its own Begin stamp.

using System;
using System.Collections.Generic;

namespace FracturingFog.Rendering
{
    /// <summary>The GPU stage a timestamp closes. <see cref="Begin"/> opens a packet.</summary>
    public enum GpuStage : byte
    {
        Begin,
        /// <summary>Staged input copies (Vulkan device-local inputs, #1228).</summary>
        Upload,
        /// <summary>The compute dispatch (summed over bands / chunks).</summary>
        Dispatch,
        /// <summary>Output copies to host-readable memory (D3D11 staging, Vulkan
        /// device-outputs). Zero when the outputs are written host-visible.</summary>
        CopyOut,
    }

    /// <summary>#1329 — one frame's GPU time per stage, in milliseconds of device
    /// clock. <see cref="Packets"/> counts the GPU packets (command buffers /
    /// bands) the frame was split into.</summary>
    public readonly record struct GpuStageTimes(double UploadMs, double DispatchMs, double CopyOutMs, int Packets)
    {
        public double TotalMs => UploadMs + DispatchMs + CopyOutMs;

        public override string ToString() =>
            $"GPU upload {UploadMs:F2} ms, dispatch {DispatchMs:F2} ms, copy-out {CopyOutMs:F2} ms ({Packets} packet{(Packets == 1 ? "" : "s")})";

        /// <summary>Turn ordered timestamps into stage times. Each stamp closes the
        /// segment since the previous stamp of the same packet; a
        /// <see cref="GpuStage.Begin"/> stamp starts a new packet (the time before it
        /// is idle). <paramref name="validMask"/> wraps a counter with fewer than 64
        /// valid bits; <paramref name="nsPerTick"/> converts device ticks to ns.
        /// Null when there is no packet, or the stamps don't start with Begin.</summary>
        public static GpuStageTimes? FromStamps(IReadOnlyList<GpuStage> stages, ReadOnlySpan<ulong> ticks,
            double nsPerTick, ulong validMask = ulong.MaxValue)
        {
            int count = Math.Min(stages.Count, ticks.Length);
            if (count == 0 || stages[0] != GpuStage.Begin || !(nsPerTick > 0) || !double.IsFinite(nsPerTick)) return null;
            double up = 0, disp = 0, copy = 0;
            int packets = 0;
            for (int i = 0; i < count; i++)
            {
                if (stages[i] == GpuStage.Begin) { packets++; continue; }
                ulong dt = (ticks[i] - ticks[i - 1]) & validMask;
                double ms = dt * nsPerTick * 1e-6;
                switch (stages[i])
                {
                    case GpuStage.Upload: up += ms; break;
                    case GpuStage.Dispatch: disp += ms; break;
                    case GpuStage.CopyOut: copy += ms; break;
                }
            }
            return new GpuStageTimes(up, disp, copy, packets);
        }
    }
}
