// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Diagnostics/HeightfieldRaymarchProbe.cs
//
// Gate for #102 Phase 2 — oblique heightfield RAYMARCH (approach B).
//
// Phase 1 (--heightfieldspike) proved the screen-space hillshade. This gate
// drives the production Phase 2 path: HeightfieldRaymarch2D.Render extrudes a
// Mandelbrot smooth-count field into a 3D surface and raymarches it from an
// oblique camera through the full ShadingPipeline. Asserts the render is a real
// 3D view — a silhouette against sky (ray-miss pixels) plus lit surface pixels
// — and that turning on volumetric fog (LightingFxData.FogDensity / VolumeSteps)
// measurably changes the image (the Q5 volumetric payoff).
//
// Output: two PPMs (no-fog + fog) next to the exe and heightfieldraymarch.out.

using System;
using System.Globalization;
using System.IO;
using System.Text;

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Diagnostics;

/// <summary>Headless gate for the #102 Phase 2 oblique heightfield raymarch.
/// See file header + Docs/Technical/Heightfield-Relief-Spike.md (approach B).</summary>
public static class HeightfieldRaymarchProbe
{
    /// <summary>Build a Mandelbrot smooth-count height field + a simple themed
    /// colour buffer for it (used as the raymarch albedo source).</summary>
    private static (float[] height, uint[] albedo) BuildField(
        int w, int h, double centerX, double centerY, double span, int maxIter)
    {
        float[] height = new float[w * h];
        uint[] albedo = new uint[w * h];
        double pxScale = span / w;
        double originX = centerX - span * 0.5;
        double originY = centerY - (span * h / w) * 0.5;
        double maxSmooth = 1e-9;
        double[] sm = new double[w * h];

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double cr = originX + x * pxScale, ci = originY + y * pxScale;
            double zr = 0, zi = 0; int i = 0;
            for (; i < maxIter; i++)
            {
                double zr2 = zr * zr, zi2 = zi * zi;
                if (zr2 + zi2 > 256.0) break;
                double nzr = zr2 - zi2 + cr;
                zi = 2.0 * zr * zi + ci; zr = nzr;
            }
            int idx = y * w + x;
            if (i >= maxIter) { sm[idx] = 0.0; continue; }
            double mag = Math.Sqrt(zr * zr + zi * zi);
            double smooth = i + 1.0 - Math.Log(Math.Log(Math.Max(mag, 1.0000001)) / Math.Log(2.0)) / Math.Log(2.0);
            if (smooth < 0) smooth = 0;
            sm[idx] = smooth;
            if (smooth > maxSmooth) maxSmooth = smooth;
        }

        for (int idx = 0; idx < w * h; idx++)
        {
            height[idx] = (float)sm[idx];
            double t = sm[idx] / maxSmooth;                 // [0,1]
            // Simple warm→cool ramp so the surface carries colour.
            byte r = (byte)(255 * Math.Clamp(0.20 + 0.80 * t, 0, 1));
            byte g = (byte)(255 * Math.Clamp(0.30 + 0.50 * t, 0, 1));
            byte b = (byte)(255 * Math.Clamp(0.60 - 0.30 * t, 0, 1));
            albedo[idx] = sm[idx] <= 0.0
                ? 0xFF101018u                                // interior = dark plane
                : 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        return (height, albedo);
    }

    private static void WritePpm(string path, uint[] argb, int w, int h)
    {
        byte[] rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            uint c = argb[i];
            rgb[i * 3] = (byte)((c >> 16) & 0xFF);
            rgb[i * 3 + 1] = (byte)((c >> 8) & 0xFF);
            rgb[i * 3 + 2] = (byte)(c & 0xFF);
        }
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        byte[] hdr = Encoding.ASCII.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"P6\n{w} {h}\n255\n"));
        fs.Write(hdr, 0, hdr.Length);
        fs.Write(rgb, 0, rgb.Length);
    }

    /// <summary>CLI entry (`--heightfieldraymarch`).</summary>
    public static int RunGate()
    {
        const int w = 480, h = 360;
        var sb = new StringBuilder();
        sb.AppendLine("Heightfield-raymarch gate (#102 Phase 2) — oblique 3D + volumetric");

        var (height, albedo) = BuildField(w, h, -0.75, 0.0, 3.0, 400);

        // Base oblique 3D render, fog off.
        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25.0,
            Relief2DCameraElevationDeg = 50.0,
            Relief2DCameraFovDeg = 55.0,
        };
        var lit = LightingFxData.CreateDefault();          // key light on, fog off
        lit.BgTopColor = 0xFF335588u;
        lit.BgBottomColor = 0xFF0A0C14u;
        p.Lighting = lit;

        uint[] noFog = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, p, noFog, out double surfFrac);

        // A genuine 3D view has BOTH surface hits and ray-miss sky (silhouette).
        double skyFrac = 1.0 - surfFrac, sumNoFog = 0;
        for (int i = 0; i < w * h; i++)
        {
            uint c = noFog[i];
            sumNoFog += ((c >> 16) & 0xFF) * 0.3 + ((c >> 8) & 0xFF) * 0.59 + (c & 0xFF) * 0.11;
        }

        // Fog on — volumetric in-scatter through the same DE.
        var pf = p.Clone();
        var flit = pf.Lighting;
        flit.FogDensity = 0.9;
        flit.VolumeSteps = 24;
        flit.FogHeightFalloff = 0.0;
        pf.Lighting = flit;
        uint[] fog = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, pf, fog);

        long changed = 0; double sumFog = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (fog[i] != noFog[i]) changed++;
            uint c = fog[i];
            sumFog += ((c >> 16) & 0xFF) * 0.3 + ((c >> 8) & 0xFF) * 0.59 + (c & 0xFF) * 0.11;
        }
        double fogChangedFrac = (double)changed / (w * h);

        string p1 = Path.Combine(AppContext.BaseDirectory, "heightfield-raymarch.ppm");
        string p2 = Path.Combine(AppContext.BaseDirectory, "heightfield-raymarch-fog.ppm");
        WritePpm(p1, noFog, w, h);
        WritePpm(p2, fog, w, h);

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  size             {w}x{h}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  sky frac         {skyFrac:0.000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  surface frac     {surfFrac:0.000}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  mean lum no-fog  {sumNoFog / (w * h):0.0}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  mean lum fog     {sumFog / (w * h):0.0}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  fog changed frac {fogChangedFrac:0.000}"));
        sb.AppendLine($"  wrote            {p1}");
        sb.AppendLine($"  wrote            {p2}");

        // Non-degeneracy: a real 3D view has both sky and surface (silhouette),
        // and the volumetric fog path measurably altered the image.
        bool ok = skyFrac > 0.05 && surfFrac > 0.10 && fogChangedFrac > 0.05;
        sb.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");

        try { File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "heightfieldraymarch.out"), sb.ToString()); }
        catch { }
        Console.Write(sb.ToString());
        return ok ? 0 : 1;
    }
}
