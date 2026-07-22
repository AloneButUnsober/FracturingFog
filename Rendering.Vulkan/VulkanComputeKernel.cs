// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// VulkanComputeKernel.cs — V3 (#42)
//
// Cross-platform Vulkan implementation of FracturingFog.Rendering.IGpuKernel,
// the same boundary the Windows-only D3D MandelbrotGpuKernel plugs into. The
// calculator fleet (MandelbrotCalculator / EscapeTimeCalculator) drives Run()
// with per-pixel output buffers; this backend dispatches the SP escape-time
// kernel (and, when a GPU palette is active, the colour pack) on a Vulkan
// compute queue and reads the results back.
//
// Source parity: compiles the identical MandelbrotKernelSource HLSL that FXC
// compiles on Windows, via DXC -> SPIR-V with the V1/V2 -fvk-*-shift binding
// maps (b0->0, t0->100, u0..u3->200..203). See the headless --vulkanrenderprobe
// gate + Docs/Technical/Vulkan-Compute-DevelopmentPlan.md §V3.
//
// Lifecycle: persistent device objects (base pipeline, per-PaletteId colour
// pipelines, HOST_VISIBLE buffers re-allocated only on a dimension change,
// one command pool). Per-Run: a small descriptor pool + set + command buffer,
// created and destroyed each dispatch so a mid-session resize can never leave a
// descriptor pointing at a freed buffer. Memory is HOST_VISIBLE|HOST_COHERENT
// (direct map, no staging) — simplest correct path; a device-local + staging
// fast path is a later perf slice, not a V3 concern.
//
// Thread-affinity: like the D3D kernel, a single caller drives Run() from the
// calc thread. Not internally synchronised.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using FracturingFog.Interefaces;

namespace FracturingFog.Rendering.Vulkan;

/// <summary>
/// Vulkan compute <see cref="IGpuKernel"/>. Construct with a live
/// <see cref="VulkanContext"/> (device + compute queue already created); the
/// caller owns the context and disposes it after the kernel. All other Vulkan
/// objects are owned and freed by this kernel.
/// </summary>
public sealed unsafe class VulkanComputeKernel : IGpuKernel
{
    // DXC register-class -> binding shifts (V1/V2). u3 (gColor) = UShift + 3.
    private const int BShift = 0;
    private const int TShift = 100;
    private const int UShift = 200;
    private const int ColorBinding = UShift + 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct ParamsBlob
    {
        public int Width, Height, MaxIter;
        public float Bailout2;
        public float CXHi, CXLo, CYHi, CYLo, ScaleHi, ScaleLo;
        public int UsePerRow, FractalKind;
        public float Param0, Param1, DitherStrength;
        public float _pad; // -> 64 bytes
    }

    // V6 perturbation params (64 bytes): 4 doubles FIRST (offsets 0/8/16/24, all
    // 8-byte-aligned, no 16-byte-row straddle) then 8 ints. Matches the HLSL
    // PerturbParams cbuffer (doubles-first layout) byte-for-byte. RowBase is the
    // TDR row-band offset; Pad0..2 round the block to 64 bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct PerturbParamsBlob
    {
        public double Scale, EscapeR2, OffX0, OffY0;
        public int Width, Height, MaxIter, RefLen, RowBase, Pad0, Pad1, Pad2;
    }

    // #88 SA params (80 bytes): 5 doubles FIRST (0/8/16/24/32) then 10 ints.
    // Matches the HLSL PerturbParams cbuffer in BuildPerturbSA byte-for-byte.
    [StructLayout(LayoutKind.Sequential)]
    private struct PerturbSaParamsBlob
    {
        public double Scale, EscapeR2, OffX0, OffY0, SaTol;
        public int Width, Height, MaxIter, RefLen, RowBase, SafeMax, Pad0, Pad1, Pad2, Pad3;
    }

    // TDR row-band tiling helper lives in MandelbrotKernelSource (shared with the
    // D3D backend). See MandelbrotKernelSource.PerturbBandRows.

    private struct Allocated { public Buffer Buffer; public DeviceMemory Memory; public ulong Size; }

    // A compiled compute program: shader module + pipeline + its layout objects.
    private sealed class Program
    {
        public ShaderModule Module;
        public DescriptorSetLayout Dsl;
        public PipelineLayout Layout;
        public Pipeline Pipeline;
        public int BindingCount;      // 5 (base) or 6 (colour)
    }

    private readonly VulkanContext _ctx;
    private readonly bool _ownsContext;
    private readonly Vk _vk;
    private readonly Device _device;

    private Program? _base;                                   // no-colour variant
    private readonly Dictionary<string, Program> _colorById = new(StringComparer.Ordinal);
    private string? _activePaletteId;

    // V6 (#82): deep-zoom perturbation program + its dedicated buffers (the
    // reference-orbit SSBOs + the 48-byte double param UBO). Output iter/smooth/
    // finalZD reuse the shared _buf[2..4].
    private Program? _perturb;
    private Allocated _perturbParams;
    private Allocated _refZrBuf, _refZiBuf;
    private int _refAlloc;

