// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.Vulkan/VulkanPipelineCache.cs
//
// #1341 — a persisted VkPipelineCache per Vulkan kernel type (compute, relief,
// froxel). The SPIR-V is already cached on disk (#456, DxcCompiler); this caches
// the driver's next step, SPIR-V → GPU code, which vkCreateComputePipelines
// otherwise redoes every session for every kind, palette and group shape.
//
// The file lives next to the shader blobs (ShaderCache/vk-pipelines-<kernel>-
// <vendor>-<device>-<uuid>.bin), one per kernel type, so the three kernels (each
// on its own VulkanContext) never overwrite each other's pipelines. It is a pure
// accelerator, and a driver trusts whatever bytes it is given past the header,
// so the file is checked before the driver sees it:
//   - our envelope: magic, payload length and a SHA-256 of the payload (a
//     truncated or bit-flipped file is ignored);
//   - the Vulkan header (VkPipelineCacheHeaderVersionOne): header size and
//     version, vendor id, device id and pipelineCacheUUID must match this device
//     (a cache from another GPU or driver is ignored).
// Anything that fails starts an empty cache, which the next save replaces. Saved
// after every new pipeline (a process can end without disposing its kernels) and
// on dispose; skipped when the data hasn't changed. FF_NO_SHADER_CACHE=1
// (ShaderBlobCache.Disabled) turns loading and saving off.

using System;
using System.IO;
using System.Security.Cryptography;
using FracturingFog.Abstractions;
using Silk.NET.Vulkan;

namespace FracturingFog.Rendering.Vulkan;

/// <summary>#1341 — a disk-backed VkPipelineCache. See the file header.</summary>
public sealed unsafe class VulkanPipelineCache : IDisposable
{
    /// <summary>Process switch for A/B timing (probes): false = no load, no save,
    /// and the driver gets an empty cache; the SPIR-V blob cache is unaffected.
    /// FF_VK_PIPELINE_CACHE=0 sets it.</summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("FF_VK_PIPELINE_CACHE") != "0";

    /// <summary>What happened to the file at creation.</summary>
    public enum LoadState { Disabled, NoFile, Loaded, Rejected }

    private static readonly byte[] Magic = "FFVKPC01"u8.ToArray();
    private const int EnvelopeBytes = 8 + 4 + 32;    // magic, payload length, SHA-256
    private const int VkHeaderBytes = 16 + 16;       // 4 × uint32 + 16-byte UUID

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly uint _vendor, _deviceId;
    private readonly byte[] _uuid;
    private byte[]? _lastSaved;

    /// <summary>The cache to pass to vkCreateComputePipelines (0 if it couldn't be made).</summary>
    public PipelineCache Handle { get; private set; }

    public string FilePath { get; }
    public LoadState State { get; }

    /// <summary>Why the file was rejected (State == Rejected).</summary>
    public string? RejectReason { get; }

    /// <summary>Bytes handed to the driver at creation.</summary>
    public int LoadedBytes { get; }

    /// <summary>Test hook (per thread): the directory a kernel created on this thread
    /// keeps its cache file in, instead of the shared ShaderCache.</summary>
    [ThreadStatic] public static string? DirectoryForThread;

    /// <summary>The cache file for <paramref name="kernel"/> on this device.</summary>
    public static string PathFor(VulkanContext ctx, string kernel) => Path.Combine(DirectoryForThread ?? ShaderBlobCache.CacheDir,
        $"vk-pipelines-{kernel}-{ctx.VendorId:X4}-{ctx.DeviceId:X4}-{Convert.ToHexString(ctx.PipelineCacheUuid)}.bin");

