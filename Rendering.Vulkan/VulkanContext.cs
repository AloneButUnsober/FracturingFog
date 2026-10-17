// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Minimal headless Vulkan context for the V0 compute spike (issue #39).
//
// Creates an instance (no WSI/surface/swapchain), enumerates physical
// devices, picks one with a compute-capable queue family, and stands up a
// logical device + compute queue. Deliberately the smallest surface that lets
// a compute pipeline run and read memory back — mirrors the role of the ILGPU
// Context in Compute.Smoke.

using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace FracturingFog.Rendering.Vulkan;

public sealed unsafe class VulkanContext : IDisposable
{
    public readonly record struct DeviceInfo(string Name, PhysicalDeviceType Type, bool HasCompute);

    public Vk Vk { get; }
    public Instance Instance { get; private set; }
    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public Queue ComputeQueue { get; private set; }
    public uint ComputeQueueFamily { get; private set; }
    public string PickedName { get; private set; } = "<none>";
    public PhysicalDeviceType PickedType { get; private set; }

    // V6 spike (#82): true if the picked device advertised shaderFloat64 and we
    // enabled it on the logical device. The FP64 perturbation kernel needs it;
    // the FP32 base/colour kernels ignore it. False on parts without hardware
    // (or driver-exposed) doubles — the perturbation probe reports SKIP then.
    public bool SupportsFloat64 { get; private set; }

    /// <summary>#1329 — nanoseconds per timestamp tick (VkPhysicalDeviceLimits.timestampPeriod).</summary>
    public float TimestampPeriod { get; private set; }

    /// <summary>#1329 — valid bits of the compute queue family's timestamps; 0 = no timestamps.</summary>
    public uint TimestampValidBits { get; private set; }

    /// <summary>#1334 — VkPhysicalDeviceLimits.minUniformBufferOffsetAlignment: the
    /// stride of the per-band UBO slices a banded dispatch binds by dynamic offset.</summary>
    public ulong MinUniformBufferOffsetAlignment { get; private set; } = 256;

    /// <summary>#1339 — the picked device's PCI vendor / device ids and driver version
    /// (they key the tuned thread-group shapes).</summary>
    public uint VendorId { get; private set; }
    public uint DeviceId { get; private set; }
    public uint DriverVersion { get; private set; }

    /// <summary>#1341 — VkPhysicalDeviceProperties.pipelineCacheUUID: a pipeline
    /// cache blob is only valid for the device + driver that report this UUID.</summary>
    public byte[] PipelineCacheUuid { get; private set; } = new byte[16];

    /// <summary>#1339 — the compute work-group limits (the tuner skips shapes past them).</summary>
    public uint MaxComputeWorkGroupInvocations { get; private set; } = 128;
    public uint MaxComputeWorkGroupSizeX { get; private set; } = 128;
    public uint MaxComputeWorkGroupSizeY { get; private set; } = 128;

    private VulkanContext(Vk vk) => Vk = vk;

    // Stand up instance only. Cheap enough that --list can run without ever
    // creating a logical device.
    public static VulkanContext CreateInstance()
    {
        var vk = Vk.GetApi();
        var ctx = new VulkanContext(vk);

        var appName = (byte*)SilkMarshal.StringToPtr("FracturingFog.Rendering.Vulkan.Smoke");
        var engName = (byte*)SilkMarshal.StringToPtr("FracturingFog");
        try
        {
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApplicationVersion = new Silk.NET.Core.Version32(0, 1, 0),
                PEngineName = engName,
                EngineVersion = new Silk.NET.Core.Version32(0, 1, 0),
                // 1.1 is universally available (incl. lavapipe); compute needs
                // nothing newer.
                ApiVersion = Vk.Version11,
            };

            var ci = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = 0,
                PpEnabledExtensionNames = null,
                EnabledLayerCount = 0,
                PpEnabledLayerNames = null,
            };

