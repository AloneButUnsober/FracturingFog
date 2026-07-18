// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Diagnostics/ColorProbe.cs
//
// --colorprobe: headless golden gate for the colour pipeline (Phase A/B/C
// options F1-F9,F12). Mirrors the --kifsprobe / --inputprobe / --rebaseprobe
// pattern in Program.cs, but unlike those *diagnostic* probes this one is a
// regression GATE: it returns a non-zero exit code when the sampled output
// drifts from the embedded golden digest, so CI can block a PR that silently
// changes colour output.
//
// Why it exists: Phase D (F11 deband, F10 alpha) will edit the exact float->byte
// quantise in GradientColorMap.MapNormalized plus the LUT build. Both change
// pixel values in ways only a golden diff catches. This gate pins the CURRENT
// output of the whole shipped option matrix so a Phase-D change that regresses
// an unrelated option (e.g. an OkLab blend, a transfer curve, palette gamma)
// trips immediately instead of at review time.
//
// Scope: Gradient + Cycling kinds. Those two exercise the shared quantise point
// (MapNormalized) and every LUT-baked option (interp space/curve, transfer +
// strength, per-stop midpoint, palette gamma) and every cycling knob (offset /
// density / wrap). The 3D kinds (Phong/Pbr) add lighting on top of the SAME LUT
// but need surface normals to evaluate, so they are out of scope for this gate
// and covered separately. ColorGen (compiled DSL themes) is a distinct codegen
// path with its own build-time check and is likewise out of scope here.
//
// Usage:
//   --colorprobe            gate: recompute + compare to GoldenDigest; exit 1 on drift.
//   --colorprobe regen      print the freshly computed digest to paste into GoldenDigest.
//   --colorprobe verbose    gate, but also dump the per-config table to stdout.
//
// A full per-config table is always written to colorprobe.out next to the exe
// so a drift can be localised to the offending option without a rebuild.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Diagnostics
{
    public static class ColorProbe
    {
        // Golden digest of the whole matrix. Regenerate deliberately with
        // `--colorprobe regen` ONLY when a colour-output change is intended and
        // reviewed; paste the printed value here. An empty string means "not
        // yet pinned" — the gate then fails and tells you to regen.
        private const string GoldenDigest =
            "b68af584c34804f02db6e07b4fdec31748ea254211efeb3e85274218ff3bfbdb";

        private const int MaxIter = 1000;
        private const int Samples = 64;   // smooth sweep points across [0, MaxIter]

        public static int Run(string[] args)
        {
            bool regen = args.Length > 1 && string.Equals(args[1], "regen", StringComparison.OrdinalIgnoreCase);
            bool verbose = args.Length > 1 && string.Equals(args[1], "verbose", StringComparison.OrdinalIgnoreCase);
            if (args.Length > 1 && string.Equals(args[1], "dither", StringComparison.OrdinalIgnoreCase))
                return RunDitherDemo();
            if (args.Length > 1 && string.Equals(args[1], "alpha", StringComparison.OrdinalIgnoreCase))
                return RunAlphaDemo();
            if (args.Length > 1 && string.Equals(args[1], "alphapng", StringComparison.OrdinalIgnoreCase))
                return RunAlphaPngGate();
            if (args.Length > 1 && string.Equals(args[1], "pngseq", StringComparison.OrdinalIgnoreCase))
                return RunPngSeqGate();
            if (args.Length > 1 && string.Equals(args[1], "alphaimage", StringComparison.OrdinalIgnoreCase))
                return RunAlphaImage(args);
            if (args.Length > 1 && string.Equals(args[1], "alphawm", StringComparison.OrdinalIgnoreCase))
                return RunAlphaWatermarkGate();
            if (args.Length > 1 && string.Equals(args[1], "alphaposter", StringComparison.OrdinalIgnoreCase))
                return RunAlphaPosterGate(args);
            if (args.Length > 1 && string.Equals(args[1], "alphascan", StringComparison.OrdinalIgnoreCase))
                return RunAlphaScan(args);
            if (args.Length > 1 && string.Equals(args[1], "alphalit", StringComparison.OrdinalIgnoreCase))
                return RunAlphaLitGate();
            if (args.Length > 1 && string.Equals(args[1], "gpualpha", StringComparison.OrdinalIgnoreCase))
                return RunGpuAlphaGate();

            var report = new StringBuilder();
            report.AppendLine("colour pipeline golden probe — Phase A/B/C option matrix (F1-F9,F12)");
            report.AppendLine($"  kinds=Gradient+Cycling  maxIter={MaxIter}  samples={Samples}  channels=ARGB");
            report.AppendLine("  per-config: label, sample RGB at t=0 / mid / end, 8-hex config digest");
            report.AppendLine();

            using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> b = stackalloc byte[4];

            foreach (var (label, data) in BuildMatrix())
            {
                IColorMap? map = DataDrivenColorThemes.Create(data);
                if (map == null)
                {
                    report.AppendLine($"  {label,-38} <Create returned null — bad config>");
                    // Fold the failure into the digest so it can't silently pass.
                    whole.AppendData(Encoding.UTF8.GetBytes(label + "##null-config##"));
                    continue;
                }

                using var cfg = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                whole.AppendData(Encoding.UTF8.GetBytes(label));

                int firstArgb = 0, midArgb = 0, lastArgb = 0;
                for (int i = 0; i < Samples; i++)
                {
                    float smooth = i * (MaxIter / (float)(Samples - 1));
                    int argb = map.Map(smooth, 0f, MaxIter);

                    b[0] = (byte)((argb >> 24) & 0xFF);
                    b[1] = (byte)((argb >> 16) & 0xFF);
                    b[2] = (byte)((argb >> 8) & 0xFF);
                    b[3] = (byte)(argb & 0xFF);
                    whole.AppendData(b);
                    cfg.AppendData(b);

                    if (i == 0) firstArgb = argb;
                    if (i == Samples / 2) midArgb = argb;
                    if (i == Samples - 1) lastArgb = argb;
                }

                string cfgHex = Convert.ToHexString(cfg.GetHashAndReset()).Substring(0, 8).ToLowerInvariant();
                report.AppendLine(
                    $"  {label,-38} {Rgb(firstArgb)} {Rgb(midArgb)} {Rgb(lastArgb)}  {cfgHex}");
            }

            string digest = Convert.ToHexString(whole.GetHashAndReset()).ToLowerInvariant();
            report.AppendLine();
            report.AppendLine($"  matrix digest = {digest}");

            string outPath = System.IO.Path.Combine(AppContext.BaseDirectory, "colorprobe.out");
            System.IO.File.WriteAllText(outPath, report.ToString());

            if (verbose)
                Console.Write(report.ToString());
            else
                Console.WriteLine(report.ToString().TrimEnd());

            if (regen)
            {
                Console.WriteLine();
                Console.WriteLine("REGEN — paste this into ColorProbe.GoldenDigest:");
                Console.WriteLine($"    \"{digest}\"");
                return 0;
            }

            Console.WriteLine();
            if (string.IsNullOrEmpty(GoldenDigest))
            {
                Console.WriteLine("RESULT: FAIL (GoldenDigest not pinned — run `--colorprobe regen` and paste the digest)");
                return 1;
            }
            if (string.Equals(GoldenDigest, digest, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("RESULT: PASS (colour output matches golden)");
                return 0;
            }
            Console.WriteLine("RESULT: FAIL (colour output drifted from golden)");
            Console.WriteLine($"  expected {GoldenDigest}");
            Console.WriteLine($"  actual   {digest}");
            Console.WriteLine("  see colorprobe.out for the per-config table to localise the drift");
            return 1;
        }

        // --colorprobe dither: diagnostic (NOT a gate) proving the F11a ordered
        // dither is mean-preserving and actually spreads a sub-LSB gradient step
        // across the 8×8 Bayer tile. Enables dither, walks one Bayer row for a
        // shallow-gradient sample, and shows the per-pixel bytes vs the plain
        // truncate. Restores DitherEnabled=false before returning.
        private static int RunDitherDemo()
        {
            var map = DataDrivenColorThemes.Create(Grad(d => { }));
            if (map is not GradientColorMap)
            {
                Console.WriteLine("RESULT: FAIL (baseline gradient did not create a GradientColorMap)");
                return 1;
            }

            // Pick a smooth value whose LUT lerp lands on a fractional byte, so
            // truncation alone bands but dither can split neighbours.
            const float smooth = 123.4f;
            int plain = map.Map(smooth, 0f, MaxIter);
            int plainR = (plain >> 16) & 0xFF, plainG = (plain >> 8) & 0xFF, plainB = plain & 0xFF;

            Console.WriteLine("F11a ordered-dither demo — baseline gradient, one Bayer row (y=0):");
            Console.WriteLine($"  plain truncate           = {Rgb(plain)}");

            GradientColorMap.DitherEnabled = true;
            GradientColorMap.DitherStrength = 1f;
            long sumR = 0, sumG = 0, sumB = 0;
            var seen = new HashSet<int>();
            var row = new StringBuilder("  dithered across x=0..7   = ");
            for (int x = 0; x < 8; x++)
            {
                GradientColorMap.SetDitherForPixel(x, 0);
                int d = map.Map(smooth, 0f, MaxIter);
                seen.Add(d);
                sumR += (d >> 16) & 0xFF; sumG += (d >> 8) & 0xFF; sumB += d & 0xFF;
                row.Append(Rgb(d)).Append(' ');
            }
            GradientColorMap.DitherEnabled = false;

            Console.WriteLine(row.ToString().TrimEnd());
            Console.WriteLine($"  row mean                 = ({sumR / 8f:0.0},{sumG / 8f:0.0},{sumB / 8f:0.0})  plain=({plainR},{plainG},{plainB})");

            // Sanity: dither must produce >1 distinct value (spreads the step)…
            bool spread = seen.Count > 1;
            // …and stay mean-preserving to within a byte of the plain truncate.
            bool meanOk = Math.Abs(sumR / 8f - plainR) <= 1.0f
                       && Math.Abs(sumG / 8f - plainG) <= 1.0f
                       && Math.Abs(sumB / 8f - plainB) <= 1.0f;

            Console.WriteLine();
            if (spread && meanOk)
            {
                Console.WriteLine("RESULT: PASS (dither spreads the step and is mean-preserving)");
                return 0;
            }
            Console.WriteLine($"RESULT: FAIL (spread={spread}, meanOk={meanOk})");
            return 1;
        }

        // --colorprobe alpha: diagnostic (NOT a gate) proving F10's per-stop
        // alpha rides the LUT's 4th lane end-to-end — a stop with A=0 through a
        // stop with A=255 must produce an interpolated alpha byte in the packed
        // ARGB, not the historical forced 0xFF.
        private static int RunAlphaDemo()
        {
            var data = new ColorThemeData
            {
                Name = "probe-alpha",
                Kind = ColorThemeKind.Gradient,
                Stops = new List<ColorStopData>
                {
                    new ColorStopData { Position = 0.00f, R = 10,  G = 20,  B = 40,  A = 0   },
                    new ColorStopData { Position = 1.00f, R = 240, G = 230, B = 120, A = 255 },
                },
            };
            var map = DataDrivenColorThemes.Create(data);
            if (map == null)
            {
                Console.WriteLine("RESULT: FAIL (alpha gradient did not create a map)");
                return 1;
            }

            Console.WriteLine("F10 per-stop alpha demo — gradient A: 0 → 255 across t=0..1:");
            int a0 = 0, a1 = 0;
            bool monotone = true;
            int prevA = -1;
            for (int i = 0; i < Samples; i++)
            {
                float t = i / (float)(Samples - 1);
                int argb = map.Map(t * MaxIter, 0f, MaxIter);
                int aByte = (argb >> 24) & 0xFF;
                if (i == 0) a0 = aByte;
                if (i == Samples - 1) a1 = aByte;
                if (aByte < prevA) monotone = false;
                prevA = aByte;
            }
            Console.WriteLine($"  alpha at t=0   = {a0}");
            Console.WriteLine($"  alpha at t=1   = {a1}");
            Console.WriteLine($"  monotone rise  = {monotone}");

            // Plumbing works iff alpha starts near 0, ends at 255, and never
            // falls — i.e. the forced-0xFF is gone and the lane interpolates.
            bool ok = a0 <= 8 && a1 == 255 && monotone;
            Console.WriteLine();
            Console.WriteLine(ok
                ? "RESULT: PASS (per-stop alpha carried through the LUT)"
                : $"RESULT: FAIL (a0={a0}, a1={a1}, monotone={monotone})");
            return ok ? 0 : 1;
        }

        // --colorprobe alphaimage [outdir]: diagnostic (NOT a gate). Produces
        // two viewable PNGs so a human can eyeball F10 per-stop alpha end to end:
        //   alpha_strip.png          — a translucent gradient saved through the
        //                              real ImageExport straight-alpha path.
        //   alpha_over_checker.png   — the same strip composited over a grey
        //                              checkerboard (straight-alpha SrcOver) so
        //                              the coverage ramp is visible to the eye.
        // If alpha were being dropped or premultiplied wrongly, the checker
        // composite would show a hard opaque band instead of a smooth fade.
        private static int RunAlphaImage(string[] args)
        {
            string outDir = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
                ? args[2]
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff-alphaimage");
            System.IO.Directory.CreateDirectory(outDir);

            // Translucent gradient: alpha ramps 0 → 255 left→right while the hue
            // sweeps, so both the colour and the coverage vary across the strip.
            var data = new ColorThemeData
            {
                Name = "probe-alpha-image",
                Kind = ColorThemeKind.Gradient,
                Stops = new List<ColorStopData>
                {
                    new ColorStopData { Position = 0.00f, R = 250, G = 40,  B = 70,  A = 0   },
                    new ColorStopData { Position = 0.50f, R = 60,  G = 200, B = 240, A = 128 },
                    new ColorStopData { Position = 1.00f, R = 250, G = 230, B = 90,  A = 255 },
                },
            };
            var map = DataDrivenColorThemes.Create(data);
            if (map == null)
            {
                Console.WriteLine("RESULT: FAIL (alpha gradient did not create a map)");
                return 1;
            }

            const int w = 512, h = 120;
            var strip = new uint[w * h];
            int minA = 255, maxA = 0;
            for (int x = 0; x < w; x++)
            {
                float t = x / (float)(w - 1);
                uint argb = unchecked((uint)map.Map(t * MaxIter, 0f, MaxIter)); // 0xAARRGGBB == BGRA in memory
                int a = (int)((argb >> 24) & 0xFF);
                if (a < minA) minA = a;
                if (a > maxA) maxA = a;
                for (int y = 0; y < h; y++) strip[y * w + x] = argb;
            }

            // Composite the strip over a grey checkerboard so translucency reads.
            var checker = new uint[w * h];
            const int cell = 12;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    uint p = strip[y * w + x];
                    float a = ((p >> 24) & 0xFF) / 255f;
                    int fr = (int)((p >> 16) & 0xFF), fg = (int)((p >> 8) & 0xFF), fb = (int)(p & 0xFF);
                    bool light = (((x / cell) + (y / cell)) & 1) == 0;
                    int bg = light ? 200 : 120;
                    int rr = (int)(fr * a + bg * (1 - a));
                    int gg = (int)(fg * a + bg * (1 - a));
                    int bb = (int)(fb * a + bg * (1 - a));
                    checker[y * w + x] = 0xFF000000u | ((uint)rr << 16) | ((uint)gg << 8) | (uint)bb;
                }

            string stripPath = System.IO.Path.Combine(outDir, "alpha_strip.png");
            string checkPath = System.IO.Path.Combine(outDir, "alpha_over_checker.png");
            FracturingFog.Imaging.ImageExport.SavePixelsToFile(
                strip, w, h, stripPath, FracturingFog.Imaging.ImageFileFormat.Png,
                (FracturingFog.Imaging.WatermarkRender?)null);
            FracturingFog.Imaging.ImageExport.SavePixelsToFile(
                checker, w, h, checkPath, FracturingFog.Imaging.ImageFileFormat.Png,
                (FracturingFog.Imaging.WatermarkRender?)null);

            Console.WriteLine("F10 per-stop alpha visual proof:");
            Console.WriteLine($"  alpha range across strip = {minA}..{maxA}");
            Console.WriteLine($"  strip  (straight alpha)  = {stripPath}");
            Console.WriteLine($"  over checkerboard        = {checkPath}");
            return 0;
        }

        // --colorprobe alphascan <file.png>: inspect an EXISTING exported PNG for
        // per-stop alpha. Straight-alpha PNGs keep RGB byte-identical to an opaque
        // theme (only the A byte differs), so an alpha-unaware viewer shows a
        // translucent export and an opaque one identically — the transparency is
        // in the file but invisible without compositing. This reports the alpha
        // coverage of the real file and writes <name>_checker.png so it can be
        // seen. Point it at an actual "Save Image" / poster output.
        private static int RunAlphaScan(string[] args)
        {
            if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2]))
            {
                Console.WriteLine("usage: --colorprobe alphascan <file.png>");
                return 2;
            }
            string path = args[2];
            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine($"alphascan: file not found — {path}");
                return 2;
            }

            using var bmp = SkiaSharp.SKBitmap.Decode(path);
            if (bmp == null)
            {
                Console.WriteLine($"alphascan: could not decode — {path}");
                return 2;
            }

            int cw = bmp.Width, ch = bmp.Height;
            int minA = 255, maxA = 0; long trans = 0, total = 0;
            var comp = new uint[cw * ch];
            const int cell = 12;
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    var px = bmp.GetPixel(x, y);
                    byte a = px.Alpha;
                    if (a < minA) minA = a;
                    if (a > maxA) maxA = a;
                    if (a < 255) trans++;
                    total++;
                    float af = a / 255f;
                    int bg = ((((x / cell) + (y / cell)) & 1) == 0) ? 200 : 120;
                    int R = (int)(px.Red * af + bg * (1 - af));
                    int G = (int)(px.Green * af + bg * (1 - af));
                    int B = (int)(px.Blue * af + bg * (1 - af));
                    comp[y * cw + x] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | (uint)B;
                }

            string checkerPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".",
                System.IO.Path.GetFileNameWithoutExtension(path) + "_checker.png");
            FracturingFog.Imaging.ImageExport.SavePixelsToFile(
                comp, cw, ch, checkerPath, FracturingFog.Imaging.ImageFileFormat.Png,
                (FracturingFog.Imaging.WatermarkRender?)null);

            Console.WriteLine($"alphascan: {path}");
            Console.WriteLine($"  size          = {cw}x{ch}");
            Console.WriteLine($"  alpha min..max = {minA}..{maxA}");
            Console.WriteLine($"  translucent px = {trans}/{total}");
            Console.WriteLine($"  checker view   = {checkerPath}");
            Console.WriteLine();
            Console.WriteLine(minA < 255
                ? "This file HAS per-stop alpha (open the _checker.png to see it)."
                : "This file is fully opaque (no alpha < 255 anywhere).");
            return 0;
        }

        // --colorprobe alphaposter [outpng]: end-to-end test of the REAL still
        // export. Renders a Mandelbrot through PosterRenderer.RenderToFile with a
        // translucent theme (no watermark) and scans the decoded PNG's alpha. This
        // is the exact path the interactive "Image" button / batch / server use;
        // the synthetic alphapng/alphawm gates only prove ImageExport in isolation.
        // If PosterRenderer or the calculator colourise flattens alpha, minA==255.
        private static int RunAlphaPosterGate(string[] args)
        {
            var data = new ColorThemeData
            {
                Name = "probe-alpha-poster",
                Kind = ColorThemeKind.Gradient,
                Stops = new List<ColorStopData>
                {
                    new ColorStopData { Position = 0.00f, R = 250, G = 40,  B = 70,  A = 0   },
                    new ColorStopData { Position = 0.50f, R = 60,  G = 200, B = 240, A = 128 },
                    new ColorStopData { Position = 1.00f, R = 250, G = 230, B = 90,  A = 255 },
                },
            };
            var map = DataDrivenColorThemes.Create(data);
            if (map == null) { Console.WriteLine("RESULT: FAIL (map null)"); return 1; }

            bool keep = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]);
            string path = keep
                ? args[2]
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ff-alphaposter-{Guid.NewGuid():N}.png");

            // Render twice: once WITHOUT a watermark (isolates PosterRenderer +
            // calculator), once WITH the default watermark (the exact path the
            // interactive "Image" button takes — SaveBgraSkia then reload +
            // re-encode). Report alpha coverage for both.
            (int min, int max, long trans, long total) ScanPoster(string p, bool withWm)
            {
                var req = new FracturingFog.Imaging.PosterRequest
                {
                    CenterX = -0.5, CenterY = 0.0, Zoom = 0.9,
                    MaxIterations = 300,
                    FractalType = FractalType.Mandelbrot,
                    ColorMap = map,
                    Quality = FracturingFog.Models.QualityPreset.Standard,
                    Width = 240, Height = 180,
                    Path = p,
                    Format = FracturingFog.Imaging.ImageFileFormat.Png,
                    Watermark = withWm ? "ALPHA TEST" : "",
                    SubText = withWm ? "probe" : "",
                };
                FracturingFog.Imaging.PosterRenderer.RenderToFile(req, System.Threading.CancellationToken.None);
                using var bmp = SkiaSharp.SKBitmap.Decode(p);
                if (bmp == null) return (255, 0, 0, 0);
                int mn = 255, mx = 0; long tr = 0, tot = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        byte a = bmp.GetPixel(x, y).Alpha;
                        if (a < mn) mn = a;
                        if (a > mx) mx = a;
                        if (a < 255) tr++;
                        tot++;
                    }
                return (mn, mx, tr, tot);
            }

            string wmPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(path) ?? System.IO.Path.GetTempPath(),
                System.IO.Path.GetFileNameWithoutExtension(path) + "_wm.png");

            (int min, int max, long trans, long total) noWm, wm;
            try
            {
                noWm = ScanPoster(path, withWm: false);
                wm = ScanPoster(wmPath, withWm: true);
            }
            finally
            {
                if (!keep)
                {
                    try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
                    try { if (System.IO.File.Exists(wmPath)) System.IO.File.Delete(wmPath); } catch { }
                }
            }

            // When keeping output, also emit a checkerboard-composited preview of
            // the real render so the alpha is *visible* — a normal alpha-unaware
            // viewer shows the straight-alpha PNG identically to an opaque theme
            // (RGB is unchanged; only the A byte differs), which is exactly why
            // the transparency looks "missing" until composited over a background.
            if (keep)
            {
                try
                {
                    using var src = SkiaSharp.SKBitmap.Decode(path);
                    if (src != null)
                    {
                        int cw = src.Width, ch = src.Height;
                        var comp = new uint[cw * ch];
                        const int cell = 12;
                        for (int y = 0; y < ch; y++)
                            for (int x = 0; x < cw; x++)
                            {
                                var px = src.GetPixel(x, y);
                                float a = px.Alpha / 255f;
                                int bg = ((((x / cell) + (y / cell)) & 1) == 0) ? 200 : 120;
                                int R = (int)(px.Red * a + bg * (1 - a));
                                int G = (int)(px.Green * a + bg * (1 - a));
                                int B = (int)(px.Blue * a + bg * (1 - a));
                                comp[y * cw + x] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | (uint)B;
                            }
                        string checkerPath = System.IO.Path.Combine(
                            System.IO.Path.GetDirectoryName(path) ?? System.IO.Path.GetTempPath(),
                            System.IO.Path.GetFileNameWithoutExtension(path) + "_checker.png");
                        FracturingFog.Imaging.ImageExport.SavePixelsToFile(
                            comp, cw, ch, checkerPath, FracturingFog.Imaging.ImageFileFormat.Png,
                            (FracturingFog.Imaging.WatermarkRender?)null);
                        Console.WriteLine($"  checker view = {checkerPath}");
                    }
                }
                catch { }
            }

            Console.WriteLine("F10 real-export (PosterRenderer) alpha scan:");
            Console.WriteLine($"  no watermark : alpha {noWm.min}..{noWm.max}  translucent {noWm.trans}/{noWm.total}");
            Console.WriteLine($"  + watermark  : alpha {wm.min}..{wm.max}  translucent {wm.trans}/{wm.total}");
            if (keep) Console.WriteLine($"  saved        = {path} , {wmPath}");

            bool okNoWm = noWm.min < 255 && noWm.trans > 0;
            bool okWm = wm.min < 255 && wm.trans > 0;
            Console.WriteLine();
            if (okNoWm && okWm)
                Console.WriteLine("RESULT: PASS (real still export carries per-stop alpha, with and without watermark)");
            else if (okNoWm && !okWm)
                Console.WriteLine("RESULT: FAIL (watermark path flattens alpha — PosterRenderer buffer had it)");
            else
                Console.WriteLine("RESULT: FAIL (export is fully opaque — alpha lost before/at save)");
            return (okNoWm && okWm) ? 0 : 1;
        }

        // --colorprobe alphawm: TRUE gate for the WATERMARKED export path.
        // The interactive "Image" button ALWAYS passes a watermark, so the save
        // goes SaveBgraSkia (writes straight-alpha PNG) -> CompositeWatermarkRenderSkia
        // (reloads that PNG, redraws, re-encodes). If the reload/re-encode flattens
        // alpha (e.g. SKBitmap.Decode returns an Opaque-typed bitmap, or the surface
        // has no alpha channel), the exported image comes back opaque even though
        // the buffer and the no-watermark path carry alpha. Samples a corner pixel
        // well away from the bottom-right watermark text.
        private static int RunAlphaWatermarkGate()
        {
            const byte A = 128, R = 200, G = 100, B = 50;
            const int w = 96, h = 64;
            var pixels = new uint[w * h];
            uint packed = ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = packed;

            var wm = new FracturingFog.Imaging.WatermarkRender
            {
                TopText = "ALPHA TEST",
                SubText = "probe",
                TextColor = new RgbDef(255, 255, 255),
                Placement = WatermarkPlacement.Bottom,
                Justify = WatermarkJustify.Right,
                IsCustom = false,
            };

            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ff-alphawm-{Guid.NewGuid():N}.png");

            byte da = 0, dr = 0, dg = 0, db = 0;
            bool decoded = false;
            try
            {
                FracturingFog.Imaging.ImageExport.SavePixelsToFile(
                    pixels, w, h, path, FracturingFog.Imaging.ImageFileFormat.Png, wm);

                using var bmp = SkiaSharp.SKBitmap.Decode(path);
                if (bmp != null)
                {
                    var c = bmp.GetPixel(2, 2); // top-left corner, clear of watermark
                    da = c.Alpha; dr = c.Red; dg = c.Green; db = c.Blue;
                    decoded = true;
                }
            }
            finally
            {
                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
            }

            Console.WriteLine("F10.3b watermarked-export straight-alpha gate:");
            Console.WriteLine($"  wrote  ARGB = ({A},{R},{G},{B})");
            Console.WriteLine($"  read   ARGB = ({da},{dr},{dg},{db})  (corner, no watermark)");

            bool ok = decoded
                      && Math.Abs(da - A) <= 2
                      && Math.Abs(dr - R) <= 3
                      && Math.Abs(dg - G) <= 3
                      && Math.Abs(db - B) <= 3;
            Console.WriteLine();
            Console.WriteLine(ok
                ? "RESULT: PASS (watermark reload kept straight alpha)"
                : "RESULT: FAIL (watermark reload/re-encode flattened alpha)");
            return ok ? 0 : 1;
        }

        // --colorprobe alphapng: TRUE gate for F10.3 straight-alpha PNG export.
        // Round-trips a hand-built translucent BGRA buffer through
        // ImageExport.SavePixelsToFile → PNG on disk → SkiaSharp decode, and
        // asserts the coverage byte survives AND the RGB is unmangled. If
        // SaveBgraSkia still declared the (wrong) Premul alpha type, the encoder
        // would divide RGB by alpha at save time — so a translucent pixel's RGB
        // would come back roughly doubled. This gate catches that regression.
        private static int RunAlphaPngGate()
        {
            const byte A = 128, R = 200, G = 100, B = 50;
            const int w = 4, h = 4;
            var pixels = new uint[w * h];
            uint packed = ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B; // BGRA in memory
            for (int i = 0; i < pixels.Length; i++) pixels[i] = packed;

            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ff-alphapng-{Guid.NewGuid():N}.png");

            byte da = 0, dr = 0, dg = 0, db = 0;
            bool decoded = false;
            try
            {
                FracturingFog.Imaging.ImageExport.SavePixelsToFile(
                    pixels, w, h, path, FracturingFog.Imaging.ImageFileFormat.Png,
                    (FracturingFog.Imaging.WatermarkRender?)null);

                using var bmp = SkiaSharp.SKBitmap.Decode(path);
                if (bmp != null)
                {
                    var c = bmp.GetPixel(1, 1);
                    da = c.Alpha; dr = c.Red; dg = c.Green; db = c.Blue;
                    decoded = true;
                }
            }
            finally
            {
                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
            }

            Console.WriteLine("F10.3 straight-alpha PNG export gate:");
            Console.WriteLine($"  wrote  ARGB = ({A},{R},{G},{B})");
            Console.WriteLine($"  read   ARGB = ({da},{dr},{dg},{db})");

            // Alpha must survive; RGB must be unmangled (±3 for PNG round-trip).
            // A Premul mislabel would roughly double RGB (÷0.5) → far outside ±3.
            bool ok = decoded
                      && Math.Abs(da - A) <= 2
                      && Math.Abs(dr - R) <= 3
                      && Math.Abs(dg - G) <= 3
                      && Math.Abs(db - B) <= 3;
            Console.WriteLine();
            Console.WriteLine(ok
                ? "RESULT: PASS (PNG kept straight alpha; RGB intact)"
                : "RESULT: FAIL (alpha dropped or RGB mangled — premultiply regression?)");
            return ok ? 0 : 1;
        }

        // --colorprobe pngseq: TRUE gate for the F10.3b video/PNG-sequence path.
        // PngSequenceWriter has its OWN SavePng (not ImageExport), so it needs its
        // own round-trip check. Writes one translucent frame through the writer,
        // decodes frame_000001.png, asserts the coverage byte survives and RGB is
        // unmangled. A Premul mislabel here would blow out translucent-theme video
        // frames while leaving opaque output byte-identical.
        private static int RunPngSeqGate()
        {
            const byte A = 128, R = 200, G = 100, B = 50;
            const int w = 4, h = 4;
            var frame = new uint[w * h];
            uint packed = ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B; // BGRA
            for (int i = 0; i < frame.Length; i++) frame[i] = packed;

            string dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ff-pngseq-{Guid.NewGuid():N}");

            byte da = 0, dr = 0, dg = 0, db = 0;
            bool decoded = false;
            try
            {
                using (var writer = new FracturingFog.PngSequenceWriter(dir, w, h))
                {
                    writer.WriteFrame(frame);
                } // Dispose drains the write queue

                string path = System.IO.Path.Combine(dir, "frame_000001.png");
                using var bmp = SkiaSharp.SKBitmap.Decode(path);
                if (bmp != null)
                {
                    var c = bmp.GetPixel(1, 1);
                    da = c.Alpha; dr = c.Red; dg = c.Green; db = c.Blue;
                    decoded = true;
                }
            }
            finally
            {
                try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
            }

            Console.WriteLine("F10.3b PNG-sequence (video-frame) straight-alpha gate:");
            Console.WriteLine($"  wrote  ARGB = ({A},{R},{G},{B})");
            Console.WriteLine($"  read   ARGB = ({da},{dr},{dg},{db})");

            bool ok = decoded
                      && Math.Abs(da - A) <= 2
                      && Math.Abs(dr - R) <= 3
                      && Math.Abs(dg - G) <= 3
                      && Math.Abs(db - B) <= 3;
            Console.WriteLine();
            Console.WriteLine(ok
                ? "RESULT: PASS (PNG-sequence frame kept straight alpha; RGB intact)"
                : "RESULT: FAIL (alpha dropped or RGB mangled — premultiply regression?)");
            return ok ? 0 : 1;
        }

        // --colorprobe alphalit: TRUE gate for F10.4 — the 3D lit bases
        // (GradientPhong3DBase / PbrGradient3DBase) sample the gradient LUT for
        // albedo, then light the RGB. Before F10.4 they packed a forced 0xFF top
        // byte, dropping the stop's authored alpha. This gate builds a Phong3D
        // AND a Pbr3D theme whose stops ramp A: 0 → 255 and asserts the lit
        // output carries an interpolated coverage byte (not forced opaque), while
        // an all-opaque control theme still packs 0xFF everywhere (byte-exact).
        private static int RunAlphaLitGate()
        {
            // Two stops: colour constant-ish, alpha ramps 0 → 255. Kind chosen per
            // case below. No lights supplied → the DataDriven bases apply their
            // defaults, so the lit path evaluates normally.
            static ColorThemeData LitData(ColorThemeKind kind, bool opaque) => new()
            {
                Name = "probe-alphalit",
                Kind = kind,
                Stops = new List<ColorStopData>
                {
                    new ColorStopData { Position = 0.00f, R = 60, G = 200, B = 240, A = (byte)(opaque ? 255 : 0)   },
                    new ColorStopData { Position = 1.00f, R = 250, G = 230, B = 90, A = 255 },
                },
            };

            // Sweep alpha through the lit path. The lit bases use CyclicT(smooth,
            // CycleSpeed=0.02), so t = smooth*0.02; keep smooth in [0,50) so t
            // sweeps [0,1) monotonically without wrapping. maxIter is large so we
            // never hit the in-set early-out.
            static (int a0, int a1, bool monotone, bool nonBlack) SweepLit(IColorMap map)
            {
                int a0 = 0, a1 = 0, prevA = -1; bool mono = true, nonBlack = false;
                const int n = 50;
                for (int i = 0; i < n; i++)
                {
                    float smooth = i * (49.5f / (n - 1)); // 0 .. 49.5 → t 0 .. 0.99
                    int argb = map.Map(smooth, 0f, MaxIter);
                    int a = (argb >> 24) & 0xFF;
                    int rgb = argb & 0xFFFFFF;
                    if (rgb != 0) nonBlack = true;
                    if (i == 0) a0 = a;
                    if (i == n - 1) a1 = a;
                    if (a < prevA) mono = false;
                    prevA = a;
                }
                return (a0, a1, mono, nonBlack);
            }

            bool overallOk = true;
            Console.WriteLine("F10.4 3D-lit per-stop alpha gate:");
            foreach (var kind in new[] { ColorThemeKind.Phong3D, ColorThemeKind.Pbr3D })
            {
                var litMap = DataDrivenColorThemes.Create(LitData(kind, opaque: false));
                var opaqueMap = DataDrivenColorThemes.Create(LitData(kind, opaque: true));
                if (litMap == null || opaqueMap == null)
                {
                    Console.WriteLine($"  {kind,-8}: FAIL (Create returned null)");
                    overallOk = false;
                    continue;
                }

                var (a0, a1, mono, nonBlack) = SweepLit(litMap);
                var (oa0, oa1, _, _) = SweepLit(opaqueMap);

                // Ramp theme: alpha must start near 0, end near 255 (the final
                // sample sits at t≈0.99 because t=1.0 wraps to 0 under Repeat),
                // rise monotonically, and the lit RGB must be non-black (lighting
                // actually ran). Opaque control: every sample forced to 255
                // (byte-exact preserved).
                bool rampOk = a0 <= 8 && a1 >= 250 && mono && nonBlack;
                bool ctrlOk = oa0 == 255 && oa1 == 255;
                bool ok = rampOk && ctrlOk;
                overallOk &= ok;

                Console.WriteLine(
                    $"  {kind,-8}: ramp a0={a0} a1={a1} mono={mono} nonBlack={nonBlack} | opaque a0={oa0} a1={oa1} -> {(ok ? "PASS" : "FAIL")}");
            }

            Console.WriteLine();
            Console.WriteLine(overallOk
                ? "RESULT: PASS (3D lit bases carry per-stop alpha as coverage; opaque byte-exact)"
                : "RESULT: FAIL (lit path dropped alpha or lighting did not run)");
            return overallOk ? 0 : 1;
        }

        // --colorprobe gpualpha: TRUE gate for F10.4b — the GPU colour path.
        //
        // The GPU pack (MandelbrotGpuKernel.cg_pack_bgra) forces an opaque 0xFF
        // top byte. Audited (issue #46): that is CORRECT, not a gap — there is
        // no authored-alpha source on the GPU path to carry. A theme only reaches
        // the GPU pack when it implements IGpuHlslPalette (EscapeTimeCalculator
        // does `ColorMap as IGpuHlslPalette`; gradient themes return null there
        // and colourise on the alpha-aware CPU writeback instead). Every
        // IGpuHlslPalette theme is a procedural scheme whose colour model is
        // float3 / vec3 (rgb()/hsv()/palette() — the ColorGen DSL has NO alpha
        // primitive), so there is no per-stop alpha to lose. The ONLY themes that
        // carry authored alpha are GradientColorMap subclasses (the F10.1 LUT
        // 4th lane), and none of those implements IGpuHlslPalette.
        //
        // The forced-0xFF pack is therefore safe *as long as those two sets stay
        // disjoint*. This gate reflects over the engine assembly and fails if any
        // concrete IGpuHlslPalette type is also a GradientColorMap — i.e. someone
        // hand-wrote an HLSL body on an alpha-carrying gradient theme, at which
        // point the GPU pack really would need the float3→float4 codegen change
        // and this gate would flag it instead of shipping silent opaque output.
        private static int RunGpuAlphaGate()
        {
            var asm = typeof(IGpuHlslPalette).Assembly;
            Type gpuIface = typeof(IGpuHlslPalette);
            Type gradBase = typeof(GradientColorMap);

            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }

            var gpuThemes = new List<string>();
            var violators = new List<string>();
            foreach (var t in types)
            {
                if (t == null || t.IsAbstract || t.IsInterface) continue;
                if (!gpuIface.IsAssignableFrom(t)) continue;
                gpuThemes.Add(t.Name);
                if (gradBase.IsAssignableFrom(t)) violators.Add(t.Name);
            }
            gpuThemes.Sort(StringComparer.Ordinal);

            Console.WriteLine("F10.4b GPU-pack alpha-source invariant gate:");
            Console.WriteLine($"  IGpuHlslPalette themes scanned          = {gpuThemes.Count}");
            Console.WriteLine($"  authored-alpha (GradientColorMap) among them = {violators.Count}");
            if (violators.Count > 0)
                Console.WriteLine($"  VIOLATORS: {string.Join(", ", violators)}");

            // Must have found the procedural GPU themes (guards against an empty
            // reflection scan making the gate vacuously pass) AND none of them may
            // be an authored-alpha carrier.
            bool ok = gpuThemes.Count > 0 && violators.Count == 0;
            Console.WriteLine();
            Console.WriteLine(ok
                ? "RESULT: PASS (no authored-alpha theme reaches the GPU pack; forced-0xFF is correct)"
                : violators.Count > 0
                    ? "RESULT: FAIL (a GradientColorMap ships an HLSL body — GPU pack now drops its alpha; F10.4b float3→float4 needed)"
                    : "RESULT: FAIL (found no IGpuHlslPalette themes — reflection scan is wrong, gate is vacuous)");
            return ok ? 0 : 1;
        }

        private static string Rgb(int argb)
            => $"({(byte)((argb >> 16) & 0xFF),3},{(byte)((argb >> 8) & 0xFF),3},{(byte)(argb & 0xFF),3})";

        // Fixed, distinct base stops — span lightness + hue so blend-space and
        // curve differences show up. NEVER edit these without a deliberate regen.
        private static List<ColorStopData> BaseStops() => new()
        {
            new ColorStopData { Position = 0.00f, R = 10,  G = 20,  B = 40  },
            new ColorStopData { Position = 0.33f, R = 200, G = 60,  B = 30  },
            new ColorStopData { Position = 0.66f, R = 40,  G = 180, B = 90  },
            new ColorStopData { Position = 1.00f, R = 240, G = 230, B = 120 },
        };

        private static List<ColorStopData> BaseStops(float midpoint)
        {
            var s = BaseStops();
            foreach (var st in s) st.Midpoint = midpoint;
            return s;
        }

        // The option matrix. Each entry is one theme config that isolates (or
        // deliberately crosses) shipped options. Order is fixed — appending new
        // configs at the END keeps earlier digests stable; inserting or
        // reordering forces a regen.
        private static IEnumerable<(string label, ColorThemeData data)> BuildMatrix()
        {
            // ── Gradient kind ────────────────────────────────────────────────
            yield return ("grad/baseline",
                Grad(d => { }));

            // F1 — interpolation space.
            yield return ("grad/space=OkLab",
                Grad(d => d.InterpolationSpace = GradientColorSpace.OkLab));
            yield return ("grad/space=Hsv",
                Grad(d => d.InterpolationSpace = GradientColorSpace.Hsv));

            // F2 — interpolation curve.
            yield return ("grad/curve=Cosine",
                Grad(d => d.InterpolationCurve = InterpolationCurve.Cosine));
            yield return ("grad/curve=Cubic",
                Grad(d => d.InterpolationCurve = InterpolationCurve.Cubic));
            yield return ("grad/curve=Step",
                Grad(d => d.InterpolationCurve = InterpolationCurve.Step));

            // F3 — transfer function + strength.
            yield return ("grad/xfer=Sqrt",
                Grad(d => d.TransferFunction = TransferFunction.Sqrt));
            yield return ("grad/xfer=Cubic",
                Grad(d => d.TransferFunction = TransferFunction.Cubic));
            yield return ("grad/xfer=Log",
                Grad(d => d.TransferFunction = TransferFunction.Log));
            yield return ("grad/xfer=Sine",
                Grad(d => d.TransferFunction = TransferFunction.Sine));
            yield return ("grad/xfer=Sqrt@0.5",
                Grad(d => { d.TransferFunction = TransferFunction.Sqrt; d.TransferStrength = 0.5f; }));

            // F7 — per-stop midpoint bias.
            yield return ("grad/midpoint=0.3",
                Grad(d => d.Stops = BaseStops(0.3f)));

            // F6 — palette gamma (baked).
            yield return ("grad/gamma=0.5",
                Grad(d => d.PaletteGamma = 0.5f));
            yield return ("grad/gamma=2.0",
                Grad(d => d.PaletteGamma = 2.0f));

            // Cross term — perceptual blend + curved transfer together.
            yield return ("grad/OkLab+Sine",
                Grad(d => { d.InterpolationSpace = GradientColorSpace.OkLab; d.TransferFunction = TransferFunction.Sine; }));

            // ── Cycling kind ─────────────────────────────────────────────────
            yield return ("cyc/baseline",
                Cyc(d => { }));

            // F4 — offset / density.
            yield return ("cyc/offset=0.25",
                Cyc(d => d.ColorOffset = 0.25f));
            yield return ("cyc/density=2.0",
                Cyc(d => d.ColorDensity = 2.0f));

            // F5 — wrap mode.
            yield return ("cyc/wrap=PingPong",
                Cyc(d => d.WrapMode = ColorWrapMode.PingPong));
            yield return ("cyc/wrap=Clamp",
                Cyc(d => d.WrapMode = ColorWrapMode.Clamp));

            // Cross term — cycling + OkLab + gamma.
            yield return ("cyc/OkLab+gamma2",
                Cyc(d => { d.InterpolationSpace = GradientColorSpace.OkLab; d.PaletteGamma = 2.0f; }));
        }

        private static ColorThemeData Grad(Action<ColorThemeData> tweak)
        {
            var d = new ColorThemeData
            {
                Name = "probe-grad",
                Kind = ColorThemeKind.Gradient,
                Stops = BaseStops(),
            };
            tweak(d);
            return d;
        }

        private static ColorThemeData Cyc(Action<ColorThemeData> tweak)
        {
            var d = new ColorThemeData
            {
                Name = "probe-cyc",
                Kind = ColorThemeKind.Cycling,
                Stops = BaseStops(),
                CycleSpeed = 0.02f,
            };
            tweak(d);
            return d;
        }
    }
}
