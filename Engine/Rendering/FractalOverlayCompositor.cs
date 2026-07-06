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
// Phase X.A / Slice A.4: ported off GDI+ (Graphics / Font / Pen / SolidBrush)
// onto SkiaSharp. SKBitmap.InstallPixels wraps the pinned BGRA buffer; SKCanvas
// draws onto the pixels in place. SKFont + SKPaint instances are cached on
// the compositor so per-frame allocation stays small (one SKPaint per stroke /
// fill colour change). The compositor itself is single-threaded — FractalRenderHost
// only calls it from the calculator continuation, behind the same _d3dGate lock.

using System;
using System.Drawing; // Color, RectangleF — System.Drawing.Primitives (portable)
using System.Globalization;
using System.Runtime.InteropServices;

using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.ViewState;

using SkiaSharp;

namespace FracturingFog.Rendering
{
    /// <summary>
    /// CPU-side overlay (grid + watermark) compositor. Lives in the engine
    /// assembly alongside FractalRenderHost; runs synchronously on whatever
    /// thread the host invokes it from. Not thread-safe.
    /// </summary>
    internal sealed class FractalOverlayCompositor
    {
        // Cached fonts. Created lazily on first use; reused for every frame.
        // SKFont owns an SKTypeface — both stay alive for the lifetime of the
        // compositor instance. Family-name lookups fall back via the platform
        // SKFontManager when "Courier New" / "Arial" aren't installed (Linux,
        // macOS — Skia maps to DejaVu Sans Mono / Helvetica respectively).

        private readonly SKFont _labelFont = MakeFont("Courier New", 9f,  SKFontStyle.Normal);
        private readonly SKFont _zeroFont  = MakeFont("Courier New", 11f, SKFontStyle.Bold);
        private readonly SKFont _mainFont  = MakeFont("Arial",       14f, SKFontStyle.Bold);
        private readonly SKFont _subFont   = MakeFont("Arial",        9f, SKFontStyle.Normal);

        private readonly SKFont _hudHeader = MakeFont("Courier New", 10f, SKFontStyle.Bold);
        private readonly SKFont _hudBody   = MakeFont("Courier New",  9f, SKFontStyle.Normal);

        private static SKFont MakeFont(string family, float sizePx, SKFontStyle style)
        {
            var tf = SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
            var f = new SKFont(tf, sizePx) { Edging = SKFontEdging.SubpixelAntialias };
            return f;
        }

        // Skia's DrawText positions at the baseline; GDI+ DrawString positions
        // at the top-left. The compositor's existing math computes top-left
        // y-coordinates, so we shift every text draw by (-ascent) to match.
        private static float Baseline(SKFont f) => -f.Metrics.Ascent;

        // Total line height (top-to-top). Matches GDI+ Font.GetHeight() within
        // ~1px, which the layout tolerates.
        private static float LineHeight(SKFont f) => f.Metrics.Descent - f.Metrics.Ascent;

        private static SKColor ToSk(Color c) => new SKColor(c.R, c.G, c.B, c.A);

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

            DrawOnto(bgra, width, height, canvas =>
            {
                if (showGrid && state != null)
                    DrawGrid(canvas, width, height, state, ink, halo);

                if (showWatermark)
                    DrawWatermark(canvas, width, height,
                        regionName, themeName, programName, programVersion,
                        activeWatermark, ink, halo);

                if (selectionRect is { } r && r.W > 0 && r.H > 0)
                    DrawSelectionRect(canvas, width, height, r.X, r.Y, r.W, r.H, ink, halo);
            });
        }

