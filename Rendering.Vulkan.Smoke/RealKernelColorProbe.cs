// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// V2 (#41): run the REAL colour-emitting Mandelbrot kernel on Vulkan compute and
// check the packed-BGRA output against a deterministic CPU mirror of the same
// HLSL palette + pack.
//
// Source: MandelbrotKernelSource.BuildColor(helpers, body) — the identical HLSL
// FXC compiles for the D3D colour variant. DXC compiles it to SPIR-V with the
// same -fvk-*-shift flags as V1, plus the colour output:
//   register class u3 (gColor UAV) -> binding 203 (UShift + 3)
//
// The probe uses the Greyscale theme's IGpuHlslPalette body (empty prelude,
// depends only on in_smooth + in_isInSet) so the C# reference is a short, exact
// mirror of the HLSL — no Engine reference dragged into this standalone project.
//
// Why a band, not a bit-exact GPU digest: same reason as V1 (see dev-plan §V1).
// Cross-vendor float/transcendental impls (D3D vs lavapipe vs a real GPU) differ
// by a few ULP; near the set boundary that flips iter, which flips a pixel
// black<->grey. So the GATE has two independent parts:
//   1. an embedded golden digest of the DETERMINISTIC CPU mirror colour — pins
//      that the reference (view + theme + pack) itself did not drift; and
//   2. a byte-disagreement BAND of the GPU colour vs that mirror (±1/channel to
//      absorb legit rounding-at-float-noise), robust across GPUs.
// A real binding/pack/EvalPalette regression disagrees on a huge fraction and
// trips (2); a change to the reference maths trips (1).

using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using FracturingFog.Rendering; // MandelbrotKernelSource (linked source)
using Probe = FracturingFog.Rendering.Vulkan.Smoke.RealKernelProbe;

namespace FracturingFog.Rendering.Vulkan.Smoke;

internal static unsafe class RealKernelColorProbe
{
    // gColor UAV register u3 -> binding UShift + 3.
    public const int ColorBinding = RealKernelProbe.UShift + 3;

    // Greyscale theme, verbatim from Engine GrayscalePalette (HlslPrelude is
    // empty; PaletteId "GrayscalePalette/v1"). Kept here as a literal so this
    // standalone project needs no Engine reference — the C# mirror below is the
    // 1:1 twin of this body.
    public const string GreyBody = @"
    if (in_isInSet > 0.5) return float3(0.0, 0.0, 0.0);
    float traw = in_smooth * 0.020;
    float t = traw - floor(traw);
    float band = 0.5 + 0.5 * sin(in_smooth * 0.12);
    float v = saturate(t * 0.75 + band * 0.25);
    return float3(v, v, v);
";

    // ── CPU mirror: Greyscale EvalPalette body + cg_pack_bgra (dither off) ────
    //
    // Deterministic and machine-independent (MathF): this is the reference the
    // golden digest pins and the GPU is banded against. inSet pixels take the
    // in-set / bulb splice (in_isInSet = 1 -> float3(0,0,0)); escaped pixels
    // feed in_smooth = smooth. Pack rounds (+0.5) and clamps exactly like the
    // shared cg_pack_bgra with gDitherStrength == 0.
    public static uint PackGrey(float smooth, bool inSet)
    {
        float v;
        if (inSet)
        {
            v = 0f; // float3(0,0,0)
        }
        else
        {
            float traw = smooth * 0.020f;
            float t = traw - MathF.Floor(traw);
            float bandv = 0.5f + 0.5f * MathF.Sin(smooth * 0.12f);
            v = Math.Clamp(t * 0.75f + bandv * 0.25f, 0f, 1f);
        }
        // cg_pack_bgra, all three channels equal, dither offset 0.
        v = Math.Clamp(v, 0f, 1f);
        uint c = (uint)Math.Clamp(v * 255f + 0.5f, 0f, 255f);
        return 0xFF000000u | (c << 16) | (c << 8) | c;
    }

    public static uint[] CpuReferenceColor(Probe.View v)
    {
        Probe.CpuReference(v, out uint[] iter, out float[] smooth);
        uint maxIter = (uint)v.MaxIter;
        int n = v.Width * v.Height;
        var color = new uint[n];
        for (int i = 0; i < n; i++)
            color[i] = PackGrey(smooth[i], iter[i] == maxIter);
        return color;
    }

