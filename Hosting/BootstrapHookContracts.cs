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

    /// <summary>Native video writer probe. Windows installs the Media
    /// Foundation <c>Mp4Writer</c>; null elsewhere. The bootstrap falls
    /// through to ffmpeg when this returns null.</summary>
    public static Func<string, int, int, IVideoWriter?>? NativeVideoWriterFactoryHook { get; set; }

    /// <summary>Native input bridge. Windows installs the swap-chain HWND
    /// subclass + Win32 client→screen + screen-pixel sampler; null elsewhere
    /// (Avalonia PointerPressed routing handles input directly).</summary>
    public static INativeInputBridge? NativeInputBridge { get; set; }

    /// <summary>Desktop colour eyedropper. Windows installs the WinForms
    /// global-hook implementation; null elsewhere.</summary>
    public static IColorSampleBridge? ColorSampleBridge { get; set; }

    /// <summary>Synchronous host dialog bridge for the source-editor VMs
    /// (PromptName / ConfirmYesNo / ShowInfo / PickOpenSync / PickSaveSync).
    /// Windows installs a WinForms-backed implementation that runs its own
    /// message loop synchronously. On Linux/macOS this stays null in S-X1b
    /// — the bootstrap helpers fall through to no-op (return null/false) so
    /// the editors do not deadlock the Avalonia dispatcher. Async Avalonia
    /// dialog parity for the cross-plat editors lands in a later slice.</summary>
    public static IHostSyncDialogs? SyncDialogs { get; set; }
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

/// <summary>
/// Synchronous dialog bridge for the source-editor VMs (UserEquation,
/// UserBulb, ColorGen). The VMs raise sync Func/EventArgs.Result events that
/// must return on the same call stack — Avalonia's async dialog stack can't
/// satisfy that without pumping a nested dispatcher frame (which crashed on
/// Cancel/X in prior attempts), so on Windows the bootstrap delegates to a
/// WinForms-backed implementation that runs its own modal message loop.
///
/// On Linux/macOS this stays null in S-X1b and the bootstrap helpers fall
/// through to a no-op (null/false) so the editors do not deadlock the
/// Avalonia dispatcher. Async dialog parity for those editors lands in a
/// later slice when the events themselves are refactored to async patterns.
/// </summary>
public interface IHostSyncDialogs
{
    string? PromptName(string title, string prompt, string defaultValue);
    bool ConfirmYesNo(string message, string title);
    void ShowInfo(string title, string body, bool isError);
    string? PickOpenSync(string title, string filter);
    string? PickSaveSync(string title, string filter, string defaultName);
}
