// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.D3D/D3DGroupShapes.cs
//
// #1339 / #1377 — the D3D11 side of the thread-group shape tuner
// (GpuGroupShapes): the store's device key, shared by every D3D11 kernel that
// tunes a slot (the 2D kernels, the relief raymarch, the froxel passes).

using Vortice.Direct3D11;

namespace FracturingFog.Rendering;

internal static class D3DGroupShapes
{
    /// <summary>"d3d11|adapter|vendor:device:revision|driver", the store's device key.</summary>
    public static string DeviceKey(ID3D11Device device)
    {
        try
        {
            using var dxgi = device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            var d = adapter.Description;
            string driver = adapter.CheckInterfaceSupport<Vortice.DXGI.IDXGIDevice>(out long umd) ? umd.ToString("X16") : "?";
            return $"d3d11|{d.Description}|{d.VendorId:X4}:{d.DeviceId:X4}:{d.Revision:X2}|{driver}";
        }
        catch { return "d3d11|unknown adapter"; }
    }
}
