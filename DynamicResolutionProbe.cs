// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1333: --dynresprobe. Drag responsiveness through a real FractalRenderHost, with
// dynamic preview resolution off (the fixed ¼ → ½ → full ladder) and on. A drag
// step pans the view a little and fires a progressive trigger; the next step
// fires as soon as the first image of the previous one reached the renderer (a
// user dragging as fast as frames come back). Reported per view: the median and
// 90th-percentile time from trigger to first image, and the median first-stage
// size. Views: shallow CPU, deep CPU (perturbation), Relief 3D, and shallow on the
// D3D11 GPU kernel when a hardware device exists. Measurement only (exit 0).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class DynamicResolutionProbe
    {
        private sealed class FirstImageRenderer : IFractalRenderer
        {
            public readonly AutoResetEvent Uploaded = new(false);
            private uint[] _last = Array.Empty<uint>();
            // A final-stage trigger first re-presents the frame already on screen (the
            // stale hold); that is not new feedback, so an upload equal to the last
            // one doesn't count.
            public void UpdateTexture(uint[] colorBuffer, int width, int height)
            {
                var span = colorBuffer.AsSpan(0, width * height);
                if (span.SequenceEqual(_last)) return;
                _last = span.ToArray();
                Uploaded.Set();
            }
            public void Render() { }
            public void Resize(int width, int height) { }
            public string RendererDescription => "probe";
            public bool VSync { get; set; }
            public void Dispose() { }
        }

        public static int Run()
        {
            const int W = 1920, H = 1080, Steps = 30;
            ID3D11Device? device = null; ID3D11DeviceContext? ctx = null;
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out device, out _, out ctx);
            bool gpu = hr.Success && device != null && ctx != null;

            var views = new List<(string Name, Func<FractalViewState> State, bool Gpu)>
            {
                ("shallow CPU (seahorse 1e3, 1000 it)", () => new FractalViewState
                    { FractalType = FractalType.Mandelbrot, CenterX = -0.7453, CenterY = 0.1127, Zoom = 1000 }, false),
                // "Deep Lightning in Space" (#1267's region; structure at 20000 iterations).
                ("deep CPU (7.6e26, perturbation, 20000 it)", () => new FractalViewState
                    { FractalType = FractalType.Mandelbrot,
                      CenterX = -1.4181949444785762, CenterXLo = -7.4415882477902279E-17,
                      CenterY = -0.12700786443815276, CenterYLo = -2.3429499355532375E-18,
                      Zoom = 7.58348E+26, PreferredIterations = 20000 }, false),
                ("Relief 3D (raymarch, seahorse 1e2)", () =>
                {
                    var s = new FractalViewState { FractalType = FractalType.Mandelbrot, CenterX = -0.7453, CenterY = 0.1127, Zoom = 100 };
                    s.FractalParameters.Relief2DEnabled = true;
                    s.FractalParameters.Relief2DRaymarch = true;
                    return s;
                }, false),
            };
            if (gpu) views.Add(("shallow GPU D3D11 (seahorse 1e3)", () => new FractalViewState
                { FractalType = FractalType.Mandelbrot, CenterX = -0.7453, CenterY = 0.1127, Zoom = 1000 }, true));
            else Console.WriteLine("dynresprobe: no D3D11 hardware device, GPU view skipped");

            var gpuTheme = ColorPalette.BuiltIns.First(m => m is FracturingFog.Interefaces.IGpuHlslPalette);
            Console.WriteLine($"dynresprobe {W}x{H}, {Steps} drag steps, target 33 ms");
            string? only = Environment.GetEnvironmentVariable("FF_DYNRES_VIEW");   // substring of a view name
            foreach (var v in views.Where(v => only == null || v.Name.Contains(only, StringComparison.OrdinalIgnoreCase)))
            foreach (bool dynamic in new[] { false, true })
            {
                var r = new FirstImageRenderer();
                var state = v.State();
                using var host = new FractalRenderHost(r, state, W, H, v.Gpu ? gpuTheme : ColorPalette.BuiltIns[0]);
                if (v.Gpu)
                {
                    host.GpuKernelFactory = (_, gate) => new MandelbrotGpuKernel(device!, ctx!, gate);
                    host.UseGpuCompute = true;
                }
                host.DynamicPreviewResolution = dynamic;
                host.InteractiveTargetMs = 33;
                using (var done = new ManualResetEventSlim())
                {
                    host.FrameCompleted += (_, _) => done.Set();
                    long w0 = Stopwatch.GetTimestamp();
                    host.Trigger(progressive: false);   // warm-up; also measures the final
                    bool ok = done.Wait(TimeSpan.FromMinutes(5));
                    if (Environment.GetEnvironmentVariable("FF_DYNRES_TRACE") == "1")
                        Console.WriteLine($"    warm-up {(ok ? "" : "TIMEOUT ")}{(Stopwatch.GetTimestamp() - w0) * 1000.0 / Stopwatch.Frequency:F0} ms, "
                            + $"{host.SnapshotFrame(out int sw0, out int sh0).Take(sw0 * sh0).Distinct().Count()} colours, {host.Mandelbrot.MaxIterations} iterations");
                }
                var lat = new List<double>(); var px = new List<double>();
                double step = 3.5 / state.Zoom / W * 7;   // 7 pixels per step
                for (int i = 0; i < Steps; i++)
                {
                    state.SetCenter(state.GetCenter().Translate(step, 0));   // exact at any depth
                    r.Uploaded.Reset();
                    int level = host.DynamicResolution.Choose(W, H);   // what this trigger will pick
                    long t0 = Stopwatch.GetTimestamp();
                    host.Trigger(progressive: true);
                    if (!r.Uploaded.WaitOne(TimeSpan.FromMinutes(2))) { Console.WriteLine("  timeout"); break; }
                    lat.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                    if (Environment.GetEnvironmentVariable("FF_DYNRES_TRACE") == "1")
                        Console.WriteLine($"    step {i}: level {level} {lat[^1]:F0} ms prev {host.DynamicResolution.PreviewMsPerPixel:E2} final {host.DynamicResolution.FinalMsPerPixel:E2}");
                    var (pw, ph) = DynamicResolution.PreviewDims(W, H, dynamic ? level : DynamicResolution.DefaultLevel);
                    px.Add((double)pw * ph / (W * H));
                }
                if (lat.Count == 0) continue;
                lat.Sort(); px.Sort();
                double Med(List<double> l) => l[l.Count / 2];
                Console.WriteLine($"  {v.Name}, dynamic {(dynamic ? "on " : "off")}: first image median {Med(lat):F0} ms, p90 {lat[(int)(lat.Count * 0.9)]:F0} ms, "
                    + $"first stage {Med(px) * 100:F1}% of the pixels, level {host.DynamicResolution.Level}");
            }
            ctx?.Dispose(); device?.Dispose();
            return 0;
        }
    }
}
