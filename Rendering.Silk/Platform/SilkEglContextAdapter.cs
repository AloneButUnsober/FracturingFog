// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SilkEglContextAdapter.cs
//
// EGL P/Invoke wrapper that turns a foreign Wayland wl_surface* (the handle
// Avalonia's NativeControlHost hands out on Wayland desktops) into a current
// OpenGL 3.3 core context plus a Silk.NET GL function loader.
//
// Why EGL: on modern Linux distributions running Wayland natively, GLX is
// unavailable — only XWayland sessions expose it via translation. Avalonia
// prefers the native Wayland backend when WAYLAND_DISPLAY is set; falling
// through to the X11 adapter under XWayland costs an extra blit per frame
// and breaks fractional scaling. EGL is the only path the compositor honours.
//
// Display sourcing: opens its own wl_display via wl_display_connect(NULL) so
// the adapter does not have to extract Avalonia's internal display pointer.
// Wayland permits multiple client connections per process — the compositor
// reference-counts them — and the per-connection overhead is one TCP-ish
// socket plus a roundtrip on init, paid once per adapter lifetime.
//
// wl_egl_window: opaque struct from libwayland-egl that bridges a wl_surface
// into an EGL window surface. We own it and resize it from
// <see cref="IGpuSurface.Resized"/>.

using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;
using FracturingFog.Abstractions;

namespace FracturingFog.Rendering.Silk.Platform;

public sealed class SilkEglContextAdapter : INativeContext, IDisposable
{
    // EGL constants (from EGL/egl.h + KHR_create_context).
    private const int EGL_RED_SIZE                          = 0x3024;
    private const int EGL_GREEN_SIZE                        = 0x3023;
    private const int EGL_BLUE_SIZE                         = 0x3022;
    private const int EGL_ALPHA_SIZE                        = 0x3021;
    private const int EGL_DEPTH_SIZE                        = 0x3025;
    private const int EGL_STENCIL_SIZE                      = 0x3026;
    private const int EGL_RENDERABLE_TYPE                   = 0x3040;
    private const int EGL_SURFACE_TYPE                      = 0x3033;
    private const int EGL_OPENGL_BIT                        = 0x0008;
    private const int EGL_WINDOW_BIT                        = 0x0004;
    private const int EGL_NONE                              = 0x3038;
    private const int EGL_OPENGL_API                        = 0x30A2;
    private const int EGL_CONTEXT_MAJOR_VERSION             = 0x3098;
    private const int EGL_CONTEXT_MINOR_VERSION             = 0x30FB;
    private const int EGL_CONTEXT_OPENGL_PROFILE_MASK       = 0x30FD;
    private const int EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT   = 0x00000001;
    private const int EGL_CONTEXT_OPENGL_FORWARD_COMPATIBLE = 0x31B1;
    private const int EGL_TRUE                              = 1;

    private readonly IGpuSurface _surface;
    private IntPtr _wlDisplay;
    private IntPtr _wlEglWindow;
    private IntPtr _eglDisplay;
    private IntPtr _eglConfig;
    private IntPtr _eglContext;
    private IntPtr _eglSurface;
    private bool _disposed;

    public GL Gl { get; }

