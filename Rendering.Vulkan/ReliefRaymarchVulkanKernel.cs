// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ReliefRaymarchVulkanKernel.cs — Relief 3D Slice 3c (#161).
//
// Cross-platform Vulkan compute dispatch of the Relief 3D sphere-trace
// (ReliefRaymarchKernelSource, entry CSRelief) — the Vulkan twin of the
// Windows-only D3D ReliefRaymarchGpuKernel (#160 / 3b). Both compile the SAME
// dependency-free HLSL: FXC → cs_5_0 on D3D, DXC → cs_6_0 -spirv here. The HLSL
// carries no [[vk::binding]] attributes (they break FXC); bindings are pinned
// with the DXC -fvk-*-shift maps used by the rest of the Vulkan backend
// (b0→0, t0..t2→100..102, u0→200).
//
// Correctness is proven against the CPU parity twin
// ReliefRaymarchGpu.RenderCpuMirror by the --vulkanrelief smoke gate (runs on
// Mesa lavapipe in CI); the two share the ReliefUniforms cbuffer twin. Scope is
// the Slice-3 shader subset (flat three-light Lambert + ambient + gradient sky);
// full ShadingPipeline FX is Slice 4.
//
// Buffers — b0 = ReliefParams UBO (256 B, 16 float4 rows); t0 = height
// (StructuredBuffer<float>, one/cell); t1 = albedo (StructuredBuffer<uint>,
// packed ARGB, one/pixel); t2 = cull mask (StructuredBuffer<uint>, one/cell,
// 0 = culled — always bound; gHasKeep gates the read); u0 = packed-ARGB output.
//
// Memory is HOST_VISIBLE|HOST_COHERENT (direct map, no staging), matching
// VulkanComputeKernel. Thread-affine: a single caller drives Run from the calc
// thread. Not internally synchronised.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

using FracturingFog.Rendering.Lighting;   // ReliefUniforms

namespace FracturingFog.Rendering.Vulkan;

/// <summary>Vulkan compute dispatch of the Relief 3D raymarch kernel (#161).
/// See the file header for the buffer bindings and the parity contract.</summary>
public sealed unsafe class ReliefRaymarchVulkanKernel : IDisposable, IReliefRaymarchKernel
{
    // DXC register-class → binding shifts (same maps as VulkanComputeKernel).
    private const int BShift = 0;
    private const int TShift = 100;
    private const int UShift = 200;

    // ReliefParams UBO twin — 16 float4 rows (256 B). Every HLSL float3 is
    // followed by a scalar that fills its row and every float3 starts a fresh
    // row, so a flat sequential struct of 64 4-byte fields matches the cbuffer
    // byte-for-byte. Field order MUST track ReliefRaymarchKernelSource.Hlsl and
    // the D3D ReliefRaymarchGpuKernel.ReliefParamsBlob.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReliefParamsBlob
    {
        public int W, H, Hw, Hh;
        public float Sy, Aspect, InvLip; public int Ortho;
        public float CamX, CamY, CamZ, TanHalf;
        public float FwdX, FwdY, FwdZ, OrthoHalfV;
        public float RightX, RightY, RightZ, Eps0;
        public float UpX, UpY, UpZ, PixelAngle;
        public float Bx, By, Bz; public int MaxSteps;
        public int GroundPlane, ShowSky, Isolate, HasKeep;
        public float L0x, L0y, L0z, I0; public float C0r, C0g, C0b, Pad0;
        public float L1x, L1y, L1z, I1; public float C1r, C1g, C1b, Pad1;
        public float L2x, L2y, L2z, I2; public float C2r, C2g, C2b, Pad2;
        public float Ambient, FloorBx, FloorBz, Pad3;
        public uint BgTop, BgBottom, FloorAlbedo, DropColor;
        public float SpecStrength, Roughness, Metallic, PadS;   // 4a
        public int ShadowSteps; public float ShadowSoftK; public int ShadowMask; public float PadSh;   // 4b
        public int AoSamples; public float AoStrength; public float PadA0, PadA1;   // 4c
        public float IblStrength; public int SkyMode; public float TriplanarStrength, TriplanarScale;   // 4d
        public int TriplanarKind; public uint TriplanarTint; public float PadT0, PadT1;   // 4d
        public float FogDensity, FogHeightFalloff; public int VolumeSteps; public float VolumeStepsFalloff;   // 4e
        public int EmptySkip, MipW, MipH, MipBlk;   // 4f
        public int HasHdri, PadH0, PadH1, PadH2;   // 4d-ii
        public float ReflStrength; public int ReflSteps, MaxBounces, UseGgx;   // 4e-ii reflections
        public float VolNoiseAmount, VolNoiseScale, VolNoiseSpeed; public int VolNoiseOctaves;   // 4e-ii FBM
        public float VolSelfShadow; public int VolSelfShadowSteps; public float SceneTime, VolAnisotropy;   // 4e-ii + #184 Slice 3 (B)
        public uint FogColor; public float VolPaletteStrength; public int HasPalette, PaletteLen;   // #184 Slice 3 (C) + #185 slice D
    }

