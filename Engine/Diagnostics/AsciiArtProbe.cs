// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Diagnostics/AsciiArtProbe.cs
//
// Prototype + headless gate for #226 (ASCII / text-art export). Self-contained
// like HeightfieldReliefProbe: its own small Mandelbrot escape-time loop builds
// a BGRA ColorBuffer + a smooth-iteration field (exactly what the render host
// hands the exporter in production), then AsciiArtRenderer emits every supported
// format. The gate asserts each output is non-degenerate (right shape, actually
// varied) and writes the files next to the exe for eyeballing.

using System;
using System.Globalization;
using System.IO;
using System.Text;

using FracturingFog.Imaging;

namespace FracturingFog.Diagnostics;

/// <summary>Prototype/gate for the ASCII text-art exporter. See file header and #226.</summary>
public static class AsciiArtProbe
{
    /// <summary>Render a Mandelbrot region into a BGRA buffer + smooth field.</summary>
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

    // Cheap smooth-count → warm/cool BGRA (0xAARRGGBB) palette, just so the
    // colored formats have something faithful to tint from.
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

    /// <summary>CLI entry (`--asciiart`). Renders one region into all formats,
    /// writes the files, asserts each is non-degenerate, returns 0 on success.</summary>
    public static int RunGate()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ASCII text-art export prototype (#226) — Mandelbrot → all formats");

        const int w = 900, h = 600;
        var (px, smooth) = RenderSource(w, h, centerX: -0.75, centerY: 0.0, span: 3.2, maxIter: 500);

        string dir = Path.Combine(AppContext.BaseDirectory, "asciiart");
        Directory.CreateDirectory(dir);

        var formats = new[]
        {
            AsciiArtFormat.PlainText,
            AsciiArtFormat.Ansi,
            AsciiArtFormat.AnsiHalfBlock,
            AsciiArtFormat.Html,
            AsciiArtFormat.Svg,
            AsciiArtFormat.Braille,
        };

        bool allOk = true;
        foreach (var fmt in formats)
        {
            var opt = new AsciiArtOptions { Format = fmt, Columns = 120 };
            if (fmt == AsciiArtFormat.PlainText || fmt == AsciiArtFormat.Braille)
                opt.WithFineRamp();

            string text;
            try { text = AsciiArtRenderer.Render(px, smooth, w, h, opt); }
            catch (Exception ex)
            {
                sb.AppendLine($"  {fmt,-14} FAIL (threw: {ex.Message})");
                allOk = false;
                continue;
            }

            string path = Path.Combine(dir, $"mandelbrot-{fmt}{AsciiArtRenderer.ExtensionFor(fmt)}");
            try { File.WriteAllText(path, text, new UTF8Encoding(false)); } catch { }

            // Non-degeneracy: has content, has >1 line, and the glyph set varies
            // (not a solid wall of one character — proves the ramp mapped range).
            int nl = 0; foreach (char c in text) if (c == '\n') nl++;
            bool varied = HasGlyphVariety(text, fmt);
            bool ok = text.Length > 0 && nl > 1 && varied;
            allOk &= ok;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {fmt,-14} {(ok ? "PASS" : "FAIL")}  {text.Length,8} chars  {nl,4} lines  {(varied ? "varied" : "FLAT")}  -> {Path.GetFileName(path)}"));
        }

        sb.AppendLine(allOk ? "RESULT: PASS" : "RESULT: FAIL");
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "asciiart.out"), sb.ToString()); }
        catch { }
        Console.Write(sb.ToString());
        return allOk ? 0 : 1;
    }

    // "Varied" = more than one distinct printable glyph appears. For the color/
    // markup formats we look past escape/markup noise at the actual cell glyphs.
    private static bool HasGlyphVariety(string text, AsciiArtFormat fmt)
    {
        var seen = new System.Collections.Generic.HashSet<char>();
        foreach (char c in text)
        {
            if (char.IsControl(c)) continue;
            // Braille: every non-newline char is a data glyph.
            if (fmt == AsciiArtFormat.Braille) { if (c != '\n') seen.Add(c); }
            else seen.Add(c);
            if (seen.Count > 3) return true;
        }
        return seen.Count > 1;
    }
}
