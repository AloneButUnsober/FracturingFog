// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1338: --oidnprobe [W H]. Is Intel Open Image Denoise worth it on FF's
// sample-driven renders? For each case, a low-sample render, then that render
// denoised by OIDN and by the À-Trous denoiser, each scored against a full-sample
// reference by PSNR (RGB) and SSIM (luma, 8x8 windows):
//  - Buddhabrot family (the visual-regression set's four): 1/8 of the samples vs the
//    default count;
//  - the 3D families (the set's seven): 1 sample per pixel vs a 4x4 supersample;
//  - Mandelbulb with thin-lens DoF: 4 lens taps vs 64 (real sample noise);
//  - a relief raymarch over a Mandelbrot: supersample 1 vs 4.
// "aux" rows use the render's albedo + normal guides where the path captures them.
// FF_OIDN_PNG=<dir> also writes each case as a strip: low | A-Trous | OIDN | reference.
// Needs the OIDN runtime (FF_OIDN_DIR or the in-app install). Exit 0 always.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog
{
    internal static class OidnProbe
    {
        public static int Run(string[] args)
        {
            int w = args.Length > 2 ? int.Parse(args[1]) : 320, h = args.Length > 2 ? int.Parse(args[2]) : 180;
            if (!OidnDenoiser.IsAvailable) { Console.WriteLine("oidnprobe: " + OidnDenoiser.LoadError); return 0; }
            Console.WriteLine($"oidnprobe {w}x{h}, OIDN {OidnDenoiser.Version} from {OidnDenoiser.LoadedFrom}");
            Console.WriteLine($"  {"case",-34} {"low",-15} {"low+A-Trous",-15} {"low+OIDN",-15}  (PSNR dB / SSIM vs reference)");

            foreach (var fam in new[] { FractalType.BuddhaBrot, FractalType.Nebulabrot, FractalType.AntiBuddhabrot, FractalType.AntiNebulabrot })
            {
                uint[] Buddha(int samples)
                {
                    var fp = new FractalParameters { BuddhaSamples = samples };
                    var c = MakeCalc(fam, w, h, fp);
                    c.Calculate(CancellationToken.None);
                    return Copy(c.ColorBuffer, w * h);
                }
                int full = new FractalParameters().BuddhaSamples;
                Report($"{fam} 1/8 samples", Buddha(full), Buddha(full / 8), null, null, null, w, h);
            }

            foreach (var fam in new[] { FractalType.Mandelbulb, FractalType.Mandelbox, FractalType.Kifs, FractalType.QuaternionJulia,
                                        FractalType.QuaternionMandelbrot, FractalType.BicomplexMandelbrot, FractalType.Kleinian })
            {
                var reference = Render3D(fam, w, h, 4, null);
                var low = Render3D(fam, w, h, 1, null);
                Report($"{fam} 1 spp (ref 16)", reference, low, null, null, null, w, h);
                if (Environment.GetEnvironmentVariable("FF_OIDN_AUX3D") == "1")
                {
                    // Experiment: albedo ≈ a lights-off frame (ambient only), normals from the AOV view.
                    var flat = Render3D(fam, w, h, 1, fp =>
                    {
                        var l = fp.Lighting;
                        l.Light1.Intensity = 0; l.Light2.Intensity = 0; l.Light3.Intensity = 0;
                        l.AmbientStrength = 1; l.AoStrength = 0; l.ShadowSteps = 0; l.SpecularStrength = 0;
                        fp.Lighting = l;
                    });
                    var nimg = Render3D(fam, w, h, 1, fp => { var l = fp.Lighting; l.DebugAov = AovView.Normals; fp.Lighting = l; });
                    var nrm = new float[w * h * 3];
                    for (int i = 0; i < w * h; i++)
                    {
                        uint q = nimg[i];
                        nrm[i * 3] = ((q >> 16) & 255) / 127.5f - 1; nrm[i * 3 + 1] = ((q >> 8) & 255) / 127.5f - 1; nrm[i * 3 + 2] = (q & 255) / 127.5f - 1;
                    }
                    Report("  ... aux (flat albedo + normal)", reference, low, flat, nrm, null, w, h);
                }
            }

            {
                void Dof(ref LightingFxData fx, int taps) { fx.DofThinLens = true; fx.DofAperture = 0.08; fx.DofSamples = taps; }
                var reference = Render3D(FractalType.Mandelbulb, w, h, 1, fp => { var l = fp.Lighting; Dof(ref l, 64); fp.Lighting = l; });
                var low = Render3D(FractalType.Mandelbulb, w, h, 1, fp => { var l = fp.Lighting; Dof(ref l, 4); fp.Lighting = l; });
                Report("Mandelbulb thin-lens DoF 4 taps (64)", reference, low, null, null, null, w, h);
            }

            {
                var (albedo, height) = ReliefSource(w, h);
                uint[] Relief(int ss, out HeightfieldRaymarch2D.ReliefAovBuffers aov)
                {
                    var fp = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DSupersample = ss };
                    var l = fp.Lighting; l.UseGpuRender = false; fp.Lighting = l;
                    aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h, captureComponents: true);
                    var dst = new uint[w * h];
                    HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, fp, dst, out _, null, aov);
                    return dst;
                }
                var reference = Relief(4, out _);
                var low = Relief(1, out var aov);
                Report("Relief over Mandelbrot ss 1 (ref 4)", reference, low, null, null, null, w, h);
                Report("  ... aux (albedo + normal)", reference, low, aov.Albedo, aov.NormalXyz, aov.Depth, w, h);
            }
            // Cost: one denoise of a frame (the filter is cached after the first call per size).
            foreach (var (cw, ch) in new[] { (640, 360), (1920, 1080) })
            {
                var img = new uint[cw * ch];
                var rng = new Random(1);
                for (int i = 0; i < img.Length; i++) img[i] = 0xFF000000u | (uint)rng.Next(0x1000000);
                OidnDenoiser.TryDenoiseBgra((uint[])img.Clone(), null, null, cw, ch, out _);   // warm-up (filter build)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int r = 0; r < 3; r++) OidnDenoiser.TryDenoiseBgra((uint[])img.Clone(), null, null, cw, ch, out _);
                Console.WriteLine($"  OIDN cost {cw}x{ch}: {sw.Elapsed.TotalMilliseconds / 3:F0} ms a frame ({Environment.ProcessorCount} threads)");
            }
            Console.WriteLine("oidnprobe done");
            return 0;
        }

        private static IFractalCalculator MakeCalc(FractalType type, int w, int h, FractalParameters fp)
        {
            var l = fp.Lighting; l.UseGpuRender = false; fp.Lighting = l;
            IFractalCalculator c = type switch
            {
                FractalType.BuddhaBrot => new BuddhabrotCalculator(w, h),
                FractalType.Nebulabrot => new NebulabrotCalculator(w, h),
                FractalType.AntiBuddhabrot => new AntiBuddhabrotCalculator(w, h),
                FractalType.AntiNebulabrot => new AntiNebulabrotCalculator(w, h),
                FractalType.Mandelbulb => new MandelbulbCalculator(w, h),
                FractalType.Mandelbox => new MandelboxCalculator(w, h),
                FractalType.Kifs => new KifsCalculator(w, h),
                FractalType.QuaternionJulia => new QuatJuliaCalculator(w, h),
                FractalType.QuaternionMandelbrot => new QuatMandelbrotCalculator(w, h),
                FractalType.BicomplexMandelbrot => new BicomplexMandelbrotCalculator(w, h),
                _ => new KleinianCalculator(w, h),
            };
            ((dynamic)c).FractalParameters = fp;
            c.ColorMap = ColorPalette.BuiltIns[0];
            return c;
        }

        // A 3D frame at ss x ss supersampling (box-downsampled).
        private static uint[] Render3D(FractalType fam, int w, int h, int ss, Action<FractalParameters>? tweak)
        {
            var fp = new FractalParameters();
            tweak?.Invoke(fp);
            var c = MakeCalc(fam, w * ss, h * ss, fp);
            c.Calculate(CancellationToken.None);
            var big = c.ColorBuffer;
            if (ss == 1) return Copy(big, w * h);
            var o = new uint[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int j = 0; j < ss; j++)
                        for (int i = 0; i < ss; i++)
                        {
                            uint p = big[(y * ss + j) * w * ss + x * ss + i];
                            r += (int)((p >> 16) & 255); g += (int)((p >> 8) & 255); b += (int)(p & 255);
                        }
                    int n = ss * ss;
                    o[y * w + x] = 0xFF000000u | (uint)((r + n / 2) / n) << 16 | (uint)((g + n / 2) / n) << 8 | (uint)((b + n / 2) / n);
                }
            return o;
        }

        private static (uint[] Albedo, float[] Height) ReliefSource(int w, int h)
        {
            var m = new MandelbrotCalculator(w, h) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0, CenterX = -0.5 };
            m.Calculate(CancellationToken.None);
            var albedo = Copy(m.ColorBuffer, w * h);
            var smooth = m.SmoothBuffer;
            var height = new float[w * h];
            float max = 1e-6f;
            for (int i = 0; i < w * h; i++) max = Math.Max(max, smooth[i]);
            for (int i = 0; i < w * h; i++) height[i] = smooth[i] / max;
            return (albedo, height);
        }

        private static uint[] Copy(uint[] a, int n) { var c = new uint[n]; Array.Copy(a, c, n); return c; }

        private static void Report(string name, uint[] reference, uint[] low, uint[]? albedo, float[]? normal, float[]? depth, int w, int h)
        {
            var atrous = AtrousDenoiser.Denoise(low, w, h, new AtrousParams { Iterations = 3 }, normal, depth);
            var oidn = Copy(low, w * h);
            bool ok = OidnDenoiser.TryDenoiseBgra(oidn, albedo, normal, w, h, out var err);
            string Score(uint[] img) => $"{Psnr(reference, img),5:F1} / {Ssim(reference, img, w, h):F3}";
            Console.WriteLine($"  {name,-34} {Score(low),-15} {Score(atrous),-15} {(ok ? Score(oidn) : "failed: " + err),-15}");
            if (Environment.GetEnvironmentVariable("FF_OIDN_PNG") is { Length: > 0 } dir)
                SaveStrip(new[] { low, atrous, oidn, reference }, w, h,
                    System.IO.Path.Combine(dir, new string(name.Trim().Where(char.IsLetterOrDigit).ToArray()) + ".png"));
        }

        private static unsafe void SaveStrip(uint[][] imgs, int w, int h, string path)
        {
            int gap = 4, sw = imgs.Length * w + (imgs.Length - 1) * gap;
            var px = new uint[sw * h];
            Array.Fill(px, 0xFF404040u);
            for (int k = 0; k < imgs.Length; k++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        px[y * sw + k * (w + gap) + x] = imgs[k][y * w + x] | 0xFF000000u;
            var info = new SkiaSharp.SKImageInfo(sw, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
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

        private static double Psnr(uint[] a, uint[] b)
        {
            double se = 0;
            for (int i = 0; i < a.Length; i++)
                for (int s = 0; s < 24; s += 8)
                {
                    double d = (int)((a[i] >> s) & 255) - (int)((b[i] >> s) & 255);
                    se += d * d;
                }
            double mse = se / (a.Length * 3.0);
            return mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
        }

        private static double Luma(uint p) => 0.299 * ((p >> 16) & 255) + 0.587 * ((p >> 8) & 255) + 0.114 * (p & 255);

        // Mean SSIM over non-overlapping 8x8 luma windows (Wang et al. 2004 constants).
        private static double Ssim(uint[] a, uint[] b, int w, int h)
        {
            const double c1 = 6.5025, c2 = 58.5225;
            double sum = 0; int n = 0;
            for (int by = 0; by + 8 <= h; by += 8)
                for (int bx = 0; bx + 8 <= w; bx += 8)
                {
                    double ma = 0, mb = 0;
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    { ma += Luma(a[(by + y) * w + bx + x]); mb += Luma(b[(by + y) * w + bx + x]); }
                    ma /= 64; mb /= 64;
                    double va = 0, vb = 0, cov = 0;
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    {
                        double da = Luma(a[(by + y) * w + bx + x]) - ma, db = Luma(b[(by + y) * w + bx + x]) - mb;
                        va += da * da; vb += db * db; cov += da * db;
                    }
                    va /= 63; vb /= 63; cov /= 63;
                    sum += (2 * ma * mb + c1) * (2 * cov + c2) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
                    n++;
                }
            return sum / Math.Max(1, n);
        }
    }
}
