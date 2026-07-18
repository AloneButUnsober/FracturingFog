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
