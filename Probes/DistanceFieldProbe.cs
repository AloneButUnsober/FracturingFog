// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1336: --distancefieldprobe [W H]. For KIFS folds and User Bulb maps (the
// families' default lighting, CPU), the raymarch with and without the baked
// distance-field cache:
//  - the bake: its level, grid, near / interior cells, bricks, memory, DE
//    evaluations and time (synchronous, every core);
//  - an orbiting camera (10 frames, 0.02 rad a frame): the frame time each way and
//    the error of the preview frames against the true-DE frames.
// FF_DF_PNG=<dir> also writes the last frame pair side by side (true | preview).
// Exit 0 always (a measurement); the bounds are asserted by the S1336 tests.

using System;
using System.Diagnostics;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog
{
    internal static class DistanceFieldProbe
    {
        private sealed record Case(string Name, Func<int, int, double, IFractalCalculator> Make);

        private static IFractalCalculator Kifs(KifsFoldKind fold, int w, int h, double theta)
        {
            var fp = new FractalParameters { KifsFold = fold };
            fp.KifsCameraTheta += theta; var lk = fp.Lighting; lk.UseGpuRender = false; fp.Lighting = lk;
            return new KifsCalculator(w, h) { FractalParameters = fp, ColorMap = ColorPalette.BuiltIns[0] };
        }

        private static IFractalCalculator Bulb(int iterations, int w, int h, double theta)
        {
            var fp = new FractalParameters { UserBulbSource = "z^8 + c", UserBulbDEMode = UserBulbDEModeKind.Numerical, UserBulbIterations = iterations };
            fp.UserBulbCameraTheta += theta; var lb = fp.Lighting; lb.UseGpuRender = false; fp.Lighting = lb;
            return new UserBulbCalculator(w, h) { FractalParameters = fp, ColorMap = ColorPalette.BuiltIns[0] };
        }

        // The true-DE frame (left) beside the cached preview (right).
        private static unsafe void SaveSideBySide(uint[] a, uint[] b, int w, int h, string path)
        {
            var px = new uint[2 * w * h];
            for (int y = 0; y < h; y++)
            {
                Array.Copy(a, y * w, px, y * 2 * w, w);
                Array.Copy(b, y * w, px, y * 2 * w + w, w);
            }
            for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000u;
            var info = new SkiaSharp.SKImageInfo(2 * w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
            fixed (uint* p = px)
            {
                using var bmp = new SkiaSharp.SKBitmap();
                bmp.InstallPixels(info, (IntPtr)p, info.RowBytes);
                using var image = SkiaSharp.SKImage.FromBitmap(bmp);
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var fs = System.IO.File.Create(path);
                data.SaveTo(fs);
            }
        }

        public static int Run(string[] args)
        {
            int w = args.Length > 2 ? int.Parse(args[1]) : 480, h = args.Length > 2 ? int.Parse(args[2]) : 270;
            Console.WriteLine($"distancefieldprobe {w}x{h}, CPU, default lighting, orbit 10 frames x 0.02 rad");
            var cases = new[]
            {
                new Case("KIFS Menger", (a, b, t) => Kifs(KifsFoldKind.Menger, a, b, t)),
                new Case("KIFS Octahedron", (a, b, t) => Kifs(KifsFoldKind.Octahedron, a, b, t)),
                new Case("KIFS MandelboxRot", (a, b, t) => Kifs(KifsFoldKind.MandelboxRot, a, b, t)),
                new Case("UserBulb z^8 num 8 it", (a, b, t) => Bulb(8, a, b, t)),
                new Case("UserBulb z^8 num 16 it", (a, b, t) => Bulb(16, a, b, t)),
            };
            foreach (var c in cases)
            {
                // Bake (synchronously, every core) on the first frame's call.
                var cache = new DistanceFieldCache { SynchronousBake = true };
                var first = c.Make(w, h, 0);
                var sw = Stopwatch.StartNew();
                cache.BeginRender();
                using (DistanceFieldCache.Use(cache)) first.Calculate(CancellationToken.None);
                double firstMs = sw.Elapsed.TotalMilliseconds;
                var f = cache.LastField;
                if (f is null) { Console.WriteLine($"  {c.Name,-24} no field (camera too close?)"); continue; }
                Console.WriteLine($"  {c.Name,-24} level {f.Level} (voxel {f.Voxel:G3}), grid {f.GridX}x{f.GridY}x{f.GridZ}, " +
                                  $"near {f.NearCells} (interior {f.InteriorCells}), bricks {f.BudgetedBricks} ({f.BrickBytes / 1048576.0:F1} MB), " +
                                  $"{f.BakeEvaluations / 1e6:F2} M DE, bake {f.BakeMs:F0} ms (coarse {f.CoarseMs:F0}); first frame {firstMs:F0} ms");

                double tFull = 0, tCache = 0, err = 0; long px = 0, over = 0;
                for (int i = 0; i < 10; i++)
                {
                    double theta = 0.02 * i;
                    var a = c.Make(w, h, theta);
                    sw.Restart(); a.Calculate(CancellationToken.None); double ta = sw.Elapsed.TotalMilliseconds;
                    var b = c.Make(w, h, theta);
                    cache.BeginRender();
                    sw.Restart();
                    using (DistanceFieldCache.Use(cache)) b.Calculate(CancellationToken.None);
                    double tb = sw.Elapsed.TotalMilliseconds;
                    if (i == 9 && Environment.GetEnvironmentVariable("FF_DF_PNG") is { Length: > 0 } dir)
                        SaveSideBySide(a.ColorBuffer, b.ColorBuffer, w, h,
                            System.IO.Path.Combine(dir, c.Name.Replace(' ', '_').Replace("^", "") + ".png"));
                    if (i < 2) continue;
                    tFull += ta; tCache += tb;
                    for (int p = 0; p < w * h; p++)
                    {
                        uint u = a.ColorBuffer[p], v = b.ColorBuffer[p];
                        int e = Math.Max(Math.Abs((int)((u >> 16) & 255) - (int)((v >> 16) & 255)),
                                Math.Max(Math.Abs((int)((u >> 8) & 255) - (int)((v >> 8) & 255)), Math.Abs((int)(u & 255) - (int)(v & 255))));
                        err += e; px++; if (e > 32) over++;
                    }
                }
                Console.WriteLine($"  {"",-24} moving: true DE {tFull / 8,7:F1} ms, cached {tCache / 8,7:F1} ms ({tFull / tCache:F2}x); " +
                                  $"preview error mean {err / px:F1} levels, >32 levels {over / (double)px:P1}");
            }
            Console.WriteLine("distancefieldprobe done");
            return 0;
        }
    }
}
