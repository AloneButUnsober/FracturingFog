// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1337: --temporallightingprobe [W H]. For each CPU 3D raymarch family, a moving
// camera (a slow dolly + pan, 10 frames) rendered in full and with the temporal
// AO / shadow reuse, at two lighting loads (AO 5 + one light's shadows at 32 steps;
// AO 8 + three lights' shadows at 64 steps): the frame time each way, the share of
// shaded pixels reused, the error of the moving frames against the full ones, and
// that the settle frame is the full frame exactly.
// Exit 0 = every settle frame matched; 1 = one did not.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog
{
    internal static class TemporalLightingProbe
    {
        private enum Family { Mandelbulb, Mandelbox, Menger, QuatJulia, QuatMandelbrot, Kleinian, Bicomplex, Coquaternion, DualOrbitVolume, UserBulb }

        private static IFractalCalculator Calc(Family fam, int w, int h, double zoom, double pan, LightingFxData fx)
        {
            var fp = new FractalParameters { Lighting = fx, UserBulbSource = "z^8 + c" };
            IFractalCalculator c = fam switch
            {
                Family.Mandelbulb      => new MandelbulbCalculator(w, h) { FractalParameters = fp },
                Family.Mandelbox       => new MandelboxCalculator(w, h) { FractalParameters = fp },
                Family.Menger          => new KifsCalculator(w, h) { FractalParameters = fp },
                Family.QuatJulia       => new QuatJuliaCalculator(w, h) { FractalParameters = fp },
                Family.QuatMandelbrot  => new QuatMandelbrotCalculator(w, h) { FractalParameters = fp },
                Family.Kleinian        => new KleinianCalculator(w, h) { FractalParameters = fp },
                Family.Bicomplex       => new BicomplexMandelbrotCalculator(w, h) { FractalParameters = fp },
                Family.Coquaternion    => new CoquaternionMandelbrotCalculator(w, h) { FractalParameters = fp },
                Family.DualOrbitVolume => new DualOrbitVolumeCalculator(w, h) { FractalParameters = fp },
                _                      => new UserBulbCalculator(w, h) { FractalParameters = fp },
            };
            c.ColorMap = ColorPalette.BuiltIns[0];
            c.Zoom = zoom;
            c.CenterX = pan;
            return c;
        }

        public static int Run(string[] args)
        {
            int w = args.Length > 2 ? int.Parse(args[1]) : 480, h = args.Length > 2 ? int.Parse(args[2]) : 270;
            int failures = 0;
            Console.WriteLine($"temporallightingprobe {w}x{h}, 10 moving frames (zoom x1.01 + pan per frame), period 4");
            foreach (bool heavy in new[] { false, true })
            {
                var fx = LightingFxData.CreateDefault();
                fx.UseGpuRender = false;
                fx.SsaoSamples = 0;
                fx.AoSamples = heavy ? 8 : 5; fx.AoStrength = 1.0;
                fx.ShadowSteps = heavy ? 64 : 32; fx.ShadowSoftK = 16;
                if (heavy)
                {
                    fx.Light2.Intensity = 0.6; fx.Light3.Intensity = 0.4;
                    fx.ShadowLightMask = 0x7;
                }
                Console.WriteLine(heavy ? "  AO 8, shadows 64 steps x 3 lights:" : "  AO 5, shadows 32 steps x 1 light:");
                foreach (var fam in Enum.GetValues<Family>())
                {
                    var cache = new TemporalLighting();
                    double tFull = 0, tTemp = 0, err = 0; long px = 0, over = 0, reused = 0, computed = 0;
                    for (int f = 0; f < 10; f++)
                    {
                        double zoom = Math.Pow(1.01, f), pan = 0.003 * f;
                        var a = Calc(fam, w, h, zoom, pan, fx);
                        var sw = Stopwatch.StartNew();
                        a.Calculate(CancellationToken.None);
                        double ta = sw.Elapsed.TotalMilliseconds;
                        var b = Calc(fam, w, h, zoom, pan, fx);
                        cache.BeginRender();
                        sw.Restart();
                        using (TemporalLighting.Use(cache)) b.Calculate(CancellationToken.None);
                        double tb = sw.Elapsed.TotalMilliseconds;
                        if (f < 2) continue;   // warm-up and the history-less first frame
                        tFull += ta; tTemp += tb;
                        reused += cache.LastReused; computed += cache.LastComputed;
                        for (int i = 0; i < w * h; i++)
                        {
                            uint p = a.ColorBuffer[i], q = b.ColorBuffer[i];
                            int e = Math.Max(Math.Abs((int)((p >> 16) & 255) - (int)((q >> 16) & 255)),
                                    Math.Max(Math.Abs((int)((p >> 8) & 255) - (int)((q >> 8) & 255)), Math.Abs((int)(p & 255) - (int)(q & 255))));
                            err += e; px++; if (e > 8) over++;
                        }
                    }
                    var full = Calc(fam, w, h, Math.Pow(1.01, 9), 0.027, fx);
                    full.Calculate(CancellationToken.None);
                    var settle = Calc(fam, w, h, Math.Pow(1.01, 9), 0.027, fx);
                    cache.BeginRender(); cache.Settle = true;
                    using (TemporalLighting.Use(cache)) settle.Calculate(CancellationToken.None);
                    bool same = full.ColorBuffer.AsSpan(0, w * h).SequenceEqual(settle.ColorBuffer.AsSpan(0, w * h));
                    if (!same) failures++;
                    Console.WriteLine($"    {fam,-16} full {tFull / 8,7:F1} ms, temporal {tTemp / 8,7:F1} ms ({tFull / tTemp:F2}x); " +
                                      $"reused {reused / (double)Math.Max(1, reused + computed):P0}; error mean {err / px:F2}, >8 levels {over / (double)px:P1}; " +
                                      $"settle {(same ? "== full" : "DIFFERS")}");
                }
            }
            Console.WriteLine(failures == 0 ? "temporallightingprobe OK" : $"temporallightingprobe FAIL: {failures} settle frame(s) differ");
            return failures == 0 ? 0 : 1;
        }
    }
}