    // ── Vulkan: compile the colour kernel + dispatch, read packed BGRA ────────
    public static void RunVulkan(VulkanContext ctx, Probe.View v, string paletteBody, out uint[] color)
    {
        byte[] spirv = DxcCompiler.CompileToSpirv(
            MandelbrotKernelSource.BuildColor(paletteHelpers: "", paletteBody: paletteBody),
            entry: MandelbrotKernelSource.EntryPoint,
            profile: "cs_6_0",
            "-fvk-b-shift", RealKernelProbe.BShift.ToString(), "0",
            "-fvk-t-shift", RealKernelProbe.TShift.ToString(), "0",
            "-fvk-u-shift", RealKernelProbe.UShift.ToString(), "0");

        var vk = ctx.Vk;
        var device = ctx.Device;
        int W = v.Width, H = v.Height, n = W * H;

        var blob = v.ToBlob();
        ulong paramsSize = 64;
        ulong iterSize = (ulong)(n * sizeof(uint));
        ulong smoothSize = (ulong)(n * sizeof(float));
        ulong finalZDSize = (ulong)(n * 4 * sizeof(float));
        ulong colorSize = (ulong)(n * sizeof(uint));
        ulong perRowSize = (ulong)(Math.Max(H, 1) * sizeof(uint));

        // 0=params,1=perRow,2=iter,3=smooth,4=finalZD,5=color
        var buffers = new Probe.Allocated[6];
        ShaderModule module = default;
        DescriptorSetLayout dsl = default;
        PipelineLayout layout = default;
        Pipeline pipeline = default;
        DescriptorPool pool = default;
        CommandPool cmdPool = default;
        nint entryPtr = 0;

        try
        {
            buffers[0] = Probe.AllocBuffer(ctx, paramsSize, BufferUsageFlags.UniformBufferBit);
            buffers[1] = Probe.AllocBuffer(ctx, perRowSize, BufferUsageFlags.StorageBufferBit);
            buffers[2] = Probe.AllocBuffer(ctx, iterSize, BufferUsageFlags.StorageBufferBit);
            buffers[3] = Probe.AllocBuffer(ctx, smoothSize, BufferUsageFlags.StorageBufferBit);
            buffers[4] = Probe.AllocBuffer(ctx, finalZDSize, BufferUsageFlags.StorageBufferBit);
            buffers[5] = Probe.AllocBuffer(ctx, colorSize, BufferUsageFlags.StorageBufferBit);

            Probe.WriteBuffer(ctx, buffers[0], &blob, (int)paramsSize);
            Probe.ZeroBuffer(ctx, buffers[1], (int)perRowSize);

            fixed (byte* code = spirv)
            {
                var smci = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)code,
                };
                Probe.Check(vk.CreateShaderModule(device, in smci, null, out module), "vkCreateShaderModule");
            }

            // Descriptor set layout: UBO@0, SSBO@100/200/201/202/203.
            var bindings = stackalloc DescriptorSetLayoutBinding[6]
            {
                Probe.LayoutBinding(0,                            DescriptorType.UniformBuffer),
                Probe.LayoutBinding((uint)RealKernelProbe.TShift, DescriptorType.StorageBuffer),   // gPerRow t0
                Probe.LayoutBinding((uint)RealKernelProbe.UShift,     DescriptorType.StorageBuffer), // gIter u0
                Probe.LayoutBinding((uint)RealKernelProbe.UShift + 1, DescriptorType.StorageBuffer), // gSmooth u1
                Probe.LayoutBinding((uint)RealKernelProbe.UShift + 2, DescriptorType.StorageBuffer), // gFinalZD u2
                Probe.LayoutBinding((uint)ColorBinding,               DescriptorType.StorageBuffer), // gColor u3
            };
            var dslci = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 6,
                PBindings = bindings,
            };
            Probe.Check(vk.CreateDescriptorSetLayout(device, in dslci, null, out dsl), "vkCreateDescriptorSetLayout");

            var dslLocal = dsl;
            var plci = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &dslLocal,
            };
            Probe.Check(vk.CreatePipelineLayout(device, in plci, null, out layout), "vkCreatePipelineLayout");

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
            Probe.Check(vk.CreateComputePipelines(device, default, 1, &cpci, null, &created), "vkCreateComputePipelines");
            pipeline = created;

            // Descriptor pool: 1 UBO + 5 SSBO in one set.
            var poolSizes = stackalloc DescriptorPoolSize[2]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
                new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 5 },
            };
            var dpci = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 2,
                PPoolSizes = poolSizes,
            };
            Probe.Check(vk.CreateDescriptorPool(device, in dpci, null, out pool), "vkCreateDescriptorPool");

            var dsai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &dslLocal,
            };
            Probe.Check(vk.AllocateDescriptorSets(device, in dsai, out DescriptorSet set), "vkAllocateDescriptorSets");

            var infos = stackalloc DescriptorBufferInfo[6];
            var writes = stackalloc WriteDescriptorSet[6];
            uint[] bindNums =
            {
                0, (uint)RealKernelProbe.TShift,
                (uint)RealKernelProbe.UShift, (uint)RealKernelProbe.UShift + 1,
                (uint)RealKernelProbe.UShift + 2, (uint)ColorBinding,
            };
            DescriptorType[] types =
            {
                DescriptorType.UniformBuffer, DescriptorType.StorageBuffer,
                DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
                DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            };
            for (int i = 0; i < 6; i++)
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
            vk.UpdateDescriptorSets(device, 6, writes, 0, null);

            var cmdPoolCi = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = ctx.ComputeQueueFamily,
            };
            Probe.Check(vk.CreateCommandPool(device, in cmdPoolCi, null, out cmdPool), "vkCreateCommandPool");

            var cbai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = cmdPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Probe.Check(vk.AllocateCommandBuffers(device, in cbai, out CommandBuffer cmd), "vkAllocateCommandBuffers");

            var begin = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Probe.Check(vk.BeginCommandBuffer(cmd, in begin), "vkBeginCommandBuffer");
            vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
            vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, layout, 0, 1, &set, 0, null);
            vk.CmdDispatch(cmd, (uint)((W + 7) / 8), (uint)((H + 7) / 8), 1);
            Probe.Check(vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");

            var cmdLocal = cmd;
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmdLocal,
            };
            Probe.Check(vk.QueueSubmit(ctx.ComputeQueue, 1, &submit, default), "vkQueueSubmit");
            Probe.Check(vk.QueueWaitIdle(ctx.ComputeQueue), "vkQueueWaitIdle");

            color = Probe.ReadBuffer<uint>(ctx, buffers[5], n);
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
            for (int i = 0; i < buffers.Length; i++) Probe.FreeBuffer(ctx, buffers[i]);
        }
    }

    /// <summary>SHA256 of the packed-BGRA colour buffer, lower-hex. Used to pin
    /// the deterministic CPU reference.</summary>
    public static string Digest(uint[] color)
    {
        var bytes = MemoryMarshal.AsBytes(color.AsSpan());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
