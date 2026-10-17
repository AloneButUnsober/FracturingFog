// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1341 — the persisted VkPipelineCache (Rendering.Vulkan/VulkanPipelineCache.cs).
// Checked:
//   • the file format, without a device: a wrapped payload unwraps to the same
//     bytes for the device that made it; a file that is short, not ours,
//     truncated or padded, or has any payload byte flipped (checksum) is refused;
//     so is a payload whose Vulkan header has a bad size or version or names
//     another vendor, device or pipelineCacheUUID, each for its own reason;
//   • on whatever Vulkan device the host has (skipped without one), in a
//     directory of its own:
//       - a first kernel finds no file and writes one as soon as it builds a
//         pipeline (before it is disposed), valid for this device;
//       - the next kernel loads it (the whole payload) and renders the same bytes;
//       - a corrupted file is refused, the kernel still renders the same bytes,
//         and it writes a good file again;
//       - the compute, relief and froxel kernels keep separate files;
//       - switched off (FF_NO_SHADER_CACHE / FF_VK_PIPELINE_CACHE=0), nothing is
//         read or written.

using System;
using System.IO;
using System.Linq;
using FracturingFog.Abstractions;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1341PipelineCacheFormatTests
{
    private const uint Vendor = 0x10DE, Device = 0x128B;
    private static readonly byte[] Uuid = Enumerable.Range(1, 16).Select(i => (byte)(i * 17)).ToArray();

    /// <summary>A payload laid out like a driver's: VkPipelineCacheHeaderVersionOne, then data.</summary>
    private static byte[] Payload(uint headerSize = 32, uint version = 1, uint vendor = Vendor, uint device = Device,
        byte[]? uuid = null, int body = 300)
    {
        var p = new byte[32 + body];
        BitConverter.TryWriteBytes(p.AsSpan(0, 4), headerSize);
        BitConverter.TryWriteBytes(p.AsSpan(4, 4), version);
        BitConverter.TryWriteBytes(p.AsSpan(8, 4), vendor);
        BitConverter.TryWriteBytes(p.AsSpan(12, 4), device);
        (uuid ?? Uuid).CopyTo(p, 16);
        for (int i = 32; i < p.Length; i++) p[i] = (byte)(i * 31 + 7);
        return p;
    }

    private static byte[]? Unwrap(byte[] file, out string? reason) => VulkanPipelineCache.Unwrap(file, Vendor, Device, Uuid, out reason);

    [Fact]
    public void A_Wrapped_Payload_Unwraps_To_The_Same_Bytes()
    {
        var payload = Payload();
        var file = VulkanPipelineCache.Wrap(payload);
        Assert.Equal(payload, Unwrap(file, out string? reason));
        Assert.Null(reason);
        Assert.Equal(payload.Length + 44, file.Length);
    }

    [Fact]
    public void Every_Flipped_Payload_Byte_Is_Caught()
    {
        var file = VulkanPipelineCache.Wrap(Payload());
        for (int i = 44 + 32; i < file.Length; i++)   // the body (header bytes have their own checks too)
        {
            var bad = (byte[])file.Clone();
            bad[i] ^= 0x01;
            Assert.Null(Unwrap(bad, out string? reason));
            Assert.Equal("checksum mismatch", reason);
        }
        var badHash = (byte[])file.Clone();
        badHash[20] ^= 0x80;                           // the stored hash itself
        Assert.Null(Unwrap(badHash, out string? r2));
        Assert.Equal("checksum mismatch", r2);
    }

    [Fact]
    public void A_File_That_Is_Not_Whole_Or_Not_Ours_Is_Refused()
    {
        var file = VulkanPipelineCache.Wrap(Payload());
        Assert.Null(Unwrap(file[..^1], out string? r));
        Assert.Equal("truncated or padded", r);
        Assert.Null(Unwrap([.. file, 0], out r));
        Assert.Equal("truncated or padded", r);
        Assert.Null(Unwrap(file[..40], out r));
        Assert.Equal("too short", r);
        var notOurs = (byte[])file.Clone();
        notOurs[0] = (byte)'X';
        Assert.Null(Unwrap(notOurs, out r));
        Assert.Equal("not a pipeline-cache file", r);
        Assert.Null(Unwrap(Payload(), out r));        // a bare driver blob, no envelope
        Assert.Equal("not a pipeline-cache file", r);
    }

    [Theory]
    [InlineData("size", "bad header size 16")]
    [InlineData("hugesize", "bad header size 4000")]
    [InlineData("version", "header version 2")]
    [InlineData("vendor", "another vendor")]
    [InlineData("device", "another device")]
    [InlineData("uuid", "another driver (pipelineCacheUUID)")]
    public void A_Payload_From_Another_Device_Or_Driver_Is_Refused(string what, string expected)
    {
        var otherUuid = (byte[])Uuid.Clone();
        otherUuid[15] ^= 1;
        var payload = what switch
        {
            "size" => Payload(headerSize: 16),
            "hugesize" => Payload(headerSize: 4000),
            "version" => Payload(version: 2),
            "vendor" => Payload(vendor: 0x1002),
            "device" => Payload(device: 0x128C),
            _ => Payload(uuid: otherUuid),
        };
        Assert.Null(Unwrap(VulkanPipelineCache.Wrap(payload), out string? reason));
        Assert.Equal(expected, reason);
    }
}

