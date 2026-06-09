// Rendering/FractalOverlayCompositor.cs
//
// CPU-side compositing of the Cartesian grid + region/theme watermark on top
// of a BGRA32 pixel buffer (the same buffer FractalRenderHost.UploadProcessedBuffer
// hands to IFractalRenderer.UpdateTexture). The Avalonia shell needs the
// overlay blended into the texture because GpuSurfaceControl is a
// NativeControlHost wrapping a real Win32 HWND — on Windows the OS composites
// the native HWND on top of every Avalonia control regardless of XAML
// Z-order, so an Avalonia.Media overlay above the surface is invisible.
//
// Uses System.Drawing.Graphics over a pinned in-memory Bitmap; Pen / Brush /
// Font instances are cached on the compositor so per-frame allocation stays
// small. The compositor itself is single-threaded — FractalRenderHost only
// calls it from the calculator continuation, behind the same _d3dGate lock.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.ViewState;

namespace FracturingFog.Rendering
{
    /// <summary>
    /// CPU-side overlay (grid + watermark) compositor. Lives in the main
    /// project alongside FractalRenderHost; runs synchronously on whatever
    /// thread the host invokes it from. Not thread-safe.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class FractalOverlayCompositor
    {
        // Cached drawing resources. Created lazily on first use; reused for
        // every frame. None are disposed until the host shuts down — the
        // OS GDI handles cap is high enough that a handful of pens + brushes
        // is comfortably below any realistic limit.

        private readonly Font _labelFont = new(new FontFamily(GenericFontFamilies.Monospace), 9f, FontStyle.Regular);
        private readonly Font _zeroFont  = new(new FontFamily(GenericFontFamilies.Monospace), 11f, FontStyle.Bold);
        private readonly Font _mainFont  = new(new FontFamily(GenericFontFamilies.SansSerif), 14f, FontStyle.Bold);
        private readonly Font _subFont   = new(new FontFamily(GenericFontFamilies.SansSerif), 9f, FontStyle.Regular);

        /// <summary>
        /// Blend grid + watermark into <paramref name="bgra"/>. <paramref name="bgra"/>
        /// is assumed to be a tightly-packed BGRA buffer of dimensions
        /// <paramref name="width"/> × <paramref name="height"/>. Caller is the
        /// owner — buffer must not be modified concurrently.
        /// </summary>
        public void Composite(
            uint[] bgra,
            int width, int height,
            FractalViewState? state,
            bool showGrid,
            bool showWatermark,
            byte contrastLuma,
            string? regionName,
            string? themeName,
            string? programName,
            string? programVersion,
            WatermarkDef? activeWatermark,
            (int X, int Y, int W, int H)? selectionRect = null)
        {
            if (bgra == null || bgra.Length < width * height) return;
            if (width <= 1 || height <= 1) return;
            if (!showGrid && !showWatermark && selectionRect == null) return;

            // Pick contrast colour from pre-sampled luma. Dark image → white
            // ink + black halo; light image → near-black ink + white halo.
            bool darkBg = contrastLuma < 128;
            Color ink    = darkBg ? Color.White : Color.FromArgb(20, 20, 20);
            Color halo   = darkBg ? Color.FromArgb(120, 0, 0, 0)
                                  : Color.FromArgb(120, 255, 255, 255);

            // Wrap the pinned BGRA buffer in a Bitmap so GDI+ can draw onto
            // it directly. Format32bppArgb maps BGRA→ARGB byte-identically on
            // little-endian, which all our targets are.
            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                using var bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, ptr);
                using var g = Graphics.FromImage(bmp);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;

                if (showGrid && state != null)
                    DrawGrid(g, width, height, state, ink, halo);

                if (showWatermark)
                    DrawWatermark(g, width, height,
                        regionName, themeName, programName, programVersion,
                        activeWatermark, ink, halo);

                if (selectionRect is { } r && r.W > 0 && r.H > 0)
                    DrawSelectionRect(g, width, height, r.X, r.Y, r.W, r.H, ink, halo);

                g.Flush();
            }
            finally
            {
                handle.Free();
            }
        }

        // ── Grid ──────────────────────────────────────────────────────────

        private void DrawGrid(Graphics g, int w, int h, FractalViewState s, Color ink, Color halo)
        {
            double cx = s.CenterX, cy = s.CenterY, zoom = s.Zoom;
            if (zoom <= 0 || double.IsNaN(zoom) || double.IsInfinity(zoom)) return;

            double scale = 3.5 / (Math.Max(w, h) * zoom);
            double xMin = cx - w * scale * 0.5, xMax = cx + w * scale * 0.5;
            double yMin = cy - h * scale * 0.5, yMax = cy + h * scale * 0.5;

            using var gridPen  = new Pen(Color.FromArgb(140, ink), 1.0f);
            using var axisPen  = new Pen(Color.FromArgb(210, ink), 1.6f);
            using var lblBrush = new SolidBrush(Color.FromArgb(220, ink));
            using var shdBrush = new SolidBrush(halo);

            double gridStep = NiceStep((xMax - xMin) / 7.0);
            if (gridStep <= 0) return;

            // Vertical lines + their x-axis labels along the bottom.
            for (double wx = Math.Ceiling(xMin / gridStep) * gridStep;
                 wx <= xMax + gridStep * 0.01; wx += gridStep)
            {
                double px = (wx - cx) / scale + w * 0.5;
                if (px < 0 || px > w) continue;
                bool isAxis = Math.Abs(wx) < gridStep * 0.01;
                g.DrawLine(isAxis ? axisPen : gridPen, (float)px, 0, (float)px, h);

                string lbl = FormatCoord(wx);
                var sz = g.MeasureString(lbl, _labelFont);
                float lx = (float)px - sz.Width * 0.5f;
                float ly = h - sz.Height - 2;
                if (ly < 0) ly = 2;
                g.DrawString(lbl, _labelFont, shdBrush, lx + 1, ly + 1);
                g.DrawString(lbl, _labelFont, lblBrush, lx, ly);
            }

            // Horizontal lines + i-suffixed labels along the left edge.
            for (double wy = Math.Ceiling(yMin / gridStep) * gridStep;
                 wy <= yMax + gridStep * 0.01; wy += gridStep)
            {
                double py = -(wy - cy) / scale + h * 0.5;
                if (py < 0 || py > h) continue;
                bool isAxis = Math.Abs(wy) < gridStep * 0.01;
                g.DrawLine(isAxis ? axisPen : gridPen, 0, (float)py, w, (float)py);
                if (isAxis) continue;

                string lbl = FormatCoord(wy) + "i";
                var sz = g.MeasureString(lbl, _labelFont);
                g.DrawString(lbl, _labelFont, shdBrush, 4, (float)py - sz.Height * 0.5f + 1);
                g.DrawString(lbl, _labelFont, lblBrush, 3, (float)py - sz.Height * 0.5f);
            }

            // Origin marker.
            double ox = (0 - cx) / scale + w * 0.5;
            double oy = -(0 - cy) / scale + h * 0.5;
            if (ox >= 0 && ox <= w && oy >= 0 && oy <= h)
            {
                g.DrawString("0", _zeroFont, shdBrush, (float)ox + 3, (float)oy + 3);
                g.DrawString("0", _zeroFont, lblBrush, (float)ox + 2, (float)oy + 2);
            }
        }

        private static double NiceStep(double raw)
        {
            if (raw <= 0) return 1.0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            double nice = norm <= 1.0 ? 1.0 : norm <= 2.0 ? 2.0 : norm <= 5.0 ? 5.0 : 10.0;
            return nice * mag;
        }

        private static string FormatCoord(double v)
        {
            if (v == 0.0) return "0";
            double abs = Math.Abs(v);
            int mag = (int)Math.Floor(Math.Log10(abs));
            int decimals = Math.Clamp(6 - mag, 0, 15);
            return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        // ── Selection rectangle (right-drag zoom rubber band) ────────────

        private static void DrawSelectionRect(Graphics g, int w, int h,
            int rx, int ry, int rw, int rh, Color ink, Color halo)
        {
            // Clamp to surface so a partly off-screen drag still draws.
            int x0 = Math.Clamp(rx, 0, w - 1);
            int y0 = Math.Clamp(ry, 0, h - 1);
            int x1 = Math.Clamp(rx + rw, 0, w - 1);
            int y1 = Math.Clamp(ry + rh, 0, h - 1);
            int clW = x1 - x0;
            int clH = y1 - y0;
            if (clW <= 0 || clH <= 0) return;

            // Halo (1px outset) then ink — keeps the outline legible against
            // both bright and dark fractal regions.
            using var haloPen = new Pen(halo, 3.0f);
            using var inkPen  = new Pen(Color.FromArgb(230, ink), 1.4f);
            var rect = new RectangleF(x0, y0, clW, clH);
            g.DrawRectangle(haloPen, rect.X, rect.Y, rect.Width, rect.Height);
            g.DrawRectangle(inkPen,  rect.X, rect.Y, rect.Width, rect.Height);

            // Faint interior tint so the selected area reads as "selected"
            // rather than just "outlined". 32-alpha keeps the fractal visible.
            using var fillBrush = new SolidBrush(Color.FromArgb(40, ink));
            g.FillRectangle(fillBrush, rect);
        }

        // ── Watermark ─────────────────────────────────────────────────────

        private void DrawWatermark(Graphics g, int w, int h,
            string? region, string? theme,
            string? programName, string? programVersion,
            WatermarkDef? activeWatermark,
            Color ink, Color halo)
        {
            // Resolve through the shared chain. The shell has already applied
            // precedence and handed us either a fully-realised activeWatermark
            // or null (= default region/theme + auto-contrast). IsCustom is
            // true exactly when activeWatermark is non-null.
            var defaultText = new RgbDef(ink.R, ink.G, ink.B);
            var wm = WatermarkResolver.Resolve(
                activeCustom: activeWatermark,
                regionEmbedded: null,
                overrideRegionWatermark: activeWatermark != null,
                useCustomWatermark: activeWatermark != null,
                regionName: region ?? string.Empty,
                themeName: theme ?? string.Empty,
                programName: programName ?? "Fracturing Fog",
                programVersion: programVersion ?? string.Empty,
                defaultTextColor: defaultText);

            Color fill = Color.FromArgb(wm.TextColor.R, wm.TextColor.G, wm.TextColor.B);
            using var mainBrush = new SolidBrush(Color.FromArgb(wm.IsCustom ? 255 : 220, fill));
            using var subBrush  = new SolidBrush(Color.FromArgb(wm.IsCustom ? 230 : 180, fill));
            Color haloColor = wm.HighlightColor != null
                ? Color.FromArgb(wm.HighlightColor.A, wm.HighlightColor.R, wm.HighlightColor.G, wm.HighlightColor.B)
                : halo;
            using var shdBrush = new SolidBrush(haloColor);

            var topSz = string.IsNullOrEmpty(wm.TopText)
                ? new SizeF(0, 0) : g.MeasureString(wm.TopText, _mainFont);
            var subSz = string.IsNullOrEmpty(wm.SubText)
                ? new SizeF(0, 0) : g.MeasureString(wm.SubText, _subFont);

            int topW = (int)Math.Ceiling(topSz.Width);
            int topH = (int)Math.Ceiling(topSz.Height);
            int subW = (int)Math.Ceiling(subSz.Width);
            int subH = (int)Math.Ceiling(subSz.Height);

            const int edgePad = 6;
            var (bx, by, bw, bh) = WatermarkResolver.ComputeBlockBounds(
                wm, w, h, topW, topH, subW, subH, edgePad);

            if (wm.BackgroundColor != null)
            {
                var bg = Color.FromArgb(wm.BackgroundColor.A,
                    wm.BackgroundColor.R, wm.BackgroundColor.G, wm.BackgroundColor.B);
                const int bgPad = 4;
                using var bgBrush = new SolidBrush(bg);
                g.FillRectangle(bgBrush, bx - bgPad, by - bgPad, bw + bgPad * 2, bh + bgPad * 2);
            }

            int topX = WatermarkResolver.AlignLineX(bx, bw, topW, wm.Justify);
            int subX = WatermarkResolver.AlignLineX(bx, bw, subW, wm.Justify);

            if (!string.IsNullOrEmpty(wm.TopText))
            {
                g.DrawString(wm.TopText, _mainFont, shdBrush, topX + 1, by + 1);
                g.DrawString(wm.TopText, _mainFont, mainBrush, topX, by);
            }
            if (!string.IsNullOrEmpty(wm.SubText))
            {
                int subY = by + topH;
                g.DrawString(wm.SubText, _subFont, shdBrush, subX + 1, subY + 1);
                g.DrawString(wm.SubText, _subFont, subBrush, subX, subY);
            }
        }

        // ── Perf HUD ──────────────────────────────────────────────────────
        //
        // Top-left diagnostic block. Drawn on top of the grid + watermark so
        // it stays readable on dense regions. Translucent black background
        // for legibility against any palette.

        private readonly Font _hudHeader = new(new FontFamily(GenericFontFamilies.Monospace), 10f, FontStyle.Bold);
        private readonly Font _hudBody   = new(new FontFamily(GenericFontFamilies.Monospace), 9f,  FontStyle.Regular);

        /// <summary>
        /// Composite the perf HUD (phase timings + HW summary) into a BGRA
        /// buffer. Standalone of <see cref="Composite"/> so the HUD layer is
        /// independent of the grid/watermark toggles — host can call only
        /// this when the user has the HUD on without the other overlays.
        /// </summary>
        public void CompositePerfHud(
            uint[] bgra, int width, int height,
            PerfSnapshot snap, string hwSummary,
            int frameW, int frameH, int maxIter, string precisionLabel)
        {
            if (bgra == null || bgra.Length < width * height) return;
            if (width <= 1 || height <= 1) return;

            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                using var bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, ptr);
                using var g = Graphics.FromImage(bmp);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;

                // 12 lines. Sized for monospace at 9pt → roughly 14 px per
                // line including the header at 10pt bold.
                string[] lines =
                {
                    "PERF HUD",
                    $"frame  {snap.FrameMs,6:F1} ms  ({snap.Fps,5:F1} fps)",
                    $"  min  {snap.FrameMin,6:F1}    max  {snap.FrameMax,6:F1}",
                    $"calc   {snap.CalcMs,6:F1} ms  ({Pct(snap.CalcMs, snap.FrameMs)})",
                    $"upload {snap.UploadMs,6:F1} ms  ({Pct(snap.UploadMs, snap.FrameMs)})",
                    $"presnt {snap.PresentMs,6:F1} ms  ({Pct(snap.PresentMs, snap.FrameMs)})",
                    $"GC g0 {snap.Gen0PerSec,5:F2}/s  g1 {snap.Gen1PerSec,5:F2}/s  g2 {snap.Gen2PerSec,5:F2}/s",
                    $"samples {snap.SampleCount}",
                    "",
                    $"frame  {frameW}x{frameH}  iter {maxIter}  {precisionLabel}",
                    hwSummary,
                };

                float maxW = 0;
                float lineH = _hudBody.GetHeight(g);
                float headerH = _hudHeader.GetHeight(g);
                foreach (var ln in lines)
                {
                    if (string.IsNullOrEmpty(ln)) continue;
                    var sz = g.MeasureString(ln, _hudBody);
                    if (sz.Width > maxW) maxW = sz.Width;
                }
                var hdrSz = g.MeasureString(lines[0], _hudHeader);
                if (hdrSz.Width > maxW) maxW = hdrSz.Width;

                const int pad = 6;
                int x0 = 8;
                int y0 = 8;
                int boxW = (int)Math.Ceiling(maxW) + pad * 2;
                int boxH = (int)Math.Ceiling(headerH + lineH * (lines.Length - 1)) + pad * 2;

                using var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
                using var bord = new Pen(Color.FromArgb(180, 80, 200, 255), 1f);
                using var headBrush = new SolidBrush(Color.FromArgb(255, 120, 220, 255));
                using var bodyBrush = new SolidBrush(Color.FromArgb(245, 230, 230, 230));
                using var shadowBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0));

                g.FillRectangle(bg, x0, y0, boxW, boxH);
                g.DrawRectangle(bord, x0, y0, boxW, boxH);

                float ty = y0 + pad;
                // Header line
                g.DrawString(lines[0], _hudHeader, shadowBrush, x0 + pad + 1, ty + 1);
                g.DrawString(lines[0], _hudHeader, headBrush, x0 + pad, ty);
                ty += headerH;
                // Body lines
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Length > 0)
                    {
                        g.DrawString(lines[i], _hudBody, shadowBrush, x0 + pad + 1, ty + 1);
                        g.DrawString(lines[i], _hudBody, bodyBrush, x0 + pad, ty);
                    }
                    ty += lineH;
                }

                g.Flush();
            }
            finally
            {
                handle.Free();
            }
        }

        private static string Pct(double part, double whole)
        {
            if (whole <= 0) return "  --%";
            double p = 100.0 * part / whole;
            if (p < 0) p = 0;
            if (p > 999) p = 999;
            return $"{p,4:F0}%";
        }
    }
}
