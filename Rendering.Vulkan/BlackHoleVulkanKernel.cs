// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.Vulkan/BlackHoleVulkanKernel.cs (#1972, black hole shadows epic #1922)
//
// Vulkan compute backend of the black hole trace: the same HLSL as the D3D11 kernel
// (BlackHoleKernelSource, linked from Rendering.D3D) compiled by DXC to SPIR-V (cs_6_0), on a
// context this kernel owns. The cross-platform GPU path (Linux, #1455). The double kernel needs
// the device's shaderFloat64 (VulkanContext.SupportsFloat64); without it only the single kernel
// runs.
//
// Bindings (DXC shifts as the other Vulkan kernels): b0 → 0 (the integer UBO), t0 → 100
// (parameters), u0 → 200 (packed fate + steps), u1 → 201 (per-pixel data). The frame is traced
// in bands of pixels, one submit and wait per band, the band size steered toward ~100 ms (TDR /
// watchdog safety, as the D3D11 twin).

using System;
using System.Runtime.InteropServices;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace FracturingFog.Rendering.Vulkan;

public sealed unsafe class BlackHoleVulkanKernel : IBlackHoleTraceKernel
{
    private const int TShift = 100, UShift = 200;

    [StructLayout(LayoutKind.Sequential)]
    private struct Ints { public uint Width, Height, HoleCount, Pinhole, MaxSteps, BandStart, BandEnd, Pad0; }

    private struct Allocated { public Buffer Buffer; public DeviceMemory Memory; public ulong Size; }

    private readonly VulkanContext _ctx;
    private readonly bool _ownsContext;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly object _gate = new();
    private CommandPool _cmdPool;
    private DescriptorSetLayout _dsl;
    private PipelineLayout _layout;
    private ShaderModule _modD, _modF;
    private Pipeline _pipeD, _pipeF;
    private Allocated _ints;
    private int _bandPixels = 4096;
    private bool _disposed;

    public string Backend => "Vulkan";
    public string DeviceName => _ctx.PickedName;
    public string LastError { get; private set; } = "";
    public const double BandTargetMs = 100;

