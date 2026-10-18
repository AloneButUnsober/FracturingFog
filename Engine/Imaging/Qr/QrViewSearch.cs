// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/Qr/QrViewSearch.cs
//
// #1305 / S6 (#1401) - fractal-native QR: instead of only forcing the image
// onto the code, look for the framing whose OWN light/dark pattern already
// agrees with the code, so the compositor flips as few modules as possible
// and more of the fractal survives untouched.
//
// Each candidate view is rendered small through the real poster pipeline
// (theme, lighting and post-FX included - what the export will look like),
// sampled at every data module's centre, and scored by how far the
// compositor would have to move each centre's luma to reach the target shade
// under each of the 8 masks (the mask is free: the decoder reads it from the
// format bits). Measured gains are modest - QR data modules are
// pseudo-random, so no smooth image correlates strongly with them; the best of
// a few hundred view x mask samples sits a few sigma below the mean. Shaping
// the code toward the image (QArt, #1402) is the stronger lever. The search is a seeded random
// beam over centre (DeepComplex, so deep views keep their precision) and zoom.
// The result is plain navigation plus a preferred mask, so it reproduces
// headless without searching again (--x --y --zoom --qr-mask).

using System;
using System.Collections.Generic;
using System.Threading;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;

namespace FracturingFog.Imaging.Qr
{
    /// <summary>A scored candidate under its best mask. <see cref="Cost"/> (what
    /// the search minimises) is the mean luma change, 0..1, the compositor
    /// applies at the data-module centres - how much of the fractal is altered.
    /// <see cref="Mismatch"/> is the share of data modules on the wrong side of
    /// the mean luma (those get flipped outright).</summary>
    public readonly record struct QrViewScore(double Cost, double Mismatch, int Mask)
    {
        /// <summary>Edge energy of the code area (mean |luma step| between
        /// neighbouring probe pixels, 0..1) - how much fractal there is to keep.</summary>
        public double Detail { get; init; }
    }

    public sealed record QrSearchProgress(int Level, int Levels, int Probes, double BestCost);

    public sealed record QrSearchResult(
        ExploreView Start, QrViewScore StartScore,
        ExploreView Best, QrViewScore BestScore,
        int Probes)
    {
        /// <summary>e.g. "change 31.0% -> 27.5%, flipped 49% -> 46% (mask 5, 97 probes)".</summary>
        public string Summary =>
            $"luma change {StartScore.Cost:P1} -> {BestScore.Cost:P1}, modules flipped {StartScore.Mismatch:P0} -> {BestScore.Mismatch:P0} "
            + $"(mask {BestScore.Mask}, {Probes} probes)";
    }

