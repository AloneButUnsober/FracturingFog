// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1455 — Linux computed everything on the CPU: no compute kernel without
// --renderer vulkan, a Vulkan kernel that attached without a DXC to compile its
// shaders, and a CPU Vulkan driver (llvmpipe) accepted as the "GPU". Locks:
//   • GpuComputeSelection.Decide, branch by branch: Windows' D3D hook decides
//     nothing; Linux's default (Auto) and an explicit --renderer vulkan want
//     Vulkan, silk / skia don't; it needs a non-CPU device with compute (a CPU
//     driver only with FF_VULKAN_ALLOW_CPU) and a dxc that runs (only probed
//     when a device qualifies); the best device by the picker's own ranking is
//     named; every "off" says why;
//   • DxcCompiler's lookup order: DXC_PATH, the Vulkan SDK, the copy bundled
//     next to the app (dxc/bin/dxc), then PATH;
//   • DxcCompiler.Probe runs whatever the lookup finds (here: the SDK's dxc).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FracturingFog.Hosting;
using FracturingFog.Rendering.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1455LinuxGpuComputeTests
{
    private static VulkanContext.DeviceInfo Dev(string name, PhysicalDeviceType t, bool compute = true) => new(name, t, compute);

    private static readonly VulkanContext.DeviceInfo Gt710 = Dev("NVIDIA GeForce GT 710", PhysicalDeviceType.DiscreteGpu);
    private static readonly VulkanContext.DeviceInfo Igpu = Dev("Intel(R) UHD Graphics 630", PhysicalDeviceType.IntegratedGpu);
    private static readonly VulkanContext.DeviceInfo Llvmpipe = Dev("llvmpipe (LLVM 21.1.8, 256 bits)", PhysicalDeviceType.Cpu);

    private static (bool, string) DxcOk() => (true, "/opt/ff/dxc/bin/dxc (dxcompiler.dll: 1.9)");

    private static GpuComputeDecision Linux(IReadOnlyList<VulkanContext.DeviceInfo>? devs, bool dxc = true,
        RendererBackend backend = RendererBackend.Auto, bool allowCpu = false)
        => GpuComputeSelection.Decide(true, backend, false, devs,
            () => dxc ? DxcOk() : (false, "'dxc' not found (No such file or directory)"), allowCpu);

    [Fact]
    public void Linux_By_Default_Uses_Vulkan_On_A_Real_Gpu_And_Says_So()
    {
        var d = Linux(new[] { Gt710 });
        Assert.True(d.UseGpu);
        Assert.Equal(Gt710.Name, d.Device);
        Assert.Equal("GPU compute: Vulkan (NVIDIA GeForce GT 710)", d.Status);
    }

    [Fact]
    public void The_Best_Device_By_The_Pickers_Ranking_Is_Named_And_A_Cpu_Driver_Beside_A_Gpu_Is_Ignored()
    {
        Assert.Equal(Gt710.Name, Linux(new[] { Llvmpipe, Igpu, Gt710 }).Device);
        Assert.Equal(Igpu.Name, Linux(new[] { Llvmpipe, Igpu }).Device);
        // A device without a compute queue never counts.
        Assert.Equal(Igpu.Name, Linux(new[] { Dev("GPU without compute", PhysicalDeviceType.DiscreteGpu, compute: false), Igpu }).Device);
    }

    [Fact]
    public void A_Cpu_Vulkan_Driver_Alone_Is_Refused_Unless_Allowed()
    {
        var off = Linux(new[] { Llvmpipe });
        Assert.False(off.UseGpu);
        Assert.Null(off.Device);
        Assert.Contains("CPU driver", off.Status);
        Assert.Contains("llvmpipe", off.Status);

        var on = Linux(new[] { Llvmpipe }, allowCpu: true);
        Assert.True(on.UseGpu);
        Assert.Contains(GpuComputeSelection.AllowCpuEnv, on.Status);
    }

    [Fact]
    public void No_Driver_Or_No_Compute_Device_Is_Off_With_The_Reason()
    {
        var none = Linux(null);
        Assert.False(none.UseGpu);
        Assert.Contains("no Vulkan driver", none.Status);

        var empty = Linux(Array.Empty<VulkanContext.DeviceInfo>());
        Assert.False(empty.UseGpu);
        Assert.Contains("no Vulkan device", empty.Status);

        var noCompute = Linux(new[] { Dev("GPU", PhysicalDeviceType.DiscreteGpu, compute: false) });
        Assert.False(noCompute.UseGpu);
    }

    [Fact]
    public void Without_A_Working_Dxc_It_Is_Off_And_Says_So()
    {
        var d = Linux(new[] { Gt710 }, dxc: false);
        Assert.False(d.UseGpu);
        Assert.Contains("no DXC shader compiler", d.Status);
        Assert.Contains("not found", d.Status);
        Assert.Contains("DXC_PATH", d.Status);
    }

    [Fact]
    public void Dxc_Is_Only_Probed_When_A_Device_Qualifies()
    {
        int probes = 0;
        (bool, string) Count() { probes++; return DxcOk(); }
        GpuComputeSelection.Decide(true, RendererBackend.Auto, false, null, Count, false);
        GpuComputeSelection.Decide(true, RendererBackend.Auto, false, new[] { Llvmpipe }, Count, false);
        GpuComputeSelection.Decide(true, RendererBackend.Silk, false, new[] { Gt710 }, Count, false);
        GpuComputeSelection.Decide(false, RendererBackend.Auto, true, new[] { Gt710 }, Count, false);
        Assert.Equal(0, probes);
        GpuComputeSelection.Decide(true, RendererBackend.Auto, false, new[] { Gt710 }, Count, false);
        Assert.Equal(1, probes);
    }

    [Theory]
    [InlineData(RendererBackend.Silk, "silk")]
    [InlineData(RendererBackend.Skia, "skia")]
    public void Silk_Or_Skia_On_Linux_Keep_The_Cpu_And_Say_So(RendererBackend backend, string name)
    {
        var d = Linux(new[] { Gt710 }, backend: backend);
        Assert.False(d.UseGpu);
        Assert.Equal($"GPU compute off (--renderer {name})", d.Status);
    }

    [Fact]
    public void Explicit_Vulkan_Works_Anywhere_But_The_Default_Only_On_Linux()
    {
        // macOS / a Windows host without the D3D hook: Auto decides nothing, says nothing.
        var macAuto = GpuComputeSelection.Decide(false, RendererBackend.Auto, false, new[] { Gt710 }, DxcOk, false);
        Assert.False(macAuto.UseGpu);
        Assert.Null(macAuto.Status);
        var macVulkan = GpuComputeSelection.Decide(false, RendererBackend.Vulkan, false, new[] { Gt710 }, DxcOk, false);
        Assert.True(macVulkan.UseGpu);
        // --renderer vulkan with only a CPU driver is refused too.
        Assert.False(Linux(new[] { Llvmpipe }, backend: RendererBackend.Vulkan).UseGpu);
    }

    [Fact]
    public void Windows_D3d_Owns_Compute_There()
    {
        foreach (var b in new[] { RendererBackend.Auto, RendererBackend.Vulkan, RendererBackend.Silk })
        {
            var d = GpuComputeSelection.Decide(false, b, hasD3dHook: true, new[] { Gt710 }, DxcOk, false);
            Assert.Equal(new GpuComputeDecision(false, null, null), d);
        }
    }

    [Fact]
    public void On_This_Machine_The_Decision_Names_The_Device_The_Kernel_Opens()
    {
        // Real devices and the real DXC, decided as Linux's default would.
        var devices = GpuComputeSelection.ProbeDevices();
        var d = GpuComputeSelection.Decide(true, RendererBackend.Auto, false, devices,
            () => (DxcCompiler.Probe(out string detail), detail), allowCpu: false);
        bool gpu = devices != null && devices.Any(x => x.HasCompute && x.Type != PhysicalDeviceType.Cpu);
        if (!gpu) { Assert.False(d.UseGpu); return; }   // no GPU Vulkan device here: off, by rule
        if (!DxcCompiler.Probe(out _)) { Assert.False(d.UseGpu); return; }
        Assert.True(d.UseGpu, d.Status);
        // The picker (VulkanContext.CreateComputeDevice) opens the same device.
        Assert.EndsWith(": " + d.Device, VulkanComputeKernel.ProbeDeviceName());
    }

    // ── DXC lookup ────────────────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff1455-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string Touch(params string[] parts)
        {
            var f = System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f)!);
            File.WriteAllText(f, "");
            return f;
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static readonly string Exe = OperatingSystem.IsWindows() ? "dxc.exe" : "dxc";

    [Fact]
    public void Dxc_Lookup_Order_Is_DxcPath_Then_The_Sdk_Then_The_Bundled_Copy_Then_Path()
    {
        using var t = new TempDir();
        string explicitDxc = t.Touch("explicit", Exe);
        string sdk = System.IO.Path.Combine(t.Path, "sdk");
        string sdkDxc = t.Touch("sdk", "Bin", Exe);
        string app = System.IO.Path.Combine(t.Path, "app");
        string bundled = t.Touch("app", DxcCompiler.BundledDirName, "bin", Exe);

        Assert.Equal(explicitDxc, DxcCompiler.LocateDxc(explicitDxc, sdk, app));
        // A DXC_PATH that doesn't exist is skipped.
        Assert.Equal(sdkDxc, DxcCompiler.LocateDxc(System.IO.Path.Combine(t.Path, "missing", Exe), sdk, app));
        Assert.Equal(sdkDxc, DxcCompiler.LocateDxc(null, sdk, app));
        // An SDK without dxc falls through to the bundled copy.
        Assert.Equal(bundled, DxcCompiler.LocateDxc(null, System.IO.Path.Combine(t.Path, "empty-sdk"), app));
        Assert.Equal(bundled, DxcCompiler.LocateDxc(null, null, app));
        // Nothing found: the bare name, for PATH resolution.
        Assert.Equal(Exe, DxcCompiler.LocateDxc(null, null, System.IO.Path.Combine(t.Path, "no-app")));
        Assert.Equal(Exe, DxcCompiler.LocateDxc("", "", ""));
    }

    [Fact]
    public void The_Sdks_Lowercase_Bin_Counts_Too()
    {
        using var t = new TempDir();
        string dxc = t.Touch("sdk", "bin", Exe);
        Assert.Equal(dxc, DxcCompiler.LocateDxc(null, System.IO.Path.Combine(t.Path, "sdk"), null), ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void Probe_Runs_The_Dxc_The_Lookup_Finds()
    {
        string found = DxcCompiler.LocateDxc(Environment.GetEnvironmentVariable("DXC_PATH"),
            Environment.GetEnvironmentVariable("VULKAN_SDK"), AppContext.BaseDirectory);
        bool ok = DxcCompiler.Probe(out string detail);
        if (!File.Exists(found))
        {
            // No DXC on this machine: the probe must fail and say what it tried.
            Assert.False(ok);
            Assert.Contains("dxc", detail);
            return;
        }
        Assert.True(ok, detail);
        Assert.StartsWith(found, detail);
    }
}