    public static SilkEglContextAdapter CreateFor(IGpuSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.Kind != GpuSurfaceKind.WaylandSurface)
            throw new PlatformNotSupportedException(
                $"SilkEglContextAdapter requires a WaylandSurface; got {surface.Kind}.");
        if (surface.Handle == IntPtr.Zero)
            throw new InvalidOperationException("IGpuSurface.Handle is null (wl_surface* missing).");
        return new SilkEglContextAdapter(surface);
    }

    private SilkEglContextAdapter(IGpuSurface surface)
    {
        _surface = surface;

        _wlDisplay = wl_display_connect(IntPtr.Zero);
        if (_wlDisplay == IntPtr.Zero)
            throw new InvalidOperationException(
                "wl_display_connect(NULL) failed — no Wayland compositor on $WAYLAND_DISPLAY.");

        _eglDisplay = eglGetDisplay(_wlDisplay);
        if (_eglDisplay == IntPtr.Zero)
            throw new InvalidOperationException("eglGetDisplay returned EGL_NO_DISPLAY.");

        if (eglInitialize(_eglDisplay, out int _, out int _) != EGL_TRUE)
            throw new InvalidOperationException("eglInitialize failed.");

        // Bind GL (not GLES) so SilkGLRenderer's 3.3 core shaders compile.
        if (eglBindAPI(EGL_OPENGL_API) != EGL_TRUE)
            throw new InvalidOperationException("eglBindAPI(EGL_OPENGL_API) failed.");

        int[] configAttribs =
        [
            EGL_SURFACE_TYPE,    EGL_WINDOW_BIT,
            EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
            EGL_RED_SIZE,        8,
            EGL_GREEN_SIZE,      8,
            EGL_BLUE_SIZE,       8,
            EGL_ALPHA_SIZE,      8,
            EGL_DEPTH_SIZE,      24,
            EGL_STENCIL_SIZE,    8,
            EGL_NONE
        ];
        IntPtr[] configs = new IntPtr[1];
        if (eglChooseConfig(_eglDisplay, configAttribs, configs, 1, out int nConfigs) != EGL_TRUE
            || nConfigs <= 0)
            throw new InvalidOperationException("eglChooseConfig returned no matching configs.");
        _eglConfig = configs[0];

        int[] ctxAttribs =
        [
            EGL_CONTEXT_MAJOR_VERSION,             3,
            EGL_CONTEXT_MINOR_VERSION,             3,
            EGL_CONTEXT_OPENGL_PROFILE_MASK,       EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT,
            EGL_CONTEXT_OPENGL_FORWARD_COMPATIBLE, EGL_TRUE,
            EGL_NONE
        ];
        _eglContext = eglCreateContext(_eglDisplay, _eglConfig, IntPtr.Zero, ctxAttribs);
        if (_eglContext == IntPtr.Zero)
            throw new InvalidOperationException("eglCreateContext (3.3 core) failed.");

        int w = System.Math.Max(1, surface.PixelWidth);
        int h = System.Math.Max(1, surface.PixelHeight);
        _wlEglWindow = wl_egl_window_create(surface.Handle, w, h);
        if (_wlEglWindow == IntPtr.Zero)
            throw new InvalidOperationException("wl_egl_window_create failed.");

        _eglSurface = eglCreateWindowSurface(_eglDisplay, _eglConfig, _wlEglWindow, IntPtr.Zero);
        if (_eglSurface == IntPtr.Zero)
            throw new InvalidOperationException("eglCreateWindowSurface failed.");

        if (eglMakeCurrent(_eglDisplay, _eglSurface, _eglSurface, _eglContext) != EGL_TRUE)
            throw new InvalidOperationException("eglMakeCurrent failed.");

        _surface.Resized += OnSurfaceResized;

        Gl = GL.GetApi(this);
    }

    private void OnSurfaceResized(object? sender, EventArgs e)
    {
        if (_disposed || _wlEglWindow == IntPtr.Zero) return;
        int w = System.Math.Max(1, _surface.PixelWidth);
        int h = System.Math.Max(1, _surface.PixelHeight);
        wl_egl_window_resize(_wlEglWindow, w, h, 0, 0);
    }

    public void MakeCurrent()
    {
        if (_disposed) return;
        eglMakeCurrent(_eglDisplay, _eglSurface, _eglSurface, _eglContext);
    }

    // S-X6 (2026-06-23) — release ctx from calling thread; see SilkWin32 sibling.
    public void ReleaseCurrent()
    {
        if (_disposed) return;
        eglMakeCurrent(_eglDisplay, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    public void SwapBuffers()
    {
        if (_disposed) return;
        eglSwapBuffers(_eglDisplay, _eglSurface);
    }

    // ── INativeContext ────────────────────────────────────────────────────
    public nint GetProcAddress(string proc, int? slot = default)
        => eglGetProcAddress(proc);

    public bool TryGetProcAddress(string proc, out nint addr, int? slot = default)
    {
        addr = eglGetProcAddress(proc);
        return addr != IntPtr.Zero;
    }

    public bool IsExtensionPresent(string extensionName) => false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _surface.Resized -= OnSurfaceResized; } catch { /* ignore */ }
        try
        {
            if (_eglDisplay != IntPtr.Zero)
            {
                eglMakeCurrent(_eglDisplay, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (_eglSurface != IntPtr.Zero) eglDestroySurface(_eglDisplay, _eglSurface);
                if (_eglContext != IntPtr.Zero) eglDestroyContext(_eglDisplay, _eglContext);
                eglTerminate(_eglDisplay);
            }
            if (_wlEglWindow != IntPtr.Zero) wl_egl_window_destroy(_wlEglWindow);
            if (_wlDisplay   != IntPtr.Zero) wl_display_disconnect(_wlDisplay);
        }
        catch { /* swallow on shutdown */ }
        _eglSurface = _eglContext = _eglConfig = _eglDisplay
                    = _wlEglWindow = _wlDisplay = IntPtr.Zero;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────
    [DllImport("libEGL.so.1")] private static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);
    [DllImport("libEGL.so.1")] private static extern int    eglInitialize(IntPtr display, out int major, out int minor);
    [DllImport("libEGL.so.1")] private static extern int    eglTerminate(IntPtr display);
    [DllImport("libEGL.so.1")] private static extern int    eglBindAPI(int api);
    [DllImport("libEGL.so.1")] private static extern int    eglChooseConfig(IntPtr display, int[] attribs, IntPtr[] configs, int configSize, out int numConfig);
    [DllImport("libEGL.so.1")] private static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr shareCtx, int[] attribs);
    [DllImport("libEGL.so.1")] private static extern int    eglDestroyContext(IntPtr display, IntPtr ctx);
    [DllImport("libEGL.so.1")] private static extern IntPtr eglCreateWindowSurface(IntPtr display, IntPtr config, IntPtr nativeWindow, IntPtr attribs);
    [DllImport("libEGL.so.1")] private static extern int    eglDestroySurface(IntPtr display, IntPtr surface);
    [DllImport("libEGL.so.1")] private static extern int    eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr ctx);
    [DllImport("libEGL.so.1")] private static extern int    eglSwapBuffers(IntPtr display, IntPtr surface);
    [DllImport("libEGL.so.1", CharSet = CharSet.Ansi)] private static extern IntPtr eglGetProcAddress(string proc);

    [DllImport("libwayland-client.so.0")] private static extern IntPtr wl_display_connect(IntPtr name);
    [DllImport("libwayland-client.so.0")] private static extern void   wl_display_disconnect(IntPtr display);

    [DllImport("libwayland-egl.so.1")] private static extern IntPtr wl_egl_window_create(IntPtr wlSurface, int width, int height);
    [DllImport("libwayland-egl.so.1")] private static extern void   wl_egl_window_destroy(IntPtr eglWindow);
    [DllImport("libwayland-egl.so.1")] private static extern void   wl_egl_window_resize(IntPtr eglWindow, int width, int height, int dx, int dy);
}
