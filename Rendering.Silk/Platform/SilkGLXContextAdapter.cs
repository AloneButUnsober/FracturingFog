// SilkGLXContextAdapter.cs
//
// GLX P/Invoke wrapper that turns a foreign X11 Window XID (from Avalonia's
// NativeControlHost on Linux) into a current OpenGL 3.3 core context plus a
// Silk.NET GL function loader.
//
// Display connection: opens its own XOpenDisplay(null) to avoid coupling to
// Avalonia's internal Display* — GLX permits multiple connections to the
// same X server with no synchronisation cost on the render path. The XID
// itself is shared with Avalonia, which owns its lifetime.
//
// Linker target: libGL.so.1 is the runtime SONAME for both Mesa and the
// proprietary NVIDIA driver on every modern desktop distribution. libX11.so.6
// likewise. CI runners install xorg-dev/libgl1 to satisfy these.

using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;
using FracturingFog.Abstractions;

namespace FracturingFog.Rendering.Silk.Platform;

public sealed class SilkGLXContextAdapter : INativeContext, IDisposable
{
    private const int GLX_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    private const int GLX_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    private const int GLX_CONTEXT_FLAGS_ARB         = 0x2094;
    private const int GLX_CONTEXT_PROFILE_MASK_ARB  = 0x9126;
    private const int GLX_CONTEXT_CORE_PROFILE_BIT_ARB = 0x00000001;
    private const int GLX_CONTEXT_FORWARD_COMPATIBLE_BIT_ARB = 0x0002;

    private const int GLX_X_RENDERABLE   = 0x8050;
    private const int GLX_DRAWABLE_TYPE  = 0x8010;
    private const int GLX_RENDER_TYPE    = 0x8011;
    private const int GLX_X_VISUAL_TYPE  = 0x22;
    private const int GLX_RED_SIZE       = 8;
    private const int GLX_GREEN_SIZE     = 9;
    private const int GLX_BLUE_SIZE      = 10;
    private const int GLX_ALPHA_SIZE     = 11;
    private const int GLX_DEPTH_SIZE     = 12;
    private const int GLX_STENCIL_SIZE   = 13;
    private const int GLX_DOUBLEBUFFER   = 5;
    private const int GLX_WINDOW_BIT     = 0x00000001;
    private const int GLX_RGBA_BIT       = 0x00000001;
    private const int GLX_TRUE_COLOR     = 0x8002;
    private const int True               = 1;

    private IntPtr _display;
    private IntPtr _glxCtx;
    private nuint  _drawable;
    private bool   _disposed;

    public GL Gl { get; }