    private const int ParamBytes = 448;

    private struct Allocated { public Buffer Buffer; public DeviceMemory Memory; public ulong Size; }

    private readonly VulkanContext _ctx;
    private readonly bool _ownsContext;
    private readonly Vk _vk;
    private readonly Device _device;
    private CommandPool _cmdPool;

    private ShaderModule _module;
    private DescriptorSetLayout _dsl;
    private PipelineLayout _layout;
    private Pipeline _pipeline;

    private Allocated _params;
    private Allocated _height, _keep;       // field-sized (hn)
    private Allocated _mip;                 // t3 — 4f coarse max-height grid
    private Allocated _hdri;                // t4 — 4d-ii flattened HDRI env
    private Allocated _palette;             // t5 — #185 theme ramp LUT
    private Allocated _albedo, _color;      // output-sized (n)
    private int _fieldCells, _mipCells, _hdriFloats, _paletteLen, _outPixels;
    private bool _disposed;

    public double LastDispatchMs { get; private set; }
    public double LastReadbackMs { get; private set; }

    /// <summary>Human-readable backend label.</summary>
    public string Description => $"Vulkan relief ({_ctx.PickedType}: {_ctx.PickedName})";

    /// <param name="ctx">A context whose logical device is already created.</param>
    /// <param name="ownsContext">When true, <see cref="Dispose"/> also disposes
    /// <paramref name="ctx"/> (see <see cref="TryCreateWithOwnContext"/>).</param>
    public ReliefRaymarchVulkanKernel(VulkanContext ctx, bool ownsContext = false)
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

