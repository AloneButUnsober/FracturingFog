// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/Qr/QrRelief.cs
//
// #1410 (3D-look QR, phase B of #1408) - extrude the QR code into the Relief 3D
// raymarch, so the blocks are real geometry: camera tilt, cast shadows, AO, the
// theme's lights, HDRI and glass all act on them.
//
//   1. Prepare (before the trace): pick the code and mask the flat way (decode-
//      verified composite, QrExport.Apply) and keep that steered composite as the
//      ALBEDO, so module colours are already in their bands.
//   2. Inject (inside HeightfieldRaymarch2D.Render, after its prepass): under the
//      code + quiet zone the fractal terrain is compressed toward its median
//      (a plinth), and dark modules rise by ReliefHeight modules with the Bevel
//      style's smoothstep ramp. Heights are in MODULE units, so a block looks the
//      same at any relief height scale. The edit lands in the prepared field that
//      both the CPU trace and the GPU kernel consume.
//   3. Resteer (on the final display buffer, after grade / tonemap / denoise):
//      every pixel's primary hit (depth AOV + the trace camera) is mapped back to
//      the field, so each pixel knows which module it shows and where in it.
//      Luma is clamped into the module's band (core band at the centre, edge band
//      elsewhere; the quiet zone light), the same rule the flat styles use.
//
// The camera elevation is raised to at least ReliefMinElevation: a low camera
// lets a block hide the light module behind it, and no steering can show a
// module the camera cannot see. QrVerifier on the saved file decides.

using System;
using System.Threading.Tasks;

