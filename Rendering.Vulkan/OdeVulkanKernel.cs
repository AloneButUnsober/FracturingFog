// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.Vulkan/OdeVulkanKernel.cs (#2049, physics epic #2048)
//
// Vulkan compute backend of the ODE template: the same HLSL as the D3D11 kernel
// (OdeKernelSource + a family's equations, linked from Rendering.D3D) compiled by DXC to
// SPIR-V (cs_6_0), on a context this kernel owns. The double kernel needs the device's
// shaderFloat64 (VulkanContext.SupportsFloat64).
//
// Bindings (DXC shifts as the other Vulkan kernels): b0 -> 0 (integer UBO), t0 -> 100 (launch
// states), t1 -> 101 (parameters), u0 -> 200 (outcomes). Chunks of OdeTraceBackends.ChunkItems
// items; in each chunk, bands with one submit and wait per band, steered toward ~100 ms
// (watchdog safety, as #1972) and capped at 65,535 groups (#2045).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace FracturingFog.Rendering.Vulkan;

public sealed unsafe class OdeVulkanKernel : IOdeTraceKernel
{
    private const int TShift = 100, UShift = 200;
    private const int Bindings = 4;
    private static readonly uint[] s_binds = { 0, TShift, TShift + 1, UShift };

    [StructLayout(LayoutKind.Sequential)]
    private struct Ints { public uint Count, BandStart, BandEnd, MaxSteps, Stride, Pad0, Pad1, Pad2; }

    private struct Allocated { public Buffer Buffer; public DeviceMemory Memory; public ulong Size; }

    private readonly VulkanContext _ctx;
    private readonly bool _ownsContext;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly object _gate = new();
    private CommandPool _cmdPool;
    private DescriptorSetLayout _dsl;
    private PipelineLayout _layout;
    private readonly Dictionary<(string, bool), (ShaderModule Mod, Pipeline Pipe)> _pipes = new();
    private Allocated _ints;
    private int _bandItems = 4096;
    private bool _disposed;

    public string Backend => "Vulkan";
    public string DeviceName => _ctx.PickedName;
    public string LastError { get; private set; } = "";
    public bool SupportsDouble => _ctx.SupportsFloat64;
    public const double BandTargetMs = 100;

