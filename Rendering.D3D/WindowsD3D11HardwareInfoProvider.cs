// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.D3D/WindowsD3D11HardwareInfoProvider.cs
//
// Phase X.0 / Slice 0.3b — IHardwareInfoProvider implementation backed by
// DXGI + D3D11 via Vortice. Lives here because the Vortice references
// pin it to net10.0-windows. AvaloniaShellBootstrap (still in WinExe
// until Slice 0.3c) constructs an instance and hands it to the cross-
// platform HostHelpContentProvider.

using System;
using System.Runtime.Versioning;
using System.Text;

using FracturingFog.Help;

namespace FracturingFog.Rendering
{
    [SupportedOSPlatform("windows")]
    public sealed class WindowsD3D11HardwareInfoProvider : IHardwareInfoProvider
    {
        public void AppendGpuAdapters(StringBuilder sb)
        {
            try
            {
                using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
                uint idx = 0;
                while (factory.EnumAdapters1(idx, out var adapter).Success)
                {
                    var desc = adapter.Description1;
                    sb.AppendLine($"Adapter {idx}: {desc.Description}");
                    sb.AppendLine($"  Vendor ID:      0x{desc.VendorId:X4}");
                    sb.AppendLine($"  Device ID:      0x{desc.DeviceId:X4}");
                    sb.AppendLine($"  Dedicated VRAM: {desc.DedicatedVideoMemory / (1024 * 1024)} MB");
                    sb.AppendLine($"  Shared RAM:     {desc.SharedSystemMemory / (1024 * 1024)} MB");
                    adapter.Dispose();
                    idx++;
                }
                if (idx == 0) sb.AppendLine("  (No DXGI adapters reported.)");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  (DXGI enumeration failed: {ex.Message})");
            }
        }

        // Discrete GPUs report a large slab of dedicated (on-board) VRAM;
        // integrated GPUs carve a small aperture out of system RAM and lean on
        // SharedSystemMemory instead. 512 MB of dedicated VRAM cleanly
        // separates every discrete card from the iGPU aperture (typically
        // 128 MB or less) without a per-vendor allow-list.
        private const ulong DiscreteVramThresholdBytes = 512UL * 1024 * 1024;

        public bool HasDiscreteGpu()
        {
            try
            {
                using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
                uint idx = 0;
                bool discrete = false;
                while (factory.EnumAdapters1(idx, out var adapter).Success)
                {
                    var desc = adapter.Description1;
                    // Skip the Microsoft Basic Render Driver / WARP software
                    // adapter — it reports VRAM but is not real hardware.
                    bool software = (desc.Flags & Vortice.DXGI.AdapterFlags.Software) != 0;
                    if (!software && (ulong)desc.DedicatedVideoMemory >= DiscreteVramThresholdBytes)
                        discrete = true;
                    adapter.Dispose();
                    idx++;
                }
                return discrete;
            }
            catch
            {
                // Conservative fallback — assume iGPU so the ceiling stays tight.
                return false;
            }
        }

        public void AppendGpuFeatureLevel(StringBuilder sb)
        {
            try
            {
                Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                    null,
                    Vortice.Direct3D.DriverType.Hardware,
                    Vortice.Direct3D11.DeviceCreationFlags.None,
                    null!,
                    out _, out var fl, out _);
                sb.AppendLine($"Max Feature Level: {fl}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  (Could not query D3D11 feature level: {ex.Message})");
            }
        }
    }
}