        // Pin the BGRA buffer, wrap it as an SKBitmap, hand a canvas to the
        // caller. BGRA8888 + Premul matches the renderer's upload format so
        // no swizzle / unpremul conversion is needed.
        private static void DrawOnto(uint[] bgra, int width, int height, Action<SKCanvas> draw)
        {
            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var bmp = new SKBitmap();
                bmp.InstallPixels(info, ptr, info.RowBytes);
                using var canvas = new SKCanvas(bmp);
                draw(canvas);
                canvas.Flush();
            }
            finally
            {
                handle.Free();
            }
        }

        // ── Grid ──────────────────────────────────────────────────────────

        private void DrawGrid(SKCanvas canvas, int w, int h, FractalViewState s, Color ink, Color halo)
        {
            double cx = s.CenterX, cy = s.CenterY, zoom = s.Zoom;
            if (zoom <= 0 || double.IsNaN(zoom) || double.IsInfinity(zoom)) return;

            double scale = 3.5 / (Math.Max(w, h) * zoom);
            double xMin = cx - w * scale * 0.5, xMax = cx + w * scale * 0.5;
            double yMin = cy - h * scale * 0.5, yMax = cy + h * scale * 0.5;

            using var gridPen  = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.0f, IsAntialias = true, Color = ToSk(Color.FromArgb(140, ink)) };
            using var axisPen  = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true, Color = ToSk(Color.FromArgb(210, ink)) };
            using var lblBrush = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = ToSk(Color.FromArgb(220, ink)) };
            using var shdBrush = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = ToSk(halo) };

            double gridStep = NiceStep((xMax - xMin) / 7.0);
            if (gridStep <= 0) return;

            float lblHeight = LineHeight(_labelFont);
            float lblBaseline = Baseline(_labelFont);

            // Vertical lines + their x-axis labels along the bottom.
            for (double wx = Math.Ceiling(xMin / gridStep) * gridStep;
                 wx <= xMax + gridStep * 0.01; wx += gridStep)
            {
                double px = (wx - cx) / scale + w * 0.5;
                if (px < 0 || px > w) continue;
                bool isAxis = Math.Abs(wx) < gridStep * 0.01;
                canvas.DrawLine((float)px, 0, (float)px, h, isAxis ? axisPen : gridPen);

                string lbl = FormatCoord(wx);
                float lblW = _labelFont.MeasureText(lbl);
                float lx = (float)px - lblW * 0.5f;
                float ly = h - lblHeight - 2;
                if (ly < 0) ly = 2;
                canvas.DrawText(lbl, lx + 1, ly + 1 + lblBaseline, _labelFont, shdBrush);
                canvas.DrawText(lbl, lx,     ly     + lblBaseline, _labelFont, lblBrush);
            }

            // Horizontal lines + i-suffixed labels along the left edge.
            for (double wy = Math.Ceiling(yMin / gridStep) * gridStep;
                 wy <= yMax + gridStep * 0.01; wy += gridStep)
            {
                double py = -(wy - cy) / scale + h * 0.5;
                if (py < 0 || py > h) continue;
                bool isAxis = Math.Abs(wy) < gridStep * 0.01;
                canvas.DrawLine(0, (float)py, w, (float)py, isAxis ? axisPen : gridPen);
                if (isAxis) continue;

                string lbl = FormatCoord(wy) + "i";
                float top = (float)py - lblHeight * 0.5f;
                canvas.DrawText(lbl, 4, top + 1 + lblBaseline, _labelFont, shdBrush);
                canvas.DrawText(lbl, 3, top     + lblBaseline, _labelFont, lblBrush);
            }

            // Origin marker.
            double ox = (0 - cx) / scale + w * 0.5;
            double oy = -(0 - cy) / scale + h * 0.5;
            if (ox >= 0 && ox <= w && oy >= 0 && oy <= h)
            {
                float zb = Baseline(_zeroFont);
                canvas.DrawText("0", (float)ox + 3, (float)oy + 3 + zb, _zeroFont, shdBrush);
                canvas.DrawText("0", (float)ox + 2, (float)oy + 2 + zb, _zeroFont, lblBrush);
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

        private static void DrawSelectionRect(SKCanvas canvas, int w, int h,
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

            var rect = new SKRect(x0, y0, x0 + clW, y0 + clH);

            // Halo (outset) then ink — keeps the outline legible against
            // both bright and dark fractal regions.
            using var haloPen = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 3.0f, IsAntialias = true, Color = ToSk(halo) };
            using var inkPen  = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, IsAntialias = true, Color = ToSk(Color.FromArgb(230, ink)) };
            canvas.DrawRect(rect, haloPen);
            canvas.DrawRect(rect, inkPen);

            // Faint interior tint so the selected area reads as "selected"
            // rather than just "outlined". 40-alpha keeps the fractal visible.
            using var fillBrush = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false, Color = ToSk(Color.FromArgb(40, ink)) };
            canvas.DrawRect(rect, fillBrush);
        }

        // ── Watermark ─────────────────────────────────────────────────────

        private void DrawWatermark(SKCanvas canvas, int w, int h,
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
            using var mainBrush = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = ToSk(Color.FromArgb(wm.IsCustom ? 255 : 220, fill)) };
            using var subBrush  = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = ToSk(Color.FromArgb(wm.IsCustom ? 230 : 180, fill)) };
            Color haloColor = wm.HighlightColor != null
                ? Color.FromArgb(wm.HighlightColor.A, wm.HighlightColor.R, wm.HighlightColor.G, wm.HighlightColor.B)
                : halo;
            using var shdBrush = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = ToSk(haloColor) };

            float topW_f = string.IsNullOrEmpty(wm.TopText) ? 0f : _mainFont.MeasureText(wm.TopText);
            float subW_f = string.IsNullOrEmpty(wm.SubText) ? 0f : _subFont.MeasureText(wm.SubText);
            float topH_f = string.IsNullOrEmpty(wm.TopText) ? 0f : LineHeight(_mainFont);
            float subH_f = string.IsNullOrEmpty(wm.SubText) ? 0f : LineHeight(_subFont);

            int topW = (int)Math.Ceiling(topW_f);
            int topH = (int)Math.Ceiling(topH_f);
            int subW = (int)Math.Ceiling(subW_f);
            int subH = (int)Math.Ceiling(subH_f);

            const int edgePad = 6;
            var (bx, by, bw, bh) = WatermarkResolver.ComputeBlockBounds(
                wm, w, h, topW, topH, subW, subH, edgePad);

            if (wm.BackgroundColor != null)
            {
                var bg = Color.FromArgb(wm.BackgroundColor.A,
                    wm.BackgroundColor.R, wm.BackgroundColor.G, wm.BackgroundColor.B);
                const int bgPad = 4;
                using var bgBrush = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false, Color = ToSk(bg) };
                canvas.DrawRect(bx - bgPad, by - bgPad, bw + bgPad * 2, bh + bgPad * 2, bgBrush);
            }

            int topX = WatermarkResolver.AlignLineX(bx, bw, topW, wm.Justify);
            int subX = WatermarkResolver.AlignLineX(bx, bw, subW, wm.Justify);

            if (!string.IsNullOrEmpty(wm.TopText))
            {
                float yb = Baseline(_mainFont);
                canvas.DrawText(wm.TopText, topX + 1, by + 1 + yb, _mainFont, shdBrush);
                canvas.DrawText(wm.TopText, topX,     by     + yb, _mainFont, mainBrush);
            }
            if (!string.IsNullOrEmpty(wm.SubText))
            {
                int subY = by + topH;
                float yb = Baseline(_subFont);
                canvas.DrawText(wm.SubText, subX + 1, subY + 1 + yb, _subFont, shdBrush);
                canvas.DrawText(wm.SubText, subX,     subY     + yb, _subFont, subBrush);
            }
        }

        // ── Perf HUD ──────────────────────────────────────────────────────
        //
        // Top-left diagnostic block. Drawn on top of the grid + watermark so
        // it stays readable on dense regions. Translucent black background
        // for legibility against any palette.

        /// <summary>
        /// Composite the perf HUD (phase timings + HW summary) into a BGRA
        /// buffer. Standalone of <see cref="Composite"/> so the HUD layer is
        /// independent of the grid/watermark toggles — host can call only
        /// this when the user has the HUD on without the other overlays.
        /// </summary>
        public void CompositePerfHud(
            uint[] bgra, int width, int height,
            PerfSnapshot snap, string hwSummary,
            int frameW, int frameH, int maxIter, string precisionLabel,
            System.Collections.Generic.IReadOnlyList<string>? contextLines = null,
            string? warningLine = null)
        {
            if (bgra == null || bgra.Length < width * height) return;
            if (width <= 1 || height <= 1) return;

            DrawOnto(bgra, width, height, canvas =>
            {
                // 12 lines. Sized for monospace at 9pt → roughly 14 px per
                // line including the header at 10pt bold.
                // Phase 1.b: optional GPU split row appears only when the
                // kernel has run since the last Reset (GpuSampleCount > 0).
                bool hasGpu = snap.GpuSampleCount > 0;
                string gpuRow = hasGpu
                    ? $"  gpu  dis {snap.GpuDispatchMs,5:F1}  rb {snap.GpuReadbackMs,5:F1} ms"
                    : "";
                // Wave 0.6 — per-stage post-FX microbar rows. Built once and
                // spliced into either variant below; empty when no stage has
                // fired since the last Reset.
                var stageLines = BuildStageLines(in snap);

                var coreLines = new System.Collections.Generic.List<string>(16)
                {
                    "PERF HUD",
                    $"frame  {snap.FrameMs,6:F1} ms  ({snap.Fps,5:F1} fps)",
                    $"  min  {snap.FrameMin,6:F1}    max  {snap.FrameMax,6:F1}",
                    $"calc   {snap.CalcMs,6:F1} ms  ({Pct(snap.CalcMs, snap.FrameMs)})",
                };
                if (hasGpu) coreLines.Add(gpuRow);
                coreLines.Add($"upload {snap.UploadMs,6:F1} ms  ({Pct(snap.UploadMs, snap.FrameMs)})");
                coreLines.Add($"presnt {snap.PresentMs,6:F1} ms  ({Pct(snap.PresentMs, snap.FrameMs)})");
                if (stageLines.Count > 0)
                {
                    coreLines.Add("post-fx:");
                    coreLines.AddRange(stageLines);
                }
                coreLines.Add($"GC g0 {snap.Gen0PerSec,5:F2}/s  g1 {snap.Gen1PerSec,5:F2}/s  g2 {snap.Gen2PerSec,5:F2}/s");
                coreLines.Add($"samples {snap.SampleCount}");
                coreLines.Add("");
                coreLines.Add($"frame  {frameW}x{frameH}  iter {maxIter}  {precisionLabel}");
                coreLines.Add(hwSummary);

                // Render-context block (host-built): centre, zoom, ref-orbit,
                // detail-depth estimate, active toggles. Helps diagnose deep-zoom
                // behaviour without cluttering the (deliberately simple) status bar.
                if (contextLines != null && contextLines.Count > 0)
                {
                    coreLines.Add("");
                    coreLines.Add("RENDER CONTEXT");
                    foreach (var cl in contextLines) coreLines.Add(cl);
                }

                // Yellow (#FFCC00, colour-blind-safe) warning line, drawn last.
                int warnIndex = -1;
                if (!string.IsNullOrEmpty(warningLine))
                {
                    coreLines.Add("");
                    warnIndex = coreLines.Count;
                    coreLines.Add(warningLine!);
                }
                string[] lines = coreLines.ToArray();

                float maxW = 0;
                float lineH = LineHeight(_hudBody);
                float headerH = LineHeight(_hudHeader);
                foreach (var ln in lines)
                {
                    if (string.IsNullOrEmpty(ln)) continue;
                    float lw = _hudBody.MeasureText(ln);
                    if (lw > maxW) maxW = lw;
                }
                float hdrW = _hudHeader.MeasureText(lines[0]);
                if (hdrW > maxW) maxW = hdrW;

                const int pad = 6;
                int x0 = 8;
                int y0 = 8;
                int boxW = (int)Math.Ceiling(maxW) + pad * 2;
                int boxH = (int)Math.Ceiling(headerH + lineH * (lines.Length - 1)) + pad * 2;

                using var bg = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false, Color = new SKColor(0, 0, 0, 170) };
                using var bord = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true, Color = new SKColor(80, 200, 255, 180) };
                using var headBrush = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(120, 220, 255, 255) };
                using var bodyBrush = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(230, 230, 230, 245) };
                using var warnBrush = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(255, 204, 0, 255) };  // #FFCC00
                using var shadowBrush = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0, 0, 0, 160) };

                canvas.DrawRect(x0, y0, boxW, boxH, bg);
                canvas.DrawRect(x0, y0, boxW, boxH, bord);

                float hdrBase = Baseline(_hudHeader);
                float bodyBase = Baseline(_hudBody);

                float ty = y0 + pad;
                // Header line
                canvas.DrawText(lines[0], x0 + pad + 1, ty + 1 + hdrBase, _hudHeader, shadowBrush);
                canvas.DrawText(lines[0], x0 + pad,     ty     + hdrBase, _hudHeader, headBrush);
                ty += headerH;
                // Body lines
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Length > 0)
                    {
                        var brush = i == warnIndex ? warnBrush : bodyBrush;
                        canvas.DrawText(lines[i], x0 + pad + 1, ty + 1 + bodyBase, _hudBody, shadowBrush);
                        canvas.DrawText(lines[i], x0 + pad,     ty     + bodyBase, _hudBody, brush);
                    }
                    ty += lineH;
                }
            });
        }

        // Wave 0.6 — per-stage post-FX micro-rows. Skips stages that never
        // fired (StageCounts[i] == 0). Each row: "  ssao   1.4 ms ▌▌▌▌▌▌"
        // where the bar length is proportional to the stage's share of the
        // sum of all active stages. Bar width capped at 12 cells so the HUD
        // box doesn't widen on heavy frames.
        private static System.Collections.Generic.List<string> BuildStageLines(in PerfSnapshot snap)
        {
            var lines = new System.Collections.Generic.List<string>(6);
            if (snap.StageMs is null || snap.StageCounts is null) return lines;
            double total = 0;
            for (int i = 0; i < snap.StageMs.Length; i++)
                if (snap.StageCounts[i] > 0) total += snap.StageMs[i];
            if (total <= 0) return lines;

            string[] names = { "ssao  ", "bloom ", "dof   ", "edge  ", "lens  ", "vol   " };
            const int BAR_MAX = 12;
            for (int i = 0; i < snap.StageMs.Length && i < names.Length; i++)
            {
                if (snap.StageCounts[i] == 0) continue;
                double ms = snap.StageMs[i];
                int barLen = (int)Math.Round(BAR_MAX * (ms / total));
                if (barLen < 1 && ms > 0) barLen = 1;
                string bar = new string('█', barLen).PadRight(BAR_MAX);
                lines.Add($"  {names[i]}{ms,5:F1} ms {bar}");
            }
            return lines;
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
