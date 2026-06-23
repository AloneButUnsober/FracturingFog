// SilkWin32ContextAdapter.cs
//
// WGL P/Invoke wrapper that turns a foreign Win32 HWND (e.g. the one Avalonia
// NativeControlHost exposes via GpuSurfaceControl) into a current OpenGL 3.3
// core context plus a Silk.NET GL function loader. The renderer then drops in
// unchanged via SilkGLRenderer.
//
// Lifecycle: Create() pins a DC, picks an RGBA8/Depth24 pixel format, builds
// a legacy context, queries wglCreateContextAttribsARB to upgrade to 3.3
// core, and makes the new context current on the calling thread. Dispose
// tears the whole chain down in reverse order.
//
// Notes:
//   - The DX backend is preferred on Windows; this adapter exists so the
//     Avalonia bootstrap can exercise the Silk path during cross-platform
//     parity testing without spinning up a Linux VM.
//   - Pixel format selection runs once per HWND; subsequent calls on the
//     same window must reuse the same Adapter or SetPixelFormat will fail.

using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;
using FracturingFog.Abstractions;

namespace FracturingFog.Rendering.Silk.Platform;

public sealed class SilkWin32ContextAdapter : INativeContext, IDisposable
{
    private const int PFD_DRAW_TO_WINDOW   = 0x00000004;
    private const int PFD_SUPPORT_OPENGL   = 0x00000020;
    private const int PFD_DOUBLEBUFFER     = 0x00000001;
    private const int PFD_TYPE_RGBA        = 0;
    private const int PFD_MAIN_PLANE       = 0;

    private const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    private const int WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    private const int WGL_CONTEXT_FLAGS_ARB         = 0x2094;
    private const int WGL_CONTEXT_PROFILE_MASK_ARB  = 0x9126;
    private const int WGL_CONTEXT_CORE_PROFILE_BIT  = 0x00000001;
    private const int WGL_CONTEXT_FORWARD_COMPATIBLE_BIT = 0x0002;

    private IntPtr _hwnd;
    private IntPtr _hdc;
    private IntPtr _glrc;
    private IntPtr _opengl32;
    private bool _disposed;

    public GL Gl { get; }