    public static class QrViewSearch
    {
        /// <summary>Search around <paramref name="baseReq"/>'s view for the framing
        /// that best matches <paramref name="qr"/>'s code at the request's output
        /// size. Throws ArgumentException when the code cannot be built or placed.</summary>
        public static QrSearchResult Run(PosterRequest baseReq, QrCodeOptions qr, QrSearchOptions o,
            IProgress<QrSearchProgress>? progress = null, CancellationToken token = default)
        {
            if (QrExport.Preflight(qr, baseReq.Width, baseReq.Height) is { } err) throw new ArgumentException(err, nameof(qr));
            if (o.Validate() is { } bad) throw new ArgumentException(bad, nameof(o));

            var codes = AllMasks(qr);
            var final = QrCompositor.Layout(baseReq.Width, baseReq.Height, codes[0].Size, qr.ModulePixels);
            double scale = (double)o.ProbeModulePixels / final.ModulePixels;
            int pw = Math.Max(codes[0].Size, (int)Math.Round(baseReq.Width * scale));
            int ph = Math.Max(codes[0].Size, (int)Math.Round(baseReq.Height * scale));
            var probeBase = baseReq with
            {
                Width = pw, Height = ph, Rotate = false, Path = "", Qr = null,
                Watermark = "", SubText = "", CustomWatermark = null,
                LetterboxInnerW = 0, LetterboxInnerH = 0, IterationRescue = null,
            };

            int probes = 0;
            QrViewScore Eval(ExploreView v)
            {
                token.ThrowIfCancellationRequested();
                probes++;
                var px = PosterRenderer.RenderDisplayPixels(WithView(probeBase, v), token, out int w, out int h);
                return Score(px, w, h, codes, final, baseReq.Width, baseReq.Height, qr.Invert);
            }
            double minDetail = 0;
            bool Acceptable(QrViewScore s) => s.Detail >= minDetail;

            var start = new ExploreView(CentreOf(baseReq), baseReq.Zoom);
            var startScore = Eval(start);
            minDetail = startScore.Detail * o.MinDetail;
            var best = new List<(ExploreView View, QrViewScore Score)> { (start, startScore) };
            var rng = new Random(o.Seed);
            double zMin = baseReq.Zoom * o.MinZoomFactor, zMax = baseReq.Zoom * o.MaxZoomFactor;
            double logSpread = Math.Log(o.MaxZoomFactor / o.MinZoomFactor);

            double radius = 1.0;   // offsets in view widths; halves every level
            for (int level = 0; level < o.Levels; level++)
            {
                var pool = new List<(ExploreView View, QrViewScore Score)>(best);
                int perParent = Math.Max(1, o.Tries / best.Count);
                foreach (var (parent, _) in best)
                    for (int t = 0; t < perParent; t++)
                    {
                        double z = level == 0
                            ? baseReq.Zoom * o.MinZoomFactor * Math.Exp(rng.NextDouble() * logSpread)
                            : parent.Zoom * Math.Exp((rng.NextDouble() - 0.5) * logSpread * radius * 0.5);
                        z = Math.Clamp(z, zMin, zMax);
                        double extent = AutoExplorer.PlaneExtent / parent.Zoom;
                        double dx = (rng.NextDouble() - 0.5) * extent * radius;
                        double dy = (rng.NextDouble() - 0.5) * extent * radius;
                        var v = new ExploreView(parent.Center.Translate(dx, dy), z);
                        var sc = Eval(v);
                        if (Acceptable(sc)) pool.Add((v, sc));
                    }
                // Stable (ties keep generation order), so a seed always gives one answer.
                best = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Take(
                    System.Linq.Enumerable.OrderBy(pool, c => c.Score.Cost), Math.Max(1, o.Keep)));
                radius *= 0.5;
                progress?.Report(new QrSearchProgress(level + 1, o.Levels, probes, best[0].Score.Cost));
            }
            return new QrSearchResult(start, startScore, best[0].View, best[0].Score, probes);
        }

        /// <summary>The code under each of the 8 masks, index = mask.</summary>
        public static QrCode[] AllMasks(QrCodeOptions qr)
        {
            var codes = new QrCode[8];
            for (int m = 0; m < 8; m++) codes[m] = QrExport.Encode(qr, m);
            return codes;
        }