    // #88 SA spike: iteration-skipping perturbation program + its coefficient
    // SSBOs (A/B/C/D complex = 8 double arrays) and 80-byte SA param UBO.
    private Program? _perturbSa;
    private Allocated _saParams;
    private Allocated _saAR, _saAI, _saBR, _saBI, _saCR, _saCI, _saDR, _saDI;
    private int _saAlloc;

    // Persistent buffers, indexed: 0=params,1=perRow,2=iter,3=smooth,4=finalZD,5=color.
    private readonly Allocated[] _buf = new Allocated[6];
    private int _allocW, _allocH;
    private CommandPool _cmdPool;
    private bool _disposed;

    public double LastDispatchMs { get; private set; }
    public double LastReadbackMs { get; private set; }
    public bool HasGpuPalette => _activePaletteId != null && _colorById.ContainsKey(_activePaletteId);

    /// <param name="ctx">A context whose logical device is already created.</param>
    /// <param name="ownsContext">When true, <see cref="Dispose"/> also disposes
    /// <paramref name="ctx"/> — used by <see cref="TryCreateWithOwnContext"/> so
    /// the host can hand the calculator a self-contained kernel.</param>
    public VulkanComputeKernel(VulkanContext ctx, bool ownsContext = false)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        if (ctx.Device.Handle == 0)
            throw new ArgumentException("VulkanContext has no logical device — call CreateComputeDevice() first.", nameof(ctx));
        _ownsContext = ownsContext;
        _vk = ctx.Vk;
        _device = ctx.Device;

