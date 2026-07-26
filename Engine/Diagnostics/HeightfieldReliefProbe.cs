// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Diagnostics/HeightfieldReliefProbe.cs
//
// Spike prototype for #102 — "real relief 3D" for 2D fractals.
//
// The current 2D "3D themes" are normal-map Phong bump only: they light the
// escape-potential's *slope* per pixel, but the surface has no actual height,
// so it cannot cast shadows onto itself, self-occlude, or show a silhouette.
// This probe demonstrates the cheapest honest upgrade — treat the smooth
// iteration count as a HEIGHT FIELD and do 2.5D relief shading:
//
//   1. Hillshade — normal from the height gradient, Lambert against a light.
//   2. Horizon cast shadow — march each pixel toward the light across the
//      height field (Bresenham-ish DDA), tracking the max elevation angle
//      seen so far; if some earlier sample subtends a higher angle than the
//      current pixel's ray to the light, the pixel is in shadow.
//
// This is a screen-space heightfield walk (O(W·H·steps)), no 3D camera, no DE.
// It proves the relief/shadow idea end-to-end; the design doc
// (Docs/Technical/Heightfield-Relief-Spike.md) weighs this against a full
// heightfield raymarch and the volumetric extension.
//
// Output: a binary PPM (P6) next to the exe + coverage stats to stdout. The
// probe is intentionally self-contained (its own tiny escape-time loop) so the
// spike doesn't entangle the render host; production would feed
// EscapeTimeCalculator.SmoothBuffer instead.

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FracturingFog.Diagnostics;

/// <summary>Spike prototype: 2.5D heightfield relief + cast shadows for a 2D
/// fractal (Mandelbrot). See file header + Docs/Technical/Heightfield-Relief-Spike.md.</summary>
public static class HeightfieldReliefProbe
{
    public readonly struct Result
    {
        public readonly int Width, Height;
        public readonly double ShadowFraction;   // [0,1] pixels in cast shadow
        public readonly double ExteriorFraction; // [0,1] escaped (has height)
        public readonly double MinRelief, MaxRelief;
        public readonly string OutputPath;
        public Result(int w, int h, double shadow, double ext,
                      double minR, double maxR, string path)
        { Width = w; Height = h; ShadowFraction = shadow; ExteriorFraction = ext;
          MinRelief = minR; MaxRelief = maxR; OutputPath = path; }
    }

    /// <summary>
    /// Renders a Mandelbrot heightfield-relief frame and writes a PPM.
    /// </summary>
    /// <param name="w">Image width.</param>
    /// <param name="h">Image height.</param>
    /// <param name="centerX">Region centre (real).</param>
    /// <param name="centerY">Region centre (imag).</param>
    /// <param name="span">Real-axis span of the view.</param>
    /// <param name="maxIter">Escape-time iteration cap.</param>
    /// <param name="heightScale">Vertical exaggeration of the height field
    /// (world height per unit normalised smooth-count). Larger = deeper carving,
    /// longer shadows.</param>
    /// <param name="lightAzimuthDeg">Light compass direction (0 = +x, 90 = +y).</param>
    /// <param name="lightElevationDeg">Light elevation above the plane. Lower =
    /// longer, more dramatic cast shadows.</param>
    /// <param name="outputPath">PPM path; null = "heightfield-relief.ppm" by exe.</param>
    public static Result Run(
        int w = 512, int h = 512,
        double centerX = -0.75, double centerY = 0.0, double span = 3.0,
        int maxIter = 400, double heightScale = 0.6,
        double lightAzimuthDeg = 135.0, double lightElevationDeg = 28.0,
        string? outputPath = null)
    {
        // ── 1. Height field from smooth iteration count ─────────────────────
        double[] height = new double[w * h];
        bool[] escaped = new bool[w * h];
        double pxScale = span / w;
        double originX = centerX - span * 0.5;
        double originY = centerY - (span * h / w) * 0.5;

        double maxSmooth = 0.0;
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
            if (i >= maxIter) { escaped[idx] = false; height[idx] = 0.0; continue; }
            // Smooth (continuous) escape count — the classic normalized-iteration.
            double mag = Math.Sqrt(zr * zr + zi * zi);
            double smooth = i + 1.0 - Math.Log(Math.Log(Math.Max(mag, 1.0000001)) / Math.Log(2.0)) / Math.Log(2.0);
            if (smooth < 0) smooth = 0;
            escaped[idx] = true;
            height[idx] = smooth;
            if (smooth > maxSmooth) maxSmooth = smooth;
        }

        // Normalise heights to [0,1], then to world units via heightScale.
        double invMax = maxSmooth > 1e-9 ? 1.0 / maxSmooth : 0.0;
        double minRelief = double.PositiveInfinity, maxRelief = double.NegativeInfinity;
        for (int p = 0; p < height.Length; p++)
        {
            double hh = height[p] * invMax * heightScale;
            height[p] = hh;
            if (escaped[p]) { if (hh < minRelief) minRelief = hh; if (hh > maxRelief) maxRelief = hh; }
        }
        if (double.IsInfinity(minRelief)) { minRelief = 0; maxRelief = 0; }

        // ── 2. Light direction (screen-space + elevation) ──────────────────
        double az = lightAzimuthDeg * Math.PI / 180.0;
        double el = lightElevationDeg * Math.PI / 180.0;
        double lx = Math.Cos(az), ly = Math.Sin(az);          // horizontal light heading
        double lightSlope = Math.Tan(el);                     // rise per unit horizontal travel

