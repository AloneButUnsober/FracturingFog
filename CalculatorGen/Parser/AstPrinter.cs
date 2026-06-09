// AstPrinter.cs
//
// Pretty-prints an AstNode back to source form. Used for the top-of-file
// banner in generated calculators ("derivative: 2*z") and for diagnostics.
// Adds parens only when precedence requires them.

using System.Globalization;
using System.Text;

namespace FracturingFog.CalculatorGen.Parser;

public static class AstPrinter
{
    public static string Print(AstNode node)
    {
        var sb = new StringBuilder();
        WriteExpr(sb, node, parentPrec: 0);
        return sb.ToString();
    }

    // Precedence: 0 = top (Add/Sub), 1 = Mul, 2 = Pow/Neg/atom
    private static void WriteExpr(StringBuilder sb, AstNode node, int parentPrec)
    {
        switch (node)
        {
            case ZRef:        sb.Append('z'); break;
            case CRef:        sb.Append('c'); break;
            case DRef:        sb.Append('D'); break;     // shows up in derivative trees only
            case DeltaRef:    sb.Append('δ'); break;     // shows up in perturbation trees only
            case EpsRef:      sb.Append('ε'); break;     // shows up in perturbation trees only
            case PrevRef:     sb.Append("prev"); break;
            case IterRef:     sb.Append('n'); break;
            case RealConst k: sb.Append(k.Value.ToString("R", CultureInfo.InvariantCulture)); break;
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
                Wrap(sb, parentPrec, 1, () => { WriteExpr(sb, m.Left, 1); sb.Append('*'); WriteExpr(sb, m.Right, 1); });
                break;
            case Pow p:
                Wrap(sb, parentPrec, 2, () => { WriteExpr(sb, p.Base, 2); sb.Append('^').Append(p.Exponent); });
                break;
            case Div d:
                Wrap(sb, parentPrec, 1, () => { WriteExpr(sb, d.Left, 1); sb.Append('/'); WriteExpr(sb, d.Right, 1); });
                break;
            case Conj cj:
                sb.Append("conj("); WriteExpr(sb, cj.Operand, 0); sb.Append(')');
                break;
            case Folded f:
                sb.Append("fold("); WriteExpr(sb, f.Operand, 0); sb.Append(')');
                break;
            case Sin s2:
                sb.Append("sin("); WriteExpr(sb, s2.Operand, 0); sb.Append(')');
                break;
            case Cos c2:
                sb.Append("cos("); WriteExpr(sb, c2.Operand, 0); sb.Append(')');
                break;
            case Exp ex:
                sb.Append("exp("); WriteExpr(sb, ex.Operand, 0); sb.Append(')');
                break;
            case Log lg:
                sb.Append("log("); WriteExpr(sb, lg.Operand, 0); sb.Append(')');
                break;
            case Arg ar:
                sb.Append("arg("); WriteExpr(sb, ar.Operand, 0); sb.Append(')');
                break;
            case Atan2 at:
                sb.Append("atan2(");
                WriteExpr(sb, at.Y, 0);
                sb.Append(", ");
                WriteExpr(sb, at.X, 0);
                sb.Append(')');
                break;
            case Min mn:
                sb.Append("min(");
                WriteExpr(sb, mn.Left, 0);
                sb.Append(", ");
                WriteExpr(sb, mn.Right, 0);
                sb.Append(')');
                break;
            case Max mx:
                sb.Append("max(");
                WriteExpr(sb, mx.Left, 0);
                sb.Append(", ");
                WriteExpr(sb, mx.Right, 0);
                sb.Append(')');
                break;
            case Mod md:
                sb.Append("mod(");
                WriteExpr(sb, md.Left, 0);
                sb.Append(", ");
                WriteExpr(sb, md.Right, 0);
                sb.Append(')');
                break;
            case If i:
                Wrap(sb, parentPrec, 0, () =>
                {
                    sb.Append("if ");
                    WriteCond(sb, i.Cond);
                    sb.Append(" then ");
                    WriteExpr(sb, i.Then, 0);
                    sb.Append(" else ");
                    WriteExpr(sb, i.Else, 0);
                });
                break;
            default:
                throw new InvalidOperationException($"AstPrinter: unhandled {node.GetType().Name}");
        }
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
                    CmpOp.Ge => " >= ",
                    CmpOp.Le => " <= ",
                    CmpOp.Eq => " == ",
                    CmpOp.Ne => " != ",
                    _ => " ?? ",
                });
                WriteCondTerm(sb, cmp.Right);
                break;
            default:
                throw new InvalidOperationException($"AstPrinter: unhandled CondNode {c.GetType().Name}");
        }
    }

    private static void WriteCondTerm(StringBuilder sb, CondTerm t)
    {
        switch (t)
        {
            case CondRe r:    sb.Append("re("); WriteExpr(sb, r.Of, 0); sb.Append(')'); break;
            case CondIm im:   sb.Append("im("); WriteExpr(sb, im.Of, 0); sb.Append(')'); break;
            case CondAbs2 a:  sb.Append("abs("); WriteExpr(sb, a.Of, 0); sb.Append(')'); break;
            case CondConst k: sb.Append(k.Value.ToString("R", CultureInfo.InvariantCulture)); break;
            default:
                throw new InvalidOperationException($"AstPrinter: unhandled CondTerm {t.GetType().Name}");
        }
    }

    private static void Wrap(StringBuilder sb, int parentPrec, int myPrec, Action emit)
    {
        bool needParens = parentPrec > myPrec;
        if (needParens) sb.Append('(');
        emit();
        if (needParens) sb.Append(')');
    }
}
