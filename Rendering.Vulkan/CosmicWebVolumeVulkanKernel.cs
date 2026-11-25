// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.Vulkan/CosmicWebVolumeVulkanKernel.cs (#2024, Cosmic Web epic #2020)
//
// Vulkan compute backend of the Cosmic Web volume raymarch: the same HLSL as the D3D11 kernel
// (CosmicWebVolumeKernelSource, linked from Rendering.D3D) compiled by DXC to SPIR-V (cs_6_0), on a
// context this kernel owns. The mapped grid is uploaded once per grid instance and kept.
//
// Bindings (DXC shifts as the other Vulkan kernels): b0 -> 0 (integer UBO), t0..t2 -> 100..102
// (parameters, theme LUT, grid), u0..u1 -> 200..201 (colour, depth). Bands of pixels, one submit and
// wait per band, steered toward ~100 ms (watchdog safety, as the D3D11 twin and #1972).

using System;
using System.Runtime.InteropServices;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using FracturingFog.CosmicWeb;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace FracturingFog.Rendering.Vulkan;

public sealed unsafe class CosmicWebVolumeVulkanKernel : ICosmicWebVolumeKernel
{
    private const int TShift = 100, UShift = 200;
    private const int Bindings = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct Ints { public uint Width, Height, Grid, Steps, DepthColour, Background, BandStart, BandEnd; }

    private struct Allocated { public Buffer Buffer; public DeviceMemory Memory; public ulong Size; }

    private readonly VulkanContext _ctx;
    private readonly bool _ownsContext;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly object _gate = new();
    private CommandPool _cmdPool;
    private DescriptorSetLayout _dsl;
    private PipelineLayout _layout;
    private ShaderModule _mod;
    private Pipeline _pipe;
    private Allocated _ints, _grid;
    private WeakReference<CosmicWebDensityGrid>? _gridKey;
    private int _bandPixels = 4096;
    private bool _disposed;

    public string Backend => "Vulkan";
    public string DeviceName => _ctx.PickedName;
    public string LastError { get; private set; } = "";
    public const double BandTargetMs = 100;

    private static readonly uint[] s_binds = { 0, TShift, TShift + 1, TShift + 2, UShift, UShift + 1 };

