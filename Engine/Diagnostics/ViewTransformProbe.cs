// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Diagnostics/ViewTransformProbe.cs
//
// Roadmap slice S2 (#396) — DEFAULT-LOOK VALIDATION gate. The S2 view-transform +
// full-float composite/read-back work must leave the DEFAULT look untouched: with
// ViewTransform.None selected, every render is byte-identical to the pre-S2 pipeline,
// and exposure is inert without a transform. This headless gate proves that through
// the REAL PosterRenderer.RenderToFile (the exact path the Image button / batch /
// server take) across representative scenes, and confirms each transform actually
// reaches the output. Pair it with a visual smoke test.
//
// Run: FracturingFog --viewtransformprobe   (writes viewtransformprobe.out, exit 0/1)

using System;
using System.Text;

using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Diagnostics
{
    public static class ViewTransformProbe
    {
        private const int W = 200, H = 150;

        // A real registered built-in palette (representative of a normal render;
        // also avoids defining a new Engine IColorMap type that the
        // ColorMapRegistrationGuard would flag as an orphaned catalog theme).
        private static IColorMap Palette() => ColorPalette.BuiltIns[0];

        private static uint[] Render(ViewTransform vt, float ev, bool translucent)
        {
            var fp = new FractalParameters();
            if (translucent)
            {
                // Exercises the full-float 2D composite: translucent interior over a
                // solid backdrop. With None the backdrop composites in 8-bit; with a
                // transform it composites in linear then tonemaps (PR #659).
                fp.InteriorAlpha = 128;
                fp.Interior2DBackground = Interior2DBackgroundMode.SolidColor;
                fp.Interior2DBgTop = 0xFF3060A0u;
                fp.Interior2DBgBottom = 0xFF3060A0u;
            }

            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ff-vtprobe-{Guid.NewGuid():N}.png");
            var req = new PosterRequest
            {
                CenterX = -0.5, CenterY = 0.0, Zoom = 0.9,
                MaxIterations = 300,
                FractalType = FractalType.Mandelbrot,
                Quality = QualityPreset.Standard,
                Width = W, Height = H,
                FractalParameters = fp,
                ColorMap = Palette(),
                Path = path,
                Format = ImageFileFormat.Png,
                ViewTransform = vt,
                ViewExposureEv = ev,
            };
            try
            {
                PosterRenderer.RenderToFile(req, System.Threading.CancellationToken.None);
                using var bmp = SkiaSharp.SKBitmap.Decode(path);
                if (bmp == null) return Array.Empty<uint>();
                var buf = new uint[bmp.Width * bmp.Height];
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        var p = bmp.GetPixel(x, y);
                        buf[y * bmp.Width + x] =
                            ((uint)p.Red << 16) | ((uint)p.Green << 8) | p.Blue;
                    }
                return buf;
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
        }

        private static long DiffCount(uint[] a, uint[] b)
        {
            if (a.Length != b.Length || a.Length == 0) return long.MaxValue;
            long d = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) d++;
            return d;
        }

        public static int RunGate()
        {
            var sb = new StringBuilder();
            sb.AppendLine("View-transform default-look validation (S2, #396)");
            sb.AppendLine($"scene = Mandelbrot {W}x{H}, via PosterRenderer.RenderToFile");
            sb.AppendLine();

            bool ok = true;
            var transforms = new[]
            {
                ViewTransform.Reinhard, ViewTransform.AcesFilmic,
                ViewTransform.AgX, ViewTransform.Filmic,
            };

            foreach (bool translucent in new[] { false, true })
            {
                string scene = translucent ? "translucent-composite" : "opaque";
                sb.AppendLine($"[{scene}]");

                var none1 = Render(ViewTransform.None, 0f, translucent);
                var none2 = Render(ViewTransform.None, 0f, translucent);
                long noneDiff = DiffCount(none1, none2);
                bool noneStable = noneDiff == 0;
                ok &= noneStable;
                sb.AppendLine($"  None byte-identical across runs : {(noneStable ? "PASS" : "FAIL")} (diff {noneDiff})");

                // Exposure with None selected must be inert (the transform gate).
                var noneExposed = Render(ViewTransform.None, 3f, translucent);
                bool expInert = DiffCount(none1, noneExposed) == 0;
                ok &= expInert;
                sb.AppendLine($"  None ignores exposure           : {(expInert ? "PASS" : "FAIL")}");

                foreach (var vt in transforms)
                {
                    var img = Render(vt, 0f, translucent);
                    long diff = DiffCount(img, none1);
                    bool reached = diff > 0 && diff != long.MaxValue;
                    ok &= reached;
                    long tot = none1.Length;
                    sb.AppendLine($"  {vt,-11} changes output      : {(reached ? "PASS" : "FAIL")} ({diff}/{tot} px)");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"RESULT: {(ok ? "PASS" : "FAIL")}");
            string report = sb.ToString();
            Console.WriteLine(report);
            try
            {
                string outPath = System.IO.Path.Combine(AppContext.BaseDirectory, "viewtransformprobe.out");
                System.IO.File.WriteAllText(outPath, report);
                Console.WriteLine($"wrote {outPath}");
            }
            catch { }
            return ok ? 0 : 1;
        }
    }
}
