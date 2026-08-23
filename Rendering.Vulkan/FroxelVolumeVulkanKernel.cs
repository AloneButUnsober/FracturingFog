// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FroxelVolumeVulkanKernel.cs — GPU froxel volume compute pass (roadmap S6,
// #389 / #408), Vulkan twin of the Windows-only D3D FroxelGpuKernel.
//
// Both compile the SAME dependency-free HLSL (FroxelKernelSource): FXC → cs_5_0
// on D3D, DXC → cs_6_0 -spirv here. The HLSL carries no [[vk::binding]]
// attributes (they break FXC); bindings are pinned with the DXC -fvk-*-shift
// maps used by the rest of the Vulkan backend (b0→0, t0..t2→100..102, u0→200).
//
// Two passes in one command buffer:
//   * CSFroxelIntegrate — one thread per froxel COLUMN, populates + integrates
//     the volume into a float4/cell storage buffer (u0 → binding 200).
//   * CSFroxelComposite — one thread per PIXEL, composites the volume over the
//     fog-free beauty by per-pixel world depth (t0=beauty 100, t1=depth 101,
//     t2=volume 102, u0=output 200).
// A shader-write→shader-read memory barrier between the two dispatches makes the
// composite see the integrate's volume writes.
//
// The GPU twin of FroxelCameraVolume.Apply (FroxelVolumePass.Populate +
// CompositeWorldDepth); the D3D --froxelgpu gate proves the shared HLSL against
// that CPU pass. Memory is HOST_VISIBLE|HOST_COHERENT (direct map, no staging),
// matching VulkanComputeKernel / ReliefRaymarchVulkanKernel. Thread-affine: a
// single caller drives Composite. Not internally synchronised.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

using FracturingFog.Rendering.Lighting;   // FroxelGpuUniforms / FroxelLight / FroxelMedium

namespace FracturingFog.Rendering.Vulkan;

/// <summary>Vulkan compute dispatch of the froxel volume pass (#408). See the file
/// header for the two-pass structure, buffer bindings and the parity contract.</summary>
public sealed unsafe class FroxelVolumeVulkanKernel : IDisposable, IFroxelVolumeKernel
{
    // DXC register-class → binding shifts (same maps as VulkanComputeKernel).
    private const int BShift = 0;
    private const int TShift = 100;
    private const int UShift = 200;

    // FroxelParams UBO twin — 14 float4 rows (224 B). Field order MUST track
    // FroxelKernelSource.Hlsl's cbuffer and the D3D FroxelGpuKernel.FroxelParamsBlob.
    [StructLayout(LayoutKind.Sequential)]
    private struct FroxelParamsBlob
    {
        public int Nx, Ny, Nz, W;
        public int H; public float Near, Far, Extent;
        public float BaseDensity, Extinction, Anisotropy, NoiseAmount;
        public float NoiseScale; public int NoiseOctaves; public float ViewX, ViewY;
        public float ViewZ; public int NumLights; public float Pad0, Pad1;
        public int Type0; public uint Color0; public float I0, Range0;
        public float Dir0x, Dir0y, Dir0z, Inner0;
        public float Pos0x, Pos0y, Pos0z, Outer0;
        public int Type1; public uint Color1; public float I1, Range1;
        public float Dir1x, Dir1y, Dir1z, Inner1;
        public float Pos1x, Pos1y, Pos1z, Outer1;
        public int Type2; public uint Color2; public float I2, Range2;
        public float Dir2x, Dir2y, Dir2z, Inner2;
        public float Pos2x, Pos2y, Pos2z, Outer2;
    }

    private const int ParamBytes = 224;

    private struct Allocated { public Buffer Buffer; public DeviceMemory Memory; public ulong Size; }

    private readonly VulkanContext _ctx;
    private readonly bool _ownsContext;
    private readonly Vk _vk;
    private readonly Device _device;
    private CommandPool _cmdPool;

    private ShaderModule _integrateModule, _compositeModule;
    private Pipeline _integratePipeline, _compositePipeline;
    private DescriptorSetLayout _dsl;
    private PipelineLayout _layout;

    private Allocated _params;
    private Allocated _volume;               // float4/cell (u0 integrate / t2 composite)
    private Allocated _beauty, _depth, _out;  // output-sized (n)
    private int _volumeCells, _outPixels;
    private bool _disposed;

    public double LastDispatchMs { get; private set; }
    public double LastReadbackMs { get; private set; }

    /// <summary>Human-readable backend label.</summary>
    public string Description => $"Vulkan froxel ({_ctx.PickedType}: {_ctx.PickedName})";

    /// <param name="ctx">A context whose logical device is already created.</param>
    /// <param name="ownsContext">When true, <see cref="Dispose"/> also disposes
    /// <paramref name="ctx"/> (see <see cref="TryCreateWithOwnContext"/>).</param>
    public FroxelVolumeVulkanKernel(VulkanContext ctx, bool ownsContext = false)
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

