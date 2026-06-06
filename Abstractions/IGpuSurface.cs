// IGpuSurface.cs
// Cross-platform GPU surface abstraction. Decouples renderers from the
// Win32 HWND so future backends (CAMetalLayer on macOS, VkSurfaceKHR on
// Linux/Vulkan, Skia GPU backend) can satisfy the same contract.
//
// Phase 2 (Avalonia migration) target: a single IGpuSurface is provided by
// whichever shell hosts the renderer — WinForms today, Avalonia
// NativeControlHost tomorrow — and the renderer never sees the shell type.
//
// Lives in FracturingFog.Abstractions (UI-free, platform-free) so the
// WinForms WinExe and the Avalonia UI library can share the contract
// without depending on each other.

using System;

namespace FracturingFog.Abstractions;

/// <summary>
/// Kind of native handle exposed by an <see cref="IGpuSurface"/>. Renderers
/// that cannot consume a given kind should throw at construction time so the
/// shell can pick a different backend.
/// </summary>
public enum GpuSurfaceKind
{
    /// <summary>Win32 HWND. Consumed by DXGI swap chains.</summary>
    Win32Hwnd,

    /// <summary>CAMetalLayer pointer on macOS. Consumed by Metal / MoltenVK.</summary>
    CoreAnimationMetalLayer,

    /// <summary>X11 Window XID on Linux. Consumed by Vulkan/OpenGL.</summary>
    X11Window,

    /// <summary>Wayland wl_surface pointer on Linux.</summary>
    WaylandSurface,
}

/// <summary>
/// A native rendering target the GPU backend can attach a swap chain to.
/// The surface owner (the UI shell) is responsible for lifetime — renderers
/// must not dispose the underlying handle.
/// </summary>
public interface IGpuSurface
{
    /// <summary>Kind of native handle returned by <see cref="Handle"/>.</summary>
    GpuSurfaceKind Kind { get; }

    /// <summary>Opaque native handle whose meaning depends on <see cref="Kind"/>.</summary>
    IntPtr Handle { get; }

    /// <summary>Current pixel width of the surface. Updated before <see cref="Resized"/> fires.</summary>
    int PixelWidth { get; }

    /// <summary>Current pixel height of the surface. Updated before <see cref="Resized"/> fires.</summary>
    int PixelHeight { get; }

    /// <summary>
    /// Device-independent scale factor (1.0 at 96 DPI, 1.5 at 150%, etc.).
    /// Renderers that do their own oversampling can use this; backends that
    /// drive a CPU buffer at PixelWidth × PixelHeight can ignore it.
    /// </summary>
    double DpiScale { get; }

    /// <summary>
    /// Raised after <see cref="PixelWidth"/>/<see cref="PixelHeight"/> change.
    /// Renderers should resize their swap chain inside the handler.
    /// </summary>
    event EventHandler? Resized;

    /// <summary>
    /// Raised when the native handle is being torn down (window closed,
    /// shell shutting down). Renderers must release any resources tied to
    /// <see cref="Handle"/> before returning from the handler.
    /// </summary>
    event EventHandler? HandleLost;
}