    public VulkanPipelineCache(VulkanContext ctx, string kernel)
    {
        _vk = ctx.Vk;
        _device = ctx.Device;
        _vendor = ctx.VendorId;
        _deviceId = ctx.DeviceId;
        _uuid = ctx.PipelineCacheUuid;
        FilePath = PathFor(ctx, kernel);

        byte[]? payload = null;
        if (ShaderBlobCache.Disabled || !Enabled) State = LoadState.Disabled;
        else
        {
            byte[]? file = null;
            try { if (File.Exists(FilePath)) file = File.ReadAllBytes(FilePath); } catch { }
            if (file == null) State = LoadState.NoFile;
            else
            {
                payload = Unwrap(file, _vendor, _deviceId, _uuid, out string? reason);
                if (payload == null) { State = LoadState.Rejected; RejectReason = reason; }
                else { State = LoadState.Loaded; LoadedBytes = payload.Length; _lastSaved = payload; }
            }
        }

        fixed (byte* p = payload)
        {
            var ci = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)(payload?.Length ?? 0),
                PInitialData = payload == null ? null : p,
            };
            PipelineCache h;
            if (_vk.CreatePipelineCache(_device, in ci, null, &h) == Result.Success) Handle = h;
            else if (payload != null)
            {
                // The driver refused data that passed our checks: start empty instead.
                ci.InitialDataSize = 0; ci.PInitialData = null;
                if (_vk.CreatePipelineCache(_device, in ci, null, &h) == Result.Success) Handle = h;
                State = LoadState.Rejected; RejectReason = "the driver refused the data"; LoadedBytes = 0; _lastSaved = null;
            }
        }
    }

    /// <summary>Write the cache to disk if it changed since the last load / save.
    /// Best effort: a failure only means the next session compiles again.</summary>
    public void Save()
    {
        if (Handle.Handle == 0 || State == LoadState.Disabled || ShaderBlobCache.Disabled) return;
        try
        {
            nuint size = 0;
            if (_vk.GetPipelineCacheData(_device, Handle, &size, null) != Result.Success || size == 0) return;
            var data = new byte[(int)size];
            fixed (byte* p = data)
                if (_vk.GetPipelineCacheData(_device, Handle, &size, p) is not (Result.Success or Result.Incomplete)) return;
            if ((int)size != data.Length) Array.Resize(ref data, (int)size);
            if (_lastSaved != null && data.AsSpan().SequenceEqual(_lastSaved)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, Wrap(data));
            try { File.Move(tmp, FilePath, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { } return; }
            _lastSaved = data;
        }
        catch { }
    }

    public void Dispose()
    {
        if (Handle.Handle == 0) return;
        Save();
        _vk.DestroyPipelineCache(_device, Handle, null);
        Handle = default;
    }

    // ── the file format ──────────────────────────────────────────────────────

    /// <summary>The file bytes for a driver <paramref name="payload"/>.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> payload)
    {
        var file = new byte[EnvelopeBytes + payload.Length];
        Magic.CopyTo(file, 0);
        BitConverter.TryWriteBytes(file.AsSpan(8, 4), payload.Length);
        SHA256.HashData(payload, file.AsSpan(12, 32));
        payload.CopyTo(file.AsSpan(EnvelopeBytes));
        return file;
    }

    /// <summary>The driver payload of <paramref name="file"/> when it is intact and
    /// was made by this device (vendor, device and pipelineCacheUUID); otherwise
    /// null and the <paramref name="reason"/>.</summary>
    public static byte[]? Unwrap(ReadOnlySpan<byte> file, uint vendorId, uint deviceId, ReadOnlySpan<byte> uuid, out string? reason)
    {
        reason = null;
        if (file.Length < EnvelopeBytes + VkHeaderBytes) { reason = "too short"; return null; }
        if (!file[..8].SequenceEqual(Magic)) { reason = "not a pipeline-cache file"; return null; }
        int len = BitConverter.ToInt32(file.Slice(8, 4));
        if (len != file.Length - EnvelopeBytes) { reason = "truncated or padded"; return null; }
        var payload = file[EnvelopeBytes..];
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        if (!hash.SequenceEqual(file.Slice(12, 32))) { reason = "checksum mismatch"; return null; }

        uint headerSize = BitConverter.ToUInt32(payload[..4]);
        uint version = BitConverter.ToUInt32(payload.Slice(4, 4));
        if (headerSize < VkHeaderBytes || headerSize > payload.Length) { reason = $"bad header size {headerSize}"; return null; }
        if (version != (uint)PipelineCacheHeaderVersion.One) { reason = $"header version {version}"; return null; }
        if (BitConverter.ToUInt32(payload.Slice(8, 4)) != vendorId) { reason = "another vendor"; return null; }
        if (BitConverter.ToUInt32(payload.Slice(12, 4)) != deviceId) { reason = "another device"; return null; }
        if (!payload.Slice(16, 16).SequenceEqual(uuid)) { reason = "another driver (pipelineCacheUUID)"; return null; }
        return payload.ToArray();
    }
}
