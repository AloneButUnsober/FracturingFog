// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1045 (GPU parity plan G0.5): --d3ddevicelossprobe. The D3D11 presenter
// recovers from a device loss instead of crashing or freezing. The host half
// (kernels re-attached on a new DeviceGeneration) is Server.Tests'
// S1045DeviceLossHostTests; this half needs a window and a D3D11 device, which the
// net10.0 test project cannot load.
//
// A hidden Win32 window carries a real DirectXRenderer; a FractalRenderHost on it
// builds a real MandelbrotGpuKernel through the bootstrap's factory. A device
// loss is injected where a real one surfaces (Present returns, Map and
// ResizeBuffers throw DXGI_ERROR_DEVICE_REMOVED; a real driver reset cannot be
// caused safely). Checked:
//   - each site: no exception, a new device (different native object),
//     DeviceGeneration + 1, the renderer presents / uploads / resizes again;
//   - the host rebuilds its kernel on the new device and a GPU frame from it
//     equals the frame before the loss, pixel for pixel;
//   - a failed recreate (driver still resetting) keeps the renderer lost and
//     silent, retries after the pause, and recovers;
//   - past the per-session cap the renderer stays lost without throwing and
//     says to restart.
//
// Exit 0 = pass or SKIP (no D3D11 hardware device / window); 1 = failure.

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class D3DDeviceLossProbe
    {
        private const int W = 320, H = 240;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(int exStyle, string className, string? windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        private const uint WS_POPUP = 0x80000000;

        private static bool s_ok = true;

        private static void Check(bool cond, string what)
        {
            Console.WriteLine($"  {(cond ? "ok  " : "FAIL")} {what}");
            if (!cond) s_ok = false;
        }

        // A recreate that fails while the driver is still resetting: a fresh
        // renderer on its own window (the first one used its recoveries).
        private static void RetryCase(uint[] frame)
        {
            IntPtr hwnd = CreateWindowExW(0, "STATIC", "ff-device-loss-probe-2", WS_POPUP, 0, 0, W, H,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            try
            {
                using var renderer = new DirectXRenderer(hwnd, W, H);
                renderer.UpdateTexture(frame, W, H);
                renderer.Render();
                renderer.SimulateRecreateFailures(2);
                renderer.SimulateDeviceLoss(DirectXRenderer.DeviceLossSite.Present);
                renderer.Render();
                Check(renderer.DeviceLost && renderer.DeviceGeneration == 0,
                    $"failed recreate: still lost, status \"{renderer.DeviceStatus}\"");
                renderer.Render(); renderer.UpdateTexture(frame, W, H); renderer.Resize(W + 4, H);
                Check(renderer.DeviceLost, "failed recreate: no retry before the pause, nothing thrown");
                renderer.ExpireRecreateDelay(); renderer.Render();
                Check(renderer.DeviceLost, "second recreate fails too");
                renderer.ExpireRecreateDelay(); renderer.Render();
                Check(!renderer.DeviceLost && renderer.DeviceGeneration == 1,
                    $"third attempt recovers (generation {renderer.DeviceGeneration}), status \"{renderer.DeviceStatus}\"");
                renderer.UpdateTexture(frame, W, H); renderer.Render(); renderer.Resize(W, H); renderer.Render();
                Check(!renderer.DeviceLost, "retried device presents, uploads and resizes");
            }
            catch (Exception ex) { Check(false, $"retry case threw {ex.GetType().Name}: {ex.Message}"); }
            finally { DestroyWindow(hwnd); }
        }

        public static int Run()
        {
            IntPtr hwnd = CreateWindowExW(0, "STATIC", "ff-device-loss-probe", WS_POPUP, 0, 0, W, H,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine("d3ddevicelossprobe SKIP: no window.");
                return 0;
            }
            try
            {
                DirectXRenderer renderer;
                try { renderer = new DirectXRenderer(hwnd, W, H); }
                catch (Exception ex)
                {
                    Console.WriteLine($"d3ddevicelossprobe SKIP: no D3D11 device ({ex.Message}).");
                    return 0;
                }
                using var host = new FractalRenderHost(renderer, new FractalViewState(), W, H, ColorPalette.BuiltIns[0]);
                host.GpuKernelFactory = (r, gate) =>
                    r is DirectXRenderer dx && dx.TryGetD3D11(out var dev, out var ctx)
                        ? new MandelbrotGpuKernel(dev, ctx, gate) : null;
                host.UseGpuCompute = true;

                var calc = (MandelbrotCalculator)typeof(FractalRenderHost)
                    .GetField("_calculator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                calc.Resize(W, H);
                calc.CenterX = -0.5; calc.CenterY = 0; calc.Zoom = 1; calc.MaxIterations = 256;

                int[] Frame(out bool gpu)
                {
                    calc.Calculate();
                    gpu = calc.LastFrameUsedGpuCompute;
                    return (int[])calc.IterationBuffer.Clone();
                }

                var frame = new uint[W * H];
                for (int i = 0; i < frame.Length; i++) frame[i] = 0xFF000000u | (uint)(i * 2654435761u >> 8);

                Console.WriteLine($"d3ddevicelossprobe {W}x{H}");
                var before = Frame(out bool gpu0);
                Check(gpu0 && before.Distinct().Count() > 10, "GPU frame before the loss");
                renderer.UpdateTexture(frame, W, H);
                renderer.Render();
                Check(renderer.DeviceGeneration == 0 && !renderer.DeviceLost, "generation 0, not lost");

                int gen = 0;
                foreach (var site in new[] { DirectXRenderer.DeviceLossSite.Present,
                                             DirectXRenderer.DeviceLossSite.Upload,
                                             DirectXRenderer.DeviceLossSite.Resize })
                {
                    renderer.TryGetD3D11(out var oldDev, out _);
                    IntPtr oldPtr = oldDev?.NativePointer ?? IntPtr.Zero;
                    var oldKernel = calc.GpuKernel;
                    renderer.SimulateDeviceLoss(site);
                    try
                    {
                        if (site == DirectXRenderer.DeviceLossSite.Present) renderer.Render();
                        else if (site == DirectXRenderer.DeviceLossSite.Upload) renderer.UpdateTexture(frame, W, H);
                        else renderer.Resize(W + 16 * (gen + 1), H);
                    }
                    catch (Exception ex) { Check(false, $"{site}: threw {ex.GetType().Name}: {ex.Message}"); }
                    gen++;
                    renderer.TryGetD3D11(out var newDev, out _);
                    Check(!renderer.DeviceLost && renderer.DeviceGeneration == gen && newDev != null && newDev.NativePointer != oldPtr,
                        $"{site}: device rebuilt (generation {renderer.DeviceGeneration}), status \"{renderer.DeviceStatus}\"");
                    try
                    {
                        renderer.UpdateTexture(frame, W, H);
                        renderer.Render();
                        renderer.Resize(W, H);
                        renderer.Render();
                        Check(!renderer.DeviceLost, $"{site}: presents, uploads and resizes on the new device");
                    }
                    catch (Exception ex) { Check(false, $"{site}: new device failed: {ex.Message}"); }

                    host.SyncGpuKernelsWithDevice();
                    var after = Frame(out bool gpu1);
                    Check(calc.GpuKernel != null && !ReferenceEquals(calc.GpuKernel, oldKernel),
                        $"{site}: host rebuilt the kernel");
                    Check(gpu1 && after.SequenceEqual(before),
                        $"{site}: GPU frame on the new device equals the frame before the loss");
                }

                // Past the cap (3 recoveries a session) the renderer stays lost.
                renderer.SimulateDeviceLoss(DirectXRenderer.DeviceLossSite.Present);
                try
                {
                    renderer.Render();
                    renderer.ExpireRecreateDelay();
                    renderer.Render(); renderer.UpdateTexture(frame, W, H); renderer.Resize(W + 8, H);
                    Check(renderer.DeviceLost && renderer.DeviceGeneration == gen
                          && renderer.DeviceStatus?.Contains("restart", StringComparison.Ordinal) == true,
                        $"past the cap: stays lost without throwing, status \"{renderer.DeviceStatus}\"");
                }
                catch (Exception ex) { Check(false, $"past the cap: threw {ex.Message}"); }

                RetryCase(frame);

                Console.WriteLine(s_ok ? "d3ddevicelossprobe OK" : "d3ddevicelossprobe FAIL");
                return s_ok ? 0 : 1;
            }
            finally
            {
                DestroyWindow(hwnd);
            }
        }
    }
}
