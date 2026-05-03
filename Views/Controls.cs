using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace FracturingFog.Views
{
    internal static class ControlExtensions
    {
        public static T Also<T>(this T ctrl, Action<T> action) where T : Control
        { action(ctrl); return ctrl; }

        public static T AlsoAdd<T>(this T ctrl, Panel parent) where T : Control
        { parent.Controls.Add(ctrl); return ctrl; }

        public static T AlsoAdd<T>(this T ctrl, Panel parent, string tooltip) where T : Control
        { new ToolTip().SetToolTip(ctrl, tooltip); parent.Controls.Add(ctrl); return ctrl; }
    }
    
    public class ColorComboBox : ComboBox
    {
        public ColorComboBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            ItemHeight = 20;
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            e.DrawBackground();

            string text = Items[e.Index]?.ToString() ?? "";
            IColorMap map = Models.ColorPalette.GetPaletteByName(text);
            map.MaxIterations = 500;
            int argb = map.SwatchSample;
            var swatch = Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);

            var swatchRect = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 3, 18, e.Bounds.Height - 6);
            using var sb = new SolidBrush(swatch);
            e.Graphics.FillRectangle(sb, swatchRect);
            e.Graphics.DrawRectangle(Pens.DimGray, swatchRect);

            var textBrush = (e.State & DrawItemState.Selected) != 0 ? Brushes.White : Brushes.LightGray;
            e.Graphics.DrawString(text, Font, textBrush, swatchRect.Right + 4, e.Bounds.Y + 2);
            e.DrawFocusRectangle();
        }
        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            base.OnPaintBackground(pevent);
            using var b = new SolidBrush(BackColor);
            pevent.Graphics.FillRectangle(b, ClientRectangle);
            pevent.Graphics.DrawRectangle(Pens.DarkGray, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }
}
