using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using FracturingFog.Abstractions;

namespace FracturingFog.UI.Avalonia.Controls;

/// <summary>
/// Avalonia <see cref="NativeControlHost"/> that exposes its child native
/// handle through <see cref="IGpuSurface"/>. The hosted renderer (DirectX 11/12
/// on Windows today; Metal/Vulkan/Skia later) attaches its swap chain to
/// <see cref="Surface"/> and watches the Resized/HandleLost events to manage
/// swap chain lifetime.
///
/// On Windows the native handle is a real HWND created by Avalonia's Win32
/// child window plumbing — drop-in compatible with the existing DXGI
/// CreateSwapChainForHwnd path. On macOS/Linux this control returns the
/// platform-equivalent handle (CAMetalLayer / X11 Window / Wayland surface)
/// and the renderer factory picks a non-DX backend.
/// </summary>
public sealed class GpuSurfaceControl : NativeControlHost
{
    private SurfaceImpl? _surface;

    /// <summary>
    /// The <see cref="IGpuSurface"/> exposed to renderers. Null until the
    /// control has been attached to a visual tree and the native handle has
    /// been created.
    /// </summary>
    public IGpuSurface? Surface => _surface;

    /// <summary>
    /// Fired the first time <see cref="Surface"/> becomes non-null. Renderer
    /// bootstrap code should subscribe here rather than reading
    /// <see cref="Surface"/> in the constructor.
    /// </summary>
    public event EventHandler? SurfaceReady;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _surface = new SurfaceImpl(handle, (int)Bounds.Width, (int)Bounds.Height, CurrentScaling());
        SurfaceReady?.Invoke(this, EventArgs.Empty);
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _surface?.RaiseHandleLost();
        _surface = null;
        base.DestroyNativeControlCore(control);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        _surface?.UpdateSize((int)result.Width, (int)result.Height, CurrentScaling());
        return result;
    }

    private double CurrentScaling() => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    private sealed class SurfaceImpl : IGpuSurface
    {
        private readonly IPlatformHandle _handle;
        private int _w;
        private int _h;
        private double _dpi;

        public SurfaceImpl(IPlatformHandle handle, int w, int h, double dpi)
        {
            _handle = handle;
            _w = w;
            _h = h;
            _dpi = dpi;
        }

        public GpuSurfaceKind Kind => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? GpuSurfaceKind.Win32Hwnd
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? GpuSurfaceKind.CoreAnimationMetalLayer
                : GpuSurfaceKind.X11Window;

        public IntPtr Handle => _handle.Handle;
        public int PixelWidth => _w;
        public int PixelHeight => _h;
        public double DpiScale => _dpi;

        public event EventHandler? Resized;
        public event EventHandler? HandleLost;

        public void UpdateSize(int w, int h, double dpi)
        {
            if (w == _w && h == _h && Math.Abs(dpi - _dpi) < 0.001) return;
            _w = w;
            _h = h;
            _dpi = dpi;
            Resized?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseHandleLost() => HandleLost?.Invoke(this, EventArgs.Empty);
    }
}
