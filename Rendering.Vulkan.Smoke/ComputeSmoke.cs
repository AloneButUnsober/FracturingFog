// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// V0 trivial compute proof: dispatch a 64x64 HLSL kernel that writes a uv
// gradient as packed BGRA into an RWStructuredBuffer, then map it back.
//
// The kernel deliberately reuses the exact cg_pack_bgra convention from the
// shipped D3D kernel (MandelbrotGpuKernel.cs), so V1 can swap this trivial body
// for the real one with no packing/endianness surprises. The whole Vulkan
// pipeline here (storage buffer, descriptor set, compute pipeline, command
// buffer, dispatch, vkMapMemory) is the boilerplate V1 will grow, kept minimal.

using System;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace FracturingFog.Rendering.Vulkan.Smoke;

internal static unsafe class ComputeSmoke
{
    public const int Width = 64;
    public const int Height = 64;
    private const int GroupSize = 8; // numthreads(8,8,1) -> 8x8 groups cover 64x64

    // Trivial HLSL CS. Explicit vk::binding(0,0) pins the RWStructuredBuffer to
    // descriptor set 0 / binding 0, matching the layout built below (avoids
    // relying on DXC's default u-register -> binding mapping).
    public const string Hlsl = """
        [[vk::binding(0, 0)]]
        RWStructuredBuffer<uint> gColor : register(u0);

        static const uint W = 64;
        static const uint H = 64;

        // Mirrors cg_pack_bgra in Rendering.D3D/MandelbrotGpuKernel.cs.
        uint pack_bgra(float3 c)
        {
            c = saturate(c);
            uint r = (uint)(c.r * 255.0 + 0.5);
            uint g = (uint)(c.g * 255.0 + 0.5);
            uint b = (uint)(c.b * 255.0 + 0.5);
            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        [numthreads(8, 8, 1)]
        void main(uint3 tid : SV_DispatchThreadID)
        {
            if (tid.x >= W || tid.y >= H) return;
            float u = tid.x / (float)(W - 1);
            float v = tid.y / (float)(H - 1);
            gColor[tid.y * W + tid.x] = pack_bgra(float3(u, v, 0.0));
        }
        """;

    // CPU mirror of pack_bgra, for corner verification.
    public static uint PackBgra(float r, float g, float b)
    {
        r = Math.Clamp(r, 0f, 1f);
        g = Math.Clamp(g, 0f, 1f);
        b = Math.Clamp(b, 0f, 1f);
        uint ri = (uint)(r * 255f + 0.5f);
        uint gi = (uint)(g * 255f + 0.5f);
        uint bi = (uint)(b * 255f + 0.5f);
        return 0xFF000000u | (ri << 16) | (gi << 8) | bi;
    }

    // Expected packed value at pixel (x,y) per the CPU mirror.
    public static uint ExpectedAt(int x, int y)
    {
        float u = x / (float)(Width - 1);
        float v = y / (float)(Height - 1);
        return PackBgra(u, v, 0f);
    }

    // Compile the trivial kernel, run it on the context's compute device, and
    // return the mapped-back Width*Height BGRA buffer.
    public static uint[] Run(VulkanContext ctx)
    {
        byte[] spirv = DxcCompiler.CompileToSpirv(Hlsl, entry: "main", profile: "cs_6_0");

        var vk = ctx.Vk;
        var device = ctx.Device;
        ulong byteSize = (ulong)(Width * Height * sizeof(uint));

        Buffer buffer = default;
        DeviceMemory memory = default;
        ShaderModule module = default;
        DescriptorSetLayout dsl = default;
        PipelineLayout layout = default;
        Pipeline pipeline = default;
        DescriptorPool pool = default;
        CommandPool cmdPool = default;
        nint entryPtr = 0;

        try
        {
            // --- storage buffer + host-visible/coherent memory ---
            var bci = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = byteSize,
                Usage = BufferUsageFlags.StorageBufferBit,
                SharingMode = SharingMode.Exclusive,
            };
            Check(vk.CreateBuffer(device, in bci, null, out buffer), "vkCreateBuffer");

            vk.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements req);
            uint memType = FindMemoryType(ctx, req.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            var mai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = memType,
            };
            Check(vk.AllocateMemory(device, in mai, null, out memory), "vkAllocateMemory");
            Check(vk.BindBufferMemory(device, buffer, memory, 0), "vkBindBufferMemory");

