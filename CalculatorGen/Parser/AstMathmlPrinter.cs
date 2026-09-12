// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// AstMathmlPrinter.cs
//
// Renders an AstNode tree to a presentation-MathML string — the same tree
// AstPrinter / AstLatexPrinter (#754) render, so the MathML shows the math
// exactly as the DSL engine interpreted it (#756). Word and LibreOffice import
// math natively as MathML (not LaTeX), so this <math>…</math> string pastes /
// imports into those apps as an editable equation object.
//
// Emits standard presentation elements only (mrow / mi / mn / mo / msup / mfrac
// / msqrt / mover / mtable), namespaced, so it is portable across MathML
// consumers. Precedence mirrors AstLatexPrinter: 0 = Add/Sub, 1 = Mul,
// 2 = Neg, 3 = Pow-base / atom; parentheses are added with <mo>(</mo>…<mo>)</mo>
// when a lower-precedence child sits inside a higher-precedence parent.

using System.Globalization;
using System.Text;

namespace FracturingFog.CalculatorGen.Parser;

public static class AstMathmlPrinter
{
    private const string Ns = "http://www.w3.org/1998/Math/MathML";

    /// <summary>Full document: &lt;math&gt;…&lt;/math&gt; with the MathML
    /// namespace, ready to paste into Word / LibreOffice.</summary>
    public static string Print(AstNode node)
    {
        var sb = new StringBuilder();
        sb.Append("<math xmlns=\"").Append(Ns).Append("\" display=\"inline\"><mrow>");
        WriteExpr(sb, node, parentPrec: 0);
        sb.Append("</mrow></math>");
        return sb.ToString();
    }

