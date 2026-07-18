// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SkiaRendererFactory.cs
//
// Mirror of SilkRendererFactory for the Skia sibling backend. Exposes a
// single Create entry point that pairs the IGpuSurface with a host-supplied
// present delegate. The factory is intentionally surface-agnostic — Skia
// does not care what kind of native handle the surface holds because the
// upload path runs entirely on the CPU side and presentation is delegated
// back to the host via the SkiaPresent callback.
//
// The host (Avalonia, WinForms) keeps ownership of the actual painting
// surface, which lets this backend slot in next to any existing 2D
// rasteriser without duplicating window plumbing.

using System;
using SkiaSharp;
using FracturingFog;
using FracturingFog.Abstractions;

namespace FracturingFog.Rendering.Skia;

public static class SkiaRendererFactory
{
    /// <summary>
    /// Constructs a CPU-side <see cref="SkiaCpuRenderer"/> sized to the
    /// supplied surface. The renderer ignores <see cref="IGpuSurface.Kind"/>
    /// because Skia never touches the native handle directly — the host's
    /// <paramref name="present"/> callback owns final pixel placement.
    /// </summary>
    public static IFractalRenderer Create(IGpuSurface surface, SkiaPresent present)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(present);
        return new SkiaCpuRenderer(surface.PixelWidth, surface.PixelHeight, present);
    }

    /// <summary>
    /// Static probe used by hosts that want to log the Skia version without
    /// constructing a renderer (mirrors <c>SilkRendererFactory.ProbeDescription</c>).
    /// </summary>
    public static string ProbeDescription() => $"Skia ({SkiaSharpVersion.Describe()} CPU — BGRA8888)";
}
