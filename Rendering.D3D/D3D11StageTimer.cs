// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.D3D/D3D11StageTimer.cs
//
// #1329 — D3D11 GPU timestamp queries for a kernel's frame: a
// TIMESTAMP_DISJOINT query brackets the frame, a TIMESTAMP query is End()ed at
// each point (a Begin before each GPU packet — a dispatch or a band — and one
// after each stage). D3D11 inputs are dynamic buffers written by the CPU
// (Map WRITE_DISCARD), so there is no GPU upload stage: a frame reports its
// dispatch and its CopyResource to the staging buffers. Read after the frame's
// readback Map (the GPU has finished by then); a disjoint interval (clock
// change, e.g. a power-state switch) reports null. Callers hold the kernel's
// D3D gate around every call, as they do for the context work.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace FracturingFog.Rendering;

internal sealed class D3D11StageTimer : IDisposable
{
    private const int Capacity = 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct DisjointData { public ulong Frequency; public int Disjoint; }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private ID3D11Query? _disjoint;
    private readonly List<ID3D11Query> _queries = new();
    private readonly List<GpuStage> _stages = new();
    private bool _open, _overflow, _failed;

    public D3D11StageTimer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _ctx = context;
    }

    /// <summary>Open a frame (closing one an aborted frame left open).</summary>
    public void BeginFrame()
    {
        if (_failed) return;
        try
        {
            if (_open) _ctx.End(_disjoint!);
            _disjoint ??= _device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            _stages.Clear();
            _overflow = false;
            _ctx.Begin(_disjoint);
            _open = true;
        }
        catch { _failed = true; _open = false; }   // no timestamp queries on this device: stay off
    }

    /// <summary>Before a GPU packet (a dispatch, a band, the copy-out).</summary>
    public void Begin() => Stamp(GpuStage.Begin);

    /// <summary>After <paramref name="stage"/>.</summary>
    public void Mark(GpuStage stage) => Stamp(stage);

    private void Stamp(GpuStage stage)
    {
        if (!_open) return;
        int i = _stages.Count;
        if (i >= Capacity) { _overflow = true; return; }
        if (i == _queries.Count) _queries.Add(_device.CreateQuery(new QueryDescription(QueryType.Timestamp)));
        _ctx.End(_queries[i]);
        _stages.Add(stage);
    }

    /// <summary>Close the frame and read its stage times (null when off, aborted,
    /// overflowed or disjoint). #1377 — <paramref name="segmentsMs"/> receives
    /// each segment's time (see <see cref="GpuStageTimes.FromStamps"/>).</summary>
    public unsafe GpuStageTimes? EndFrame(List<double>? segmentsMs = null)
    {
        if (!_open) return null;
        _open = false;
        _ctx.End(_disjoint!);
        if (_overflow || _stages.Count == 0) return null;
        DisjointData dj;
        if (!Wait(_disjoint!, &dj, (uint)sizeof(DisjointData)) || dj.Disjoint != 0 || dj.Frequency == 0) return null;
        var ticks = new ulong[_stages.Count];
        for (int i = 0; i < ticks.Length; i++)
        {
            ulong t;
            if (!Wait(_queries[i], &t, sizeof(ulong))) return null;
            ticks[i] = t;
        }
        return GpuStageTimes.FromStamps(_stages, ticks, 1e9 / dj.Frequency, segmentsMs: segmentsMs);
    }

    // GetData returns S_FALSE until the GPU has reached the query; the frame's
    // readback Map has already waited for the work, so this is short.
    private unsafe bool Wait(ID3D11Asynchronous q, void* data, uint size)
    {
        long deadline = Environment.TickCount64 + 2000;
        while (true)
        {
            var r = _ctx.GetData(q, (IntPtr)data, size, AsyncGetDataFlags.None);
            if (r.Code == 0) return true;
            if (r.Failure || Environment.TickCount64 > deadline) return false;
            System.Threading.Thread.Yield();
        }
    }

    public void Dispose()
    {
        foreach (var q in _queries) { try { q.Dispose(); } catch { } }
        _queries.Clear();
        try { _disjoint?.Dispose(); } catch { }
    }
}
