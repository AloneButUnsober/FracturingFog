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
            string? programVersion)
        {
            if (bgra == null || bgra.Length < width * height) return;
            if (width <= 1 || height <= 1) return;
            if (!showGrid && !showWatermark) return;

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
                        ink, halo);

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

        // ── Watermark ─────────────────────────────────────────────────────

        private void DrawWatermark(Graphics g, int w, int h,
            string? region, string? theme,
            string? programName, string? programVersion,
            Color ink, Color halo)
        {
            string main = "";
            if (!string.IsNullOrEmpty(region)) main = region!;
            if (!string.IsNullOrEmpty(theme))
                main = string.IsNullOrEmpty(main) ? theme! : main + " - " + theme;

            string sub = $"{programName ?? "Fracturing Fog"} v{programVersion ?? "?"} {DateTime.Now.Year}";

            using var mainBrush = new SolidBrush(Color.FromArgb(220, ink));
            using var subBrush  = new SolidBrush(Color.FromArgb(180, ink));
            using var shdBrush  = new SolidBrush(halo);

            var mainSz = g.MeasureString(main, _mainFont);
            var subSz  = g.MeasureString(sub, _subFont);

            float pad = 6;
            float bx = w - Math.Max(mainSz.Width, subSz.Width) - pad;
            float by = h - mainSz.Height - subSz.Height - pad;

            if (!string.IsNullOrEmpty(main))
            {
                g.DrawString(main, _mainFont, shdBrush, bx + 1, by + 1);
                g.DrawString(main, _mainFont, mainBrush, bx, by);
            }
            float subY = by + mainSz.Height;
            float subX = w - subSz.Width - pad;
            g.DrawString(sub, _subFont, shdBrush, subX + 1, subY + 1);
            g.DrawString(sub, _subFont, subBrush, subX, subY);
        }
    }
}
