// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SilkRendererFactory.cs
// Construction helpers for SilkGLRenderer.
//
// The renderer itself is context-agnostic (it consumes an already-current
// Silk.NET GL instance + present hooks). Context creation is platform-
// specific:
//
//   Windows : WGL via the existing HWND from GpuSurfaceControl, or the GL
//             context Avalonia.OpenGL maintains for its Skia compositor.
//   Linux   : GLX via the X11 Window XID, or EGL for Wayland.
//   macOS   : CGL via NSOpenGLView, or Metal-on-MoltenGL bridge.
//
// Avalonia's OpenGL platform integration (`Avalonia.OpenGL.GlInterface`)
// already produces a function-pointer source compatible with Silk.NET via
// `GL.GetApi(IGLContext)` from Silk.NET.OpenGL — so the Avalonia shell can
// stand up a renderer with no extra P/Invoke.
//
// This factory exposes the explicit `Create` entry the host calls once it has
// arranged a context. Native-surface adoption helpers (WGL / GLX / EGL) live
// outside this assembly so platform natives stay opt-in.

using System;
using Silk.NET.OpenGL;
using FracturingFog.Abstractions;

namespace FracturingFog.Rendering.Silk;

public static class SilkRendererFactory
{
    /// <summary>
    /// Builds a <see cref="SilkGLRenderer"/> against an already-current GL
    /// context. <paramref name="makeCurrent"/> and <paramref name="swap"/>
    /// are invoked on every UpdateTexture / Render call; supply no-ops if the
    /// host pins the context to one thread permanently.
    /// </summary>
    public static SilkGLRenderer Create(
        GL gl,
        IGpuSurface surface,
        Action makeCurrent,
        Action swap,
        Action? releaseCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return new SilkGLRenderer(
            gl,
            System.Math.Max(1, surface.PixelWidth),
            System.Math.Max(1, surface.PixelHeight),
            makeCurrent,
            swap,
            releaseCurrent);
    }

    /// <summary>
    /// Probe text for the System Info / About dialog before a renderer is
    /// constructed. Cheap; calls no GL.
    /// </summary>
    public static string ProbeDescription() => "OpenGL 3.3 (Silk.NET)";
}
