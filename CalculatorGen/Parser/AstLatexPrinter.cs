// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// AstLatexPrinter.cs
//
// Renders an AstNode tree to a STANDARD, portable LaTeX math string — the
// same tree AstPrinter renders to source form and the emitters compile, so
// the LaTeX shows the math EXACTLY as the DSL engine interpreted it (#753 /
// #754). No custom macros: only \frac, \sqrt, ^{}, \overline, \cdot,
// \left|...\right|, \operatorname{...}, \lfloor/\lceil, and a cases block for
// `if`. That keeps the output pasteable into Overleaf/LaTeX, KaTeX, MathJax,
// and the modern Word equation editor with no preamble.
//
// This is the SOURCE-string half of the feature. Visual typesetting (#755) and
// MathML export (#756) are sibling printers over this same tree.
//
// Precedence mirrors AstPrinter: 0 = Add/Sub, 1 = Mul, 2 = Neg, 3 = Pow-base /
// atom. \frac and the function/bracket forms are self-grouping, so they sit at
// atom precedence and never take outer parens.

using System.Globalization;
using System.Text;

namespace FracturingFog.CalculatorGen.Parser;

public static class AstLatexPrinter
{
    public static string Print(AstNode node)
    {
        var sb = new StringBuilder();
        WriteExpr(sb, node, parentPrec: 0);
        return sb.ToString();
    }

    private static void WriteExpr(StringBuilder sb, AstNode node, int parentPrec)
    {
        switch (node)
        {
            case ZRef:        sb.Append('z'); break;
            case CRef:        sb.Append('c'); break;
            case DRef:        sb.Append('D'); break;            // derivative trees only
            case DeltaRef:    sb.Append("\\delta"); break;       // perturbation trees only
            case EpsRef:      sb.Append("\\varepsilon"); break;  // perturbation trees only
            case PrevRef:     sb.Append("z_{n-1}"); break;
            case IterRef:     sb.Append('n'); break;
            case RealConst k: sb.Append(FormatReal(k.Value)); break;
            case ImagUnit:    sb.Append('i'); break;

            case Neg n:
                Wrap(sb, parentPrec, 2, () => { sb.Append('-'); WriteExpr(sb, n.Operand, 2); });
                break;
            case Add a:
                Wrap(sb, parentPrec, 0, () => { WriteExpr(sb, a.Left, 0); sb.Append(" + "); WriteExpr(sb, a.Right, 0); });
                break;
            case Sub s:
                Wrap(sb, parentPrec, 0, () => { WriteExpr(sb, s.Left, 0); sb.Append(" - "); WriteExpr(sb, s.Right, 1); });
                break;
            case Mul m:
                Wrap(sb, parentPrec, 1, () => { WriteExpr(sb, m.Left, 1); sb.Append(" \\cdot "); WriteExpr(sb, m.Right, 1); });
                break;
            case Pow p:
                // base^{exp}; base at Pow-base precedence so sums/products get parens.
                sb.Append("{"); WriteExpr(sb, p.Base, 3); sb.Append("}^{")
                  .Append(p.Exponent.ToString(CultureInfo.InvariantCulture)).Append('}');
                break;
            case PowC pc:
                sb.Append("{"); WriteExpr(sb, pc.Base, 3); sb.Append("}^{"); WriteExpr(sb, pc.Exp, 0); sb.Append('}');
                break;
            case Div d:
                // \frac isolates both operands — no outer parens needed.
                sb.Append("\\frac{"); WriteExpr(sb, d.Left, 0); sb.Append("}{"); WriteExpr(sb, d.Right, 0); sb.Append('}');
                break;

            case Conj cj:   sb.Append("\\overline{"); WriteExpr(sb, cj.Operand, 0); sb.Append('}'); break;
            case Sqrt sq:   sb.Append("\\sqrt{");     WriteExpr(sb, sq.Operand, 0); sb.Append('}'); break;
            case AbsOp ab:  sb.Append("\\left|");     WriteExpr(sb, ab.Operand, 0); sb.Append("\\right|"); break;
            case Floor fl:  sb.Append("\\lfloor ");   WriteExpr(sb, fl.Operand, 0); sb.Append(" \\rfloor"); break;
            case Ceil ce:   sb.Append("\\lceil ");    WriteExpr(sb, ce.Operand, 0); sb.Append(" \\rceil"); break;

            case Sin s2:    Func(sb, "\\sin",    s2.Operand); break;
            case Cos c2:    Func(sb, "\\cos",    c2.Operand); break;
            case Log lg:    Func(sb, "\\ln",     lg.Operand); break;
            case Arg ar:    Func(sb, "\\arg",    ar.Operand); break;
            case Asin as1:  Func(sb, "\\arcsin", as1.Operand); break;
            case Acos ac1:  Func(sb, "\\arccos", ac1.Operand); break;
            case Atan at1:  Func(sb, "\\arctan", at1.Operand); break;
            case Exp ex:    sb.Append("e^{"); WriteExpr(sb, ex.Operand, 0); sb.Append('}'); break;

            case Asinh ah1: OpFunc(sb, "arcsinh", ah1.Operand); break;
            case Acosh ch1: OpFunc(sb, "arccosh", ch1.Operand); break;
            case Atanh th1: OpFunc(sb, "arctanh", th1.Operand); break;
            case Round rd:  OpFunc(sb, "round",   rd.Operand); break;
            case Trunc tr:  OpFunc(sb, "trunc",   tr.Operand); break;
            case Fract fr:  OpFunc(sb, "fract",   fr.Operand); break;
            case Sign sg:   OpFunc(sb, "sign",    sg.Operand); break;
            case Folded f:  OpFunc(sb, "fold",    f.Operand); break;
            case ReOp r3:   OpFunc(sb, "Re",      r3.Operand); break;
            case ImOp im3:  OpFunc(sb, "Im",      im3.Operand); break;

            case Atan2 at:  OpFunc2(sb, "atan2", at.Y, at.X); break;
            case Min mn:    sb.Append("\\min\\left("); WriteExpr(sb, mn.Left, 0); sb.Append(", "); WriteExpr(sb, mn.Right, 0); sb.Append("\\right)"); break;
            case Max mx:    sb.Append("\\max\\left("); WriteExpr(sb, mx.Left, 0); sb.Append(", "); WriteExpr(sb, mx.Right, 0); sb.Append("\\right)"); break;
            case Mod md:    OpFunc2(sb, "mod", md.Left, md.Right); break;

            case Clamp cl:
                sb.Append("\\operatorname{clamp}\\left(");
                WriteExpr(sb, cl.X, 0); sb.Append(", ");
                WriteExpr(sb, cl.Lo, 0); sb.Append(", ");
                WriteExpr(sb, cl.Hi, 0);
                sb.Append("\\right)");
                break;

            case If i:
                // Piecewise form — cases block reads as the true math intent.
                sb.Append("\\begin{cases} ");
                WriteExpr(sb, i.Then, 0);
                sb.Append(" & \\text{if } ");
                WriteCond(sb, i.Cond);
                sb.Append(" \\\\ ");
                WriteExpr(sb, i.Else, 0);
                sb.Append(" & \\text{otherwise} \\end{cases}");
                break;

            default:
                throw new InvalidOperationException($"AstLatexPrinter: unhandled {node.GetType().Name}");
        }
    }

