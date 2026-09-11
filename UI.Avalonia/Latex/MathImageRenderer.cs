// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MathImageRenderer.cs
//
// #755 — visually typeset the DSL engine's INTERPRETED equation. Walks the same
// parsed AstNode tree that AstPrinter / AstLatexPrinter (#754) render to text,
// but lays it out as real math (superscripts, stacked fractions, grown
// delimiters, |·| bars, overlines, piecewise cases) and rasterises to an
// Avalonia bitmap via SkiaSharp 3.
//
// Off-the-shelf LaTeX controls don't bind to this stack (CSharpMath.Avalonia →
// Av11; CSharpMath.SkiaSharp → Skia 2.88), so this is a self-contained box-model
// typesetter over the small, known node set (CalculatorGen/Parser/AstNodes.cs).
//
// Coordinate convention inside the layout: a Box measures W (advance width),
// A (ascent — distance ABOVE the baseline, positive up) and D (descent — below).
// Draw(canvas, x, baseline) paints with the pen at the left edge x and the text
// baseline at y. Everything is laid out in RASTER pixels at Ss× the logical size;
// the bitmap is tagged 96·Ss dpi so Avalonia displays it crisply at logical size.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FracturingFog.CalculatorGen.Parser;
using SkiaSharp;

namespace FracturingFog.UI.Avalonia.Latex;

/// <summary>Renders a CalcGen equation to a typeset math bitmap. All-static;
/// <see cref="TryRender"/> never throws — it returns null on any parse/layout
/// failure so the caller can simply hide the image.</summary>
public static class MathImageRenderer
{
    private const int Ss = 2;              // supersample factor (crisp on hi-DPI)
    private const float BaseSizeLogical = 22f;
    private const float BaseSize = BaseSizeLogical * Ss;
    private const int MaxDim = 4096;       // guard against pathological widths

    /// <summary>Parse <paramref name="equation"/> and return a typeset bitmap,
    /// or null on failure. <paramref name="fg"/> is packed 0xAARRGGBB.</summary>
    public static Bitmap? TryRender(string equation, uint fg)
    {
        SKBitmap? bmp = Rasterize(equation, fg);
        if (bmp == null) return null;
        using (bmp)
            return ToAvaloniaBitmap(bmp);
    }