    public BlackHoleVulkanKernel(VulkanContext ctx, bool ownsContext = false)
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
        uint* binds = stackalloc uint[4] { 0, TShift, UShift, UShift + 1 };
        var bindings = stackalloc DescriptorSetLayoutBinding[4];
        for (int i = 0; i < 4; i++)
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = binds[i], DescriptorType = i == 0 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit,
            };
        var dslci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 4, PBindings = bindings };
        Check(_vk.CreateDescriptorSetLayout(_device, in dslci, null, out _dsl), "vkCreateDescriptorSetLayout");
        var dsl = _dsl;
        var plci = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &dsl };
        Check(_vk.CreatePipelineLayout(_device, in plci, null, out _layout), "vkCreatePipelineLayout");
        _ints = Alloc((ulong)sizeof(Ints), BufferUsageFlags.UniformBufferBit);
    }

    /// <summary>A kernel on its own Vulkan context, or null when there is no Vulkan device. Never throws.</summary>
    public static BlackHoleVulkanKernel? TryCreateWithOwnContext()
    {
        VulkanContext? ctx = null;
        try
        {
            ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) { ctx.Dispose(); return null; }
            ctx.CreateComputeDevice();
            return new BlackHoleVulkanKernel(ctx, ownsContext: true);
        }
        catch { ctx?.Dispose(); return null; }
    }

    private Pipeline PipelineFor(bool dbl)
    {
        if (dbl && _pipeD.Handle != 0) return _pipeD;
        if (!dbl && _pipeF.Handle != 0) return _pipeF;
        byte[] spirv = DxcCompiler.CompileToSpirv(BlackHoleKernelSource.Build(dbl), BlackHoleKernelSource.Entry, "cs_6_0",
            "-fvk-b-shift", "0", "0", "-fvk-t-shift", TShift.ToString(), "0", "-fvk-u-shift", UShift.ToString(), "0");
        ShaderModule mod;
        fixed (byte* code = spirv)
        {
            var smci = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Check(_vk.CreateShaderModule(_device, in smci, null, out mod), "vkCreateShaderModule");
        }
        nint entry = SilkMarshal.StringToPtr(BlackHoleKernelSource.Entry);
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
            if (dbl) { _modD = mod; _pipeD = pipe; } else { _modF = mod; _pipeF = pipe; }
            return pipe;
        }
        finally { SilkMarshal.Free(entry); }
    }

    public BlackHoleShadowGpuCalculator.Outcomes? Trace(in BlackHoleGpuParams p, bool single, CancellationToken ct = default)
    {
        if (!single && !_ctx.SupportsFloat64) { LastError = $"{DeviceName}: no Vulkan shaderFloat64 (use single precision)"; return null; }
        int total = p.Width * p.Height;
        if (total <= 0) return null;
        var pp = p;
        lock (_gate)
        {
            Allocated par = default, packed = default, data = default;
            DescriptorPool pool = default;
            try
            {
                var pipe = PipelineFor(!single);
                int perPixel = single ? 4 : 8;
                if (single)
                {
                    var f = BlackHoleTraceBackends.PackSingle(pp);
                    par = Alloc((ulong)(f.Length * sizeof(float)), BufferUsageFlags.StorageBufferBit);
                    fixed (float* src = f) Write(par, src, f.Length * sizeof(float));
                }
                else
                {
                    var d = BlackHoleTraceBackends.Pack(pp);
                    par = Alloc((ulong)(d.Length * sizeof(double)), BufferUsageFlags.StorageBufferBit);
                    fixed (double* src = d) Write(par, src, d.Length * sizeof(double));
                }
                packed = Alloc((ulong)total * sizeof(uint), BufferUsageFlags.StorageBufferBit, readback: true);
                data = Alloc((ulong)total * (ulong)perPixel * sizeof(uint), BufferUsageFlags.StorageBufferBit, readback: true);

                var poolSizes = stackalloc DescriptorPoolSize[2]
                {
                    new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
                    new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 3 },
                };
                var dpci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 2, PPoolSizes = poolSizes };
                Check(_vk.CreateDescriptorPool(_device, in dpci, null, out pool), "vkCreateDescriptorPool");
                var dsl = _dsl;
                var dsai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &dsl };
                DescriptorSet set;
                Check(_vk.AllocateDescriptorSets(_device, in dsai, &set), "vkAllocateDescriptorSets");
                Buffer* bufs = stackalloc Buffer[4] { _ints.Buffer, par.Buffer, packed.Buffer, data.Buffer };
                uint* binds = stackalloc uint[4] { 0, TShift, UShift, UShift + 1 };
                var infos = stackalloc DescriptorBufferInfo[4];
                var writes = stackalloc WriteDescriptorSet[4];
                for (int i = 0; i < 4; i++)
                {
                    infos[i] = new DescriptorBufferInfo { Buffer = bufs[i], Offset = 0, Range = Vk.WholeSize };
                    writes[i] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = binds[i], DescriptorCount = 1,
                        DescriptorType = i == 0 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer, PBufferInfo = &infos[i],
                    };
                }
                _vk.UpdateDescriptorSets(_device, 4, writes, 0, null);

                int start = 0;
                while (start < total)
                {
                    if (ct.IsCancellationRequested) return null;
                    int end = Math.Min(total, start + _bandPixels);
                    var ints = new Ints
                    {
                        Width = (uint)pp.Width, Height = (uint)pp.Height, HoleCount = (uint)Math.Clamp(pp.HoleCount, 1, 4),
                        Pinhole = (uint)pp.Pinhole, MaxSteps = (uint)Math.Max(1, pp.MaxSteps), BandStart = (uint)start, BandEnd = (uint)end,
                    };
                    Write(_ints, &ints, sizeof(Ints));
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Submit(pipe, set, (uint)((end - start + BlackHoleKernelSource.GroupSize - 1) / BlackHoleKernelSource.GroupSize));
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    double scale = ms > 0.01 ? Math.Clamp(BandTargetMs / ms, 0.25, 4.0) : 4.0;
                    _bandPixels = (int)Math.Clamp(_bandPixels * scale, 256, 1 << 22);
                    start = end;
                }

                var outPacked = new int[total];
                var outData = new double[total * BlackHoleShadowGpuCalculator.DataStride];
                Read(packed, MemoryMarshal.AsBytes(outPacked.AsSpan()));
                if (single)
                {
                    var f = new float[total * 4];
                    Read(data, MemoryMarshal.AsBytes(f.AsSpan()));
                    for (int i = 0; i < f.Length; i++) outData[i] = f[i];
                }
                else Read(data, MemoryMarshal.AsBytes(outData.AsSpan()));
                LastError = "";
                return new BlackHoleShadowGpuCalculator.Outcomes(outPacked, outData);
            }
            catch (Exception ex)
            {
                LastError = $"BlackHoleShadow Vulkan trace failed: {ex.Message}";
                return null;
            }
            finally
            {
                if (pool.Handle != 0) _vk.DestroyDescriptorPool(_device, pool, null);
                Free(ref par); Free(ref packed); Free(ref data);
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
            // Make the shader writes visible to the host reads after the last band.
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
            if (_pipeD.Handle != 0) _vk.DestroyPipeline(_device, _pipeD, null);
            if (_pipeF.Handle != 0) _vk.DestroyPipeline(_device, _pipeF, null);
            if (_modD.Handle != 0) _vk.DestroyShaderModule(_device, _modD, null);
            if (_modF.Handle != 0) _vk.DestroyShaderModule(_device, _modF, null);
            if (_layout.Handle != 0) _vk.DestroyPipelineLayout(_device, _layout, null);
            if (_dsl.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _dsl, null);
            Free(ref _ints);
            if (_cmdPool.Handle != 0) _vk.DestroyCommandPool(_device, _cmdPool, null);
            if (_ownsContext) { try { _ctx.Dispose(); } catch { } }
        }
    }
}