    public CosmicWebVolumeVulkanKernel(VulkanContext ctx, bool ownsContext = false)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _ownsContext = ownsContext;
        _vk = ctx.Vk;
        _device = ctx.Device;
        var cpci = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = ctx.ComputeQueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(_vk.CreateCommandPool(_device, in cpci, null, out _cmdPool), "vkCreateCommandPool");
        var bindings = stackalloc DescriptorSetLayoutBinding[Bindings];
        for (int i = 0; i < Bindings; i++)
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = s_binds[i], DescriptorType = i == 0 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit,
            };
        var dslci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = Bindings, PBindings = bindings };
        Check(_vk.CreateDescriptorSetLayout(_device, in dslci, null, out _dsl), "vkCreateDescriptorSetLayout");
        var dsl = _dsl;
        var plci = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &dsl };
        Check(_vk.CreatePipelineLayout(_device, in plci, null, out _layout), "vkCreatePipelineLayout");
        _ints = Alloc((ulong)sizeof(Ints), BufferUsageFlags.UniformBufferBit);
    }

    /// <summary>A kernel on its own Vulkan context, or null when there is no Vulkan device. Never throws.</summary>
    public static CosmicWebVolumeVulkanKernel? TryCreateWithOwnContext()
    {
        VulkanContext? ctx = null;
        try
        {
            ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) { ctx.Dispose(); return null; }
            ctx.CreateComputeDevice();
            return new CosmicWebVolumeVulkanKernel(ctx, ownsContext: true);
        }
        catch { ctx?.Dispose(); return null; }
    }

    private Pipeline PipelineOnce()
    {
        if (_pipe.Handle != 0) return _pipe;
        byte[] spirv = DxcCompiler.CompileToSpirv(CosmicWebVolumeKernelSource.Source, CosmicWebVolumeKernelSource.Entry, "cs_6_0",
            "-fvk-b-shift", "0", "0", "-fvk-t-shift", TShift.ToString(), "0", "-fvk-u-shift", UShift.ToString(), "0");
        fixed (byte* code = spirv)
        {
            var smci = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Check(_vk.CreateShaderModule(_device, in smci, null, out _mod), "vkCreateShaderModule");
        }
        nint entry = SilkMarshal.StringToPtr(CosmicWebVolumeKernelSource.Entry);
        try
        {
            var cpci = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = _mod, PName = (byte*)entry,
                },
                Layout = _layout,
            };
            Pipeline pipe;
            Check(_vk.CreateComputePipelines(_device, default, 1, &cpci, null, &pipe), "vkCreateComputePipelines");
            _pipe = pipe;
            return pipe;
        }
        finally { SilkMarshal.Free(entry); }
    }

    private Allocated GridBuffer(CosmicWebDensityGrid grid)
    {
        if (_grid.Buffer.Handle != 0 && _gridKey != null && _gridKey.TryGetTarget(out var held) && ReferenceEquals(held, grid)) return _grid;
        Free(ref _grid);
        _gridKey = null;
        _grid = Alloc((ulong)grid.Value.Length * sizeof(float), BufferUsageFlags.StorageBufferBit);
        fixed (float* src = grid.Value) Write(_grid, src, grid.Value.Length * sizeof(float));
        _gridKey = new WeakReference<CosmicWebDensityGrid>(grid);
        return _grid;
    }

    public CosmicWebVolumeFrame? Render(in CosmicWebVolumeParams p, CosmicWebDensityGrid grid, uint[] lut, CancellationToken ct = default)
    {
        int total = p.Width * p.Height;
        if (total <= 0) return null;
        var pp = p;
        lock (_gate)
        {
            Allocated par = default, lutBuf = default, col = default, dep = default;
            DescriptorPool pool = default;
            try
            {
                var pipe = PipelineOnce();
                var g = GridBuffer(grid);
                var f = pp.PackFloats();
                par = Alloc((ulong)(f.Length * sizeof(float)), BufferUsageFlags.StorageBufferBit);
                fixed (float* src = f) Write(par, src, f.Length * sizeof(float));
                lutBuf = Alloc((ulong)(lut.Length * sizeof(uint)), BufferUsageFlags.StorageBufferBit);
                fixed (uint* src = lut) Write(lutBuf, src, lut.Length * sizeof(uint));
                col = Alloc((ulong)total * sizeof(uint), BufferUsageFlags.StorageBufferBit, readback: true);
                dep = Alloc((ulong)total * sizeof(uint), BufferUsageFlags.StorageBufferBit, readback: true);

                var poolSizes = stackalloc DescriptorPoolSize[2]
                {
                    new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
                    new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = Bindings - 1 },
                };
                var dpci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 2, PPoolSizes = poolSizes };
                Check(_vk.CreateDescriptorPool(_device, in dpci, null, out pool), "vkCreateDescriptorPool");
                var dsl = _dsl;
                var dsai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &dsl };
                DescriptorSet set;
                Check(_vk.AllocateDescriptorSets(_device, in dsai, &set), "vkAllocateDescriptorSets");
                Buffer* bufs = stackalloc Buffer[Bindings] { _ints.Buffer, par.Buffer, lutBuf.Buffer, g.Buffer, col.Buffer, dep.Buffer };
                var infos = stackalloc DescriptorBufferInfo[Bindings];
                var writes = stackalloc WriteDescriptorSet[Bindings];
                for (int i = 0; i < Bindings; i++)
                {
                    infos[i] = new DescriptorBufferInfo { Buffer = bufs[i], Offset = 0, Range = Vk.WholeSize };
                    writes[i] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = s_binds[i], DescriptorCount = 1,
                        DescriptorType = i == 0 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer, PBufferInfo = &infos[i],
                    };
                }
                _vk.UpdateDescriptorSets(_device, Bindings, writes, 0, null);

                int start = 0;
                while (start < total)
                {
                    if (ct.IsCancellationRequested) return null;
                    int end = Math.Min(total, start + _bandPixels);
                    var ints = new Ints
                    {
                        Width = (uint)pp.Width, Height = (uint)pp.Height, Grid = (uint)pp.Grid, Steps = (uint)pp.Steps,
                        DepthColour = pp.DepthColour ? 1u : 0u, Background = pp.Background, BandStart = (uint)start, BandEnd = (uint)end,
                    };
                    Write(_ints, &ints, sizeof(Ints));
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Submit(pipe, set, (uint)((end - start + CosmicWebVolumeKernelSource.GroupSize - 1) / CosmicWebVolumeKernelSource.GroupSize));
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    double scale = ms > 0.01 ? Math.Clamp(BandTargetMs / ms, 0.25, 4.0) : 4.0;
                    _bandPixels = (int)Math.Clamp(_bandPixels * scale, 256, CosmicWebVolumeKernelSource.MaxBandPixels);   // #2045
                    start = end;
                }

                var colour = new uint[total];
                var depth = new float[total];
                Read(col, MemoryMarshal.AsBytes(colour.AsSpan()));
                Read(dep, MemoryMarshal.AsBytes(depth.AsSpan()));
                LastError = "";
                return new CosmicWebVolumeFrame(colour, depth);
            }
            catch (Exception ex)
            {
                LastError = $"CosmicWeb Vulkan volume failed: {ex.Message}";
                return null;
            }
            finally
            {
                if (pool.Handle != 0) _vk.DestroyDescriptorPool(_device, pool, null);
                Free(ref par); Free(ref lutBuf); Free(ref col); Free(ref dep);
            }
        }
    }

    private void Submit(Pipeline pipe, DescriptorSet set, uint groups)
    {
        var cbai = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo, CommandPool = _cmdPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1,
        };
        Check(_vk.AllocateCommandBuffers(_device, in cbai, out CommandBuffer cmd), "vkAllocateCommandBuffers");
        try
        {
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Check(_vk.BeginCommandBuffer(cmd, in begin), "vkBeginCommandBuffer");
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipe);
            var s = set;
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 0, 1, &s, 0, null);
            _vk.CmdDispatch(cmd, groups, 1, 1);
            var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.HostReadBit };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
            Check(_vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");
            var c = cmd;
            var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &c };
            Check(_vk.QueueSubmit(_ctx.ComputeQueue, 1, &submit, default), "vkQueueSubmit");
            Check(_vk.QueueWaitIdle(_ctx.ComputeQueue), "vkQueueWaitIdle");
        }
        finally { _vk.FreeCommandBuffers(_device, _cmdPool, 1, in cmd); }
    }

    private Allocated Alloc(ulong size, BufferUsageFlags usage, bool readback = false)
    {
        var bci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Check(_vk.CreateBuffer(_device, in bci, null, out Buffer buffer), "vkCreateBuffer");
        _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements req);
        uint memType = VulkanHostMemory.FindType(_vk, _ctx.PhysicalDevice, req.MemoryTypeBits, readback);
        var mai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = memType };
        Check(_vk.AllocateMemory(_device, in mai, null, out DeviceMemory mem), "vkAllocateMemory");
        Check(_vk.BindBufferMemory(_device, buffer, mem, 0), "vkBindBufferMemory");
        return new Allocated { Buffer = buffer, Memory = mem, Size = size };
    }

    private void Free(ref Allocated a)
    {
        if (a.Buffer.Handle != 0) _vk.DestroyBuffer(_device, a.Buffer, null);
        if (a.Memory.Handle != 0) _vk.FreeMemory(_device, a.Memory, null);
        a = default;
    }

    private void Write(Allocated a, void* src, int bytes)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.Memory, 0, (ulong)bytes, 0, &mapped), "vkMapMemory");
        System.Buffer.MemoryCopy(src, mapped, bytes, bytes);
        _vk.UnmapMemory(_device, a.Memory);
    }

    private void Read(Allocated a, Span<byte> dst)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.Memory, 0, (ulong)dst.Length, 0, &mapped), "vkMapMemory");
        new ReadOnlySpan<byte>(mapped, dst.Length).CopyTo(dst);
        _vk.UnmapMemory(_device, a.Memory);
    }

    private static void Check(Result r, string what)
    {
        if (r != Result.Success) throw new InvalidOperationException($"{what} failed: {r}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _vk.DeviceWaitIdle(_device); } catch { }
            if (_pipe.Handle != 0) _vk.DestroyPipeline(_device, _pipe, null);
            if (_mod.Handle != 0) _vk.DestroyShaderModule(_device, _mod, null);
            if (_layout.Handle != 0) _vk.DestroyPipelineLayout(_device, _layout, null);
            if (_dsl.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _dsl, null);
            Free(ref _ints); Free(ref _grid);
            if (_cmdPool.Handle != 0) _vk.DestroyCommandPool(_device, _cmdPool, null);
            if (_ownsContext) { try { _ctx.Dispose(); } catch { } }
        }
    }
}
