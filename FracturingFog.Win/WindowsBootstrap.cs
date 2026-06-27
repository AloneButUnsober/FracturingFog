// FracturingFog.Win/WindowsBootstrap.cs
//
// S-X1 carve (2026-06-23). Installs Windows-only services into the
// cross-platform AvaloniaShellBootstrap (in FracturingFog.Hosting) before
// the Avalonia shell boots:
//
//   * IHardwareInfoProvider     ← WindowsD3D11HardwareInfoProvider (Rendering.D3D)
//   * GpuKernelFactoryHook      ← DirectXRenderer downcast + MandelbrotGpuKernel (Rendering.D3D)
//   * NativeVideoWriterFactoryHook ← Mp4Writer (Rendering.D3D — Media Foundation)
//   * NativeInputBridge         ← NativeMouseForwarder subclass + Win32 GetPixel sampling
//
// FracturingFog.Win owns NativeMouseForwarder, references Rendering.D3D and
// Hosting, and is the only place the AvaloniaShellBootstrap hook surface is
// wired on Windows. The cross-platform FracturingFog.App skips this install
// entirely on Linux/macOS so the hooks stay null and the bootstrap takes its
// cross-plat code paths (Silk renderer, ffmpeg-only video, Avalonia pointer
// events, no eyedropper).

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using FracturingFog.Hosting;
using FracturingFog.Input;
using FracturingFog.Rendering;

namespace FracturingFog.Win;

[SupportedOSPlatform("windows")]
public static class WindowsBootstrap
{
    /// <summary>
    /// Wire the Windows-only hooks on
    /// <see cref="AvaloniaShellBootstrap"/>. Call before
    /// <c>AvaloniaShell.Run</c> on every Windows entry point (legacy
    /// WinExe and the cross-plat App on its win-x64 leg).
    /// </summary>
    public static void Install()
    {
        if (!OperatingSystem.IsWindows()) return;

        // S-X1b — cross-platform RendererFactory (Engine) dispatches Win32
        // HWND surfaces through Win32HwndBackend. Wire the DirectX 11/12
        // factory + probe before any surface arrives so the bootstrap's
        // RendererFactory.Create(surface) call takes the DX path on Windows.
        FracturingFog.RendererFactory.Win32HwndBackend =
            (hwnd, w, h, force) => FracturingFog.WindowsDxRendererFactory.Create(hwnd, w, h, force);
        FracturingFog.RendererFactory.Win32ProbeBackend =
            () => FracturingFog.WindowsDxRendererFactory.ProbeDescription();

        BootstrapHooks.HardwareInfoProvider =
            new WindowsD3D11HardwareInfoProvider();

        BootstrapHooks.GpuKernelFactoryHook = (renderer, gate) =>
        {
            if (renderer is FracturingFog.DirectXRenderer dx
                && dx.TryGetD3D11(out var dev, out var ctx))
            {
                return new FracturingFog.Rendering.MandelbrotGpuKernel(dev, ctx, gate);
            }
            return null;
        };

        BootstrapHooks.NativeVideoWriterFactoryHook = (path, w, h) =>
        {
            try { return new FracturingFog.Mp4Writer(path, w, h); }
            catch { return null; /* MF init failed → bootstrap falls through to ffmpeg */ }
        };

        BootstrapHooks.NativeInputBridge = new WindowsNativeInputBridge();

        // S-X8 (2026-06-27) — desktop pixel sampler. Was wired only from the
        // legacy WinExe's Program.cs via WinExeColorSampleBridge (WinForms-
        // bound DesktopEyedropper wrapper), so FracturingFog.App on Windows
        // left the bridge null and the Color Theme Editor's Eyedropper
        // silently no-op'd. WindowsColorSampleBridge has no WinForms dep so
        // it works from both entry points.
        if (BootstrapHooks.ColorSampleBridge == null)
            BootstrapHooks.ColorSampleBridge = new WindowsColorSampleBridge();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsNativeInputBridge : INativeInputBridge
{
    public void Attach(IntPtr surfaceHandle, IFractalInputController input)
        => NativeMouseForwarder.Attach(surfaceHandle, input);

    public void Detach() => NativeMouseForwarder.Detach();

    public Action<bool>? ContextMenuRequested
    {
        set => NativeMouseForwarder.ContextMenuRequested = value;
    }

    public Action? FocusRequested
    {
        set => NativeMouseForwarder.FocusRequested = value;
    }

    public Func<bool>? LeftDragWindowHook
    {
        set => NativeMouseForwarder.LeftDragWindowHook = value;
    }

    public Func<int, int, bool>? InspectClickHook
    {
        set => NativeMouseForwarder.InspectClickHook = value;
    }

    public bool TrySampleClient(IntPtr surfaceHandle, int clientX, int clientY,
                                 out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var pt = new POINT { X = clientX, Y = clientY };
        if (!ClientToScreen(surfaceHandle, ref pt)) return false;

        // Win32 screen-pixel grab. Mirrors what DesktopEyedropper.SamplePixel
        // does via GDI+ CopyFromScreen, but stays inside FracturingFog.Win
        // so the bridge has no System.Drawing / WinForms dep.
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;
        try
        {
            uint colorRef = GetPixel(screenDc, pt.X, pt.Y);
            if (colorRef == CLR_INVALID) return false;
            r = (byte)(colorRef & 0xFF);
            g = (byte)((colorRef >> 8) & 0xFF);
            b = (byte)((colorRef >> 16) & 0xFF);
            return true;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const uint CLR_INVALID = 0xFFFFFFFF;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);
}
