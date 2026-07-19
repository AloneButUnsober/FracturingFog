// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// V1 (#40): run the REAL Mandelbrot base kernel (iter/smooth) on Vulkan compute
// and check it against a C# reference that mirrors the same float math.
//
// Source: MandelbrotKernelSource.BuildBase() — the identical HLSL that FXC
// compiles for the D3D path. DXC compiles it to SPIR-V with -fvk-*-shift flags
// (no vk::binding attributes in the shared source, so FXC stays happy):
//   register class b -> binding+0    (Params cbuffer -> UBO,   binding 0)
//   register class t -> binding+100  (gPerRow SRV    -> SSBO,  binding 100)
//   register class u -> binding+200  (gIter/gSmooth/gFinalZD UAVs -> SSBO 200..202)
//
// Parity is checked ULP-band vs the CPU reference (cross-vendor float / FMA /
// transcendental impls are not bit-exact D3D vs lavapipe vs a real GPU), not a
// strict digest. See Docs/Technical/Vulkan-Compute-DevelopmentPlan.md §V1.

using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using FracturingFog.Rendering; // MandelbrotKernelSource (linked source)

namespace FracturingFog.Rendering.Vulkan.Smoke;

internal static unsafe class RealKernelProbe
{
    // DXC binding shifts per register class (see file header).
    public const int BShift = 0;
    public const int TShift = 100;
    public const int UShift = 200;

    // 64-byte cbuffer Params blob (15 scalars + 4 bytes tail pad = float4
    // multiple). Field order MUST match MandelbrotKernelSource.HlslBase's
    // cbuffer and MandelbrotGpuKernel.Params. std140 packs consecutive scalars
    // at 4-byte offsets, so this maps 1:1 onto the DXC-generated UBO.
    [StructLayout(LayoutKind.Sequential)]
    public struct ParamsBlob
    {
        public int Width, Height, MaxIter;
        public float Bailout2;
        public float CXHi, CXLo, CYHi, CYLo, ScaleHi, ScaleLo;
        public int UsePerRow, FractalKind;
        public float Param0, Param1, DitherStrength;
        public float _pad; // -> 64 bytes
    }

    // Fixed shallow Mandelbrot view for the parity probe: classic ~3.5-wide
    // frame centred near the seahorse-valley mouth. Mix of in-set + escaped.
    public sealed class View
    {
        public int Width = 128;
        public int Height = 128;
        public int MaxIter = 256;
        public double Bailout2 = 4.0;
        public double CenterX = -0.75;
        public double CenterY = 0.0;
        public double Scale;   // world units per pixel

        public View() => Scale = 3.5 / Width;

        public ParamsBlob ToBlob()
        {
            // Split centre + scale into hi/lo floats exactly as the D3D Run()
            // path does (MandelbrotGpuKernel.Run).
            float cxHi = (float)CenterX, cyHi = (float)CenterY, scHi = (float)Scale;
            return new ParamsBlob
            {
                Width = Width,
                Height = Height,
                MaxIter = MaxIter,
                Bailout2 = (float)Bailout2,
                CXHi = cxHi,
                CXLo = (float)(CenterX - cxHi),
                CYHi = cyHi,
                CYLo = (float)(CenterY - cyHi),
                ScaleHi = scHi,
                ScaleLo = (float)(Scale - scHi),
                UsePerRow = 0,
                FractalKind = 0,
                Param0 = 0f,
                Param1 = 0f,
                DitherStrength = 0f,
                _pad = 0f,
            };
        }
    }