            // --- shader module ---
            fixed (byte* code = spirv)
            {
                var smci = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)code,
                };
                Check(vk.CreateShaderModule(device, in smci, null, out module), "vkCreateShaderModule");
            }

            // --- descriptor set layout: binding 0 = storage buffer (compute) ---
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
            var dslci = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding,
            };
            Check(vk.CreateDescriptorSetLayout(device, in dslci, null, out dsl), "vkCreateDescriptorSetLayout");

            var dslLocal = dsl;
            var plci = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &dslLocal,
            };
            Check(vk.CreatePipelineLayout(device, in plci, null, out layout), "vkCreatePipelineLayout");

            // --- compute pipeline ---
            entryPtr = SilkMarshal.StringToPtr("main");
            var cpci = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = (byte*)entryPtr,
                },
                Layout = layout,
            };
            Pipeline createdPipeline;
            Check(vk.CreateComputePipelines(device, default, 1, &cpci, null, &createdPipeline),
                "vkCreateComputePipelines");
            pipeline = createdPipeline;

            // --- descriptor pool + set, point it at the buffer ---
            var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 1 };
            var dpci = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
            };
            Check(vk.CreateDescriptorPool(device, in dpci, null, out pool), "vkCreateDescriptorPool");

            var dsai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &dslLocal,
            };
            Check(vk.AllocateDescriptorSets(device, in dsai, out DescriptorSet set), "vkAllocateDescriptorSets");

            var bufInfo = new DescriptorBufferInfo { Buffer = buffer, Offset = 0, Range = byteSize };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bufInfo,
            };
            vk.UpdateDescriptorSets(device, 1, &write, 0, null);

            // --- command buffer: bind + dispatch 8x8 groups ---
            var cmdPoolCi = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = ctx.ComputeQueueFamily,
            };
            Check(vk.CreateCommandPool(device, in cmdPoolCi, null, out cmdPool), "vkCreateCommandPool");

            var cbai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = cmdPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(vk.AllocateCommandBuffers(device, in cbai, out CommandBuffer cmd), "vkAllocateCommandBuffers");

            var begin = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(vk.BeginCommandBuffer(cmd, in begin), "vkBeginCommandBuffer");
            vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
            vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, layout, 0, 1, &set, 0, null);
            vk.CmdDispatch(cmd, Width / GroupSize, Height / GroupSize, 1);
            Check(vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");

            var cmdLocal = cmd;
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmdLocal,
            };
            Check(vk.QueueSubmit(ctx.ComputeQueue, 1, &submit, default), "vkQueueSubmit");
            Check(vk.QueueWaitIdle(ctx.ComputeQueue), "vkQueueWaitIdle");

            // --- map + read back (HOST_COHERENT: no invalidate needed) ---
            void* mapped;
            Check(vk.MapMemory(device, memory, 0, byteSize, 0, &mapped), "vkMapMemory");
            var result = new uint[Width * Height];
            new Span<uint>(mapped, Width * Height).CopyTo(result);
            vk.UnmapMemory(device, memory);
            return result;
        }
        finally
        {
            if (entryPtr != 0) SilkMarshal.Free(entryPtr);
            if (cmdPool.Handle != 0) vk.DestroyCommandPool(device, cmdPool, null);
            if (pool.Handle != 0) vk.DestroyDescriptorPool(device, pool, null);
            if (pipeline.Handle != 0) vk.DestroyPipeline(device, pipeline, null);
            if (layout.Handle != 0) vk.DestroyPipelineLayout(device, layout, null);
            if (dsl.Handle != 0) vk.DestroyDescriptorSetLayout(device, dsl, null);
            if (module.Handle != 0) vk.DestroyShaderModule(device, module, null);
            if (buffer.Handle != 0) vk.DestroyBuffer(device, buffer, null);
            if (memory.Handle != 0) vk.FreeMemory(device, memory, null);
        }
    }

    private static uint FindMemoryType(VulkanContext ctx, uint typeBits, MemoryPropertyFlags required)
    {
        PhysicalDeviceMemoryProperties memProps;
        ctx.Vk.GetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, &memProps);
        var types = (MemoryType*)&memProps.MemoryTypes;
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            bool typeOk = (typeBits & (1u << (int)i)) != 0;
            bool propsOk = (types[i].PropertyFlags & required) == required;
            if (typeOk && propsOk) return i;
        }
        throw new InvalidOperationException(
            $"no memory type with {required} for typeBits 0x{typeBits:X}");
    }

    private static void Check(Result r, string what)
    {
        if (r != Result.Success)
            throw new InvalidOperationException($"{what} failed: {r}");
    }
}
