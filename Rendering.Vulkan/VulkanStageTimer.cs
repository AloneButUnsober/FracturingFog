// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.Vulkan/VulkanStageTimer.cs
//
// #1329 — GPU timestamp queries for a kernel's frame. A frame is several
// command buffers (one per TDR band / chunk, plus a readback submit), submitted
// in order; since #1334 a frame's bands don't wait for each other. Each command
// buffer starts with a Begin stamp (TOP_OF_PIPE) and closes each stage with a BOTTOM_OF_PIPE stamp; the first
// command buffer of the frame resets the pool. After the frame's last wait the
// results are read and turned into stage times (GpuStageTimes.FromStamps).
// Disabled (every call a no-op, the result null) when the queue family has no
// timestamp bits.

using System;
using System.Collections.Generic;
using FracturingFog.Rendering;
using Silk.NET.Vulkan;

namespace FracturingFog.Rendering.Vulkan;

internal sealed unsafe class VulkanStageTimer : IDisposable
{
    // Enough for a deep frame's bands (PerturbBandRows keeps them in the tens)
    // and a Buddhabrot batch's chunks; a frame that needs more reports null.
    private const uint Capacity = 1024;

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly QueryPool _pool;
    private readonly double _nsPerTick;
    private readonly ulong _mask;
    private readonly List<GpuStage> _stages = new();
    private bool _reset, _overflow;

    public bool Enabled { get; }

    public VulkanStageTimer(VulkanContext ctx)
    {
        _vk = ctx.Vk;
        _device = ctx.Device;
        uint bits = ctx.TimestampValidBits;
        _nsPerTick = ctx.TimestampPeriod;
        if (bits == 0 || !(_nsPerTick > 0)) return;
        _mask = bits >= 64 ? ulong.MaxValue : (1UL << (int)bits) - 1;
        var qpci = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = Capacity,
        };
        if (_vk.CreateQueryPool(_device, in qpci, null, out _pool) != Result.Success) return;
        Enabled = true;
    }

    /// <summary>Start a frame: forget the last frame's stamps.</summary>
    public void BeginFrame()
    {
        _stages.Clear();
        _reset = false;
        _overflow = false;
    }

    /// <summary>Right after vkBeginCommandBuffer: open a packet (resetting the pool
    /// in the frame's first command buffer).</summary>
    public void Begin(CommandBuffer cmd)
    {
        if (!Enabled) return;
        if (!_reset)
        {
            _vk.CmdResetQueryPool(cmd, _pool, 0, Capacity);
            _reset = true;
        }
        Write(cmd, GpuStage.Begin, PipelineStageFlags.TopOfPipeBit);
    }

    /// <summary>Close <paramref name="stage"/>: the GPU time since the previous stamp.</summary>
    public void Mark(CommandBuffer cmd, GpuStage stage)
    {
        if (!Enabled || !_reset) return;
        Write(cmd, stage, PipelineStageFlags.BottomOfPipeBit);
    }

    private void Write(CommandBuffer cmd, GpuStage stage, PipelineStageFlags at)
    {
        if (_stages.Count >= Capacity) { _overflow = true; return; }
        _vk.CmdWriteTimestamp(cmd, at, _pool, (uint)_stages.Count);
        _stages.Add(stage);
    }

    /// <summary>After the frame's last wait: read the stamps. Null when disabled,
    /// overflowed or empty.</summary>
    public GpuStageTimes? EndFrame()
    {
        if (!Enabled || _overflow || _stages.Count == 0) return null;
        int count = _stages.Count;
        var ticks = new ulong[count];
        Result r;
        fixed (ulong* p = ticks)
            r = _vk.GetQueryPoolResults(_device, _pool, 0, (uint)count, (nuint)(count * sizeof(ulong)), p,
                sizeof(ulong), QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
        if (r != Result.Success) return null;
        return GpuStageTimes.FromStamps(_stages, ticks, _nsPerTick, _mask);
    }

    public void Dispose()
    {
        if (Enabled && _pool.Handle != 0) _vk.DestroyQueryPool(_device, _pool, null);
    }
}