        BuildPipeline();
        _params = AllocBuffer(ParamBytes, BufferUsageFlags.UniformBufferBit);
        // t3 / t4 / t5 must always be bound (no partially-bound flag); start with
        // 1-cell stubs — the shader reads them only when gEmptySkip / gHasHdri /
        // gHasPalette != 0.
        EnsureMipBuffer(1);
        EnsureHdriBuffer(1);
        EnsurePaletteBuffer(1);
    }

    /// <summary>Create a self-contained kernel that owns a fresh
    /// <see cref="VulkanContext"/> (instance + compute device), or null when no
    /// Vulkan device is available or init fails. Never throws.</summary>
    public static ReliefRaymarchVulkanKernel? TryCreateWithOwnContext()
    {
        VulkanContext? ctx = null;
        try
        {
            ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) { ctx.Dispose(); return null; }
            ctx.CreateComputeDevice();
            return new ReliefRaymarchVulkanKernel(ctx, ownsContext: true);
        }
        catch
        {
            ctx?.Dispose();
            return null;
        }
    }

    /// <summary>Dispatch the relief raymarch for <paramref name="u"/> and read the
    /// packed-ARGB result into <paramref name="dst"/> (length ≥ W·H). Height field
    /// (<paramref name="hbuf"/>, W·H = Hw·Hh cells), optional cull mask
    /// (<paramref name="keep"/>) and albedo are uploaded each call. The Vulkan twin
    /// of <see cref="ReliefRaymarchGpu.RenderCpuMirror"/>.</summary>
    public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ReliefRaymarchVulkanKernel));
        int w = u.W, h = u.H, hn = u.Hw * u.Hh, n = w * h;
        if (w <= 0 || h <= 0) return;
        if (hbuf.Length < hn || albedo.Length < n || dst.Length < n)
            throw new ArgumentException("relief GPU input buffer too small for the uniforms");

        long t0 = Stopwatch.GetTimestamp();
        EnsureFieldBuffers(hn);
        EnsureOutputBuffers(n);

        // Uploads.
        fixed (float* p = hbuf) WriteBytes(_height, p, hn * sizeof(float));
        fixed (uint* p = albedo) WriteBytes(_albedo, p, n * sizeof(uint));
        UploadKeep(_keep, keep, hn);

        // 4f — build + upload the coarse max-height grid when the skip is on.
        if (u.EmptySkip != 0)
        {
            var mip = ReliefHeightMip.BuildMaxGrid(hbuf, u.Hw, u.Hh, u.MipBlk, out _, out _);
            EnsureMipBuffer(mip.Length);
            fixed (float* p = mip) WriteBytes(_mip, p, mip.Length * sizeof(float));
        }

        // 4d-ii — upload the flattened HDRI env when SkyMode == Hdri resolved.
        if (u.HdriBuf != null)
        {
            EnsureHdriBuffer(u.HdriBuf.Length);
            fixed (uint* p = u.HdriBuf) WriteBytes(_hdri, p, u.HdriBuf.Length * sizeof(uint));
        }

        // #185 — upload the theme ramp when the palette map is active.
        if (u.VolPaletteStrength > 0.0 && u.VolPalette != null && u.VolPalette.Length >= 2)
        {
            EnsurePaletteBuffer(u.VolPalette.Length);
            fixed (uint* p = u.VolPalette) WriteBytes(_palette, p, u.VolPalette.Length * sizeof(uint));
        }

        var blob = BuildBlob(in u, keep != null);
        WriteBytes(_params, &blob, sizeof(ReliefParamsBlob));

        // Per-Run descriptor pool + set (a resize reallocates buffers, so a stale
        // set could dangle — build fresh each dispatch, like VulkanComputeKernel).
        Buffer* srcBufs = stackalloc Buffer[8] { _params.Buffer, _height.Buffer, _albedo.Buffer, _keep.Buffer, _mip.Buffer, _hdri.Buffer, _palette.Buffer, _color.Buffer };
        uint* bindNums = stackalloc uint[8] { 0, (uint)TShift, (uint)TShift + 1, (uint)TShift + 2, (uint)TShift + 3, (uint)TShift + 4, (uint)TShift + 5, (uint)UShift };
        var types = stackalloc DescriptorType[8]
        {
            DescriptorType.UniformBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            DescriptorType.StorageBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
        };

        DescriptorPool pool = default;
        CommandBuffer cmd = default;
        try
        {
            var poolSizes = stackalloc DescriptorPoolSize[2]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
                new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 7 },
            };
            var dpci = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1, PoolSizeCount = 2, PPoolSizes = poolSizes,
            };
            Check(_vk.CreateDescriptorPool(_device, in dpci, null, out pool), "vkCreateDescriptorPool");

            var dslLocal = _dsl;
            var dsai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &dslLocal,
            };
            Check(_vk.AllocateDescriptorSets(_device, in dsai, out DescriptorSet set), "vkAllocateDescriptorSets");

            var infos = stackalloc DescriptorBufferInfo[8];
            var writes = stackalloc WriteDescriptorSet[8];
            for (int i = 0; i < 8; i++)
            {
                infos[i] = new DescriptorBufferInfo { Buffer = srcBufs[i], Offset = 0, Range = Vk.WholeSize };
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set, DstBinding = bindNums[i], DescriptorCount = 1,
                    DescriptorType = types[i], PBufferInfo = &infos[i],
                };
            }
            _vk.UpdateDescriptorSets(_device, 8, writes, 0, null);

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
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
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
        ReadUints(_color, dst, n);
        long tEnd = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
        LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
    }

    private static ReliefParamsBlob BuildBlob(in ReliefUniforms u, bool hasKeep)
    {
        var c = u.Cam;
        return new ReliefParamsBlob
        {
            W = u.W, H = u.H, Hw = u.Hw, Hh = u.Hh,
            Sy = (float)u.Sy, Aspect = (float)u.Aspect, InvLip = (float)u.InvLip,
            Ortho = c.Ortho ? 1 : 0,
            CamX = (float)c.CamX, CamY = (float)c.CamY, CamZ = (float)c.CamZ, TanHalf = (float)c.TanHalf,
            FwdX = (float)c.FX, FwdY = (float)c.FY, FwdZ = (float)c.FZ, OrthoHalfV = (float)c.OrthoHalfV,
            RightX = (float)c.RX, RightY = 0f, RightZ = (float)c.RZ, Eps0 = (float)c.Eps0,
            UpX = (float)c.UX, UpY = (float)c.UY, UpZ = (float)c.UZ, PixelAngle = (float)c.PixelAngle,
            Bx = (float)c.Bx, By = (float)c.By, Bz = (float)c.Bz, MaxSteps = c.MaxSteps,
            GroundPlane = c.GroundPlane ? 1 : 0,
            ShowSky = u.ShowSky ? 1 : 0, Isolate = u.Isolate ? 1 : 0, HasKeep = hasKeep ? 1 : 0,
            L0x = (float)u.L0x, L0y = (float)u.L0y, L0z = (float)u.L0z, I0 = (float)u.I0,
            C0r = (float)u.C0r, C0g = (float)u.C0g, C0b = (float)u.C0b, Pad0 = 0f,
            L1x = (float)u.L1x, L1y = (float)u.L1y, L1z = (float)u.L1z, I1 = (float)u.I1,
            C1r = (float)u.C1r, C1g = (float)u.C1g, C1b = (float)u.C1b, Pad1 = 0f,
            L2x = (float)u.L2x, L2y = (float)u.L2y, L2z = (float)u.L2z, I2 = (float)u.I2,
            C2r = (float)u.C2r, C2g = (float)u.C2g, C2b = (float)u.C2b, Pad2 = 0f,
            Ambient = (float)u.Ambient, FloorBx = (float)c.FloorBx, FloorBz = (float)c.FloorBz, Pad3 = 0f,
            BgTop = u.BgTop, BgBottom = u.BgBottom, FloorAlbedo = u.FloorAlbedo, DropColor = u.DropColor,
            SpecStrength = (float)u.SpecStrength, Roughness = (float)u.Roughness, Metallic = (float)u.Metallic, PadS = 0f,
            ShadowSteps = u.ShadowSteps, ShadowSoftK = (float)u.ShadowSoftK, ShadowMask = u.ShadowLightMask, PadSh = 0f,
            AoSamples = u.AoSamples, AoStrength = (float)u.AoStrength, PadA0 = 0f, PadA1 = 0f,
            IblStrength = (float)u.IblStrength, SkyMode = u.SkyMode,
            TriplanarStrength = (float)u.TriplanarStrength, TriplanarScale = (float)u.TriplanarScale,
            TriplanarKind = u.TriplanarKind, TriplanarTint = u.TriplanarTint, PadT0 = 0f, PadT1 = 0f,
            FogDensity = (float)u.FogDensity, FogHeightFalloff = (float)u.FogHeightFalloff,
            VolumeSteps = u.VolumeSteps, VolumeStepsFalloff = (float)u.VolumeStepsFalloff,
            EmptySkip = u.EmptySkip, MipW = u.MipW, MipH = u.MipH, MipBlk = u.MipBlk,
            HasHdri = u.HdriBuf != null ? 1 : 0,
            ReflStrength = (float)u.ReflectionStrength, ReflSteps = u.ReflectionSteps,
            MaxBounces = u.MaxBounces, UseGgx = u.UseGgxSampling ? 1 : 0,
            VolNoiseAmount = (float)u.VolumeNoiseAmount, VolNoiseScale = (float)u.VolumeNoiseScale,
            VolNoiseSpeed = (float)u.VolumeNoiseSpeed, VolNoiseOctaves = u.VolumeNoiseOctaves,
            VolSelfShadow = (float)u.VolumeSelfShadow, VolSelfShadowSteps = u.VolumeSelfShadowSteps,
            SceneTime = (float)u.SceneTime, VolAnisotropy = (float)u.VolAnisotropy,
            FogColor = u.FogColor,
            VolPaletteStrength = (float)u.VolPaletteStrength,
            HasPalette = (u.VolPaletteStrength > 0.0 && u.VolPalette != null && u.VolPalette.Length >= 2) ? 1 : 0,
            PaletteLen = u.VolPalette?.Length ?? 0,
        };
    }

    // ── pipeline ────────────────────────────────────────────────────────────────
    private void BuildPipeline()
    {
        byte[] spirv = DxcCompiler.CompileToSpirv(
            ReliefRaymarchKernelSource.Build(), ReliefRaymarchKernelSource.EntryPoint, "cs_6_0",
            "-fvk-b-shift", BShift.ToString(), "0",
            "-fvk-t-shift", TShift.ToString(), "0",
            "-fvk-u-shift", UShift.ToString(), "0");

        fixed (byte* code = spirv)
        {
            var smci = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length, PCode = (uint*)code,
            };
            Check(_vk.CreateShaderModule(_device, in smci, null, out _module), "vkCreateShaderModule");
        }

        // b0 UBO + t0/t1/t2/t3/t4/t5 + u0 SSBOs (t3 = 4f mip grid, t4 = 4d-ii HDRI
        // env, t5 = #185 theme ramp).
        uint* bindNums = stackalloc uint[8] { 0, (uint)TShift, (uint)TShift + 1, (uint)TShift + 2, (uint)TShift + 3, (uint)TShift + 4, (uint)TShift + 5, (uint)UShift };
        var bindings = stackalloc DescriptorSetLayoutBinding[8];
        for (int i = 0; i < 8; i++)
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = bindNums[i],
                DescriptorType = i == 0 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit,
            };
        var dslci = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 8, PBindings = bindings,
        };
        Check(_vk.CreateDescriptorSetLayout(_device, in dslci, null, out _dsl), "vkCreateDescriptorSetLayout");

        var dslLocal = _dsl;
        var plci = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1, PSetLayouts = &dslLocal,
        };
        Check(_vk.CreatePipelineLayout(_device, in plci, null, out _layout), "vkCreatePipelineLayout");

        nint entryPtr = SilkMarshal.StringToPtr(ReliefRaymarchKernelSource.EntryPoint);
        try
        {
            var cpci = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = _module, PName = (byte*)entryPtr,
                },
                Layout = _layout,
            };
            Pipeline created;
            Check(_vk.CreateComputePipelines(_device, default, 1, &cpci, null, &created), "vkCreateComputePipelines");
            _pipeline = created;
        }
        finally { SilkMarshal.Free(entryPtr); }
    }

    // ── buffers ───────────────────────────────────────────────────────────────
    private void EnsureFieldBuffers(int cells)
    {
        if (_height.Buffer.Handle != 0 && _fieldCells == cells) return;
        FreeBuffer(ref _height);
        FreeBuffer(ref _keep);
        _height = AllocBuffer((ulong)(cells * sizeof(float)), BufferUsageFlags.StorageBufferBit);
        _keep = AllocBuffer((ulong)(cells * sizeof(uint)), BufferUsageFlags.StorageBufferBit);
        _fieldCells = cells;
    }

    // 4f — (re)allocate the coarse max-height grid buffer (t3) to `cells` floats.
    private void EnsureMipBuffer(int cells)
    {
        if (cells < 1) cells = 1;
        if (_mip.Buffer.Handle != 0 && _mipCells == cells) return;
        FreeBuffer(ref _mip);
        _mip = AllocBuffer((ulong)(cells * sizeof(float)), BufferUsageFlags.StorageBufferBit);
        _mipCells = cells;
    }

    // 4d-ii — (re)allocate the flattened-HDRI buffer (t4) to `count` uints.
    private void EnsureHdriBuffer(int count)
    {
        if (count < 1) count = 1;
        if (_hdri.Buffer.Handle != 0 && _hdriFloats == count) return;
        FreeBuffer(ref _hdri);
        _hdri = AllocBuffer((ulong)(count * sizeof(uint)), BufferUsageFlags.StorageBufferBit);
        _hdriFloats = count;
    }

    // #185 — (re)allocate the theme-ramp buffer (t5) to `count` packed-ARGB uints.
    private void EnsurePaletteBuffer(int count)
    {
        if (count < 1) count = 1;
        if (_palette.Buffer.Handle != 0 && _paletteLen == count) return;
        FreeBuffer(ref _palette);
        _palette = AllocBuffer((ulong)(count * sizeof(uint)), BufferUsageFlags.StorageBufferBit);
        _paletteLen = count;
    }

    private void EnsureOutputBuffers(int n)
    {
        if (_color.Buffer.Handle != 0 && _outPixels == n) return;
        FreeBuffer(ref _albedo);
        FreeBuffer(ref _color);
        _albedo = AllocBuffer((ulong)(n * sizeof(uint)), BufferUsageFlags.StorageBufferBit);
        _color = AllocBuffer((ulong)(n * sizeof(uint)), BufferUsageFlags.StorageBufferBit);
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

    // keep byte[] (0/1) → uint per cell. Null → all 1 (gHasKeep gates the read
    // anyway; a valid buffer stays bound so t2 is never dangling).
    private void UploadKeep(Allocated a, byte[]? keep, int count)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, a.Memory, 0, (ulong)(count * sizeof(uint)), 0, &mapped), "vkMapMemory");
        uint* dst = (uint*)mapped;
        if (keep != null) { for (int i = 0; i < count; i++) dst[i] = keep[i]; }
        else { for (int i = 0; i < count; i++) dst[i] = 1u; }
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
        if (_pipeline.Handle != 0) _vk.DestroyPipeline(_device, _pipeline, null);
        if (_layout.Handle != 0) _vk.DestroyPipelineLayout(_device, _layout, null);
        if (_dsl.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _dsl, null);
        if (_module.Handle != 0) _vk.DestroyShaderModule(_device, _module, null);
        FreeBuffer(ref _params);
        FreeBuffer(ref _height);
        FreeBuffer(ref _keep);
        FreeBuffer(ref _mip);
        FreeBuffer(ref _hdri);
        FreeBuffer(ref _palette);
        FreeBuffer(ref _albedo);
        FreeBuffer(ref _color);
        if (_cmdPool.Handle != 0) { _vk.DestroyCommandPool(_device, _cmdPool, null); _cmdPool = default; }
        if (_ownsContext) { try { _ctx.Dispose(); } catch { } }
    }
}