    private static void Func(StringBuilder sb, string cmd, AstNode operand)
    {
        sb.Append(cmd).Append("\\left("); WriteExpr(sb, operand, 0); sb.Append("\\right)");
    }

    private static void OpFunc(StringBuilder sb, string name, AstNode operand)
    {
        sb.Append("\\operatorname{").Append(name).Append("}\\left("); WriteExpr(sb, operand, 0); sb.Append("\\right)");
    }

    private static void OpFunc2(StringBuilder sb, string name, AstNode a, AstNode b)
    {
        sb.Append("\\operatorname{").Append(name).Append("}\\left(");
        WriteExpr(sb, a, 0); sb.Append(", "); WriteExpr(sb, b, 0);
        sb.Append("\\right)");
    }

    private static void WriteCond(StringBuilder sb, CondNode c)
    {
        switch (c)
        {
            case Cmp cmp:
                WriteCondTerm(sb, cmp.Left);
                sb.Append(cmp.Op switch
                {
                    CmpOp.Gt => " > ",
                    CmpOp.Lt => " < ",
                    CmpOp.Ge => " \\ge ",
                    CmpOp.Le => " \\le ",
                    CmpOp.Eq => " = ",
                    CmpOp.Ne => " \\ne ",
                    _ => " \\mathbin{?} ",
                });
                WriteCondTerm(sb, cmp.Right);
                break;
            default:
                throw new InvalidOperationException($"AstLatexPrinter: unhandled CondNode {c.GetType().Name}");
        }
    }

    private static void WriteCondTerm(StringBuilder sb, CondTerm t)
    {
        switch (t)
        {
            case CondRe r:    OpFunc(sb, "Re", r.Of); break;
            case CondIm im:   OpFunc(sb, "Im", im.Of); break;
            case CondAbs2 a:  sb.Append("{\\left|"); WriteExpr(sb, a.Of, 0); sb.Append("\\right|}^{2}"); break;
            case CondArg ag:  sb.Append("\\arg\\left("); WriteExpr(sb, ag.Of, 0); sb.Append("\\right)"); break;
            case CondConst k: sb.Append(FormatReal(k.Value)); break;
            default:
                throw new InvalidOperationException($"AstLatexPrinter: unhandled CondTerm {t.GetType().Name}");
        }
    }

    // Round-trip ("R") like AstPrinter so the printed constant matches the
    // parsed value exactly; invariant culture so the decimal point is a dot.
    private static string FormatReal(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static void Wrap(StringBuilder sb, int parentPrec, int myPrec, Action emit)
    {
        bool needParens = parentPrec > myPrec;
        if (needParens) sb.Append("\\left(");
        emit();
        if (needParens) sb.Append("\\right)");
    }
}