    public static SilkGLXContextAdapter CreateFor(IGpuSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.Kind != GpuSurfaceKind.X11Window)
            throw new PlatformNotSupportedException(
                $"SilkGLXContextAdapter requires an X11Window surface; got {surface.Kind}.");
        if (surface.Handle == IntPtr.Zero)
            throw new InvalidOperationException("IGpuSurface.Handle is null (X11 XID missing).");
        return new SilkGLXContextAdapter((nuint)surface.Handle.ToInt64());
    }

    private SilkGLXContextAdapter(nuint xid)
    {
        _drawable = xid;
        _display  = XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException("XOpenDisplay(null) failed — no X server available.");

        int screen = XDefaultScreen(_display);

        int[] visualAttribs =
        [
            GLX_X_RENDERABLE,    True,
            GLX_DRAWABLE_TYPE,   GLX_WINDOW_BIT,
            GLX_RENDER_TYPE,     GLX_RGBA_BIT,
            GLX_X_VISUAL_TYPE,   GLX_TRUE_COLOR,
            GLX_RED_SIZE,        8,
            GLX_GREEN_SIZE,      8,
            GLX_BLUE_SIZE,       8,
            GLX_ALPHA_SIZE,      8,
            GLX_DEPTH_SIZE,      24,
            GLX_STENCIL_SIZE,    8,
            GLX_DOUBLEBUFFER,    True,
            0
        ];

        IntPtr fbConfigs = glXChooseFBConfig(_display, screen, visualAttribs, out int nConfigs);
        if (fbConfigs == IntPtr.Zero || nConfigs <= 0)
            throw new InvalidOperationException("glXChooseFBConfig returned no matching configs.");

        IntPtr fbConfig = Marshal.ReadIntPtr(fbConfigs); // first match
        XFree(fbConfigs);

        IntPtr createAttribs = glXGetProcAddress("glXCreateContextAttribsARB");
        if (createAttribs == IntPtr.Zero)
            throw new InvalidOperationException("glXCreateContextAttribsARB not exported — GLX 1.4+ driver required.");

        int[] ctxAttribs =
        [
            GLX_CONTEXT_MAJOR_VERSION_ARB, 3,
            GLX_CONTEXT_MINOR_VERSION_ARB, 3,
            GLX_CONTEXT_PROFILE_MASK_ARB,  GLX_CONTEXT_CORE_PROFILE_BIT_ARB,
            GLX_CONTEXT_FLAGS_ARB,         GLX_CONTEXT_FORWARD_COMPATIBLE_BIT_ARB,
            0
        ];
        var del = Marshal.GetDelegateForFunctionPointer<GlxCreateContextAttribsARBDelegate>(createAttribs);
        _glxCtx = del(_display, fbConfig, IntPtr.Zero, True, ctxAttribs);
        if (_glxCtx == IntPtr.Zero)
            throw new InvalidOperationException("glXCreateContextAttribsARB returned null context.");

        if (glXMakeCurrent(_display, _drawable, _glxCtx) == 0)
            throw new InvalidOperationException("glXMakeCurrent failed.");

        Gl = GL.GetApi(this);
    }

    public void MakeCurrent()
    {
        if (_disposed) return;
        glXMakeCurrent(_display, _drawable, _glxCtx);
    }

    public void SwapBuffers()
    {
        if (_disposed) return;
        glXSwapBuffers(_display, _drawable);
    }

    // ── INativeContext ────────────────────────────────────────────────────
    public nint GetProcAddress(string proc, int? slot = default)
        => glXGetProcAddress(proc);

    public bool TryGetProcAddress(string proc, out nint addr, int? slot = default)
    {
        addr = glXGetProcAddress(proc);
        return addr != IntPtr.Zero;
    }

    public bool IsExtensionPresent(string extensionName) => false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_display != IntPtr.Zero)
            {
                glXMakeCurrent(_display, 0, IntPtr.Zero);
                if (_glxCtx != IntPtr.Zero) glXDestroyContext(_display, _glxCtx);
                XCloseDisplay(_display);
            }
        }
        catch { /* swallow on shutdown */ }
        _glxCtx = _display = IntPtr.Zero;
        _drawable = 0;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GlxCreateContextAttribsARBDelegate(
        IntPtr display, IntPtr fbConfig, IntPtr shareCtx, int direct, int[] attribs);

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int    XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int    XDefaultScreen(IntPtr display);
    [DllImport("libX11.so.6")] private static extern void   XFree(IntPtr ptr);

    [DllImport("libGL.so.1")] private static extern IntPtr glXChooseFBConfig(IntPtr display, int screen, int[] attribs, out int nElements);
    [DllImport("libGL.so.1")] private static extern int    glXMakeCurrent(IntPtr display, nuint drawable, IntPtr ctx);
    [DllImport("libGL.so.1")] private static extern void   glXSwapBuffers(IntPtr display, nuint drawable);
    [DllImport("libGL.so.1")] private static extern void   glXDestroyContext(IntPtr display, IntPtr ctx);
    [DllImport("libGL.so.1", CharSet = CharSet.Ansi)] private static extern IntPtr glXGetProcAddress(string proc);
}