    public static SilkWin32ContextAdapter CreateFor(IGpuSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.Kind != GpuSurfaceKind.Win32Hwnd)
            throw new PlatformNotSupportedException(
                $"SilkWin32ContextAdapter requires a Win32 HWND surface; got {surface.Kind}.");
        if (surface.Handle == IntPtr.Zero)
            throw new InvalidOperationException("IGpuSurface.Handle is null.");
        return new SilkWin32ContextAdapter(surface.Handle);
    }

    private SilkWin32ContextAdapter(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _opengl32 = LoadLibraryW("opengl32.dll");
        if (_opengl32 == IntPtr.Zero)
            throw new InvalidOperationException("LoadLibrary opengl32.dll failed.");

        _hdc = GetDC(hwnd);
        if (_hdc == IntPtr.Zero)
            throw new InvalidOperationException("GetDC returned null for foreign HWND.");

        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize         = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion      = 1,
            dwFlags       = (uint)(PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER),
            iPixelType    = PFD_TYPE_RGBA,
            cColorBits    = 32,
            cDepthBits    = 24,
            cStencilBits  = 8,
            iLayerType    = PFD_MAIN_PLANE,
        };
        int pf = ChoosePixelFormat(_hdc, ref pfd);
        if (pf == 0) throw new InvalidOperationException("ChoosePixelFormat failed.");
        if (!SetPixelFormat(_hdc, pf, ref pfd))
            throw new InvalidOperationException("SetPixelFormat failed.");

        IntPtr legacyRc = wglCreateContext(_hdc);
        if (legacyRc == IntPtr.Zero)
            throw new InvalidOperationException("wglCreateContext (legacy) failed.");
        if (!wglMakeCurrent(_hdc, legacyRc))
            throw new InvalidOperationException("wglMakeCurrent (legacy) failed.");

        IntPtr createCtxAttribs = wglGetProcAddress("wglCreateContextAttribsARB");
        if (createCtxAttribs == IntPtr.Zero)
        {
            // Driver lacks the ARB extension — keep the legacy context. GL 3.3
            // core may not be available; the renderer will fail at link time.
            _glrc = legacyRc;
        }
        else
        {
            int[] attribs =
            [
                WGL_CONTEXT_MAJOR_VERSION_ARB, 3,
                WGL_CONTEXT_MINOR_VERSION_ARB, 3,
                WGL_CONTEXT_PROFILE_MASK_ARB,  WGL_CONTEXT_CORE_PROFILE_BIT,
                WGL_CONTEXT_FLAGS_ARB,         WGL_CONTEXT_FORWARD_COMPATIBLE_BIT,
                0
            ];
            var del = Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribsARBDelegate>(createCtxAttribs);
            IntPtr coreRc = del(_hdc, IntPtr.Zero, attribs);
            if (coreRc == IntPtr.Zero)
            {
                _glrc = legacyRc;
            }
            else
            {
                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                wglDeleteContext(legacyRc);
                if (!wglMakeCurrent(_hdc, coreRc))
                    throw new InvalidOperationException("wglMakeCurrent (core) failed.");
                _glrc = coreRc;
            }
        }

        Gl = GL.GetApi(this);
    }

    public void MakeCurrent()
    {
        if (_disposed) return;
        wglMakeCurrent(_hdc, _glrc);
    }

    // S-X6 (2026-06-23) — releases ctx from the calling thread. WGL ctx is
    // pinned per-thread; without an explicit release, the next thread that
    // calls MakeCurrent silently fails (ERROR_BUSY) and subsequent GL calls
    // return GL_INVALID_OPERATION on a null current ctx.
    public void ReleaseCurrent()
    {
        if (_disposed) return;
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
    }

    public void SwapBuffers()
    {
        if (_disposed) return;
        Gdi32_SwapBuffers(_hdc);
    }

    // ── INativeContext ────────────────────────────────────────────────────
    public nint GetProcAddress(string proc, int? slot = default)
    {
        IntPtr p = wglGetProcAddress(proc);
        if (p == IntPtr.Zero || p == (IntPtr)1 || p == (IntPtr)2 || p == (IntPtr)3 || p == (IntPtr)(-1))
        {
            // Pre-1.2 entry points live in opengl32.dll, not the ICD.
            p = GetProcAddress_K32(_opengl32, proc);
        }
        return p;
    }

    public bool TryGetProcAddress(string proc, out nint addr, int? slot = default)
    {
        addr = GetProcAddress(proc, slot);
        return addr != IntPtr.Zero;
    }

    public bool IsExtensionPresent(string extensionName) => false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            if (_glrc != IntPtr.Zero) wglDeleteContext(_glrc);
            if (_hdc != IntPtr.Zero) ReleaseDC(_hwnd, _hdc);
            if (_opengl32 != IntPtr.Zero) FreeLibrary(_opengl32);
        }
        catch { /* swallow on shutdown */ }
        _glrc = _hdc = _opengl32 = _hwnd = IntPtr.Zero;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public uint   dwFlags;
        public byte   iPixelType;
        public byte   cColorBits;
        public byte   cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift;
        public byte   cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte   cDepthBits, cStencilBits, cAuxBuffers;
        public byte   iLayerType, bReserved;
        public uint   dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WglCreateContextAttribsARBDelegate(IntPtr hdc, IntPtr hShareContext, int[] attribList);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")]  private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")]  private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll", EntryPoint = "SwapBuffers")] private static extern bool Gdi32_SwapBuffers(IntPtr hdc);
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr hglrc);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr wglGetProcAddress(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryW(string name);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr hModule);
    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress_K32(IntPtr hModule, string name);
}