        BuildPipelines();
        _params = AllocBuffer(ParamBytes, BufferUsageFlags.UniformBufferBit);
        // 1-cell / 1-px stubs so a resize-before-first-dispatch path never binds a
        // zero-handle descriptor (Run reallocates to the real sizes anyway).
        EnsureVolumeBuffer(1);
        EnsureOutputBuffers(1);
    }

    /// <summary>Create a self-contained kernel that owns a fresh
    /// <see cref="VulkanContext"/> (instance + compute device), or null when no
    /// Vulkan device is available or init fails. Never throws.</summary>
    public static FroxelVolumeVulkanKernel? TryCreateWithOwnContext()
    {
        VulkanContext? ctx = null;
        try
        {
            ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) { ctx.Dispose(); return null; }
            ctx.CreateComputeDevice();
            return new FroxelVolumeVulkanKernel(ctx, ownsContext: true);
        }
        catch
        {
            ctx?.Dispose();
            return null;
        }
    }

    /// <summary>Populate + integrate the froxel volume for <paramref name="u"/> and
    /// composite it over <paramref name="beauty"/> by <paramref name="worldDepth"/>,
    /// writing the packed-ARGB result into <paramref name="dst"/>. The Vulkan twin of
    /// <see cref="FroxelCameraVolume.Apply"/>.</summary>
    public void Composite(in FroxelGpuUniforms u, uint[] beauty, float[] worldDepth, int w, int h, uint[] dst)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FroxelVolumeVulkanKernel));
        if (beauty == null) throw new ArgumentNullException(nameof(beauty));
        if (worldDepth == null) throw new ArgumentNullException(nameof(worldDepth));
        if (dst == null) throw new ArgumentNullException(nameof(dst));
        int n = w * h;
        if (w <= 0 || h <= 0) return;
        if (beauty.Length < n || worldDepth.Length < n || dst.Length < n)
            throw new ArgumentException("froxel GPU buffer too small for w*h");

        var g = u.Grid;
        int cells = g.DimX * g.DimY * g.DimZ;

        long t0 = Stopwatch.GetTimestamp();
        EnsureVolumeBuffer(cells);
        EnsureOutputBuffers(n);

        // Uploads.
        fixed (uint* p = beauty) WriteBytes(_beauty, p, n * sizeof(uint));
        fixed (float* p = worldDepth) WriteBytes(_depth, p, n * sizeof(float));
        var blob = BuildBlob(in u, w, h);
        WriteBytes(_params, &blob, sizeof(FroxelParamsBlob));

        // Two descriptor sets from the shared layout {b0, t0, t1, t2, u0}: the
        // integrate set binds the volume at u0 (200); the composite set binds the
        // volume at t2 (102) + the output at u0 (200). t0/t1/t2 are filled in both
        // sets with valid buffers so no bound descriptor is ever dangling.
        DescriptorPool pool = default;
        CommandBuffer cmd = default;
        try
        {
            var poolSizes = stackalloc DescriptorPoolSize[2]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 2 },
                new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 8 },
            };
            var dpci = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 2, PoolSizeCount = 2, PPoolSizes = poolSizes,
            };
            Check(_vk.CreateDescriptorPool(_device, in dpci, null, out pool), "vkCreateDescriptorPool");

            var layouts = stackalloc DescriptorSetLayout[2] { _dsl, _dsl };
            var dsai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool, DescriptorSetCount = 2, PSetLayouts = layouts,
            };
            var sets = stackalloc DescriptorSet[2];
            Check(_vk.AllocateDescriptorSets(_device, in dsai, sets), "vkAllocateDescriptorSets");
            DescriptorSet integrateSet = sets[0], compositeSet = sets[1];

            // Bindings per set: {0:params, 100:beauty, 101:depth, 102:volume, 200:<rw>}.
            // Integrate's u0 (200) = volume; composite's u0 (200) = output.
            WriteSet(integrateSet, _params.Buffer, _beauty.Buffer, _depth.Buffer, _volume.Buffer, _volume.Buffer);
            WriteSet(compositeSet, _params.Buffer, _beauty.Buffer, _depth.Buffer, _volume.Buffer, _out.Buffer);

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

            // Pass 1 — populate + integrate every column into the volume.
            var iSet = integrateSet;
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _integratePipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 0, 1, &iSet, 0, null);
            _vk.CmdDispatch(cmd, (uint)((g.DimX + 7) / 8), (uint)((g.DimY + 7) / 8), 1);

            // Barrier: integrate's shader writes to the volume must be visible to the
            // composite's shader reads.
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
            };
            _vk.CmdPipelineBarrier(cmd,
                PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit,
                0, 1, &barrier, 0, null, 0, null);

            // Pass 2 — composite over the beauty by per-pixel depth.
            var cSet = compositeSet;
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _compositePipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 0, 1, &cSet, 0, null);
            _vk.CmdDispatch(cmd, (uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);

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
        ReadUints(_out, dst, n);
        long tEnd = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
        LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
    }

    // Update one descriptor set's five bindings {0, 100, 101, 102, 200}.
    private void WriteSet(DescriptorSet set, Buffer b0, Buffer t0, Buffer t1, Buffer t2, Buffer u0)
    {
        Buffer* bufs = stackalloc Buffer[5] { b0, t0, t1, t2, u0 };
        uint* binds = stackalloc uint[5] { 0, (uint)TShift, (uint)TShift + 1, (uint)TShift + 2, (uint)UShift };
        var types = stackalloc DescriptorType[5]
        {
            DescriptorType.UniformBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
        };
        var infos = stackalloc DescriptorBufferInfo[5];
        var writes = stackalloc WriteDescriptorSet[5];
        for (int i = 0; i < 5; i++)
        {
            infos[i] = new DescriptorBufferInfo { Buffer = bufs[i], Offset = 0, Range = Vk.WholeSize };
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set, DstBinding = binds[i], DescriptorCount = 1,
                DescriptorType = types[i], PBufferInfo = &infos[i],
            };
        }
        _vk.UpdateDescriptorSets(_device, 5, writes, 0, null);
    }

    private static FroxelParamsBlob BuildBlob(in FroxelGpuUniforms u, int w, int h)
    {
        var g = u.Grid;
        var m = u.Medium;
        var lights = m.Lights;
        int nl = lights?.Length ?? 0;

        FroxelLight L0 = nl > 0 ? lights![0] : default;
        FroxelLight L1 = nl > 1 ? lights![1] : default;
        FroxelLight L2 = nl > 2 ? lights![2] : default;

        return new FroxelParamsBlob
        {
            Nx = g.DimX, Ny = g.DimY, Nz = g.DimZ, W = w,
            H = h, Near = (float)g.Near, Far = (float)g.Far, Extent = (float)m.WorldExtent,
            BaseDensity = (float)m.BaseDensity, Extinction = (float)m.Extinction,
            Anisotropy = (float)m.Anisotropy, NoiseAmount = (float)m.NoiseAmount,
            NoiseScale = (float)m.NoiseScale, NoiseOctaves = m.NoiseOctaves,
            ViewX = (float)m.ViewDx, ViewY = (float)m.ViewDy,
            ViewZ = (float)m.ViewDz, NumLights = nl, Pad0 = 0f, Pad1 = 0f,
            Type0 = L0.Type, Color0 = L0.Color, I0 = (float)L0.Intensity, Range0 = (float)L0.Range,
            Dir0x = (float)L0.Lx, Dir0y = (float)L0.Ly, Dir0z = (float)L0.Lz, Inner0 = (float)L0.InnerCos,
            Pos0x = (float)L0.PosX, Pos0y = (float)L0.PosY, Pos0z = (float)L0.PosZ, Outer0 = (float)L0.OuterCos,
            Type1 = L1.Type, Color1 = L1.Color, I1 = (float)L1.Intensity, Range1 = (float)L1.Range,
            Dir1x = (float)L1.Lx, Dir1y = (float)L1.Ly, Dir1z = (float)L1.Lz, Inner1 = (float)L1.InnerCos,
            Pos1x = (float)L1.PosX, Pos1y = (float)L1.PosY, Pos1z = (float)L1.PosZ, Outer1 = (float)L1.OuterCos,
            Type2 = L2.Type, Color2 = L2.Color, I2 = (float)L2.Intensity, Range2 = (float)L2.Range,
            Dir2x = (float)L2.Lx, Dir2y = (float)L2.Ly, Dir2z = (float)L2.Lz, Inner2 = (float)L2.InnerCos,
            Pos2x = (float)L2.PosX, Pos2y = (float)L2.PosY, Pos2z = (float)L2.PosZ, Outer2 = (float)L2.OuterCos,
        };
    }

    // ── pipelines ───────────────────────────────────────────────────────────────
    private void BuildPipelines()
    {
        // Shared layout {b0 UBO, t0/t1/t2 SSBO, u0 SSBO}. The integrate pipeline uses
        // {b0, u0}; the composite pipeline uses all five. A superset layout is fine —
        // each pipeline only touches the bindings it declares.
        uint* binds = stackalloc uint[5] { 0, (uint)TShift, (uint)TShift + 1, (uint)TShift + 2, (uint)UShift };
        var bindings = stackalloc DescriptorSetLayoutBinding[5];
        for (int i = 0; i < 5; i++)
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = binds[i],
                DescriptorType = i == 0 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit,
            };
        var dslci = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 5, PBindings = bindings,
        };
        Check(_vk.CreateDescriptorSetLayout(_device, in dslci, null, out _dsl), "vkCreateDescriptorSetLayout");

        var dslLocal = _dsl;
        var plci = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1, PSetLayouts = &dslLocal,
        };
        Check(_vk.CreatePipelineLayout(_device, in plci, null, out _layout), "vkCreatePipelineLayout");

        (_integrateModule, _integratePipeline) = CreatePipeline(FroxelKernelSource.IntegrateEntry);
        (_compositeModule, _compositePipeline) = CreatePipeline(FroxelKernelSource.CompositeEntry);
    }

    // Compile one froxel entry point → (shader module, compute pipeline), reusing
    // the shared _layout.
    private (ShaderModule, Pipeline) CreatePipeline(string entry)
    {
        byte[] spirv = DxcCompiler.CompileToSpirv(
            FroxelKernelSource.Build(), entry, "cs_6_0",
            "-fvk-b-shift", BShift.ToString(), "0",
            "-fvk-t-shift", TShift.ToString(), "0",
            "-fvk-u-shift", UShift.ToString(), "0");

        ShaderModule mod;
        fixed (byte* code = spirv)
        {
            var smci = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length, PCode = (uint*)code,
            };
            Check(_vk.CreateShaderModule(_device, in smci, null, out mod), "vkCreateShaderModule");
        }

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
                    Module = mod, PName = (byte*)entryPtr,
                },
                Layout = _layout,
            };
            Pipeline created;
            Check(_vk.CreateComputePipelines(_device, default, 1, &cpci, null, &created), "vkCreateComputePipelines");
            return (mod, created);
        }
        finally { SilkMarshal.Free(entryPtr); }
    }

    // ── buffers ───────────────────────────────────────────────────────────────
    private void EnsureVolumeBuffer(int cells)
    {
        if (cells < 1) cells = 1;
        if (_volume.Buffer.Handle != 0 && _volumeCells == cells) return;
        FreeBuffer(ref _volume);
        _volume = AllocBuffer((ulong)(cells * 4 * sizeof(float)), BufferUsageFlags.StorageBufferBit);
        _volumeCells = cells;
    }

    private void EnsureOutputBuffers(int n)
    {
        if (n < 1) n = 1;
        if (_out.Buffer.Handle != 0 && _outPixels == n) return;
        FreeBuffer(ref _beauty);
        FreeBuffer(ref _depth);
        FreeBuffer(ref _out);
        _beauty = AllocBuffer((ulong)(n * sizeof(uint)), BufferUsageFlags.StorageBufferBit);
        _depth = AllocBuffer((ulong)(n * sizeof(float)), BufferUsageFlags.StorageBufferBit);
        _out = AllocBuffer((ulong)(n * sizeof(uint)), BufferUsageFlags.StorageBufferBit);
        _outPixels = n;
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

    private void ReadUints(Allocated a, uint[] dst, int n)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.Memory, 0, (ulong)(n * sizeof(uint)), 0, &mapped), "vkMapMemory");
        new Span<uint>(mapped, n).CopyTo(dst.AsSpan(0, n));
        _vk.UnmapMemory(_device, a.Memory);
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
    {
        PhysicalDeviceMemoryProperties memProps;
        _vk.GetPhysicalDeviceMemoryProperties(_ctx.PhysicalDevice, &memProps);
        var mtypes = (MemoryType*)&memProps.MemoryTypes;
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 && (mtypes[i].PropertyFlags & required) == required)
                return i;
        throw new InvalidOperationException($"no memory type with {required} for typeBits 0x{typeBits:X}");
    }

    private static void Check(Result r, string what)
    {
        if (r != Result.Success) throw new InvalidOperationException($"{what} failed: {r}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_device.Handle != 0) _vk.DeviceWaitIdle(_device); } catch { }
        if (_integratePipeline.Handle != 0) _vk.DestroyPipeline(_device, _integratePipeline, null);
        if (_compositePipeline.Handle != 0) _vk.DestroyPipeline(_device, _compositePipeline, null);
        if (_layout.Handle != 0) _vk.DestroyPipelineLayout(_device, _layout, null);
        if (_dsl.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _dsl, null);
        if (_integrateModule.Handle != 0) _vk.DestroyShaderModule(_device, _integrateModule, null);
        if (_compositeModule.Handle != 0) _vk.DestroyShaderModule(_device, _compositeModule, null);
        FreeBuffer(ref _params);
        FreeBuffer(ref _volume);
        FreeBuffer(ref _beauty);
        FreeBuffer(ref _depth);
        FreeBuffer(ref _out);
        if (_cmdPool.Handle != 0) { _vk.DestroyCommandPool(_device, _cmdPool, null); _cmdPool = default; }
        if (_ownsContext) { try { _ctx.Dispose(); } catch { } }
    }
}