    // ── CPU reference: exact float mirror of CSMain (Mandelbrot, kind 0) ──────
    public static void CpuReference(View v, out uint[] iter, out float[] smooth)
    {
        var p = v.ToBlob();
        int W = v.Width, H = v.Height, maxIter = v.MaxIter;
        float bail2 = p.Bailout2;
        iter = new uint[W * H];
        smooth = new float[W * H];

        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int idx = y * W + x;
            float fx = (float)x - 0.5f * W;
            float fy = (float)y - 0.5f * H;
            float cx = p.CXHi + fx * p.ScaleHi + p.CXLo + fx * p.ScaleLo;
            float cy = p.CYHi + fy * p.ScaleHi + p.CYLo + fy * p.ScaleLo;

            if (InCardioid(cx, cy) || InPeriod2Bulb(cx, cy))
            {
                iter[idx] = (uint)maxIter;
                smooth[idx] = 0f;
                continue;
            }

            float zr = 0f, zi = 0f, cr = cx, ci = cy, dr = 1f, di = 0f;
            int it = 0;
            for (; it < maxIter; it++)
            {
                float zr2 = zr * zr;
                float zi2 = zi * zi;
                float mag2 = zr2 + zi2;
                if (mag2 >= bail2) break;

                float newDr = 2f * (zr * dr - zi * di) + 1f;
                float newDi = 2f * (zr * di + zi * dr);
                dr = newDr;
                di = newDi;

                float zrNew = zr2 - zi2 + cr;
                float ziu = zr * zi;
                zi = ziu + ziu + ci;
                zr = zrNew;
            }

            if (it >= maxIter)
            {
                iter[idx] = (uint)maxIter;
                smooth[idx] = 0f;
            }
            else
            {
                iter[idx] = (uint)it;
                float mag = MathF.Sqrt(zr * zr + zi * zi);
                float nu = MathF.Log(MathF.Log(MathF.Max(mag, 1.001f))) / MathF.Log(2f);
                smooth[idx] = (float)it + 1f - nu;
            }
        }
    }

    private static bool InCardioid(float cx, float cy)
    {
        float xm = cx - 0.25f;
        float q = xm * xm + cy * cy;
        return q * (q + xm) <= 0.25f * cy * cy;
    }

    private static bool InPeriod2Bulb(float cx, float cy)
    {
        float dx = cx + 1f;
        return dx * dx + cy * cy <= 0.0625f;
    }

    // ── Vulkan: compile the real base kernel + dispatch, read iter/smooth ─────
    public static void RunVulkan(VulkanContext ctx, View v, out uint[] iter, out float[] smooth)
    {
        byte[] spirv = DxcCompiler.CompileToSpirv(
            MandelbrotKernelSource.BuildBase(),
            entry: MandelbrotKernelSource.EntryPoint,
            profile: "cs_6_0",
            "-fvk-b-shift", BShift.ToString(), "0",
            "-fvk-t-shift", TShift.ToString(), "0",
            "-fvk-u-shift", UShift.ToString(), "0");

        var vk = ctx.Vk;
        var device = ctx.Device;
        int W = v.Width, H = v.Height, n = W * H;

        var blob = v.ToBlob();
        ulong paramsSize = 64;
        ulong iterSize = (ulong)(n * sizeof(uint));
        ulong smoothSize = (ulong)(n * sizeof(float));
        ulong finalZDSize = (ulong)(n * 4 * sizeof(float));
        ulong perRowSize = (ulong)(Math.Max(H, 1) * sizeof(uint));

        var buffers = new Allocated[5]; // 0=params,1=perRow,2=iter,3=smooth,4=finalZD
        ShaderModule module = default;
        DescriptorSetLayout dsl = default;
        PipelineLayout layout = default;
        Pipeline pipeline = default;
        DescriptorPool pool = default;
        CommandPool cmdPool = default;
        nint entryPtr = 0;

        try
        {
            buffers[0] = AllocBuffer(ctx, paramsSize, BufferUsageFlags.UniformBufferBit);
            buffers[1] = AllocBuffer(ctx, perRowSize, BufferUsageFlags.StorageBufferBit);
            buffers[2] = AllocBuffer(ctx, iterSize, BufferUsageFlags.StorageBufferBit);
            buffers[3] = AllocBuffer(ctx, smoothSize, BufferUsageFlags.StorageBufferBit);
            buffers[4] = AllocBuffer(ctx, finalZDSize, BufferUsageFlags.StorageBufferBit);

            // Upload params; zero per-row (UsePerRow=0 → unread, but must bind).
            WriteBuffer(ctx, buffers[0], &blob, (int)paramsSize);
            ZeroBuffer(ctx, buffers[1], (int)perRowSize);

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

            // Descriptor set layout: UBO@0, SSBO@100/200/201/202.
            var bindings = stackalloc DescriptorSetLayoutBinding[5]
            {
                LayoutBinding(0,             DescriptorType.UniformBuffer),
                LayoutBinding((uint)TShift,  DescriptorType.StorageBuffer),      // gPerRow t0
                LayoutBinding((uint)UShift,  DescriptorType.StorageBuffer),      // gIter u0
                LayoutBinding((uint)UShift+1,DescriptorType.StorageBuffer),      // gSmooth u1
                LayoutBinding((uint)UShift+2,DescriptorType.StorageBuffer),      // gFinalZD u2
            };
            var dslci = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 5,
                PBindings = bindings,
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

            entryPtr = SilkMarshal.StringToPtr(MandelbrotKernelSource.EntryPoint);
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
            Pipeline created;
            Check(vk.CreateComputePipelines(device, default, 1, &cpci, null, &created), "vkCreateComputePipelines");
            pipeline = created;

            // Descriptor pool: 1 UBO + 4 SSBO in one set.
            var poolSizes = stackalloc DescriptorPoolSize[2]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
                new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 4 },
            };
            var dpci = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 2,
                PPoolSizes = poolSizes,
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

            // One write per binding.
            var infos = stackalloc DescriptorBufferInfo[5];
            var writes = stackalloc WriteDescriptorSet[5];
            uint[] bindNums = { 0, (uint)TShift, (uint)UShift, (uint)UShift + 1, (uint)UShift + 2 };
            DescriptorType[] types =
            {
                DescriptorType.UniformBuffer, DescriptorType.StorageBuffer,
                DescriptorType.StorageBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            };
            for (int i = 0; i < 5; i++)
            {
                infos[i] = new DescriptorBufferInfo { Buffer = buffers[i].Buffer, Offset = 0, Range = Vk.WholeSize };
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = bindNums[i],
                    DescriptorCount = 1,
                    DescriptorType = types[i],
                    PBufferInfo = &infos[i],
                };
            }
            vk.UpdateDescriptorSets(device, 5, writes, 0, null);

            // Command buffer.
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
            vk.CmdDispatch(cmd, (uint)((W + 7) / 8), (uint)((H + 7) / 8), 1);
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

            iter = ReadBuffer<uint>(ctx, buffers[2], n);
            smooth = ReadBuffer<float>(ctx, buffers[3], n);
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
            for (int i = 0; i < buffers.Length; i++) FreeBuffer(ctx, buffers[i]);
        }
    }

    // ── small Vulkan buffer helpers (shared with RealKernelColorProbe) ───────
    public struct Allocated { public Buffer Buffer; public DeviceMemory Memory; }

    internal static DescriptorSetLayoutBinding LayoutBinding(uint binding, DescriptorType type) => new()
    {
        Binding = binding,
        DescriptorType = type,
        DescriptorCount = 1,
        StageFlags = ShaderStageFlags.ComputeBit,
    };

    internal static Allocated AllocBuffer(VulkanContext ctx, ulong size, BufferUsageFlags usage)
    {
        var vk = ctx.Vk;
        var bci = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Check(vk.CreateBuffer(ctx.Device, in bci, null, out Buffer buffer), "vkCreateBuffer");
        vk.GetBufferMemoryRequirements(ctx.Device, buffer, out MemoryRequirements req);
        uint memType = FindMemoryType(ctx, req.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        var mai = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = memType,
        };
        Check(vk.AllocateMemory(ctx.Device, in mai, null, out DeviceMemory mem), "vkAllocateMemory");
        Check(vk.BindBufferMemory(ctx.Device, buffer, mem, 0), "vkBindBufferMemory");
        return new Allocated { Buffer = buffer, Memory = mem };
    }

    internal static void WriteBuffer(VulkanContext ctx, Allocated a, void* src, int bytes)
    {
        void* mapped;
        Check(ctx.Vk.MapMemory(ctx.Device, a.Memory, 0, (ulong)bytes, 0, &mapped), "vkMapMemory");
        System.Buffer.MemoryCopy(src, mapped, bytes, bytes);
        ctx.Vk.UnmapMemory(ctx.Device, a.Memory);
    }

    internal static void ZeroBuffer(VulkanContext ctx, Allocated a, int bytes)
    {
        void* mapped;
        Check(ctx.Vk.MapMemory(ctx.Device, a.Memory, 0, (ulong)bytes, 0, &mapped), "vkMapMemory");
        new Span<byte>(mapped, bytes).Clear();
        ctx.Vk.UnmapMemory(ctx.Device, a.Memory);
    }

    internal static T[] ReadBuffer<T>(VulkanContext ctx, Allocated a, int count) where T : unmanaged
    {
        void* mapped;
        ulong bytes = (ulong)(count * sizeof(T));
        Check(ctx.Vk.MapMemory(ctx.Device, a.Memory, 0, bytes, 0, &mapped), "vkMapMemory");
        var result = new T[count];
        new Span<T>(mapped, count).CopyTo(result);
        ctx.Vk.UnmapMemory(ctx.Device, a.Memory);
        return result;
    }

    internal static void FreeBuffer(VulkanContext ctx, Allocated a)
    {
        if (a.Buffer.Handle != 0) ctx.Vk.DestroyBuffer(ctx.Device, a.Buffer, null);
        if (a.Memory.Handle != 0) ctx.Vk.FreeMemory(ctx.Device, a.Memory, null);
    }

    internal static uint FindMemoryType(VulkanContext ctx, uint typeBits, MemoryPropertyFlags required)
    {
        PhysicalDeviceMemoryProperties memProps;
        ctx.Vk.GetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, &memProps);
        var types = (MemoryType*)&memProps.MemoryTypes;
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) != 0 && (types[i].PropertyFlags & required) == required)
                return i;
        }
        throw new InvalidOperationException($"no memory type with {required} for typeBits 0x{typeBits:X}");
    }

    internal static void Check(Result r, string what)
    {
        if (r != Result.Success) throw new InvalidOperationException($"{what} failed: {r}");
    }
}
