// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SilkCglContextAdapter.cs
//
// macOS NSOpenGL P/Invoke wrapper that turns a foreign NSView* (the handle
// Avalonia's NativeControlHost hands out on macOS — labelled
// GpuSurfaceKind.CoreAnimationMetalLayer in IGpuSurface but in practice an
// NSView pointer for non-Metal surfaces) into a current OpenGL 3.3 core
// context plus a Silk.NET GL function loader.
//
// Why NSOpenGL and not raw CGL: NSOpenGLContext.setView: is the only public
// path to bind a GL context to an NSView's backing CALayer. CGLContextObj is
// the underlying primitive — NSOpenGLContext.CGLContextObj exposes it for
// callers that need to reach the lower level.
//
// macOS deprecated NSOpenGL/CGL in 10.14 in favour of Metal, but Apple keeps
// the framework shipping in current SDKs (verified through macOS 15). The
// deprecation warning is a compile-time SDK concern; runtime symbols remain.
//
// objc_msgSend signatures: variadic in C, requires one Marshal delegate per
// (return, argument) tuple in managed code. Kept as private nested delegate
// types so the call sites stay readable.

using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;
using FracturingFog.Abstractions;

namespace FracturingFog.Rendering.Silk.Platform;

public sealed class SilkCglContextAdapter : INativeContext, IDisposable
{
    // NSOpenGLPixelFormatAttribute values (NSOpenGL.h).
    private const uint NSOpenGLPFADoubleBuffer   = 5;
    private const uint NSOpenGLPFAColorSize      = 8;
    private const uint NSOpenGLPFAAlphaSize      = 11;
    private const uint NSOpenGLPFADepthSize      = 12;
    private const uint NSOpenGLPFAStencilSize    = 13;
    private const uint NSOpenGLPFAAccelerated    = 73;
    private const uint NSOpenGLPFAOpenGLProfile  = 99;
    private const uint NSOpenGLProfileVersion3_2Core = 0x3200;

    private const int RTLD_LAZY = 1;

    private IntPtr _pixelFormat;
    private IntPtr _context;
    private IntPtr _nsView;
    private IntPtr _openGLFramework;
    private bool _disposed;

    public GL Gl { get; }

