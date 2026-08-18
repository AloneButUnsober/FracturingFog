// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.D3D/RenderFactory.cs
//
// S-X1b (2026-06-23) — Windows-only DirectX renderer factory. Carved out of
// the previous FracturingFog.RendererFactory: the surface-aware Create
// overload + PreferredBackend + NonWin32Backend / SkiaBackend hooks moved
// into Engine\Rendering\RendererFactory.cs (cross-platform), and this file
// keeps only the Win-only IntPtr Create + ProbeDescription. The cross-plat
// RendererFactory invokes WindowsDxRendererFactory.Create through its
// Win32HwndBackend hook, wired by FracturingFog.Win.WindowsBootstrap.Install
// on Windows hosts.
//
// Renamed to WindowsDxRendererFactory so the cross-plat type
// FracturingFog.RendererFactory (in Engine) can keep the original short name
// at the existing call sites in AvaloniaShellBootstrap and Hosting.

using System;
using System.Runtime.Versioning;
using FracturingFog.Abstractions;

namespace FracturingFog;

/// <summary>
/// Windows-only DirectX 11/12 renderer factory. When <c>force_D3D11</c> is
/// false it builds a DX12 renderer on an FL 12.0+ GPU (falling back to DX11
/// otherwise); when true it always builds DX11. Wired into the cross-platform
/// <see cref="RendererFactory.Win32HwndBackend"/> hook by
/// <c>FracturingFog.Win.WindowsBootstrap.Install</c>.
///
/// #375: the app-level DEFAULT is DX11 — the Engine <see cref="RendererFactory"/>
/// passes <c>force_D3D11 = true</c> unless the user opted into DX12
/// (<see cref="RendererFactory.PreferD3D12"/>), because DX12 could hang / hit
/// DXGI_ERROR_DEVICE_REMOVED on some GPUs. This factory just honours the bool.
/// </summary>
public static class WindowsDxRendererFactory
{
    /// <summary>
    /// Creates a DirectX 12 renderer when <paramref name="force_D3D11"/> is false
    /// and the GPU supports FL 12.0+, otherwise a DirectX 11 renderer. Never
    /// throws — falls back silently.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IFractalRenderer Create(IntPtr hwnd, int width, int height, bool force_D3D11 = false)
    {
        if (!force_D3D11 && DirectX12Renderer.IsAvailable())
        {
            try
            {
                return new DirectX12Renderer(hwnd, width, height);
            }
            catch
            {
                // D3D12 init failed for a non-capability reason (driver bug,
                // SDK mismatch, etc.). Fall through to D3D11.
            }
        }

        return new DirectXRenderer(hwnd, width, height);
    }

    /// <summary>
    /// Returns a short description of which API will be used on this machine,
    /// useful for the title bar or System Info dialog before a renderer is created.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string ProbeDescription()
        => DirectX12Renderer.IsAvailable() ? "DirectX 12" : "DirectX 11";
}
