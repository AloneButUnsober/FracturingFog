// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace FracturingFog.Views
{

    // ─────────────────────────────────────────────────────────────────────────────
    // DirectX render panel
    // ─────────────────────────────────────────────────────────────────────────────

    internal sealed class RenderPanel : Panel
    {
        public RenderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);
            MouseEnter += (_, _) => Focus();
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x00200000; return cp; }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Cartesian grid helper
    // ─────────────────────────────────────────────────────────────────────────────
    // The previous WS_EX_LAYERED sibling-window approach could not composite over
    // the D3D11 FlipDiscard swap chain on modern Windows.  The grid is now blended
    // directly into the fractal ColorBuffer by MainForm.BlendGridOverlay() before
    // the texture is uploaded to the GPU — see UploadProcessedBuffer().
    //
    // This class is purely a drawing helper: it holds the view-state accessors and
    // exposes DrawGrid(Graphics,w,h) which renders the Cartesian grid into any
    // Graphics context (typically a 32bpp ARGB Bitmap with a transparent background).

    internal sealed class GridOverlayPanel : System.Windows.Forms.Control
    {
        private readonly Func<(double cx, double cy)> _getCenter;
        private readonly Func<double> _getZoom;
        private readonly Func<Size> _getPanelSize;
        private readonly Func<Color> _getSwatchColor;

        public GridOverlayPanel(
            Func<(double, double)> getCenter,
            Func<double> getZoom,
            Func<Size> getPanelSize,
            Func<Color> getSwatchColor)
        {
            _getCenter = getCenter;
            _getZoom = getZoom;
            _getPanelSize = getPanelSize;
            _getSwatchColor = getSwatchColor;
        }

        /// <summary>
        /// Renders the Cartesian grid into <paramref name="g"/> at the given pixel
        /// dimensions.  The caller is responsible for clearing the bitmap to
        /// Transparent before calling this method.
        /// </summary>
        public void DrawGrid(Graphics g, int w, int h)
            => DrawCartesianGrid(g, w, h);

        // ── Drawing implementation ────────────────────────────────────────────────

        private void DrawCartesianGrid(Graphics g, int w, int h)
        {
            var (cx, cy) = _getCenter();
            double zoom = _getZoom();
            double scale = 3.5 / (System.Math.Max(w, h) * zoom);

            double xMin = cx - w * scale * 0.5, xMax = cx + w * scale * 0.5;
            double yMin = cy - h * scale * 0.5, yMax = cy + h * scale * 0.5;

            Color gridColor = ComputeContrastColor(_getSwatchColor());
            using var gridPen = new Pen(Color.FromArgb(160, gridColor), 1.0f);
            using var axisPen = new Pen(Color.FromArgb(210, gridColor), 1.8f);
            using var labelBrush = new SolidBrush(Color.FromArgb(200, gridColor));
            using var shadowBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            using var labelFont = new Font("Consolas", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var zeroFont = new Font("Consolas", 8.5f, FontStyle.Bold, GraphicsUnit.Point);

            double gridStep = NiceStep((xMax - xMin) / 7.0);

            // Vertical lines.
            for (double wx = System.Math.Ceiling(xMin / gridStep) * gridStep;
                 wx <= xMax + gridStep * 0.01; wx += gridStep)
            {
                float px = W2SX(wx, cx, scale, w);
                if (px < 0 || px > w) continue;
                bool isAxis = System.Math.Abs(wx) < gridStep * 0.01;
                g.DrawLine(isAxis ? axisPen : gridPen, px, 0, px, h);
                string lbl = FormatCoord(wx);
                var sz = g.MeasureString(lbl, labelFont);
                float lx = px - sz.Width * 0.5f, ly = h - sz.Height - 2;
                if (ly < 0) ly = 2;
                g.DrawString(lbl, labelFont, shadowBrush, lx + 1, ly + 1);
                g.DrawString(lbl, labelFont, labelBrush, lx, ly);
            }

            // Horizontal lines.
            for (double wy = System.Math.Ceiling(yMin / gridStep) * gridStep;
                 wy <= yMax + gridStep * 0.01; wy += gridStep)
            {
                float py = W2SY(wy, cy, scale, h);
                if (py < 0 || py > h) continue;
                bool isAxis = System.Math.Abs(wy) < gridStep * 0.01;
                g.DrawLine(isAxis ? axisPen : gridPen, 0, py, w, py);
                if (isAxis) continue;
                string lbl = FormatCoord(wy) + "i";
                var sz = g.MeasureString(lbl, labelFont);
                g.DrawString(lbl, labelFont, shadowBrush, 4, py - sz.Height * 0.5f + 1);
                g.DrawString(lbl, labelFont, labelBrush, 3, py - sz.Height * 0.5f);
            }

            // Origin label.
            float ox = W2SX(0, cx, scale, w);
            float oy = W2SY(0, cy, scale, h);
            if (ox >= 0 && ox <= w && oy >= 0 && oy <= h)
            {
                g.DrawString("0", zeroFont, shadowBrush, ox + 3, oy + 3);
                g.DrawString("0", zeroFont, labelBrush, ox + 2, oy + 2);
            }
        }

        private static float W2SX(double wx, double cx, double scale, int w)
            => (float)((wx - cx) / scale + w * 0.5);

        private static float W2SY(double wy, double cy, double scale, int h)
            => (float)(-(wy - cy) / scale + h * 0.5);

        private static double NiceStep(double raw)
        {
            if (raw <= 0) return 1.0;
            double mag = System.Math.Pow(10, System.Math.Floor(System.Math.Log10(raw)));
            double norm = raw / mag;
            double nice = norm <= 1.0 ? 1.0 : norm <= 2.0 ? 2.0 : norm <= 5.0 ? 5.0 : 10.0;
            return nice * mag;
        }

        private static string FormatCoord(double v)
        {
            if (v == 0.0) return "0";
            double abs = System.Math.Abs(v);
            // Always render 7 significant digits so that deep-zoom grid lines
            // show distinct labels even when graduations differ only in the 6th–7th
            // decimal place (e.g. -1.744453 vs -1.744452).
            // "mag" is the order of magnitude of the integer part:
            //   abs = 1.744  → mag = 0  → decimals = 6
            //   abs = 0.022  → mag = -2 → decimals = 8  (clamped to 15)
            int mag = (int)System.Math.Floor(System.Math.Log10(abs));
            int decimals = System.Math.Clamp(6 - mag, 0, 15);
            return v.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static Color ComputeContrastColor(Color swatch)
        {
            float r = swatch.R / 255f, g = swatch.G / 255f, b = swatch.B / 255f;
            float cmax = System.Math.Max(r, System.Math.Max(g, b));
            float cmin = System.Math.Min(r, System.Math.Min(g, b));
            float delta = cmax - cmin;
            float l = (cmax + cmin) * 0.5f;
            float h2 = 0f;
            if (delta > 0.001f)
            {
                if (cmax == r) h2 = ((g - b) / delta) % 6f;
                else if (cmax == g) h2 = (b - r) / delta + 2f;
                else h2 = (r - g) / delta + 4f;
                h2 = (h2 / 6f + 1f) % 1f;
            }
            float s2 = delta < 0.001f ? 0f : delta / (1f - System.Math.Abs(2f * l - 1f));
            float hc = (h2 + 0.5f) % 1f;
            float lc = l < 0.5f
                ? System.Math.Clamp(1f - l * 0.6f, 0.65f, 1.0f)
                : System.Math.Clamp(1f - l * 1.4f, 0.0f, 0.35f);
            float sc = System.Math.Clamp(s2 * 0.5f + 0.5f, 0.5f, 1.0f);
            float cv = (1f - System.Math.Abs(2f * lc - 1f)) * sc;
            float xv = cv * (1f - System.Math.Abs((hc * 6f) % 2f - 1f));
            float m = lc - cv * 0.5f;
            float rr, gg, bb;
            switch ((int)(hc * 6f))
            {
                case 0: rr = cv; gg = xv; bb = 0; break;
                case 1: rr = xv; gg = cv; bb = 0; break;
                case 2: rr = 0; gg = cv; bb = xv; break;
                case 3: rr = 0; gg = xv; bb = cv; break;
                case 4: rr = xv; gg = 0; bb = cv; break;
                default: rr = cv; gg = 0; bb = xv; break;
            }
            return Color.FromArgb(
                (int)System.Math.Clamp((rr + m) * 255f, 0, 255),
                (int)System.Math.Clamp((gg + m) * 255f, 0, 255),
                (int)System.Math.Clamp((bb + m) * 255f, 0, 255));
        }
    }
}