    public OdeVulkanKernel(VulkanContext ctx, bool ownsContext = false)
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
    public static OdeVulkanKernel? TryCreateWithOwnContext()
    {
        VulkanContext? ctx = null;
        try
        {
            ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) { ctx.Dispose(); return null; }
            ctx.CreateComputeDevice();
            return new OdeVulkanKernel(ctx, ownsContext: true);
        }
        catch { ctx?.Dispose(); return null; }
    }

    private Pipeline PipelineFor(OdeFamily fam, bool dbl)
    {
        if (_pipes.TryGetValue((fam.Key, dbl), out var have)) return have.Pipe;
        byte[] spirv = DxcCompiler.CompileToSpirv(OdeKernelSource.Build(fam.Hlsl, fam.Dimension, fam.Channels, dbl), OdeKernelSource.Entry, "cs_6_0",
            "-fvk-b-shift", "0", "0", "-fvk-t-shift", TShift.ToString(), "0", "-fvk-u-shift", UShift.ToString(), "0");
        ShaderModule mod;
        fixed (byte* code = spirv)
        {
            var smci = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Check(_vk.CreateShaderModule(_device, in smci, null, out mod), "vkCreateShaderModule");
        }
        nint entry = SilkMarshal.StringToPtr(OdeKernelSource.Entry);
        try
        {
            var cpci = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = mod, PName = (byte*)entry,
                },
                Layout = _layout,
            };
            Pipeline pipe;
            Check(_vk.CreateComputePipelines(_device, default, 1, &cpci, null, &pipe), "vkCreateComputePipelines");
            _pipes[(fam.Key, dbl)] = (mod, pipe);
            return pipe;
        }
        finally { SilkMarshal.Free(entry); }
    }

    public OdeResult? Trace(OdeJob job, CancellationToken ct = default)
    {
        bool dbl = !job.Single;
        if (dbl && !_ctx.SupportsFloat64) { LastError = $"{DeviceName}: no Vulkan shaderFloat64 (use single precision)"; return null; }
        var fam = job.Family;
        int dim = fam.Dimension, stride = OdeKernelSource.Stride(dim, fam.Channels, dbl);
        var result = new OdeResult(job.Count, dim, fam.Channels);
        if (job.Count == 0) return result;
        lock (_gate)
        {
            Allocated par = default, lbuf = default, obuf = default;
            DescriptorPool pool = default;
            try
            {
                var pipe = PipelineFor(fam, dbl);
                var p = OdeTraceBackends.Params(job);
                int es = dbl ? sizeof(double) : sizeof(float);
                par = Alloc((ulong)(p.Length * es), BufferUsageFlags.StorageBufferBit);
                WriteReals(par, p, dbl);
                int chunk = Math.Min(OdeTraceBackends.ChunkItems, job.Count);
                lbuf = Alloc((ulong)(chunk * dim * es), BufferUsageFlags.StorageBufferBit);
                obuf = Alloc((ulong)chunk * (ulong)stride * sizeof(uint), BufferUsageFlags.StorageBufferBit, readback: true);

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
                Buffer* bufs = stackalloc Buffer[Bindings] { _ints.Buffer, lbuf.Buffer, par.Buffer, obuf.Buffer };
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

                var launch = new double[chunk * dim];
                var words = new uint[chunk * stride];
                for (int first = 0; first < job.Count; first += chunk)
                {
                    if (ct.IsCancellationRequested) return null;
                    int n = Math.Min(chunk, job.Count - first);
                    job.Launches(first, n, launch);
                    WriteReals(lbuf, launch.AsSpan(0, n * dim), dbl);
                    int start = 0;
                    while (start < n)
                    {
                        if (ct.IsCancellationRequested) return null;
                        int end = Math.Min(n, start + _bandItems);
                        var ints = new Ints { Count = (uint)n, BandStart = (uint)start, BandEnd = (uint)end, MaxSteps = (uint)Math.Max(1, job.MaxSteps), Stride = (uint)stride };
                        Write(_ints, &ints, sizeof(Ints));
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        Submit(pipe, set, (uint)((end - start + OdeKernelSource.GroupSize - 1) / OdeKernelSource.GroupSize));
                        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        double scale = ms > 0.01 ? Math.Clamp(BandTargetMs / ms, 0.25, 4.0) : 4.0;
                        _bandItems = (int)Math.Clamp(_bandItems * scale, 256, OdeKernelSource.MaxBandPixels);
                        start = end;
                    }
                    Read(obuf, MemoryMarshal.AsBytes(words.AsSpan(0, n * stride)));
                    OdeTraceBackends.Decode(words, first, n, fam, !dbl, result);
                }
                LastError = "";
                return result;
            }
            catch (Exception ex)
            {
                LastError = $"ODE Vulkan trace failed: {ex.Message}";
                return null;
            }
            finally
            {
                if (pool.Handle != 0) _vk.DestroyDescriptorPool(_device, pool, null);
                Free(ref par); Free(ref lbuf); Free(ref obuf);
            }
        }
    }

    private void WriteReals(Allocated a, ReadOnlySpan<double> v, bool dbl)
    {
        if (dbl) { fixed (double* src = v) Write(a, src, v.Length * sizeof(double)); return; }
        var f = new float[v.Length];
        for (int i = 0; i < f.Length; i++) f[i] = (float)v[i];
        fixed (float* src = f) Write(a, src, f.Length * sizeof(float));
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
        var bci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = Math.Max(4UL, size), Usage = usage, SharingMode = SharingMode.Exclusive };
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
        if (bytes <= 0) return;
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
            foreach (var (mod, pipe) in _pipes.Values)
            {
                if (pipe.Handle != 0) _vk.DestroyPipeline(_device, pipe, null);
                if (mod.Handle != 0) _vk.DestroyShaderModule(_device, mod, null);
            }
            _pipes.Clear();
            if (_layout.Handle != 0) _vk.DestroyPipelineLayout(_device, _layout, null);
            if (_dsl.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _dsl, null);
            Free(ref _ints);
            if (_cmdPool.Handle != 0) _vk.DestroyCommandPool(_device, _cmdPool, null);
            if (_ownsContext) { try { _ctx.Dispose(); } catch { } }
        }
    }
}
