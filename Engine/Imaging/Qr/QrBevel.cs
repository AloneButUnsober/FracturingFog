// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/Qr/QrBevel.cs
//
// #1409 (3D-look QR, phase A of #1408) - the Bevel style. Dark modules become
// raised blocks on a light floor: a heightfield at output resolution (blocks of
// connected dark modules merge, so finders read as solid rings), a bevel ramp
// at the block edges, Blinn-Phong shading RELATIVE to a flat surface (plateaus
// and floor keep their steered colour, only slopes shade) and a soft cast
// shadow from the key light onto the floor.
//
// Readability: a decoder samples module centres. The luma band follows each
// pixel's distance to the nearest pixel of the OTHER shade: the strict core
// band from (0.5 - dot/2) modules in (which covers every sampled centre),
// widening smoothly toward an edge. Measuring to the other shade rather than
// the module border keeps a run of same-shade modules one continuous face (no
// core square per module) and lets a light floor darken toward the walls like
// ambient occlusion. Lighting comes after the steer, and every pixel is then
// clamped back into its bound (data modules may widen to the Dots style's soft
// band at an edge; function modules and the quiet zone only to the edge
// band). Centres are flat anyway: the bevel is narrower than the gap between
// the core and the module edge. QrVerifier, on the saved file, is the arbiter.
//
// Not HeightfieldRelief2D: that pass treats height <= 0 as the unlit interior,
// which here is the whole light floor (shadows must land on it), and it reads
// FractalParameters / a shared static scratch.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Imaging.Qr
{
    /// <summary>One directional light in SAVED-image space: x right, y up, z
    /// toward the viewer (normalised). Colours 0..1; <paramref name="Weight"/>
    /// scales the light's share of the shading.</summary>
    public sealed record QrBevelLight(double X, double Y, double Z,
        double DiffR, double DiffG, double DiffB,
        double SpecR, double SpecG, double SpecB,
        double Shininess, double Weight);

    /// <summary>The lights the Bevel style shades with. The first light casts
    /// the shadow.</summary>
    public sealed record QrBevelLighting(IReadOnlyList<QrBevelLight> Lights, string Source)
    {
        public const double FillWeight = 0.35, RimWeight = 0.2;

        /// <summary>#1410 — no lights: the Bevel style's smooth bands without its
        /// shading (the relief raymarch lights the extruded code itself).</summary>
        public static QrBevelLighting None { get; } = new(Array.Empty<QrBevelLight>(), "none");

        /// <summary>A single white light at azimuth / elevation (degrees; azimuth
        /// counter-clockwise from +x, so 135 = from the upper left).</summary>
        public static QrBevelLighting FromAngles(double azimuthDeg, double elevationDeg, string source = "explicit")
        {
            double az = azimuthDeg * Math.PI / 180, el = Math.Clamp(elevationDeg, 1, 89) * Math.PI / 180;
            var l = new QrBevelLight(Math.Cos(az) * Math.Cos(el), Math.Sin(az) * Math.Cos(el), Math.Sin(el),
                1, 1, 1, 1, 1, 1, 32, 1);
            return new QrBevelLighting(new[] { l }, source);
        }

        /// <summary>What <paramref name="opt"/> asks for: the theme's key / fill /
        /// rim lights when <see cref="QrCodeOptions.BevelLightFromTheme"/> and the
        /// colour map carries 3D lights, else the explicit (or default) angles.
        /// <paramref name="rotatedClockwise"/>: the saved image is the 90° CW
        /// rotation of the render, so the theme's lights turn with it.</summary>
        public static QrBevelLighting Resolve(QrCodeOptions opt, IColorMap? map, bool rotatedClockwise = false)
        {
            if (opt.BevelLightFromTheme && map is IThemeLights t)
            {
                var lights = new List<QrBevelLight> { FromTheme(t.ExportKeyLight, 1) };
                lights.Add(FromTheme(t.ExportFillLight, FillWeight));
                if (t.ExportUseRimLight) lights.Add(FromTheme(t.ExportRimLight, RimWeight));
                var lit = new QrBevelLighting(lights, "theme");
                return rotatedClockwise ? lit.RotatedClockwise() : lit;
            }
            return FromAngles(opt.BevelAzimuthDeg, opt.BevelElevationDeg,
                opt.BevelLightFromTheme ? "default (theme has no 3D lights)" : "explicit");
        }

        /// <summary>The same lights after the image is turned 90° clockwise
        /// (y up: (x, y) → (y, -x)).</summary>
        public QrBevelLighting RotatedClockwise()
        {
            var r = new List<QrBevelLight>(Lights.Count);
            foreach (var l in Lights) r.Add(l with { X = l.Y, Y = -l.X });
            return this with { Lights = r };
        }

        // Theme lights are complex-plane convention: x right, y UP (an
        // "upper-left" key is (-, +, +)), which is the saved image's
        // orientation too. Diffuse colours are tints (max channel -> 1); a light
        // with no specular colour (the PBR themes) gets a white highlight so
        // BevelSpecular still does something.
        private static QrBevelLight FromTheme(LightSource s, double weight)
        {
            double len = Math.Sqrt(s.Lx * s.Lx + s.Ly * s.Ly + s.Lz * s.Lz);
            double x = 0, y = 0, z = 1;
            if (len > 1e-6) { x = s.Lx / len; y = s.Ly / len; z = s.Lz / len; }
            if (z < 0.02) z = 0.02;   // a light below the plane would only shadow
            double dm = Math.Max(1e-6, Math.Max(s.DiffR, Math.Max(s.DiffG, s.DiffB)));
            double sm = Math.Max(s.SpecR, Math.Max(s.SpecG, s.SpecB));
            bool noSpec = sm <= 1e-3;
            if (noSpec) sm = 1;
            return new QrBevelLight(x, y, z,
                s.DiffR / dm, s.DiffG / dm, s.DiffB / dm,
                noSpec ? 1 : s.SpecR / sm, noSpec ? 1 : s.SpecG / sm, noSpec ? 1 : s.SpecB / sm,
                s.Shininess >= 4 ? Math.Min(s.Shininess, 256) : 32, weight);
        }
    }

    public static class QrBevel
    {
        // Diffuse slope gain (relative to a flat surface) and the shadow's
        // penumbra as a share of the block height.
        private const double DiffuseGain = 1.3;
        private const double PenumbraOfHeight = 0.25;

        /// <summary>Composite <paramref name="code"/> into <paramref name="buf"/>
        /// (straight-alpha BGRA, in place) in the Bevel style: steer, light, and
        /// hold every pixel in its band. Covers the symbol and the quiet zone.</summary>
        public static void Composite(uint[] buf, int w, int h, QrLayout L, QrCode code, QrCodeOptions opt,
            QrBevelLighting? lighting)
        {
            lighting ??= QrBevelLighting.FromAngles(opt.BevelAzimuthDeg, opt.BevelElevationDeg, "default");
            int m = L.ModulePixels;
            bool invert = opt.Invert;

            // Work region: symbol + quiet zone, clipped to the image.
            int q = L.QuietPixels;
            int rx0 = Math.Max(0, L.OriginX - q), ry0 = Math.Max(0, L.OriginY - q);
            int rx1 = Math.Min(w, L.OriginX + L.SymbolPixels + q), ry1 = Math.Min(h, L.OriginY + L.SymbolPixels + q);
            int rw = rx1 - rx0, rh = ry1 - ry0;
            if (rw <= 0 || rh <= 0) return;
            int n = rw * rh;

            // Per pixel: shown dark (the quiet zone is light unless inverted),
            // raised (a dark module), function module.
            var dark = new bool[n];
            var raised = new bool[n];
            var func = new bool[n];
            for (int y = 0; y < rh; y++)
                for (int x = 0; x < rw; x++)
                {
                    int i = y * rw + x;
                    if (ModuleAt(L, rx0 + x, ry0 + y, out int mx, out int my))
                    {
                        dark[i] = raised[i] = code.Dark[my, mx] ^ invert;
                        func[i] = code.IsFunction[my, mx];
                    }
                    else dark[i] = invert;
                }

            // Distance (px, centre to centre) to the nearest pixel of the other
            // shade. It drives the band: the strict core band from
            // (0.5 - dot/2) modules in, which covers every module's sampled
            // centre, widening smoothly toward the edge. Measured to the other
            // SHADE, not to the module border, so merged runs of one shade stay
            // one continuous face (no core square per module) and a light floor
            // darkens toward the walls like ambient occlusion.
            var toLight = Chamfer(dark, rw, rh);
            var toDark = Chamfer(Not(dark), rw, rh);
            double frac = Math.Clamp(opt.DotFraction, QrCodeOptions.MinDotFraction, QrCodeOptions.MaxDotFraction);
            double coreHalf = Math.Max(1, Math.Round(m * frac)) / (2.0 * m);
            double coreReach = Math.Max(1.0, (0.5 - coreHalf) * m);
            var core = new float[n];   // 0 at an edge .. 1 in the core
            for (int i = 0; i < n; i++)
            {
                double t = Math.Clamp(((dark[i] ? toLight[i] : toDark[i]) - 0.5) / coreReach, 0, 1);
                core[i] = (float)(t * t * (3 - 2 * t));
            }

            // 1. Steer: luma into the band, keeping hue / chroma.
            Parallel.For(0, rh, y =>
            {
                for (int x = 0; x < rw; x++)
                {
                    int i = y * rw + x, gi = (ry0 + y) * w + rx0 + x;
                    double t = core[i];
                    buf[gi] = dark[i]
                        ? QrCompositor.Steer(buf[gi], 0, Lerp(QrCompositor.DarkEdgeMax, QrCompositor.DarkCoreMax, t))
                        : QrCompositor.Steer(buf[gi], Lerp(QrCompositor.LightEdgeMin, QrCompositor.LightCoreMin, t), 255);
                }
            });

            // #1410 — unlit (QrBevelLighting.None): the smooth bands only, for an
            // albedo the relief raymarch lights for real.
            if (lighting.Lights.Count == 0) goto Bound;

            // 2. Heightfield: raised blocks with a smoothstep bevel at their edges.
            var toFloor = Chamfer(raised, rw, rh);
            double bevelPx = Math.Max(1.0, Math.Clamp(opt.BevelWidth, QrCodeOptions.MinBevelWidth, QrCodeOptions.MaxBevelWidth) * m);
            double heightPx = Math.Clamp(opt.BevelHeight, QrCodeOptions.MinBevelHeight, QrCodeOptions.MaxBevelHeight) * m;
            var hf = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (!raised[i]) continue;
                double t = Math.Clamp((toFloor[i] - 0.5) / bevelPx, 0, 1);
                hf[i] = (float)(heightPx * t * t * (3 - 2 * t));
            }

            // 3. Light: Blinn-Phong relative to a flat surface, plus a soft
            //    cast shadow from the key light.
            var lights = lighting.Lights;
            var flatDiff = new double[lights.Count];
            var flatSpec = new double[lights.Count];
            var half = new (double X, double Y, double Z)[lights.Count];
            for (int k = 0; k < lights.Count; k++)
            {
                var l = lights[k];
                double hx = l.X, hy = l.Y, hz = l.Z + 1, hl = Math.Sqrt(hx * hx + hy * hy + hz * hz);
                half[k] = (hx / hl, hy / hl, hz / hl);
                flatDiff[k] = Math.Max(0, l.Z);
                flatSpec[k] = Math.Pow(Math.Max(0, half[k].Z), l.Shininess);
            }
            double specStrength = Math.Clamp(opt.BevelSpecular, 0, 1);
            double shadowStrength = Math.Clamp(opt.BevelShadow, 0, 1);

            // The shadow walk goes toward the key light in SCREEN pixels (y down).
            var key = lights[0];
            double kxy = Math.Sqrt(key.X * key.X + key.Y * key.Y);
            double sx = kxy > 1e-6 ? key.X / kxy : 0, sy = kxy > 1e-6 ? -key.Y / kxy : 0;
            double rise = kxy > 1e-6 ? key.Z / kxy : double.PositiveInfinity;   // height per px travelled
            int maxSteps = kxy > 1e-6 ? (int)Math.Ceiling(heightPx / rise) + 1 : 0;
            double penumbra = Math.Max(0.5, heightPx * PenumbraOfHeight);

            Parallel.For(0, rh, y =>
            {
                for (int x = 0; x < rw; x++)
                {
                    int i = y * rw + x, gi = (ry0 + y) * w + rx0 + x;

                    // Normal from the central-difference height gradient (y up).
                    double hL = hf[y * rw + Math.Max(0, x - 1)], hR = hf[y * rw + Math.Min(rw - 1, x + 1)];
                    double hU = hf[Math.Max(0, y - 1) * rw + x], hD = hf[Math.Min(rh - 1, y + 1) * rw + x];
                    double dhx = (hR - hL) * 0.5, dhy = (hU - hD) * 0.5;
                    double nl = Math.Sqrt(dhx * dhx + dhy * dhy + 1);
                    double nx = -dhx / nl, ny = -dhy / nl, nz = 1 / nl;

                    double tr = 0, tg = 0, tb = 0, spr = 0, spg = 0, spb = 0;
                    bool slope = dhx != 0 || dhy != 0;
                    if (slope)
                        for (int k = 0; k < lights.Count; k++)
                        {
                            var l = lights[k];
                            double d = (Math.Max(0, nx * l.X + ny * l.Y + nz * l.Z) - flatDiff[k]) * l.Weight;
                            // Brightening takes the light's tint; darkening is neutral.
                            if (d > 0) { tr += d * l.DiffR; tg += d * l.DiffG; tb += d * l.DiffB; }
                            else { tr += d; tg += d; tb += d; }
                            double s = Math.Pow(Math.Max(0, nx * half[k].X + ny * half[k].Y + nz * half[k].Z), l.Shininess)
                                       - flatSpec[k];
                            if (s > 0) { s *= l.Weight; spr += s * l.SpecR; spg += s * l.SpecG; spb += s * l.SpecB; }
                        }

                    double shade = 0;
                    if (shadowStrength > 0 && maxSteps > 0)
                    {
                        double h0 = hf[i];
                        double px = x, py = y;
                        for (int s = 1; s <= maxSteps; s++)
                        {
                            px += sx; py += sy;
                            int ix = (int)(px + 0.5), iy = (int)(py + 0.5);
                            if ((uint)ix >= (uint)rw || (uint)iy >= (uint)rh) break;
                            double over = hf[iy * rw + ix] - (h0 + rise * s);
                            if (over > -penumbra)
                            {
                                double a = Math.Min(1, (over + penumbra) / (2 * penumbra));
                                if (a > shade) shade = a;
                                if (shade >= 1) break;
                            }
                        }
                    }
                    if (!slope && shade <= 0) continue;

                    uint c = buf[gi];
                    double mul = 1 - shadowStrength * shade, lit = 1 - shade;
                    double r = ((c >> 16) & 0xFF) * (1 + DiffuseGain * tr) * mul + 255 * specStrength * spr * lit;
                    double g = ((c >> 8) & 0xFF) * (1 + DiffuseGain * tg) * mul + 255 * specStrength * spg * lit;
                    double b = (c & 0xFF) * (1 + DiffuseGain * tb) * mul + 255 * specStrength * spb * lit;
                    buf[gi] = 0xFF000000u | (uint)ClampByte(r) << 16 | (uint)ClampByte(g) << 8 | (uint)ClampByte(b);
                }
            });

            Bound:
            // 4. Hold every pixel in its band: the core band in the core, widening
            //    toward an edge to the Dots style's soft band (function modules and
            //    the quiet zone only to the edge band), so a decoder still reads the
            //    shade while the faces keep their lighting.
            Parallel.For(0, rh, y =>
            {
                for (int x = 0; x < rw; x++)
                {
                    int i = y * rw + x, gi = (ry0 + y) * w + rx0 + x;
                    double t = core[i];
                    bool strict = func[i] || !ModuleAt(L, rx0 + x, ry0 + y, out _, out _);
                    buf[gi] = dark[i]
                        ? QrCompositor.ClampLuma(buf[gi], 0,
                            Lerp(strict ? QrCompositor.DarkEdgeMax : QrCompositor.DarkSoftMax, QrCompositor.DarkCoreMax, t))
                        : QrCompositor.ClampLuma(buf[gi],
                            Lerp(strict ? QrCompositor.LightEdgeMin : QrCompositor.LightSoftMin, QrCompositor.LightCoreMin, t), 255);
                }
            });

            // 5. Hold every module's MEAN on its side, at Steer's worst case (light
            //    >= the light edge band, dark <= the dark edge band). A shrunken or
            //    soft-focus image averages a module, and a long shadow can cover most
            //    of a light module. The fix scales the module's luma uniformly toward
            //    its end of the range, so the relative lighting is kept.
            Parallel.For(0, code.Size, my =>
            {
                for (int mx = 0; mx < code.Size; mx++)
                {
                    bool d = code.Dark[my, mx] ^ invert;
                    int x0 = L.OriginX + mx * m, y0 = L.OriginY + my * m;
                    long sum = 0;
                    for (int y = y0; y < y0 + m; y++)
                        for (int x = x0; x < x0 + m; x++) sum += QrVerifier.LumaOf(buf[y * w + x]);
                    double mean = (double)sum / (m * m);
                    int lo = 0, hi = 255;
                    if (!d && mean < QrCompositor.LightEdgeMin)
                        lo = (int)Math.Ceiling(255 * (QrCompositor.LightEdgeMin - mean) / (255 - mean));
                    else if (d && mean > QrCompositor.DarkEdgeMax)
                        hi = (int)Math.Floor(255 * QrCompositor.DarkEdgeMax / mean);
                    else continue;
                    for (int y = y0; y < y0 + m; y++)
                        for (int x = x0; x < x0 + m; x++) buf[y * w + x] = QrCompositor.Steer(buf[y * w + x], lo, hi);
                }
            });
        }

        private static int Lerp(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

        private static bool[] Not(bool[] a)
        {
            var r = new bool[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = !a[i];
            return r;
        }

        private static bool ModuleAt(QrLayout L, int x, int y, out int mx, out int my)
        {
            int dx = x - L.OriginX, dy = y - L.OriginY;
            mx = my = 0;
            if (dx < 0 || dy < 0 || dx >= L.SymbolPixels || dy >= L.SymbolPixels) return false;
            mx = dx / L.ModulePixels; my = dy / L.ModulePixels;
            return true;
        }

        /// <summary>3-4 chamfer distance (in px, centre to centre) from each set
        /// pixel to the nearest unset pixel; unset pixels are 0. With no unset
        /// pixel at all the distances stay huge (treated as "far").</summary>
        public static float[] Chamfer(bool[] set, int w, int h)
        {
            const float Inf = 1e9f, A = 1f, D = 4f / 3f;
            var d = new float[w * h];
            for (int i = 0; i < d.Length; i++) d[i] = set[i] ? Inf : 0f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (d[i] == 0f) continue;
                    float v = d[i];
                    if (x > 0) v = Math.Min(v, d[i - 1] + A);
                    if (y > 0)
                    {
                        v = Math.Min(v, d[i - w] + A);
                        if (x > 0) v = Math.Min(v, d[i - w - 1] + D);
                        if (x < w - 1) v = Math.Min(v, d[i - w + 1] + D);
                    }
                    d[i] = v;
                }
            for (int y = h - 1; y >= 0; y--)
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = y * w + x;
                    if (d[i] == 0f) continue;
                    float v = d[i];
                    if (x < w - 1) v = Math.Min(v, d[i + 1] + A);
                    if (y < h - 1)
                    {
                        v = Math.Min(v, d[i + w] + A);
                        if (x < w - 1) v = Math.Min(v, d[i + w + 1] + D);
                        if (x > 0) v = Math.Min(v, d[i + w - 1] + D);
                    }
                    d[i] = v;
                }
            return d;
        }

        private static int ClampByte(double v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)Math.Round(v);
    }
}
