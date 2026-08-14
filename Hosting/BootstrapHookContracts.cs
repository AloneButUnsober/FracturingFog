// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/BootstrapHookContracts.cs
//
// S-X1 (2026-06-23) — Hook contracts AvaloniaShellBootstrap exposes so the
// platform-specific installer (FracturingFog.Win.WindowsBootstrap on Windows;
// nothing on Linux/macOS today) can wire the platform-specific services
// without dragging Win-only refs into Hosting.dll.
//
// Lives in Hosting.dll (not the WinExe-resident AvaloniaShellBootstrap.cs)
// so FracturingFog.Win can implement INativeInputBridge / IColorSampleBridge
// against a Hosting ProjectReference. S-X1b will move
// AvaloniaShellBootstrap.cs itself into Hosting.dll once RendererFactory is
// split and the WinForms dialog tail is replaced with AvaloniaDialogs.

using System;

using FracturingFog;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Input;
using FracturingFog.Rendering;

namespace FracturingFog.Hosting;

/// <summary>
/// Static hook surface AvaloniaShellBootstrap reads at boot. The
/// platform-specific installer (FracturingFog.Win.WindowsBootstrap on Windows;
/// nothing on Linux/macOS today) writes here before AvaloniaShell.Run so the
/// surface-ready callback consults the hooks when wiring renderer / video /
/// native-input / hardware-probe / colour-sample services.
/// </summary>
public static class BootstrapHooks
{
    /// <summary>Hardware tab probe. Windows installs
    /// <c>WindowsD3D11HardwareInfoProvider</c>; null elsewhere.</summary>
    public static IHardwareInfoProvider? HardwareInfoProvider { get; set; }

    /// <summary>D3D11 compute-kernel factory. Windows installs the
    /// DirectXRenderer downcast → <c>MandelbrotGpuKernel</c>; null elsewhere
    /// (UseGpuCompute stays off).</summary>
    public static Func<IFractalRenderer, object, IGpuKernel?>? GpuKernelFactoryHook { get; set; }

    /// <summary>#162 (Slice 3d) — D3D11 relief-raymarch kernel factory. Windows
    /// installs the DirectXRenderer downcast → <c>ReliefRaymarchGpuKernel</c>
    /// (same native handles as the escape-time kernel); null elsewhere (the CPU
    /// relief raymarch runs regardless — the GPU path is opt-in).</summary>
    public static Func<IFractalRenderer, object, FracturingFog.Rendering.Lighting.IReliefRaymarchKernel?>? ReliefKernelFactoryHook { get; set; }

    /// <summary>Native video writer probe. Windows installs the Media
    /// Foundation <c>Mp4Writer</c>; null elsewhere. The bootstrap falls
    /// through to ffmpeg when this returns null.</summary>
    public static Func<string, int, int, IVideoWriter?>? NativeVideoWriterFactoryHook { get; set; }

    /// <summary>VLAO audit #291 — headless-batch video writer probe. Same
    /// Media-Foundation <c>Mp4Writer</c> as <see cref="NativeVideoWriterFactoryHook"/>
    /// but carries the explicit frame rate the batch CLI requests (the batch
    /// entry runs before the full bootstrap, and batch fps must not silently
    /// default to 30). Null off Windows → BatchRenderer falls through to the
    /// PNG-sequence + ffmpeg path. Signature: (path, width, height, fps).</summary>
    public static Func<string, int, int, int, IVideoWriter?>? BatchVideoWriterFactoryHook { get; set; }

    /// <summary>Native input bridge. Windows installs the swap-chain HWND
    /// subclass + Win32 client→screen + screen-pixel sampler; null elsewhere
    /// (Avalonia PointerPressed routing handles input directly).</summary>
    public static INativeInputBridge? NativeInputBridge { get; set; }

    /// <summary>Desktop colour eyedropper. Windows installs the WinForms
    /// global-hook implementation; null elsewhere.</summary>
    public static IColorSampleBridge? ColorSampleBridge { get; set; }
}

/// <summary>
/// Platform bridge for the native render-surface input pipeline. On Windows
/// the implementation subclasses the swap-chain HWND via
/// <c>NativeMouseForwarder</c> and provides Win32 client→screen +
/// screen-pixel sampling. On Linux/macOS the bootstrap leaves
/// <c>NativeInputBridge</c> null and Avalonia PointerPressed routing handles
/// mouse input directly because the GL/Skia render path does not composite
/// a separate native HWND on top of the XAML tree.
/// </summary>
public interface INativeInputBridge
{
    void Attach(IntPtr surfaceHandle, IFractalInputController input);
    void Detach();
    Action<bool>? ContextMenuRequested { set; }
    Action? FocusRequested { set; }
    Func<bool>? LeftDragWindowHook { set; }
    Func<int, int, bool>? InspectClickHook { set; }
    bool TrySampleClient(IntPtr surfaceHandle, int clientX, int clientY,
                         out byte r, out byte g, out byte b);
}

/// <summary>
/// Platform bridge for the desktop colour eyedropper. On Windows the
/// implementation wraps the WinForms-bound
/// <c>FracturingFog.Views.Editors.DesktopEyedropper</c> (global mouse hook
/// → GDI+ pixel sample). On Linux/macOS the bridge stays null and the Color
/// Theme Editor's "sample colour" button completes without picking.
/// </summary>
public interface IColorSampleBridge
{
    bool IsActive { get; }
    void Begin(Action<(byte R, byte G, byte B)> onPicked, Action onCancelled);
}