    public static SilkCglContextAdapter CreateFor(IGpuSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.Kind != GpuSurfaceKind.CoreAnimationMetalLayer)
            throw new PlatformNotSupportedException(
                $"SilkCglContextAdapter requires a macOS surface; got {surface.Kind}.");
        if (surface.Handle == IntPtr.Zero)
            throw new InvalidOperationException("IGpuSurface.Handle is null (NSView* missing).");
        return new SilkCglContextAdapter(surface.Handle);
    }

    private SilkCglContextAdapter(IntPtr nsView)
    {
        _nsView = nsView;

        // dlopen OpenGL.framework so dlsym can find pre-3.0 GL entry points the
        // ICD does not export through wglGetProcAddress' equivalent.
        _openGLFramework = dlopen("/System/Library/Frameworks/OpenGL.framework/OpenGL", RTLD_LAZY);
        if (_openGLFramework == IntPtr.Zero)
            throw new InvalidOperationException("dlopen OpenGL.framework failed — no GL runtime on this host.");

        // Build NSOpenGLPixelFormat with 3.2 core profile (the highest the
        // legacy GL stack on macOS exposes; SilkGLRenderer's GLSL 330 shaders
        // compile against it because Apple ships GL 4.1 core but locks
        // anything past 3.2 behind that single profile token).
        Span<uint> attrs = stackalloc uint[]
        {
            NSOpenGLPFAAccelerated,    0,
            NSOpenGLPFADoubleBuffer,   0,
            NSOpenGLPFAColorSize,      32,
            NSOpenGLPFAAlphaSize,      8,
            NSOpenGLPFADepthSize,      24,
            NSOpenGLPFAStencilSize,    8,
            NSOpenGLPFAOpenGLProfile,  NSOpenGLProfileVersion3_2Core,
            0
        };

        IntPtr nsOpenGLPixelFormatCls = objc_getClass("NSOpenGLPixelFormat");
        IntPtr nsOpenGLContextCls     = objc_getClass("NSOpenGLContext");
        if (nsOpenGLPixelFormatCls == IntPtr.Zero || nsOpenGLContextCls == IntPtr.Zero)
            throw new InvalidOperationException("Objective-C runtime missing NSOpenGL classes.");

        IntPtr selAlloc = sel_registerName("alloc");
        IntPtr selInitWithAttributes = sel_registerName("initWithAttributes:");
        IntPtr selInitWithFormatShareContext = sel_registerName("initWithFormat:shareContext:");
        IntPtr selSetView = sel_registerName("setView:");
        IntPtr selMakeCurrentContext = sel_registerName("makeCurrentContext");
        IntPtr selClearCurrentContext = sel_registerName("clearCurrentContext");
        IntPtr selFlushBuffer = sel_registerName("flushBuffer");
        IntPtr selRelease = sel_registerName("release");

        // [[NSOpenGLPixelFormat alloc] initWithAttributes:attrs]
        unsafe
        {
            fixed (uint* pAttrs = attrs)
            {
                IntPtr pfAlloc = objc_msgSend_IntPtr(nsOpenGLPixelFormatCls, selAlloc);
                _pixelFormat   = objc_msgSend_IntPtr_IntPtr(pfAlloc, selInitWithAttributes, (IntPtr)pAttrs);
            }
        }
        if (_pixelFormat == IntPtr.Zero)
            throw new InvalidOperationException("NSOpenGLPixelFormat init failed — no GL 3.2 core profile available.");

        // [[NSOpenGLContext alloc] initWithFormat:pf shareContext:nil]
        IntPtr ctxAlloc = objc_msgSend_IntPtr(nsOpenGLContextCls, selAlloc);
        _context        = objc_msgSend_IntPtr_IntPtr_IntPtr(ctxAlloc, selInitWithFormatShareContext, _pixelFormat, IntPtr.Zero);
        if (_context == IntPtr.Zero)
            throw new InvalidOperationException("NSOpenGLContext init failed.");

        // [ctx setView:nsView]; [ctx makeCurrentContext]
        objc_msgSend_void_IntPtr(_context, selSetView, _nsView);
        objc_msgSend_void(_context, selMakeCurrentContext);

        // Cache shared selectors used per-frame.
        _selMakeCurrent  = selMakeCurrentContext;
        _selFlushBuffer  = selFlushBuffer;
        _selClearCurrent = selClearCurrentContext;
        _nsOpenGLCtxCls  = nsOpenGLContextCls;
        _selRelease      = selRelease;

        Gl = GL.GetApi(this);
    }

    private IntPtr _selMakeCurrent;
    private IntPtr _selFlushBuffer;
    private IntPtr _selClearCurrent;
    private IntPtr _selRelease;
    private IntPtr _nsOpenGLCtxCls;

    public void MakeCurrent()
    {
        if (_disposed) return;
        objc_msgSend_void(_context, _selMakeCurrent);
    }

    // S-X6 (2026-06-23) — release ctx from calling thread; see SilkWin32 sibling.
    public void ReleaseCurrent()
    {
        if (_disposed) return;
        // [NSOpenGLContext clearCurrentContext] — class method on NSOpenGLContext.
        objc_msgSend_void(_nsOpenGLCtxCls, _selClearCurrent);
    }

    public void SwapBuffers()
    {
        if (_disposed) return;
        objc_msgSend_void(_context, _selFlushBuffer);
    }

    // ── INativeContext ────────────────────────────────────────────────────
    public nint GetProcAddress(string proc, int? slot = default)
    {
        // dlsym against the dlopen'd OpenGL.framework first, fall back to
        // RTLD_DEFAULT so any process-linked GL symbols also resolve.
        IntPtr p = dlsym(_openGLFramework, proc);
        if (p == IntPtr.Zero) p = dlsym(IntPtr.Zero /* RTLD_DEFAULT */, proc);
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
            // [NSOpenGLContext clearCurrentContext]
            if (_nsOpenGLCtxCls != IntPtr.Zero && _selClearCurrent != IntPtr.Zero)
                objc_msgSend_void(_nsOpenGLCtxCls, _selClearCurrent);
            if (_context     != IntPtr.Zero && _selRelease != IntPtr.Zero)
                objc_msgSend_void(_context,     _selRelease);
            if (_pixelFormat != IntPtr.Zero && _selRelease != IntPtr.Zero)
                objc_msgSend_void(_pixelFormat, _selRelease);
            if (_openGLFramework != IntPtr.Zero) dlclose(_openGLFramework);
        }
        catch { /* swallow on shutdown */ }
        _context = _pixelFormat = _openGLFramework = _nsView = IntPtr.Zero;
    }

    // ── objc_msgSend delegate signatures ─────────────────────────────────
    // Each variant matches a single (return, args) tuple; Marshal pins the
    // correct calling convention per platform ABI. Returned via direct
    // DllImport entry points rather than function pointers so the JIT
    // inlines the call.

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr a);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr a);

    [DllImport("/usr/lib/libobjc.dylib", CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib", CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libSystem.dylib", CharSet = CharSet.Ansi)]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport("/usr/lib/libSystem.dylib", CharSet = CharSet.Ansi)]
    private static extern IntPtr dlsym(IntPtr handle, string symbol);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int dlclose(IntPtr handle);
}
