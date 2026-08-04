// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Diagnostics/AsciiAnimationProbe.cs
//
// Prototype + headless gate for #230 (ASCII animation recording). Reuses the
// self-contained Mandelbrot source from AsciiArtProbe to build one still frame,
// then drives the #229 FX chain across a handful of time steps (hue + breathe)
// so consecutive frames genuinely differ, records them, and serializes every
// AsciiAnimationFormat. The gate asserts each container is well-formed and
// non-degenerate (right frame count, frames vary) and writes the files next to
// the exe for eyeballing.

using System;
using System.Globalization;
using System.IO;
using System.Text;

using FracturingFog.Imaging;

namespace FracturingFog.Diagnostics;

/// <summary>Prototype/gate for the ASCII animation recorder. See file header and #230.</summary>
public static class AsciiAnimationProbe
{
    // Small self-contained Mandelbrot region → BGRA buffer + smooth field, same
    // escape-time loop AsciiArtProbe uses (kept local so the gate has no engine
    // wiring dependency).
    private static (uint[] px, float[] smooth) RenderSource(
        int w, int h, double centerX, double centerY, double span, int maxIter)
    {
        var px = new uint[w * h];
        var smooth = new float[w * h];
        double pxScale = span / w;
        double originX = centerX - span * 0.5;
        double originY = centerY - (span * h / w) * 0.5;

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double cr = originX + x * pxScale;
            double ci = originY + y * pxScale;
            double zr = 0, zi = 0;
            int i = 0;
            for (; i < maxIter; i++)
            {
                double zr2 = zr * zr, zi2 = zi * zi;
                if (zr2 + zi2 > 256.0) break;
                double nzr = zr2 - zi2 + cr;
                zi = 2.0 * zr * zi + ci;
                zr = nzr;
            }
            int idx = y * w + x;
            if (i >= maxIter) { smooth[idx] = 0f; px[idx] = 0xFF000000u; continue; }
            double mag = Math.Sqrt(zr * zr + zi * zi);
            double s = i + 1.0 - Math.Log(Math.Log(Math.Max(mag, 1.0000001)) / Math.Log(2.0)) / Math.Log(2.0);
            if (s < 0) s = 0;
            smooth[idx] = (float)s;
            px[idx] = Palette(s);
        }
        return (px, smooth);
    }

    private static uint Palette(double smooth)
    {
        double t = 0.5 + 0.5 * Math.Sin(0.12 * smooth);
        double r = 9 * (1 - t) * t * t * t * 255;
        double g = 15 * (1 - t) * (1 - t) * t * t * 255;
        double b = 8.5 * (1 - t) * (1 - t) * (1 - t) * t * 255;
        uint R = (uint)Math.Clamp(r, 0, 255);
        uint G = (uint)Math.Clamp(g, 0, 255);
        uint B = (uint)Math.Clamp(b, 0, 255);
        return 0xFF000000u | (R << 16) | (G << 8) | B;
    }

    /// <summary>CLI entry (`--asciianim`). Records an FX-animated Mandelbrot into
    /// every animation format, writes the files, asserts each is well-formed and
    /// its frames vary, returns 0 on success.</summary>
    public static int RunGate()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ASCII animation recorder prototype (#230) — FX-animated Mandelbrot");

        const int w = 640, h = 420;
        var (px, smooth) = RenderSource(w, h, centerX: -0.75, centerY: 0.0, span: 3.2, maxIter: 400);

        var opt = new AsciiArtOptions { Columns = 100 };
        opt.WithFineRamp();

        // Build the frames: same fractal, advancing the FX clock so hue rotates
        // and the glyph density breathes — a static fractal with living FX, the
        // exact case the live pump animates (#229).
        const int frames = 12;
        const double fps = 12.0;
        double dt = 1.0 / fps;
        var rec = new AsciiAnimationRecorder();
        for (int f = 0; f < frames; f++)
        {
            var cells = AsciiArtRenderer.RenderCells(px, smooth, w, h, opt, out int cols, out int rows);
            AsciiFxChain.Apply(cells, cols, rows, opt.Ramp, new AsciiFxSettings
            {
                TimeSeconds = f * dt,
                HueCycle = true, HueCycleDegPerSec = 90.0,
                Breathe = true, BreatheGammaAmp = 0.6, BreatheHz = 0.8,
            });
            rec.AddFrame(cells, cols, rows, dt);
        }

        string dir = Path.Combine(AppContext.BaseDirectory, "asciianim");
        Directory.CreateDirectory(dir);

        var formats = new[]
        {
            AsciiAnimationFormat.AsciinemaCast,
            AsciiAnimationFormat.AnimatedSvg,
            AsciiAnimationFormat.AnsiSequence,
        };

        bool allOk = true;
        foreach (var fmt in formats)
        {
            string text;
            try { text = rec.Serialize(fmt, opt); }
            catch (Exception ex)
            {
                sb.AppendLine($"  {fmt,-14} FAIL (threw: {ex.Message})");
                allOk = false;
                continue;
            }

            string path = Path.Combine(dir, $"mandelbrot-anim-{fmt}{AsciiAnimationRecorder.ExtensionFor(fmt)}");
            try { File.WriteAllText(path, text, new UTF8Encoding(false)); } catch { }

            bool ok = Validate(fmt, text, frames, out string note);
            allOk &= ok;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {fmt,-14} {(ok ? "PASS" : "FAIL")}  {text.Length,9} chars  {note}  -> {Path.GetFileName(path)}"));
        }

        sb.AppendLine(allOk ? "RESULT: PASS" : "RESULT: FAIL");
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "asciianim.out"), sb.ToString()); }
        catch { }
        Console.Write(sb.ToString());
        return allOk ? 0 : 1;
    }

    // Per-format structural + non-degeneracy checks.
    private static bool Validate(AsciiAnimationFormat fmt, string text, int frames, out string note)
    {
        switch (fmt)
        {
            case AsciiAnimationFormat.AsciinemaCast:
            {
                // Header line + one event per frame; events carry ANSI colour.
                int lines = 0; foreach (char c in text) if (c == '\n') lines++;
                bool header = text.StartsWith("{\"version\":2", StringComparison.Ordinal);
                int events = CountOccurrences(text, ", \"o\", \"");
                bool colour = text.Contains("38;2;", StringComparison.Ordinal);
                note = $"{events} events, {lines} lines";
                return header && events == frames && colour && lines >= frames + 1;
            }
            case AsciiAnimationFormat.AnimatedSvg:
            {
                int anims = CountOccurrences(text, "<animate ");
                bool svg = text.StartsWith("<svg", StringComparison.Ordinal) &&
                           text.TrimEnd().EndsWith("</svg>", StringComparison.Ordinal);
                bool loop = text.Contains("repeatCount=\"indefinite\"", StringComparison.Ordinal);
                note = $"{anims} frame tracks";
                return svg && anims == frames && loop;
            }
            case AsciiAnimationFormat.AnsiSequence:
            {
                int clears = CountOccurrences(text, "\x1b[2J");
                bool colour = text.Contains("38;2;", StringComparison.Ordinal);
                note = $"{clears} frame clears";
                return clears == frames && colour;
            }
            default:
                note = "unknown";
                return false;
        }
    }

    private static int CountOccurrences(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }
}