[Collection(MandelbrotGpuStaticsCollection.Name)]   // flips ShaderBlobCache.Disabled / VulkanPipelineCache.Enabled
public sealed class S1341VulkanPipelineCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FracturingFog.Tests.vkpc", Guid.NewGuid().ToString("N"));
    private readonly bool _savedEnabled = VulkanPipelineCache.Enabled, _savedDisabled = ShaderBlobCache.Disabled;

    public S1341VulkanPipelineCacheTests() => VulkanPipelineCache.DirectoryForThread = _dir;

    public void Dispose()
    {
        VulkanPipelineCache.DirectoryForThread = null;
        VulkanPipelineCache.Enabled = _savedEnabled;
        ShaderBlobCache.Disabled = _savedDisabled;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static VulkanComputeKernel Kernel()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        return k;
    }

    private const int W = 64, H = 48, N = W * H;

    /// <summary>Two pipelines' worth of frames (Mandelbrot and Burning Ship), all outputs.</summary>
    private static byte[] Frames(VulkanComputeKernel k)
    {
        var all = new System.Collections.Generic.List<byte>();
        foreach (var kind in new[] { FractalKind.Mandelbrot, FractalKind.BurningShip })
        {
            var it = new int[N]; var sm = new float[N]; var zr = new float[N]; var zi = new float[N]; var dr = new float[N]; var di = new float[N];
            k.Run(W, H, -0.5, 0, 3.0 / W, 300, 4.0, it, sm, zr, zi, dr, di, kind: kind);
            foreach (Array a in new Array[] { it, sm, zr, zi, dr, di })
            {
                var b = new byte[Buffer.ByteLength(a)];
                Buffer.BlockCopy(a, 0, b, 0, b.Length);
                all.AddRange(b);
            }
        }
        return all.ToArray();
    }

    private int _emptyPayload = -1;

    /// <summary>The payload an empty cache saves on this device (the driver writes
    /// more than the 32-byte header even with no pipelines in it).</summary>
    private int EmptyPayload()
    {
        if (_emptyPayload >= 0) return _emptyPayload;
        string saved = VulkanPipelineCache.DirectoryForThread!;
        VulkanPipelineCache.DirectoryForThread = Path.Combine(_dir, "empty");
        try
        {
            using var ctx = VulkanContext.CreateInstance();
            ctx.CreateComputeDevice();
            string path;
            using (var c = new VulkanPipelineCache(ctx, "empty")) { path = c.FilePath; c.Save(); }
            var payload = VulkanPipelineCache.Unwrap(File.ReadAllBytes(path), ctx.VendorId, ctx.DeviceId, ctx.PipelineCacheUuid, out _);
            return _emptyPayload = payload!.Length;
        }
        finally { VulkanPipelineCache.DirectoryForThread = saved; }
    }

    /// <summary>The file's payload if it is valid for this device; also checks the
    /// driver put pipelines in it (more than an empty cache holds), which it only
    /// does when the kernel passed the cache to vkCreateComputePipelines.</summary>
    private byte[]? ValidPayload(string path, out string? reason)
    {
        // The device's own ids, from a context made for that purpose.
        using var ctx = VulkanContext.CreateInstance();
        ctx.CreateComputeDevice();
        var payload = VulkanPipelineCache.Unwrap(File.ReadAllBytes(path), ctx.VendorId, ctx.DeviceId, ctx.PipelineCacheUuid, out reason);
        if (payload != null)
            Assert.True(payload.Length > EmptyPayload(),
                $"{Path.GetFileName(path)}: no pipelines in the cache ({payload.Length} bytes, an empty one has {EmptyPayload()})");
        return payload;
    }

    [Fact]
    public void A_Saved_Cache_Is_Loaded_By_The_Next_Kernel_And_Renders_The_Same()
    {
        byte[] first, second;
        string path;
        using (var k = Kernel())
        {
            Assert.Equal(VulkanPipelineCache.LoadState.NoFile, k.PipelineCache.State);
            path = k.PipelineCache.FilePath;
            Assert.StartsWith(_dir, path);
            Assert.False(File.Exists(path));
            first = Frames(k);
            Assert.True(k.PipelinesCreated >= 2, "precondition: pipelines were built");
            Assert.True(File.Exists(path), "not written through after the pipelines were built");
            Assert.NotNull(ValidPayload(path, out string? reason));
            Assert.Null(reason);
        }

        int payloadBytes;
        using (var k = Kernel())
        {
            payloadBytes = ValidPayload(path, out _)!.Length;
            Assert.Equal(VulkanPipelineCache.LoadState.Loaded, k.PipelineCache.State);
            Assert.Equal(payloadBytes, k.PipelineCache.LoadedBytes);
            second = Frames(k);
        }
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_Corrupted_Cache_Is_Refused_The_Frames_Stay_The_Same_And_A_Good_File_Is_Written()
    {
        byte[] reference;
        string path;
        using (var k = Kernel()) { reference = Frames(k); path = k.PipelineCache.FilePath; }

        var bytes = File.ReadAllBytes(path);
        bytes[^9] ^= 0x40;
        File.WriteAllBytes(path, bytes);
        using (var k = Kernel())
        {
            Assert.Equal(VulkanPipelineCache.LoadState.Rejected, k.PipelineCache.State);
            Assert.Equal("checksum mismatch", k.PipelineCache.RejectReason);
            Assert.Equal(0, k.PipelineCache.LoadedBytes);
            Assert.Equal(reference, Frames(k));
        }
        using (var k = Kernel())
        {
            Assert.Equal(VulkanPipelineCache.LoadState.Loaded, k.PipelineCache.State);   // rewritten whole
            Assert.Equal(reference, Frames(k));
        }
    }

    [Fact]
    public void Each_Kernel_Type_Keeps_Its_Own_File()
    {
        string compute, relief, froxel;
        using (var k = Kernel()) { Frames(k); compute = k.PipelineCache.FilePath; }
        using (var r = ReliefRaymarchVulkanKernel.TryCreateWithOwnContext())
        {
            if (r is null) Assert.Skip("no Vulkan relief kernel");
            Assert.True(r.PipelinesCreated >= 1);
            relief = r.PipelineCache.FilePath;
        }
        using (var f = FroxelVolumeVulkanKernel.TryCreateWithOwnContext())
        {
            if (f is null) Assert.Skip("no Vulkan froxel kernel");
            Assert.True(f.PipelinesCreated >= 1);
            froxel = f.PipelineCache.FilePath;
        }
        Assert.Equal(3, new[] { compute, relief, froxel }.Distinct().Count());
        Assert.All(new[] { compute, relief, froxel }, p => Assert.NotNull(ValidPayload(p, out _)));
        Assert.Contains("-compute-", Path.GetFileName(compute));
        Assert.Contains("-relief-", Path.GetFileName(relief));
        Assert.Contains("-froxel-", Path.GetFileName(froxel));

        using (var r = ReliefRaymarchVulkanKernel.TryCreateWithOwnContext())
            Assert.Equal(VulkanPipelineCache.LoadState.Loaded, r!.PipelineCache.State);
    }

    [Theory]
    [InlineData(true)]    // FF_NO_SHADER_CACHE
    [InlineData(false)]   // FF_VK_PIPELINE_CACHE=0
    public void Switched_Off_Nothing_Is_Read_Or_Written(bool blobCacheOff)
    {
        string path;
        using (var k = Kernel()) { Frames(k); path = k.PipelineCache.FilePath; }
        Assert.True(File.Exists(path));
        File.Delete(path);

        if (blobCacheOff) ShaderBlobCache.Disabled = true; else VulkanPipelineCache.Enabled = false;
        using (var k = Kernel())
        {
            Assert.Equal(VulkanPipelineCache.LoadState.Disabled, k.PipelineCache.State);
            Frames(k);
            Assert.True(k.PipelinesCreated >= 2);
        }
        Assert.False(File.Exists(path), "a disabled cache wrote its file");
    }
}