    /// <summary>Pure-Skia path (no Avalonia platform): PNG bytes for the typeset
    /// equation, or null on failure. Used by tests and any headless caller.</summary>
    internal static byte[]? RenderPng(string equation, uint fg)
    {
        using SKBitmap? bmp = Rasterize(equation, fg);
        if (bmp == null) return null;
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    // Parse → layout → CPU raster to a premultiplied BGRA SKBitmap. Never throws;
    // returns null on parse/layout failure or an out-of-range canvas size.
    private static SKBitmap? Rasterize(string equation, uint fg)
    {
        if (string.IsNullOrWhiteSpace(equation)) return null;
        try
        {
            AstNode root = EquationParser.Parse(equation);
            using var ctx = new Ctx(new SKColor(fg));
            Box box = ctx.Build(root, BaseSize);

            float pad = BaseSize * 0.35f;
            int w = (int)MathF.Ceiling(box.W + 2 * pad);
            int h = (int)MathF.Ceiling(box.A + box.D + 2 * pad);
            if (w <= 0 || h <= 0 || w > MaxDim || h > MaxDim) return null;

            var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.Transparent);
                box.Draw(canvas, pad, pad + box.A);   // baseline = top pad + ascent
                canvas.Flush();
            }
            return bmp;
        }
        catch
        {
            return null;   // best-effort preview — never surface an error here
        }
    }

    // Copy the Skia bitmap into a WriteableBitmap. dpi is left at the default 96
    // so Bitmap.Size == PixelSize: a >96 dpi WriteableBitmap makes the Image lay
    // out at the DIP size (½ the pixels) while still painting the full raster,
    // which shows only the top-left corner (the "cut short" bug). The Ss×
    // supersample is instead resolved by the view's Stretch="Uniform" downscale,
    // which keeps the glyphs crisp.
    private static Bitmap ToAvaloniaBitmap(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        var wb = new WriteableBitmap(
            new PixelSize(w, h), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            IntPtr srcPtr = src.GetPixels();
            IntPtr dstPtr = fb.Address;
            int srcStride = src.RowBytes;
            int dstStride = fb.RowBytes;
            int rowBytes = Math.Min(srcStride, dstStride);
            byte[] row = new byte[rowBytes];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(IntPtr.Add(srcPtr, y * srcStride), row, 0, rowBytes);
                Marshal.Copy(row, 0, IntPtr.Add(dstPtr, y * dstStride), rowBytes);
            }
        }
        return wb;
    }

    // ── layout context (font cache + paint) ─────────────────────────────────
    private sealed class Ctx : IDisposable
    {
        private readonly SKTypeface _reg;
        private readonly SKTypeface _ital;
        private readonly SKPaint _paint;
        private readonly SKPaint _stroke;
        private readonly Dictionary<(int, bool), SKFont> _fonts = new();

        public Ctx(SKColor fg)
        {
            _reg = SKTypeface.FromFamilyName("Cambria Math", SKFontStyle.Normal)
                   ?? SKTypeface.FromFamilyName("Times New Roman", SKFontStyle.Normal)
                   ?? SKTypeface.Default;
            _ital = SKTypeface.FromFamilyName("Cambria Math", SKFontStyle.Italic)
                    ?? SKTypeface.FromFamilyName("Times New Roman", SKFontStyle.Italic)
                    ?? _reg;
            _paint = new SKPaint { Color = fg, IsAntialias = true };
            _stroke = new SKPaint { Color = fg, IsAntialias = true, IsStroke = true };
        }

        public SKPaint Paint => _paint;

        public SKFont Font(float size, bool italic)
        {
            var key = ((int)MathF.Round(size), italic);
            if (!_fonts.TryGetValue(key, out var f))
            {
                f = new SKFont(italic ? _ital : _reg, size) { Edging = SKFontEdging.SubpixelAntialias };
                _fonts[key] = f;
            }
            return f;
        }

        public SKPaint Stroke(float thickness)
        {
            _stroke.StrokeWidth = thickness;
            return _stroke;
        }

        public void Dispose()
        {
            foreach (var f in _fonts.Values) f.Dispose();
            _paint.Dispose();
            _stroke.Dispose();
            _reg.Dispose();
            if (!ReferenceEquals(_ital, _reg)) _ital.Dispose();
        }

        // ── AstNode → Box ────────────────────────────────────────────────────
        public Box Build(AstNode node, float size) => Build(node, size, parentPrec: 0);

        private Box Build(AstNode node, float size, int parentPrec)
        {
            switch (node)
            {
                case ZRef: return Var("z", size);
                case CRef: return Var("c", size);
                case DRef: return Var("D", size);
                case DeltaRef: return Var("δ", size);       // δ
                case EpsRef: return Var("ε", size);         // ε
                case IterRef: return Var("n", size);
                case ImagUnit: return Var("i", size);
                case PrevRef: return Subscript(Var("z", size), Text("n-1", size * 0.7f, italic: false));
                case RealConst k: return Text(FormatReal(k.Value), size, italic: false);

                case Neg ng: return Paren0(parentPrec, 2, size,
                    () => Row(Text("−", size, false), Build(ng.Operand, size, 2)));   // −
                case Add a: return Paren0(parentPrec, 0, size,
                    () => Row(Build(a.Left, size, 0), Op("+", size), Build(a.Right, size, 0)));
                case Sub s: return Paren0(parentPrec, 0, size,
                    () => Row(Build(s.Left, size, 0), Op("−", size), Build(s.Right, size, 1)));
                case Mul m: return Paren0(parentPrec, 1, size,
                    () => Row(Build(m.Left, size, 1), Op("·", size), Build(m.Right, size, 1)));  // ·

                case Pow p: return Sup(BasePow(p.Base, size), Text(p.Exponent.ToString(CultureInfo.InvariantCulture), size * 0.72f, false));
                case PowC pc: return Sup(BasePow(pc.Base, size), Build(pc.Exp, size * 0.72f, 0));
                case Exp ex: return Sup(Var("e", size), Build(ex.Operand, size * 0.72f, 0));

                case Div d: return Frac(Build(d.Left, size, 0), Build(d.Right, size, 0), size);

                case Conj cj: return Overline(Build(cj.Operand, size, 0), size);
                case AbsOp ab: return Bars(Build(ab.Operand, size, 0), size);
                case Floor fl: return Bracket(Build(fl.Operand, size, 0), size, floor: true);
                case Ceil ce: return Bracket(Build(ce.Operand, size, 0), size, floor: false);

                case Sin s2: return Func("sin", s2.Operand, size);
                case Cos c2: return Func("cos", c2.Operand, size);
                case Log lg: return Func("ln", lg.Operand, size);
                case Sqrt sq: return Func("sqrt", sq.Operand, size);
                case Arg ar: return Func("arg", ar.Operand, size);
                case Asin a1: return Func("asin", a1.Operand, size);
                case Acos a1: return Func("acos", a1.Operand, size);
                case Atan a1: return Func("atan", a1.Operand, size);
                case Asinh a1: return Func("asinh", a1.Operand, size);
                case Acosh a1: return Func("acosh", a1.Operand, size);
                case Atanh a1: return Func("atanh", a1.Operand, size);
                case Round r1: return Func("round", r1.Operand, size);
                case Trunc t1: return Func("trunc", t1.Operand, size);
                case Fract f1: return Func("fract", f1.Operand, size);
                case Sign g1: return Func("sign", g1.Operand, size);
                case Folded f2: return Func("fold", f2.Operand, size);
                case ReOp r2: return Func("Re", r2.Operand, size);
                case ImOp i2: return Func("Im", i2.Operand, size);

                case Atan2 a2: return Func2("atan2", a2.Y, a2.X, size);
                case Min mn: return Func2("min", mn.Left, mn.Right, size);
                case Max mx: return Func2("max", mx.Left, mx.Right, size);
                case Mod md: return Func2("mod", md.Left, md.Right, size);
                case Clamp cl: return Func3("clamp", cl.X, cl.Lo, cl.Hi, size);

                case If iff: return Cases(iff, size);

                default:
                    // Never throw — degrade any unhandled node to its text form.
                    return Text(AstPrinter.Print(node), size * 0.9f, italic: false);
            }
        }

        // Base of a power: wrap in parens when it isn't a bare atom, so
        // (z+c)^2 typesets with growing parens.
        private Box BasePow(AstNode b, float size)
        {
            bool atom = b is ZRef or CRef or IterRef or ImagUnit or RealConst or DRef or DeltaRef or EpsRef or PrevRef;
            // Build at prec 0 (no auto-paren) then add exactly one delimiter for
            // non-atoms, so (z+c)^2 gets a single set of grown parens, not two.
            var inner = Build(b, size, 0);
            return atom ? inner : Delim(inner, size);
        }

        // ── leaf / helper box builders ───────────────────────────────────────
        private Box Var(string s, float size) => Text(s, size, italic: true);

        private Box Text(string s, float size, bool italic)
        {
            var f = Font(size, italic);
            var m = f.Metrics;
            return new TextBox(s, f, _paint, f.MeasureText(s), -m.Ascent, m.Descent);
        }

        // Binary operator with symmetric side spacing.
        private Box Op(string s, float size)
        {
            float gap = size * (s == "·" ? 0.12f : 0.22f);
            return Row(Gap(gap), Text(s, size, italic: false), Gap(gap));
        }

        private static Box Gap(float w) => new GapBox(w);

        private Box Func(string name, AstNode arg, float size)
            => Row(Text(name, size, italic: false), Gap(size * 0.06f), Delim(Build(arg, size, 0), size));

        private Box Func2(string name, AstNode a, AstNode b, float size)
            => Row(Text(name, size, false), Gap(size * 0.06f),
                   Delim(Row(Build(a, size, 0), Comma(size), Build(b, size, 0)), size));

        private Box Func3(string name, AstNode a, AstNode b, AstNode c, float size)
            => Row(Text(name, size, false), Gap(size * 0.06f),
                   Delim(Row(Build(a, size, 0), Comma(size), Build(b, size, 0), Comma(size), Build(c, size, 0)), size));

        private Box Comma(float size) => Row(Text(",", size, false), Gap(size * 0.25f));

        private Box Row(params Box[] parts) => new RowBox(parts);

        private Box Subscript(Box b, Box sub)
        {
            float drop = b.D + sub.A * 0.15f;
            return new RowBox(new[] { b, new ShiftBox(sub, drop) });
        }

        private Box Sup(Box b, Box sup)
        {
            float raise = b.A * 0.55f + sup.D;
            return new RowBox(new[] { b, new ShiftBox(sup, -raise) });
        }

        private Box Frac(Box num, Box den, float size)
            => new FracBox(num, den, size * 0.28f, MathF.Max(1f, size * 0.05f), size * 0.14f, _paint);

        private Box Overline(Box inner, float size)
            => new OverlineBox(inner, size * 0.10f, MathF.Max(1f, size * 0.05f), Stroke(MathF.Max(1f, size * 0.05f)));

        private Box Bars(Box inner, float size)
            => new BarsBox(inner, size * 0.12f, MathF.Max(1f, size * 0.05f), Stroke(MathF.Max(1f, size * 0.05f)));

        private Box Bracket(Box inner, float size, bool floor)
            => new BracketBox(inner, size * 0.14f, size * 0.30f, MathF.Max(1f, size * 0.05f), floor, Stroke(MathF.Max(1f, size * 0.05f)));

        // Parenthesise inner with glyphs grown to its height.
        private Box Delim(Box inner, float size)
        {
            float target = inner.A + inner.D;
            float pSize = Math.Clamp(target * 1.05f, size, size * 5f);
            var f = Font(pSize, italic: false);
            var m = f.Metrics;
            float pA = -m.Ascent, pD = m.Descent;
            float lw = f.MeasureText("("), rw = f.MeasureText(")");
            return new DelimBox(inner, f, _paint, lw, rw, pA, pD);
        }

        private Box Cases(If iff, float size)
        {
            var then = Build(iff.Then, size, 0);
            var els = Build(iff.Else, size, 0);
            Box row1 = Row(then, Gap(size * 0.6f), Text("if ", size, false), Cond(iff.Cond, size));
            Box row2 = Row(els, Gap(size * 0.6f), Text("otherwise", size, false));
            return new CasesBox(new[] { row1, row2 }, size * 0.35f, size, _paint, this);
        }

        private Box Cond(CondNode c, float size) => c switch
        {
            Cmp cmp => Row(CondTerm(cmp.Left, size), Text(CmpSym(cmp.Op), size, false), CondTerm(cmp.Right, size)),
            _ => Text("?", size, false),
        };

        private static string CmpSym(CmpOp op) => op switch
        {
            CmpOp.Gt => " > ", CmpOp.Lt => " < ", CmpOp.Ge => " ≥ ",
            CmpOp.Le => " ≤ ", CmpOp.Eq => " = ", CmpOp.Ne => " ≠ ", _ => " ? ",
        };

        private Box CondTerm(CondTerm t, float size) => t switch
        {
            CondRe r => Func("Re", r.Of, size),
            CondIm im => Func("Im", im.Of, size),
            CondArg ag => Func("arg", ag.Of, size),
            CondAbs2 a => Sup(Bars(Build(a.Of, size, 0), size), Text("2", size * 0.72f, false)),
            CondConst k => Text(FormatReal(k.Value), size, false),
            _ => Text("?", size, false),
        };

        private Box Paren0(int parentPrec, int myPrec, float size, Func<Box> emit)
        {
            var inner = emit();
            return parentPrec > myPrec ? Delim(inner, size) : inner;
        }
    }

    private static string FormatReal(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // ── box model ─────────────────────────────────────────────────────────────
    private abstract class Box
    {
        public float W, A, D;
        public abstract void Draw(SKCanvas c, float x, float baseline);
    }

    private sealed class GapBox : Box
    {
        public GapBox(float w) { W = w; A = 0; D = 0; }
        public override void Draw(SKCanvas c, float x, float baseline) { }
    }

    private sealed class TextBox : Box
    {
        private readonly string _s; private readonly SKFont _f; private readonly SKPaint _p;
        public TextBox(string s, SKFont f, SKPaint p, float w, float a, float d)
        { _s = s; _f = f; _p = p; W = w; A = a; D = d; }
        public override void Draw(SKCanvas c, float x, float baseline) => c.DrawText(_s, x, baseline, _f, _p);
    }

    private sealed class RowBox : Box
    {
        private readonly Box[] _parts;
        public RowBox(Box[] parts)
        {
            _parts = parts;
            foreach (var b in parts) { W += b.W; A = MathF.Max(A, b.A); D = MathF.Max(D, b.D); }
        }
        public override void Draw(SKCanvas c, float x, float baseline)
        {
            foreach (var b in _parts) { b.Draw(c, x, baseline); x += b.W; }
        }
    }

    // Shift a child down by dy (dy<0 raises it, e.g. superscripts).
    private sealed class ShiftBox : Box
    {
        private readonly Box _b; private readonly float _dy;
        public ShiftBox(Box b, float dy) { _b = b; _dy = dy; W = b.W; A = b.A - dy; D = b.D + dy; }
        public override void Draw(SKCanvas c, float x, float baseline) => _b.Draw(c, x, baseline + _dy);
    }

    private sealed class FracBox : Box
    {
        private readonly Box _num, _den; private readonly float _axis, _t, _pad; private readonly SKPaint _p;
        public FracBox(Box num, Box den, float axis, float thickness, float pad, SKPaint p)
        {
            _num = num; _den = den; _axis = axis; _t = thickness; _pad = pad; _p = p;
            float gap = pad;
            W = MathF.Max(num.W, den.W) + 2 * pad;
            A = axis + gap + _t / 2 + num.D + num.A;
            D = -axis + gap + _t / 2 + den.A + den.D;
        }
        public override void Draw(SKCanvas c, float x, float baseline)
        {
            float gap = _pad;
            float barY = baseline - _axis;
            float cx = x + W / 2;
            _num.Draw(c, cx - _num.W / 2, barY - gap - _t / 2 - _num.D);
            _den.Draw(c, cx - _den.W / 2, barY + gap + _t / 2 + _den.A);
            using var fill = new SKPaint { Color = _p.Color, IsAntialias = true };
            c.DrawRect(x + _pad * 0.5f, barY - _t / 2, W - _pad, _t, fill);
        }
    }

    private sealed class OverlineBox : Box
    {
        private readonly Box _b; private readonly float _gap, _t; private readonly SKPaint _stroke;
        public OverlineBox(Box b, float gap, float t, SKPaint stroke)
        { _b = b; _gap = gap; _t = t; _stroke = stroke; W = b.W; A = b.A + gap + t; D = b.D; }
        public override void Draw(SKCanvas c, float x, float baseline)
        {
            _b.Draw(c, x, baseline);
            float y = baseline - _b.A - _gap;
            c.DrawLine(x, y, x + W, y, _stroke);
        }
    }

    private sealed class BarsBox : Box
    {
        private readonly Box _b; private readonly float _pad, _t; private readonly SKPaint _stroke;
        public BarsBox(Box b, float pad, float t, SKPaint stroke)
        { _b = b; _pad = pad; _t = t; _stroke = stroke; W = b.W + 2 * pad; A = b.A; D = b.D; }
        public override void Draw(SKCanvas c, float x, float baseline)
        {
            float top = baseline - A, bot = baseline + D;
            c.DrawLine(x + _t / 2, top, x + _t / 2, bot, _stroke);
            _b.Draw(c, x + _pad, baseline);
            c.DrawLine(x + W - _t / 2, top, x + W - _t / 2, bot, _stroke);
        }
    }

    private sealed class BracketBox : Box
    {
        private readonly Box _b; private readonly float _pad, _foot, _t; private readonly bool _floor; private readonly SKPaint _stroke;
        public BracketBox(Box b, float pad, float foot, float t, bool floor, SKPaint stroke)
        { _b = b; _pad = pad; _foot = foot; _t = t; _floor = floor; _stroke = stroke; W = b.W + 2 * pad; A = b.A; D = b.D; }
        public override void Draw(SKCanvas c, float x, float baseline)
        {
            float top = baseline - A, bot = baseline + D;
            float lx = x + _t / 2, rx = x + W - _t / 2;
            c.DrawLine(lx, top, lx, bot, _stroke);
            c.DrawLine(rx, top, rx, bot, _stroke);
            if (_floor) { c.DrawLine(lx, bot, lx + _foot, bot, _stroke); c.DrawLine(rx, bot, rx - _foot, bot, _stroke); }
            else        { c.DrawLine(lx, top, lx + _foot, top, _stroke); c.DrawLine(rx, top, rx - _foot, top, _stroke); }
            _b.Draw(c, x + _pad, baseline);
        }
    }

    private sealed class DelimBox : Box
    {
        private readonly Box _inner; private readonly SKFont _f; private readonly SKPaint _p;
        private readonly float _lw, _rw, _pA, _pD, _off;
        public DelimBox(Box inner, SKFont f, SKPaint p, float lw, float rw, float pA, float pD)
        {
            _inner = inner; _f = f; _p = p; _lw = lw; _rw = rw; _pA = pA; _pD = pD;
            float innerMid = (inner.D - inner.A) / 2;   // vertical centre, +down
            float parenMid = (pD - pA) / 2;
            _off = innerMid - parenMid;                  // baseline shift for parens
            W = lw + inner.W + rw;
            A = MathF.Max(inner.A, pA - _off);
            D = MathF.Max(inner.D, pD + _off);
        }
        public override void Draw(SKCanvas c, float x, float baseline)
        {
            c.DrawText("(", x, baseline + _off, _f, _p);
            _inner.Draw(c, x + _lw, baseline);
            c.DrawText(")", x + _lw + _inner.W, baseline + _off, _f, _p);
        }
    }

    // Piecewise cases: a grown left brace + two left-aligned rows, vertically
    // centred on the math axis.
    private sealed class CasesBox : Box
    {
        private readonly Box[] _rows; private readonly float _gap; private readonly SKFont _brace; private readonly SKPaint _p; private readonly float _braceW; private readonly float _off;
        public CasesBox(Box[] rows, float gap, float size, SKPaint p, Ctx ctx)
        {
            _rows = rows; _gap = gap; _p = p;
            float bodyW = 0, total = 0;
            foreach (var r in rows) { bodyW = MathF.Max(bodyW, r.W); total += r.A + r.D; }
            total += gap * (rows.Length - 1);
            float braceSize = Math.Clamp(total * 1.05f, size, size * 8f);
            _brace = ctx.Font(braceSize, italic: false);
            var m = _brace.Metrics;
            _braceW = _brace.MeasureText("{") + size * 0.1f;
            float axis = size * 0.28f;
            A = total / 2 + axis;
            D = total / 2 - axis;
            float braceMid = (m.Descent - (-m.Ascent)) / 2;
            _off = -( (D - A) / 2 ) - braceMid;   // centre brace on box centre
            W = _braceW + bodyW;
        }
        public override void Draw(SKCanvas c, float x, float baseline)
        {
            c.DrawText("{", x, baseline + _off, _brace, _p);
            float top = baseline - A;
            float bx = x + _braceW;
            foreach (var r in _rows)
            {
                r.Draw(c, bx, top + r.A);
                top += r.A + r.D + _gap;
            }
        }
    }
}