        var cpci = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = ctx.ComputeQueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(_vk.CreateCommandPool(_device, in cpci, null, out _cmdPool), "vkCreateCommandPool");
    }

    /// <summary>Human-readable backend label for the System Info dialog.</summary>
    public string Description => $"Vulkan compute ({_ctx.PickedType}: {_ctx.PickedName})";

    // ── host helpers (V3-GUI #57) ─────────────────────────────────────────────

    /// <summary>Probe the picked Vulkan device's name without keeping any state,
    /// or null when no compute-capable device exists. Used by the host bootstrap
    /// to decide whether to select the Vulkan backend and to build the System
    /// Info probe string. Never throws.</summary>
    public static string? ProbeDeviceName()
    {
        VulkanContext? ctx = null;
        try
        {
            ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) return null;
            ctx.CreateComputeDevice();
            return $"{ctx.PickedType}: {ctx.PickedName}";
        }
        catch { return null; }
        finally { ctx?.Dispose(); }
    }

    /// <summary>Probe whether the picked Vulkan device advertises shaderFloat64
    /// — i.e. whether <see cref="RunPerturb"/> (the deep-zoom double perturbation
    /// kernel) can run. Stands up and tears down a throwaway context; never
    /// throws (false on any failure). Used by the host to decide whether to
    /// enable <c>MandelbrotCalculator.UseGpuPerturbation</c>.</summary>
    public static bool ProbeSupportsFloat64()
    {
        VulkanContext? ctx = null;
        try
        {
            ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) return false;
            ctx.CreateComputeDevice();
            return ctx.SupportsFloat64;
        }
        catch { return false; }
        finally { ctx?.Dispose(); }
    }

    /// <summary>Create a self-contained kernel that owns a fresh
    /// <see cref="VulkanContext"/> (instance + compute device), or null when no
    /// Vulkan device is available or init fails. The returned kernel disposes its
    /// context on <see cref="Dispose"/>. Suitable as a
    /// <c>FractalRenderHost.GpuKernelFactory</c>. Never throws.</summary>
    public static VulkanComputeKernel? TryCreateWithOwnContext()
    {
        VulkanContext? ctx = null;
        try
        {
            ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) { ctx.Dispose(); return null; }
            ctx.CreateComputeDevice();
            return new VulkanComputeKernel(ctx, ownsContext: true);
        }
        catch
        {
            ctx?.Dispose();
            return null;
        }
    }

    public void SetPalette(IGpuHlslPalette? palette)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VulkanComputeKernel));
        if (palette == null) { _activePaletteId = null; return; }
        string id = palette.PaletteId ?? "";
        if (string.IsNullOrEmpty(id)) { _activePaletteId = null; return; }
        if (_colorById.ContainsKey(id)) { _activePaletteId = id; return; }
        try
        {
            string hlsl = MandelbrotKernelSource.BuildColor(palette.HlslPrelude, palette.HlslPaletteBody);
            _colorById[id] = BuildProgram(hlsl, MandelbrotKernelSource.EntryPoint,
                0u, (uint)TShift, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2, (uint)ColorBinding);
            _activePaletteId = id;
        }
        catch (Exception ex)
        {
            // Mirror the D3D kernel: a palette that fails to compile falls back
            // to the CPU palette pass rather than throwing into the calc loop.
            Debug.WriteLine($"[VulkanComputeKernel] palette '{id}' SPIR-V compile failed; staying on CPU palette: {ex.Message}");
            _activePaletteId = null;
        }
    }

    public void Run(
        int width, int height,
        double centerX, double centerY,
        double scale, int maxIter, double bailout2,
        int[] iterDst, float[] smoothDst,
        float[] finalZrDst, float[] finalZiDst,
        float[] finalDrDst, float[] finalDiDst,
        int[]? perRowMaxIter = null,
        FractalKind kind = FractalKind.Mandelbrot,
        float param0 = 0f, float param1 = 0f,
        uint[]? colorDst = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VulkanComputeKernel));
        if (width <= 0 || height <= 0) return;

        bool useColor = colorDst != null && HasGpuPalette;
        Program prog = useColor ? _colorById[_activePaletteId!]
            : (_base ??= BuildProgram(MandelbrotKernelSource.BuildBase(), MandelbrotKernelSource.EntryPoint,
                0u, (uint)TShift, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2));

        long t0 = Stopwatch.GetTimestamp();
        int n = width * height;
        EnsureBuffers(width, height);

        bool usePerRow = perRowMaxIter != null && perRowMaxIter.Length >= height;

        // Upload params.
        float cxHi = (float)centerX, cyHi = (float)centerY, scHi = (float)scale;
        var blob = new ParamsBlob
        {
            Width = width, Height = height, MaxIter = maxIter,
            Bailout2 = (float)bailout2,
            CXHi = cxHi, CXLo = (float)(centerX - cxHi),
            CYHi = cyHi, CYLo = (float)(centerY - cyHi),
            ScaleHi = scHi, ScaleLo = (float)(scale - scHi),
            UsePerRow = usePerRow ? 1 : 0,
            FractalKind = (int)kind,
            Param0 = param0, Param1 = param1,
            // Same runtime dither knob as the D3D path so GPU output matches.
            DitherStrength = FracturingFog.Models.GradientColorMap.DitherEnabled
                ? FracturingFog.Models.GradientColorMap.DitherStrength : 0f,
            _pad = 0f,
        };
        WriteBytes(_buf[0], &blob, sizeof(ParamsBlob));

        // Per-row caps: upload as uint[] (narrow negatives to 0 -> shader falls
        // back to gMaxIter), else zero the (still-bound) buffer.
        if (usePerRow)
        {
            var tmp = new uint[height];
            for (int i = 0; i < height; i++) { int v = perRowMaxIter![i]; tmp[i] = v > 0 ? (uint)v : 0u; }
            fixed (uint* p = tmp) WriteBytes(_buf[1], p, height * sizeof(uint));
        }
        else ZeroBuffer(_buf[1]);

        // Per-Run descriptor pool + set bound to the active program.
        int bc = prog.BindingCount;
        DescriptorPool pool = default;
        CommandBuffer cmd = default;
        try
        {
            var poolSizes = stackalloc DescriptorPoolSize[2]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
                new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = (uint)(bc - 1) },
            };
            var dpci = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1, PoolSizeCount = 2, PPoolSizes = poolSizes,
            };
            Check(_vk.CreateDescriptorPool(_device, in dpci, null, out pool), "vkCreateDescriptorPool");

            var dslLocal = prog.Dsl;
            var dsai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &dslLocal,
            };
            Check(_vk.AllocateDescriptorSets(_device, in dsai, out DescriptorSet set), "vkAllocateDescriptorSets");

            // Binding order: UBO@0, perRow@100, iter@200, smooth@201, finalZD@202, [color@203].
            uint* bindNums = stackalloc uint[6] { 0, (uint)TShift, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2, (uint)ColorBinding };
            var types = stackalloc DescriptorType[6]
            {
                DescriptorType.UniformBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
                DescriptorType.StorageBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            };
            int* bufIndex = stackalloc int[6] { 0, 1, 2, 3, 4, 5 };
            var infos = stackalloc DescriptorBufferInfo[6];
            var writes = stackalloc WriteDescriptorSet[6];
            for (int i = 0; i < bc; i++)
            {
                infos[i] = new DescriptorBufferInfo { Buffer = _buf[bufIndex[i]].Buffer, Offset = 0, Range = Vk.WholeSize };
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set, DstBinding = bindNums[i], DescriptorCount = 1,
                    DescriptorType = types[i], PBufferInfo = &infos[i],
                };
            }
            _vk.UpdateDescriptorSets(_device, (uint)bc, writes, 0, null);

            var cbai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _cmdPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1,
            };
            Check(_vk.AllocateCommandBuffers(_device, in cbai, out cmd), "vkAllocateCommandBuffers");

            var begin = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(_vk.BeginCommandBuffer(cmd, in begin), "vkBeginCommandBuffer");
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, prog.Pipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, prog.Layout, 0, 1, &set, 0, null);
            _vk.CmdDispatch(cmd, (uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);
            Check(_vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");

            var cmdLocal = cmd;
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1, PCommandBuffers = &cmdLocal,
            };
            Check(_vk.QueueSubmit(_ctx.ComputeQueue, 1, &submit, default), "vkQueueSubmit");
            Check(_vk.QueueWaitIdle(_ctx.ComputeQueue), "vkQueueWaitIdle");
        }
        finally
        {
            if (cmd.Handle != 0) _vk.FreeCommandBuffers(_device, _cmdPool, 1, in cmd);
            if (pool.Handle != 0) _vk.DestroyDescriptorPool(_device, pool, null);
        }

        long tDispatch = Stopwatch.GetTimestamp();

        // Readback + scatter into the caller's CPU arrays.
        ReadIter(_buf[2], iterDst, n);
        ReadFloats(_buf[3], smoothDst, n);
        ReadFinalZD(_buf[4], finalZrDst, finalZiDst, finalDrDst, finalDiDst, n);
        if (useColor) ReadUints(_buf[5], colorDst!, n);

        long tEnd = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
        LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
    }

    // ── V6 (#82) deep-zoom perturbation ────────────────────────────────────────

    public bool SupportsPerturbation => _ctx.SupportsFloat64;

    public void RunPerturb(
        int width, int height,
        double scale, int maxIter, double escapeRadius2,
        double offsetX0, double offsetY0,
        double[] refZr, double[] refZi, int refLen,
        int[] iterDst, float[] smoothDst,
        float[] finalZrDst, float[] finalZiDst,
        float[] finalDrDst, float[] finalDiDst)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VulkanComputeKernel));
        if (!_ctx.SupportsFloat64)
            throw new NotSupportedException("Vulkan device has no shaderFloat64 — cannot run the perturbation kernel.");
        if (width <= 0 || height <= 0) return;
        if (refLen < 1) throw new ArgumentException("reference orbit is empty", nameof(refLen));
        if (refZr.Length < refLen || refZi.Length < refLen)
            throw new ArgumentException("reference-orbit arrays shorter than refLen");

        long t0 = Stopwatch.GetTimestamp();
        int n = width * height;
        EnsureBuffers(width, height);          // shares iter/smooth/finalZD at _buf[2..4]
        EnsurePerturbBuffers(refLen);
        _perturb ??= BuildProgram(MandelbrotKernelSource.BuildPerturb(), MandelbrotKernelSource.PerturbEntryPoint,
            0u, (uint)TShift, (uint)TShift + 1, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2);

        // Upload the reference orbit once (Hi-limb doubles). Params (with the
        // per-band RowBase) are re-written inside the band loop below.
        var blob = new PerturbParamsBlob
        {
            Width = width, Height = height, MaxIter = maxIter, RefLen = refLen,
            Scale = scale, EscapeR2 = escapeRadius2, OffX0 = offsetX0, OffY0 = offsetY0,
            RowBase = 0,
        };
        fixed (double* pr = refZr) WriteBytes(_refZrBuf, pr, refLen * sizeof(double));
        fixed (double* pi = refZi) WriteBytes(_refZiBuf, pi, refLen * sizeof(double));

        // Binding order matches BuildProgram's bindingNums above:
        //   b0=params, t0=refZr(100), t1=refZi(101), u0=iter(200), u1=smooth(201), u2=finalZD(202).
        Buffer* srcBufs = stackalloc Buffer[6]
        {
            _perturbParams.Buffer, _refZrBuf.Buffer, _refZiBuf.Buffer,
            _buf[2].Buffer, _buf[3].Buffer, _buf[4].Buffer,
        };
        uint* bindNums = stackalloc uint[6] { 0, (uint)TShift, (uint)TShift + 1, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2 };
        var types = stackalloc DescriptorType[6]
        {
            DescriptorType.UniformBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            DescriptorType.StorageBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
        };

        DescriptorPool pool = default;
        CommandBuffer cmd = default;
        try
        {
            var poolSizes = stackalloc DescriptorPoolSize[2]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
                new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 5 },
            };
            var dpci = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1, PoolSizeCount = 2, PPoolSizes = poolSizes,
            };
            Check(_vk.CreateDescriptorPool(_device, in dpci, null, out pool), "vkCreateDescriptorPool");

            var dslLocal = _perturb.Dsl;
            var dsai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &dslLocal,
            };
            Check(_vk.AllocateDescriptorSets(_device, in dsai, out DescriptorSet set), "vkAllocateDescriptorSets");

            var infos = stackalloc DescriptorBufferInfo[6];
            var writes = stackalloc WriteDescriptorSet[6];
            for (int i = 0; i < 6; i++)
            {
                infos[i] = new DescriptorBufferInfo { Buffer = srcBufs[i], Offset = 0, Range = Vk.WholeSize };
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set, DstBinding = bindNums[i], DescriptorCount = 1,
                    DescriptorType = types[i], PBufferInfo = &infos[i],
                };
            }
            _vk.UpdateDescriptorSets(_device, 6, writes, 0, null);

            var cbai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _cmdPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1,
            };

            // TDR tiling — dispatch the frame in row bands, each a separate
            // submit, so no single GPU packet runs long enough to trip the
            // watchdog. The descriptor set (bindings) is fixed; only the UBO's
            // RowBase changes per band, and QueueWaitIdle after each submit
            // guarantees the band finished reading the UBO before the next
            // WriteBytes overwrites it.
            int bandRows = MandelbrotKernelSource.PerturbBandRows(width, height, maxIter);
            int bandCount = (height + bandRows - 1) / bandRows;
            int bandIndex = 0;
            for (int rowBase = 0; rowBase < height; rowBase += bandRows, bandIndex++)
            {
                int rows = Math.Min(bandRows, height - rowBase);
                blob.RowBase = rowBase;
                WriteBytes(_perturbParams, &blob, sizeof(PerturbParamsBlob));

                Check(_vk.AllocateCommandBuffers(_device, in cbai, out cmd), "vkAllocateCommandBuffers");
                var begin = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(_vk.BeginCommandBuffer(cmd, in begin), "vkBeginCommandBuffer");
                _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _perturb.Pipeline);
                _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _perturb.Layout, 0, 1, &set, 0, null);
                _vk.CmdDispatch(cmd, (uint)((width + 7) / 8), (uint)((rows + 7) / 8), 1);
                Check(_vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");

                var cmdLocal = cmd;
                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1, PCommandBuffers = &cmdLocal,
                };
                long tBand = Stopwatch.GetTimestamp();
                Check(_vk.QueueSubmit(_ctx.ComputeQueue, 1, &submit, default), "vkQueueSubmit");
                Check(_vk.QueueWaitIdle(_ctx.ComputeQueue), "vkQueueWaitIdle");
                _vk.FreeCommandBuffers(_device, _cmdPool, 1, in cmd);
                cmd = default;

                // Perf-fallback: after the FIRST band, extrapolate the whole-frame
                // time. If the GPU is too slow at this depth (weak FP64), abort so
                // the caller falls back to the CPU deep path instead of grinding
                // for minutes. QueueWaitIdle above makes this band's time real.
                if (bandIndex == 0 && bandCount > 1)
                {
                    double band0Ms = (Stopwatch.GetTimestamp() - tBand) * 1000.0 / Stopwatch.Frequency;
                    if (MandelbrotKernelSource.PerturbTooSlow(band0Ms, bandCount))
                    {
                        if (pool.Handle != 0) { _vk.DestroyDescriptorPool(_device, pool, null); pool = default; }
                        throw new TimeoutException(
                            $"{MandelbrotKernelSource.PerturbTooSlowMarker}: band0={band0Ms:F1}ms × {bandCount} bands " +
                            $"> {MandelbrotKernelSource.PerturbBudgetMs:F0}ms budget");
                    }
                }
            }
        }
        finally
        {
            if (cmd.Handle != 0) _vk.FreeCommandBuffers(_device, _cmdPool, 1, in cmd);
            if (pool.Handle != 0) _vk.DestroyDescriptorPool(_device, pool, null);
        }

        long tDispatch = Stopwatch.GetTimestamp();
        ReadIter(_buf[2], iterDst, n);
        ReadFloats(_buf[3], smoothDst, n);
        ReadFinalZD(_buf[4], finalZrDst, finalZiDst, finalDrDst, finalDiDst, n);
        long tEnd = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
        LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
    }

    /// <summary>#88 SA spike — deep-zoom perturbation with a Series-Approximation
    /// prelude. Same rebased δ loop as <see cref="RunPerturb"/>, but each pixel
    /// first analytically skips to iteration k via the uploaded SA coefficients
    /// (A/B/C/D, length refLen+1). Correctness is speed-independent; validates on
    /// weak-FP64 hardware. Perf sign-off is deferred to strong-FP64 HW.</summary>
    public void RunPerturbSA(
        int width, int height,
        double scale, int maxIter, double escapeRadius2,
        double offsetX0, double offsetY0,
        double[] refZr, double[] refZi, int refLen,
        double saTolerance, int safeMax,
        double[] aR, double[] aI, double[] bR, double[] bI,
        double[] cR, double[] cI, double[] dR, double[] dI,
        int[] iterDst, float[] smoothDst,
        float[] finalZrDst, float[] finalZiDst,
        float[] finalDrDst, float[] finalDiDst)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VulkanComputeKernel));
        if (!_ctx.SupportsFloat64)
            throw new NotSupportedException("Vulkan device has no shaderFloat64 — cannot run the SA perturbation kernel.");
        if (width <= 0 || height <= 0) return;
        if (refLen < 1) throw new ArgumentException("reference orbit is empty", nameof(refLen));

        int coeffLen = aR.Length;   // SA arrays are refLen+1 long
        if (coeffLen < refLen + 1)
            throw new ArgumentException("SA coefficient arrays shorter than refLen+1");

        long t0 = Stopwatch.GetTimestamp();
        int n = width * height;
        EnsureBuffers(width, height);
        EnsurePerturbBuffers(refLen);
        EnsurePerturbSaBuffers(coeffLen);
        _perturbSa ??= BuildProgram(MandelbrotKernelSource.BuildPerturbSA(), MandelbrotKernelSource.PerturbSaEntryPoint,
            0u, (uint)TShift, (uint)TShift + 1,
            (uint)TShift + 2, (uint)TShift + 3, (uint)TShift + 4, (uint)TShift + 5,
            (uint)TShift + 6, (uint)TShift + 7, (uint)TShift + 8, (uint)TShift + 9,
            (uint)UShift, (uint)UShift + 1, (uint)UShift + 2);

        var blob = new PerturbSaParamsBlob
        {
            Width = width, Height = height, MaxIter = maxIter, RefLen = refLen,
            Scale = scale, EscapeR2 = escapeRadius2, OffX0 = offsetX0, OffY0 = offsetY0,
            SaTol = saTolerance, SafeMax = safeMax, RowBase = 0,
        };
        fixed (double* pr = refZr) WriteBytes(_refZrBuf, pr, refLen * sizeof(double));
        fixed (double* pi = refZi) WriteBytes(_refZiBuf, pi, refLen * sizeof(double));
        fixed (double* p = aR) WriteBytes(_saAR, p, coeffLen * sizeof(double));
        fixed (double* p = aI) WriteBytes(_saAI, p, coeffLen * sizeof(double));
        fixed (double* p = bR) WriteBytes(_saBR, p, coeffLen * sizeof(double));
        fixed (double* p = bI) WriteBytes(_saBI, p, coeffLen * sizeof(double));
        fixed (double* p = cR) WriteBytes(_saCR, p, coeffLen * sizeof(double));
        fixed (double* p = cI) WriteBytes(_saCI, p, coeffLen * sizeof(double));
        fixed (double* p = dR) WriteBytes(_saDR, p, coeffLen * sizeof(double));
        fixed (double* p = dI) WriteBytes(_saDI, p, coeffLen * sizeof(double));

        // Binding order matches BuildProgram above: b0, t0,t1, t2..t9, u0,u1,u2.
        const int NB = 14;
        Buffer* srcBufs = stackalloc Buffer[NB]
        {
            _saParams.Buffer, _refZrBuf.Buffer, _refZiBuf.Buffer,
            _saAR.Buffer, _saAI.Buffer, _saBR.Buffer, _saBI.Buffer,
            _saCR.Buffer, _saCI.Buffer, _saDR.Buffer, _saDI.Buffer,
            _buf[2].Buffer, _buf[3].Buffer, _buf[4].Buffer,
        };
        uint* bindNums = stackalloc uint[NB]
        {
            0, (uint)TShift, (uint)TShift + 1,
            (uint)TShift + 2, (uint)TShift + 3, (uint)TShift + 4, (uint)TShift + 5,
            (uint)TShift + 6, (uint)TShift + 7, (uint)TShift + 8, (uint)TShift + 9,
            (uint)UShift, (uint)UShift + 1, (uint)UShift + 2,
        };
        var types = stackalloc DescriptorType[NB];
        types[0] = DescriptorType.UniformBuffer;
        for (int i = 1; i < NB; i++) types[i] = DescriptorType.StorageBuffer;

        DescriptorPool pool = default;
        CommandBuffer cmd = default;
        try
        {
            var poolSizes = stackalloc DescriptorPoolSize[2]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
                new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = NB - 1 },
            };
            var dpci = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1, PoolSizeCount = 2, PPoolSizes = poolSizes,
            };
            Check(_vk.CreateDescriptorPool(_device, in dpci, null, out pool), "vkCreateDescriptorPool");

            var dslLocal = _perturbSa.Dsl;
            var dsai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &dslLocal,
            };
            Check(_vk.AllocateDescriptorSets(_device, in dsai, out DescriptorSet set), "vkAllocateDescriptorSets");

            var infos = stackalloc DescriptorBufferInfo[NB];
            var writes = stackalloc WriteDescriptorSet[NB];
            for (int i = 0; i < NB; i++)
            {
                infos[i] = new DescriptorBufferInfo { Buffer = srcBufs[i], Offset = 0, Range = Vk.WholeSize };
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set, DstBinding = bindNums[i], DescriptorCount = 1,
                    DescriptorType = types[i], PBufferInfo = &infos[i],
                };
            }
            _vk.UpdateDescriptorSets(_device, NB, writes, 0, null);

            var cbai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _cmdPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1,
            };

            int bandRows = MandelbrotKernelSource.PerturbBandRows(width, height, maxIter);
            int bandCount = (height + bandRows - 1) / bandRows;
            int bandIndex = 0;
            for (int rowBase = 0; rowBase < height; rowBase += bandRows, bandIndex++)
            {
                int rows = Math.Min(bandRows, height - rowBase);
                blob.RowBase = rowBase;
                WriteBytes(_saParams, &blob, sizeof(PerturbSaParamsBlob));

                Check(_vk.AllocateCommandBuffers(_device, in cbai, out cmd), "vkAllocateCommandBuffers");
                var begin = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(_vk.BeginCommandBuffer(cmd, in begin), "vkBeginCommandBuffer");
                _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _perturbSa.Pipeline);
                _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _perturbSa.Layout, 0, 1, &set, 0, null);
                _vk.CmdDispatch(cmd, (uint)((width + 7) / 8), (uint)((rows + 7) / 8), 1);
                Check(_vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");

                var cmdLocal = cmd;
                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1, PCommandBuffers = &cmdLocal,
                };
                Check(_vk.QueueSubmit(_ctx.ComputeQueue, 1, &submit, default), "vkQueueSubmit");
                Check(_vk.QueueWaitIdle(_ctx.ComputeQueue), "vkQueueWaitIdle");
                _vk.FreeCommandBuffers(_device, _cmdPool, 1, in cmd);
                cmd = default;
            }
        }
        finally
        {
            if (cmd.Handle != 0) _vk.FreeCommandBuffers(_device, _cmdPool, 1, in cmd);
            if (pool.Handle != 0) _vk.DestroyDescriptorPool(_device, pool, null);
        }

        long tDispatch = Stopwatch.GetTimestamp();
        ReadIter(_buf[2], iterDst, n);
        ReadFloats(_buf[3], smoothDst, n);
        ReadFinalZD(_buf[4], finalZrDst, finalZiDst, finalDrDst, finalDiDst, n);
        long tEnd = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
        LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
    }

    private void EnsurePerturbSaBuffers(int coeffLen)
    {
        if (_saParams.Buffer.Handle == 0)
            _saParams = AllocBuffer((ulong)sizeof(PerturbSaParamsBlob), BufferUsageFlags.UniformBufferBit);
        if (_saAR.Buffer.Handle == 0 || _saAlloc < coeffLen)
        {
            FreeBuffer(ref _saAR); FreeBuffer(ref _saAI);
            FreeBuffer(ref _saBR); FreeBuffer(ref _saBI);
            FreeBuffer(ref _saCR); FreeBuffer(ref _saCI);
            FreeBuffer(ref _saDR); FreeBuffer(ref _saDI);
            ulong sz = (ulong)(coeffLen * sizeof(double));
            _saAR = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit);
            _saAI = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit);
            _saBR = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit);
            _saBI = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit);
            _saCR = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit);
            _saCI = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit);
            _saDR = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit);
            _saDI = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit);
            _saAlloc = coeffLen;
        }
    }

    private void EnsurePerturbBuffers(int refLen)
    {
        if (_perturbParams.Buffer.Handle == 0)
            _perturbParams = AllocBuffer((ulong)sizeof(PerturbParamsBlob), BufferUsageFlags.UniformBufferBit);
        if (_refZrBuf.Buffer.Handle == 0 || _refAlloc < refLen)
        {
            FreeBuffer(ref _refZrBuf);
            FreeBuffer(ref _refZiBuf);
            ulong sz = (ulong)(refLen * sizeof(double));
            _refZrBuf = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit);
            _refZiBuf = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit);
            _refAlloc = refLen;
        }
    }

    // ── pipeline build ────────────────────────────────────────────────────────
    // Compile + stand up a compute program. bindingNums[0] is the UBO (b0); the
    // rest are SSBOs, in the order the caller's descriptor writes use. entry
    // selects the HLSL entry point (CSMain for base/colour, CSPerturb for the
    // perturbation variant).
    private Program BuildProgram(string hlsl, string entry, params uint[] bindingNums)
    {
        byte[] spirv = DxcCompiler.CompileToSpirv(
            hlsl, entry, "cs_6_0",
            "-fvk-b-shift", BShift.ToString(), "0",
            "-fvk-t-shift", TShift.ToString(), "0",
            "-fvk-u-shift", UShift.ToString(), "0");

        var prog = new Program { BindingCount = bindingNums.Length };
        fixed (byte* code = spirv)
        {
            var smci = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length, PCode = (uint*)code,
            };
            Check(_vk.CreateShaderModule(_device, in smci, null, out prog.Module), "vkCreateShaderModule");
        }

        var bindings = stackalloc DescriptorSetLayoutBinding[bindingNums.Length];
        for (int i = 0; i < bindingNums.Length; i++)
            bindings[i] = LayoutBinding(bindingNums[i],
                i == 0 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer);
        var dslci = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)bindingNums.Length, PBindings = bindings,
        };
        Check(_vk.CreateDescriptorSetLayout(_device, in dslci, null, out prog.Dsl), "vkCreateDescriptorSetLayout");

        var dslLocal = prog.Dsl;
        var plci = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1, PSetLayouts = &dslLocal,
        };
        Check(_vk.CreatePipelineLayout(_device, in plci, null, out prog.Layout), "vkCreatePipelineLayout");

        nint entryPtr = SilkMarshal.StringToPtr(entry);
        try
        {
            var cpci = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = prog.Module, PName = (byte*)entryPtr,
                },
                Layout = prog.Layout,
            };
            Pipeline created;
            Check(_vk.CreateComputePipelines(_device, default, 1, &cpci, null, &created), "vkCreateComputePipelines");
            prog.Pipeline = created;
        }
        finally { SilkMarshal.Free(entryPtr); }

        return prog;
    }

    private void DestroyProgram(Program p)
    {
        if (p.Pipeline.Handle != 0) _vk.DestroyPipeline(_device, p.Pipeline, null);
        if (p.Layout.Handle != 0) _vk.DestroyPipelineLayout(_device, p.Layout, null);
        if (p.Dsl.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, p.Dsl, null);
        if (p.Module.Handle != 0) _vk.DestroyShaderModule(_device, p.Module, null);
    }

    // ── buffers ───────────────────────────────────────────────────────────────
    private void EnsureBuffers(int width, int height)
    {
        if (_buf[2].Buffer.Handle != 0 && _allocW == width && _allocH == height) return;

        for (int i = 0; i < _buf.Length; i++) FreeBuffer(ref _buf[i]);

        int n = width * height;
        _buf[0] = AllocBuffer(64, BufferUsageFlags.UniformBufferBit);                       // params
        _buf[1] = AllocBuffer((ulong)(Math.Max(height, 1) * sizeof(uint)), BufferUsageFlags.StorageBufferBit); // perRow
        _buf[2] = AllocBuffer((ulong)(n * sizeof(uint)),  BufferUsageFlags.StorageBufferBit);  // iter
        _buf[3] = AllocBuffer((ulong)(n * sizeof(float)), BufferUsageFlags.StorageBufferBit);  // smooth
        _buf[4] = AllocBuffer((ulong)(n * 4 * sizeof(float)), BufferUsageFlags.StorageBufferBit); // finalZD
        _buf[5] = AllocBuffer((ulong)(n * sizeof(uint)),  BufferUsageFlags.StorageBufferBit);  // color
        _allocW = width; _allocH = height;
    }

    private Allocated AllocBuffer(ulong size, BufferUsageFlags usage)
    {
        var bci = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive,
        };
        Check(_vk.CreateBuffer(_device, in bci, null, out Buffer buffer), "vkCreateBuffer");
        _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements req);
        uint memType = FindMemoryType(req.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        var mai = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = memType,
        };
        Check(_vk.AllocateMemory(_device, in mai, null, out DeviceMemory mem), "vkAllocateMemory");
        Check(_vk.BindBufferMemory(_device, buffer, mem, 0), "vkBindBufferMemory");
        return new Allocated { Buffer = buffer, Memory = mem, Size = size };
    }

    private void FreeBuffer(ref Allocated a)
    {
        if (a.Buffer.Handle != 0) _vk.DestroyBuffer(_device, a.Buffer, null);
        if (a.Memory.Handle != 0) _vk.FreeMemory(_device, a.Memory, null);
        a = default;
    }

    private void WriteBytes(Allocated a, void* src, int bytes)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.Memory, 0, (ulong)bytes, 0, &mapped), "vkMapMemory");
        System.Buffer.MemoryCopy(src, mapped, bytes, bytes);
        _vk.UnmapMemory(_device, a.Memory);
    }

    private void ZeroBuffer(Allocated a)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.Memory, 0, a.Size, 0, &mapped), "vkMapMemory");
        new Span<byte>(mapped, (int)a.Size).Clear();
        _vk.UnmapMemory(_device, a.Memory);
    }

    private void ReadIter(Allocated a, int[] dst, int n)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.Memory, 0, (ulong)(n * sizeof(uint)), 0, &mapped), "vkMapMemory");
        var src = (uint*)mapped;
        for (int i = 0; i < n; i++) dst[i] = (int)src[i];
        _vk.UnmapMemory(_device, a.Memory);
    }

    private void ReadFloats(Allocated a, float[] dst, int n)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.Memory, 0, (ulong)(n * sizeof(float)), 0, &mapped), "vkMapMemory");
        new Span<float>(mapped, n).CopyTo(dst.AsSpan(0, n));
        _vk.UnmapMemory(_device, a.Memory);
    }

    private void ReadUints(Allocated a, uint[] dst, int n)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.Memory, 0, (ulong)(n * sizeof(uint)), 0, &mapped), "vkMapMemory");
        new Span<uint>(mapped, n).CopyTo(dst.AsSpan(0, n));
        _vk.UnmapMemory(_device, a.Memory);
    }

    private void ReadFinalZD(Allocated a, float[] zr, float[] zi, float[] dr, float[] di, int n)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.Memory, 0, (ulong)(n * 4 * sizeof(float)), 0, &mapped), "vkMapMemory");
        var src = (float*)mapped;
        for (int i = 0; i < n; i++)
        {
            int b = i * 4;
            zr[i] = src[b + 0]; zi[i] = src[b + 1]; dr[i] = src[b + 2]; di[i] = src[b + 3];
        }
        _vk.UnmapMemory(_device, a.Memory);
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
    {
        PhysicalDeviceMemoryProperties memProps;
        _vk.GetPhysicalDeviceMemoryProperties(_ctx.PhysicalDevice, &memProps);
        var types = (MemoryType*)&memProps.MemoryTypes;
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 && (types[i].PropertyFlags & required) == required)
                return i;
        throw new InvalidOperationException($"no memory type with {required} for typeBits 0x{typeBits:X}");
    }

    private static DescriptorSetLayoutBinding LayoutBinding(uint binding, DescriptorType type) => new()
    {
        Binding = binding, DescriptorType = type, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit,
    };

    private static void Check(Result r, string what)
    {
        if (r != Result.Success) throw new InvalidOperationException($"{what} failed: {r}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_device.Handle != 0) _vk.DeviceWaitIdle(_device); } catch { }
        if (_base != null) { DestroyProgram(_base); _base = null; }
        if (_perturb != null) { DestroyProgram(_perturb); _perturb = null; }
        if (_perturbSa != null) { DestroyProgram(_perturbSa); _perturbSa = null; }
        foreach (var p in _colorById.Values) DestroyProgram(p);
        _colorById.Clear();
        for (int i = 0; i < _buf.Length; i++) FreeBuffer(ref _buf[i]);
        FreeBuffer(ref _perturbParams);
        FreeBuffer(ref _refZrBuf);
        FreeBuffer(ref _refZiBuf);
        FreeBuffer(ref _saParams);
        FreeBuffer(ref _saAR); FreeBuffer(ref _saAI);
        FreeBuffer(ref _saBR); FreeBuffer(ref _saBI);
        FreeBuffer(ref _saCR); FreeBuffer(ref _saCI);
        FreeBuffer(ref _saDR); FreeBuffer(ref _saDI);
        if (_cmdPool.Handle != 0) { _vk.DestroyCommandPool(_device, _cmdPool, null); _cmdPool = default; }
        if (_ownsContext) { try { _ctx.Dispose(); } catch { } }
    }
}