using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Imaging.Qr
{
    /// <summary>Runtime state of a QR extruded into a relief poster: created by the
    /// poster path, filled by the relief stage, consumed when the QR is applied.</summary>
    public sealed class QrReliefState
    {
        public QrReliefState(QrCodeOptions options) => Options = options ?? throw new ArgumentNullException(nameof(options));
        public QrCodeOptions Options { get; }
        /// <summary>Set once the relief stage has traced the code (null = it did not
        /// run, e.g. relief raymarch off; the flat QR is applied instead).</summary>
        public QrReliefPlan? Plan { get; set; }
    }

    public sealed class QrReliefPlan
    {
        private QrReliefPlan(QrCodeOptions opt, QrApplyResult flat, int w, int h)
        {
            Options = opt; Flat = flat; W = w; H = h;
        }

        public QrCodeOptions Options { get; }
        /// <summary>The flat composite's code + layout (on the albedo / output grid).</summary>
        public QrApplyResult Flat { get; }
        public int W { get; }
        public int H { get; }
        public QrCode Code => Flat.Code;
        public QrLayout Layout => Flat.Layout;

        // Captured from the trace.
        private float[]? _depth;
        private HeightfieldRaymarch2D.ReliefCamera _cam;
        private double _sy;
        private double _plinthWorldY;

        public bool Traced => _depth != null;

        /// <summary>Choose the code (mask by decoding, as the flat path does) and
        /// composite it into <paramref name="albedo"/> in place. Bevel becomes Steer:
        /// the relief does the lighting for real.</summary>
        public static QrReliefPlan Prepare(uint[] albedo, int w, int h, QrCodeOptions opt)
        {
            // Steer / Bevel: the Bevel style's smooth bands, unlit (Steer's core
            // square would show on every lit face). Dots / Halftone keep their look.
            bool smooth = opt.Style is QrStyle.Steer or QrStyle.Bevel;
            var r = smooth
                ? QrExport.Apply(albedo, w, h, opt with { Style = QrStyle.Bevel }, QrBevelLighting.None)
                : QrExport.Apply(albedo, w, h, opt);
            return new QrReliefPlan(opt, r, w, h);
        }

        /// <summary>Camera elevation actually used: at least the QR minimum.</summary>
        public static double CappedElevation(double elevationDeg, QrCodeOptions opt)
            => Math.Max(elevationDeg, Math.Clamp(opt.ReliefMinElevationDeg, QrCodeOptions.MinReliefElevation, 89));

        /// <summary><see cref="HeightfieldRaymarch2D.ReliefHeightInjection"/>: plinth +
        /// blocks into the prepared field.</summary>
        public void Inject(float[] hbuf, int hw, int hh, double sy, double aspect)
        {
            if (sy <= 0 || hw <= 2 || hh <= 2) return;
            var L = Layout;
            double m = L.ModulePixels;
            // Field cell -> albedo pixel scale (both grids cover the same view).
            double ax = (double)W / hw, ay = (double)H / hh;
            double q = L.QuietPixels;
            double rx0 = L.OriginX - q, ry0 = L.OriginY - q;
            double rx1 = L.OriginX + L.SymbolPixels + q, ry1 = L.OriginY + L.SymbolPixels + q;
            double blend = 2 * m;   // terrain eases back to full over two modules outside

            // Plinth level: the median of the fractal under the code area.
            var under = new System.Collections.Generic.List<float>();
            for (int fy = 0; fy < hh; fy++)
            {
                double py = (fy + 0.5) * ay;
                if (py < ry0 || py >= ry1) continue;
                for (int fx = 0; fx < hw; fx++)
                {
                    double px = (fx + 0.5) * ax;
                    if (px >= rx0 && px < rx1) under.Add(hbuf[fy * hw + fx]);
                }
            }
            if (under.Count == 0) return;
            under.Sort();
            float baseH = under[under.Count / 2];
            _plinthWorldY = baseH * sy;
            double terrain = Math.Clamp(Options.ReliefTerrain, 0, 1);

            // Raised mask on the field grid, then the bevel ramp.
            var raised = new bool[hw * hh];
            bool invert = Options.Invert;
            for (int fy = 0; fy < hh; fy++)
                for (int fx = 0; fx < hw; fx++)
                    if (ModuleAt((fx + 0.5) * ax, (fy + 0.5) * ay, out int mx, out int my, out _, out _))
                        raised[fy * hw + fx] = Code.Dark[my, mx] ^ invert;
            var toFloor = QrBevel.Chamfer(raised, hw, hh);
            double cellsPerModule = m / Math.Max(ax, ay);
            double bevelCells = Math.Max(1.0, Math.Clamp(Options.BevelWidth, QrCodeOptions.MinBevelWidth, QrCodeOptions.MaxBevelWidth) * cellsPerModule);
            // Module height in world units = ReliefHeight x module size (world z spans
            // one image height), in field units / sy.
            double ext = Math.Clamp(Options.ReliefHeight, QrCodeOptions.MinReliefHeight, QrCodeOptions.MaxReliefHeight)
                         * (m / H) / sy;

            Parallel.For(0, hh, fy =>
            {
                double py = (fy + 0.5) * ay;
                for (int fx = 0; fx < hw; fx++)
                {
                    double px = (fx + 0.5) * ax;
                    double dOut = Math.Max(Math.Max(rx0 - px, px - rx1), Math.Max(ry0 - py, py - ry1));
                    if (dOut >= blend) continue;
                    int i = fy * hw + fx;
                    double keep = dOut <= 0 ? terrain : terrain + (1 - terrain) * Smooth(dOut / blend);
                    double hv = baseH + keep * (hbuf[i] - baseH);
                    if (raised[i])
                    {
                        double t = Math.Clamp((toFloor[i] - 0.5) / bevelCells, 0, 1);
                        hv += ext * Smooth(t);
                    }
                    hbuf[i] = (float)Math.Max(0.0, hv);
                }
            });
        }

        /// <summary>Keep what the trace needs to map pixels back to modules.</summary>
        public void CaptureTrace(HeightfieldRaymarch2D.ReliefAovBuffers aov)
        {
            if (aov.TraceCamera is not { } cam) return;
            _cam = cam;
            _sy = aov.TraceSy;
            _depth = (float[])aov.Depth.Clone();
        }

        /// <summary>Clamp the final display buffer's pixels into their module's band
        /// and return the code + the layout as it appears on screen (its module
        /// size estimated from the projection, for the verifier's small pass).</summary>
        public QrApplyResult Resteer(uint[] buf, int w, int h)
        {
            if (_depth == null || w != W || h != H) throw new InvalidOperationException("QR relief: no trace for this buffer.");
            var L = Layout;
            double aspect = (double)w / h;
            bool invert = Options.Invert;
            double frac = Math.Clamp(Options.DotFraction, QrCodeOptions.MinDotFraction, QrCodeOptions.MaxDotFraction);
            double coreHalf = Math.Max(1, Math.Round(L.ModulePixels * frac)) / (2.0 * L.ModulePixels);
            var cam = _cam;
            var depth = _depth;
            int n = L.Modules;
            // Which module each pixel shows (-1 none, -2 quiet zone).
            var owner = new int[w * h];

            // 1. Band clamp. Data modules may widen to the Dots soft band at an edge,
            //    so wall shading and cast shadows survive; function modules and the
            //    quiet zone only to the edge band.
            Parallel.For(0, h, py =>
            {
                for (int px = 0; px < w; px++)
                {
                    int i = py * w + px;
                    owner[i] = -1;
                    double d = depth[i];
                    if (!(d > 0) || d > 1e6) continue;   // sky / miss
                    var (x, _, z) = HeightfieldRaymarch2D.HitPoint(in cam, w, h, aspect, px, py, d);
                    double ax = (x / aspect + 0.5) * W, ay = (z + 0.5) * H;
                    if (ModuleAt(ax, ay, out int mx, out int my, out double lx, out double ly))
                    {
                        owner[i] = my * n + mx;
                        bool dark = Code.Dark[my, mx] ^ invert;
                        bool func = Code.IsFunction[my, mx];
                        // Band from the distance to the other shade, as the Bevel style:
                        // core band from (0.5 - dot/2) modules in, widening to the edge.
                        double t = Smooth(Math.Clamp(DistanceToOtherShade(mx, my, lx, ly, dark) / (0.5 - coreHalf), 0, 1));
                        int darkEdge = func ? QrCompositor.DarkEdgeMax : QrCompositor.DarkSoftMax;
                        int lightEdge = func ? QrCompositor.LightEdgeMin : QrCompositor.LightSoftMin;
                        buf[i] = dark
                            ? QrCompositor.ClampLuma(buf[i], 0, Lerp(darkEdge, QrCompositor.DarkCoreMax, t))
                            : QrCompositor.ClampLuma(buf[i], Lerp(lightEdge, QrCompositor.LightCoreMin, t), 255);
                    }
                    else if (InQuiet(ax, ay))
                    {
                        owner[i] = -2;
                        buf[i] = invert
                            ? QrCompositor.ClampLuma(buf[i], 0, QrCompositor.DarkEdgeMax)
                            : QrCompositor.ClampLuma(buf[i], QrCompositor.LightEdgeMin, 255);
                    }
                }
            });

            // 2. Hold every module's on-screen MEAN on its side at Steer's worst case,
            //    as the Bevel style does: the small / blur passes average a module, and
            //    a long shadow or AO can cover most of a light one. A uniform remap
            //    toward the module's end of the range keeps the relative lighting.
            var sum = new long[n * n];
            var cnt = new int[n * n];
            for (int i = 0; i < owner.Length; i++)
            {
                int o = owner[i];
                if (o < 0) continue;
                sum[o] += QrVerifier.LumaOf(buf[i]);
                cnt[o]++;
            }
            var lo = new int[n * n];
            var hi = new int[n * n];
            bool any = false;
            for (int o = 0; o < n * n; o++)
            {
                hi[o] = 255;
                if (cnt[o] == 0) continue;
                double mean = (double)sum[o] / cnt[o];
                bool dark = Code.Dark[o / n, o % n] ^ invert;
                if (!dark && mean < QrCompositor.LightEdgeMin)
                { lo[o] = (int)Math.Ceiling(255 * (QrCompositor.LightEdgeMin - mean) / (255 - mean)); any = true; }
                else if (dark && mean > QrCompositor.DarkEdgeMax)
                { hi[o] = (int)Math.Floor(255 * QrCompositor.DarkEdgeMax / mean); any = true; }
            }
            if (any)
                Parallel.For(0, h, py =>
                {
                    for (int px = 0; px < w; px++)
                    {
                        int i = py * w + px, o = owner[i];
                        if (o >= 0 && (lo[o] > 0 || hi[o] < 255)) buf[i] = QrCompositor.Steer(buf[i], lo[o], hi[o]);
                    }
                });

            return new QrApplyResult(Code, ScreenLayout(w, h), Flat.Shape);
        }

        /// <summary>How much the camera must zoom out (factor &lt; 1) for the code's
        /// quiet zone to sit inside the frame with a small margin; 1 = it fits.
        /// Measured on the projected outer corners of the quiet zone at plinth level.</summary>
        public double FrameFit(int w, int h)
        {
            if (_depth == null) return 1.0;
            var L = Layout;
            double aspect = (double)w / h;
            double q = L.QuietPixels;
            double margin = 0.02 * Math.Min(w, h);
            double fit = 1.0;
            foreach (var (cx, cy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                double ax = L.OriginX - q + cx * (L.SymbolPixels + 2 * q);
                double ay = L.OriginY - q + cy * (L.SymbolPixels + 2 * q);
                double wx = (ax / W - 0.5) * aspect, wz = ay / H - 0.5;
                var (sx, sy) = Project(in _cam, w, h, aspect, wx, _plinthWorldY, wz);
                if (double.IsNaN(sx)) return 0.5;
                double ox = Math.Abs(sx - w * 0.5), oy = Math.Abs(sy - h * 0.5);
                if (ox > 1e-9) fit = Math.Min(fit, (w * 0.5 - margin) / ox);
                if (oy > 1e-9) fit = Math.Min(fit, (h * 0.5 - margin) / oy);
            }
            return fit >= 1.0 ? 1.0 : Math.Max(0.2, fit * 0.97);
        }

        /// <summary>The symbol as it lands on screen: origin / module size from the
        /// projected symbol corners (the bounding box; under tilt the code is a
        /// trapezoid, which the decoder handles).</summary>
        private QrLayout ScreenLayout(int w, int h)
        {
            var L = Layout;
            double aspect = (double)w / h;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var (cx, cy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                double ax = L.OriginX + cx * L.SymbolPixels, ay = L.OriginY + cy * L.SymbolPixels;
                double wx = (ax / W - 0.5) * aspect, wz = ay / H - 0.5;
                // Corners on the floor plane: close enough for a size estimate.
                var (sx, sy) = Project(in _cam, w, h, aspect, wx, 0.0, wz);
                minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
                minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
            }
            double side = Math.Max(1, Math.Min(maxX - minX, maxY - minY));
            int mp = Math.Max(1, (int)Math.Round(side / L.Modules));
            return new QrLayout((int)Math.Round(minX), (int)Math.Round(minY), mp, L.Modules);
        }

        /// <summary>World point → pixel-centre coordinates (inverse of the trace's
        /// ray generation; pinhole or orthographic).</summary>
        public static (double X, double Y) Project(in HeightfieldRaymarch2D.ReliefCamera cam, int w, int h, double aspect,
            double x, double y, double z)
        {
            double dx = x - cam.CamX, dy = y - cam.CamY, dz = z - cam.CamZ;
            double a = dx * cam.RX + dz * cam.RZ;
            double b = dx * cam.UX + dy * cam.UY + dz * cam.UZ;
            double c = dx * cam.FX + dy * cam.FY + dz * cam.FZ;
            double ndcx, ndcy;
            if (cam.Ortho)
            {
                ndcx = a / (aspect * cam.OrthoHalfV);
                ndcy = b / cam.OrthoHalfV;
            }
            else
            {
                if (c <= 1e-12) return (double.NaN, double.NaN);
                ndcx = a / c / (aspect * cam.TanHalf);
                ndcy = b / c / cam.TanHalf;
            }
            return ((ndcx + 1) * 0.5 * w, (1 - ndcy) * 0.5 * h);
        }

        private bool ModuleAt(double ax, double ay, out int mx, out int my, out double lx, out double ly)
        {
            var L = Layout;
            double dx = (ax - L.OriginX) / L.ModulePixels, dy = (ay - L.OriginY) / L.ModulePixels;
            mx = (int)Math.Floor(dx); my = (int)Math.Floor(dy);
            lx = dx - mx; ly = dy - my;
            return mx >= 0 && my >= 0 && mx < L.Modules && my < L.Modules;
        }

        private bool InQuiet(double ax, double ay)
        {
            var L = Layout;
            double q = L.QuietPixels;
            return ax >= L.OriginX - q && ax < L.OriginX + L.SymbolPixels + q
                && ay >= L.OriginY - q && ay < L.OriginY + L.SymbolPixels + q;
        }

        private static double Smooth(double t) => t * t * (3 - 2 * t);

        private static int Lerp(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

        /// <summary>Distance, in modules, from local point (lx, ly) of module
        /// (mx, my) to the nearest neighbouring module of the other shade (outside
        /// the symbol counts as light: the quiet zone). Capped at one module.</summary>
        private double DistanceToOtherShade(int mx, int my, double lx, double ly, bool dark)
        {
            int n = Layout.Modules;
            bool invert = Options.Invert;
            double best = 1.0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = mx + dx, ny = my + dy;
                    bool nd = nx >= 0 && ny >= 0 && nx < n && ny < n ? Code.Dark[ny, nx] ^ invert : invert;
                    if (nd == dark) continue;
                    // Distance from (lx, ly) to the neighbour cell [dx, dx+1] x [dy, dy+1].
                    double ex = dx < 0 ? lx : dx > 0 ? 1 - lx : 0;
                    double ey = dy < 0 ? ly : dy > 0 ? 1 - ly : 0;
                    double d = Math.Sqrt(ex * ex + ey * ey);
                    if (d < best) best = d;
                }
            return best;
        }
    }
}
