// AvaloniaBootstrap.cs
//
// Lives in the WinExe (not in UI.Avalonia) so the Avalonia shell stays
// renderer-agnostic. Wires the live IGpuSurface produced by the Avalonia
// GpuSurfaceControl to the existing DirectX RendererFactory and drives a
// minimal render loop so Phase 2.1 has a visible proof of life.
//
// Phase 2.2+ will replace the test pattern with a real MandelbrotCalculator
// driven by a MainViewModel; this file then shrinks to a few lines that
// construct the view model and let it own the renderer.

using System;
using System.Threading;
using FracturingFog.Abstractions;

namespace FracturingFog;

internal static class AvaloniaBootstrap
{
    private static IFractalRenderer? s_renderer;
    private static Timer? s_renderTimer;
    private static uint[] s_buffer = Array.Empty<uint>();
    private static int s_bufferW;
    private static int s_bufferH;
    private static int s_frame;
    private static readonly object s_gate = new();

    /// <summary>
    /// Called by Avalonia's MainWindow when the native GPU surface is ready.
    /// Constructs the highest-capability DirectX renderer for the surface and
    /// starts a 60 Hz timer that uploads a test pattern and presents it.
    /// </summary>
    public static void OnSurfaceReady(IGpuSurface surface)
    {
        try
        {
            s_renderer = RendererFactory.Create(surface);
        }
        catch (Exception ex)
        {
            // The Avalonia shell stays up so the user can see the failure in
            // the status bar; without the renderer there is just nothing
            // being drawn into the embedded HWND.
            Console.Error.WriteLine($"[AvaloniaBootstrap] Renderer init failed: {ex}");
            return;
        }

        surface.Resized += (_, _) => ResizeBuffer(surface.PixelWidth, surface.PixelHeight);
        surface.HandleLost += (_, _) => Shutdown();

        ResizeBuffer(surface.PixelWidth, surface.PixelHeight);

        // ~60 Hz. System.Threading.Timer runs on a worker thread; the Vortice
        // device context is free-threaded so uploading + presenting from here
        // is safe. Phase 2.3 will move this onto a proper render loop owned
        // by the view model.
        s_renderTimer = new Timer(_ => Tick(), null, 0, 16);
    }

    private static void ResizeBuffer(int w, int h)
    {
        lock (s_gate)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (w == s_bufferW && h == s_bufferH) return;
            s_bufferW = w;
            s_bufferH = h;
            s_buffer = new uint[w * h];
        }
    }

    private static void Tick()
    {
        IFractalRenderer? renderer = s_renderer;
        if (renderer is null) return;

        int w, h;
        uint[] buf;
        lock (s_gate)
        {
            w = s_bufferW;
            h = s_bufferH;
            buf = s_buffer;
        }
        if (w <= 0 || h <= 0 || buf.Length < w * h) return;

        // Animated test pattern: scrolling diagonal bands in BGRA. Proves the
        // upload + swap chain path is alive without dragging the calculator
        // and color theme stack into the Phase 2.1 bootstrap.
        int phase = unchecked(s_frame++) & 0xFF;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                byte r = (byte)((x + phase) & 0xFF);
                byte g = (byte)((y - phase) & 0xFF);
                byte b = (byte)(((x ^ y) + phase) & 0xFF);
                buf[row + x] = (uint)((0xFF << 24) | (r << 16) | (g << 8) | b);
            }
        }

        try
        {
            renderer.UpdateTexture(buf, w, h);
            renderer.Render();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AvaloniaBootstrap] Render tick failed: {ex.Message}");
            Shutdown();
        }
    }

    private static void Shutdown()
    {
        s_renderTimer?.Dispose();
        s_renderTimer = null;
        try { s_renderer?.Dispose(); } catch { /* surface may already be gone */ }
        s_renderer = null;
    }
}