            Instance instance;
            var r = vk.CreateInstance(in ci, null, &instance);
            if (r != Result.Success)
                throw new InvalidOperationException($"vkCreateInstance failed: {r}");
            ctx.Instance = instance;
        }
        finally
        {
            SilkMarshal.Free((nint)appName);
            SilkMarshal.Free((nint)engName);
        }
        return ctx;
    }

    // Enumerate all physical devices with their type + compute-capability, in
    // enumeration order. Used by --list and by the picker.
    public IReadOnlyList<DeviceInfo> EnumerateDevices()
    {
        var list = new List<DeviceInfo>();
        foreach (var pd in PhysicalDevices())
        {
            PhysicalDeviceProperties props;
            Vk.GetPhysicalDeviceProperties(pd, &props);
            string name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "<unknown>";
            list.Add(new DeviceInfo(name, props.DeviceType, HasComputeQueue(pd, out _, out _)));
        }
        return list;
    }

    // Pick the best compute-capable device and create the logical device +
    // compute queue on it. Preference: Discrete > Integrated > Virtual > CPU >
    // Other. lavapipe presents as a CPU device — accepted (software Vulkan is
    // exactly what CI runs on).
    public void CreateComputeDevice()
    {
        PhysicalDevice best = default;
        int bestRank = int.MinValue;
        string bestName = "<none>";
        PhysicalDeviceType bestType = default;
        uint bestFamily = 0, bestTsBits = 0;
        float bestTsPeriod = 0;
        ulong bestUboAlign = 256;
        PhysicalDeviceProperties bestProps = default;
        bool found = false;

        foreach (var pd in PhysicalDevices())
        {
            if (!HasComputeQueue(pd, out uint family, out uint tsBits)) continue;
            PhysicalDeviceProperties props;
            Vk.GetPhysicalDeviceProperties(pd, &props);
            int rank = RankType(props.DeviceType);
            if (rank > bestRank)
            {
                bestRank = rank;
                best = pd;
                bestFamily = family;
                bestTsBits = tsBits;
                bestTsPeriod = props.Limits.TimestampPeriod;
                bestUboAlign = Math.Max(1UL, props.Limits.MinUniformBufferOffsetAlignment);
                bestProps = props;
                bestType = props.DeviceType;
                bestName = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "<unknown>";
                found = true;
            }
        }

        if (!found)
            throw new InvalidOperationException("No Vulkan device with a compute queue family.");

        PhysicalDevice = best;
        ComputeQueueFamily = bestFamily;
        TimestampValidBits = bestTsBits;
        TimestampPeriod = bestTsPeriod;
        MinUniformBufferOffsetAlignment = bestUboAlign;
        VendorId = bestProps.VendorID;
        DeviceId = bestProps.DeviceID;
        DriverVersion = bestProps.DriverVersion;
        MaxComputeWorkGroupInvocations = bestProps.Limits.MaxComputeWorkGroupInvocations;
        MaxComputeWorkGroupSizeX = bestProps.Limits.MaxComputeWorkGroupSize[0];
        MaxComputeWorkGroupSizeY = bestProps.Limits.MaxComputeWorkGroupSize[1];
        var uuid = new byte[16];
        for (int i = 0; i < 16; i++) uuid[i] = bestProps.PipelineCacheUuid[i];
        PipelineCacheUuid = uuid;
        PickedName = bestName;
        PickedType = bestType;

        float priority = 1.0f;
        var qci = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = bestFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        // V6 spike (#82): opt into shaderFloat64 when the device supports it so a
        // `double` compute kernel (the perturbation δ loop) can run. Querying and
        // enabling a single supported feature is inert for the FP32 kernels.
        PhysicalDeviceFeatures supported;
        Vk.GetPhysicalDeviceFeatures(best, &supported);
        SupportsFloat64 = supported.ShaderFloat64;
        var enabled = new PhysicalDeviceFeatures { ShaderFloat64 = SupportsFloat64 };

        var dci = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &qci,
            EnabledExtensionCount = 0,
            EnabledLayerCount = 0,
            PEnabledFeatures = &enabled,
        };

        Device device;
        var r = Vk.CreateDevice(best, in dci, null, &device);
        if (r != Result.Success)
            throw new InvalidOperationException($"vkCreateDevice failed: {r}");
        Device = device;

        Vk.GetDeviceQueue(device, bestFamily, 0, out Queue queue);
        ComputeQueue = queue;
    }

    private PhysicalDevice[] PhysicalDevices()
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, ref count, null);
        var devices = new PhysicalDevice[count];
        if (count > 0)
            fixed (PhysicalDevice* p = devices)
                Vk.EnumeratePhysicalDevices(Instance, ref count, p);
        return devices;
    }

    private bool HasComputeQueue(PhysicalDevice pd, out uint family, out uint timestampValidBits)
    {
        family = 0;
        timestampValidBits = 0;
        uint qCount = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(pd, ref qCount, null);
        var qprops = new QueueFamilyProperties[qCount];
        if (qCount > 0)
            fixed (QueueFamilyProperties* qp = qprops)
                Vk.GetPhysicalDeviceQueueFamilyProperties(pd, ref qCount, qp);
        for (uint i = 0; i < qCount; i++)
        {
            if ((qprops[i].QueueFlags & QueueFlags.ComputeBit) != 0)
            {
                family = i;
                timestampValidBits = qprops[i].TimestampValidBits;
                return true;
            }
        }
        return false;
    }

    private static int RankType(PhysicalDeviceType t) => t switch
    {
        PhysicalDeviceType.DiscreteGpu => 4,
        PhysicalDeviceType.IntegratedGpu => 3,
        PhysicalDeviceType.VirtualGpu => 2,
        PhysicalDeviceType.Cpu => 1,
        _ => 0,
    };

    public void Dispose()
    {
        if (Device.Handle != 0)
        {
            Vk.DestroyDevice(Device, null);
            Device = default;
        }
        if (Instance.Handle != 0)
        {
            Vk.DestroyInstance(Instance, null);
            Instance = default;
        }
        Vk.Dispose();
    }
}
