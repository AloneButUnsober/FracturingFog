// SkiaCpuRenderer.cs
//
// CPU-side Skia implementation of IFractalRenderer. Wraps a pinned uint[] BGRA
// buffer into an SKBitmap with SKColorType.Bgra8888 — the byte layout the
// calculator already produces — and hands the resulting SKImage to a host-
// supplied present delegate. The host (Avalonia DrawingContext, WinForms
// PictureBox, etc.) decides how to actually paint pixels to the screen.
//
// No GL context, no swap chain, no native window. The trade-off vs.
// SilkGLRenderer is straight-line CPU upload + memcpy through Skia's
// rasteriser; the win is that this renderer survives on hosts where the GL
// stack is broken (headless build agents, locked-down VMs, old Mesa).
//
// Geometry is one stretched bitmap blit into the host surface — equivalent
// to the textured triangle the DX and Silk backends draw, just at the Skia
// layer rather than the GPU pipeline. Filtering matches Skia's default
// (linear). Bicubic / nearest can be added by exposing SKSamplingOptions.

using System;
using System.Runtime.InteropServices;
using SkiaSharp;
using FracturingFog;

namespace FracturingFog.Rendering.Skia;

/// <summary>
/// Host-bound delegate invoked once per <see cref="Render"/> call. The
/// adapter owns the <see cref="SKImage"/> for the duration of the call only —
/// hosts must consume or copy it synchronously and must not dispose it.
/// </summary>
/// <param name="image">The current frame as an immutable Skia image view
/// over the renderer's pinned buffer.</param>
/// <param name="width">Surface width in pixels at present time.</param>
/// <param name="height">Surface height in pixels at present time.</param>
public delegate void SkiaPresent(SKImage image, int width, int height);

public sealed class SkiaCpuRenderer : IFractalRenderer
{
    private readonly SkiaPresent _present;
    private SKBitmap? _bitmap;
    private GCHandle _pinHandle;
    private uint[]? _ownBuffer;
    private int _width;
    private int _height;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private bool _disposed;

    public string RendererDescription { get; }

    public SkiaCpuRenderer(int width, int height, SkiaPresent present)
    {
        ArgumentNullException.ThrowIfNull(present);
        _present = present;
        _surfaceWidth  = System.Math.Max(1, width);
        _surfaceHeight = System.Math.Max(1, height);

        RendererDescription = $"Skia ({SkiaSharpVersion.Describe()} CPU — BGRA8888)";
    }

    public unsafe void UpdateTexture(uint[] colorBuffer, int width, int height)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(colorBuffer);
        if (colorBuffer.Length < width * height)
            throw new ArgumentException("colorBuffer too small for given dimensions", nameof(colorBuffer));

        // Rebuild the SKBitmap only when the source dimensions change. The
        // SKImage shares the underlying pixels — re-pinning is the price of
        // resize. Caller-stable buffers (steady-state animation) cost one
        // copy and one GCHandle.Alloc per resize.
        if (_bitmap == null || width != _width || height != _height)
        {
            DisposeBitmap();

            // Copy the caller's array into a private buffer we own. Pinning
            // the caller's array would prevent the calculator from swapping
            // frames in place; the per-frame copy is the same as the
            // glTexSubImage2D upload Silk performs and stays off the GPU.
            _ownBuffer = new uint[width * height];
            Array.Copy(colorBuffer, _ownBuffer, _ownBuffer.Length);
            _pinHandle = GCHandle.Alloc(_ownBuffer, GCHandleType.Pinned);

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            _bitmap = new SKBitmap();
            if (!_bitmap.InstallPixels(info, _pinHandle.AddrOfPinnedObject(),
                                       info.RowBytes, releaseProc: null, context: null))
            {
                throw new InvalidOperationException("SKBitmap.InstallPixels failed for pinned BGRA buffer.");
            }
            _width  = width;
            _height = height;
        }
        else
        {
            // In-place pixel refresh on the pinned buffer; SKImage view sees
            // the new bytes on next consumer read.
            Buffer.BlockCopy(colorBuffer, 0, _ownBuffer!, 0, width * height * sizeof(uint));
        }
    }

    public void Render()
    {
        if (_disposed || _bitmap == null) return;
        using SKImage snapshot = SKImage.FromBitmap(_bitmap);
        _present(snapshot, _surfaceWidth, _surfaceHeight);
    }

    public void Resize(int width, int height)
    {
        _surfaceWidth  = System.Math.Max(1, width);
        _surfaceHeight = System.Math.Max(1, height);
        // Source bitmap size is independent of the present surface size; the
        // host scales SKImage → surface rect via SKCanvas.DrawImage.
    }

    private void DisposeBitmap()
    {
        try { _bitmap?.Dispose(); } catch { /* ignore */ }
        _bitmap = null;
        if (_pinHandle.IsAllocated)
        {
            try { _pinHandle.Free(); } catch { /* ignore */ }
        }
        _ownBuffer = null;
        _width = _height = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeBitmap();
    }
}