    private static void WriteExpr(StringBuilder sb, AstNode node, int parentPrec)
    {
        switch (node)
        {
            case ZRef:        Mi(sb, "z"); break;
            case CRef:        Mi(sb, "c"); break;
            case DRef:        Mi(sb, "D"); break;            // derivative trees only
            case DeltaRef:    Mi(sb, "δ"); break;        // δ, perturbation trees only
            case EpsRef:      Mi(sb, "ε"); break;        // ε, perturbation trees only
            case IterRef:     Mi(sb, "n"); break;
            case ImagUnit:    Mi(sb, "i"); break;
            case PrevRef:
                sb.Append("<msub>"); Mi(sb, "z");
                sb.Append("<mrow>"); Mi(sb, "n"); Mo(sb, "−"); Mn(sb, "1"); sb.Append("</mrow></msub>");
                break;
            case RealConst k: Mn(sb, FormatReal(k.Value)); break;

            case Neg n:
                Wrap(sb, parentPrec, 2, () => { Mo(sb, "−"); WriteExpr(sb, n.Operand, 2); });
                break;
            case Add a:
                Wrap(sb, parentPrec, 0, () => { WriteExpr(sb, a.Left, 0); Mo(sb, "+"); WriteExpr(sb, a.Right, 0); });
                break;
            case Sub s:
                Wrap(sb, parentPrec, 0, () => { WriteExpr(sb, s.Left, 0); Mo(sb, "−"); WriteExpr(sb, s.Right, 1); });
                break;
            case Mul m:
                Wrap(sb, parentPrec, 1, () => { WriteExpr(sb, m.Left, 1); Mo(sb, "⋅"); WriteExpr(sb, m.Right, 1); });
                break;

            case Pow p:
                sb.Append("<msup>"); BasePow(sb, p.Base); Mn(sb, p.Exponent.ToString(CultureInfo.InvariantCulture)); sb.Append("</msup>");
                break;
            case PowC pc:
                sb.Append("<msup>"); BasePow(sb, pc.Base); sb.Append("<mrow>"); WriteExpr(sb, pc.Exp, 0); sb.Append("</mrow></msup>");
                break;
            case Exp ex:
                sb.Append("<msup>"); Mi(sb, "e"); sb.Append("<mrow>"); WriteExpr(sb, ex.Operand, 0); sb.Append("</mrow></msup>");
                break;

            case Div d:
                sb.Append("<mfrac><mrow>"); WriteExpr(sb, d.Left, 0); sb.Append("</mrow><mrow>"); WriteExpr(sb, d.Right, 0); sb.Append("</mrow></mfrac>");
                break;

            case Conj cj:
                sb.Append("<mover accent=\"true\"><mrow>"); WriteExpr(sb, cj.Operand, 0); sb.Append("</mrow><mo>¯</mo></mover>");
                break;
            case Sqrt sq:
                sb.Append("<msqrt><mrow>"); WriteExpr(sb, sq.Operand, 0); sb.Append("</mrow></msqrt>");
                break;
            case AbsOp ab:  Bracketed(sb, "|", "|", ab.Operand); break;
            case Floor fl:  Bracketed(sb, "⌊", "⌋", fl.Operand); break;
            case Ceil ce:   Bracketed(sb, "⌈", "⌉", ce.Operand); break;

            case Sin s2:    Func(sb, "sin", s2.Operand); break;
            case Cos c2:    Func(sb, "cos", c2.Operand); break;
            case Log lg:    Func(sb, "ln", lg.Operand); break;
            case Arg ar:    Func(sb, "arg", ar.Operand); break;
            case Asin a1:   Func(sb, "arcsin", a1.Operand); break;
            case Acos a1:   Func(sb, "arccos", a1.Operand); break;
            case Atan a1:   Func(sb, "arctan", a1.Operand); break;
            case Asinh a1:  Func(sb, "arcsinh", a1.Operand); break;
            case Acosh a1:  Func(sb, "arccosh", a1.Operand); break;
            case Atanh a1:  Func(sb, "arctanh", a1.Operand); break;
            case Round r1:  Func(sb, "round", r1.Operand); break;
            case Trunc t1:  Func(sb, "trunc", t1.Operand); break;
            case Fract f1:  Func(sb, "fract", f1.Operand); break;
            case Sign g1:   Func(sb, "sign", g1.Operand); break;
            case Folded f2: Func(sb, "fold", f2.Operand); break;
            case ReOp r2:   Func(sb, "Re", r2.Operand); break;
            case ImOp i2:   Func(sb, "Im", i2.Operand); break;

            case Atan2 a2:  Func2(sb, "atan2", a2.Y, a2.X); break;
            case Min mn:    Func2(sb, "min", mn.Left, mn.Right); break;
            case Max mx:    Func2(sb, "max", mx.Left, mx.Right); break;
            case Mod md:    Func2(sb, "mod", md.Left, md.Right); break;
            case Clamp cl:  Func3(sb, "clamp", cl.X, cl.Lo, cl.Hi); break;

            case If iff:    WriteCases(sb, iff); break;

            default:
                // Never throw — degrade unknown nodes to a plain identifier of
                // their source text so the MathML stays well-formed.
                Mi(sb, AstPrinter.Print(node));
                break;
        }
    }

    // Base of a power: wrap non-atoms in parentheses so (z+c)^2 reads correctly.
    private static void BasePow(StringBuilder sb, AstNode b)
    {
        bool atom = b is ZRef or CRef or IterRef or ImagUnit or RealConst or DRef or DeltaRef or EpsRef or PrevRef;
        if (atom) { sb.Append("<mrow>"); WriteExpr(sb, b, 3); sb.Append("</mrow>"); }
        else { sb.Append("<mrow><mo>(</mo>"); WriteExpr(sb, b, 0); sb.Append("<mo>)</mo></mrow>"); }
    }

    private static void Func(StringBuilder sb, string name, AstNode arg)
    {
        sb.Append("<mrow>"); Mi(sb, name); sb.Append("<mo>⁡</mo><mo>(</mo>");   // ⁡ = function application
        WriteExpr(sb, arg, 0);
        sb.Append("<mo>)</mo></mrow>");
    }