        /// <summary>Score a rendered probe (straight BGRA, <paramref name="w"/> x
        /// <paramref name="h"/>, the same framing as the final W x H image) against
        /// every mask. Module centres are mapped from the final layout.</summary>
        public static QrViewScore Score(uint[] px, int w, int h, QrCode[] codes, QrLayout final,
            int finalW, int finalH, bool invert)
        {
            var code0 = codes[0];
            int n = code0.Size;
            double sx = (double)w / finalW, sy = (double)h / finalH;
            var luma = new byte[n, n];
            var pix = new uint[n, n];
            var samples = new List<byte>(n * n);
            for (int my = 0; my < n; my++)
                for (int mx = 0; mx < n; mx++)
                {
                    if (code0.IsFunction[my, mx]) continue;
                    int x = Math.Clamp((int)((final.OriginX + (mx + 0.5) * final.ModulePixels) * sx), 0, w - 1);
                    int y = Math.Clamp((int)((final.OriginY + (my + 0.5) * final.ModulePixels) * sy), 0, h - 1);
                    uint p = px[y * w + x];
                    byte l = QrVerifier.LumaOf(p);
                    pix[my, mx] = p;
                    luma[my, mx] = l;
                    samples.Add(l);
                }
            // Mean, not median: on a two-level image the median sits ON a level
            // and "< median" would call nothing dark.
            double threshold = 128;
            if (samples.Count > 0) { long sum = 0; foreach (byte b in samples) sum += b; threshold = (double)sum / samples.Count; }

            // The compositor's luma change at a module centre, per target shade.
            var change = new double[n, n, 2];
            for (int my = 0; my < n; my++)
                for (int mx = 0; mx < n; mx++)
                {
                    if (code0.IsFunction[my, mx]) continue;
                    int l = luma[my, mx];
                    change[my, mx, 0] = Math.Abs(QrVerifier.LumaOf(QrCompositor.Steer(pix[my, mx], dark: false, core: true)) - l) / 255.0;
                    change[my, mx, 1] = Math.Abs(QrVerifier.LumaOf(QrCompositor.Steer(pix[my, mx], dark: true, core: true)) - l) / 255.0;
                }

            var best = new QrViewScore(double.MaxValue, 1, 0);
            for (int m = 0; m < codes.Length; m++)
            {
                var c = codes[m];
                int miss = 0, total = 0;
                double cost = 0;
                for (int my = 0; my < n; my++)
                    for (int mx = 0; mx < n; mx++)
                    {
                        if (c.IsFunction[my, mx]) continue;
                        total++;
                        bool targetDark = c.Dark[my, mx] ^ invert;
                        if ((luma[my, mx] < threshold) != targetDark) miss++;
                        cost += change[my, mx, targetDark ? 1 : 0];
                    }
                if (total == 0) return new QrViewScore(0, 0, m) { Detail = Detail(px, w, h, final, finalW, finalH) };
                cost /= total;
                if (cost < best.Cost) best = new QrViewScore(cost, (double)miss / total, m);
            }
            return best with { Detail = Detail(px, w, h, final, finalW, finalH) };
        }

        /// <summary>Mean absolute luma step (0..1) between horizontal and vertical
        /// neighbours inside the code area of a probe.</summary>
        public static double Detail(uint[] px, int w, int h, QrLayout final, int finalW, int finalH)
        {
            double sx = (double)w / finalW, sy = (double)h / finalH;
            int x0 = Math.Clamp((int)(final.OriginX * sx), 0, w - 1), x1 = Math.Clamp((int)((final.OriginX + final.SymbolPixels) * sx), 1, w);
            int y0 = Math.Clamp((int)(final.OriginY * sy), 0, h - 1), y1 = Math.Clamp((int)((final.OriginY + final.SymbolPixels) * sy), 1, h);
            long sum = 0, n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int l = QrVerifier.LumaOf(px[y * w + x]);
                    if (x + 1 < x1) { sum += Math.Abs(l - QrVerifier.LumaOf(px[y * w + x + 1])); n++; }
                    if (y + 1 < y1) { sum += Math.Abs(l - QrVerifier.LumaOf(px[(y + 1) * w + x])); n++; }
                }
            return n == 0 ? 0 : sum / (255.0 * n);
        }

        public static DeepComplex CentreOf(PosterRequest r) => new(
            new OD(r.CenterX, r.CenterXLo, r.CenterX2, r.CenterX3, r.CenterX4, r.CenterX5, r.CenterX6, r.CenterX7),
            new OD(r.CenterY, r.CenterYLo, r.CenterY2, r.CenterY3, r.CenterY4, r.CenterY5, r.CenterY6, r.CenterY7));

        /// <summary><paramref name="r"/> re-centred (all limbs) and re-zoomed.</summary>
        public static PosterRequest WithView(PosterRequest r, ExploreView v) => r with
        {
            CenterX = v.Center.Re.X0, CenterXLo = v.Center.Re.X1, CenterX2 = v.Center.Re.X2, CenterX3 = v.Center.Re.X3,
            CenterX4 = v.Center.Re.X4, CenterX5 = v.Center.Re.X5, CenterX6 = v.Center.Re.X6, CenterX7 = v.Center.Re.X7,
            CenterY = v.Center.Im.X0, CenterYLo = v.Center.Im.X1, CenterY2 = v.Center.Im.X2, CenterY3 = v.Center.Im.X3,
            CenterY4 = v.Center.Im.X4, CenterY5 = v.Center.Im.X5, CenterY6 = v.Center.Im.X6, CenterY7 = v.Center.Im.X7,
            Zoom = v.Zoom,
        };
    }
}
