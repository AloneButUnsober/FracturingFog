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
// descriptor pointing at a freed buffer.
//
// Memory (#1228, VulkanBufferPlacement):
//   - Buffers the shader READS many times — the reference orbit, the SA / BLA
//     tables — are DEVICE_LOCAL behind a host-visible staging twin: uploads land
//     in staging and are copied in at the start of the next dispatch's command
//     buffer.
//   - The frame outputs (iter / smooth / finalZD / colour / trap / orbit means)
//     and the Buddhabrot histogram stay in host-cached memory (#1173-L): the
//     shader writes them while it computes, and those writes cross PCIe in the
//     shadow of the compute. Staging them in VRAM adds a copy after the dispatch —
//     measured on a GT 710: 1080p SP +3.5 ms at 64 iterations and +10 ms at 1024,
//     Buddhabrot 720p +2-4%. FF_VULKAN_MEMORY=device-outputs stages them anyway
//     (cleared with vkCmdFillBuffer, copied out after the dispatch), for judging
//     the trade on faster hardware.
//   - Params UBOs and the per-row caps are tiny and stay direct-mapped.
// Where host-visible memory already sits on a device-local heap (integrated
// GPUs, software devices) nothing is staged. FF_VULKAN_MEMORY=host restores the
// pre-#1228 all-host layout (escape hatch / A-B).
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
    private const int TrapBinding = UShift + 4;   // #1173-J: u4 (gTrap), orbit variant only

    [StructLayout(LayoutKind.Sequential)]
    private struct ParamsBlob
    {
        public int Width, Height, MaxIter;
        public float Bailout2;
        public float CXHi, CXLo, CYHi, CYLo, ScaleHi, ScaleLo;
        public int UsePerRow, FractalKind;
        public float Param0, Param1, DitherStrength;
        public float WarpStrength, WarpK, WarpHalfSpan;   // #1173-I domain warp
        public float _pad0, _pad1; // -> 80 bytes
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
        public int Width, Height, MaxIter, RefLen, RowBase, SafeMax, BlaLevels, Pad1, Pad2, Pad3;   // BlaLevels: G4.5b (was Pad0)
    }

    // TDR row-band tiling helper lives in MandelbrotKernelSource (shared with the
    // D3D backend). See MandelbrotKernelSource.PerturbBandRows.

    private struct Allocated
    {
        public Buffer Buffer; public DeviceMemory Memory; public ulong Size;
        public ulong AllocSize; public bool DeviceLocal;   // #1166 — resident accounting
        // #1228 — the host-visible twin of a device-local buffer (0 = direct-mapped).
        public Buffer Staging; public DeviceMemory StagingMemory; public ulong StagingAllocSize;
        public readonly bool Staged => Staging.Handle != 0;
        /// <summary>The memory the CPU maps: the staging twin, or the buffer itself.</summary>
        public readonly DeviceMemory HostMemory => Staged ? StagingMemory : Memory;
    }

    /// <summary>#1228 — what a buffer is for. <c>Host</c>: host-visible, direct map
    /// (UBOs, per-row caps). <c>Upload</c>: shader input, device-local behind a
    /// staging twin the CPU writes. <c>Output</c>: written by the shader and read
    /// back — host-cached and direct, or device-local behind a host-cached staging
    /// twin under <see cref="VulkanBufferPlacement.DeviceOutputs"/>.</summary>
    private enum Residency { Host, Upload, Output }

    // #1228 — staging → device copies and device fills waiting for the next
    // command buffer (FlushPending), recorded in call order.
    private readonly List<(Buffer Src, Buffer Dst, ulong Bytes)> _pendingCopies = new();
    private readonly List<(Buffer Dst, ulong Bytes)> _pendingFills = new();

    /// <summary>#1228 — the buffer layout this kernel runs.</summary>
    public VulkanBufferPlacement Placement { get; }

    // #1166 — device memory this kernel holds right now (every buffer goes through
    // AllocBuffer / FreeBuffer), and the part on a device-local heap (VRAM on a
    // discrete card; host-visible types can live on either heap).
    private long _residentBytes, _residentDeviceLocalBytes;

    /// <summary>#1166 — device memory the kernel's buffers hold now, in bytes.</summary>
    public long ResidentBytes => System.Threading.Interlocked.Read(ref _residentBytes);

    /// <summary>#1166 — the part of <see cref="ResidentBytes"/> on a device-local heap.</summary>
    public long ResidentDeviceLocalBytes => System.Threading.Interlocked.Read(ref _residentDeviceLocalBytes);

    // A compiled compute program: shader module + pipeline + its layout objects.
    private sealed class Program
    {
        public ShaderModule Module;
        public DescriptorSetLayout Dsl;
        public PipelineLayout Layout;
        public Pipeline Pipeline;
        public int BindingCount;      // 5 (base), 6 (colour) or 7 (orbit colour + trap, #1173-J)
    }

    private readonly VulkanContext _ctx;
    private readonly bool _ownsContext;
    private readonly Vk _vk;
    private readonly Device _device;

    // #1227: every SP variant is specialised for the fractal kind it runs
    // (MandelbrotKernelSource.ForKind) and built on first use.
    private readonly Dictionary<int, Program> _baseByKind = new();          // no-colour variant
    private readonly Dictionary<string, Func<int, Program>> _colorBuilders = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Id, int Kind), Program> _colorByIdKind = new();
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

    // Persistent buffers, indexed: 0=params,1=perRow,2=iter,3=smooth,4=finalZD,5=color,6=trap.
    private readonly Allocated[] _buf = new Allocated[7];
    private int _allocW, _allocH;
    private CommandPool _cmdPool;
    private bool _disposed;

    public double LastDispatchMs { get; private set; }
    public double LastReadbackMs { get; private set; }
    public bool HasGpuPalette => _activePaletteId != null && _colorBuilders.ContainsKey(_activePaletteId);

    private Program BaseProgram(int kind)
    {
        if (_baseByKind.TryGetValue(kind, out var p)) return p;
        p = BuildProgram(MandelbrotKernelSource.ForKind(MandelbrotKernelSource.BuildBase(), kind), MandelbrotKernelSource.EntryPoint,
            0u, (uint)TShift, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2);
        _baseByKind[kind] = p;
        return p;
    }

    private Program ColorProgram(string id, int kind)
    {
        if (_colorByIdKind.TryGetValue((id, kind), out var p)) return p;
        p = _colorBuilders[id](kind);
        _colorByIdKind[(id, kind)] = p;
        return p;
    }

    /// <param name="ctx">A context whose logical device is already created.</param>
    /// <param name="ownsContext">When true, <see cref="Dispose"/> also disposes
    /// <paramref name="ctx"/> — used by <see cref="TryCreateWithOwnContext"/> so
    /// the host can hand the calculator a self-contained kernel.</param>
    /// <param name="placement">#1228 — the buffer layout; null reads FF_VULKAN_MEMORY
    /// (see <see cref="VulkanHostMemory.PlacementFromEnvironment"/>).</param>
    public VulkanComputeKernel(VulkanContext ctx, bool ownsContext = false, VulkanBufferPlacement? placement = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        if (ctx.Device.Handle == 0)
            throw new ArgumentException("VulkanContext has no logical device — call CreateComputeDevice() first.", nameof(ctx));
        _ownsContext = ownsContext;
        Placement = placement ?? VulkanHostMemory.PlacementFromEnvironment();
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
    public static VulkanComputeKernel? TryCreateWithOwnContext(VulkanBufferPlacement? placement = null)
    {
        VulkanContext? ctx = null;
        try
        {
            ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) { ctx.Dispose(); return null; }
            ctx.CreateComputeDevice();
            return new VulkanComputeKernel(ctx, ownsContext: true, placement);
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
        if (_colorBuilders.ContainsKey(id)) { _activePaletteId = id; return; }
        try
        {
            // F16 (#603) — orbit palette (non-None mask) → orbit-accumulating
            // kernel; otherwise the plain escape-only colour kernel.
            // #1173-J — the orbit variant also binds the gTrap output (u4 → 204).
            Func<int, Program> build;
            if (palette is IGpuOrbitPalette o && o.OrbitInputs != GpuOrbitInputs.None)
            {
                string hlsl = MandelbrotKernelSource.BuildColorOrbit(palette.HlslPrelude, palette.HlslPaletteBody, (int)o.OrbitInputs);
                build = kind => BuildProgram(MandelbrotKernelSource.ForKind(hlsl, kind), MandelbrotKernelSource.EntryPoint,
                    0u, (uint)TShift, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2, (uint)ColorBinding, (uint)TrapBinding);
            }
            else
            {
                string hlsl = MandelbrotKernelSource.BuildColor(palette.HlslPrelude, palette.HlslPaletteBody);
                build = kind => BuildProgram(MandelbrotKernelSource.ForKind(hlsl, kind), MandelbrotKernelSource.EntryPoint,
                    0u, (uint)TShift, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2, (uint)ColorBinding);
            }
            _colorByIdKind[(id, 0)] = build(0);   // compile now so a bad theme falls back here
            _colorBuilders[id] = build;
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
        uint[]? colorDst = null,
        float[]? trapDst = null,
        GpuDomainWarp warp = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VulkanComputeKernel));
        if (width <= 0 || height <= 0) return;

        bool useColor = colorDst != null && HasGpuPalette;
        Program prog = useColor ? ColorProgram(_activePaletteId!, (int)kind) : BaseProgram((int)kind);

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
            WarpStrength = warp.Active ? warp.Strength : 0f,
            WarpK = warp.K,
            WarpHalfSpan = warp.HalfSpan,
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

            // Binding order: UBO@0, perRow@100, iter@200, smooth@201, finalZD@202, [color@203], [trap@204].
            uint* bindNums = stackalloc uint[7] { 0, (uint)TShift, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2, (uint)ColorBinding, (uint)TrapBinding };
            var types = stackalloc DescriptorType[7]
            {
                DescriptorType.UniformBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
                DescriptorType.StorageBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
                DescriptorType.StorageBuffer,
            };
            int* bufIndex = stackalloc int[7] { 0, 1, 2, 3, 4, 5, 6 };
            var infos = stackalloc DescriptorBufferInfo[7];
            var writes = stackalloc WriteDescriptorSet[7];
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
            FlushPending(cmd);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, prog.Pipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, prog.Layout, 0, 1, &set, 0, null);
            _vk.CmdDispatch(cmd, (uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);
            bool trapOut = useColor && trapDst != null && prog.BindingCount == 7;
            RecordReadback(cmd, Out(_buf[2], n * sizeof(uint)), Out(_buf[3], n * sizeof(float)), Out(_buf[4], n * 4 * sizeof(float)),
                useColor ? Out(_buf[5], n * sizeof(uint)) : default, trapOut ? Out(_buf[6], n * sizeof(float)) : default);
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
        if (useColor && trapDst != null && prog.BindingCount == 7) ReadFloats(_buf[6], trapDst, n);   // #1173-J

        long tEnd = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
        LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
    }

    // ── V6 (#82) deep-zoom perturbation ────────────────────────────────────────

    public bool SupportsPerturbation => _ctx.SupportsFloat64;

    public string BackendLabel => "Vulkan";

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

        var blob = new PerturbParamsBlob
        {
            Width = width, Height = height, MaxIter = maxIter, RefLen = refLen,
            Scale = scale, EscapeR2 = escapeRadius2, OffX0 = offsetX0, OffY0 = offsetY0,
            RowBase = 0,
        };
        DispatchPerturbPlain(_perturb, blob, width, height, maxIter, refZr, refZi, refLen, null, "");
        SubmitReadback(Out(_buf[2], n * sizeof(uint)), Out(_buf[3], n * sizeof(float)), Out(_buf[4], n * 4 * sizeof(float)));

        long tDispatch = Stopwatch.GetTimestamp();
        ReadIter(_buf[2], iterDst, n);
        ReadFloats(_buf[3], smoothDst, n);
        ReadFinalZD(_buf[4], finalZrDst, finalZiDst, finalDrDst, finalDiDst, n);
        long tEnd = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
        LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
    }

    /// <summary>#607 / G4.6 — the orbit kernel's double work is the plain kernel's.</summary>
    public bool SupportsPerturbationOrbit => SupportsPerturbation;

    /// <summary>#607 / G4.6 — deep-zoom perturbation with orbit accumulation
    /// (MandelbrotKernelSource.BuildPerturbOrbit, one program per mask): the plain
    /// rebased loop's dispatch plus the gOrbit means at binding 203 (u3).</summary>
    public void RunPerturbOrbit(
        int width, int height,
        double scale, int maxIter, double escapeRadius2,
        double offsetX0, double offsetY0,
        double[] refZr, double[] refZi, int refLen,
        int orbitMask, double centerRe, double centerIm,
        int[] iterDst, float[] smoothDst,
        float[] finalZrDst, float[] finalZiDst,
        float[] finalDrDst, float[] finalDiDst,
        float[] orbitDst)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VulkanComputeKernel));
        if (!_ctx.SupportsFloat64)
            throw new NotSupportedException("Vulkan device has no shaderFloat64 — cannot run the orbit perturbation kernel.");
        orbitMask &= MandelbrotKernelSource.OrbAll;
        int stride = MandelbrotKernelSource.PerturbOrbitStride(orbitMask);
        if (stride == 0) throw new ArgumentException("no orbit inputs in the mask", nameof(orbitMask));
        if (width <= 0 || height <= 0) return;
        if (refLen < 1) throw new ArgumentException("reference orbit is empty", nameof(refLen));
        if (refZr.Length < refLen || refZi.Length < refLen)
            throw new ArgumentException("reference-orbit arrays shorter than refLen");
        int n = width * height;
        if (orbitDst.Length < n * stride) throw new ArgumentException("orbitDst shorter than width * height * stride", nameof(orbitDst));

        long t0 = Stopwatch.GetTimestamp();
        EnsureBuffers(width, height);
        EnsurePerturbBuffers(refLen);
        if (_orbitOut.Buffer.Handle == 0 || _orbitOut.Size < (ulong)(n * stride * sizeof(float)))
        {
            FreeBuffer(ref _orbitOut);
            _orbitOut = AllocBuffer((ulong)(n * stride * sizeof(float)), BufferUsageFlags.StorageBufferBit, Residency.Output);
        }
        if (!_perturbOrbit.TryGetValue(orbitMask, out var prog))
        {
            prog = BuildProgram(MandelbrotKernelSource.BuildPerturbOrbit(orbitMask), MandelbrotKernelSource.PerturbOrbitEntryPoint,
                0u, (uint)TShift, (uint)TShift + 1, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2, (uint)UShift + 3);
            _perturbOrbit[orbitMask] = prog;
        }

        var blob = new PerturbParamsBlob
        {
            Width = width, Height = height, MaxIter = maxIter, RefLen = refLen,
            Scale = scale, EscapeR2 = escapeRadius2, OffX0 = offsetX0, OffY0 = offsetY0,
            RowBase = 0,
            Pad0 = BitConverter.SingleToInt32Bits((float)centerRe),   // gCRe
            Pad1 = BitConverter.SingleToInt32Bits((float)centerIm),   // gCIm
        };
        DispatchPerturbPlain(prog, blob, width, height, maxIter, refZr, refZi, refLen, _orbitOut, " (orbit)");
        SubmitReadback(Out(_buf[2], n * sizeof(uint)), Out(_buf[3], n * sizeof(float)), Out(_buf[4], n * 4 * sizeof(float)),
            Out(_orbitOut, (long)n * stride * sizeof(float)));

        long tDispatch = Stopwatch.GetTimestamp();
        ReadIter(_buf[2], iterDst, n);
        ReadFloats(_buf[3], smoothDst, n);
        ReadFinalZD(_buf[4], finalZrDst, finalZiDst, finalDrDst, finalDiDst, n);
        ReadFloats(_orbitOut, orbitDst, n * stride);
        long tEnd = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
        LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
    }

    // ── #838 / G4.7 Buddhabrot sample pass ───────────────────────────────────────

    // 64 bytes: 11 ints, 3 floats, 1 int, 1 float — the HLSL BuddhaParams cbuffer byte-for-byte.
    [StructLayout(LayoutKind.Sequential)]
    private struct BuddhaParamsBlob
    {
        public int Width, Height, MaxOrbit, InSet, Low, Mid, Hd;
        public uint Seed, Batch, ThreadBase, ThreadCount;
        public float Scale, MidX, MidY;
        public int MinIter;      // #1218
        public float Pad1;
    }

    private Program? _buddha;
    private Allocated _buddhaParams, _buddhaHits;

    public bool SupportsBuddhabrot => true;

    /// <summary>#838 / G4.7 — one Buddhabrot uniform sample batch (BuddhaKernelSource):
    /// the device histogram is cleared, the samples run in adaptive TDR-sized
    /// dispatches (one submit each, BuddhaKernelSource.Plan), and the three bands are
    /// added into the caller's arrays.</summary>
    public void RunBuddhaBatch(in GpuBuddhaBatch b, uint[] hitsR, uint[] hitsG, uint[] hitsB)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VulkanComputeKernel));
        int n = b.Width * b.Height;
        if (n <= 0 || b.Samples <= 0) return;
        if (hitsR.Length < n || hitsG.Length < n || hitsB.Length < n)
            throw new ArgumentException("hit arrays shorter than width * height");

        long t0 = Stopwatch.GetTimestamp();
        _buddha ??= BuildProgram(BuddhaKernelSource.Build(), BuddhaKernelSource.EntryPoint, 0u, (uint)UShift);
        if (_buddhaParams.Buffer.Handle == 0) _buddhaParams = AllocBuffer(64, BufferUsageFlags.UniformBufferBit);
        ulong hitBytes = (ulong)n * 3 * sizeof(uint);
        if (_buddhaHits.Buffer.Handle == 0 || _buddhaHits.Size < hitBytes)
        {
            FreeBuffer(ref _buddhaHits);
            _buddhaHits = AllocBuffer(hitBytes, BufferUsageFlags.StorageBufferBit, Residency.Output);
        }
        ZeroBuffer(_buddhaHits);

        var blob = new BuddhaParamsBlob
        {
            Width = b.Width, Height = b.Height, MaxOrbit = b.MaxOrbit, InSet = b.InSet ? 1 : 0,
            Low = b.Low, Mid = b.Mid, Hd = b.HighDefinition ? 1 : 0,
            Seed = b.Seed, Batch = (uint)b.Batch, ThreadCount = (uint)b.Samples,
            Scale = (float)b.Scale, MidX = (float)b.MidX, MidY = (float)b.MidY,
            MinIter = b.MinIter,
        };

        DescriptorPool pool = default;
        try
        {
            var poolSizes = stackalloc DescriptorPoolSize[2]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
                new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 1 },
            };
            var dpci = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1, PoolSizeCount = 2, PPoolSizes = poolSizes,
            };
            Check(_vk.CreateDescriptorPool(_device, in dpci, null, out pool), "vkCreateDescriptorPool");
            var dslLocal = _buddha.Dsl;
            var dsai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &dslLocal,
            };
            Check(_vk.AllocateDescriptorSets(_device, in dsai, out DescriptorSet set), "vkAllocateDescriptorSets");
            var infos = stackalloc DescriptorBufferInfo[2]
            {
                new DescriptorBufferInfo { Buffer = _buddhaParams.Buffer, Offset = 0, Range = Vk.WholeSize },
                new DescriptorBufferInfo { Buffer = _buddhaHits.Buffer, Offset = 0, Range = Vk.WholeSize },
            };
            var writes = stackalloc WriteDescriptorSet[2]
            {
                new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1,
                    DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &infos[0],
                },
                new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)UShift, DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &infos[1],
                },
            };
            _vk.UpdateDescriptorSets(_device, 2, writes, 0, null);

            var cbai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _cmdPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1,
            };
            BuddhaKernelSource.Plan(b.Samples, b.MaxOrbit, (baseT, count) =>
            {
                long tChunk = Stopwatch.GetTimestamp();
                var blobLocal = blob;
                blobLocal.ThreadBase = (uint)baseT;
                WriteBytes(_buddhaParams, &blobLocal, sizeof(BuddhaParamsBlob));
                var setForCmd = set;
                Check(_vk.AllocateCommandBuffers(_device, in cbai, out CommandBuffer cmd), "vkAllocateCommandBuffers");
                try
                {
                    var begin = new CommandBufferBeginInfo
                    {
                        SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                    };
                    Check(_vk.BeginCommandBuffer(cmd, in begin), "vkBeginCommandBuffer");
                    FlushPending(cmd);   // #1228: the histogram clear lands before the first chunk
                    _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _buddha.Pipeline);
                    _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _buddha.Layout, 0, 1, &setForCmd, 0, null);
                    _vk.CmdDispatch(cmd, (uint)((count + BuddhaKernelSource.GroupSize - 1) / BuddhaKernelSource.GroupSize), 1, 1);
                    Check(_vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");
                    var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
                    Check(_vk.QueueSubmit(_ctx.ComputeQueue, 1, &submit, default), "vkQueueSubmit");
                    // One packet per dispatch (TDR); the UBO is rewritten for the next.
                    Check(_vk.QueueWaitIdle(_ctx.ComputeQueue), "vkQueueWaitIdle");
                }
                finally { _vk.FreeCommandBuffers(_device, _cmdPool, 1, in cmd); }
                return (Stopwatch.GetTimestamp() - tChunk) * 1000.0 / Stopwatch.Frequency;
            });
        }
        finally
        {
            if (pool.Handle != 0) _vk.DestroyDescriptorPool(_device, pool, null);
        }

        SubmitReadback(Out(_buddhaHits, (long)hitBytes));
        long tDispatch = Stopwatch.GetTimestamp();
        void* mapped;
        Check(_vk.MapMemory(_device, _buddhaHits.HostMemory, 0, hitBytes, 0, &mapped), "vkMapMemory");
        var src = (uint*)mapped;
        for (int i = 0; i < n; i++)
        {
            hitsR[i] += src[i];
            hitsG[i] += src[n + i];
            hitsB[i] += src[2 * n + i];
        }
        _vk.UnmapMemory(_device, _buddhaHits.HostMemory);
        long tEnd = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
        LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
    }

    // #607 / G4.6 — orbit perturbation programs (one per mask) + the gOrbit output.
    private readonly Dictionary<int, Program> _perturbOrbit = new();
    private Allocated _orbitOut;

    /// <summary>Upload the reference orbit and run a BuildPerturb-layout program
    /// (plain or orbit variant) over the frame in TDR row bands, with the
    /// first-band too-slow abort. <paramref name="orbitOut"/> non-null binds the
    /// orbit variant's gOrbit at 203.</summary>
    private void DispatchPerturbPlain(Program prog, PerturbParamsBlob blob, int width, int height, int maxIter,
        double[] refZr, double[] refZi, int refLen, Allocated? orbitOut, string label)
    {
        fixed (double* pr = refZr) WriteBytes(_refZrBuf, pr, refLen * sizeof(double));
        fixed (double* pi = refZi) WriteBytes(_refZiBuf, pi, refLen * sizeof(double));

        // Binding order matches BuildProgram's bindingNums:
        //   b0=params, t0=refZr(100), t1=refZi(101), u0=iter(200), u1=smooth(201), u2=finalZD(202),
        //   [u3=orbit means(203), the orbit variant only (#607)].
        int nb = orbitOut.HasValue ? 7 : 6;
        Buffer* srcBufs = stackalloc Buffer[7]
        {
            _perturbParams.Buffer, _refZrBuf.Buffer, _refZiBuf.Buffer,
            _buf[2].Buffer, _buf[3].Buffer, _buf[4].Buffer, orbitOut.GetValueOrDefault().Buffer,
        };
        uint* bindNums = stackalloc uint[7] { 0, (uint)TShift, (uint)TShift + 1, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2, (uint)UShift + 3 };
        var types = stackalloc DescriptorType[7]
        {
            DescriptorType.UniformBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            DescriptorType.StorageBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            DescriptorType.StorageBuffer,
        };

        DescriptorPool pool = default;
        CommandBuffer cmd = default;
        try
        {
            var poolSizes = stackalloc DescriptorPoolSize[2]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
                new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = (uint)(nb - 1) },
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

            var infos = stackalloc DescriptorBufferInfo[7];
            var writes = stackalloc WriteDescriptorSet[7];
            for (int i = 0; i < nb; i++)
            {
                infos[i] = new DescriptorBufferInfo { Buffer = srcBufs[i], Offset = 0, Range = Vk.WholeSize };
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set, DstBinding = bindNums[i], DescriptorCount = 1,
                    DescriptorType = types[i], PBufferInfo = &infos[i],
                };
            }
            _vk.UpdateDescriptorSets(_device, (uint)nb, writes, 0, null);

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
                FlushPending(cmd);   // #1228: the orbit upload lands before band 0
                _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, prog.Pipeline);
                _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, prog.Layout, 0, 1, &set, 0, null);
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
                            $"> {MandelbrotKernelSource.PerturbBudgetMs:F0}ms budget{label}");
                    }
                }
            }
        }
        finally
        {
            if (cmd.Handle != 0) _vk.FreeCommandBuffers(_device, _cmdPool, 1, in cmd);
            if (pool.Handle != 0) _vk.DestroyDescriptorPool(_device, pool, null);
        }

    }

    /// <summary>#88 / G4.5 — the SA kernel runs wherever the plain double one does.</summary>
    public bool SupportsPerturbationSA => SupportsPerturbation;

    /// <summary>#88 SA spike — deep-zoom perturbation with a Series-Approximation
    /// prelude. Same rebased δ loop as <see cref="RunPerturb"/>, but each pixel
    /// first analytically skips to iteration k via the uploaded SA coefficients
    /// (A/B/C/D, length refLen+1). Correctness is speed-independent; validates on
    /// weak-FP64 hardware. Perf sign-off is deferred to strong-FP64 HW. G4.5 —
    /// carries the same first-band too-slow abort as <see cref="RunPerturb"/>.</summary>
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
        float[] finalDrDst, float[] finalDiDst,
        double[]? blaCoeffs = null, int blaLevels = 0)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VulkanComputeKernel));
        if (!_ctx.SupportsFloat64)
            throw new NotSupportedException("Vulkan device has no shaderFloat64 — cannot run the SA perturbation kernel.");
        if (blaCoeffs == null || blaCoeffs.Length < 5) blaLevels = 0;   // #88 / G4.5b
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
        EnsureBlaBuffer(blaLevels > 0 ? blaCoeffs!.Length : 5);
        _perturbSa ??= BuildProgram(MandelbrotKernelSource.BuildPerturbSA(), MandelbrotKernelSource.PerturbSaEntryPoint,
            0u, (uint)TShift, (uint)TShift + 1,
            (uint)TShift + 2, (uint)TShift + 3, (uint)TShift + 4, (uint)TShift + 5,
            (uint)TShift + 6, (uint)TShift + 7, (uint)TShift + 8, (uint)TShift + 9,
            (uint)TShift + 10,
            (uint)UShift, (uint)UShift + 1, (uint)UShift + 2);

        var blob = new PerturbSaParamsBlob
        {
            Width = width, Height = height, MaxIter = maxIter, RefLen = refLen,
            Scale = scale, EscapeR2 = escapeRadius2, OffX0 = offsetX0, OffY0 = offsetY0,
            SaTol = saTolerance, SafeMax = safeMax, RowBase = 0,
            BlaLevels = blaLevels,
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
        if (blaLevels > 0)
            fixed (double* p = blaCoeffs) WriteBytes(_blaBuf, p, blaCoeffs!.Length * sizeof(double));

        // Binding order matches BuildProgram above: b0, t0,t1, t2..t9, t10 (BLA), u0,u1,u2.
        const int NB = 15;
        Buffer* srcBufs = stackalloc Buffer[NB]
        {
            _saParams.Buffer, _refZrBuf.Buffer, _refZiBuf.Buffer,
            _saAR.Buffer, _saAI.Buffer, _saBR.Buffer, _saBI.Buffer,
            _saCR.Buffer, _saCI.Buffer, _saDR.Buffer, _saDI.Buffer,
            _blaBuf.Buffer,
            _buf[2].Buffer, _buf[3].Buffer, _buf[4].Buffer,
        };
        uint* bindNums = stackalloc uint[NB]
        {
            0, (uint)TShift, (uint)TShift + 1,
            (uint)TShift + 2, (uint)TShift + 3, (uint)TShift + 4, (uint)TShift + 5,
            (uint)TShift + 6, (uint)TShift + 7, (uint)TShift + 8, (uint)TShift + 9,
            (uint)TShift + 10,
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
                FlushPending(cmd);   // #1228: orbit + SA / BLA uploads land before band 0
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
                long tBand = Stopwatch.GetTimestamp();
                Check(_vk.QueueSubmit(_ctx.ComputeQueue, 1, &submit, default), "vkQueueSubmit");
                Check(_vk.QueueWaitIdle(_ctx.ComputeQueue), "vkQueueWaitIdle");
                _vk.FreeCommandBuffers(_device, _cmdPool, 1, in cmd);
                cmd = default;

                // G4.5 — the same perf abort as RunPerturb: extrapolate the frame from
                // band 0 and hand back to the CPU deep path when the GPU is too slow.
                if (bandIndex == 0 && bandCount > 1)
                {
                    double band0Ms = (Stopwatch.GetTimestamp() - tBand) * 1000.0 / Stopwatch.Frequency;
                    if (MandelbrotKernelSource.PerturbTooSlow(band0Ms, bandCount))
                    {
                        if (pool.Handle != 0) { _vk.DestroyDescriptorPool(_device, pool, null); pool = default; }
                        throw new TimeoutException(
                            $"{MandelbrotKernelSource.PerturbTooSlowMarker}: band0={band0Ms:F1}ms × {bandCount} bands " +
                            $"> {MandelbrotKernelSource.PerturbBudgetMs:F0}ms budget (SA)");
                    }
                }
            }
        }
        finally
        {
            if (cmd.Handle != 0) _vk.FreeCommandBuffers(_device, _cmdPool, 1, in cmd);
            if (pool.Handle != 0) _vk.DestroyDescriptorPool(_device, pool, null);
        }
        SubmitReadback(Out(_buf[2], n * sizeof(uint)), Out(_buf[3], n * sizeof(float)), Out(_buf[4], n * 4 * sizeof(float)));

        long tDispatch = Stopwatch.GetTimestamp();
        ReadIter(_buf[2], iterDst, n);
        ReadFloats(_buf[3], smoothDst, n);
        ReadFinalZD(_buf[4], finalZrDst, finalZiDst, finalDrDst, finalDiDst, n);
        long tEnd = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
        LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
    }

    // #88 / G4.5b — the flattened BLA table (t10). Always bound; a 5-double
    // placeholder when the frame runs without BLA (gBlaLevels = 0).
    private Allocated _blaBuf;
    private int _blaAlloc;

    private void EnsureBlaBuffer(int doubles)
    {
        if (_blaBuf.Buffer.Handle != 0 && _blaAlloc >= doubles) return;
        FreeBuffer(ref _blaBuf);
        _blaBuf = AllocBuffer((ulong)(doubles * sizeof(double)), BufferUsageFlags.StorageBufferBit, Residency.Upload);
        _blaAlloc = doubles;
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
            _saAR = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit, Residency.Upload);
            _saAI = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit, Residency.Upload);
            _saBR = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit, Residency.Upload);
            _saBI = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit, Residency.Upload);
            _saCR = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit, Residency.Upload);
            _saCI = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit, Residency.Upload);
            _saDR = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit, Residency.Upload);
            _saDI = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit, Residency.Upload);
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
            _refZrBuf = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit, Residency.Upload);
            _refZiBuf = AllocBuffer(sz, BufferUsageFlags.StorageBufferBit, Residency.Upload);
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
        _buf[0] = AllocBuffer(80, BufferUsageFlags.UniformBufferBit);                       // params (#1173-I: 80 B)
        _buf[1] = AllocBuffer((ulong)(Math.Max(height, 1) * sizeof(uint)), BufferUsageFlags.StorageBufferBit); // perRow
        _buf[2] = AllocBuffer((ulong)(n * sizeof(uint)),  BufferUsageFlags.StorageBufferBit, Residency.Output);  // iter
        _buf[3] = AllocBuffer((ulong)(n * sizeof(float)), BufferUsageFlags.StorageBufferBit, Residency.Output);  // smooth
        _buf[4] = AllocBuffer((ulong)(n * 4 * sizeof(float)), BufferUsageFlags.StorageBufferBit, Residency.Output); // finalZD
        _buf[5] = AllocBuffer((ulong)(n * sizeof(uint)),  BufferUsageFlags.StorageBufferBit, Residency.Output);  // color
        _buf[6] = AllocBuffer((ulong)(n * sizeof(float)), BufferUsageFlags.StorageBufferBit, Residency.Output);  // trap (#1173-J)
        _allocW = width; _allocH = height;
    }

    /// <summary>#1228 — allocate a buffer for <paramref name="residency"/>. A staged
    /// buffer is device-local with a host-visible staging twin (host-cached for
    /// readback, #1173-L); it is direct-mapped instead when the host-visible type is
    /// already on a device-local heap (unified memory) or <see cref="Placement"/>
    /// keeps it on the host.</summary>
    private Allocated AllocBuffer(ulong size, BufferUsageFlags usage, Residency residency = Residency.Host)
    {
        bool readback = residency == Residency.Output;
        bool staged = Placement switch
        {
            VulkanBufferPlacement.HostOnly => false,
            VulkanBufferPlacement.DeviceOutputs => residency != Residency.Host,
            _ => residency == Residency.Upload,
        };
        if (staged)
        {
            Buffer dev = CreateBufferFor(size, usage | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit, out MemoryRequirements dreq);
            uint hostType = VulkanHostMemory.FindType(_vk, _ctx.PhysicalDevice, dreq.MemoryTypeBits, readback);
            uint? devType = VulkanHostMemory.FindDeviceLocalType(_vk, _ctx.PhysicalDevice, dreq.MemoryTypeBits);
            if (devType is uint dt && !VulkanHostMemory.IsOnDeviceLocalHeap(_vk, _ctx.PhysicalDevice, hostType))
            {
                var a = new Allocated { Buffer = dev, Size = size, DeviceLocal = true };
                (a.Memory, a.AllocSize) = AllocAndBind(dev, dreq, dt);
                System.Threading.Interlocked.Add(ref _residentDeviceLocalBytes, (long)a.AllocSize);
                a.Staging = CreateBufferFor(size, readback ? BufferUsageFlags.TransferDstBit : BufferUsageFlags.TransferSrcBit,
                    out MemoryRequirements sreq);
                (a.StagingMemory, a.StagingAllocSize) = AllocAndBind(a.Staging, sreq,
                    VulkanHostMemory.FindType(_vk, _ctx.PhysicalDevice, sreq.MemoryTypeBits, readback));
                return a;
            }
            _vk.DestroyBuffer(_device, dev, null);   // unified memory: direct map below
        }

        Buffer buffer = CreateBufferFor(size, usage, out MemoryRequirements req);
        uint memType = VulkanHostMemory.FindType(_vk, _ctx.PhysicalDevice, req.MemoryTypeBits, readback);
        var (mem, allocSize) = AllocAndBind(buffer, req, memType);
        bool local = VulkanHostMemory.IsOnDeviceLocalHeap(_vk, _ctx.PhysicalDevice, memType);
        if (local) System.Threading.Interlocked.Add(ref _residentDeviceLocalBytes, (long)allocSize);
        return new Allocated { Buffer = buffer, Memory = mem, Size = size, AllocSize = allocSize, DeviceLocal = local };
    }

    private Buffer CreateBufferFor(ulong size, BufferUsageFlags usage, out MemoryRequirements req)
    {
        var bci = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive,
        };
        Check(_vk.CreateBuffer(_device, in bci, null, out Buffer buffer), "vkCreateBuffer");
        _vk.GetBufferMemoryRequirements(_device, buffer, out req);
        return buffer;
    }

    // Allocate + bind; counts the allocation as resident (#1166).
    private (DeviceMemory, ulong) AllocAndBind(Buffer buffer, MemoryRequirements req, uint memType)
    {
        var mai = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = memType,
        };
        Check(_vk.AllocateMemory(_device, in mai, null, out DeviceMemory mem), "vkAllocateMemory");
        Check(_vk.BindBufferMemory(_device, buffer, mem, 0), "vkBindBufferMemory");
        System.Threading.Interlocked.Add(ref _residentBytes, (long)req.Size);
        return (mem, req.Size);
    }

    private void FreeBuffer(ref Allocated a)
    {
        if (a.Staged)   // #1228: drop queued copies / fills a failed dispatch left behind
        {
            Buffer buf = a.Buffer;
            _pendingCopies.RemoveAll(c => c.Dst.Handle == buf.Handle);
            _pendingFills.RemoveAll(f => f.Dst.Handle == buf.Handle);
        }
        if (a.Buffer.Handle != 0) _vk.DestroyBuffer(_device, a.Buffer, null);
        if (a.Memory.Handle != 0)
        {
            _vk.FreeMemory(_device, a.Memory, null);
            System.Threading.Interlocked.Add(ref _residentBytes, -(long)a.AllocSize);
            if (a.DeviceLocal) System.Threading.Interlocked.Add(ref _residentDeviceLocalBytes, -(long)a.AllocSize);
        }
        if (a.Staging.Handle != 0) _vk.DestroyBuffer(_device, a.Staging, null);
        if (a.StagingMemory.Handle != 0)
        {
            _vk.FreeMemory(_device, a.StagingMemory, null);
            System.Threading.Interlocked.Add(ref _residentBytes, -(long)a.StagingAllocSize);
        }
        a = default;
    }

    /// <summary>Write <paramref name="bytes"/> into the buffer: straight into its
    /// memory, or (#1228) into the staging twin with a copy queued for the next
    /// command buffer.</summary>
    private void WriteBytes(Allocated a, void* src, int bytes)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.HostMemory, 0, (ulong)bytes, 0, &mapped), "vkMapMemory");
        System.Buffer.MemoryCopy(src, mapped, bytes, bytes);
        _vk.UnmapMemory(_device, a.HostMemory);
        if (a.Staged && bytes > 0) _pendingCopies.Add((a.Staging, a.Buffer, (ulong)bytes));
    }

    private void ZeroBuffer(Allocated a)
    {
        if (a.Staged) { _pendingFills.Add((a.Buffer, a.Size)); return; }   // #1228: vkCmdFillBuffer
        void* mapped;
        Check(_vk.MapMemory(_device, a.Memory, 0, a.Size, 0, &mapped), "vkMapMemory");
        new Span<byte>(mapped, (int)a.Size).Clear();
        _vk.UnmapMemory(_device, a.Memory);
    }

    /// <summary>#1228 — record the queued staging copies and fills at the start of a
    /// command buffer, then make them visible to the compute shader.</summary>
    private void FlushPending(CommandBuffer cmd)
    {
        if (_pendingCopies.Count == 0 && _pendingFills.Count == 0) return;
        foreach (var (src, dst, bytes) in _pendingCopies)
        {
            var region = new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = bytes };
            _vk.CmdCopyBuffer(cmd, src, dst, 1, &region);
        }
        foreach (var (dst, bytes) in _pendingFills)
            _vk.CmdFillBuffer(cmd, dst, 0, Vk.WholeSize, 0);
        _pendingCopies.Clear();
        _pendingFills.Clear();
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.ComputeShaderBit,
            0, 1, &barrier, 0, null, 0, null);
    }

    // A readback request: the first Bytes of a buffer (default = none).
    private readonly record struct ReadbackSpan(Allocated Buf, ulong Bytes);
    private static ReadbackSpan Out(Allocated a, long bytes) => new(a, (ulong)bytes);

    /// <summary>#1228 — after the dispatch in <paramref name="cmd"/>: copy each staged
    /// output's first bytes to its host-cached staging twin and make them visible
    /// to the host. Direct-mapped outputs need nothing.</summary>
    private void RecordReadback(CommandBuffer cmd, params ReadOnlySpan<ReadbackSpan> outs)
    {
        if (!AnyStaged(outs)) return;
        var before = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.TransferReadBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.TransferBit,
            0, 1, &before, 0, null, 0, null);
        foreach (var o in outs)
        {
            if (o.Bytes == 0 || !o.Buf.Staged) continue;
            var region = new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = Math.Min(o.Bytes, o.Buf.Size) };
            _vk.CmdCopyBuffer(cmd, o.Buf.Buffer, o.Buf.Staging, 1, &region);
        }
        var after = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.HostReadBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit,
            0, 1, &after, 0, null, 0, null);
    }

    private static bool AnyStaged(ReadOnlySpan<ReadbackSpan> outs)
    {
        foreach (var o in outs) if (o.Bytes > 0 && o.Buf.Staged) return true;
        return false;
    }

    /// <summary>#1228 — <see cref="RecordReadback"/> in its own submit, for the
    /// banded paths whose dispatches span several command buffers.</summary>
    private void SubmitReadback(params ReadOnlySpan<ReadbackSpan> outs)
    {
        if (!AnyStaged(outs)) return;
        var cbai = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _cmdPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1,
        };
        Check(_vk.AllocateCommandBuffers(_device, in cbai, out CommandBuffer cmd), "vkAllocateCommandBuffers");
        try
        {
            var begin = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(_vk.BeginCommandBuffer(cmd, in begin), "vkBeginCommandBuffer");
            RecordReadback(cmd, outs);
            Check(_vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");
            var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
            Check(_vk.QueueSubmit(_ctx.ComputeQueue, 1, &submit, default), "vkQueueSubmit");
            Check(_vk.QueueWaitIdle(_ctx.ComputeQueue), "vkQueueWaitIdle");
        }
        finally { _vk.FreeCommandBuffers(_device, _cmdPool, 1, in cmd); }
    }

    private void ReadIter(Allocated a, int[] dst, int n)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.HostMemory, 0, (ulong)(n * sizeof(uint)), 0, &mapped), "vkMapMemory");
        var src = (uint*)mapped;
        for (int i = 0; i < n; i++) dst[i] = (int)src[i];
        _vk.UnmapMemory(_device, a.HostMemory);
    }

    private void ReadFloats(Allocated a, float[] dst, int n)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.HostMemory, 0, (ulong)(n * sizeof(float)), 0, &mapped), "vkMapMemory");
        new Span<float>(mapped, n).CopyTo(dst.AsSpan(0, n));
        _vk.UnmapMemory(_device, a.HostMemory);
    }

    private void ReadUints(Allocated a, uint[] dst, int n)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.HostMemory, 0, (ulong)(n * sizeof(uint)), 0, &mapped), "vkMapMemory");
        new Span<uint>(mapped, n).CopyTo(dst.AsSpan(0, n));
        _vk.UnmapMemory(_device, a.HostMemory);
    }

    private void ReadFinalZD(Allocated a, float[] zr, float[] zi, float[] dr, float[] di, int n)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.HostMemory, 0, (ulong)(n * 4 * sizeof(float)), 0, &mapped), "vkMapMemory");
        var src = (float*)mapped;
        for (int i = 0; i < n; i++)
        {
            int b = i * 4;
            zr[i] = src[b + 0]; zi[i] = src[b + 1]; dr[i] = src[b + 2]; di[i] = src[b + 3];
        }
        _vk.UnmapMemory(_device, a.HostMemory);
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
        foreach (var p in _baseByKind.Values) DestroyProgram(p);
        _baseByKind.Clear();
        if (_perturb != null) { DestroyProgram(_perturb); _perturb = null; }
        if (_perturbSa != null) { DestroyProgram(_perturbSa); _perturbSa = null; }
        foreach (var p in _perturbOrbit.Values) DestroyProgram(p);   // #607
        if (_buddha != null) { DestroyProgram(_buddha); _buddha = null; }   // #838
        _perturbOrbit.Clear();
        foreach (var p in _colorByIdKind.Values) DestroyProgram(p);
        _colorByIdKind.Clear();
        _colorBuilders.Clear();
        for (int i = 0; i < _buf.Length; i++) FreeBuffer(ref _buf[i]);
        FreeBuffer(ref _perturbParams);
        FreeBuffer(ref _refZrBuf);
        FreeBuffer(ref _refZiBuf);
        FreeBuffer(ref _saParams);
        FreeBuffer(ref _saAR); FreeBuffer(ref _saAI);
        FreeBuffer(ref _saBR); FreeBuffer(ref _saBI);
        FreeBuffer(ref _saCR); FreeBuffer(ref _saCI);
        FreeBuffer(ref _saDR); FreeBuffer(ref _saDI);
        FreeBuffer(ref _blaBuf);   // #88 / G4.5b
        FreeBuffer(ref _orbitOut); // #607
        FreeBuffer(ref _buddhaParams); FreeBuffer(ref _buddhaHits);   // #838
        if (_cmdPool.Handle != 0) { _vk.DestroyCommandPool(_device, _cmdPool, null); _cmdPool = default; }
        if (_ownsContext) { try { _ctx.Dispose(); } catch { } }
    }
}