        // ── 3. Shade: hillshade × cast-shadow ──────────────────────────────
        byte[] rgb = new byte[w * h * 3];
        long shadowCount = 0, exteriorCount = 0;
        double stepLen = 1.0;                 // one pixel per march step
        int maxShadowSteps = Math.Max(w, h);  // march at most across the frame

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = y * w + x;
            int o = idx * 3;
            if (!escaped[idx])
            {
                // Interior — flat black (matches the set's usual in-set fill).
                rgb[o] = rgb[o + 1] = rgb[o + 2] = 0;
                continue;
            }
            exteriorCount++;

            // Hillshade: gradient of the height field → surface normal.
            double hL = SampleH(height, escaped, w, h, x - 1, y);
            double hR = SampleH(height, escaped, w, h, x + 1, y);
            double hD = SampleH(height, escaped, w, h, x, y - 1);
            double hU = SampleH(height, escaped, w, h, x, y + 1);
            double dhx = (hR - hL) / (2.0 * pxScale);
            double dhy = (hU - hD) / (2.0 * pxScale);
            // Normal = (-dhx, -dhy, 1) normalised.
            double nlen = Math.Sqrt(dhx * dhx + dhy * dhy + 1.0);
            double nx = -dhx / nlen, ny = -dhy / nlen, nz = 1.0 / nlen;
            // Light vector (unit): horizontal (lx,ly) scaled by cos(el), up sin(el).
            double lvx = lx * Math.Cos(el), lvy = ly * Math.Cos(el), lvz = Math.Sin(el);
            double lambert = Math.Max(0.0, nx * lvx + ny * lvy + nz * lvz);

            // Cast shadow: walk toward the light across the height field.
            double startH = height[idx];
            bool inShadow = false;
            double px = x, py = y;
            for (int s = 1; s <= maxShadowSteps; s++)
            {
                px += lx * stepLen; py += ly * stepLen;
                int sx = (int)Math.Round(px), sy = (int)Math.Round(py);
                if (sx < 0 || sy < 0 || sx >= w || sy >= h) break;
                int sidx = sy * w + sx;
                if (!escaped[sidx]) continue;               // interior = no occluder
                double travelled = s * stepLen * pxScale;   // world horizontal distance
                double rayHeight = startH + lightSlope * travelled;
                if (height[sidx] > rayHeight) { inShadow = true; break; }
            }
            if (inShadow) shadowCount++;

            // Combine: ambient + (shadowed ? 0 : lambert), tinted warm→cool by height.
            double ambient = 0.18;
            double lit = ambient + (inShadow ? 0.0 : 0.82 * lambert);
            lit = Math.Clamp(lit, 0.0, 1.0);
            double t = maxRelief > minRelief ? (startH - minRelief) / (maxRelief - minRelief) : 0.0;
            // Simple two-stop ramp: deep = blue-grey, high = warm.
            double r = lit * (0.25 + 0.75 * t);
            double g = lit * (0.32 + 0.45 * t);
            double b = lit * (0.55 - 0.25 * t);
            rgb[o]     = (byte)(Math.Clamp(r, 0, 1) * 255);
            rgb[o + 1] = (byte)(Math.Clamp(g, 0, 1) * 255);
            rgb[o + 2] = (byte)(Math.Clamp(b, 0, 1) * 255);
        }

        // ── 4. Write PPM (P6 binary) ───────────────────────────────────────
        string path = outputPath ?? Path.Combine(AppContext.BaseDirectory, "heightfield-relief.ppm");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            byte[] header = Encoding.ASCII.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"P6\n{w} {h}\n255\n"));
            fs.Write(header, 0, header.Length);
            fs.Write(rgb, 0, rgb.Length);
        }

        double shadowFrac = exteriorCount > 0 ? (double)shadowCount / exteriorCount : 0.0;
        double exteriorFrac = (double)exteriorCount / (w * h);
        return new Result(w, h, shadowFrac, exteriorFrac, minRelief, maxRelief, path);
    }

    private static double SampleH(double[] height, bool[] escaped, int w, int h, int x, int y)
    {
        if (x < 0) x = 0; else if (x >= w) x = w - 1;
        if (y < 0) y = 0; else if (y >= h) y = h - 1;
        int idx = y * w + x;
        // Interior reads as height 0 (the set sits at the base plane).
        return escaped[idx] ? height[idx] : 0.0;
    }

    /// <summary>CLI entry (`--heightfieldspike`). Renders the default frame,
    /// asserts the relief + cast-shadow pass produced a non-degenerate image,
    /// returns 0 on success.</summary>
    public static int RunGate()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Heightfield-relief spike (#102) — 2.5D relief + cast shadows on Mandelbrot");
        var res = Run();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  size            {res.Width}x{res.Height}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  exterior frac   {res.ExteriorFraction:0.000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  relief range    [{res.MinRelief:0.0000}, {res.MaxRelief:0.0000}]"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  cast-shadow frac {res.ShadowFraction:0.000}  (of exterior pixels)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  wrote           {res.OutputPath}"));

        // Non-degeneracy: some exterior, real relief, and cast shadows actually
        // occurred (proves the horizon walk found occluders — the whole point).
        bool ok = res.ExteriorFraction > 0.05
               && res.MaxRelief > res.MinRelief
               && res.ShadowFraction > 0.001;
        sb.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");

        try { File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "heightfieldspike.out"), sb.ToString()); }
        catch { }
        Console.Write(sb.ToString());
        return ok ? 0 : 1;
    }
}