    private static void Func2(StringBuilder sb, string name, AstNode a, AstNode b)
    {
        sb.Append("<mrow>"); Mi(sb, name); sb.Append("<mo>(</mo>");
        WriteExpr(sb, a, 0); Mo(sb, ","); WriteExpr(sb, b, 0);
        sb.Append("<mo>)</mo></mrow>");
    }

    private static void Func3(StringBuilder sb, string name, AstNode a, AstNode b, AstNode c)
    {
        sb.Append("<mrow>"); Mi(sb, name); sb.Append("<mo>(</mo>");
        WriteExpr(sb, a, 0); Mo(sb, ","); WriteExpr(sb, b, 0); Mo(sb, ","); WriteExpr(sb, c, 0);
        sb.Append("<mo>)</mo></mrow>");
    }

    private static void Bracketed(StringBuilder sb, string open, string close, AstNode inner)
    {
        sb.Append("<mrow><mo>").Append(Esc(open)).Append("</mo>");
        WriteExpr(sb, inner, 0);
        sb.Append("<mo>").Append(Esc(close)).Append("</mo></mrow>");
    }

    // Piecewise: { [then  if cond] / [else  otherwise] } as a braced 2-row table.
    private static void WriteCases(StringBuilder sb, If iff)
    {
        sb.Append("<mrow><mo>{</mo><mtable columnalign=\"left left\">");
        sb.Append("<mtr><mtd>"); WriteExpr(sb, iff.Then, 0); sb.Append("</mtd><mtd><mtext>if </mtext>");
        WriteCond(sb, iff.Cond); sb.Append("</mtd></mtr>");
        sb.Append("<mtr><mtd>"); WriteExpr(sb, iff.Else, 0); sb.Append("</mtd><mtd><mtext>otherwise</mtext></mtd></mtr>");
        sb.Append("</mtable></mrow>");
    }

    private static void WriteCond(StringBuilder sb, CondNode c)
    {
        switch (c)
        {
            case Cmp cmp:
                sb.Append("<mrow>");
                WriteCondTerm(sb, cmp.Left);
                Mo(sb, cmp.Op switch
                {
                    CmpOp.Gt => ">", CmpOp.Lt => "<", CmpOp.Ge => "≥",
                    CmpOp.Le => "≤", CmpOp.Eq => "=", CmpOp.Ne => "≠", _ => "?",
                });
                WriteCondTerm(sb, cmp.Right);
                sb.Append("</mrow>");
                break;
            default:
                Mi(sb, "?");
                break;
        }
    }

    private static void WriteCondTerm(StringBuilder sb, CondTerm t)
    {
        switch (t)
        {
            case CondRe r:    Func(sb, "Re", r.Of); break;
            case CondIm im:   Func(sb, "Im", im.Of); break;
            case CondArg ag:  Func(sb, "arg", ag.Of); break;
            case CondAbs2 a:
                sb.Append("<msup><mrow><mo>|</mo>"); WriteExpr(sb, a.Of, 0); sb.Append("<mo>|</mo></mrow>"); Mn(sb, "2"); sb.Append("</msup>");
                break;
            case CondConst k: Mn(sb, FormatReal(k.Value)); break;
            default:          Mi(sb, "?"); break;
        }
    }

    private static void Wrap(StringBuilder sb, int parentPrec, int myPrec, System.Action emit)
    {
        bool needParens = parentPrec > myPrec;
        sb.Append("<mrow>");
        if (needParens) sb.Append("<mo>(</mo>");
        emit();
        if (needParens) sb.Append("<mo>)</mo>");
        sb.Append("</mrow>");
    }

    private static void Mi(StringBuilder sb, string s) => sb.Append("<mi>").Append(Esc(s)).Append("</mi>");
    private static void Mn(StringBuilder sb, string s) => sb.Append("<mn>").Append(Esc(s)).Append("</mn>");
    private static void Mo(StringBuilder sb, string s) => sb.Append("<mo>").Append(Esc(s)).Append("</mo>");

    private static string FormatReal(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // XML-escape element text (comparison operators <, >, & must be entities).
    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.IndexOfAny(new[] { '&', '<', '>' }) < 0) return s;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
